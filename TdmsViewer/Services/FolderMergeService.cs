using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TdmsViewer.Services;

/// <summary>Extracts zipped TDMS files from a folder and concatenates them into one merged TDMS file.</summary>
public sealed class FolderMergeService
{
    // Matches a date (optionally with time) embedded in a filename, e.g. 2024-05-01, 20240501_130502.
    private static readonly Regex DatePattern = new(
        @"(?<y>\d{4})[-_]?(?<mo>\d{2})[-_]?(?<d>\d{2})(?:[ _T-]?(?<h>\d{2})[-_:]?(?<mi>\d{2})(?:[-_:]?(?<s>\d{2}))?)?",
        RegexOptions.Compiled);

    /// <summary>
    /// Finds all .zip files in <paramref name="folderPath"/>, extracts their .tdms contents, orders them by the
    /// earliest date in the filename (falling back to a natural filename sort), and binary-concatenates them into a
    /// single "&lt;earliest&gt;_merged.tdms" file placed in the same folder. Returns the merged file path.
    /// </summary>
    public string MergeZippedFolder(string folderPath, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var zips = Directory.GetFiles(folderPath, "*.zip", SearchOption.TopDirectoryOnly);
        if (zips.Length == 0)
            throw new InvalidOperationException("No .zip files were found in the selected folder.");

        var tempDir = Path.Combine(Path.GetTempPath(), "TdmsMerge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var extracted = new List<(string OriginalName, string Path)>();
            foreach (var zip in zips)
            {
                progress?.Report($"Unzipping {Path.GetFileName(zip)}...");
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Length == 0 && string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                    if (!entry.Name.EndsWith(".tdms", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Name.EndsWith(".tdms_index", StringComparison.OrdinalIgnoreCase)) continue;

                    var dest = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(zip)}__{entry.Name}");
                    entry.ExtractToFile(dest, overwrite: true);
                    extracted.Add((entry.Name, dest));
                }
            }

            if (extracted.Count == 0)
                throw new InvalidOperationException("The selected folder's zip files contain no .tdms files.");

            var ordered = extracted
                .OrderBy(x => ExtractDate(x.OriginalName) ?? DateTime.MaxValue)
                .ThenBy(x => x.OriginalName, NaturalStringComparer.Instance)
                .ToList();

            var baseName = Path.GetFileNameWithoutExtension(ordered[0].OriginalName);
            var outputPath = Path.Combine(folderPath, $"{baseName}_merged.tdms");

            progress?.Report($"Merging {ordered.Count} files into {Path.GetFileName(outputPath)}...");
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                foreach (var (name, path) in ordered)
                {
                    progress?.Report($"Appending {name}...");
                    using var input = File.OpenRead(path);
                    input.CopyTo(output);
                }
            }

            return outputPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* leftover temp is harmless */ }
        }
    }

    /// <summary>Parses the first date/time embedded in a filename, or null when none is present.</summary>
    private static DateTime? ExtractDate(string fileName)
    {
        var m = DatePattern.Match(fileName);
        if (!m.Success) return null;

        try
        {
            var year = int.Parse(m.Groups["y"].Value);
            var month = int.Parse(m.Groups["mo"].Value);
            var day = int.Parse(m.Groups["d"].Value);
            if (month is < 1 or > 12 || day is < 1 or > 31) return null;

            var hour = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) : 0;
            var minute = m.Groups["mi"].Success ? int.Parse(m.Groups["mi"].Value) : 0;
            var second = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value) : 0;
            if (hour > 23 || minute > 59 || second > 59) return null;

            return new DateTime(year, month, day, hour, minute, second);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Orders strings so embedded numbers sort by value (e.g. "File 2" before "File 10").</summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    private static readonly Regex Chunk = new(@"\d+|\D+", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;

        var xs = Chunk.Matches(x);
        var ys = Chunk.Matches(y);
        var count = Math.Min(xs.Count, ys.Count);

        for (var i = 0; i < count; i++)
        {
            var a = xs[i].Value;
            var b = ys[i].Value;

            int cmp;
            if (char.IsDigit(a[0]) && char.IsDigit(b[0]) &&
                long.TryParse(a, out var na) && long.TryParse(b, out var nb))
            {
                cmp = na.CompareTo(nb);
            }
            else
            {
                cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }

            if (cmp != 0) return cmp;
        }

        return xs.Count.CompareTo(ys.Count);
    }
}
