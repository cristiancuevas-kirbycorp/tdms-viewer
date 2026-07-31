using System.IO;
using TdmsViewer.Models;
using TdmsFile = NationalInstruments.Tdms.File;
using TdmsReader = NationalInstruments.Tdms.Reader;

namespace TdmsViewer.Services;

public interface ITdmsService
{
    /// <summary>Currently opened file path.</summary>
    string? CurrentPath { get; }

    /// <summary>Defragments the file in place (only when fragmented) then loads channel metadata.</summary>
    IReadOnlyList<TdmsChannelInfo> Open(string tdmsPath, bool defragment = true, IProgress<string>? progress = null);

    /// <summary>Loads Y data for a channel, using its group's "Time Stamp" channel as X when present.</summary>
    ChannelData LoadChannel(string group, string channel);
}

public sealed class TdmsService : ITdmsService
{
    public string? CurrentPath { get; private set; }

    public IReadOnlyList<TdmsChannelInfo> Open(string tdmsPath, bool defragment = true, IProgress<string>? progress = null)
    {
        if (!File.Exists(tdmsPath))
            throw new FileNotFoundException("TDMS file not found.", tdmsPath);

        var name = Path.GetFileName(tdmsPath);
        progress?.Report($"Opening {name}...");
        if (defragment && IsFragmented(tdmsPath))
        {
            progress?.Report($"Defragmenting {name} (one-time)...");
            DefragmentInPlace(tdmsPath);
        }
        CurrentPath = tdmsPath;
        progress?.Report($"Loading channels from {name}...");

        var channels = new List<TdmsChannelInfo>();
        using var file = new TdmsFile(CurrentPath).Open();
        var rootProps = file.Properties.ToDictionary(
            p => p.Key,
            p => p.Value?.ToString() ?? string.Empty);
        foreach (var group in file.Groups.Values)
        {
            var groupProps = group.Properties.ToDictionary(
                p => p.Key,
                p => p.Value?.ToString() ?? string.Empty);
            foreach (var channel in group.Channels.Values)
            {
                var props = channel.Properties.ToDictionary(
                    p => p.Key,
                    p => p.Value?.ToString() ?? string.Empty);

                channels.Add(new TdmsChannelInfo
                {
                    Group = group.Name,
                    Name = channel.Name,
                    Unit = props.TryGetValue("unit_string", out var u) ? u : null,
                    Description = props.TryGetValue("description", out var d) ? d
                        : props.TryGetValue("NI_ChannelDescription", out var d2) ? d2 : null,
                    Count = channel.DataCount,
                    DataType = channel.DataType?.Name ?? "Double",
                    Properties = props,
                    GroupProperties = groupProps,
                    RootProperties = rootProps,
                });
            }
        }
        return channels;
    }

    public ChannelData LoadChannel(string group, string channel)
    {
        if (CurrentPath is null)
            throw new InvalidOperationException("No TDMS file is open.");

        using var file = new TdmsFile(CurrentPath).Open();
        if (!file.Groups.TryGetValue(group, out var grp) ||
            !grp.Channels.TryGetValue(channel, out var ch))
        {
            throw new KeyNotFoundException($"Channel '{group}/{channel}' not found.");
        }

        var y = ToDoubleArray(ch.GetData<object>());

        if (grp.Channels.TryGetValue(TdmsChannelInfo.TimeStampChannelName, out var timeChannel) &&
            !ReferenceEquals(timeChannel, ch))
        {
            var raw = timeChannel.GetData<object>().Take(y.Length).ToArray();
            if (raw.Length > 0 && raw[0] is DateTime)
            {
                var x = raw.Select(v => ((DateTime)v!).ToOADate()).ToArray();
                return new ChannelData { X = Align(x, y.Length), Y = y, XIsDateTime = true };
            }
            var xn = ToDoubleArray(raw);
            return new ChannelData { X = Align(xn, y.Length), Y = y };
        }

        var index = Enumerable.Range(0, y.Length).Select(i => (double)i).ToArray();
        return new ChannelData { X = index, Y = y };
    }

    /// <summary>Rewrites the file consolidating all segments into one, returning the cleaned copy path.</summary>
    /// <summary>True when the file has more than one segment (i.e. worth defragmenting).</summary>
    private static bool IsFragmented(string tdmsPath)
    {
        try
        {
            using var stream = new FileStream(tdmsPath, FileMode.Open, FileAccess.Read);
            var reader = new TdmsReader(stream);
            var count = 0;
            for (var seg = reader.ReadFirstSegment(); seg is not null; seg = reader.ReadSegment(seg.NextSegmentOffset))
            {
                if (++count > 1)
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Rewrites the file as one consolidated segment via a temp file, then replaces the original.</summary>
    private static void DefragmentInPlace(string tdmsPath)
    {
        var temp = tdmsPath + ".defragtmp";
        if (File.Exists(temp))
            File.Delete(temp);

        try
        {
            using (var file = new TdmsFile(tdmsPath).Open())
                file.ReWrite(temp);

            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new IOException("Defragmentation produced an empty file.");

            try { File.Replace(temp, tdmsPath, null); }
            catch (Exception) { File.Move(temp, tdmsPath, overwrite: true); }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* leftover temp is harmless */ }
            }
        }
    }

    private static double[] ToDoubleArray(IEnumerable<object> data)
    {
        var list = new List<double>();
        foreach (var value in data)
        {
            list.Add(value switch
            {
                null => double.NaN,
                double d => d,
                float f => f,
                bool b => b ? 1d : 0d,
                DateTime dt => dt.ToOADate(),
                IConvertible c => SafeToDouble(c),
                _ => double.NaN,
            });
        }
        return list.ToArray();
    }

    private static double SafeToDouble(IConvertible c)
    {
        try { return c.ToDouble(null); }
        catch { return double.NaN; }
    }

    private static double[] Align(double[] source, int length)
    {
        if (source.Length == length) return source;
        var result = new double[length];
        var copy = Math.Min(source.Length, length);
        Array.Copy(source, result, copy);
        for (var i = copy; i < length; i++) result[i] = double.NaN;
        return result;
    }
}
