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

    [Fact]
    public void EffectEnabled_NegativeSpeed_AllEffectsFalse()
    {
        // Task 1 follow-up: EffectEnabled's speed<=0 short-circuit was only ever exercised at
        // exactly 0.0 (EffectEnabled_SpeedZero_AllEffectsFalse_EvenIfToggleTrue above). A negative
        // AnimationSpeed should never occur in practice (the settings UI clamps to 0.0–2.0), but
        // the "<= 0.0" guard is written defensively — this locks that in, mirroring
        // Scale_NegativeSpeed_ReturnsZero's coverage of the analogous Scale() guard.
        var settings = new AppSettings
        {
            AnimationSpeed          = -1.0,
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

    // ── EffectEnabled — OS "Reduce Motion" runtime clamp (task brief, 2026-07-11) ─────

    [Fact]
    public void EffectEnabled_ReduceMotionTrue_AllEffectsFalse_EvenIfToggleTrueAndSpeedNonZero()
    {
        var settings = new AppSettings
        {
            AnimationSpeed          = 1.0,
            AnimatePageTransitions  = true,
            AnimateHoverEffects     = true,
            AnimatePopOutMotion     = true,
            AnimateEditChrome       = true,
            AnimateValueChanges     = true,
            AnimateSpecularShimmer  = true,
        };

        MotionMath.EffectEnabled(settings, MotionEffect.PageTransitions, reduceMotion: true).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.HoverEffects,    reduceMotion: true).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.PopOutMotion,    reduceMotion: true).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.EditChrome,      reduceMotion: true).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.ValueChanges,    reduceMotion: true).Should().BeFalse();
        MotionMath.EffectEnabled(settings, MotionEffect.SpecularShimmer, reduceMotion: true).Should().BeFalse();
    }

    [Fact]
    public void EffectEnabled_ReduceMotionFalse_ReturnsPerEffectToggle_UnaffectedByClamp()
    {
        var settings = new AppSettings { AnimationSpeed = 1.0, AnimatePageTransitions = true };
        MotionMath.EffectEnabled(settings, MotionEffect.PageTransitions, reduceMotion: false).Should().BeTrue();
    }

    [Fact]
    public void EffectEnabled_DefaultParameter_NoReduceMotionClamp_MatchesTwoArgOverloadBehavior()
    {
        // Existing callers that don't know about the OS signal (the default parameter) must be
        // completely unaffected — pins source/behavior compatibility for any two-arg call site.
        var settings = new AppSettings { AnimationSpeed = 1.0, AnimateHoverEffects = true };
        MotionMath.EffectEnabled(settings, MotionEffect.HoverEffects)
            .Should().Be(MotionMath.EffectEnabled(settings, MotionEffect.HoverEffects, reduceMotion: false));
    }

    [Fact]
    public void EffectEnabled_ReduceMotionClears_ReturnsToPerEffectToggle()
    {
        // "Signal clears -> rendered on again" precedence, mirrored from AccessibilityClampTests'
        // glass equivalent: the same settings input flips purely based on the reduceMotion arg.
        var settings = new AppSettings { AnimationSpeed = 1.0, AnimatePageTransitions = true };
        MotionMath.EffectEnabled(settings, MotionEffect.PageTransitions, reduceMotion: true)
            .Should().BeFalse("the signal was active");
        MotionMath.EffectEnabled(settings, MotionEffect.PageTransitions, reduceMotion: false)
            .Should().BeTrue("the signal cleared and AnimatePageTransitions was never touched");
    }

    // ── DepthMultiplier ──────────────────────────────────────────────────────

    [Fact]
    public void DepthMultiplier_DefaultIntensity_ReturnsOne()
    {
        // AppSettings.DepthIntensity defaults to 0.5 — the elevation tokens baked into
        // Colors.axaml ARE the 0.5 values, so 0.5 must map to a 1.0x (unchanged) multiplier.
        MotionMath.DepthMultiplier(0.5).Should().Be(1.0);
    }

    [Fact]
    public void DepthMultiplier_Zero_ReturnsZero()
    {
        // "0 = shadowless" per the Phase 8 plan.
        MotionMath.DepthMultiplier(0.0).Should().Be(0.0);
    }

    [Fact]
    public void DepthMultiplier_One_ReturnsTwo()
    {
        // "1 = 2x the default alphas" per the Phase 8 plan.
        MotionMath.DepthMultiplier(1.0).Should().Be(2.0);
    }

    [Fact]
    public void DepthMultiplier_NegativeIntensity_ClampsToZero()
    {
        // Defensive: DepthIntensity is UI-range-clamped to 0.0-1.0, but the helper itself must not
        // produce a negative multiplier (which would need a negative alpha) if ever called out of range.
        MotionMath.DepthMultiplier(-0.5).Should().Be(0.0);
    }

    [Fact]
    public void DepthMultiplier_AboveOne_ClampsToTwo()
    {
        MotionMath.DepthMultiplier(1.5).Should().Be(2.0);
    }
}
