using FluentAssertions;
using NexusMonitor.Core.Reactive;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Pins the two canonical background-sampling cadences so a future edit can't silently
/// change the values every automation service relies on (see MonitoringCadence.cs remarks).
/// </summary>
public class MonitoringCadenceTests
{
    [Fact]
    public void Fast_Is_OneSecond() =>
        MonitoringCadence.Fast.Should().Be(TimeSpan.FromSeconds(1));

    [Fact]
    public void Normal_Is_TwoSeconds() =>
        MonitoringCadence.Normal.Should().Be(TimeSpan.FromSeconds(2));

    [Fact]
    public void Fast_Is_Faster_Than_Normal() =>
        MonitoringCadence.Fast.Should().BeLessThan(MonitoringCadence.Normal);
}
