using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TdmsViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private bool _crashDialogOpen;

    /// <summary>Folder where logs live: %LocalAppData%\TdmsViewer.</summary>
    internal static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TdmsViewer");

    /// <summary>Short assembly version, e.g. "1.0.9".</summary>
    internal static string AppVersion { get; } = GetAppVersion();

    private static string GetAppVersion()
    {
        var v = typeof(App).Assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;

        // Avoid stacking a new dialog for every repeated render-time exception.
        if (_crashDialogOpen) return;
        _crashDialogOpen = true;
        try
        {
            var owner = Current?.MainWindow;
            var window = new CrashReportWindow(e.Exception.ToString());
            if (owner is { IsLoaded: true } && !ReferenceEquals(owner, window))
                window.Owner = owner;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Log(ex);
        }
        finally
        {
            _crashDialogOpen = false;
        }
    }

    /// <summary>Appends an exception to %LocalAppData%\TdmsViewer\error.log.</summary>
    internal static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, "error.log"), $"[{DateTime.Now:u}]\n{ex}\n\n");
        }
        catch
        {
            // logging must never throw
        }
    }
}

