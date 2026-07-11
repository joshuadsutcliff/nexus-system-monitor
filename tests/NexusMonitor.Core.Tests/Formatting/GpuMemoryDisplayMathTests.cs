using FluentAssertions;
using NexusMonitor.Core.Formatting;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="GpuMemoryDisplayMath"/> — the pure "used / total" GPU-memory display-format
/// logic behind <c>NexusMonitor.UI.ViewModels.GpuDeviceViewModel.SubValueDisplay</c>/
/// <c>DedicatedDisplay</c> and <c>NexusMonitor.UI.ViewModels.PerformanceViewModel.GpuMemDisplay</c>
/// (Sym-2 Task 6's GPU-memory work plus its zero-total display follow-up fix). Lives in Core (with
/// these tests) because the UI assembly has no test project of its own — the same "Core-adjacent
/// logic in a UI assembly" carve-out <c>BackdropMathTests</c>/<c>MotionMathTests</c>/
/// <c>TypeScaleMathTests</c> already document.
/// </summary>
public class GpuMemoryDisplayMathTests
{
    // ── Real total (> 0) — "used / total GB" branch ────────────────────────────

    [Fact]
    public void FormatUsedTotal_RealTotal_RendersUsedSlashTotal()
    {
        GpuMemoryDisplayMath.FormatUsedTotal(usedGb: 0.4, totalGb: 16.0)
            .Should().Be("0.4 / 16 GB");
    }

    [Theory]
    [InlineData(0.0, 16.0, "0.0 / 16 GB")]
    [InlineData(8.0, 16.0, "8.0 / 16 GB")]
    [InlineData(15.96, 16.0, "16.0 / 16 GB")] // F1 rounding on used, F0 rounding (truncating format) on total
    [InlineData(0.05, 8.0, "0.1 / 8 GB")]
    public void FormatUsedTotal_RealTotal_FormatsUsedToOneDecimalAndTotalToWholeNumber(
        double usedGb, double totalGb, string expected)
    {
        GpuMemoryDisplayMath.FormatUsedTotal(usedGb, totalGb).Should().Be(expected);
    }

    // ── Zero/unknown total (honest-unavailable) — used-only branch ─────────────

    [Fact]
    public void FormatUsedTotal_ZeroTotal_RendersUsedOnly_NoSlashNoZero()
    {
        // The regression this exists to prevent: Apple Silicon unified memory has no dedicated
        // VRAM pool to report a total for (DedicatedMemoryTotalBytes is honestly 0 there). Before
        // the Sym-2 Task 6 follow-up fix, this rendered "0.4 / 0 GB" — reads as a fabricated
        // zero-capacity pool. The used-only branch must never contain "/" or a bare "0 GB".
        var result = GpuMemoryDisplayMath.FormatUsedTotal(usedGb: 0.4, totalGb: 0.0);

        result.Should().Be("0.4 GB");
        result.Should().NotContain("/");
        result.Should().NotContain("0 GB", "a zero-total figure must never appear in the used-only branch");
    }

    [Theory]
    [InlineData(0.0, "0.0 GB")]
    [InlineData(12.3, "12.3 GB")]
    public void FormatUsedTotal_ZeroTotal_FormatsUsedToOneDecimal(double usedGb, string expected)
    {
        GpuMemoryDisplayMath.FormatUsedTotal(usedGb, totalGb: 0.0).Should().Be(expected);
    }

    // ── Boundary: total = 0 vs total > 0 is the entire branch decision ─────────

    [Fact]
    public void FormatUsedTotal_Boundary_TotalExactlyZero_TakesUsedOnlyBranch()
    {
        GpuMemoryDisplayMath.FormatUsedTotal(usedGb: 1.0, totalGb: 0.0)
            .Should().Be("1.0 GB")
            .And.NotContain("/");
    }

    [Fact]
    public void FormatUsedTotal_Boundary_SmallestPositiveTotal_TakesUsedSlashTotalBranch()
    {
        // Any positive total — however small — is a real, honestly-reported capacity and must
        // take the "used / total" branch, not the zero/unknown-total fallback. The branch
        // decision is `totalGb > 0`, not a plausibility/rounding threshold.
        GpuMemoryDisplayMath.FormatUsedTotal(usedGb: 1.0, totalGb: 0.1)
            .Should().Be("1.0 / 0 GB") // F0 rounds 0.1 down to "0" — still the used/total branch, just a tiny total
            .And.Contain("/");
    }

    [Fact]
    public void FormatUsedTotal_Boundary_NegativeTotal_TreatedAsZeroUnknownBranch()
    {
        // Defensive: a negative total should never occur upstream (memory-byte sanitization
        // clamps negatives to 0 before this is reached), but the branch condition (`> 0`) already
        // handles it the same as honestly-unknown rather than rendering a nonsensical
        // "used / -N GB".
        GpuMemoryDisplayMath.FormatUsedTotal(usedGb: 1.0, totalGb: -5.0)
            .Should().Be("1.0 GB")
            .And.NotContain("/");
    }

    // ── Suffix parameters — SubValueDisplay's distinct wording per branch ──────

    [Fact]
    public void FormatUsedTotal_WithTotalSuffix_RealTotal_AppendsSuffixAfterGB()
    {
        // Matches GpuDeviceViewModel.SubValueDisplay's real-total wording exactly.
        GpuMemoryDisplayMath.FormatUsedTotal(0.4, 16.0, totalSuffix: " VRAM", zeroTotalSuffix: " GPU memory")
            .Should().Be("0.4 / 16 GB VRAM");
    }

    [Fact]
    public void FormatUsedTotal_WithZeroTotalSuffix_ZeroTotal_AppendsSuffixAfterGB()
    {
        // Matches GpuDeviceViewModel.SubValueDisplay's zero-total wording exactly.
        GpuMemoryDisplayMath.FormatUsedTotal(0.4, 0.0, totalSuffix: " VRAM", zeroTotalSuffix: " GPU memory")
            .Should().Be("0.4 GB GPU memory");
    }

    [Fact]
    public void FormatUsedTotal_NoSuffixArgs_RealTotal_MatchesDedicatedDisplayAndGpuMemDisplayWording()
    {
        // Matches GpuDeviceViewModel.DedicatedDisplay and PerformanceViewModel.GpuMemDisplay,
        // which call FormatUsedTotal with no suffixes (bare "used / total GB").
        GpuMemoryDisplayMath.FormatUsedTotal(0.4, 16.0)
            .Should().Be("0.4 / 16 GB");
    }

    [Fact]
    public void FormatUsedTotal_NoSuffixArgs_ZeroTotal_MatchesDedicatedDisplayAndGpuMemDisplayWording()
    {
        GpuMemoryDisplayMath.FormatUsedTotal(0.4, 0.0)
            .Should().Be("0.4 GB");
    }
}
