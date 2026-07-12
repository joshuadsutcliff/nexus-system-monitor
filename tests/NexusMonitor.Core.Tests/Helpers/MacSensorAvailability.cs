using System.Linq;
using System.Runtime.InteropServices;
using NexusMonitor.Platform.MacOS;

namespace NexusMonitor.Core.Tests.Helpers;

/// <summary>
/// Runtime detection of which macOS sensor routes are actually backed by real hardware on the
/// host running this test process. Each route is probed once per test run and cached
/// (<see cref="Lazy{T}"/>, thread-safe by default) — every test that needs the same answer reuses
/// it rather than re-probing.
///
/// Exists because GitHub's <c>macos-latest</c> CI runner is a VM with no AppleSMC, PMU HID
/// temperature services, or GPU IOAccelerator registry node: PR #24's CI run failed
/// <c>MacOSTemperatureIntegrationTests.Provider_FiftyConsecutiveTicks_NeverThrowAndStayPlausible</c>
/// (CPU temp 0.0, expected a plausible [10,120] range) and
/// <c>AppleSmc_PerTickTemperatureReadCost_IsCheap</c> (<c>AppleSmc.Open()</c> returned null —
/// the "must open unprivileged on this host" assumption is false on a VM), and other tests in
/// both integration files would fail the identical way for the identical reason: they assumed
/// real Apple-Silicon-desktop hardware unconditionally.
///
/// The product code under test already has an honest-degrade convention for exactly this case —
/// <c>AppleSmc.Open()</c> returns <c>null</c> when the service is absent, <c>IOHidSensors
/// .ReadSocTemperature()</c> and <c>IOAccelerator.ReadPerformanceStatistics()</c> degrade to
/// 0/<c>null</c> rather than throwing. These tests now mirror that same philosophy instead of
/// assuming real hardware: detect what's actually there, then assert full plausible-value fidelity
/// when it is, and the honest-degrade invariants (no throw, honest 0/null) when it isn't — never a
/// silent early `return` that would hide a real regression on either kind of host.
/// </summary>
internal static class MacSensorAvailability
{
    private static readonly Lazy<bool> _appleSmcCpuKey     = new(DetectAppleSmcCpuKey);
    private static readonly Lazy<bool> _ioHidTemperature   = new(DetectIoHidTemperature);
    private static readonly Lazy<bool> _ioAcceleratorStats = new(DetectIoAcceleratorStats);

    /// <summary>True if AppleSMC opens on this host AND at least one CPU (performance- or
    /// efficiency-core) temperature key from this machine's resolved key set decodes to a
    /// plausible (10-120 °C) value. False on a VM: either the AppleSMC service itself is absent
    /// (<c>Open()</c> returns null), or it opens but every key reads absent/garbage.</summary>
    public static bool HasAppleSmcCpuKey => _appleSmcCpuKey.Value;

    /// <summary>True if the IOHID PMU-temperature fallback route (page 0xff00 / usage 5) yields at
    /// least one plausible, non-<c>tcal</c> die-temperature reading. False on a VM: no PMU HID
    /// event services exist there, so <c>ReadSocTemperature()</c> honestly degrades to 0.</summary>
    public static bool HasIoHidTemperature => _ioHidTemperature.Value;

    /// <summary>True if at least one IOAccelerator registry entry opens AND yields a
    /// PerformanceStatistics sample with a usable utilization key. False on a VM: no GPU
    /// accelerator node exists in the IOKit registry, so <c>Open()</c> returns null.</summary>
    public static bool HasIoAcceleratorStats => _ioAcceleratorStats.Value;

    private static bool DetectAppleSmcCpuKey()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            using var smc = AppleSmc.Open();
            if (smc is null) return false;

            var isArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var keySet = SmcTemperature.ResolveKeySet(isArm64 ? "Apple M4" : "Intel", isArm64);
            foreach (var key in keySet.CpuPerformance.Concat(keySet.CpuEfficiency))
            {
                var value = smc.ReadTemperature(key);
                if (value.HasValue && SmcTemperature.IsPlausible(value.Value)) return true;
            }
            return false;
        }
        catch
        {
            // Detection itself must never throw and abort the whole run before a single test
            // executes — "can't tell if it's there" degrades to "treat as absent," the same
            // honest-degrade direction every read in this arc already takes.
            return false;
        }
    }

    private static bool DetectIoHidTemperature()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            // ReadSocTemperature() already plausibility-filters internally and returns exactly
            // 0.0 on any failure, absence, or all-garbage read — a non-zero result IS the
            // availability signal; no separate probe logic needed.
            return IOHidSensors.ReadSocTemperature() != 0.0;
        }
        catch
        {
            return false;
        }
    }

    private static bool DetectIoAcceleratorStats()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            using var accel = IOAccelerator.Open();
            if (accel is null) return false;
            return accel.ReadPerformanceStatistics() is not null;
        }
        catch
        {
            return false;
        }
    }
}
