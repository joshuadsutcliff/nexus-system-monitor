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
    public void SelectTemperatureCelsius_LabelsPresentButNoEdge_FallsBackToTemp1()
    {
        var readings = new[]
        {
            new AmdGpuTemperature.Reading(1, "junction", 55000),
            new AmdGpuTemperature.Reading(2, "mem", 48000),
        };

        AmdGpuTemperature.SelectTemperatureCelsius(readings).Should().Be(55.0);
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
