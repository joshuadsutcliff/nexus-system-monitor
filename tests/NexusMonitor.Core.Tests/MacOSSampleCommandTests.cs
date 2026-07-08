using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MacOSSampleCommand"/> — pure `/usr/bin/sample` argument construction.
/// No process I/O, so this runs identically on every OS.
/// </summary>
public class MacOSSampleCommandTests
{
    [Fact]
    public void BuildArguments_ProducesPidDurationFlagAndPath()
    {
        var args = MacOSSampleCommand.BuildArguments(1234, "/tmp/out.txt");

        args.Should().Equal("1234", "3", "-f", "/tmp/out.txt");
    }

    [Fact]
    public void BuildArguments_UsesThreeSecondDuration()
    {
        MacOSSampleCommand.DurationSeconds.Should().Be(3);
    }

    [Fact]
    public void BuildArguments_DifferentPidAndPath_ReflectsBothInline()
    {
        var args = MacOSSampleCommand.BuildArguments(99, "/Users/josh/Desktop/dump.txt");

        args.Should().Equal("99", "3", "-f", "/Users/josh/Desktop/dump.txt");
    }

    [Fact]
    public void BinaryPath_IsSystemSampleTool()
    {
        MacOSSampleCommand.BinaryPath.Should().Be("/usr/bin/sample");
    }
}
