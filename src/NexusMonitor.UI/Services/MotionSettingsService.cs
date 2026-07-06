using Avalonia;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Motion;

namespace NexusMonitor.UI.Services;

/// <summary>
/// Applies AppSettings' animation/motion preferences to the app's live Avalonia resource
/// dictionary and exposes per-effect gating for XAML/code consumers.
///
/// <see cref="Apply"/> writes three duration tokens into <see cref="Application.Current"/>'s
/// resources — <c>MotionFast</c>/<c>MotionBase</c>/<c>MotionSlow</c> (each a <see cref="TimeSpan"/>,
/// base values 120/180/280 ms) — scaled by <see cref="AppSettings.AnimationSpeed"/> via
/// <see cref="MotionMath.Scale"/>. Any Transition bound to these via DynamicResource picks up the
/// new value on the next resource lookup. <c>Themes/Motion.axaml</c> seeds the same three keys
/// with their 1.0x defaults so design-time/XAML previews never hit a missing-resource lookup
/// before this service has run.
///
/// AnimationSpeed == 0 collapses all three tokens to <see cref="TimeSpan.Zero"/> AND makes
/// <see cref="EffectEnabled"/> return false for every <see cref="MotionEffect"/>, regardless of
/// the individual per-effect toggle — "0 = everything instant" per the Phase 8 UI-polish plan.
///
/// The duration-scaling and per-effect gating math itself lives in
/// <see cref="NexusMonitor.Core.Motion.MotionMath"/> (Core), which carries the unit tests for
/// this logic — the UI assembly has no test project of its own.
/// </summary>
public sealed class MotionSettingsService
{
    private static readonly TimeSpan FastBaseDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan BaseBaseDuration  = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SlowBaseDuration  = TimeSpan.FromMilliseconds(280);

    /// <summary>Raised after <see cref="Apply"/> has written the recomputed durations into
    /// <see cref="Application.Current"/>'s resources.</summary>
    public event Action? MotionChanged;

    /// <summary>
    /// Recomputes <c>MotionFast</c>/<c>MotionBase</c>/<c>MotionSlow</c> from
    /// <paramref name="settings"/>.AnimationSpeed and writes them into
    /// <see cref="Application.Current"/>'s resource dictionary, then raises
    /// <see cref="MotionChanged"/>. No-ops (and does not raise the event) if
    /// <see cref="Application.Current"/> is null — e.g. if ever called before the Avalonia
    /// application instance exists.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        resources["MotionFast"] = MotionMath.Scale(FastBaseDuration, settings.AnimationSpeed);
        resources["MotionBase"] = MotionMath.Scale(BaseBaseDuration, settings.AnimationSpeed);
        resources["MotionSlow"] = MotionMath.Scale(SlowBaseDuration, settings.AnimationSpeed);

        MotionChanged?.Invoke();
    }

    /// <summary>
    /// True when <paramref name="effect"/> should currently animate for <paramref name="settings"/>:
    /// false for every effect when AnimationSpeed is 0 (all motion off), otherwise the effect's
    /// own <c>Animate*</c> toggle. Forwards to <see cref="MotionMath.EffectEnabled"/>.
    /// </summary>
    public static bool EffectEnabled(AppSettings settings, MotionEffect effect) =>
        MotionMath.EffectEnabled(settings, effect);
}
