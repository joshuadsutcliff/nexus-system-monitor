using FluentAssertions;
using NexusMonitor.Platform.Linux;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="AmdGpuTemperature.SelectTemperatureCelsius"/> — pure selection/parsing
/// logic for the amdgpu hwmon temp*_input / temp*_label convention. No filesystem access, so
/// this runs identically on every OS regardless of whether real AMD hwmon sysfs is present.
/// </summary>
public class AmdGpuTemperatureTests
{
    [Fact]
    public void SelectTemperatureCelsius_PrefersEdgeLabel_OverOtherLabels()
    {
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, "edge", 45000),
            new AmdGpuTemperature.Reading(2, "junction", 55000),
            new AmdGpuTemperature.Reading(3, "mem", 48000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(45.0);
    }

    [Fact]
    public void SelectTemperatureCelsius_NoLabelsAtAll_FallsBackToTemp1()
    {
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, null, 42500),
            new AmdGpuTemperature.Reading(2, null, 55000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(42.5);
    }

    [Fact]
    public void SelectTemperatureCelsius_Temp1LabeledJunctionAndNoEdge_ReturnsUnavailable()
    {
        // temp1 carries a different-meaning label (junction can run tens of °C hotter than
        // edge) and no "edge" reading exists anywhere — must not silently substitute junction
        // under the "GPU temperature" label. Honest-UI: unavailable beats misleading.
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, "junction", 55000),
            new AmdGpuTemperature.Reading(2, "mem", 48000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(0);
    }

    [Fact]
    public void SelectTemperatureCelsius_Temp1Unlabeled_OtherReadingsLabeledNonEdge_FallsBackToTemp1()
    {
        // temp1 itself is unlabeled (no informative label to distrust), so it remains a valid
        // fallback even though other readings carry non-edge labels.
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, null, 42500),
            new AmdGpuTemperature.Reading(2, "mem", 48000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(42.5);
    }

    [Fact]
    public void SelectTemperatureCelsius_Temp1LabeledEdge_FallsBackToTemp1()
    {
        // temp1 labeled "edge" is the redundant-but-valid case — same value whether reached via
        // the edge-preference branch or the temp1 fallback branch.
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, "edge", 45000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(45.0);
    }

    [Fact]
    public void SelectTemperatureCelsius_EdgeOutOfOrder_StillFound()
    {
        // hwmon does not guarantee "edge" lands on temp1 — some layouts put it later.
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, "mem", 48000),
            new AmdGpuTemperature.Reading(2, "edge", 46000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(46.0);
    }

    [Fact]
    public void SelectTemperatureCelsius_EdgeLabelCaseInsensitive()
    {
        var readings = new[] { new AmdGpuTemperature.Reading(1, "EDGE", 50000) };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(50.0);
    }

    [Fact]
    public void SelectTemperatureCelsius_EmptyReadings_ReturnsZero()
    {
        AmdGpuTemperature.SelectTemperatureCelsius([]).Should().Be(0);
    }

    [Fact]
    public void SelectTemperatureCelsius_NoEdgeAndNoTemp1_ReturnsZero()
    {
        var readings = new[] { new AmdGpuTemperature.Reading(2, "mem", 48000) };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void SelectTemperatureCelsius_EdgeValueNonPositive_TreatedAsUnavailable(long milliDegreesC)
    {
        var readings = new[] { new AmdGpuTemperature.Reading(1, "edge", milliDegreesC) };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(0);
    }

    [Fact]
    public void SelectTemperatureCelsius_EdgeValueAbove150C_TreatedAsUnavailable()
    {
        var readings = new[] { new AmdGpuTemperature.Reading(1, "edge", 150001) };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(0);
    }

    [Fact]
    public void SelectTemperatureCelsius_ExactlyAtOneFiftyBoundary_IsValid()
    {
        var readings = new[] { new AmdGpuTemperature.Reading(1, "edge", 150000) };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(150.0);
    }

    [Fact]
    public void SelectTemperatureCelsius_DoesNotFallBackWhenSelectedEdgeIsInvalid()
    {
        // Even though temp1 would be usable, an invalid "edge" reading must not silently
        // fall through to another sensor — it should read as unavailable (honest-UI convention).
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, "edge", 0),
            new AmdGpuTemperature.Reading(2, "mem", 48000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(0);
    }
}
