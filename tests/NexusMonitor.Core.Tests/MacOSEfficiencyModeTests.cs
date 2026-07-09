using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MacOSEfficiencyMode"/> — pure PRIO_DARWIN_BG value mapping. No P/Invoke
/// or file I/O, so this runs identically on every OS.
/// </summary>
public class MacOSEfficiencyModeTests
{
    [Fact]
    public void PrioDarwinProcess_MatchesSdkHeaderConstant()
    {
        // <sys/resource.h>:106 — #define PRIO_DARWIN_PROCESS 4
        MacOSEfficiencyMode.PrioDarwinProcess.Should().Be(4);
    }

    [Fact]
    public void PrioDarwinBg_MatchesSdkHeaderConstant()
    {
        // <sys/resource.h>:120 — #define PRIO_DARWIN_BG 0x1000
        MacOSEfficiencyMode.PrioDarwinBg.Should().Be(0x1000);
    }

    [Fact]
    public void ToPrioValue_Enabled_ReturnsPrioDarwinBg()
    {
        MacOSEfficiencyMode.ToPrioValue(true).Should().Be(0x1000);
    }

    [Fact]
    public void ToPrioValue_Disabled_ReturnsZero()
    {
        MacOSEfficiencyMode.ToPrioValue(false).Should().Be(0);
    }

    [Fact]
    public void FromPrioValue_Zero_IsFalse()
    {
        MacOSEfficiencyMode.FromPrioValue(0).Should().BeFalse();
    }

    [Fact]
    public void FromPrioValue_PrioDarwinBgFlagValue_IsTrue()
    {
        MacOSEfficiencyMode.FromPrioValue(0x1000).Should().BeTrue();
    }

    [Fact]
    public void FromPrioValue_PlainOne_IsTrue()
    {
        // getpriority(2)'s documented self (who=0) read-back returns a plain 0/1, not the raw
        // 0x1000 flag — FromPrioValue must treat "1" as on, not just "0x1000".
        MacOSEfficiencyMode.FromPrioValue(1).Should().BeTrue();
    }
}
