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
