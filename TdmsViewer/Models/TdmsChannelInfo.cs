namespace TdmsViewer.Models;

/// <summary>Lightweight metadata for a channel; data is loaded lazily on demand.</summary>
public sealed class TdmsChannelInfo
{
    public const string TimeStampChannelName = "Time Stamp";

    public required string Group { get; init; }
    public required string Name { get; init; }
    public string? Unit { get; init; }
    public string? Description { get; init; }
    public long Count { get; init; }
    public string DataType { get; init; } = "Double";

    /// <summary>All raw TDMS properties (base + custom) as strings.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Properties of the owning group.</summary>
    public IReadOnlyDictionary<string, string> GroupProperties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>File (root) level properties.</summary>
    public IReadOnlyDictionary<string, string> RootProperties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>True when this channel is the group's shared time axis.</summary>
    public bool IsTimeStamp =>
        string.Equals(Name, TimeStampChannelName, StringComparison.OrdinalIgnoreCase);

    public string Path => $"{Group}/{Name}";
}
