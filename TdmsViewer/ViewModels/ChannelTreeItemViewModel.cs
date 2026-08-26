using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TdmsViewer.Models;

namespace TdmsViewer.ViewModels;

/// <summary>A node in the "Available Channels" tree: either a group or a channel leaf.</summary>
public sealed partial class ChannelTreeItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isPicked;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded = true;

    public string Name { get; }
    public bool IsGroup { get; }
    public TdmsChannelInfo? Channel { get; }
    public string? Unit => Channel?.Unit;
    public ObservableCollection<ChannelTreeItemViewModel> Children { get; } = new();

    public ChannelTreeItemViewModel(string groupName)
    {
        Name = groupName;
        IsGroup = true;
    }

    public ChannelTreeItemViewModel(TdmsChannelInfo channel)
    {
        Channel = channel;
        Name = channel.Name;
        IsGroup = false;
    }
}
