namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Pure, OS-agnostic helpers for the macOS GPU performance-statistics route (Sym-2 Task 6):
/// utilization key preference, percent clamping, and memory-byte sanitization. No P/Invoke lives
/// here — <see cref="IOAccelerator"/> does the IOKit calls and hands decoded (key, value) pairs
/// through these helpers — so every rule is unit-testable on any host, mirroring the pure/IO
/// split <see cref="SmcTemperature"/> established for Task 5.
///
/// Route + key names validated live on this machine (base M4, macOS 26.5.2 — see
/// .superpowers/sdd/sym2-ground-truth.md): the public IOAccelerator registry node's
/// "PerformanceStatistics" CFDictionary exposes "Device Utilization %" (Int), a documented
/// fallback name "GPU Activity(%)" seen on some macOS/driver combinations, and
/// "In use system memory" / "Alloc system memory" (bytes) for GPU-allocated unified memory.
/// Public IOKit registry API only — no IOReport, no private frameworks.
/// </summary>
internal static class GpuPerformanceStats
{
    /// <summary>Primary utilization key (probe-confirmed present and live on this machine).</summary>
    public const string DeviceUtilizationKey = "Device Utilization %";

    /// <summary>Fallback utilization key name seen on some macOS/driver combinations. Tried only
    /// when <see cref="DeviceUtilizationKey"/> is absent from the PerformanceStatistics dict.</summary>
    public const string GpuActivityKey = "GPU Activity(%)";

    /// <summary>GPU-allocated unified memory currently in use (bytes; probe-confirmed live:
    /// 58,720,256 at probe time). This is the honest "used" figure for Apple Silicon's unified
    /// memory — there is no separate VRAM pool to report a used/total ratio against.</summary>
    public const string InUseSystemMemoryKey = "In use system memory";

    /// <summary>GPU-allocated unified memory footprint claimed by the driver, in-use or reserved
    /// (bytes; probe-confirmed live: 1,332,559,872 at probe time). Distinct from and larger than
    /// <see cref="InUseSystemMemoryKey"/> — never used as a fabricated "total capacity" figure.</summary>
    public const string AllocSystemMemoryKey = "Alloc system memory";

    /// <summary>
    /// Picks the utilization reading from a per-tick key→value map: <see cref="DeviceUtilizationKey"/>
    /// wins if present, else <see cref="GpuActivityKey"/>, else <c>null</c> (neither key present on
    /// this machine/driver — caller treats as unavailable, never garbage). The winning value is
    /// clamped to the physically valid [0, 100] range before being returned.
    /// </summary>
    public static double? SelectUtilization(IReadOnlyDictionary<string, long> stats)
    {
        if (stats.TryGetValue(DeviceUtilizationKey, out var primary))  return ClampPercent(primary);
        if (stats.TryGetValue(GpuActivityKey, out var fallback))       return ClampPercent(fallback);
        return null;
    }

    /// <summary>Clamps a raw utilization reading to [0, 100] — defends against a driver reporting
    /// a momentary out-of-range value without ever fabricating a value that wasn't read.</summary>
    public static double ClampPercent(double value) => Math.Clamp(value, 0.0, 100.0);

    /// <summary>Sanitizes a raw byte count from the registry: negative is not a physically valid
    /// memory size, so it degrades to 0 (the repo's "unavailable" sentinel) rather than
    /// propagating a negative number into the UI.</summary>
    public static long ClampMemoryBytes(long value) => value < 0 ? 0 : value;

    /// <summary>True when <paramref name="stats"/> contains a usable utilization key — used to
    /// select the "real" IOAccelerator entry when a machine reports more than one (see
    /// <see cref="IOAccelerator"/>; exactly one is expected on this M4, but the design must not
    /// assume that on unknown hardware).</summary>
    public static bool HasUtilizationKey(IReadOnlyDictionary<string, long> stats) =>
        stats.ContainsKey(DeviceUtilizationKey) || stats.ContainsKey(GpuActivityKey);
}

/// <summary>One tick's decoded GPU performance-statistics reading: utilization already clamped to
/// [0, 100], memory figures already sanitized to non-negative bytes.</summary>
internal sealed record GpuPerformanceSample(
    double UtilizationPercent,
    long   InUseSystemMemoryBytes,
    long   AllocSystemMemoryBytes);
