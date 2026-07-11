using System.Diagnostics;
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
    /// that has never touched the toggle — unparsable output) degrades to <see langword="false"/>.
    /// Reads degrade, never throw — internal (not private) so
    /// <c>MacOSAccessibilitySignalsIntegrationTests</c> (gated to real macOS hosts via
    /// <c>InternalsVisibleTo</c>) can exercise the real subprocess path directly.
    /// </summary>
    internal static bool ReadBoolPreference(string key)
    {
        try
        {
            var psi = new ProcessStartInfo("defaults", $"read com.apple.universalaccess {key}")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            if (proc.ExitCode != 0) return false;

            return ParseBoolPreferenceOutput(output);
        }
        catch
        {
            return false;
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
