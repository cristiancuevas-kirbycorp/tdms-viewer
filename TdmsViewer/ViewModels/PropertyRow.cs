namespace TdmsViewer.ViewModels;

/// <summary>A single row in the channel properties table.</summary>
public sealed class PropertyRow
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Value { get; init; }
}
