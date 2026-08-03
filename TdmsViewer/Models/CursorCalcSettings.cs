using System.IO;
using System.Text.Json;

namespace TdmsViewer.Models;

/// <summary>Which between-cursor calculations are shown in the cursor readout. Saved globally.</summary>
public sealed class CursorCalcSettings
{
    public bool ValueA { get; set; } = true;
    public bool ValueB { get; set; } = true;
    public bool Delta { get; set; } = true;
    public bool Min { get; set; } = true;
    public bool Max { get; set; } = true;
    public bool PeakToPeak { get; set; }
    public bool Mean { get; set; } = true;
    public bool Rms { get; set; }
    public bool StdDev { get; set; }
    public bool Integral { get; set; }
    public bool Slope { get; set; }

    /// <summary>Ordered columns: key, short header, and the dialog label.</summary>
    public static readonly (string Key, string Header, string Label)[] Columns =
    {
        ("ValueA", "A", "Value at cursor A"),
        ("ValueB", "B", "Value at cursor B"),
        ("Delta", "B\u2212A", "Delta (B \u2212 A)"),
        ("Min", "Min", "Minimum"),
        ("Max", "Max", "Maximum"),
        ("PeakToPeak", "Pk\u2011Pk", "Peak-to-peak (Max \u2212 Min)"),
        ("Mean", "Mean", "Mean (average)"),
        ("Rms", "RMS", "RMS (root mean square)"),
        ("StdDev", "Std", "Standard deviation"),
        ("Integral", "\u222B", "Integral (area, trapezoidal)"),
        ("Slope", "Slope", "Slope (\u0394Y / \u0394X between A and B)"),
    };

    public bool IsEnabled(string key) => key switch
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
        _ => false,
    };

    public void Set(string key, bool value)
    {
        switch (key)
        {
            case "ValueA": ValueA = value; break;
            case "ValueB": ValueB = value; break;
            case "Delta": Delta = value; break;
            case "Min": Min = value; break;
            case "Max": Max = value; break;
            case "PeakToPeak": PeakToPeak = value; break;
            case "Mean": Mean = value; break;
            case "Rms": Rms = value; break;
            case "StdDev": StdDev = value; break;
            case "Integral": Integral = value; break;
            case "Slope": Slope = value; break;
        }
    }

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TdmsViewer", "cursor-calcs.json");

    public static CursorCalcSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<CursorCalcSettings>(File.ReadAllText(StorePath)) ?? new CursorCalcSettings();
        }
        catch { /* fall back to defaults */ }
        return new CursorCalcSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch { /* non-fatal */ }
    }
}
