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
    /// Captures the process's exact native priority and applies a temporary mapped priority.
    /// A restore receipt is returned only after the native setter confirms the change.
    /// </summary>
    Task<PriorityApplyResult> ApplyTemporaryPriorityAsync(
        int pid, ProcessPriority priority, CancellationToken ct = default);

    /// <summary>Restores the exact native priority held by a prior apply receipt.</summary>
    Task<PriorityRestoreResult> RestorePriorityAsync(
        PriorityRestoreReceipt receipt, CancellationToken ct = default);

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

public enum ProcessPriorityFailureKind
{
    Unknown,
    ProcessExited,
    PermissionDenied,
    InvalidRequest,
}

public sealed record ProcessPriorityFailure(
    ProcessPriorityFailureKind Kind,
    string ErrorDomain,
    int NativeErrorCode,
    string Message);

/// <summary>
/// Opaque provider-owned state required to restore a temporary priority change exactly.
/// </summary>
public sealed record PriorityRestoreReceipt(
    int Pid,
    string ProviderKind,
    long NativeValue);

public sealed record PriorityApplyResult(
    PriorityRestoreReceipt? Receipt,
    ProcessPriorityFailure? Failure)
{
    public bool Succeeded => Receipt is not null && Failure is null;

    public static PriorityApplyResult Success(PriorityRestoreReceipt receipt) => new(receipt, null);
    public static PriorityApplyResult Failed(ProcessPriorityFailure failure) => new(null, failure);
}

public enum PriorityRestoreStatus
{
    Restored,
    ProcessExited,
    Failed,
}

public sealed record PriorityRestoreResult(
    PriorityRestoreStatus Status,
    ProcessPriorityFailure? Failure = null)
{
    public static PriorityRestoreResult Restored() => new(PriorityRestoreStatus.Restored);
    public static PriorityRestoreResult Exited(ProcessPriorityFailure failure) =>
        new(PriorityRestoreStatus.ProcessExited, failure);
    public static PriorityRestoreResult Failed(ProcessPriorityFailure failure) =>
        new(PriorityRestoreStatus.Failed, failure);
}

public sealed record PriorityRestoreFailure(int Pid, ProcessPriorityFailure Failure);

public sealed record PriorityRestoreReport(
    int Attempted,
    int Restored,
    int Exited,
    IReadOnlyList<PriorityRestoreFailure> Failures)
{
    public static PriorityRestoreReport Empty { get; } = new(0, 0, 0, []);
    public int Pending => Failures.Count;
    public bool IsPartial => Restored + Exited > 0 && Pending > 0;
}

public sealed class ProcessPriorityOperationException : InvalidOperationException
{
    public int Pid { get; }
    public ProcessPriorityFailure Failure { get; }

    public ProcessPriorityOperationException(int pid, ProcessPriorityFailure failure)
        : base(failure.Message)
    {
        Pid = pid;
        Failure = failure;
    }
}
