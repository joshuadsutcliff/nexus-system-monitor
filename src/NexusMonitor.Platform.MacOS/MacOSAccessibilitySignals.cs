using System.Diagnostics;
using System.Threading.Tasks;
using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// macOS implementation of <see cref="IAccessibilitySignals"/>.
///
/// <para><b>Route chosen:</b> shells out to the public <c>defaults read</c> CLI against the
/// <c>com.apple.universalaccess</c> preferences domain — the same domain System Settings &gt;
/// Accessibility &gt; Display's "Reduce motion"/"Reduce transparency" toggles write to (keys
/// <c>reduceMotion</c>/<c>reduceTransparency</c>) — rather than reading
/// <c>NSWorkspace.accessibilityDisplayShouldReduceMotion</c>/<c>ShouldReduceTransparency</c> via
/// the Objective-C runtime. Two reasons, both already established precedent elsewhere in this
/// codebase: (1) <c>NexusMonitor.Platform.MacOS.csproj</c> deliberately ships on the plain net8.0
/// TFM with NO Microsoft.macOS/ObjC-runtime reference — that runtime SIGBUSes at startup on
/// macOS 26 (Tahoe) / Apple Silicon (see that file's own comment); (2) CFStringRef P/Invoke
/// marshalling is a known landmine this codebase has already routed around once before (see
/// <see cref="MacOSSleepPreventionProvider"/>'s doc comment on <c>IOPMAssertionCreateWithName</c>)
/// by using a subprocess instead of raw P/Invoke. <c>defaults read</c> applies that same
/// subprocess-based interop pattern to this OS-preference read — an "equivalently reliable
/// public read" per the task brief, not the Objective-C runtime route.</para>
///
/// <para><b>Read ONCE at construction, not on every property access</b> (contrast
/// <c>WindowsAccessibilitySignals</c>, which reads live on every access because a P/Invoke +
/// registry read is cheap): each <c>defaults read</c> call here spawns a real subprocess, which
/// is too expensive to run from what may be a hot path (these properties are consulted from UI
/// code that can run on every hover/animation tick). This makes the macOS signal
/// RESTART-SCOPED — if the user flips Reduce Motion/Transparency in System Settings while Nexus
/// is running, the app won't notice until its next launch. Per the task brief: "Read at startup
/// minimum; live change-listening only if a cheap per-platform hook exists (do not build polling
/// infrastructure)" — no cheap hook exists for this domain without either the Objective-C
/// runtime (<c>NSDistributedNotificationCenter</c>) or a polling loop, both out of scope here, so
/// startup-only is the honest, in-scope choice.</para>
/// </summary>
public sealed class MacOSAccessibilitySignals : IAccessibilitySignals
{
    public bool ReduceMotion { get; }
    public bool ReduceTransparency { get; }

    public MacOSAccessibilitySignals()
    {
        ReduceMotion       = ReadBoolPreference("reduceMotion");
        ReduceTransparency = ReadBoolPreference("reduceTransparency");
    }

    /// <summary>
    /// Runs <c>defaults read com.apple.universalaccess &lt;key&gt;</c> and parses the boolean
    /// result via <see cref="ParseBoolPreferenceOutput"/>. Guarded: any failure (command not
    /// found, non-zero exit — including "key not set", which is the common case on a machine
    /// that has never touched the toggle — unparsable output, or a hung child process) degrades
    /// to <see langword="false"/>. Reads degrade, never throw — internal (not private) so
    /// <c>MacOSAccessibilitySignalsIntegrationTests</c> (gated to real macOS hosts via
    /// <c>InternalsVisibleTo</c>) can exercise the real subprocess path directly.
    ///
    /// <para><b>Gate-review fix (2026-07-11):</b> the original version called
    /// <c>proc.StandardOutput.ReadToEnd()</c> (blocking) before <c>WaitForExit</c>, with stderr
    /// redirected but never drained — the classic .NET <see cref="Process"/>-redirect deadlock
    /// precondition: if the child ever writes enough to stderr to fill its OS pipe buffer while
    /// this method blocks reading stdout, both sides stall forever. This runs at DI-singleton
    /// construction time (effectively app startup), so a hang here would hang the whole app.
    /// Fixed with the standard non-deadlocking pattern: start async reads on BOTH redirected
    /// streams before synchronously waiting for exit (so neither pipe can back up and block the
    /// child while we wait), bound that wait with a hard timeout, and kill-on-timeout so a wedged
    /// <c>defaults</c> process can never hang the caller.</para>
    /// </summary>
    internal static bool ReadBoolPreference(string key)
    {
        const int timeoutMs = 3000;
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("defaults", $"read com.apple.universalaccess {key}")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            proc = Process.Start(psi);
            if (proc is null) return false;

            // Drain BOTH redirected streams concurrently via async reads BEFORE the blocking
            // WaitForExit below — this is what actually prevents the deadlock (whichever stream
            // isn't being read is what can fill its pipe buffer and stall the child); the
            // WaitForExit timeout + kill-on-timeout below is a second, independent safety net
            // against a child that simply never exits.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            // The process has exited, so both pipes are at EOF and these tasks should complete
            // almost immediately — still bounded defensively rather than awaited unconditionally.
            if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, timeoutMs))
                return false;

            if (proc.ExitCode != 0) return false;

            return ParseBoolPreferenceOutput(stdoutTask.Result);
        }
        catch
        {
            return false;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    /// <summary>
    /// Pure parser for <c>defaults read</c>'s stdout on success: macOS's <c>defaults</c> CLI
    /// prints a boolean-typed preference as the bare digit <c>1</c> or <c>0</c> (with a trailing
    /// newline) — this also tolerates the word forms defensively since no P/Invoke or subprocess
    /// is involved, so it costs nothing to be lenient. No OS dependency — runs identically on
    /// every CI runner.
    /// </summary>
    internal static bool ParseBoolPreferenceOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var trimmed = output.Trim();
        return trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
