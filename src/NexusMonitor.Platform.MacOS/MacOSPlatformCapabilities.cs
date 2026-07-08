using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.MacOS;

public sealed class MacOSPlatformCapabilities : IPlatformCapabilities
{
    public bool SupportsCpuAffinity        => false;
    public bool SupportsTrimMemory         => false;
    // Sym-1 Task 4 (2026-07-08): MacOSProcessProvider.CreateDumpFileAsync now shells out to
    // `/usr/bin/sample <pid> 3 -f <output>` — a real, working capture (verified live: own-user
    // process succeeds and the output contains a "Call graph:" section; a root-owned process
    // fails honestly without sudo, exit code 255). The artifact is a call-stack sampling report,
    // NOT a memory dump — there is no minidump equivalent on macOS (see IProcessProvider's doc
    // comment and MacOSSampleCommand). DumpFileExtension is "txt" on this platform accordingly.
    public bool SupportsCreateDump         => true;
    public bool SupportsFindWindow         => false;
    public bool SupportsIoPriority         => false;
    public bool SupportsMemoryPriority     => false;
    public bool UsesMetaKey                => true;
    public string FileManagerName          => "Finder";
    public string ServiceManagerName       => "Launch Daemons";
    // Sym-1 Task 3 (2026-07-08): ServicesView.axaml's binding on this flag gates ONLY the
    // "Set Startup Type" context-menu submenu (the write/edit path — SetStartupTypeCommand ->
    // MacOSServicesProvider.SetStartTypeAsync, which unconditionally throws
    // PlatformNotSupportedException; there is no launchd runtime call that flips a job's start
    // policy). The Start Type display column and detail-sidebar row are NOT gated by this flag —
    // they're unconditionally visible, and MacOSServicesProvider now populates them with honest
    // launchd-derived values (Automatic/Manual/Disabled/Unknown) instead of a hardcoded Unknown.
    // Per the brief's binding flag ruling: do NOT flip this to true, since doing so would surface
    // an edit menu whose backing implementation is absent, not just a display column. Keep false
    // until a real write path exists (would need plist mutation + launchctl bootout/bootstrap,
    // itself needing elevated permissions for system-owned LaunchDaemons — the same class of
    // problem as SupportsStartupToggle below). If read/write ever need independent gating, split
    // this into two capability flags rather than reusing one for both.
    public bool SupportsServiceStartupType => false;
    public bool SupportsRegistry           => false;
    // Sym-1 Task 4 (2026-07-08): MacOSProcessProvider now implements a real Efficiency Mode via
    // the Darwin background QoS clamp (setpriority(PRIO_DARWIN_PROCESS, pid, PRIO_DARWIN_BG)) —
    // verified live to change the OS-visible scheduling priority of an arbitrary same-user
    // process (ps -o pri: 20 -> 4 -> 20 across set/clear). Read-back is honest but limited by a
    // genuine macOS asymmetry: getpriority(PRIO_DARWIN_PROCESS, pid) only returns a truthful
    // value for pid==0 (the calling process itself) — see MacOSEfficiencyMode's doc comment for
    // the full empirical derivation. For every other pid, ProcessesViewModel/the process grid
    // shows what this app instance has itself set, not an omniscient live read (macOS exposes no
    // public unprivileged API for that — not even `taskpolicy(8)` has a query flag).
    public bool SupportsEfficiencyMode     => true;
    public bool SupportsHandles            => false;
    public bool SupportsMemoryMap          => false;
    // DOCUMENTED DECISION (not an oversight): MacOSPowerPlanProvider is a real, pmset-backed
    // provider and reads (GetPowerPlans/GetActivePlan) work fine unprivileged. The problem is
    // writes — `pmset -a lowpowermode 1` and friends can silently fail when the process isn't
    // elevated, and pmset gives no reliable exit-code/output signal we can surface as an error.
    // Flipping this to true today would let a user pick "Power Saver" in the UI, see it marked
    // active, and have nothing actually change on the system — the exact class of dishonesty
    // this flag exists to prevent. Keep this false until the provider can detect and surface a
    // failed `pmset` write (e.g. by reading back the setting after applying it and reporting a
    // failure through LastError), at which point PerformanceProfilesView can be un-hidden here.
    public bool SupportsPowerPlan          => false;
    public string OpenLocationMenuLabel    => "Reveal in Finder";
    // The dump/CreateDumpFile file-save dialog's suggested extension. "txt" here, not "dmp" —
    // the macOS artifact is a `sample`(1) text report, not a binary minidump (see
    // SupportsCreateDump above), so suggesting ".dmp" would misleadingly imply the same binary
    // format Windows produces.
    public string DumpFileExtension        => "txt";
    public bool SupportsDirectX            => false;
    // StartupView's Enable/Disable toggle is hidden on macOS: MacOSStartupProvider.SetEnabledAsync
    // is a documented no-op (modifying LaunchAgent/LaunchDaemon plists needs elevated permissions).
    // The startup list itself is still shown — only the mutate action is gated off.
    public bool SupportsStartupToggle      => false;
}
