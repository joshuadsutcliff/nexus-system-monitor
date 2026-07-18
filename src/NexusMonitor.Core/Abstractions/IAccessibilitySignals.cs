namespace NexusMonitor.Core.Abstractions;

/// <summary>
/// Reads OS-level accessibility preferences that this app treats as a RUNTIME CLAMP on
/// rendering — never a settings mutation. When a signal is active, the corresponding visual
/// effect is forced off at render/apply time (see <c>NexusMonitor.Core.Motion.MotionMath.EffectEnabled</c>'s
/// <c>reduceMotion</c> parameter and <c>NexusMonitor.Core.Backdrop.BackdropMath.GetHintChain</c>'s
/// <c>reduceTransparency</c> parameter, plus <c>NexusMonitor.Core.Accessibility.AccessibilityClamp</c>
/// for Crystal Glass), but the user's own stored <see cref="Models.AppSettings"/> choice (e.g.
/// <see cref="Models.AppSettings.IsGlassEnabled"/>, <see cref="Models.AppSettings.AnimatePageTransitions"/>)
/// is left completely untouched — nothing here ever writes to <c>AppSettings</c> or
/// <c>settings.json</c>. The effect resumes automatically the moment the OS signal clears,
/// subject to each platform implementation's own read timing (see that implementation's doc
/// comment for whether the read is live on every access or cached at startup/restart-scoped).
///
/// Honest-failure convention: any OS read that fails degrades to <see langword="false"/> (assume
/// no clamp active) — implementations of this interface never throw.
/// </summary>
public interface IAccessibilitySignals
{
    /// <summary>True when the OS "Reduce Motion" accessibility preference is currently enabled.</summary>
    bool ReduceMotion { get; }

    /// <summary>True when the OS "Reduce Transparency" accessibility preference is currently enabled.</summary>
    bool ReduceTransparency { get; }
}

/// <summary>
/// Honest fallback for platforms with no known OS accessibility signal (Linux — see the Hosting
/// DI registration comment) and for mock/design-time builds. Both signals always read as off,
/// i.e. never clamps anything — the same "no known signal, so no claim" honesty used elsewhere
/// in this codebase (e.g. <c>NullSleepPreventionProvider</c>).
/// </summary>
public sealed class NullAccessibilitySignals : IAccessibilitySignals
{
    public bool ReduceMotion => false;
    public bool ReduceTransparency => false;
}
