namespace NexusMonitor.Core.Accessibility;

/// <summary>
/// Pure precedence logic for the OS "Reduce Transparency" accessibility clamp on Crystal Glass.
/// Computes the EFFECTIVE (rendered) glass-enabled state from the user's own stored
/// <c>AppSettings.IsGlassEnabled</c> choice and the live <c>IAccessibilitySignals.ReduceTransparency</c>
/// signal, without ever mutating the stored setting itself.
///
/// Lives in Core (with unit test coverage) for the same "Core-adjacent logic in a UI assembly"
/// reason as <c>NexusMonitor.Core.Motion.MotionMath</c> and <c>NexusMonitor.Core.Backdrop.BackdropMath</c>:
/// <c>NexusMonitor.UI.ViewModels.SettingsViewModel.ApplyGlass</c>/<c>ApplySpecular</c> call
/// straight through to this; no precedence logic is duplicated there.
/// </summary>
public static class AccessibilityClamp
{
    /// <summary>
    /// True only when the user's own glass setting is on AND the OS is not asking for reduced
    /// transparency. <paramref name="reduceTransparency"/> always wins when active — it forces
    /// the EFFECTIVE glass state off at render time regardless of <paramref name="glassEnabled"/>
    /// — but this never flips <paramref name="glassEnabled"/> itself; callers must keep
    /// persisting/displaying the user's own setting untouched and only use this return value for
    /// the rendered effect.
    /// </summary>
    public static bool EffectiveGlassEnabled(bool glassEnabled, bool reduceTransparency) =>
        glassEnabled && !reduceTransparency;
}
