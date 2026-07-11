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
