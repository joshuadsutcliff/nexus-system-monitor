using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.Windows;

public sealed class WindowsPlatformCapabilities : IPlatformCapabilities
{
    public bool SupportsCpuAffinity       => true;
    public bool SupportsTrimMemory        => true;
    public bool SupportsCreateDump        => true;
    public bool SupportsFindWindow        => true;
    public bool SupportsIoPriority        => true;
    public bool SupportsMemoryPriority    => true;
    public bool UsesMetaKey               => false;
    public string FileManagerName         => "Explorer";
    public string ServiceManagerName      => "Services";
    public bool SupportsServiceStartupType => true;
    public bool SupportsRegistry           => true;
    public bool SupportsEfficiencyMode     => true;
    public bool SupportsHandles            => true;
    public bool SupportsMemoryMap          => true;
    public bool SupportsPowerPlan          => true;
    public string OpenLocationMenuLabel    => "Open File Location";
    public string DumpFileExtension        => "dmp";
    public bool SupportsDirectX            => true;
    public bool SupportsStartupToggle      => true;
}
