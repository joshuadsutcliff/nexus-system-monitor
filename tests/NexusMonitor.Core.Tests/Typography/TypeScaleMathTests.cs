using FluentAssertions;
using NexusMonitor.Core.Typography;
using Xunit;

namespace NexusMonitor.Core.Tests.Typography;

/// <summary>
/// Tests for <see cref="TypeScaleMath"/> — the pure tile-area-to-scale-step math behind
/// <c>NexusMonitor.UI.Controls.DynamicTypeScale</c>. Lives in Core (with these tests) because the
/// UI assembly has no test project of its own — same rationale as <c>MotionMathTests</c>.
/// </summary>
public class TypeScaleMathTests
{
    // ── ScaleFor ─────────────────────────────────────────────────────────────

    [Fact]
    public void ScaleFor_Small_Returns0_85()
    {
        TypeScaleMath.ScaleFor(TypeScaleStep.Small).Should().Be(0.85);
    }

    [Fact]
    public void ScaleFor_Medium_Returns1_0()
    {
        TypeScaleMath.ScaleFor(TypeScaleStep.Medium).Should().Be(1.0);
    }

    [Fact]
    public void ScaleFor_Large_Returns1_15()
    {
        TypeScaleMath.ScaleFor(TypeScaleStep.Large).Should().Be(1.15);
    }

    [Fact]
    public void ScaleFor_ExtraLarge_Returns1_3()
    {
        TypeScaleMath.ScaleFor(TypeScaleStep.ExtraLarge).Should().Be(1.3);
    }

    // ── StepFor: plain classification (no currentStep — no hysteresis) ───────

    [Theory]
    [InlineData(5_388)]   // 1×1 minimum tile
    [InlineData(44_999)]  // just under the S/M boundary
    public void StepFor_NoCurrentStep_SmallArea_ReturnsSmall(double area)
    {
        TypeScaleMath.StepFor(area).Should().Be(TypeScaleStep.Small);
    }

    [Theory]
    [InlineData(45_000)]  // exactly the S/M boundary
    [InlineData(79_404)]  // factory 6×2 card (CPU/Memory/Disk/GPU) — the Medium anchor
    [InlineData(119_999)] // just under the M/L boundary
    public void StepFor_NoCurrentStep_MediumArea_ReturnsMedium(double area)
    {
        TypeScaleMath.StepFor(area).Should().Be(TypeScaleStep.Medium);
    }

    [Theory]
    [InlineData(120_000)] // exactly the M/L boundary
    [InlineData(160_680)] // factory 12×2 card (Health Score)
    [InlineData(247_200)] // factory 12×3 card (Predictions)
    [InlineData(289_999)] // just under the L/XL boundary
    public void StepFor_NoCurrentStep_LargeArea_ReturnsLarge(double area)
    {
        TypeScaleMath.StepFor(area).Should().Be(TypeScaleStep.Large);
    }

    [Theory]
    [InlineData(290_000)] // exactly the L/XL boundary
    [InlineData(333_720)] // factory 12×4 card (Bottleneck/Top Consumers/Recommendations)
    [InlineData(420_240)] // factory 12×5 card (Health Trends)
    public void StepFor_NoCurrentStep_ExtraLargeArea_ReturnsExtraLarge(double area)
    {
        TypeScaleMath.StepFor(area).Should().Be(TypeScaleStep.ExtraLarge);
    }

    // ── StepFor: hysteresis ───────────────────────────────────────────────────

    [Fact]
    public void StepFor_GrowingPastRawThreshold_DoesNotAdvance_UntilPastHysteresisBand()
    {
        // Sitting at Medium, area crosses the raw 120,000 S/M..M/L boundary but not the +5% band —
        // must NOT jump to Large yet (this is the jitter this whole class exists to prevent).
        TypeScaleMath.StepFor(122_000, TypeScaleStep.Medium).Should().Be(TypeScaleStep.Medium);
    }

    [Fact]
    public void StepFor_GrowingPastHysteresisBand_Advances()
    {
        // 120,000 * 1.05 = 126,000 — just past it should flip to Large.
        TypeScaleMath.StepFor(126_001, TypeScaleStep.Medium).Should().Be(TypeScaleStep.Large);
    }

    [Fact]
    public void StepFor_ShrinkingPastRawThreshold_DoesNotRetreat_UntilPastHysteresisBand()
    {
        // Sitting at Large, area drops back under the raw 120,000 boundary but not below the -5%
        // band — must stay Large.
        TypeScaleMath.StepFor(118_000, TypeScaleStep.Large).Should().Be(TypeScaleStep.Large);
    }

    [Fact]
    public void StepFor_ShrinkingPastHysteresisBand_Retreats()
    {
        // 120,000 * 0.95 = 114,000 — just under it should fall back to Medium.
        TypeScaleMath.StepFor(113_999, TypeScaleStep.Large).Should().Be(TypeScaleStep.Medium);
    }

    [Fact]
    public void StepFor_OscillatingAroundRawThreshold_NeverFlips()
    {
        // The exact jitter scenario: a resize settling on the raw boundary, nudging a few px
        // either side each frame. Hysteresis must hold the step steady at Medium throughout.
        var step = TypeScaleMath.StepFor(120_000, TypeScaleStep.Medium);
        step.Should().Be(TypeScaleStep.Medium);
        step = TypeScaleMath.StepFor(119_500, step);
        step.Should().Be(TypeScaleStep.Medium);
        step = TypeScaleMath.StepFor(120_500, step);
        step.Should().Be(TypeScaleStep.Medium);
        step = TypeScaleMath.StepFor(119_000, step);
        step.Should().Be(TypeScaleStep.Medium);
    }

    [Fact]
    public void StepFor_LargeOneShotGrowth_WalksAllTheWayToExtraLarge()
    {
        // A single resize from a tiny custom tile straight to the largest factory size must land
        // on ExtraLarge in one call, not require multiple StepFor invocations.
        TypeScaleMath.StepFor(420_240, TypeScaleStep.Small).Should().Be(TypeScaleStep.ExtraLarge);
    }

    [Fact]
    public void StepFor_LargeOneShotShrink_WalksAllTheWayToSmall()
    {
        TypeScaleMath.StepFor(5_388, TypeScaleStep.ExtraLarge).Should().Be(TypeScaleStep.Small);
    }

    [Fact]
    public void StepFor_UnchangedArea_ReturnsSameStep()
    {
        TypeScaleMath.StepFor(79_404, TypeScaleStep.Medium).Should().Be(TypeScaleStep.Medium);
    }

    // ── StepFor: first-push contract (gate fix bundle, Finding 2 regression pin) ─────────────

    [Fact]
    public void StepFor_FirstPushNoHysteresis_44000_ReturnsSmall_NotMedium()
    {
        // Pins the DynamicTypeScale.HostState fix: a host's very FIRST bounds push must classify
        // via the plain (non-hysteresis) thresholds — StepFor(area), i.e. currentStep: null — not
        // hysteresis-from-an-assumed-Medium-start. 44,000px² sits just under the raw 45,000 S/M
        // boundary (genuinely Small) but ABOVE the hysteresis retreat threshold from Medium
        // (45,000 * 0.95 = 42,750) — so the buggy first-push call that hysteresis-walked from an
        // assumed Medium start would incorrectly stick at Medium (asserted below as the contrast).
        TypeScaleMath.StepFor(44_000).Should().Be(TypeScaleStep.Small);
        TypeScaleMath.StepFor(44_000, TypeScaleStep.Medium).Should().Be(TypeScaleStep.Medium); // the bug this fix avoids
    }
}
