using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Pure-logic coverage for the Sym-2 Task 6 macOS GPU utilization/memory work: utilization
/// key-preference selection, percent clamping, and memory-byte sanitization. No IOKit here, so
/// these run on every OS (Windows/Linux CI included) — the live IOAccelerator path is exercised
/// by the macOS-gated <see cref="IOAcceleratorIntegrationTests"/>.
/// </summary>
public class GpuPerformanceStatsTests
{
    // ── Utilization key preference ──────────────────────────────────────────────

    [Fact]
    public void SelectUtilization_PrefersDeviceUtilizationKey_WhenBothPresent()
    {
        var stats = new Dictionary<string, long>
        {
            [GpuPerformanceStats.DeviceUtilizationKey] = 7,
            [GpuPerformanceStats.GpuActivityKey]       = 99,
        };
        GpuPerformanceStats.SelectUtilization(stats).Should().Be(7.0,
            "Device Utilization % is the probe-confirmed primary key and must win when both are present");
    }

    [Fact]
    public void SelectUtilization_FallsBackToGpuActivityKey_WhenPrimaryAbsent()
    {
        var stats = new Dictionary<string, long> { [GpuPerformanceStats.GpuActivityKey] = 42 };
        GpuPerformanceStats.SelectUtilization(stats).Should().Be(42.0);
    }

    [Fact]
    public void SelectUtilization_NeitherKeyPresent_ReturnsNull()
    {
        var stats = new Dictionary<string, long> { ["In use system memory"] = 123 };
        GpuPerformanceStats.SelectUtilization(stats).Should().BeNull(
            "no utilization key on this machine/driver must degrade to unavailable, never garbage");
    }

    [Fact]
    public void SelectUtilization_EmptyDictionary_ReturnsNull()
    {
        GpuPerformanceStats.SelectUtilization(new Dictionary<string, long>()).Should().BeNull();
    }

    // ── Percent clamping ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(7.0, 7.0)]
    [InlineData(-5.0, 0.0)]     // a momentary negative driver blip clamps, never fabricates
    [InlineData(150.0, 100.0)]  // a momentary >100 driver blip clamps
    public void ClampPercent_BoundsToZeroToOneHundred(double raw, double expected)
    {
        GpuPerformanceStats.ClampPercent(raw).Should().Be(expected);
    }

    [Fact]
    public void SelectUtilization_ClampsOutOfRangeReading()
    {
        var stats = new Dictionary<string, long> { [GpuPerformanceStats.DeviceUtilizationKey] = 150 };
        GpuPerformanceStats.SelectUtilization(stats).Should().Be(100.0);
    }

    // ── Memory-byte sanitization ─────────────────────────────────────────────────

    [Theory]
    [InlineData(58720256L, 58720256L)]   // probe-confirmed "In use system memory" value
    [InlineData(1332559872L, 1332559872L)] // probe-confirmed "Alloc system memory" value
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]                 // not physically valid — degrade to 0, never negative
    public void ClampMemoryBytes_NeverNegative(long raw, long expected)
    {
        GpuPerformanceStats.ClampMemoryBytes(raw).Should().Be(expected);
    }

    // ── Memory-key presence (C4: honest-unavailable vs. genuinely-reported zero) ──

    [Fact]
    public void ReadMemoryBytes_KeyAbsent_ReturnsNull()
    {
        // Utilization key present, memory keys absent — must degrade to unavailable (null),
        // never render as a fabricated measured 0.
        var stats = new Dictionary<string, long> { [GpuPerformanceStats.DeviceUtilizationKey] = 42 };
        GpuPerformanceStats.ReadMemoryBytes(stats, GpuPerformanceStats.InUseSystemMemoryKey).Should().BeNull(
            "the hardware/driver didn't report this key — must surface as unavailable, never a fabricated 0");
    }

    [Fact]
    public void ReadMemoryBytes_KeyPresentWithZero_ReturnsZero()
    {
        var stats = new Dictionary<string, long> { [GpuPerformanceStats.InUseSystemMemoryKey] = 0 };
        GpuPerformanceStats.ReadMemoryBytes(stats, GpuPerformanceStats.InUseSystemMemoryKey).Should().Be(0L,
            "a genuinely reported 0 must stay 0, not collapse into the same representation as unavailable");
    }

    [Fact]
    public void ReadMemoryBytes_KeyPresentWithRealValue_ReturnsThatValue()
    {
        var stats = new Dictionary<string, long> { [GpuPerformanceStats.AllocSystemMemoryKey] = 1_332_559_872L };
        GpuPerformanceStats.ReadMemoryBytes(stats, GpuPerformanceStats.AllocSystemMemoryKey)
            .Should().Be(1_332_559_872L);
    }

    [Fact]
    public void ReadMemoryBytes_KeyPresentNegative_ClampsToZero_NotNull()
    {
        // Present-but-invalid (negative) is still a *present* reading — sanitize to 0, don't
        // conflate it with the absent-key case.
        var stats = new Dictionary<string, long> { [GpuPerformanceStats.AllocSystemMemoryKey] = -1L };
        GpuPerformanceStats.ReadMemoryBytes(stats, GpuPerformanceStats.AllocSystemMemoryKey).Should().Be(0L);
    }

    [Fact]
    public void ReadMemoryBytes_EmptyDictionary_ReturnsNull()
    {
        GpuPerformanceStats.ReadMemoryBytes(new Dictionary<string, long>(), GpuPerformanceStats.InUseSystemMemoryKey)
            .Should().BeNull();
    }

    // ── Utilization-key presence (multi-accelerator entry selection) ───────────

    [Fact]
    public void HasUtilizationKey_TrueWhenEitherKeyPresent()
    {
        GpuPerformanceStats.HasUtilizationKey(
            new Dictionary<string, long> { [GpuPerformanceStats.DeviceUtilizationKey] = 0 }).Should().BeTrue();
        GpuPerformanceStats.HasUtilizationKey(
            new Dictionary<string, long> { [GpuPerformanceStats.GpuActivityKey] = 0 }).Should().BeTrue();
    }

    [Fact]
    public void HasUtilizationKey_FalseWhenNeitherKeyPresent()
    {
        GpuPerformanceStats.HasUtilizationKey(
            new Dictionary<string, long> { [GpuPerformanceStats.InUseSystemMemoryKey] = 123 }).Should().BeFalse();
        GpuPerformanceStats.HasUtilizationKey(new Dictionary<string, long>()).Should().BeFalse();
    }
}
