using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ScottPlot;
using TdmsViewer.Models;
using TdmsViewer.Services;
using TdmsViewer.ViewModels;

namespace TdmsViewer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IUpdateService _update = new UpdateService();
    private Point _boxStart;
    private bool _boxDragging;
    private Point _dragStart;
    private TdmsChannelInfo? _dragChannel;
    private bool _panning;
    private Point _panStartPixel;
    private double _panDataPerPixel;
    private AxisLimits _panStartLimits;
    private bool _hasPlotContent;
    private bool _cursorsOn;
    private readonly double[] _cursorX = new double[2];
    private readonly List<ScottPlot.Plottables.VerticalLine> _cursorLines = new();
    private ScottPlot.Plottables.HorizontalSpan? _cursorBand;
    private int _draggingCursor = -1;
    private readonly List<(string Name, double[] X, double[] Y, string ColorHex, bool Dt)> _rendered = new();
    private bool _renderedDateTime;
    private List<AxisViewModel> _renderedScales = new();
    private FrameworkElement? _dragOverlay;
    private Point _dragOffset;
    private bool _restoringCursors;
    private CursorCalcSettings _calcs = CursorCalcSettings.Load();

    // Axis tick-label sizes (print is higher-resolution so it needs a larger value).
    private const float AxisTickFontSize = 15f;
    private const float PrintTickFontSize = 22f;

    public string AppVersion { get; }

    public MainWindow()
    {
        InitializeComponent();

        var v = GetType().Assembly.GetName().Version;
        AppVersion = v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        Title = $"TDMS Viewer {AppVersion}";

        // We drive pan/zoom ourselves to keep the Y axes fixed while moving in time.
        WpfPlot.UserInputProcessor.IsEnabled = false;

        _vm = new MainViewModel(new TdmsService(), new FormulaService(), new ProjectService());
        _vm.PlotInvalidated += (_, fitX) => { RenderPlot(fitX); _vm.ScheduleAutoSave(); };
        _vm.PreviewInvalidated += (_, _) => RenderPreview();
        _vm.ColorPickRequested += OnColorPickRequested;
        DataContext = _vm;

        PlotHost.SizeChanged += (_, _) => RepositionOverlays();

        Loaded += async (_, _) =>
        {
            await _vm.RestoreActiveWorkspaceAsync();
            await CheckForUpdatesAsync(silent: true);
        };
    }

    // --- Workspace tabs ---

    private void WorkspaceTabs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is WorkspaceViewModel ws)
            ws.IsEditing = true;
    }

    // Handled on mouse-down so closing an inactive tab doesn't select (and load) it first.
    private void WorkspaceClose_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel ws })
        {
            _vm.CloseWorkspaceCommand.Execute(ws);
            e.Handled = true;
        }
    }

    private void WorkspaceRename_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void WorkspaceRename_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && sender is TextBox tb)
        {
            CommitWorkspaceRename(tb);
            Keyboard.ClearFocus();
        }
    }

    private void WorkspaceRename_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitWorkspaceRename(tb);
    }

    private void CommitWorkspaceRename(TextBox tb)
    {
        if (tb.DataContext is not WorkspaceViewModel ws) return;
        if (string.IsNullOrWhiteSpace(ws.Name))
            ws.Name = System.IO.Path.GetFileName(ws.Path);
        ws.IsEditing = false;
        _vm.SaveWorkspaces();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PersistConfig();
        base.OnClosed(e);
    }

    private void ReportsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case ReportViewModel report:
                _vm.SelectedReport = report;
                _vm.SelectedPage = report.Pages.FirstOrDefault();
                _vm.ShowReportSettings = true;
                break;
            case PageViewModel page:
                _vm.SelectedPage = page;
                _vm.ShowReportSettings = false;
                break;
        }
    }

    private void ChannelsTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChannelsTree.SelectedItem is ChannelTreeItemViewModel { IsGroup: false, Channel: { } info })
            _vm.AddChannel(info);
    }

    private void ChannelsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ChannelTreeItemViewModel { IsGroup: false, Channel: { } info })
            _ = _vm.SelectChannelAsync(info);
    }

    // --- Drag channel from tree onto the graph ---

    private void ChannelsTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragChannel = (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            as ChannelTreeItemViewModel) is { IsGroup: false, Channel: { } info } ? info : null;
    }

    private void ChannelsTree_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragChannel is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(typeof(TdmsChannelInfo), _dragChannel);
        DragDrop.DoDragDrop(ChannelsTree, data, DragDropEffects.Copy);
        _dragChannel = null;
    }

    private void Plot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TdmsChannelInfo)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Plot_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TdmsChannelInfo)) is TdmsChannelInfo info)
            _vm.AddChannel(info);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        // Runs/inlines are content elements, not visuals, so walk logical parents for those.
        while (current is not null and not T)
        {
            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement fce => fce.Parent,
                _ => LogicalTreeHelper.GetParent(current),
            };
        }
        return current as T;
    }

    private void RenderPreview()
    {
        var plot = PreviewPlot.Plot;
        plot.Clear();

        var data = _vm.PreviewData;
        if (data is null || data.Y.Length == 0)
        {
            PreviewPlot.Refresh();
            return;
        }

        var signal = plot.Add.SignalXY(data.X, data.Y);
        plot.HideLegend();
        if (_vm.SelectedChannelInfo is { } info)
            plot.Title(info.Name);
        if (data.XIsDateTime)
            plot.Axes.DateTimeTicksBottom();

        var (xMin, xMax, xHas) = FiniteExtent(data.X);
        var (yMin, yMax, yHas) = FiniteExtent(data.Y);
        if (xHas && yHas)
        {
            var (xLo, xHi) = PadX(xMin, xMax);
            var (yLo, yHi) = PadY(yMin, yMax);
            plot.Axes.SetLimits(xLo, xHi, yLo, yHi);
        }
        else
        {
            plot.Axes.AutoScale();
        }
        PreviewPlot.Refresh();
    }

    private void RenderPlot(bool fitX = true)
    {
        var plot = WpfPlot.Plot;
        // Read the X (time) range from the bottom axis; GetLimits() touches Left, which may be absent.
        var priorXLeft = plot.Axes.Bottom.Min;
        var priorXRight = plot.Axes.Bottom.Max;
        plot.Clear();
        plot.HideLegend();
        _cursorLines.Clear();
        _cursorBand = null;
        _rendered.Clear();
        plot.Axes.Remove(ScottPlot.Edge.Left);
        plot.Axes.Remove(ScottPlot.Edge.Right);

        var page = _vm.SelectedPage;
        if (page is null)
        {
            plot.Axes.AddLeftAxis();
            GraphLegend.Visibility = Visibility.Collapsed;
            CursorPanel.Visibility = Visibility.Collapsed;
            WpfPlot.Refresh();
            return;
        }

        var anyDateTime = false;
        var xMin = double.PositiveInfinity;
        var xMax = double.NegativeInfinity;
        var axisExtents = new List<(IYAxis Axis, double Min, double Max, bool Has, AxisViewModel Scale)>();

        // One ScottPlot axis per defined scale that has visible plots assigned to it.
        foreach (var scale in page.Axes)
        {
            var members = page.Series.Where(s => s.Visible && s.AxisId == scale.Id).ToList();
            if (members.Count == 0) continue;

            var axis = scale.Side == AxisSide.Left
                ? (IYAxis)plot.Axes.AddLeftAxis()
                : plot.Axes.AddRightAxis();

            var yMin = double.PositiveInfinity;
            var yMax = double.NegativeInfinity;
            var yHas = false;

            foreach (var series in members)
            {
                try
                {
                    var data = _vm.GetSeriesData(series.Model);
                    if (data.Y.Length == 0) continue;

                    AddSeries(plot, series, data, axis);
                    anyDateTime |= data.XIsDateTime;
                    _rendered.Add((series.DisplayName, data.X, data.Y, series.ColorHex, data.XIsDateTime));

                    var (xLo, xHi, xHas) = FiniteExtent(data.X);
                    if (xHas) { xMin = Math.Min(xMin, xLo); xMax = Math.Max(xMax, xHi); }

                    var (yLo, yHi, yFound) = FiniteExtent(data.Y);
                    if (yFound) { yMin = Math.Min(yMin, yLo); yMax = Math.Max(yMax, yHi); yHas = true; }
                }
                catch (Exception ex)
                {
                    // A single bad channel shouldn't take down the plot.
                    App.Log(ex);
                }
            }

            axis.Label.Text = scale.Name;
            if (members.Count == 1)
            {
                var c = ScottPlot.Color.FromHex(members[0].ColorHex.TrimStart('#'));
                axis.Label.ForeColor = c;
                axis.TickLabelStyle.ForeColor = c;
            }

            axisExtents.Add((axis, yMin, yMax, yHas, scale));
        }

        // ScottPlot's renderer requires a left axis; add a hidden placeholder when all scales are right-side.
        if (!axisExtents.Any(a => a.Scale.Side == AxisSide.Left))
        {
            var placeholder = plot.Axes.AddLeftAxis();
            placeholder.IsVisible = axisExtents.Count == 0;
        }

        if (anyDateTime)
            plot.Axes.DateTimeTicksBottom();

        // Explicit, NaN-safe scaling: shared X, independent Y per axis (auto or manual).
        if (double.IsFinite(xMin) && double.IsFinite(xMax) && xMax >= xMin)
        {
            var m = page.Model;
            var preserveX = !fitX && _hasPlotContent
                && double.IsFinite(priorXLeft) && priorXRight > priorXLeft;

            // Priority: this page's saved X view, then keep current (Y/style edits), then fit.
            double xa, xb;
            if (m.XMin is double smin && m.XMax is double smax
                && double.IsFinite(smin) && double.IsFinite(smax) && smax > smin)
                (xa, xb) = (smin, smax);
            else if (preserveX)
                (xa, xb) = (priorXLeft, priorXRight);
            else
                (xa, xb) = PadX(xMin, xMax);

            foreach (var (axis, min, max, has, scale) in axisExtents)
            {
                double ya, yb;
                if (scale is { Auto: false } && scale.Max > scale.Min)
                {
                    ya = scale.Min;
                    yb = scale.Max;
                }
                else if (has)
                {
                    (ya, yb) = PadY(min, max);
                    scale?.SetComputed(ya, yb);
                }
                else
                {
                    continue;
                }
                plot.Axes.SetLimits(new AxisLimits(xa, xb, ya, yb), plot.Axes.Bottom, axis);
            }
        }

        _hasPlotContent = axisExtents.Any(a => a.Has);
        _renderedDateTime = _rendered.Any(r => r.Dt);
        _renderedScales = axisExtents.Select(a => a.Scale).ToList();
        foreach (var (axis, _, _, _, _) in axisExtents) axis.TickLabelStyle.FontSize = AxisTickFontSize;
        plot.Axes.Bottom.TickLabelStyle.FontSize = AxisTickFontSize;
        UpdatePlotOverlays();
        WpfPlot.Refresh();
    }

    // Refreshes the WPF legend items and restores overlay corners + cursors for the active page.
    private void UpdatePlotOverlays()
    {
        var items = _rendered.Select(r => new LegendItem(r.Name, r.ColorHex)).ToList();
        LegendItems.ItemsSource = items;
        GraphLegend.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.SelectedPage is not { } page) return;
        PositionOverlay(GraphLegend, page.Model.LegendX, page.Model.LegendY, defaultRight: true, defaultBottom: true);
        PositionOverlay(CursorPanel, page.Model.CursorX, page.Model.CursorY, defaultRight: false, defaultBottom: false);
        RestoreCursorsForPage(page);
    }

    private static (double Min, double Max, bool Has) FiniteExtent(double[] values)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        var has = false;
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            has = true;
        }
        return (min, max, has);
    }

    private static (double Lo, double Hi) PadY(double min, double max)
    {
        if (min == max)
        {
            var p = min == 0 ? 1 : Math.Abs(min) * 0.05;
            return (min - p, max + p);
        }
        var pad = (max - min) * 0.05;
        return (min - pad, max + pad);
    }

    private static (double Lo, double Hi) PadX(double min, double max)
    {
        if (min == max)
            return (min - 1, max + 1);
        return (min, max);
    }

    /// <summary>Adds a series: fast SignalXY for line-only ascending data, Scatter otherwise (markers or unsorted X).</summary>
    private static void AddSeries(Plot plot, SeriesViewModel series, ChannelData data, IYAxis axis)
    {
        var color = ScottPlot.Color.FromHex(series.ColorHex.TrimStart('#'));
        var pattern = series.LineStyle switch
        {
            SeriesLineStyle.Dashed => ScottPlot.LinePattern.Dashed,
            SeriesLineStyle.Dotted => ScottPlot.LinePattern.Dotted,
            _ => ScottPlot.LinePattern.Solid,
        };

        // SignalXY requires strictly ascending X; fall back to Scatter when it isn't (or when markers are on).
        if (series.Marker == SeriesMarker.None && IsAscending(data.X))
        {
            var s = plot.Add.SignalXY(data.X, data.Y);
            s.Color = color;
            s.LineWidth = (float)series.LineWidth;
            s.LineStyle.Pattern = pattern;
            s.LegendText = series.DisplayName;
            s.Axes.YAxis = axis;
        }
        else
        {
            var s = plot.Add.Scatter(data.X, data.Y);
            s.Color = color;
            s.LineWidth = (float)series.LineWidth;
            s.LineStyle.Pattern = pattern;
            s.MarkerShape = series.Marker switch
            {
                SeriesMarker.Circle => ScottPlot.MarkerShape.FilledCircle,
                SeriesMarker.Square => ScottPlot.MarkerShape.FilledSquare,
                SeriesMarker.Triangle => ScottPlot.MarkerShape.FilledTriangleUp,
                SeriesMarker.Diamond => ScottPlot.MarkerShape.FilledDiamond,
                _ => ScottPlot.MarkerShape.None,
            };
            s.MarkerSize = (float)series.Model.MarkerSize;
            s.LegendText = series.DisplayName;
            s.Axes.YAxis = axis;
        }
    }

    private static bool IsAscending(double[] x)
    {
        for (var i = 1; i < x.Length; i++)
            if (x[i] < x[i - 1]) return false;
        return true;
    }

    private void OnColorPickRequested(object? sender, EventArgs e)
    {
        if (sender is not SeriesViewModel series) return;

        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var drawing = (System.Windows.Media.Color)ColorConverter.ConvertFromString(series.ColorHex);
            dialog.Color = System.Drawing.Color.FromArgb(drawing.R, drawing.G, drawing.B);
        }
        catch { /* keep default */ }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            series.ColorHex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    // --- Zoom toolbar ---

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPage is { } page)
        {
            page.Model.XMin = null;
            page.Model.XMax = null;
            _vm.ScheduleAutoSave();
        }
        RenderPlot(fitX: true);
    }

    // Captures the current time (X) view onto the active page so reopening it restores the same zoom.
    private void SaveCurrentXRange()
    {
        if (_vm.SelectedPage is not { } page) return;
        var b = WpfPlot.Plot.Axes.Bottom;
        if (double.IsFinite(b.Min) && double.IsFinite(b.Max) && b.Max > b.Min)
        {
            page.Model.XMin = b.Min;
            page.Model.XMax = b.Max;
            _vm.ScheduleAutoSave();
        }
    }

    // Maps a double-click near a Y axis to that scale's editor (title area = rename, numbers area = min/max).
    private bool TryEditAxisAtPoint(Point p)
    {
        var (scale, titleArea) = HitTestAxis(p);
        if (scale is null) return false;
        new ScaleEditorWindow(scale, focusName: titleArea) { Owner = this }.ShowDialog();
        return true;
    }

    // Returns the scale under a point in the left/right axis band, and whether the point is over the title (vs numbers).
    private (AxisViewModel? Scale, bool TitleArea) HitTestAxis(Point p)
    {
        if (_renderedScales.Count == 0) return (null, false);

        var rect = WpfPlot.Plot.RenderManager.LastRender.DataRect;
        if (rect.Width <= 0 || rect.Height <= 0) return (null, false);

        var dpi = VisualTreeHelper.GetDpi(WpfPlot);
        var left = rect.Left / dpi.DpiScaleX;
        var right = rect.Right / dpi.DpiScaleX;
        var top = rect.Top / dpi.DpiScaleY;
        var bottom = rect.Bottom / dpi.DpiScaleY;

        if (p.Y < top - 4 || p.Y > bottom + 4) return (null, false);

        AxisSide side;
        bool titleArea;
        if (p.X < left)
        {
            side = AxisSide.Left;
            titleArea = p.X < left * 0.5;
        }
        else if (p.X > right)
        {
            side = AxisSide.Right;
            titleArea = p.X > right + (WpfPlot.ActualWidth - right) * 0.5;
        }
        else
        {
            return (null, false);
        }

        return (_renderedScales.FirstOrDefault(s => s.Side == side), titleArea);
    }

    // Right-click on a Y axis opens a menu to edit/move/add/delete that scale.
    private void Plot_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var (scale, _) = HitTestAxis(e.GetPosition(WpfPlot));
        var menu = new ContextMenu();

        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }

        if (scale is not null)
        {
            menu.Items.Add(Item("Scale settings\u2026", () => new ScaleEditorWindow(scale, true) { Owner = this }.ShowDialog()));
            menu.Items.Add(Item("Set min / max\u2026", () => new ScaleEditorWindow(scale, false) { Owner = this }.ShowDialog()));

            var auto = new MenuItem { Header = "Auto-scale", IsCheckable = true, IsChecked = scale.Auto };
            auto.Click += (s, _) => scale.Auto = ((MenuItem)s).IsChecked;
            menu.Items.Add(auto);

            menu.Items.Add(new Separator());
            var toRight = scale.Side == AxisSide.Left;
            menu.Items.Add(Item(toRight ? "Move to right side" : "Move to left side",
                () => scale.Side = toRight ? AxisSide.Right : AxisSide.Left));

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Add scale", () => _vm.AddAxisCommand.Execute(null)));
            menu.Items.Add(Item("Delete scale", () => _vm.RemoveAxisCommand.Execute(scale),
                enabled: (_vm.SelectedPage?.Axes.Count ?? 0) > 1));

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Reset zoom (fit all)", () => RenderPlot(fitX: true)));
        }
        else
        {
            menu.Items.Add(Item("Add scale", () => _vm.AddAxisCommand.Execute(null)));
            menu.Items.Add(Item("Reset zoom (fit all)", () => RenderPlot(fitX: true)));
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    // Scroll wheel zooms only the shared time (X) axis, centered on the cursor.
    private void Plot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var plot = WpfPlot.Plot;
        var dpi = VisualTreeHelper.GetDpi(WpfPlot);
        var pos = e.GetPosition(WpfPlot);
        var mouseX = plot.GetCoordinates(
            new Pixel((float)(pos.X * dpi.DpiScaleX), (float)(pos.Y * dpi.DpiScaleY))).X;

        var limits = plot.Axes.GetLimits();
        var factor = e.Delta > 0 ? 0.82 : 1 / 0.82;
        var left = mouseX - (mouseX - limits.Left) * factor;
        var right = mouseX + (limits.Right - mouseX) * factor;

        plot.Axes.SetLimitsX(left, right);
        WpfPlot.Refresh();
        SaveCurrentXRange();
        e.Handled = true;
    }

    // Left-drag pans the time (X) axis only; Y axes stay put. With cursors on, grabs a nearby cursor instead.
    private void Plot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click on a Y-axis: edit its name (title area) or min/max (numbers area).
        if (e.ClickCount == 2 && TryEditAxisAtPoint(e.GetPosition(WpfPlot)))
        {
            e.Handled = true;
            return;
        }

        var plot = WpfPlot.Plot;
        var dpi = VisualTreeHelper.GetDpi(WpfPlot);
        var p = e.GetPosition(WpfPlot);
        var dataX = plot.GetCoordinates(new Pixel((float)(p.X * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X;

        if (_cursorsOn && _cursorLines.Count == 2)
        {
            var perPixel = Math.Abs(
                plot.GetCoordinates(new Pixel((float)((p.X + 1) * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X - dataX);
            var tolerance = perPixel * 6;
            var near = -1;
            var best = double.MaxValue;
            for (var i = 0; i < 2; i++)
            {
                var d = Math.Abs(_cursorX[i] - dataX);
                if (d <= tolerance && d < best) { best = d; near = i; }
            }
            if (near >= 0)
            {
                _draggingCursor = near;
                WpfPlot.CaptureMouse();
                WpfPlot.Cursor = Cursors.SizeWE;
                return;
            }
        }

        _panStartPixel = p;
        _panStartLimits = plot.Axes.GetLimits();
        var xa = plot.GetCoordinates(new Pixel((float)(p.X * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X;
        var xb = plot.GetCoordinates(new Pixel((float)((p.X + 100) * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X;
        _panDataPerPixel = (xb - xa) / 100.0;
        _panning = true;
        WpfPlot.CaptureMouse();
        WpfPlot.Cursor = Cursors.ScrollWE;
    }

    private void Plot_MouseMove(object sender, MouseEventArgs e)
    {
        var plot = WpfPlot.Plot;
        if (_draggingCursor >= 0)
        {
            var dpi = VisualTreeHelper.GetDpi(WpfPlot);
            var p = e.GetPosition(WpfPlot);
            var dataX = plot.GetCoordinates(new Pixel((float)(p.X * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X;
            _cursorX[_draggingCursor] = dataX;
            _cursorLines[_draggingCursor].X = dataX;
            if (_cursorBand is not null)
            {
                _cursorBand.X1 = Math.Min(_cursorX[0], _cursorX[1]);
                _cursorBand.X2 = Math.Max(_cursorX[0], _cursorX[1]);
            }
            UpdateCursorReadout();
            WpfPlot.Refresh();
            return;
        }

        if (!_panning)
        {
            // Hovering over a cursor line shows the resize (left/right) cursor to hint it's draggable.
            if (_cursorsOn && _cursorLines.Count == 2)
            {
                var dpi = VisualTreeHelper.GetDpi(WpfPlot);
                var p = e.GetPosition(WpfPlot);
                var dataX = plot.GetCoordinates(new Pixel((float)(p.X * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X;
                var perPixel = Math.Abs(
                    plot.GetCoordinates(new Pixel((float)((p.X + 1) * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY))).X - dataX);
                var tol = perPixel * 6;
                WpfPlot.Cursor = _cursorX.Any(cx => Math.Abs(cx - dataX) <= tol) ? Cursors.SizeWE : Cursors.Arrow;
            }
            return;
        }

        var dx = e.GetPosition(WpfPlot).X - _panStartPixel.X;
        var shift = -dx * _panDataPerPixel;
        plot.Axes.SetLimitsX(_panStartLimits.Left + shift, _panStartLimits.Right + shift);
        WpfPlot.Refresh();
    }

    private void Plot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingCursor >= 0)
        {
            _draggingCursor = -1;
            WpfPlot.ReleaseMouseCapture();
            WpfPlot.Cursor = Cursors.Arrow;
            if (_vm.SelectedPage is { } page)
            {
                page.Model.CursorA = _cursorX[0];
                page.Model.CursorB = _cursorX[1];
                _vm.ScheduleAutoSave();
            }
            return;
        }
        if (!_panning) return;
        _panning = false;
        WpfPlot.ReleaseMouseCapture();
        WpfPlot.Cursor = Cursors.Arrow;
        SaveCurrentXRange();
    }

    // --- Cursors ---

    private static readonly ScottPlot.Color CursorColorA = ScottPlot.Color.FromHex("2D7DD2");
    private static readonly ScottPlot.Color CursorColorB = ScottPlot.Color.FromHex("E8710A");

    private void Cursors_Checked(object sender, RoutedEventArgs e)
    {
        if (_restoringCursors) return;
        _cursorsOn = true;
        var limits = WpfPlot.Plot.Axes.GetLimits();
        if (double.IsFinite(limits.Left) && limits.Right > limits.Left)
        {
            var span = limits.Right - limits.Left;
            _cursorX[0] = limits.Left + span * 0.33;
            _cursorX[1] = limits.Left + span * 0.66;
        }
        if (_vm.SelectedPage is { } page)
        {
            page.Model.CursorsOn = true;
            page.Model.CursorA = _cursorX[0];
            page.Model.CursorB = _cursorX[1];
            PositionOverlay(CursorPanel, page.Model.CursorX, page.Model.CursorY, defaultRight: false, defaultBottom: false);
            _vm.ScheduleAutoSave();
        }
        CursorPanel.Visibility = Visibility.Visible;
        DrawCursors();
        WpfPlot.Refresh();
    }

    private void Cursors_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_restoringCursors) return;
        _cursorsOn = false;
        RemoveCursorPlottables();
        CursorPanel.Visibility = Visibility.Collapsed;
        if (_vm.SelectedPage is { } page)
        {
            page.Model.CursorsOn = false;
            _vm.ScheduleAutoSave();
        }
        WpfPlot.Refresh();
    }

    // Applies the saved cursor state for a page after a render (called from UpdatePlotOverlays).
    private void RestoreCursorsForPage(PageViewModel page)
    {
        _restoringCursors = true;
        CursorToggle.IsChecked = page.Model.CursorsOn;
        _restoringCursors = false;

        if (page.Model.CursorsOn)
        {
            _cursorsOn = true;
            var limits = WpfPlot.Plot.Axes.GetLimits();
            var span = limits.Right - limits.Left;
            _cursorX[0] = page.Model.CursorA ?? (double.IsFinite(limits.Left) ? limits.Left + span * 0.33 : 0);
            _cursorX[1] = page.Model.CursorB ?? (double.IsFinite(limits.Left) ? limits.Left + span * 0.66 : 1);
            DrawCursors();
            CursorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _cursorsOn = false;
            RemoveCursorPlottables();
            CursorPanel.Visibility = Visibility.Collapsed;
        }
    }

    // --- Draggable corner overlays (graph legend + cursor readout) ---

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        _dragOverlay = el;
        var p = e.GetPosition(PlotHost);
        var tl = el.TranslatePoint(new Point(0, 0), PlotHost);
        _dragOffset = new Point(p.X - tl.X, p.Y - tl.Y);
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOverlay is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(PlotHost);
        var maxX = Math.Max(0, PlotHost.ActualWidth - _dragOverlay.ActualWidth);
        var maxY = Math.Max(0, PlotHost.ActualHeight - _dragOverlay.ActualHeight);
        _dragOverlay.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _dragOverlay.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        _dragOverlay.Margin = new Thickness(Math.Clamp(p.X - _dragOffset.X, 0, maxX), Math.Clamp(p.Y - _dragOffset.Y, 0, maxY), 0, 0);
        e.Handled = true;
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragOverlay is null) return;
        var el = _dragOverlay;
        _dragOverlay = null;
        el.ReleaseMouseCapture();

        if (_vm.SelectedPage is { } page && PlotHost.ActualWidth > 0 && PlotHost.ActualHeight > 0)
        {
            var tl = el.TranslatePoint(new Point(0, 0), PlotHost);
            var fx = Math.Clamp(tl.X / PlotHost.ActualWidth, 0, 1);
            var fy = Math.Clamp(tl.Y / PlotHost.ActualHeight, 0, 1);
            if (ReferenceEquals(el, GraphLegend)) { page.Model.LegendX = fx; page.Model.LegendY = fy; }
            else { page.Model.CursorX = fx; page.Model.CursorY = fy; }
            _vm.ScheduleAutoSave();
        }
        e.Handled = true;
    }

    // Places an overlay at its saved fraction, or at a default corner when unset.
    private void PositionOverlay(FrameworkElement el, double? fx, double? fy, bool defaultRight, bool defaultBottom)
    {
        if (fx is double x && fy is double y && PlotHost.ActualWidth > 0 && PlotHost.ActualHeight > 0)
        {
            el.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            el.VerticalAlignment = System.Windows.VerticalAlignment.Top;
            el.Margin = new Thickness(x * PlotHost.ActualWidth, y * PlotHost.ActualHeight, 0, 0);
        }
        else
        {
            el.HorizontalAlignment = defaultRight ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
            el.VerticalAlignment = defaultBottom ? System.Windows.VerticalAlignment.Bottom : System.Windows.VerticalAlignment.Top;
            el.Margin = new Thickness(10);
        }
    }

    // Keeps overlays anchored to their saved fractions when the plot area resizes.
    private void RepositionOverlays()
    {
        if (_vm.SelectedPage is not { } page) return;
        PositionOverlay(GraphLegend, page.Model.LegendX, page.Model.LegendY, defaultRight: true, defaultBottom: true);
        PositionOverlay(CursorPanel, page.Model.CursorX, page.Model.CursorY, defaultRight: false, defaultBottom: false);
    }

    // Nearest corner to where the legend sits (the printed legend can only use corners).
    private static ScottPlot.Alignment PrintLegendAlignment(PageViewModel page)
    {
        var right = (page.Model.LegendX ?? 1.0) > 0.4;
        var bottom = (page.Model.LegendY ?? 1.0) > 0.4;
        return (right, bottom) switch
        {
            (false, false) => ScottPlot.Alignment.UpperLeft,
            (true, false) => ScottPlot.Alignment.UpperRight,
            (false, true) => ScottPlot.Alignment.LowerLeft,
            _ => ScottPlot.Alignment.LowerRight,
        };
    }

    private sealed record LegendItem(string Name, string ColorHex);

    private void RemoveCursorPlottables()
    {
        var plot = WpfPlot.Plot;
        foreach (var line in _cursorLines)
            plot.Remove(line);
        _cursorLines.Clear();
        if (_cursorBand is not null)
        {
            plot.Remove(_cursorBand);
            _cursorBand = null;
        }
    }

    private void DrawCursors()
    {
        var plot = WpfPlot.Plot;
        RemoveCursorPlottables();

        // Shaded band between the two cursors makes the region unmistakable.
        var span = plot.Add.HorizontalSpan(Math.Min(_cursorX[0], _cursorX[1]), Math.Max(_cursorX[0], _cursorX[1]));
        span.FillColor = ScottPlot.Color.FromHex("2D7DD2").WithAlpha(28);
        span.LineColor = ScottPlot.Colors.Transparent;
        _cursorBand = span;

        for (var i = 0; i < 2; i++)
        {
            var vl = plot.Add.VerticalLine(_cursorX[i]);
            vl.Color = i == 0 ? CursorColorA : CursorColorB;
            vl.LineWidth = 2.5f;
            vl.LinePattern = ScottPlot.LinePattern.Dotted;
            vl.Text = i == 0 ? "A" : "B";
            vl.LabelStyle.Bold = true;
            vl.LabelStyle.FontSize = 14;
            _cursorLines.Add(vl);
        }
        UpdateCursorReadout();
    }

    private void UpdateCursorReadout()
    {
        var lo = Math.Min(_cursorX[0], _cursorX[1]);
        var hi = Math.Max(_cursorX[0], _cursorX[1]);
        var dx = _cursorX[1] - _cursorX[0];

        var rows = new List<CursorRow>();
        foreach (var (name, xs, ys, colorHex, _) in _rendered)
        {
            var (ia, okA) = NearestIndex(xs, _cursorX[0]);
            var (ib, okB) = NearestIndex(xs, _cursorX[1]);
            var va = okA ? ys[ia] : double.NaN;
            var vb = okB ? ys[ib] : double.NaN;
            var bothAB = double.IsFinite(va) && double.IsFinite(vb);

            var (mn, mx, mean, rms, std, integral, n) = RangeStats(xs, ys, lo, hi);
            var slope = bothAB && dx != 0 ? (vb - va) / dx : double.NaN;

            rows.Add(new CursorRow
            {
                Plot = name,
                ColorHex = colorHex,
                ValueA = Fmt(va),
                ValueB = Fmt(vb),
                Delta = bothAB ? Fmt(vb - va) : "\u2014",
                Min = n > 0 ? Fmt(mn) : "\u2014",
                Max = n > 0 ? Fmt(mx) : "\u2014",
                PeakToPeak = n > 0 ? Fmt(mx - mn) : "\u2014",
                Mean = n > 0 ? Fmt(mean) : "\u2014",
                Rms = n > 0 ? Fmt(rms) : "\u2014",
                StdDev = n > 0 ? Fmt(std) : "\u2014",
                Integral = n > 0 ? Fmt(integral) : "\u2014",
                Slope = double.IsFinite(slope) ? Fmt(slope) : "\u2014",
            });
        }
        BuildCursorReadout(rows);
        CursorHeader.Text = BuildCursorHeader();
    }

    // Builds the readout (header + rows) in code so only the enabled calculation columns appear.
    private void BuildCursorReadout(List<CursorRow> rows)
    {
        CursorReadout.Children.Clear();
        var cols = CursorCalcSettings.Columns.Where(c => _calcs.IsEnabled(c.Key)).ToList();

        CursorReadout.Children.Add(BuildReadoutRow(null, "Plot", cols, c => c.Header, isHeader: true));
        foreach (var r in rows)
            CursorReadout.Children.Add(BuildReadoutRow(r.ColorHex, r.Plot, cols, c => r.Get(c.Key), isHeader: false));
    }

    private UIElement BuildReadoutRow(
        string? colorHex, string name,
        List<(string Key, string Header, string Label)> cols,
        Func<(string Key, string Header, string Label), string> value, bool isHeader)
    {
        var muted = (Brush)FindResource("MutedBrush");
        var grid = new Grid { Margin = new Thickness(0, isHeader ? 0 : 1, 0, isHeader ? 3 : 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        foreach (var _ in cols)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        if (!isHeader && colorHex is not null)
        {
            var swatch = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(2),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Background = HexBrush(colorHex),
            };
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);
        }

        var nameBlock = new TextBlock
        {
            Text = name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name,
            Margin = new Thickness(4, 0, 0, 0),
        };
        if (isHeader) { nameBlock.FontWeight = FontWeights.SemiBold; nameBlock.Foreground = muted; }
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        for (var i = 0; i < cols.Count; i++)
        {
            var cell = new TextBlock
            {
                Text = value(cols[i]),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(4, 0, 0, 0),
            };
            if (isHeader) { cell.FontWeight = FontWeights.SemiBold; cell.Foreground = muted; }
            else if (cols[i].Key == "Delta") cell.Foreground = muted;
            Grid.SetColumn(cell, i + 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Brush HexBrush(string hex)
    {
        try
        {
            var s = hex.StartsWith('#') ? hex : "#" + hex;
            return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(s));
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    private void CursorConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CursorCalcWindow(_calcs) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _calcs.Save();
            if (_cursorsOn) UpdateCursorReadout();
        }
    }

    private void CopyPropertyValue_Click(object sender, RoutedEventArgs e) => CopyProperty(sender, r => r.Value);
    private void CopyPropertyName_Click(object sender, RoutedEventArgs e) => CopyProperty(sender, r => r.Name);
    private void CopyPropertyBoth_Click(object sender, RoutedEventArgs e) => CopyProperty(sender, r => $"{r.Name}: {r.Value}");

    private static void CopyProperty(object sender, Func<PropertyRow, string> pick)
    {
        if (sender is MenuItem { DataContext: PropertyRow row })
        {
            try { Clipboard.SetText(pick(row) ?? string.Empty); } catch { /* clipboard may be busy */ }
        }
    }

    // Builds the cursor readout table for a page's PDF, mirroring the on-screen readout.
    private PdfCursorReadout? BuildPrintCursorData(PageViewModel page)
    {
        if (!page.Model.CursorsOn || page.Model.CursorA is not double ca || page.Model.CursorB is not double cb)
            return null;

        var lo = Math.Min(ca, cb);
        var hi = Math.Max(ca, cb);
        var dx = cb - ca;
        var cols = CursorCalcSettings.Columns.Where(c => _calcs.IsEnabled(c.Key)).ToList();
        var rows = new List<PdfCursorRow>();
        var anyDateTime = false;

        foreach (var series in page.Series.Where(s => s.Visible))
        {
            double[] xs, ys;
            try
            {
                var data = _vm.GetSeriesData(series.Model);
                xs = data.X; ys = data.Y;
                anyDateTime |= data.XIsDateTime;
            }
            catch (Exception ex) { App.Log(ex); continue; }
            if (ys.Length == 0) continue;

            var (ia, okA) = NearestIndex(xs, ca);
            var (ib, okB) = NearestIndex(xs, cb);
            var va = okA ? ys[ia] : double.NaN;
            var vb = okB ? ys[ib] : double.NaN;
            var bothAB = double.IsFinite(va) && double.IsFinite(vb);
            var (mn, mx, mean, rms, std, integral, n) = RangeStats(xs, ys, lo, hi);
            var slope = bothAB && dx != 0 ? (vb - va) / dx : double.NaN;

            string V(string key) => key switch
            {
                "ValueA" => Fmt(va),
                "ValueB" => Fmt(vb),
                "Delta" => bothAB ? Fmt(vb - va) : "\u2014",
                "Min" => n > 0 ? Fmt(mn) : "\u2014",
                "Max" => n > 0 ? Fmt(mx) : "\u2014",
                "PeakToPeak" => n > 0 ? Fmt(mx - mn) : "\u2014",
                "Mean" => n > 0 ? Fmt(mean) : "\u2014",
                "Rms" => n > 0 ? Fmt(rms) : "\u2014",
                "StdDev" => n > 0 ? Fmt(std) : "\u2014",
                "Integral" => n > 0 ? Fmt(integral) : "\u2014",
                "Slope" => double.IsFinite(slope) ? Fmt(slope) : "\u2014",
                _ => string.Empty,
            };
            rows.Add(new PdfCursorRow(series.DisplayName, series.ColorHex, cols.Select(c => V(c.Key)).ToList()));
        }
        if (rows.Count == 0) return null;

        string X(double x) => anyDateTime ? DateTime.FromOADate(x).ToString("HH:mm:ss") : x.ToString("G6");
        var dtText = anyDateTime ? TimeSpan.FromDays(Math.Abs(dx)).ToString(@"hh\:mm\:ss") : Math.Abs(dx).ToString("G4");
        var header = $"A {X(ca)}    B {X(cb)}    \u0394t {dtText}";

        return new PdfCursorReadout(header, cols.Select(c => c.Header).ToList(), rows,
            page.Model.CursorX ?? 0.02, page.Model.CursorY ?? 0.02);
    }

    private string BuildCursorHeader()
    {
        string X(double x) => _renderedDateTime ? DateTime.FromOADate(x).ToString("HH:mm:ss") : x.ToString("G6");
        var dt = _cursorX[1] - _cursorX[0];
        var dtText = _renderedDateTime
            ? TimeSpan.FromDays(Math.Abs(dt)).ToString(@"hh\:mm\:ss")
            : Math.Abs(dt).ToString("G4");
        return $"A {X(_cursorX[0])}    B {X(_cursorX[1])}    \u0394t {dtText}";
    }

    private static string Fmt(double v) => double.IsFinite(v) ? v.ToString("G6") : "—";

    // Stats of finite Y over the sample range whose X falls between the cursors.
    private static (double Min, double Max, double Mean, double Rms, double Std, double Integral, int Count)
        RangeStats(double[] xs, double[] ys, double lo, double hi)
    {
        var start = LowerBound(xs, lo);
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0, sumSq = 0, integral = 0;
        var n = 0;
        var havePrev = false;
        double prevX = 0, prevY = 0;
        for (var i = start; i < xs.Length && xs[i] <= hi; i++)
        {
            var y = ys[i];
            var x = xs[i];
            if (!double.IsFinite(y)) { havePrev = false; continue; }
            if (y < min) min = y;
            if (y > max) max = y;
            sum += y;
            sumSq += y * y;
            n++;
            if (havePrev) integral += (x - prevX) * (y + prevY) / 2;
            prevX = x; prevY = y; havePrev = true;
        }
        if (n == 0)
            return (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        var mean = sum / n;
        var rms = Math.Sqrt(sumSq / n);
        var std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        return (min, max, mean, rms, std, integral, n);
    }

    private static int LowerBound(double[] xs, double value)
    {
        int lo = 0, hi = xs.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static (int Index, bool Ok) NearestIndex(double[] xs, double x)
    {
        if (xs.Length == 0) return (0, false);
        if (x <= xs[0]) return (0, true);
        if (x >= xs[^1]) return (xs.Length - 1, true);

        int lo = 0, hi = xs.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] < x) lo = mid + 1;
            else if (xs[mid] > x) hi = mid - 1;
            else return (mid, true);
        }
        var a = Math.Clamp(hi, 0, xs.Length - 1);
        var b = Math.Clamp(lo, 0, xs.Length - 1);
        return (Math.Abs(xs[a] - x) <= Math.Abs(xs[b] - x) ? (a, true) : (b, true));
    }

    private void ZoomInX_Click(object sender, RoutedEventArgs e) => ZoomX(0.7);
    private void ZoomOutX_Click(object sender, RoutedEventArgs e) => ZoomX(1.4);
    private void ZoomInY_Click(object sender, RoutedEventArgs e) => ZoomY(0.7);
    private void ZoomOutY_Click(object sender, RoutedEventArgs e) => ZoomY(1.4);

    private void ZoomX(double factor)
    {
        var l = WpfPlot.Plot.Axes.GetLimits();
        var center = (l.Left + l.Right) / 2;
        var half = (l.Right - l.Left) / 2 * factor;
        WpfPlot.Plot.Axes.SetLimitsX(center - half, center + half);
        WpfPlot.Refresh();
        SaveCurrentXRange();
    }

    private void ZoomY(double factor)
    {
        var l = WpfPlot.Plot.Axes.GetLimits();
        var center = (l.Bottom + l.Top) / 2;
        var half = (l.Top - l.Bottom) / 2 * factor;
        WpfPlot.Plot.Axes.SetLimitsY(center - half, center + half);
        WpfPlot.Refresh();
    }

    // --- Box zoom overlay ---

    private void BoxZoom_Checked(object sender, RoutedEventArgs e) => ZoomOverlay.IsHitTestVisible = true;
    private void BoxZoom_Unchecked(object sender, RoutedEventArgs e) => ZoomOverlay.IsHitTestVisible = false;

    private void ZoomOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _boxStart = e.GetPosition(ZoomOverlay);
        _boxDragging = true;
        Canvas.SetLeft(ZoomRect, _boxStart.X);
        Canvas.SetTop(ZoomRect, _boxStart.Y);
        ZoomRect.Width = 0;
        ZoomRect.Height = 0;
        ZoomRect.Visibility = Visibility.Visible;
        ZoomOverlay.CaptureMouse();
    }

    private void ZoomOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_boxDragging) return;
        var p = e.GetPosition(ZoomOverlay);
        Canvas.SetLeft(ZoomRect, Math.Min(p.X, _boxStart.X));
        Canvas.SetTop(ZoomRect, Math.Min(p.Y, _boxStart.Y));
        ZoomRect.Width = Math.Abs(p.X - _boxStart.X);
        ZoomRect.Height = Math.Abs(p.Y - _boxStart.Y);
    }

    private void ZoomOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_boxDragging) return;
        _boxDragging = false;
        ZoomOverlay.ReleaseMouseCapture();
        ZoomRect.Visibility = Visibility.Collapsed;

        var end = e.GetPosition(ZoomOverlay);
        if (Math.Abs(end.X - _boxStart.X) < 5 || Math.Abs(end.Y - _boxStart.Y) < 5)
            return;

        var dpi = VisualTreeHelper.GetDpi(WpfPlot);
        var c1 = WpfPlot.Plot.GetCoordinates(
            new Pixel((float)(_boxStart.X * dpi.DpiScaleX), (float)(_boxStart.Y * dpi.DpiScaleY)));
        var c2 = WpfPlot.Plot.GetCoordinates(
            new Pixel((float)(end.X * dpi.DpiScaleX), (float)(end.Y * dpi.DpiScaleY)));

        WpfPlot.Plot.Axes.SetLimits(
            Math.Min(c1.X, c2.X), Math.Max(c1.X, c2.X),
            Math.Min(c1.Y, c2.Y), Math.Max(c1.Y, c2.Y));
        WpfPlot.Refresh();
        SaveCurrentXRange();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, $"TDMS Viewer\nVersion {AppVersion}", "About TDMS Viewer",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(silent: false);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        var current = GetType().Assembly.GetName().Version ?? new System.Version(1, 0, 0);
        UpdateInfo? info;
        try
        {
            info = await _update.CheckAsync(current);
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(this, $"Couldn't check for updates.\n{ex.Message}", "Update",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (info is null)
        {
            if (!silent)
                MessageBox.Show(this, $"You're on the latest version ({AppVersion}).", "Update",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = MessageBox.Show(this,
            $"Version {info.Version} is available (you have {AppVersion}).\n\nDownload and install it now? The app will restart.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;

        try
        {
            _vm.IsBusy = true;
            _vm.BusyTitle = "Updating...";
            _vm.StatusText = "Downloading update...";
            var progress = new Progress<double>(p => _vm.StatusText = $"Downloading update... {p:P0}");
            var newExe = await _update.DownloadAsync(info.DownloadUrl, progress);
            _vm.StatusText = "Installing update...";
            _update.ApplyAndRestart(newExe);
        }
        catch (Exception ex)
        {
            _vm.IsBusy = false;
            MessageBox.Show(this,
                $"Update failed.\n{ex.Message}\n\nYou can download it manually from the releases page.",
                "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PrintPdf_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!_vm.IsFileLoaded)
        {
            MessageBox.Show(this, "Open a TDMS file and add a graph before printing.",
                "Print to PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Print the selected tree item: a report -> all its pages; a page -> just that page.
        ReportViewModel? report;
        List<PageViewModel> pages;
        switch (ReportsTree.SelectedItem)
        {
            case ReportViewModel r:
                report = r;
                pages = r.Pages.ToList();
                break;
            case PageViewModel p:
                report = _vm.Reports.FirstOrDefault(rr => rr.Pages.Contains(p));
                pages = new List<PageViewModel> { p };
                break;
            default:
                report = _vm.SelectedReport;
                pages = report?.Pages.ToList() ?? new List<PageViewModel>();
                break;
        }

        if (report is null || pages.Count == 0)
        {
            MessageBox.Show(this, "Select a report or a graph page in the Reports list to print.",
                "Print to PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = MakeSafeFileName(report.Title) + ".pdf",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var tdmsName = _vm.ActiveWorkspace is { } ws ? System.IO.Path.GetFileName(ws.Path) : string.Empty;
            new ReportPdfService().Build(report, pages, tdmsName, dialog.FileName, RenderPageImage, BuildPrintCursorData);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, $"Could not create the PDF.\n{ex.Message}",
                "Print to PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var safe = string.Join("_", (name ?? "Report").Split(System.IO.Path.GetInvalidFileNameChars()));
        return string.IsNullOrWhiteSpace(safe) ? "Report" : safe;
    }

    // Renders a page's plot (no cursors/interaction) to a PNG for printing.
    private byte[]? RenderPageImage(PageViewModel page, int width, int height)
    {
        try
        {
            var plot = new Plot();
            plot.Axes.Remove(ScottPlot.Edge.Left);
            plot.Axes.Remove(ScottPlot.Edge.Right);

            var anyDateTime = false;
            var xMin = double.PositiveInfinity;
            var xMax = double.NegativeInfinity;
            var axisExtents = new List<(IYAxis Axis, double Min, double Max, bool Has, AxisViewModel Scale)>();

            foreach (var scale in page.Axes)
            {
                var members = page.Series.Where(s => s.Visible && s.AxisId == scale.Id).ToList();
                if (members.Count == 0) continue;

                var axis = scale.Side == AxisSide.Left
                    ? (IYAxis)plot.Axes.AddLeftAxis()
                    : plot.Axes.AddRightAxis();

                var yMin = double.PositiveInfinity;
                var yMax = double.NegativeInfinity;
                var yHas = false;
                foreach (var series in members)
                {
                    try
                    {
                        var data = _vm.GetSeriesData(series.Model);
                        if (data.Y.Length == 0) continue;
                        AddSeries(plot, series, data, axis);
                        anyDateTime |= data.XIsDateTime;
                        var (xLo, xHi, xHas) = FiniteExtent(data.X);
                        if (xHas) { xMin = Math.Min(xMin, xLo); xMax = Math.Max(xMax, xHi); }
                        var (yLo, yHi, yFound) = FiniteExtent(data.Y);
                        if (yFound) { yMin = Math.Min(yMin, yLo); yMax = Math.Max(yMax, yHi); yHas = true; }
                    }
                    catch (Exception ex) { App.Log(ex); }
                }

                axis.Label.Text = scale.Name;
                if (members.Count == 1)
                {
                    var c = ScottPlot.Color.FromHex(members[0].ColorHex.TrimStart('#'));
                    axis.Label.ForeColor = c;
                    axis.TickLabelStyle.ForeColor = c;
                }
                axisExtents.Add((axis, yMin, yMax, yHas, scale));
            }

            if (!axisExtents.Any(a => a.Scale.Side == AxisSide.Left))
            {
                var placeholder = plot.Axes.AddLeftAxis();
                placeholder.IsVisible = axisExtents.Count == 0;
            }
            if (anyDateTime) plot.Axes.DateTimeTicksBottom();

            if (double.IsFinite(xMin) && double.IsFinite(xMax) && xMax >= xMin)
            {
                double xa, xb;
                if (page.Model.XMin is double smin && page.Model.XMax is double smax
                    && double.IsFinite(smin) && double.IsFinite(smax) && smax > smin)
                    (xa, xb) = (smin, smax);
                else
                    (xa, xb) = PadX(xMin, xMax);

                foreach (var (axis, min, max, has, scale) in axisExtents)
                {
                    double ya, yb;
                    if (scale is { Auto: false } && scale.Max > scale.Min) { ya = scale.Min; yb = scale.Max; }
                    else if (has) { (ya, yb) = PadY(min, max); }
                    else continue;
                    plot.Axes.SetLimits(new AxisLimits(xa, xb, ya, yb), plot.Axes.Bottom, axis);
                }
            }

            // Legend at the page's saved corner (bigger than the default).
            if (page.Series.Any(s => s.Visible))
            {
                plot.ShowLegend();
                plot.Legend.Alignment = PrintLegendAlignment(page);
                plot.Legend.FontSize = 16;
            }

            // Cursors, if the page has them on.
            if (page.Model.CursorsOn && page.Model.CursorA is double ca && page.Model.CursorB is double cb)
            {
                var band = plot.Add.HorizontalSpan(Math.Min(ca, cb), Math.Max(ca, cb));
                band.FillColor = ScottPlot.Color.FromHex("2D7DD2").WithAlpha(28);
                band.LineColor = ScottPlot.Colors.Transparent;
                foreach (var (cx, label, color) in new[] { (ca, "A", CursorColorA), (cb, "B", CursorColorB) })
                {
                    var vl = plot.Add.VerticalLine(cx);
                    vl.Color = color;
                    vl.LineWidth = 2.5f;
                    vl.LinePattern = ScottPlot.LinePattern.Dotted;
                    vl.Text = label;
                    vl.LabelStyle.Bold = true;
                    vl.LabelStyle.FontSize = 14;
                }
            }

            foreach (var (axis, _, _, _, _) in axisExtents) axis.TickLabelStyle.FontSize = PrintTickFontSize;
            plot.Axes.Bottom.TickLabelStyle.FontSize = PrintTickFontSize;

            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tvplot_{Guid.NewGuid():N}.png");
            plot.SavePng(tmp, width, height);
            var bytes = System.IO.File.ReadAllBytes(tmp);
            System.IO.File.Delete(tmp);
            return bytes;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }
}