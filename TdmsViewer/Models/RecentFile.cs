using System.IO;
using System.Text.Json.Serialization;

namespace TdmsViewer.Models;

/// <summary>A previously opened TDMS file, shown in the Recent Files menu.</summary>
public sealed class RecentFile
{
    public required string Path { get; init; }
    public DateTime OpenedUtc { get; init; }

    [JsonIgnore]
    public string DisplayName => System.IO.Path.GetFileName(Path);
}
