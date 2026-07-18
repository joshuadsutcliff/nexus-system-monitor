using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Motion;

namespace NexusMonitor.UI.Services;

/// <summary>
/// Applies AppSettings' animation/motion/depth preferences to the app's live Avalonia resource
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
/// <see cref="Apply"/> also writes four elevation tokens — <c>ElevationRaised</c>/
/// <c>ElevationFloating</c>/<c>ElevationModal</c>/<c>ElevationToast</c> (each a
/// <see cref="BoxShadows"/>) — scaled by <see cref="AppSettings.DepthIntensity"/> via
/// <see cref="MotionMath.DepthMultiplier"/>: each shadow stop's alpha channel is multiplied by the
/// resulting 0.0–2.0x factor (0 = shadowless, 0.5 = unchanged/baked-in default, 1.0 = 2x).
/// <c>ElevationToast</c> (Phase 8 Task 3 carryover A) is a dedicated, lighter token for
/// <c>NotificationToast</c> alone, restoring its original pre-Task-2 shadow weight — see that
/// task's report for why it had briefly been consolidated onto (and made heavier by)
/// <c>ElevationModal</c>. The BASE shadow values scaled from are this class's own
/// <see cref="DarkElevationRaisedBase"/>/<see cref="LightElevationRaisedBase"/> (and siblings)
/// constants — picked by <see cref="Application.RequestedThemeVariant"/> — NOT read back from
/// <see cref="Application.Current"/>'s resources, so repeated <see cref="Apply"/> calls (e.g. a
/// live DepthIntensity-slider wiring, added in Phase 8 Task 3) always recompute from the same
/// fixed base instead of compounding an already-scaled value. <c>Themes/Colors.axaml</c>'s
/// per-theme <c>ElevationRaised</c>/<c>Floating</c>/<c>Modal</c>/<c>Toast</c> ThemeDictionary
/// entries carry the identical numeric values as design-time/preview seeds (same relationship as
/// <c>Themes/Motion.axaml</c> to the duration constants above) and are otherwise unused once this
/// service has run.
///
/// The duration-scaling, depth-multiplier, and per-effect gating math itself lives in
/// <see cref="NexusMonitor.Core.Motion.MotionMath"/> (Core), which carries the unit tests for
/// this logic — the UI assembly has no test project of its own.
/// </summary>
public sealed class MotionSettingsService
{
    private readonly IAccessibilitySignals _accessibilitySignals;

    public MotionSettingsService(IAccessibilitySignals accessibilitySignals)
    {
        _accessibilitySignals = accessibilitySignals;
    }

    private static readonly TimeSpan FastBaseDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan BaseBaseDuration  = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SlowBaseDuration  = TimeSpan.FromMilliseconds(280);

    // Elevation scaling bases — byte-for-byte identical to Themes/Colors.axaml's ThemeDictionary
    // seeds (see that file's comments for the packet citation/rationale). Hardcoded here rather
    // than read back from Application.Current's resources so Apply() is idempotent under repeat
    // calls (see class doc).
    private static readonly BoxShadows DarkElevationRaisedBase   = BoxShadows.Parse("0 1 2 0 #14000000");
    private static readonly BoxShadows DarkElevationFloatingBase = BoxShadows.Parse("0 3 10 0 #2E000000, 0 1 3 0 #1F000000");
    private static readonly BoxShadows DarkElevationModalBase    = BoxShadows.Parse("0 8 40 0 #66000000, 0 2 8 0 #33000000");
    // Phase 8 Task 3 carryover A: NotificationToast's own original (pre-Task-2) shadow weight —
    // deliberately lighter than ElevationModal, and theme-invariant like it (see Colors.axaml).
    private static readonly BoxShadows DarkElevationToastBase    = BoxShadows.Parse("0 4 24 0 #44000000, 0 1 4 0 #22000000");

    private static readonly BoxShadows LightElevationRaisedBase   = BoxShadows.Parse("0 1 3 0 #1F000000, 0 2 6 0 #14000000");
    private static readonly BoxShadows LightElevationFloatingBase = BoxShadows.Parse("0 4 14 0 #29000000, 0 1 4 0 #1A000000");
    private static readonly BoxShadows LightElevationModalBase    = BoxShadows.Parse("0 8 40 0 #66000000, 0 2 8 0 #33000000");
    private static readonly BoxShadows LightElevationToastBase    = BoxShadows.Parse("0 4 24 0 #44000000, 0 1 4 0 #22000000");

    /// <summary>Raised after <see cref="Apply"/> has written the recomputed durations into
    /// <see cref="Application.Current"/>'s resources.</summary>
    public event Action? MotionChanged;

    /// <summary>
    /// Recomputes <c>MotionFast</c>/<c>MotionBase</c>/<c>MotionSlow</c> from
    /// <paramref name="settings"/>.AnimationSpeed and <c>ElevationRaised</c>/<c>Floating</c>/
    /// <c>Modal</c> from <paramref name="settings"/>.DepthIntensity, writes them into
    /// <see cref="Application.Current"/>'s resource dictionary, then raises
    /// <see cref="MotionChanged"/>. No-ops (and does not raise the event) if
    /// <see cref="Application.Current"/> is null — e.g. if ever called before the Avalonia
    /// application instance exists.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        var app = Application.Current;
        var resources = app?.Resources;
        if (app is null || resources is null) return;

        resources["MotionFast"] = MotionMath.Scale(FastBaseDuration, settings.AnimationSpeed);
        resources["MotionBase"] = MotionMath.Scale(BaseBaseDuration, settings.AnimationSpeed);
        resources["MotionSlow"] = MotionMath.Scale(SlowBaseDuration, settings.AnimationSpeed);

        var isLight = app.RequestedThemeVariant == ThemeVariant.Light;
        var multiplier = MotionMath.DepthMultiplier(settings.DepthIntensity);
        resources["ElevationRaised"]   = ScaleShadowAlpha(isLight ? LightElevationRaisedBase   : DarkElevationRaisedBase,   multiplier);
        resources["ElevationFloating"] = ScaleShadowAlpha(isLight ? LightElevationFloatingBase : DarkElevationFloatingBase, multiplier);
        resources["ElevationModal"]    = ScaleShadowAlpha(isLight ? LightElevationModalBase    : DarkElevationModalBase,    multiplier);
        resources["ElevationToast"]    = ScaleShadowAlpha(isLight ? LightElevationToastBase    : DarkElevationToastBase,    multiplier);

        MotionChanged?.Invoke();
    }

    /// <summary>Returns a copy of <paramref name="source"/> with every shadow stop's <see
    /// cref="Color.A"/> multiplied by <paramref name="multiplier"/> (clamped to a valid byte) —
    /// offsets, blur, spread, and inset are left untouched.</summary>
    private static BoxShadows ScaleShadowAlpha(BoxShadows source, double multiplier)
    {
        if (source.Count == 0) return source;

        var stops = new BoxShadow[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var stop = source[i];
            var scaledAlpha = (byte)Math.Clamp(stop.Color.A * multiplier, 0, 255);
            stops[i] = new BoxShadow
            {
                OffsetX = stop.OffsetX,
                OffsetY = stop.OffsetY,
                Blur    = stop.Blur,
                Spread  = stop.Spread,
                Color   = new Color(scaledAlpha, stop.Color.R, stop.Color.G, stop.Color.B),
                IsInset = stop.IsInset,
            };
        }

        return stops.Length == 1 ? new BoxShadows(stops[0]) : new BoxShadows(stops[0], stops[1..]);
    }

    /// <summary>
    /// True when <paramref name="effect"/> should currently animate for <paramref name="settings"/>:
    /// false for every effect when AnimationSpeed is 0 (all motion off) OR the OS "Reduce Motion"
    /// accessibility signal is active (a runtime clamp — see <see cref="IAccessibilitySignals"/> —
    /// that never mutates <paramref name="settings"/>), otherwise the effect's own <c>Animate*</c>
    /// toggle. Forwards to <see cref="MotionMath.EffectEnabled"/>. Instance method (not static)
    /// specifically so it can read the DI-injected <see cref="IAccessibilitySignals"/>.
    /// </summary>
    public bool EffectEnabled(AppSettings settings, MotionEffect effect) =>
        MotionMath.EffectEnabled(settings, effect, _accessibilitySignals.ReduceMotion);
}
