using FluentAssertions;
using NexusMonitor.Core.Services;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class UpdateVersionComparerTests
{
    // ── TryCompare ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.6.0",     "0.5.2",   true)]   // newer patch-level bump
    [InlineData("0.5.2",     "0.5.2",   false)]  // equal
    [InlineData("0.5.1",     "0.5.2",   false)]  // older
    [InlineData("v0.6.0",    "0.5.2",   true)]   // "v" prefix on latest tag
    [InlineData("0.6.0",     "v0.5.2",  true)]   // "v" prefix on running version
    [InlineData("V0.6.0",    "0.5.2",   true)]   // uppercase "V" prefix
    [InlineData("0.1.8.2",   "0.1.8.1", true)]   // 4-part newer
    [InlineData("0.1.8.1",   "0.1.8.2", false)]  // 4-part older
    [InlineData("1.0.0.0",   "1.0.0",   false)]  // 4-part equal to 3-part (trailing zero)
    [InlineData("1.0.0.1",   "1.0.0",   true)]   // 4-part newer than 3-part
    [InlineData("1.2.3-beta.1+build5", "1.2.2", true)] // pre-release/build metadata stripped, still compares
    public void TryCompare_ProducesExpectedResult(string latestTag, string running, bool expectedNewer)
    {
        var ok = UpdateVersionComparer.TryCompare(latestTag, running, out var isNewer);

        ok.Should().BeTrue();
        isNewer.Should().Be(expectedNewer);
    }

    [Fact]
    public void TryCompare_MalformedLatestTag_ReturnsFalse_AndDoesNotThrow()
    {
        Action act = () => UpdateVersionComparer.TryCompare("not-a-version", "0.5.2", out _);

        act.Should().NotThrow();
        UpdateVersionComparer.TryCompare("not-a-version", "0.5.2", out var isNewer).Should().BeFalse();
        isNewer.Should().BeFalse();
    }

    [Fact]
    public void TryCompare_MalformedRunningVersion_ReturnsFalse_AndDoesNotThrow()
    {
        UpdateVersionComparer.TryCompare("0.6.0", "garbage", out var isNewer).Should().BeFalse();
        isNewer.Should().BeFalse();
    }

    // ── TryParse ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData("1.2.abc")]
    [InlineData("1.2.3.4.5.x")]
    public void TryParse_MalformedInput_ReturnsFalse_WithoutThrowing(string? raw)
    {
        Action act = () => UpdateVersionComparer.TryParse(raw, out _);

        act.Should().NotThrow();
        UpdateVersionComparer.TryParse(raw, out var parts).Should().BeFalse();
        parts.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_StripsPreReleaseAndBuildMetadata()
    {
        UpdateVersionComparer.TryParse("1.2.3-beta.1+buildinfo", out var parts).Should().BeTrue();
        parts.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void TryParse_FourPartVersion_ParsesAllSegments()
    {
        UpdateVersionComparer.TryParse("0.1.8.2", out var parts).Should().BeTrue();
        parts.Should().Equal(0, 1, 8, 2);
    }

    // ── Compare ────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_MissingTrailingSegmentsTreatedAsZero()
    {
        UpdateVersionComparer.Compare(new[] { 1, 0 }, new[] { 1, 0, 0, 0 }).Should().Be(0);
    }

    [Fact]
    public void Compare_ShorterArrayWithHigherLeadingSegment_IsNewer()
    {
        UpdateVersionComparer.Compare(new[] { 2 }, new[] { 1, 9, 9, 9 }).Should().BePositive();
    }

    // ── StripPrefix ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("v0.6.0", "0.6.0")]
    [InlineData("V0.6.0", "0.6.0")]
    [InlineData("0.6.0",  "0.6.0")]
    [InlineData("",       "")]
    public void StripPrefix_RemovesLeadingV(string input, string expected)
    {
        UpdateVersionComparer.StripPrefix(input).Should().Be(expected);
    }
}
