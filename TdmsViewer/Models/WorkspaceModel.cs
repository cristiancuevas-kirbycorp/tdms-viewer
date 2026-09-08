namespace TdmsViewer.Models;

/// <summary>An open TDMS file shown as a tab. Its reports/config live in the file's sidecar.</summary>
public sealed class WorkspaceModel
{
    public string Name { get; set; } = string.Empty;
    public string TdmsPath { get; set; } = string.Empty;
}

/// <summary>Persisted set of open workspace tabs and which one was active.</summary>
public sealed class WorkspaceStore
{
    public List<WorkspaceModel> Workspaces { get; set; } = new();
    public int ActiveIndex { get; set; }

    // Window placement (restored/normal bounds; maximized state preserved separately so
    // we can remember "opened maximized on monitor X" without losing the pre-maximize size).
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }
}
