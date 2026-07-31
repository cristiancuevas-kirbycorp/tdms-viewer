namespace TdmsViewer;

/// <summary>One plot's values at the two time cursors, plus stats between them.</summary>
public sealed class CursorRow
{
    public required string Plot { get; init; }
    public required string ColorHex { get; init; }
    public required string ValueA { get; init; }
    public required string ValueB { get; init; }
    public required string Delta { get; init; }
    public required string Min { get; init; }
    public required string Max { get; init; }
    public required string Avg { get; init; }
}
