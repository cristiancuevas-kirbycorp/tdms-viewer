using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TdmsViewer.Models;
using TdmsViewer.Services;

namespace TdmsViewer.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const string CalculationsGroup = "ReportCalculations";

    private static readonly string[] Palette =
    {
        "#1F77B4", "#FF00FF", "#2CA02C", "#D62728", "#9467BD",
        "#8C564B", "#17BECF", "#BCBD22", "#E377C2", "#7F7F7F",
    };

    private readonly ITdmsService _tdms;
    private readonly IFormulaService _formulas;
    private readonly IProjectService _projects;

    private ProjectModel _project = new();
    private int _colorIndex;

    /// <summary>Raised when the plot must redraw. The bool is true to refit the X (time) axis, false to keep it.</summary>
    public event EventHandler<bool>? PlotInvalidated;

    [ObservableProperty]
    private string _statusText = "No TDMS file loaded — use File ▸ Open TDMS to begin.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPageCommand))]
    private bool _isFileLoaded;

    /// <summary>The currently selected workspace tab.</summary>
    [ObservableProperty]
    private WorkspaceViewModel? _activeWorkspace;

    /// <summary>True when the selected tree node is a report (show report settings instead of the graph).</summary>
    [ObservableProperty]
    private bool _showReportSettings;

    // --- Center pane content selection (welcome / report settings / graph page) ---

    [ObservableProperty]
    private bool _welcomeVisible = true;

    [ObservableProperty]
    private bool _reportSettingsVisible;

    [ObservableProperty]
    private bool _graphPageVisible;

    partial void OnIsFileLoadedChanged(bool value) => UpdateCenterView();
    partial void OnShowReportSettingsChanged(bool value) => UpdateCenterView();

    // No file -> Welcome; report node -> settings; page node -> graph.
    private void UpdateCenterView()
    {
        WelcomeVisible = !IsFileLoaded;
        ReportSettingsVisible = IsFileLoaded && ShowReportSettings;
        GraphPageVisible = IsFileLoaded && !ShowReportSettings;
    }

    [ObservableProperty]
    private ReportViewModel? _selectedReport;

    [ObservableProperty]
    private PageViewModel? _selectedPage;

    [ObservableProperty]
    private string _formulaName = "MyChannel";

    [ObservableProperty]
    private string _formulaExpression = string.Empty;

    [ObservableProperty]
    private string? _channelToInsert;

    [ObservableProperty]
    private string _channelFilter = string.Empty;

    [ObservableProperty]
    private TdmsChannelInfo? _selectedChannelInfo;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyTitle = "Loading TDMS...";

    private IReadOnlyList<TdmsChannelInfo> _allChannels = Array.Empty<TdmsChannelInfo>();
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _autoSaveTimer;

    public ObservableCollection<ReportViewModel> Reports { get; } = new();
    public ObservableCollection<ChannelTreeItemViewModel> ChannelTree { get; } = new();

    /// <summary>Open TDMS files shown as tabs across the top.</summary>
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = new();

    /// <summary>Set while restoring tabs at startup so selecting a tab doesn't trigger a load.</summary>
    private bool _suppressWorkspaceActivation;

    /// <summary>Flat "Group / Channel" list used by the formula channel picker.</summary>
    public ObservableCollection<string> AllChannelPaths { get; } = new();

    /// <summary>Recently opened TDMS files (most recent first).</summary>
    public ObservableCollection<RecentFile> RecentFiles { get; } = new();

    /// <summary>Properties of the channel currently selected in the tree.</summary>
    public ObservableCollection<PropertyRow> SelectedProperties { get; } = new();

    /// <summary>Data of the currently selected channel, for the preview graph.</summary>
    public ChannelData? PreviewData { get; private set; }

    /// <summary>Raised when the preview graph should be redrawn.</summary>
    public event EventHandler? PreviewInvalidated;

    public MainViewModel(ITdmsService tdms, IFormulaService formulas, IProjectService projects)
    {
        _tdms = tdms;
        _formulas = formulas;
        _projects = projects;

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            BuildChannelTree(_allChannels);
        };

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            PersistConfig();
        };

        var report = new ReportViewModel(new ReportModel());
        report.Pages.Add(new PageViewModel(new PageModel()));
        _project.Reports.Add(report.Model);
        Reports.Add(report);
        SelectedReport = report;
        SelectedPage = report.Pages[0];
        report.Pages[0].IsSelected = true;

        LoadRecent();
        LoadWorkspaces();
    }

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.SeriesChanged -= OnPageSeriesChanged;
        if (newValue is not null)
            newValue.SeriesChanged += OnPageSeriesChanged;
        PlotInvalidated?.Invoke(this, true);
        SyncChannelChecks();
    }

    private void OnPageSeriesChanged(object? sender, EventArgs e)
    {
        SyncChannelChecks();
        PlotInvalidated?.Invoke(this, false);
    }

    private bool _syncingChecks;

    // A ticked channel is drawn on the active page; unticking removes it.
    private void OnLeafChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingChecks || e.PropertyName != nameof(ChannelTreeItemViewModel.IsSelected)) return;
        if (sender is not ChannelTreeItemViewModel { Channel: { } info } leaf || SelectedPage is null) return;

        var wasEmpty = SelectedPage.Series.Count == 0;
        var existing = SelectedPage.Series.FirstOrDefault(s => SeriesMatches(s, info));
        if (leaf.IsSelected)
        {
            if (existing is null) AddSeriesFor(info);
        }
        else if (existing is not null)
        {
            SelectedPage.Series.Remove(existing);
            SelectedPage.Model.Series.Remove(existing.Model);
        }
        RaisePlotAfterAdd(wasEmpty && leaf.IsSelected);
    }

    // First plot on a page auto-fits X and Y; later plots keep the current time view.
    private void RaisePlotAfterAdd(bool fitFirst)
    {
        if (fitFirst && SelectedPage is not null)
        {
            SelectedPage.Model.XMin = null;
            SelectedPage.Model.XMax = null;
        }
        PlotInvalidated?.Invoke(this, fitFirst);
    }

    private static bool SeriesMatches(SeriesViewModel s, TdmsChannelInfo info) =>
        s.Model.Group == info.Group && s.Model.Channel == info.Name;

    private void SyncChannelChecks()
    {
        if (SelectedPage is null) return;
        _syncingChecks = true;
        foreach (var leaf in ChannelTree.SelectMany(g => g.Children).Where(c => c.Channel is not null))
            leaf.IsSelected = SelectedPage.Series.Any(s => SeriesMatches(s, leaf.Channel!));
        _syncingChecks = false;
    }

    [RelayCommand]
    private void OpenTdms()
    {
        var dialog = new OpenFileDialog { Filter = "TDMS files (*.tdms)|*.tdms|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        AddOrActivateWorkspace(dialog.FileName);
    }

    [RelayCommand]
    private void OpenRecent(RecentFile? file)
    {
        if (file is null) return;
        if (!File.Exists(file.Path))
        {
            StatusText = $"File not found: {file.Path}";
            RecentFiles.Remove(file);
            SaveRecent();
            return;
        }
        AddOrActivateWorkspace(file.Path);
    }

    // --- Workspaces (tabs) ---

    /// <summary>Opens the file in an existing tab if already open, otherwise adds a new tab and activates it.</summary>
    private void AddOrActivateWorkspace(string path)
    {
        var existing = Workspaces.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveWorkspace = existing;
            return;
        }

        var ws = new WorkspaceViewModel(new WorkspaceModel { Name = Path.GetFileName(path), TdmsPath = path });
        Workspaces.Add(ws);
        ActiveWorkspace = ws;
    }

    [RelayCommand]
    private void CloseWorkspace(WorkspaceViewModel? ws)
    {
        if (ws is null) return;
        var wasActive = ReferenceEquals(ws, ActiveWorkspace);
        var idx = Workspaces.IndexOf(ws);
        if (wasActive) PersistConfig();
        Workspaces.Remove(ws);
        SaveWorkspaces();
        if (wasActive)
            ActiveWorkspace = Workspaces.Count == 0 ? null : Workspaces[Math.Min(idx, Workspaces.Count - 1)];
    }

    partial void OnActiveWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        SaveWorkspaces();
        if (_suppressWorkspaceActivation) return;

        // Save the outgoing tab's live state so we can restore it instantly later.
        if (oldValue is not null && oldValue.Loaded)
        {
            PersistConfig();
            SaveActiveContext(oldValue);
        }

        if (newValue is null)
            ClearToEmpty();
        else if (newValue.Loaded)
            RestoreContext(newValue);       // instant: no TDMS re-read
        else
            _ = LoadWorkspaceAsync(newValue); // first activation: load once, then cache
    }

    /// <summary>Loads the active tab's file at startup (called once the window is ready).</summary>
    public async Task RestoreActiveWorkspaceAsync()
    {
        if (ActiveWorkspace is { } ws && File.Exists(ws.Path))
            await LoadWorkspaceAsync(ws);
    }

    // Snapshots the current live state into a workspace for instant restore.
    private void SaveActiveContext(WorkspaceViewModel ws)
    {
        ws.Channels = _allChannels;
        ws.Project = _project;
        ws.ReportVms = Reports.ToList();
        ws.SelectedReport = SelectedReport;
        ws.SelectedPage = SelectedPage;
    }

    // Restores a previously-loaded workspace without touching the TDMS metadata again.
    private void RestoreContext(WorkspaceViewModel ws)
    {
        _project = ws.Project!;
        _allChannels = ws.Channels!;
        _tdms.SetCurrentPath(ws.Path);

        Reports.Clear();
        foreach (var r in ws.ReportVms!) Reports.Add(r);
        SelectedReport = ws.SelectedReport;
        SelectedPage = ws.SelectedPage;

        BuildChannelTree(_allChannels);
        IsFileLoaded = true;
        ShowReportSettings = false;
        StatusText = $"{Path.GetFileName(ws.Path)} — {_allChannels.Count} channels.";
        PlotInvalidated?.Invoke(this, true);
    }

    private async Task LoadWorkspaceAsync(WorkspaceViewModel ws)
    {
        ResetToDefaultProject();     // fresh report if the file has no saved config
        await OpenPathAsync(ws.Path);
        if (IsFileLoaded)
            SaveActiveContext(ws);   // cache so future switches are instant
    }

    // Fresh single-report project used when a newly opened file has no sidecar config.
    private void ResetToDefaultProject()
    {
        _project = new ProjectModel();
        var report = new ReportViewModel(new ReportModel());
        report.Pages.Add(new PageViewModel(new PageModel()));
        _project.Reports.Add(report.Model);
        Reports.Clear();
        Reports.Add(report);
        SelectedReport = report;
        SelectedPage = report.Pages[0];
        report.Pages[0].IsSelected = true;
    }

    // Resets to the no-file state (welcome screen) with a fresh default report.
    private void ClearToEmpty()
    {
        PersistConfig();
        ResetToDefaultProject();
        _allChannels = Array.Empty<TdmsChannelInfo>();
        ChannelTree.Clear();
        AllChannelPaths.Clear();
        SelectedProperties.Clear();
        PreviewData = null;
        ShowReportSettings = false;
        IsFileLoaded = false;
        StatusText = "No TDMS file loaded — use File ▸ Open TDMS to begin.";
        PlotInvalidated?.Invoke(this, true);
    }

    private async Task OpenPathAsync(string path)
    {
        var progress = new Progress<string>(m => StatusText = m);
        BusyTitle = "Loading TDMS...";
        IsBusy = true;
        try
        {
            var channels = await Task.Run(() => _tdms.Open(path, defragment: true, progress));
            _allChannels = channels;
            LoadSidecar(path);
            _project.TdmsPath = path;
            BuildChannelTree(channels);
            AddRecent(path);
            IsFileLoaded = true;
            PlotInvalidated?.Invoke(this, true);
            StatusText = $"Loaded {channels.Count} channels from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Sidecar config (hidden file next to the TDMS holding this app's report/formula config) ---

    private static string SidecarPath(string tdmsPath) => tdmsPath + ".tvcfg";

    private void LoadSidecar(string tdmsPath)
    {
        var side = SidecarPath(tdmsPath);
        if (!File.Exists(side)) return;
        try
        {
            _project = _projects.Load(side);
            Reports.Clear();
            foreach (var r in _project.Reports)
                Reports.Add(new ReportViewModel(r));
            if (Reports.Count == 0)
            {
                var report = new ReportViewModel(new ReportModel());
                report.Pages.Add(new PageViewModel(new PageModel()));
                _project.Reports.Add(report.Model);
                Reports.Add(report);
            }
            SelectedReport = Reports.FirstOrDefault();
            SelectedPage = SelectedReport?.Pages.FirstOrDefault();
            if (SelectedReport is not null) SelectedReport.IsExpanded = true;
            if (SelectedPage is not null) SelectedPage.IsSelected = true;
            ShowReportSettings = false;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Writes the current config to the hidden sidecar file beside the open TDMS.</summary>
    public void PersistConfig()
    {
        _autoSaveTimer.Stop();
        if (string.IsNullOrEmpty(_project.TdmsPath)) return;
        try
        {
            var side = SidecarPath(_project.TdmsPath);
            if (File.Exists(side)) File.SetAttributes(side, FileAttributes.Normal);
            _projects.Save(_project, side);
            File.SetAttributes(side, FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Requests a debounced auto-save (~1.5s after the last change).</summary>
    public void ScheduleAutoSave()
    {
        if (string.IsNullOrEmpty(_project.TdmsPath)) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>Add Report / Add Page are only meaningful once a TDMS is open.</summary>
    private bool CanEditReports() => IsFileLoaded;

    // --- Recent files ---

    private static string RecentStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TdmsViewer", "recent.json");

    private void AddRecent(string path)
    {
        var existing = RecentFiles.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentFiles.Remove(existing);
        RecentFiles.Insert(0, new RecentFile { Path = path, OpenedUtc = DateTime.UtcNow });
        while (RecentFiles.Count > 10) RecentFiles.RemoveAt(RecentFiles.Count - 1);
        SaveRecent();
    }

    private void LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentStorePath)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<RecentFile>>(File.ReadAllText(RecentStorePath));
            if (list is null) return;
            foreach (var r in list)
                RecentFiles.Add(r);
        }
        catch { /* ignore a corrupt/missing recent list */ }
    }

    private void SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentStorePath)!);
            File.WriteAllText(RecentStorePath, System.Text.Json.JsonSerializer.Serialize(RecentFiles.ToList()));
        }
        catch { /* non-fatal */ }
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        SaveRecent();
    }

    // --- Workspace persistence ---

    private static string WorkspaceStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TdmsViewer", "workspaces.json");

    public void SaveWorkspaces()
    {
        try
        {
            var store = new WorkspaceStore
            {
                Workspaces = Workspaces.Select(w => w.Model).ToList(),
                ActiveIndex = ActiveWorkspace is null ? -1 : Workspaces.IndexOf(ActiveWorkspace),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceStorePath)!);
            File.WriteAllText(WorkspaceStorePath, System.Text.Json.JsonSerializer.Serialize(store));
        }
        catch { /* non-fatal */ }
    }

    private void LoadWorkspaces()
    {
        try
        {
            if (!File.Exists(WorkspaceStorePath)) return;
            var store = System.Text.Json.JsonSerializer.Deserialize<WorkspaceStore>(File.ReadAllText(WorkspaceStorePath));
            if (store is null || store.Workspaces.Count == 0) return;

            _suppressWorkspaceActivation = true;
            foreach (var m in store.Workspaces)
                Workspaces.Add(new WorkspaceViewModel(m));
            var idx = store.ActiveIndex >= 0 && store.ActiveIndex < Workspaces.Count ? store.ActiveIndex : 0;
            ActiveWorkspace = Workspaces[idx];
            _suppressWorkspaceActivation = false;
        }
        catch
        {
            _suppressWorkspaceActivation = false;
        }
    }

    private void BuildChannelTree(IReadOnlyList<TdmsChannelInfo> channels)
    {
        ChannelTree.Clear();
        AllChannelPaths.Clear();

        var tokens = (ChannelFilter ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Every token must appear somewhere in "group + channel", so tokens can span group and channel names.
        bool Matches(string group, string name)
        {
            if (tokens.Length == 0) return true;
            var combined = $"{group} {name}";
            foreach (var t in tokens)
                if (!combined.Contains(t, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        var calc = new ChannelTreeItemViewModel(CalculationsGroup) { IsExpanded = tokens.Length > 0 };
        foreach (var f in _project.Formulas.Where(f => Matches(CalculationsGroup, f.Name)))
        {
            var leaf = new ChannelTreeItemViewModel(new TdmsChannelInfo { Group = CalculationsGroup, Name = f.Name });
            leaf.PropertyChanged += OnLeafChanged;
            calc.Children.Add(leaf);
        }
        if (calc.Children.Count > 0 || tokens.Length == 0)
            ChannelTree.Add(calc);

        foreach (var group in channels.GroupBy(c => c.Group).OrderBy(g => g.Key))
        {
            var node = new ChannelTreeItemViewModel(group.Key) { IsExpanded = tokens.Length > 0 };
            foreach (var ch in group.Where(c => Matches(group.Key, c.Name)).OrderBy(c => c.Name))
            {
                var leaf = new ChannelTreeItemViewModel(ch);
                leaf.PropertyChanged += OnLeafChanged;
                node.Children.Add(leaf);
                AllChannelPaths.Add($"{ch.Group}/{ch.Name}");
            }
            if (node.Children.Count > 0)
                ChannelTree.Add(node);
        }
        ChannelToInsert = AllChannelPaths.FirstOrDefault();
        SyncChannelChecks();
    }

    // Rebuild the tree only after the user pauses typing, so filtering stays responsive.
    partial void OnChannelFilterChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    [RelayCommand]
    private void ExpandAll() => SetAllExpanded(true);

    [RelayCommand]
    private void CollapseAll() => SetAllExpanded(false);

    private void SetAllExpanded(bool expanded)
    {
        foreach (var group in ChannelTree)
            group.IsExpanded = expanded;
    }

    private CancellationTokenSource? _previewCts;

    /// <summary>Populates the properties table and preview data for the selected channel.</summary>
    public async Task SelectChannelAsync(TdmsChannelInfo info)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var token = cts.Token;

        SelectedChannelInfo = info;
        SelectedProperties.Clear();

        void Add(string category, string name, string value) =>
            SelectedProperties.Add(new PropertyRow { Category = category, Name = name, Value = value });

        Add("Base Properties", "Name", info.Name);
        Add("Base Properties", "Group", info.Group);
        Add("Base Properties", "Description", info.Description ?? string.Empty);
        Add("Base Properties", "Unit", info.Unit ?? string.Empty);
        Add("Base Properties", "Data type", info.DataType);
        Add("Base Properties", "Length", info.Count.ToString("N0"));

        foreach (var kv in info.Properties.Where(p => !HiddenProps.Contains(p.Key)).OrderBy(p => p.Key))
            Add("Custom Properties", kv.Key, kv.Value);

        foreach (var kv in info.GroupProperties.OrderBy(p => p.Key))
            Add("Group Properties", kv.Key, kv.Value);

        foreach (var kv in info.RootProperties.OrderBy(p => p.Key))
            Add("File Properties", kv.Key, kv.Value);

        PreviewData = null;
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        StatusText = $"Loading '{info.Name}'...";

        ChannelData data;
        try
        {
            data = await Task.Run(() => GetChannelData(info), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"Preview failed: {ex.Message}";
            return;
        }

        if (token.IsCancellationRequested || !ReferenceEquals(cts, _previewCts))
            return;

        PreviewData = data;
        if (data.Y.Length > 0)
        {
            var finite = data.Y.Where(double.IsFinite).ToArray();
            if (finite.Length > 0)
            {
                Add("Base Properties", "Minimum", finite.Min().ToString("G6"));
                Add("Base Properties", "Maximum", finite.Max().ToString("G6"));
            }
        }

        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        StatusText = $"{info.Name}: {info.Count:N0} samples.";
    }

    private static readonly HashSet<string> HiddenProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "unit_string", "description", "NI_ChannelDescription",
    };

    /// <summary>Loads channel data, evaluating formulas for calculated channels.</summary>
    private ChannelData GetChannelData(TdmsChannelInfo info)
    {
        if (info.Group == CalculationsGroup)
        {
            var formula = _project.Formulas.FirstOrDefault(f => f.Name == info.Name)
                ?? throw new KeyNotFoundException($"Formula '{info.Name}' not found.");
            return _formulas.Evaluate(formula.Expression, (g, c) => _tdms.LoadChannel(g, c));
        }
        return _tdms.LoadChannel(info.Group, info.Name);
    }

    [RelayCommand]
    private void AddSelected()
    {
        if (SelectedPage is null) return;

        var wasEmpty = SelectedPage.Series.Count == 0;
        foreach (var leaf in ChannelTree.SelectMany(g => g.Children).Where(c => c.IsSelected && c.Channel is not null))
        {
            AddSeriesFor(leaf.Channel!);
            leaf.IsSelected = false;
        }
        RaisePlotAfterAdd(wasEmpty);
    }

    /// <summary>Adds a single channel to the active page (used by double-click).</summary>
    public void AddChannel(TdmsChannelInfo info)
    {
        if (SelectedPage is null) return;
        var wasEmpty = SelectedPage.Series.Count == 0;
        AddSeriesFor(info);
        RaisePlotAfterAdd(wasEmpty);
    }

    private void AddSeriesFor(TdmsChannelInfo info)
    {
        var isFormula = info.Group == CalculationsGroup;
        var model = new PlotSeriesModel
        {
            Group = info.Group,
            Channel = info.Name,
            Formula = isFormula ? _project.Formulas.FirstOrDefault(f => f.Name == info.Name)?.Expression : null,
            ColorHex = NextColor(),
        };
        SelectedPage!.Model.Series.Add(model);
        SelectedPage.AddSeries(new SeriesViewModel(model));
    }

    [RelayCommand]
    private void InsertChannelReference()
    {
        if (string.IsNullOrWhiteSpace(ChannelToInsert)) return;
        var token = $"[{ChannelToInsert}]";
        FormulaExpression = string.IsNullOrWhiteSpace(FormulaExpression)
            ? token
            : $"{FormulaExpression} {token}";
    }

    [RelayCommand(CanExecute = nameof(CanEditReports))]
    private void AddReport()
    {
        var report = new ReportViewModel(new ReportModel { Name = $"Report {Reports.Count + 1}" });
        report.Pages.Add(new PageViewModel(new PageModel()));
        _project.Reports.Add(report.Model);
        Reports.Add(report);
        SelectedReport = report;
        SelectedPage = report.Pages[0];
        report.Pages[0].IsSelected = true;
        ShowReportSettings = false;
        ScheduleAutoSave();
    }

    [RelayCommand(CanExecute = nameof(CanEditReports))]
    private void AddPage()
    {
        if (SelectedReport is null) return;
        var page = new PageViewModel(new PageModel { Name = $"Graph {SelectedReport.Pages.Count + 1}" });
        SelectedReport.Model.Pages.Add(page.Model);
        SelectedReport.Pages.Add(page);
        SelectedPage = page;
        page.IsSelected = true;
        ShowReportSettings = false;
        ScheduleAutoSave();
    }

    [RelayCommand]
    private void DeleteSelectedSeries(SeriesViewModel? series)
    {
        if (SelectedPage is null || series is null) return;
        SelectedPage.Series.Remove(series);
        SelectedPage.Model.Series.Remove(series.Model);
        PlotInvalidated?.Invoke(this, false);
    }

    [RelayCommand]
    private void PickColor(SeriesViewModel? series)
    {
        if (series is null) return;
        ColorPickRequested?.Invoke(series, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddAxis()
    {
        SelectedPage?.AddAxis();
        PlotInvalidated?.Invoke(this, false);
    }

    [RelayCommand]
    private void RemoveAxis(AxisViewModel? axis)
    {
        if (SelectedPage is null || axis is null) return;
        SelectedPage.RemoveAxis(axis);
        PlotInvalidated?.Invoke(this, false);
    }

    /// <summary>Raised so the view can show a color dialog for the given series.</summary>
    public event EventHandler? ColorPickRequested;

    [RelayCommand]
    private void AddFormula()
    {
        if (string.IsNullOrWhiteSpace(FormulaName) || string.IsNullOrWhiteSpace(FormulaExpression)) return;

        var existing = _project.Formulas.FirstOrDefault(f => f.Name == FormulaName);
        if (existing is null)
            _project.Formulas.Add(new FormulaModel { Name = FormulaName, Expression = FormulaExpression });
        else
            existing.Expression = FormulaExpression;

        var calc = ChannelTree.FirstOrDefault(g => g.Name == CalculationsGroup);
        if (calc is null)
        {
            calc = new ChannelTreeItemViewModel(CalculationsGroup);
            ChannelTree.Insert(0, calc);
        }
        if (calc.Children.All(c => c.Name != FormulaName))
            calc.Children.Add(new ChannelTreeItemViewModel(new TdmsChannelInfo { Group = CalculationsGroup, Name = FormulaName }));

        StatusText = $"Formula '{FormulaName}' saved.";
    }

    [RelayCommand]
    private void SaveProject()
    {
        var dialog = new SaveFileDialog { Filter = "TDMS Viewer project (*.tvproj)|*.tvproj", DefaultExt = ".tvproj" };
        if (dialog.ShowDialog() != true) return;
        _projects.Save(_project, dialog.FileName);
        StatusText = $"Saved project to {Path.GetFileName(dialog.FileName)}.";
    }

    [RelayCommand]
    private async Task LoadProject()
    {
        var dialog = new OpenFileDialog { Filter = "TDMS Viewer project (*.tvproj)|*.tvproj|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;

        _project = _projects.Load(dialog.FileName);

        Reports.Clear();
        foreach (var r in _project.Reports)
            Reports.Add(new ReportViewModel(r));
        SelectedReport = Reports.FirstOrDefault();
        SelectedPage = SelectedReport?.Pages.FirstOrDefault();
        if (SelectedReport is not null) SelectedReport.IsExpanded = true;
        if (SelectedPage is not null) SelectedPage.IsSelected = true;
        ShowReportSettings = false;

        if (!string.IsNullOrEmpty(_project.TdmsPath) && File.Exists(_project.TdmsPath))
        {
            var path = _project.TdmsPath;
            var progress = new Progress<string>(m => StatusText = m);
            BusyTitle = "Loading TDMS...";
            IsBusy = true;
            try
            {
                var channels = await Task.Run(() => _tdms.Open(path, defragment: true, progress));
                _allChannels = channels;
                BuildChannelTree(channels);
                AddRecent(path);
                IsFileLoaded = true;
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to open TDMS: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }
        PlotInvalidated?.Invoke(this, true);
        StatusText = $"Loaded project {Path.GetFileName(dialog.FileName)}.";
    }

    /// <summary>Resolves plot data for a series, evaluating formulas as needed.</summary>
    public ChannelData GetSeriesData(PlotSeriesModel series)
    {
        if (series.IsFormula)
            return _formulas.Evaluate(series.Formula!, (g, c) => _tdms.LoadChannel(g, c));
        return _tdms.LoadChannel(series.Group, series.Channel);
    }

    private string NextColor() => Palette[_colorIndex++ % Palette.Length];
}
