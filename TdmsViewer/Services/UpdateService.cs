using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace TdmsViewer.Services;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string HtmlUrl);

public interface IUpdateService
{
    /// <summary>Returns update info when a newer stable release exists, otherwise null.</summary>
    Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default);

    /// <summary>Downloads the new exe next to the current one; returns its path.</summary>
    Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken ct = default);

    /// <summary>Swaps in the downloaded exe once this process exits, then relaunches.</summary>
    void ApplyAndRestart(string newExePath);
}

public sealed class UpdateService : IUpdateService
{
    private const string Owner = "cristiancuevas-kirbycorp";
    private const string Repo = "tdms-viewer";
    private const string AssetName = "TdmsViewer.exe";

    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TdmsViewer");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = ParseVersion(root.GetProperty("tag_name").GetString());
        if (version is null) return null;

        var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        if (downloadUrl is null) return null;

        var cur = Normalize(current);
        var latest = Normalize(version);
        return latest > cur ? new UpdateInfo(version, downloadUrl, htmlUrl) : null;
    }

    public async Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("Cannot locate the running executable.");
        var target = Path.Combine(exeDir, "TdmsViewer.update.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TdmsViewer");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        return target;
    }

    public void ApplyAndRestart(string newExePath)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the running executable.");
        var exeDir = Path.GetDirectoryName(exePath) ?? ".";
        var pid = Environment.ProcessId;

        // Waits for this process to exit, swaps in the new exe, relaunches it, and logs each step.
        var script = Path.Combine(Path.GetTempPath(), $"tdmsviewer_update_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, $"""
            @echo off
            cd /d "{exeDir}"
            set "LOG={exeDir}\update.log"
            echo [%date% %time%] update start pid={pid}>> "%LOG%"
            :waitloop
            tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
            if errorlevel 1 goto swap
            set /a w+=1
            if %w% GEQ 120 (echo [%date% %time%] timed out waiting>> "%LOG%" & goto done)
            ping -n 2 127.0.0.1 >nul
            goto waitloop
            :swap
            move /y "{newExePath}" "{exePath}" >nul 2>&1
            if not errorlevel 1 goto launch
            set /a m+=1
            if %m% GEQ 30 (echo [%date% %time%] swap failed>> "%LOG%" & goto done)
            ping -n 2 127.0.0.1 >nul
            goto swap
            :launch
            echo [%date% %time%] launching new version>> "%LOG%"
            start "" "{exePath}"
            :done
            del "%~f0"
            """);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        Application.Current.Shutdown();
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }
}
