namespace TdmsViewer.Models;

/// <summary>X/Y data for a single channel, ready to plot.</summary>
public sealed class ChannelData
{
    public required double[] X { get; init; }
    public required double[] Y { get; init; }

    /// <summary>True when X holds OLE Automation dates (DateTime axis).</summary>
    public bool XIsDateTime { get; init; }
}
