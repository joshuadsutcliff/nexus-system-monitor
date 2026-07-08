using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.Linux;

public sealed class LinuxPlatformCapabilities : IPlatformCapabilities
{
    public bool SupportsCpuAffinity        => true;
    public bool SupportsTrimMemory         => false;
    public bool SupportsCreateDump         => false;
    public bool SupportsFindWindow         => false;
    // SetIoPriorityAsync in LinuxProcessProvider uses the ioprio_set syscall (251 on x86_64,
    // 30 on aarch64; see LinuxIoPriority) — implemented as of Sym-1 Task 2.
    public bool SupportsIoPriority         => true;
    public bool SupportsMemoryPriority     => false;
    public bool UsesMetaKey                => false;
    public string FileManagerName          => "Files";
    public string ServiceManagerName       => "systemd";
    public bool SupportsServiceStartupType => true;
    public bool SupportsRegistry           => false;
    public bool SupportsEfficiencyMode     => false;
    // GetHandlesAsync reads /proc/<pid>/fd (Sym-1 Task 2).
    public bool SupportsHandles            => true;
    // GetMemoryMapAsync reads /proc/<pid>/maps via the shared ProcMapsParser (Sym-1 Task 2).
    public bool SupportsMemoryMap          => true;
    public bool SupportsPowerPlan          => true;
    public string OpenLocationMenuLabel    => "Show in Files";
    public bool SupportsDirectX            => false;
    public bool SupportsStartupToggle      => true;
}
