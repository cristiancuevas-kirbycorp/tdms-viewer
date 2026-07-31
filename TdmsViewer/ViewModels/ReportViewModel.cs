using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using TdmsViewer.Models;

namespace TdmsViewer.ViewModels;

public sealed partial class SeriesViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    private string _colorHex;

    [ObservableProperty]
    private double _lineWidth;

    [ObservableProperty]
    private SeriesLineStyle _lineStyle;

    [ObservableProperty]
    private SeriesMarker _marker;

    [ObservableProperty]
    private string _axisId;

    public PlotSeriesModel Model { get; }

    /// <summary>Raised when a styling property changes and the plot should refresh.</summary>
    public event EventHandler? Changed;

    public SeriesViewModel(PlotSeriesModel model)
    {
        Model = model;
        _visible = model.Visible;
        _colorHex = model.ColorHex;
        _lineWidth = model.LineWidth;
        _lineStyle = model.LineStyle;
        _marker = model.Marker;
        _axisId = model.AxisId;
    }

    public string DisplayName => Model.DisplayName;

    public static Array LineStyleOptions { get; } = Enum.GetValues(typeof(SeriesLineStyle));
    public static Array MarkerOptions { get; } = Enum.GetValues(typeof(SeriesMarker));

    partial void OnVisibleChanged(bool value)
    {
        Model.Visible = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnColorHexChanged(string value)
    {
        Model.ColorHex = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnLineWidthChanged(double value)
    {
        Model.LineWidth = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnLineStyleChanged(SeriesLineStyle value)
    {
        Model.LineStyle = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnMarkerChanged(SeriesMarker value)
    {
        Model.Marker = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAxisIdChanged(string value)
    {
        Model.AxisId = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A first-class Y-scale (name, side, auto/manual min-max).</summary>
public sealed partial class AxisViewModel : ObservableObject
{
    private bool _suppress;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private AxisSide _side;

    [ObservableProperty]
    private bool _auto;

    [ObservableProperty]
    private double _min;

    [ObservableProperty]
    private double _max;

    public AxisModel Model { get; }
    public string Id => Model.Id;

    public event EventHandler? Changed;

    public static Array SideOptions { get; } = Enum.GetValues(typeof(AxisSide));

    public AxisViewModel(AxisModel model)
    {
        Model = model;
        _name = model.Name;
        _side = model.Side;
        _auto = model.Auto;
        _min = model.Min;
        _max = model.Max;
    }

    /// <summary>Updates the displayed min/max from an auto-scale pass without flipping to manual.</summary>
    public void SetComputed(double min, double max)
    {
        _suppress = true;
        Min = min;
        Max = max;
        _suppress = false;
    }

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        if (!_suppress) Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSideChanged(AxisSide value)
    {
        Model.Side = value;
        if (!_suppress) Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAutoChanged(bool value)
    {
        Model.Auto = value;
        if (!_suppress) Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnMinChanged(double value)
    {
        Model.Min = value;
        if (_suppress) return;
        if (Auto) Auto = false; else Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnMaxChanged(double value)
    {
        Model.Max = value;
        if (_suppress) return;
        if (Auto) Auto = false; else Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    public PageModel Model { get; }
    public ObservableCollection<SeriesViewModel> Series { get; } = new();
    public ObservableCollection<AxisViewModel> Axes { get; } = new();

    /// <summary>Bubbles up when any series or axis changes so the view can re-render.</summary>
    public event EventHandler? SeriesChanged;

    public PageViewModel(PageModel model)
    {
        Model = model;
        _name = model.Name;

        foreach (var a in model.Axes)
            AttachAxis(new AxisViewModel(a));

        foreach (var s in model.Series)
        {
            var svm = new SeriesViewModel(s);
            EnsureAxis(svm);
            AttachSeries(svm);
        }

        Series.CollectionChanged += OnSeriesCollectionChanged;
    }

    /// <summary>Adds a series, creating its own new scale when it doesn't reference one.</summary>
    public void AddSeries(SeriesViewModel series)
    {
        EnsureAxis(series);
        AttachSeries(series);
    }

    public AxisViewModel AddAxis()
    {
        var m = new AxisModel
        {
            Name = $"Scale {Axes.Count + 1}",
            Side = Axes.Count % 2 == 0 ? AxisSide.Left : AxisSide.Right,
            Auto = true,
        };
        Model.Axes.Add(m);
        var vm = new AxisViewModel(m);
        AttachAxis(vm);
        SeriesChanged?.Invoke(this, EventArgs.Empty);
        return vm;
    }

    public void RemoveAxis(AxisViewModel axis)
    {
        if (Axes.Count <= 1) return;
        var fallback = Axes.First(a => !ReferenceEquals(a, axis));
        foreach (var s in Series.Where(s => s.AxisId == axis.Id))
            s.AxisId = fallback.Id;

        axis.Changed -= OnAnyChanged;
        Model.Axes.Remove(axis.Model);
        Axes.Remove(axis);
        SeriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureAxis(SeriesViewModel series)
    {
        if (Axes.Any(a => a.Id == series.AxisId)) return;

        var m = new AxisModel
        {
            Name = series.DisplayName,
            Side = Axes.Count % 2 == 0 ? AxisSide.Left : AxisSide.Right,
            Auto = true,
        };
        Model.Axes.Add(m);
        AttachAxis(new AxisViewModel(m));
        series.AxisId = m.Id;
    }

    private void AttachSeries(SeriesViewModel series)
    {
        series.Changed += OnAnyChanged;
        if (!Series.Contains(series))
            Series.Add(series);
    }

    private void AttachAxis(AxisViewModel axis)
    {
        axis.Changed += OnAnyChanged;
        if (!Axes.Contains(axis))
            Axes.Add(axis);
    }

    private void OnSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        SeriesChanged?.Invoke(this, EventArgs.Empty);

    private void OnAnyChanged(object? sender, EventArgs e) =>
        SeriesChanged?.Invoke(this, EventArgs.Empty);

    partial void OnNameChanged(string value) => Model.Name = value;
}

public sealed partial class ReportViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    public ReportModel Model { get; }
    public ObservableCollection<PageViewModel> Pages { get; } = new();

    public ReportViewModel(ReportModel model)
    {
        Model = model;
        _name = model.Name;
        foreach (var p in model.Pages)
            Pages.Add(new PageViewModel(p));
    }

    partial void OnNameChanged(string value) => Model.Name = value;
}
