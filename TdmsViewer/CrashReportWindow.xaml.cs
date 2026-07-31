using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TdmsViewer;

/// <summary>Shown after a crash so the user can copy a full report to send to the developer.</summary>
public partial class CrashReportWindow : Window
{
    private readonly string _details;

    public CrashReportWindow(string details)
    {
        InitializeComponent();
        _details = details;
        ReportBox.Text = BuildReport();
    }

    private string BuildReport()
    {
        var desc = string.IsNullOrWhiteSpace(DescriptionBox?.Text) ? "(none provided)" : DescriptionBox.Text.Trim();
        return
            "TDMS Viewer problem report\n" +
            $"Version: {App.AppVersion}\n" +
            $"Time:    {DateTime.Now:u}\n" +
            $"OS:      {Environment.OSVersion}\n\n" +
            $"What I was doing:\n{desc}\n\n" +
            $"--- Error details ---\n{_details}";
    }

    private void DescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ReportBox is not null) ReportBox.Text = BuildReport();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportBox.Text);
            CopyButton.Content = "Copied!";
        }
        catch
        {
            // Clipboard can be briefly locked by another process; ignore.
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", App.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
