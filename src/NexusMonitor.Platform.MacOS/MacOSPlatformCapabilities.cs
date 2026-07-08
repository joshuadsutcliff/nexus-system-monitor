using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.MacOS;

public sealed class MacOSPlatformCapabilities : IPlatformCapabilities
{
    public bool SupportsCpuAffinity        => false;
    public bool SupportsTrimMemory         => false;
    public bool SupportsCreateDump         => false;
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
    public bool SupportsEfficiencyMode     => false;
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
    public bool SupportsDirectX            => false;
    // StartupView's Enable/Disable toggle is hidden on macOS: MacOSStartupProvider.SetEnabledAsync
    // is a documented no-op (modifying LaunchAgent/LaunchDaemon plists needs elevated permissions).
    // The startup list itself is still shown — only the mutate action is gated off.
    public bool SupportsStartupToggle      => false;
}
