namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Pure mapping between the app's on/off Efficiency Mode request and the Darwin
/// setpriority(2)/getpriority(2) "prio" value for <c>PRIO_DARWIN_PROCESS</c>. No P/Invoke here —
/// <see cref="MacOSProcessProvider"/> does the actual syscalls — so the mapping is unit-testable
/// on every OS, mirroring the pure/IO split used by <see cref="LaunchdStartType"/> (Sym-1 Task 3)
/// and <c>LinuxIoPriority</c> (Sym-1 Task 2).
///
/// Constants verified against this machine's SDK header (Sym-1 Task 4 brief) at
/// <c>/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk/usr/include/sys/resource.h</c>:
/// <list type="bullet">
/// <item>Line 106: <c>#define PRIO_DARWIN_PROCESS 4</c> — "Second argument is a PID".</item>
/// <item>Line 120: <c>#define PRIO_DARWIN_BG 0x1000</c> — "use PRIO_DARWIN_BG to set the current
/// thread into 'background' state which lowers CPU, disk IO, and networking priorities".</item>
/// </list>
///
/// IMPORTANT, empirically-verified asymmetry (see the Task 4 report for the full derivation):
/// <c>setpriority(PRIO_DARWIN_PROCESS, pid, PRIO_DARWIN_BG)</c> works for ANY same-user target
/// pid (confirmed via an external process changing a spawned child AND an unrelated detached
/// process's OS-visible `ps -o pri` from 20 to 4, then back after clearing). But
/// <c>getpriority(PRIO_DARWIN_PROCESS, pid)</c> only returns a truthful value when
/// <c>pid == 0</c> ("the current thread or process") — this is documented behavior in
/// getpriority(2)'s own man page ("Only a value of zero... is supported for who when setting or
/// getting background state") and was independently confirmed: reading an arbitrary non-self pid
/// always returns 0 regardless of its real clamp state, even immediately after a verified-successful
/// set. There is no other public/unprivileged API that exposes another process's BG-clamp state —
/// `taskpolicy(8)` (the documented CLI cross-check) only has -b/-B (set/clear), no query flag.
/// <see cref="MacOSProcessProvider"/> therefore only trusts a live getpriority read for its own
/// pid; for every other pid it reports what THIS app instance has itself set (never fabricated
/// for untouched pids — default is honest "not set by us", not a verified-off claim).
/// </summary>
public static class MacOSEfficiencyMode
{
    /// <summary>PRIO_DARWIN_PROCESS — "which" value selecting a target by pid.</summary>
    public const int PrioDarwinProcess = 4;

    /// <summary>PRIO_DARWIN_BG — the "prio" value that clamps a process into the background QoS tier.</summary>
    public const int PrioDarwinBg = 0x1000;

    /// <summary>Maps the on/off request to the setpriority(2) "prio" argument: PRIO_DARWIN_BG to
    /// enable the background clamp, 0 to clear it.</summary>
    public static int ToPrioValue(bool enabled) => enabled ? PrioDarwinBg : 0;

    /// <summary>
    /// Maps a getpriority(2) return value to on/off. Deliberately a plain non-zero check rather
    /// than a PRIO_DARWIN_BG bitmask test: the self (who=0) read-back returns a plain 0/1 per the
    /// getpriority(2) man page ("returns 0 when ... not in background state or 1 when ... in
    /// background state"), not the 0x1000 flag value echoed back, so a bitmask test would
    /// misclassify the documented "1" case as false.
    /// </summary>
    public static bool FromPrioValue(int prio) => prio != 0;
}
