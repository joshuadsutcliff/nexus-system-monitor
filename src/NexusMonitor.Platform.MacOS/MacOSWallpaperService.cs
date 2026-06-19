using System.Reactive.Linq;
using System.Reactive.Subjects;
using NexusMonitor.Core.Services;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// macOS wallpaper service: reads the current desktop picture via osascript,
/// polls for changes every 30 seconds.
/// </summary>
public sealed class MacOSWallpaperService : IWallpaperService, IDisposable
{
    private readonly Subject<WallpaperInfo> _subject   = new();
    private readonly System.Timers.Timer    _pollTimer;
    private          WallpaperInfo          _last;

    public IObservable<WallpaperInfo> WallpaperChanged => _subject.AsObservable();

    public MacOSWallpaperService()
    {
        _last = GetCurrentWallpaper();
        _pollTimer = new System.Timers.Timer(30_000) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => CheckForChange();
        _pollTimer.Start();
    }

    public WallpaperInfo GetCurrentWallpaper()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "osascript",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            };
            // Use ArgumentList, NOT Arguments: with UseShellExecute=false there is no
            // shell to strip quotes, so a single-quoted -e string would pass the literal
            // ' to osascript and fail to parse (error -2740). Each argv element is passed
            // verbatim here.
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Finder\" to get POSIX path of (desktop picture as text)");
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return WallpaperInfo.Default;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } }
            var output = outputTask.Result.Trim();

            if (!string.IsNullOrEmpty(output) && File.Exists(output))
                return WallpaperInfo.FromFile(output);
        }
        catch { /* fall through */ }

        return WallpaperInfo.Default;
    }

    private void CheckForChange()
    {
        var current = GetCurrentWallpaper();
        if (current.FilePath != _last.FilePath)
        {
            _last = current;
            _subject.OnNext(current);
        }
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        _subject.Dispose();
    }
}
