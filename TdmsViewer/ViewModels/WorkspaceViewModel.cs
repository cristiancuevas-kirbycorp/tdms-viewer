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

    public WorkspaceViewModel(WorkspaceModel model)
    {
        Model = model;
        _name = model.Name;
    }

    partial void OnNameChanged(string value) => Model.Name = value;
}
