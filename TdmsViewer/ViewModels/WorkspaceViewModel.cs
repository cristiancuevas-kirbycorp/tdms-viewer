using CommunityToolkit.Mvvm.ComponentModel;
using TdmsViewer.Models;

namespace TdmsViewer.ViewModels;

/// <summary>A workspace tab: a named, renamable handle to an open TDMS file.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    /// <summary>True while the tab label is being renamed inline.</summary>
    [ObservableProperty]
    private bool _isEditing;

    public WorkspaceModel Model { get; }

    public string Path => Model.TdmsPath;

    // Cached loaded state so switching back to this tab is instant (no TDMS re-read).
    internal IReadOnlyList<TdmsChannelInfo>? Channels;
    internal ProjectModel? Project;
    internal List<ReportViewModel>? ReportVms;
    internal ReportViewModel? SelectedReport;
    internal PageViewModel? SelectedPage;
    internal bool Loaded => Channels is not null;

    public WorkspaceViewModel(WorkspaceModel model)
    {
        Model = model;
        _name = model.Name;
    }

    partial void OnNameChanged(string value) => Model.Name = value;
}
