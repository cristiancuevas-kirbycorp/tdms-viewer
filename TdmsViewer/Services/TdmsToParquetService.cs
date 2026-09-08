using System.Globalization;
using System.IO;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using TdmsViewer.Models;
using TdmsChannel = NationalInstruments.Tdms.Channel;
using TdmsFile = NationalInstruments.Tdms.File;

namespace TdmsViewer.Services;

public interface ITdmsToParquetService
{
    /// <summary>Exports the TDMS file into a per-group Parquet bundle designed for LLM/agent consumption.</summary>
    /// <returns>The absolute path of the bundle directory that was created.</returns>
    Task<string> ExportAsync(string tdmsPath, string outputRoot, IProgress<string>? progress = null);
}

public sealed class TdmsToParquetService : ITdmsToParquetService
{
    private const CompressionMethod DefaultCompression = CompressionMethod.Zstd;

    public async Task<string> ExportAsync(string tdmsPath, string outputRoot, IProgress<string>? progress = null)
    {
        if (!File.Exists(tdmsPath))
            throw new FileNotFoundException("TDMS file not found.", tdmsPath);
        Directory.CreateDirectory(outputRoot);

        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(tdmsPath));
        var bundle = Path.Combine(outputRoot, baseName + "_parquet_bundle");
        var dataDir = Path.Combine(bundle, "data");
        Directory.CreateDirectory(dataDir);

        progress?.Report($"Opening {Path.GetFileName(tdmsPath)}...");
        using var file = new TdmsFile(tdmsPath).Open();
        var rootProps = ToStringDict(file.Properties);

        var manifest = new List<ManifestRow>();
        var summary = new List<SummaryRow>();

        foreach (var group in file.Groups.Values)
        {
            progress?.Report($"Exporting group '{group.Name}'...");

            var channels = group.Channels.Values.ToList();
            var tsCh = channels.FirstOrDefault(c =>
                string.Equals(c.Name, TdmsChannelInfo.TimeStampChannelName, StringComparison.OrdinalIgnoreCase));

            // Get canonical timestamp axis if the group has one.
            DateTime[]? timestamps = null;
            if (tsCh is not null)
                timestamps = TryReadTimestamps(tsCh);
            var rateHz = timestamps is not null ? EstimateRateHz(timestamps) : (double?)null;

            var dataChannels = channels.Where(c => !ReferenceEquals(c, tsCh)).ToList();
            if (dataChannels.Count == 0) continue;

            var rateSuffix = rateHz.HasValue ? "_" + FormatRate(rateHz.Value) + "hz" : "";
            var groupFileName = SanitizeFileName(group.Name) + rateSuffix + ".parquet";
            var groupFilePath = Path.Combine(dataDir, groupFileName);
            var relFile = "data/" + groupFileName;

            var written = await WriteGroupParquetAsync(groupFilePath, group.Name, dataChannels, timestamps);

            foreach (var col in written.Columns)
            {
                manifest.Add(new ManifestRow
                {
                    Group = group.Name,
                    Channel = col.OriginalChannelName,
                    ColumnName = col.ColumnName,
                    Unit = col.Unit,
                    Description = col.Description,
                    Dtype = col.ParquetDtype,
                    SampleCount = col.SampleCount,
                    SampleRateHz = rateHz,
                    TStartUtc = timestamps is { Length: > 0 } ts ? ts[0] : null,
                    TEndUtc = timestamps is { Length: > 0 } te ? te[^1] : null,
                    RelativeFile = relFile,
                });
                if (col.Stats is { } s)
                    summary.Add(s with { ColumnName = col.ColumnName });
            }
        }

        progress?.Report("Writing manifest.parquet...");
        await WriteManifestParquetAsync(Path.Combine(bundle, "manifest.parquet"), manifest);

        progress?.Report("Writing channel_dictionary.csv...");
        WriteChannelDictionaryCsv(Path.Combine(bundle, "channel_dictionary.csv"), manifest);

        progress?.Report("Writing summary.parquet...");
        await WriteSummaryParquetAsync(Path.Combine(bundle, "summary.parquet"), summary);

        progress?.Report("Writing README.md...");
        WriteReadme(Path.Combine(bundle, "README.md"), tdmsPath, rootProps, manifest);

        progress?.Report($"Export complete: {bundle}");
        return bundle;
    }

    // ------------------------------------------------------------------ per-group writer

    private sealed record WrittenGroup(List<WrittenColumn> Columns);

    private sealed record WrittenColumn(
        string OriginalChannelName,
        string ColumnName,
        string Unit,
        string Description,
        string ParquetDtype,
        long SampleCount,
        SummaryRow? Stats);

    private static async Task<WrittenGroup> WriteGroupParquetAsync(
        string outputPath,
        string groupName,
        List<TdmsChannel> channels,
        DateTime[]? timestamps)
    {
        // First pass: materialize typed arrays for each channel so we know max length + dtypes.
        var loaded = new List<(TdmsChannel Ch, Array Data, ParquetKind Kind)>();
        foreach (var ch in channels)
        {
            var raw = ch.GetData<object>().ToArray();
            var kind = ClassifyChannel(ch, raw);
            var data = ConvertChannel(raw, kind);
            loaded.Add((ch, data, kind));
        }

        var rowCount = loaded.Count == 0 ? 0 : loaded.Max(x => x.Data.Length);
        if (timestamps is not null)
            rowCount = Math.Max(rowCount, timestamps.Length);

        // Build schema.
        var fields = new List<Field>();
        DataField? tsField = null;
        DataField? idxField = null;
        if (timestamps is not null)
        {
            tsField = new DataField<DateTime>("timestamp_utc");
            fields.Add(tsField);
        }
        else
        {
            idxField = new DataField<long>("sample_index");
            fields.Add(idxField);
        }

        var columnDefs = new List<(WrittenColumn Meta, DataField Field, Array Data)>();
        foreach (var (ch, data, kind) in loaded)
        {
            var props = ToStringDict(ch.Properties);
            var unit = props.TryGetValue("unit_string", out var u) ? u : "";
            var desc = props.TryGetValue("description", out var d1)
                ? d1
                : props.TryGetValue("NI_ChannelDescription", out var d2) ? d2 : "";
            var colName = MakeColumnName(groupName, ch.Name, unit);

            var (field, padded, dtypeLabel) = BuildNullableField(colName, kind, data, rowCount);
            var stats = ComputeStats(kind, data);
            columnDefs.Add((
                new WrittenColumn(ch.Name, colName, unit, desc, dtypeLabel, data.Length, stats),
                field, padded));
        }
        fields.AddRange(columnDefs.Select(c => c.Field));

        var schema = new ParquetSchema(fields);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var fs = File.Create(outputPath);
        using var writer = await ParquetWriter.CreateAsync(schema, fs);
        writer.CompressionMethod = DefaultCompression;

        using (var rg = writer.CreateRowGroup())
        {
            if (tsField is not null)
                await rg.WriteColumnAsync(new DataColumn(tsField, PadTimestamps(timestamps!, rowCount)));
            if (idxField is not null)
                await rg.WriteColumnAsync(new DataColumn(idxField, BuildIndex(rowCount)));

            foreach (var (_, field, data) in columnDefs)
                await rg.WriteColumnAsync(new DataColumn(field, data));
        }

        return new WrittenGroup(columnDefs.Select(c => c.Meta).ToList());
    }

    // ------------------------------------------------------------------ type classification

    private enum ParquetKind { Double, Float, Int32, Int64, Bool, String, DateTime, Skip }

    private static ParquetKind ClassifyChannel(TdmsChannel ch, object?[] raw)
    {
        var name = ch.DataType?.Name;
        return name switch
        {
            "Double" => ParquetKind.Float,     // downcast doubles to float32 for size
            "Single" => ParquetKind.Float,
            "Byte" or "SByte" or "Int16" or "UInt16" or "Int32" or "UInt16?" => ParquetKind.Int32,
            "UInt32" or "Int64" or "UInt64" => ParquetKind.Int64,
            "Boolean" => ParquetKind.Bool,
            "String" => ParquetKind.String,
            "DateTime" => ParquetKind.DateTime,
            _ => raw.Length > 0 ? ClassifyFromValue(raw[0]) : ParquetKind.Skip,
        };
    }

    private static ParquetKind ClassifyFromValue(object? sample) => sample switch
    {
        double => ParquetKind.Float,
        float => ParquetKind.Float,
        bool => ParquetKind.Bool,
        DateTime => ParquetKind.DateTime,
        string => ParquetKind.String,
        long or ulong => ParquetKind.Int64,
        int or short or sbyte or byte or ushort or uint => ParquetKind.Int32,
        _ => ParquetKind.Skip,
    };

    private static Array ConvertChannel(object?[] raw, ParquetKind kind)
    {
        switch (kind)
        {
            case ParquetKind.Float:
            {
                var arr = new float?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    arr[i] = raw[i] switch
                    {
                        null => null,
                        float f => f,
                        double d => (float)d,
                        IConvertible c => SafeToFloat(c),
                        _ => null,
                    };
                return arr;
            }
            case ParquetKind.Int32:
            {
                var arr = new int?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    arr[i] = raw[i] switch
                    {
                        null => null,
                        int i32 => i32,
                        IConvertible c => SafeToInt32(c),
                        _ => null,
                    };
                return arr;
            }
            case ParquetKind.Int64:
            {
                var arr = new long?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    arr[i] = raw[i] switch
                    {
                        null => null,
                        long i64 => i64,
                        ulong u64 => unchecked((long)u64),
                        IConvertible c => SafeToInt64(c),
                        _ => null,
                    };
                return arr;
            }
            case ParquetKind.Bool:
            {
                var arr = new bool?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    arr[i] = raw[i] switch
                    {
                        null => null,
                        bool b => b,
                        IConvertible c => SafeToInt32(c) != 0,
                        _ => null,
                    };
                return arr;
            }
            case ParquetKind.DateTime:
            {
                var arr = new DateTime?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    arr[i] = raw[i] switch
                    {
                        null => null,
                        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                        _ => null,
                    };
                return arr;
            }
            case ParquetKind.String:
            default:
            {
                var arr = new string?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    arr[i] = raw[i]?.ToString();
                return arr;
            }
        }
    }

    private static (DataField Field, Array Padded, string DtypeLabel) BuildNullableField(
        string name, ParquetKind kind, Array data, int rowCount)
    {
        return kind switch
        {
            ParquetKind.Float => (new DataField<float?>(name), PadNullable<float>(data, rowCount), "float32"),
            ParquetKind.Int32 => (new DataField<int?>(name), PadNullable<int>(data, rowCount), "int32"),
            ParquetKind.Int64 => (new DataField<long?>(name), PadNullable<long>(data, rowCount), "int64"),
            ParquetKind.Bool => (new DataField<bool?>(name), PadNullable<bool>(data, rowCount), "bool"),
            ParquetKind.DateTime => (new DataField<DateTime?>(name), PadNullable<DateTime>(data, rowCount), "timestamp"),
            _ => (new DataField<string?>(name), PadStrings(data, rowCount), "string"),
        };
    }

    private static T?[] PadNullable<T>(Array data, int rowCount) where T : struct
    {
        var src = (T?[])data;
        if (src.Length == rowCount) return src;
        var dst = new T?[rowCount];
        Array.Copy(src, dst, Math.Min(src.Length, rowCount));
        return dst;
    }

    private static string?[] PadStrings(Array data, int rowCount)
    {
        var src = (string?[])data;
        if (src.Length == rowCount) return src;
        var dst = new string?[rowCount];
        Array.Copy(src, dst, Math.Min(src.Length, rowCount));
        return dst;
    }

    private static DateTime[] PadTimestamps(DateTime[] src, int rowCount)
    {
        if (src.Length == rowCount) return src;
        var dst = new DateTime[rowCount];
        Array.Copy(src, dst, Math.Min(src.Length, rowCount));
        return dst;
    }

    private static long[] BuildIndex(int rowCount)
    {
        var idx = new long[rowCount];
        for (var i = 0; i < rowCount; i++) idx[i] = i;
        return idx;
    }

    // ------------------------------------------------------------------ stats

    private static SummaryRow? ComputeStats(ParquetKind kind, Array data)
    {
        if (kind is not (ParquetKind.Float or ParquetKind.Int32 or ParquetKind.Int64)) return null;

        double sum = 0, sumSq = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        double? first = null, last = null;
        long count = 0, nullCount = 0;

        void Sample(double v)
        {
            if (double.IsNaN(v)) { nullCount++; return; }
            if (count == 0) first = v;
            last = v;
            sum += v;
            sumSq += v * v;
            if (v < min) min = v;
            if (v > max) max = v;
            count++;
        }

        switch (kind)
        {
            case ParquetKind.Float:
                foreach (var v in (float?[])data)
                    if (v.HasValue) Sample(v.Value); else nullCount++;
                break;
            case ParquetKind.Int32:
                foreach (var v in (int?[])data)
                    if (v.HasValue) Sample(v.Value); else nullCount++;
                break;
            case ParquetKind.Int64:
                foreach (var v in (long?[])data)
                    if (v.HasValue) Sample(v.Value); else nullCount++;
                break;
        }

        if (count == 0)
            return new SummaryRow("", null, null, null, null, null, null, nullCount);

        var mean = sum / count;
        var variance = Math.Max(0, sumSq / count - mean * mean);
        var std = Math.Sqrt(variance);
        return new SummaryRow("", min, max, mean, std, first, last, nullCount);
    }

    // ------------------------------------------------------------------ manifest / summary / csv / readme

    private static async Task WriteManifestParquetAsync(string path, List<ManifestRow> rows)
    {
        var fGroup = new DataField<string>("group");
        var fChannel = new DataField<string>("channel");
        var fCol = new DataField<string>("column_name");
        var fUnit = new DataField<string>("unit");
        var fDesc = new DataField<string>("description");
        var fDtype = new DataField<string>("dtype");
        var fCount = new DataField<long>("sample_count");
        var fRate = new DataField<double?>("sample_rate_hz");
        var fStart = new DataField<DateTime?>("t_start_utc");
        var fEnd = new DataField<DateTime?>("t_end_utc");
        var fFile = new DataField<string>("file");

        var schema = new ParquetSchema(fGroup, fChannel, fCol, fUnit, fDesc, fDtype, fCount, fRate, fStart, fEnd, fFile);

        using var fs = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, fs);
        writer.CompressionMethod = DefaultCompression;
        using var rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(fGroup, rows.Select(r => r.Group).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fChannel, rows.Select(r => r.Channel).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fCol, rows.Select(r => r.ColumnName).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fUnit, rows.Select(r => r.Unit).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fDesc, rows.Select(r => r.Description).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fDtype, rows.Select(r => r.Dtype).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fCount, rows.Select(r => r.SampleCount).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fRate, rows.Select(r => r.SampleRateHz).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fStart, rows.Select(r => r.TStartUtc).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fEnd, rows.Select(r => r.TEndUtc).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fFile, rows.Select(r => r.RelativeFile).ToArray()));
    }

    private static async Task WriteSummaryParquetAsync(string path, List<SummaryRow> rows)
    {
        var fCol = new DataField<string>("column_name");
        var fMin = new DataField<double?>("min");
        var fMax = new DataField<double?>("max");
        var fMean = new DataField<double?>("mean");
        var fStd = new DataField<double?>("std");
        var fFirst = new DataField<double?>("first");
        var fLast = new DataField<double?>("last");
        var fNull = new DataField<long>("null_count");

        var schema = new ParquetSchema(fCol, fMin, fMax, fMean, fStd, fFirst, fLast, fNull);

        using var fs = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, fs);
        writer.CompressionMethod = DefaultCompression;
        using var rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(fCol, rows.Select(r => r.ColumnName).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fMin, rows.Select(r => r.Min).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fMax, rows.Select(r => r.Max).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fMean, rows.Select(r => r.Mean).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fStd, rows.Select(r => r.Std).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fFirst, rows.Select(r => r.First).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fLast, rows.Select(r => r.Last).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(fNull, rows.Select(r => r.NullCount).ToArray()));
    }

    private static void WriteChannelDictionaryCsv(string path, List<ManifestRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("column_name,group,channel,unit,description,dtype,sample_count,sample_rate_hz,t_start_utc,t_end_utc,file");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.ColumnName)).Append(',');
            sb.Append(Csv(r.Group)).Append(',');
            sb.Append(Csv(r.Channel)).Append(',');
            sb.Append(Csv(r.Unit)).Append(',');
            sb.Append(Csv(r.Description)).Append(',');
            sb.Append(Csv(r.Dtype)).Append(',');
            sb.Append(r.SampleCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.SampleRateHz?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append(r.TStartUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append(r.TEndUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.AppendLine(Csv(r.RelativeFile));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteReadme(string path, string sourceTdms, Dictionary<string, string> rootProps, List<ManifestRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TDMS Parquet Bundle");
        sb.AppendLine();
        sb.AppendLine($"- **Source file:** `{Path.GetFileName(sourceTdms)}`");
        sb.AppendLine($"- **Exported:** {DateTime.UtcNow:O}");
        sb.AppendLine($"- **Channels:** {rows.Count}");
        sb.AppendLine($"- **Compression:** {DefaultCompression}");
        sb.AppendLine();

        if (rootProps.Count > 0)
        {
            sb.AppendLine("## Root properties");
            sb.AppendLine();
            foreach (var kv in rootProps.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- **{kv.Key}:** {kv.Value}");
            sb.AppendLine();
        }

        sb.AppendLine("## Layout");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("data/<Group>[_<rate>hz].parquet   one Parquet per TDMS group");
        sb.AppendLine("manifest.parquet                  queryable catalog (one row per channel)");
        sb.AppendLine("summary.parquet                   per-channel min/max/mean/std/first/last/null_count");
        sb.AppendLine("channel_dictionary.csv            same catalog as manifest.parquet, human/LLM friendly");
        sb.AppendLine("README.md                         this file");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("Each per-group file has a `timestamp_utc` column (when the group has a `Time Stamp` channel)");
        sb.AppendLine("or a `sample_index` column otherwise. Numeric channels are stored as `float32` or native int");
        sb.AppendLine("widths. String and boolean channels are stored natively.");
        sb.AppendLine();

        sb.AppendLine("## Groups");
        sb.AppendLine();
        sb.AppendLine("| Group | File | Channels | Sample rate (Hz) |");
        sb.AppendLine("|---|---|---:|---:|");
        foreach (var g in rows.GroupBy(r => r.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.First();
            var rate = first.SampleRateHz.HasValue
                ? first.SampleRateHz.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "—";
            sb.AppendLine($"| `{g.Key}` | `{first.RelativeFile}` | {g.Count()} | {rate} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Sample DuckDB queries");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine("-- what channels exist?");
        sb.AppendLine("SELECT * FROM 'manifest.parquet';");
        sb.AppendLine();
        sb.AppendLine("-- fast per-channel stats without touching the raw data files");
        sb.AppendLine("SELECT * FROM 'summary.parquet' WHERE column_name LIKE '%Battery%';");
        sb.AppendLine();
        sb.AppendLine("-- correlate two subsystems by time (nearest-timestamp join)");
        sb.AppendLine("SELECT p.timestamp_utc, p.*, b.*");
        sb.AppendLine("FROM 'data/Propulsion_100hz.parquet' p");
        sb.AppendLine("ASOF JOIN 'data/Battery_1000hz.parquet' b USING (timestamp_utc);");
        sb.AppendLine("```");

        File.WriteAllText(path, sb.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime[]? TryReadTimestamps(TdmsChannel ch)
    {
        var raw = ch.GetData<object>().ToArray();
        if (raw.Length == 0 || raw[0] is not DateTime) return null;
        var dt = new DateTime[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            dt[i] = raw[i] is DateTime v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : DateTime.MinValue;
        return dt;
    }

    private static double? EstimateRateHz(DateTime[] timestamps)
    {
        if (timestamps.Length < 2) return null;
        var take = Math.Min(64, timestamps.Length - 1);
        var deltas = new List<double>(take);
        for (var i = 1; i <= take; i++)
        {
            var dt = (timestamps[i] - timestamps[i - 1]).TotalSeconds;
            if (dt > 0) deltas.Add(dt);
        }
        if (deltas.Count == 0) return null;
        deltas.Sort();
        var median = deltas[deltas.Count / 2];
        if (median <= 0) return null;
        var hz = 1.0 / median;
        // Snap to nearest integer if within 2%.
        var rounded = Math.Round(hz);
        if (rounded > 0 && Math.Abs(hz - rounded) / rounded < 0.02) return rounded;
        return hz;
    }

    private static string FormatRate(double hz)
    {
        if (hz >= 1)
        {
            var rounded = Math.Round(hz);
            if (Math.Abs(hz - rounded) / rounded < 0.02)
                return ((long)rounded).ToString(CultureInfo.InvariantCulture);
        }
        return hz.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_');
    }

    private static string MakeColumnName(string group, string channel, string? unit)
    {
        var g = SanitizeIdent(group);
        var c = SanitizeIdent(channel);
        var name = $"{g}_{c}";
        if (!string.IsNullOrWhiteSpace(unit))
        {
            var u = SanitizeIdent(unit);
            if (u.Length > 0) name += "_" + u;
        }
        return name;
    }

    private static string SanitizeIdent(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastUnderscore = false;
        foreach (var ch in s)
        {
            var ok = char.IsLetterOrDigit(ch);
            if (ok)
            {
                sb.Append(ch);
                lastUnderscore = false;
            }
            else if (!lastUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }
        while (sb.Length > 0 && sb[^1] == '_') sb.Length--;
        return sb.ToString();
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    private static Dictionary<string, string> ToStringDict(IDictionary<string, object> src) =>
        src.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? string.Empty);

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var needsQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static float? SafeToFloat(IConvertible c)
    {
        try { return (float)c.ToDouble(CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static int? SafeToInt32(IConvertible c)
    {
        try { return c.ToInt32(CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static long? SafeToInt64(IConvertible c)
    {
        try { return c.ToInt64(CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}

// ------------------------------------------------------------------ row records

internal sealed record ManifestRow
{
    public required string Group { get; init; }
    public required string Channel { get; init; }
    public required string ColumnName { get; init; }
    public required string Unit { get; init; }
    public required string Description { get; init; }
    public required string Dtype { get; init; }
    public required long SampleCount { get; init; }
    public double? SampleRateHz { get; init; }
    public DateTime? TStartUtc { get; init; }
    public DateTime? TEndUtc { get; init; }
    public required string RelativeFile { get; init; }
}

internal sealed record SummaryRow(
    string ColumnName,
    double? Min,
    double? Max,
    double? Mean,
    double? Std,
    double? First,
    double? Last,
    long NullCount);
