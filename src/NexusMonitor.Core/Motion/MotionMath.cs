using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Motion;

/// <summary>
/// Named animation categories individually gated by the corresponding
/// <c>AppSettings.Animate*</c> toggle. Consumed by <see cref="MotionMath.EffectEnabled"/> and
/// (Task 3) by every migrated Transition site to decide whether that category should animate.
/// </summary>
public enum MotionEffect
{
    /// <summary>Dashboard tab/page cross-fade transitions.</summary>
    PageTransitions,
    /// <summary>Button/control hover-state brush and lift transitions.</summary>
    HoverEffects,
    /// <summary>Widget pop-out window open/close scale motion.</summary>
    PopOutMotion,
    /// <summary>Edit-mode chrome and widget-gallery fade transitions.</summary>
    EditChrome,
    /// <summary>Animated numeric value changes on widget tiles.</summary>
    ValueChanges,
    /// <summary>Crystal Glass specular shimmer sweep timer.</summary>
    SpecularShimmer,
}

/// <summary>
/// Pure motion-token math backing <c>NexusMonitor.UI.Services.MotionSettingsService</c>: duration
/// scaling and per-effect enablement gating.
///
/// Lives in Core (with unit test coverage) rather than in the UI assembly's service class because
/// NexusMonitor.UI has no test project of its own — this is the "Core-adjacent logic in a UI
/// assembly" carve-out. MotionSettingsService.Apply/EffectEnabled call straight through to these
/// members; no behavior is duplicated.
/// </summary>
public static class MotionMath
{
    /// <summary>
    /// Scales <paramref name="baseDuration"/> by <paramref name="speed"/>: result =
    /// <paramref name="baseDuration"/> ÷ <paramref name="speed"/>, so a higher speed yields a
    /// shorter (faster) duration. Any non-positive <paramref name="speed"/> (including exactly
    /// 0, which means "animations off" per the Phase 8 plan) yields <see cref="TimeSpan.Zero"/>
    /// rather than dividing by zero or flipping the sign.
    /// </summary>
    /// <param name="baseDuration">The reference duration at 1.0x speed (e.g. the 120/180/280 ms
    /// MotionFast/Base/Slow tokens).</param>
    /// <param name="speed">The user's <see cref="AppSettings.AnimationSpeed"/> multiplier
    /// (expected range 0.0–2.0).</param>
    public static TimeSpan Scale(TimeSpan baseDuration, double speed)
    {
        if (speed <= 0.0) return TimeSpan.Zero;
        return baseDuration / speed;
    }

    /// <summary>
    /// Returns whether <paramref name="effect"/> should currently animate. When
    /// <see cref="AppSettings.AnimationSpeed"/> is 0 (or negative), every effect is disabled
    /// regardless of its own toggle — "0 = everything instant" per the Phase 8 plan. Otherwise
    /// returns the effect's own <c>Animate*</c> toggle from <paramref name="settings"/>.
    /// </summary>
    public static bool EffectEnabled(AppSettings settings, MotionEffect effect)
    {
        if (settings.AnimationSpeed <= 0.0) return false;

        return effect switch
        {
            MotionEffect.PageTransitions => settings.AnimatePageTransitions,
            MotionEffect.HoverEffects    => settings.AnimateHoverEffects,
            MotionEffect.PopOutMotion    => settings.AnimatePopOutMotion,
            MotionEffect.EditChrome      => settings.AnimateEditChrome,
            MotionEffect.ValueChanges    => settings.AnimateValueChanges,
            MotionEffect.SpecularShimmer => settings.AnimateSpecularShimmer,
            _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unknown MotionEffect."),
        };
    }
}
