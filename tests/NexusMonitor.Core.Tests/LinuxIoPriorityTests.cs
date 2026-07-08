using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Platform.Linux;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="LinuxIoPriority"/> — pure ioprio_set(2) value computation and syscall
/// number selection. No P/Invoke or file I/O, so this runs identically on every OS.
/// </summary>
public class LinuxIoPriorityTests
{
    [Theory]
    [InlineData(IoPriority.VeryLow, 3, 0)]
    [InlineData(IoPriority.Low,     2, 7)]
    [InlineData(IoPriority.Normal,  2, 4)]
    [InlineData(IoPriority.High,    2, 0)]
    public void MapToClassAndData_MapsEveryEnumValueExplicitly(IoPriority priority, int expectedClass, int expectedData)
    {
        var (ioClass, data) = LinuxIoPriority.MapToClassAndData(priority);

        ioClass.Should().Be(expectedClass);
        data.Should().Be(expectedData);
    }

    [Fact]
    public void MapToClassAndData_NeverMapsToRealTimeClass()
    {
        // IOPRIO_CLASS_RT == 1. Realtime I/O priority requires privileges this app must not
        // assume it has, so no IoPriority value may ever resolve to it.
        foreach (IoPriority priority in Enum.GetValues<IoPriority>())
        {
            var (ioClass, _) = LinuxIoPriority.MapToClassAndData(priority);
            ioClass.Should().NotBe(1);
        }
    }

    [Theory]
    [InlineData(IoPriority.VeryLow, (3 << 13) | 0)]
    [InlineData(IoPriority.Low,     (2 << 13) | 7)]
    [InlineData(IoPriority.Normal,  (2 << 13) | 4)]
    [InlineData(IoPriority.High,    (2 << 13) | 0)]
    public void ComputeIoprioValue_PacksClassAndDataCorrectly(IoPriority priority, int expected)
    {
        LinuxIoPriority.ComputeIoprioValue(priority).Should().Be(expected);
    }

    [Fact]
    public void GetSyscallNumber_X64_Is251()
    {
        LinuxIoPriority.GetSyscallNumber(Architecture.X64).Should().Be(251);
    }

    [Fact]
    public void GetSyscallNumber_Arm64_Is30()
    {
        LinuxIoPriority.GetSyscallNumber(Architecture.Arm64).Should().Be(30);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void GetSyscallNumber_UnknownArchitecture_ReturnsNull(Architecture architecture)
    {
        LinuxIoPriority.GetSyscallNumber(architecture).Should().BeNull();
    }
}
