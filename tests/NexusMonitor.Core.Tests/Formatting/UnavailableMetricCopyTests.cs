using FluentAssertions;
using NexusMonitor.Core.Formatting;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="UnavailableMetricCopy"/> — the approved-final user-facing tooltip copy
/// explaining WHY an honest "—"/"N/A" placeholder is showing (unavailable-metric-tooltips
/// feature). These strings are locked wording; the tests here guard shape (non-empty, single
/// sentence, no banned legacy product names) rather than re-asserting the literal text, so a
/// future approved wording change doesn't require touching the test file.
/// </summary>
public class UnavailableMetricCopyTests
{
    public static readonly TheoryData<string, string> AllCopyStrings = new()
    {
        { nameof(UnavailableMetricCopy.Generic), UnavailableMetricCopy.Generic },
        { nameof(UnavailableMetricCopy.GpuTempAppleSiliconIdle), UnavailableMetricCopy.GpuTempAppleSiliconIdle },
        { nameof(UnavailableMetricCopy.CpuTempUnsupported), UnavailableMetricCopy.CpuTempUnsupported },
        { nameof(UnavailableMetricCopy.GpuMemoryTotalMacOS), UnavailableMetricCopy.GpuMemoryTotalMacOS },
    };

    [Theory]
    [MemberData(nameof(AllCopyStrings))]
    public void Copy_IsNonEmpty(string _, string copy)
    {
        copy.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(AllCopyStrings))]
    public void Copy_IsSingleSentence_NoDoubledTerminalPeriod(string _, string copy)
    {
        copy.Should().EndWith(".", "each string is a single sentence");
        copy.Should().NotContain("..", "a doubled/tripled terminal period reads as a typo, not an ellipsis");
    }

    [Theory]
    [MemberData(nameof(AllCopyStrings))]
    public void Copy_DoesNotContainBannedLegacyProductNames(string _, string copy)
    {
        copy.Should().NotContain("ProBalance");
        copy.Should().NotContain("IdleSaver");
        copy.Should().NotContain("SmartTrim");
    }

    [Fact]
    public void Generic_ExactWording()
    {
        UnavailableMetricCopy.Generic.Should().Be(
            "Not available on this system — Nexus shows nothing rather than estimate.");
    }

    [Fact]
    public void GpuTempAppleSiliconIdle_ExactWording()
    {
        UnavailableMetricCopy.GpuTempAppleSiliconIdle.Should().Be(
            "GPU temperature isn't reliably readable at idle on this Apple Silicon model — Nexus shows a value only when it's real.");
    }

    [Fact]
    public void CpuTempUnsupported_ExactWording()
    {
        UnavailableMetricCopy.CpuTempUnsupported.Should().Be(
            "CPU temperature isn't accessible on this hardware — Nexus shows nothing rather than guess.");
    }

    [Fact]
    public void GpuMemoryTotalMacOS_ExactWording()
    {
        UnavailableMetricCopy.GpuMemoryTotalMacOS.Should().Be(
            "This GPU doesn't report a total memory figure — Nexus shows only what's actually measured.");
    }
}
