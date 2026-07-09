using System.Runtime.InteropServices;
using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.Linux;

/// <summary>
/// Pure mapping from the cross-platform <see cref="IoPriority"/> enum to the Linux
/// ioprio_set(2) "ioprio" value (class + data packed as <c>(class &lt;&lt; 13) | data</c>),
/// plus syscall-number selection by CPU architecture. Deliberately has no P/Invoke or file
/// I/O so the mapping policy is unit-testable on every OS — mirrors the separation used for
/// <see cref="AmdGpuTemperature"/>. <see cref="LinuxProcessProvider"/> does the actual syscall.
/// </summary>
public static class LinuxIoPriority
{
    /// <summary>Bit shift applied to the ioprio class when packing (class, data) into one int.</summary>
    public const int IoprioClassShift = 13;

    // ioprio_set(2) class values (see linux/ioprio.h). IOPRIO_CLASS_RT (1) is deliberately
    // never used here: scheduling realtime I/O priority for an arbitrary process requires
    // CAP_SYS_ADMIN/root, and this app has no business demanding that on the user's behalf.
    // The app's "High" level instead maps to the highest priority available within the
    // unprivileged Best-Effort class (data 0) — the documented, safe substitute for RT.
    private const int IoprioClassBestEffort = 2;
    private const int IoprioClassIdle       = 3;

    /// <summary>
    /// Maps every <see cref="IoPriority"/> value to an ioprio_set(2) (class, data) pair.
    /// Best-Effort data ranges 0 (highest) – 7 (lowest); 4 is the kernel's documented default.
    /// </summary>
    public static (int IoprioClass, int Data) MapToClassAndData(IoPriority priority) => priority switch
    {
        // IOPRIO_CLASS_IDLE — only gets disk time when nothing else wants it. Data is not
        // interpreted for this class by the kernel; 0 is passed for a deterministic value.
        IoPriority.VeryLow => (IoprioClassIdle, 0),
        // Best-Effort, lowest data value within the class.
        IoPriority.Low     => (IoprioClassBestEffort, 7),
        // Best-Effort, kernel-documented default priority.
        IoPriority.Normal  => (IoprioClassBestEffort, 4),
        // Best-Effort, highest data value within the class — the closest unprivileged
        // equivalent to "High" since IOPRIO_CLASS_RT is off-limits (see class comment above).
        IoPriority.High    => (IoprioClassBestEffort, 0),
        _                  => (IoprioClassBestEffort, 4),
    };

    /// <summary>Packs (class, data) into the single ioprio value ioprio_set(2) expects.</summary>
    public static int ComputeIoprioValue(IoPriority priority)
    {
        var (ioClass, data) = MapToClassAndData(priority);
        return (ioClass << IoprioClassShift) | data;
    }

    /// <summary>
    /// ioprio_set is syscall number 251 on x86_64 and 30 on aarch64 (Linux syscall tables are
    /// architecture-specific — there is no portable constant). Any other architecture has no
    /// known-correct number here, so this returns null ("not supported") rather than guessing
    /// and issuing a syscall with the wrong number.
    /// </summary>
    public static int? GetSyscallNumber(Architecture architecture) => architecture switch
    {
        Architecture.X64   => 251,
        Architecture.Arm64 => 30,
        _                  => null,
    };
}
