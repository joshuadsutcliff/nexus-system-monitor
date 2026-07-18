using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Abstractions;

public interface IProcessProvider
{
    IObservable<IReadOnlyList<ProcessInfo>> GetProcessStream(TimeSpan interval);
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default);

    Task KillProcessAsync(int pid, bool killTree = false, CancellationToken ct = default);
    Task SuspendProcessAsync(int pid, CancellationToken ct = default);
    Task ResumeProcessAsync(int pid, CancellationToken ct = default);
    Task SetPriorityAsync(int pid, ProcessPriority priority, CancellationToken ct = default);

    /// <summary>
    /// Reads the process's current priority class/nice value and maps it back to
    /// <see cref="ProcessPriority"/>. Returns null when the priority cannot be determined
    /// (process has exited, access denied, or an unrecognized/platform-specific value) —
    /// callers must treat null as "unknown," never assume <see cref="ProcessPriority.Normal"/>.
    /// </summary>
    Task<ProcessPriority?> GetPriorityAsync(int pid, CancellationToken cancellationToken = default);
    Task SetAffinityAsync(int pid, long affinityMask, CancellationToken ct = default);
    Task SetIoPriorityAsync(int pid, IoPriority priority, CancellationToken ct = default);
    Task SetMemoryPriorityAsync(int pid, MemoryPriority priority, CancellationToken ct = default);
    Task TrimWorkingSetAsync(int pid, CancellationToken ct = default);
    Task SetEfficiencyModeAsync(int pid, bool enabled, CancellationToken ct = default);
    Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(int pid, CancellationToken ct = default);
    Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(int pid, CancellationToken ct = default);
    Task<IReadOnlyList<EnvironmentEntry>> GetEnvironmentAsync(int pid, CancellationToken ct = default);
    Task<IReadOnlyList<HandleInfo>> GetHandlesAsync(int pid, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRegionInfo>> GetMemoryMapAsync(int pid, CancellationToken ct = default);

    /// <summary>
    /// Captures a diagnostic snapshot of the process at the specified path. On Windows this is a
    /// real minidump (MiniDumpWriteDump). On macOS it is a `sample`(1) call-stack sampling
    /// report — a text profile of executing functions, not a memory dump, since no minidump
    /// equivalent exists there. Not implemented on Linux.
    /// </summary>
    Task CreateDumpFileAsync(int pid, string outputPath, CancellationToken ct = default);

    /// <summary>Returns (processAffinityMask, systemAffinityMask) for the given process.</summary>
    Task<(long ProcessMask, long SystemMask)> GetAffinityMasksAsync(int pid, CancellationToken ct = default);

    /// <summary>
    /// Assigns preferred CPU sets to the process (Windows 10+). Softer than affinity —
    /// the OS prefers these CPUs but may schedule elsewhere if needed.
    /// Pass an empty array to clear CPU set assignments.
    /// No-op on macOS/Linux.
    /// </summary>
    Task SetCpuSetsAsync(int pid, uint[] cpuSetIds, CancellationToken ct = default);
}

public enum ProcessPriority { Idle, BelowNormal, Normal, AboveNormal, High, RealTime }
public enum IoPriority { VeryLow = 0, Low = 1, Normal = 2, High = 3 }
public enum MemoryPriority { VeryLow = 1, Low = 2, Medium = 3, Normal = 4, High = 5 }
