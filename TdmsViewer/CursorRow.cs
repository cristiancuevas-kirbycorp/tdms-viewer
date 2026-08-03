namespace TdmsViewer;

/// <summary>One plot's values at the two time cursors, plus stats between them.</summary>
public sealed class CursorRow
{
    public required string Plot { get; init; }
    public required string ColorHex { get; init; }
    public string ValueA { get; init; } = "";
    public string ValueB { get; init; } = "";
    public string Delta { get; init; } = "";
    public string Min { get; init; } = "";
    public string Max { get; init; } = "";
    public string PeakToPeak { get; init; } = "";
    public string Mean { get; init; } = "";
    public string Rms { get; init; } = "";
    public string StdDev { get; init; } = "";
    public string Integral { get; init; } = "";
    public string Slope { get; init; } = "";

    public string Get(string key) => key switch
    {
        "ValueA" => ValueA,
        "ValueB" => ValueB,
        "Delta" => Delta,
        "Min" => Min,
        "Max" => Max,
        "PeakToPeak" => PeakToPeak,
        "Mean" => Mean,
        "Rms" => Rms,
        "StdDev" => StdDev,
        "Integral" => Integral,
        "Slope" => Slope,
        _ => "",
    };
}
