namespace NexusMonitor.UI.Services;

/// <summary>
/// Abstraction over the three window operations involved in restoring a window from the tray
/// (Show, set WindowState to Normal, Activate). Exists purely so the ORDER in which these three
/// calls happen (issue #43) can be unit-tested without instantiating a real Avalonia
/// <c>Window</c>, which requires a configured Avalonia application and cannot be constructed in
/// a plain xUnit test in this project (no Avalonia.Headless dependency here). Production code
/// implements this against a real <c>Window</c>; tests implement it against a recording fake.
/// </summary>
public interface IWindowRestoreTarget
{
    void Show();
    void SetWindowStateNormal();
    void Activate();
}

/// <summary>
/// Issue #43 (KDE tray left-click doesn't restore a minimized window): the ORDER of these three
/// calls matters and is the entire bug. The previous code called Activate() before forcing
/// WindowState to Normal (and only did so conditionally, when already Minimized). On
/// X11/XWayland, activating a still-iconic window is a no-op at the window-manager level — the
/// SNI "Activate" D-Bus call returns OK, but per WM_STATE the window stays Iconic, because the
/// WM won't raise/focus a window that hasn't been mapped/restored yet. `xdotool windowactivate`
/// against the same window works, confirming the window itself is perfectly restorable and only
/// the call sequence was wrong.
///
/// The correct order, applied here and shared by both the tray icon's Clicked handler and the
/// "Show Nexus Monitor" menu item (previously duplicated with the same bug in both places):
///   1. Show() first — covers the hidden-to-tray case, where the window isn't just minimized,
///      it's not shown at all.
///   2. WindowState = Normal second, and UNCONDITIONALLY (not gated on "if Minimized") — a
///      window hidden to tray can report a non-Minimized WindowState while still not being
///      presented, and setting Normal when it's already Normal is a harmless no-op.
///   3. Activate() LAST, once the window is mapped and non-iconic, so the WM actually has
///      something focusable to raise. This ordering is valid on Windows and macOS too (both
///      already work today), so there is no platform branching here.
/// </summary>
public static class WindowRestoreOperations
{
    /// <summary>
    /// Applies the Show -> Normal -> Activate sequence to <paramref name="target"/>. See the
    /// class remarks for why this exact order is required (issue #43).
    /// </summary>
    public static void Restore(IWindowRestoreTarget target)
    {
        target.Show();
        target.SetWindowStateNormal();
        target.Activate();
    }
}
