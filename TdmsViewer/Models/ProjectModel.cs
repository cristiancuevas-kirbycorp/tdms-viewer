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

    /// <summary>Saved time (X) view for this page; null means auto-fit. Restored when the page is reopened.</summary>
    public double? XMin { get; set; }
    public double? XMax { get; set; }

    /// <summary>Two time cursors: on/off and their X positions, saved per page.</summary>
    public bool CursorsOn { get; set; }
    public double? CursorA { get; set; }
    public double? CursorB { get; set; }

    /// <summary>Free positions (0..1 fraction of the plot area, top-left) for the legend and cursor overlays. Null = default corner.</summary>
    public double? LegendX { get; set; }
    public double? LegendY { get; set; }
    public double? CursorX { get; set; }
    public double? CursorY { get; set; }
}

/// <summary>A corner of the plot area used to place the legend / cursor overlays.</summary>
public enum PlotCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
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

    /// <summary>Title used in the printed report header/footer.</summary>
    public string Title { get; set; } = "Report 1";

    public ReportSlot HeaderLeft { get; set; } = ReportSlot.ReportName;
    public ReportSlot HeaderMiddle { get; set; } = ReportSlot.Date;
    public ReportSlot HeaderRight { get; set; } = ReportSlot.None;
    public ReportSlot FooterLeft { get; set; } = ReportSlot.None;
    public ReportSlot FooterMiddle { get; set; } = ReportSlot.Date;
    public ReportSlot FooterRight { get; set; } = ReportSlot.PageNumber;

    /// <summary>Free text shown where a slot is set to <see cref="ReportSlot.CustomText"/>.</summary>
    public string CustomText { get; set; } = string.Empty;

    /// <summary>Image shown where a slot is set to <see cref="ReportSlot.CustomImage"/>.</summary>
    public string? CustomImagePath { get; set; }

    public List<PageModel> Pages { get; set; } = new();
}

/// <summary>Content options for a report header/footer position.</summary>
public enum ReportSlot
{
    None,
    ReportName,
    PageName,
    Date,
    DateTime,
    PageNumber,
    FileName,
    CustomText,
    CustomImage,
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
