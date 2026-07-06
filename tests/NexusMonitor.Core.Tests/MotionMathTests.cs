using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Motion;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MotionMath"/> — the pure duration-scaling and per-effect gating math
/// behind <c>NexusMonitor.UI.Services.MotionSettingsService</c>. Lives in Core (with these tests)
/// because the UI assembly has no test project of its own.
/// </summary>
public class MotionMathTests
{
    // ── Scale ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scale_SpeedOne_ReturnsBaseDurationUnchanged()
    {
        MotionMath.Scale(TimeSpan.FromMilliseconds(120), 1.0)
            .Should().Be(TimeSpan.FromMilliseconds(120));
    }

    [Fact]
    public void Scale_SpeedTwo_HalvesDuration()
    {
        MotionMath.Scale(TimeSpan.FromMilliseconds(120), 2.0)
            .Should().Be(TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public void Scale_SpeedHalf_DoublesDuration()
    {
        MotionMath.Scale(TimeSpan.FromMilliseconds(120), 0.5)
            .Should().Be(TimeSpan.FromMilliseconds(240));
    }

    [Fact]
    public void Scale_SpeedZero_ReturnsZero()
    {
        MotionMath.Scale(TimeSpan.FromMilliseconds(280), 0.0)
            .Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Scale_NegativeSpeed_ReturnsZero()
    {
        // Defensive: AppSettings.AnimationSpeed is UI-range-clamped to 0.0–2.0, but the helper
        // itself must not produce a negative or overflowing duration if ever called out of range.
        MotionMath.Scale(TimeSpan.FromMilliseconds(180), -1.0)
            .Should().Be(TimeSpan.Zero);
    }

    // ── EffectEnabled ────────────────────────────────────────────────────────

    [Fact]
    public void EffectEnabled_SpeedZero_AllEffectsFalse_EvenIfToggleTrue()
    {
        var settings = new AppSettings
        {
            AnimationSpeed          = 0.0,
            AnimatePageTransitions  = true,
            AnimateHoverEffects     = true,
            AnimatePopOutMotion     = true,
            AnimateEditChrome       = true,
            AnimateValueChanges     = true,
            AnimateSpecularShimmer  = true,
        };

        MotionMath.EffectEnabled(settings, MotionEffect.PageTransitions).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.HoverEffects).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.PopOutMotion).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.EditChrome).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.ValueChanges).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.SpecularShimmer).Should().BeFalse();
    }

    [Fact]
    public void EffectEnabled_SpeedNonZero_ReturnsPerEffectToggle()
    {
        var settings = new AppSettings
        {
            AnimationSpeed          = 1.0,
            AnimatePageTransitions  = true,
            AnimateHoverEffects     = false,
            AnimatePopOutMotion     = true,
            AnimateEditChrome       = false,
            AnimateValueChanges     = true,
            AnimateSpecularShimmer  = false,
        };

        MotionMath.EffectEnabled(settings, MotionEffect.PageTransitions).Should().BeTrue();
        MotionMath.EffectEnabled(settings, MotionEffect.HoverEffects).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.PopOutMotion).Should().BeTrue();
        MotionMath.EffectEnabled(settings, MotionEffect.EditChrome).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.ValueChanges).Should().BeTrue();
        MotionMath.EffectEnabled(settings, MotionEffect.SpecularShimmer).Should().BeFalse();
    }
}
