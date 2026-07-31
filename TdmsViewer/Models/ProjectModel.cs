namespace TdmsViewer.Models;

public enum AxisSide
{
    Left,
    Right
}

public enum SeriesLineStyle
{
    Solid,
    Dashed,
    Dotted
}

public enum SeriesMarker
{
    None,
    Circle,
    Square,
    Triangle,
    Diamond
}

/// <summary>A channel (or formula) drawn on a page's graph, assigned to a named Y-scale.</summary>
public sealed class PlotSeriesModel
{
    public string Group { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;

    /// <summary>Non-empty when this series is a calculated/formula channel.</summary>
    public string? Formula { get; set; }

    public string ColorHex { get; set; } = "#1F77B4";
    public double LineWidth { get; set; } = 2;
    public SeriesLineStyle LineStyle { get; set; } = SeriesLineStyle.Solid;
    public SeriesMarker Marker { get; set; } = SeriesMarker.None;
    public double MarkerSize { get; set; } = 5;
    public bool Visible { get; set; } = true;

    /// <summary>Id of the Y-scale this plot is drawn against (see <see cref="AxisModel"/>).</summary>
    public string AxisId { get; set; } = string.Empty;

    public bool IsFormula => !string.IsNullOrWhiteSpace(Formula);
    public string DisplayName => IsFormula ? Channel : $"{Group} / {Channel}";
}

public sealed class PageModel
{
    public string Name { get; set; } = "Graph 1";
    public List<PlotSeriesModel> Series { get; set; } = new();
    public List<AxisModel> Axes { get; set; } = new();
}

/// <summary>A first-class Y-scale (LabVIEW scale-legend style): named, sided, auto or fixed.</summary>
public sealed class AxisModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Y";
    public AxisSide Side { get; set; } = AxisSide.Left;
    public bool Auto { get; set; } = true;
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class ReportModel
{
    public string Name { get; set; } = "Report 1";
    public List<PageModel> Pages { get; set; } = new();
}

/// <summary>Root savable document (*.tvproj).</summary>
public sealed class ProjectModel
{
    public string? TdmsPath { get; set; }
    public List<ReportModel> Reports { get; set; } = new();
    public List<FormulaModel> Formulas { get; set; } = new();
}

/// <summary>A user-defined calculated channel evaluated against other channels.</summary>
public sealed class FormulaModel
{
    public string Name { get; set; } = "test_formula";
    public string Expression { get; set; } = string.Empty;
}
