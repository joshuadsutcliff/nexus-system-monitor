using System.Text;

namespace NexusMonitor.UI;

/// <summary>
/// Appends structured crash reports to %AppData%\NexusMonitor\crash.log.
/// All methods are non-throwing — if logging itself fails, the error is silently swallowed
/// so that crash-handler code never masks the original exception.
/// </summary>
internal static class CrashLogger
{
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusMonitor");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "crash.log");

    // Keep the file under ~200 KB; trim aggressively when exceeded
    private const long MaxLogBytes = 200 * 1024;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #42: on Linux/KDE, disposing the D-Bus StatusNotifierItem tray triggers a
    /// continuation (Tmds.DBus's connection teardown) that gets posted to the Avalonia
    /// dispatcher; if that continuation loses the race against dispatcher shutdown, it surfaces
    /// as a <see cref="TaskCanceledException"/> or <see cref="ObjectDisposedException"/> on a
    /// background thread, reaching <c>AppDomain.CurrentDomain.UnhandledException</c> and
    /// polluting crash.log with expected-during-teardown noise that isn't a real crash.
    /// </summary>
    /// <param name="ex">The exception under evaluation. Null-safe: returns false for null.</param>
    /// <param name="isShuttingDown">
    /// Whether the app has already committed to exiting (<see cref="App.IsShuttingDown"/>).
    /// </param>
    /// <returns>
    /// True only when ALL of: the app is shutting down; the exception (or, for an
    /// <see cref="AggregateException"/>, one of its inner exceptions) is a
    /// <see cref="TaskCanceledException"/> or <see cref="ObjectDisposedException"/>; and the
    /// stack trace of that exception contains "Tmds.DBus". Deliberately narrow — this must never
    /// mask a genuine crash, which is exactly what issue #42 complains about.
    /// </returns>
    public static bool ShouldSuppress(Exception? ex, bool isShuttingDown)
    {
        if (!isShuttingDown || ex is null) return false;
        return ContainsSuppressibleCause(ex);
    }

    /// <summary>
    /// Testable seam for <see cref="ShouldSuppress"/>: walks <paramref name="ex"/> (and, for an
    /// <see cref="AggregateException"/>, each inner exception) looking for a suppressible
    /// exception type whose own stack trace mentions "Tmds.DBus". Internal so unit tests can
    /// exercise the matching logic directly without needing to fabricate the outer
    /// <c>isShuttingDown</c>/AppDomain plumbing.
    /// </summary>
    internal static bool ContainsSuppressibleCause(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                if (ContainsSuppressibleCause(inner)) return true;
            }
            return false;
        }

        bool isSuppressibleType = ex is TaskCanceledException or ObjectDisposedException;
        if (!isSuppressibleType) return false;

        return ex.StackTrace is { } stack && stack.Contains("Tmds.DBus", StringComparison.Ordinal);
    }

    /// <summary>Appends a crash report for <paramref name="ex"/> to crash.log.</summary>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="context">
    /// Short label describing where the exception originated, e.g.
    /// "Startup", "AppDomain.UnhandledException", "UI Thread".
    /// </param>
    public static void Write(Exception ex, string context = "Runtime")
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            TrimIfNeeded();

            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"  Nexus Monitor — Crash Report");
            sb.AppendLine($"  Timestamp : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"  Context   : {context}");
            sb.AppendLine($"  OS        : {Environment.OSVersion}");
            sb.AppendLine($"  CLR       : {Environment.Version}");
            sb.AppendLine($"  Platform  : {(Environment.Is64BitProcess ? "x64" : "x86")}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            AppendException(sb, ex, depth: 0);
            sb.AppendLine();

            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // Never throw from a crash handler
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        string pad = new(' ', depth * 2);

        sb.AppendLine($"{pad}Type    : {ex.GetType().FullName}");
        sb.AppendLine($"{pad}Message : {ex.Message}");

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            sb.AppendLine($"{pad}Stack Trace:");
            foreach (var line in ex.StackTrace.Split('\n'))
            {
                string trimmed = line.TrimEnd();
                if (!string.IsNullOrEmpty(trimmed))
                    sb.AppendLine($"{pad}  {trimmed}");
            }
        }

        // AggregateException: unroll inner list first so each sub-exception is labelled
        if (ex is AggregateException agg)
        {
            for (int i = 0; i < agg.InnerExceptions.Count; i++)
            {
                sb.AppendLine($"{pad}--- AggregateException inner [{i}] ---");
                AppendException(sb, agg.InnerExceptions[i], depth + 1);
            }
        }
        else if (ex.InnerException is not null)
        {
            sb.AppendLine($"{pad}--- Inner Exception ---");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    /// <summary>
    /// If the log file exceeds <see cref="MaxLogBytes"/>, discards the older half so the
    /// file never grows unbounded while still keeping recent history.
    /// </summary>
    private static void TrimIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length < MaxLogBytes) return;

            string content = File.ReadAllText(LogPath);
            // Drop the first half — crude but avoids loading large files twice
            File.WriteAllText(LogPath, content[(content.Length / 2)..]);
        }
        catch { }
    }
}
