using System.Globalization;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Pure argument-list construction for the <c>/usr/bin/sample</c> invocation used by
/// <see cref="MacOSProcessProvider.CreateDumpFileAsync"/>. No process I/O here — kept separate so
/// the argument shape is unit-testable on every OS, mirroring the pure/IO split used elsewhere in
/// this project (<see cref="MacOSEfficiencyMode"/>, <see cref="LaunchdStartType"/>).
///
/// <c>sample</c> produces a call-stack sampling report (a text profile of what functions were
/// executing), NOT a memory dump — there is no minidump equivalent on macOS. See the Sym-1 Task 4
/// report for the empirical capture proof (own-user process succeeds and contains a
/// "Call graph:" section; a root-owned process fails without <c>sudo</c>, exit code 255).
/// </summary>
public static class MacOSSampleCommand
{
    /// <summary>Path to the system `sample` tool (present on every macOS install; part of the
    /// Command Line Tools / Xcode developer tooling shipped with the OS).</summary>
    public const string BinaryPath = "/usr/bin/sample";

    /// <summary>Sampling duration in seconds, per the brief: `sample &lt;pid&gt; 3 -f &lt;output&gt;`.</summary>
    public const int DurationSeconds = 3;

    /// <summary>
    /// Builds the argument list for <c>sample &lt;pid&gt; 3 -f &lt;outputPath&gt;</c>. `-f` is the
    /// short alias `sample(1)` accepts for `-file` (verified empirically — the man page only
    /// documents the long form, but `-f` works identically and is what the brief specifies).
    /// </summary>
    public static string[] BuildArguments(int pid, string outputPath) =>
    [
        pid.ToString(CultureInfo.InvariantCulture),
        DurationSeconds.ToString(CultureInfo.InvariantCulture),
        "-f",
        outputPath,
    ];
}
