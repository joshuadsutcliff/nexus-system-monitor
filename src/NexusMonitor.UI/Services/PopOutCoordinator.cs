using System.Linq;
using Avalonia;
using Avalonia.Controls;
using NexusMonitor.Core.Pages;
using NexusMonitor.Core.Services;
using NexusMonitor.UI.Windows;

namespace NexusMonitor.UI.Services;

/// <summary>
/// Owns the lifecycle of one DashboardViewModel's widget pop-out windows: opening them (capped,
/// cascaded or restored to remembered geometry and clamped to the live screen layout), capturing
/// final geometry when the user closes one individually, and closing every open pop-out for
/// shutdown or a workspace-profile switch while keeping their popped-out state intact.
/// <para>
/// Deliberately plain and not DI-registered: an instance is owned directly by one
/// DashboardViewModel because it needs that VM's page/save context. It never calls
/// <see cref="PageLayoutEngine.SetPopOut"/> or touches persistence itself — instead it hands
/// geometry back through the <c>onReturned</c>/<c>onPersistedForShutdown</c> constructor
/// callbacks, which the owning VM wires to its own page-edit and save path.
/// </para>
/// </summary>
public sealed class PopOutCoordinator
{
    /// <summary>Maximum simultaneously open pop-out windows. <see cref="TryPopOut"/> refuses and
    /// toasts a footprint notice once this many are already open.</summary>
    public const int MaxOpenPopOuts = 6;

    /// <summary>Size given to a newly popped-out widget with no remembered geometry — equal to
    /// <see cref="WidgetPopOutWindow"/>'s minimum size.</summary>
    private const int DefaultWidth  = 240;

    /// <summary>Size given to a newly popped-out widget with no remembered geometry — equal to
    /// <see cref="WidgetPopOutWindow"/>'s minimum size.</summary>
    private const int DefaultHeight = 160;

    /// <summary>Base cascade offset (from the main window's position, on both axes) applied to a
    /// pop-out with no remembered geometry, so it doesn't land directly on top of the main
    /// window. Each additional simultaneously-open pop-out steps a further <see cref="CascadeStepPx"/>.</summary>
    private const int CascadeBaseOffsetPx = 240;

    /// <summary>Per-window cascade step added to <see cref="CascadeBaseOffsetPx"/>, multiplied by
    /// how many pop-outs are already open.</summary>
    private const int CascadeStepPx = 32;

    private readonly Window _ownerWindow;
    private readonly object _dashboardViewModel;
    private readonly IInAppNotificationService? _notificationService;
    private readonly Action<Guid, PopOutState> _onReturned;
    private readonly Action<Guid, PopOutState> _onPersistedForShutdown;

    private readonly Dictionary<Guid, WidgetPopOutWindow> _open = new();
    private readonly HashSet<Guid> _suppressReturnOnClose = new();

    /// <summary>Currently open pop-out windows, keyed by the widget instance id they host.</summary>
    public IReadOnlyDictionary<Guid, WidgetPopOutWindow> Open => _open;

    /// <summary>
    /// Creates a coordinator for one DashboardViewModel.
    /// </summary>
    /// <param name="ownerWindow">The main window. Its live <see cref="Window.Screens"/> and its
    /// own position/size are read fresh on every <see cref="TryPopOut"/> call — never cached — so
    /// clamping always reflects the current monitor layout.</param>
    /// <param name="dashboardViewModel">The owning DashboardViewModel. Passed through to every
    /// <see cref="WidgetPopOutWindow"/> as its DataContext so the hosted widget control's
    /// inherited bindings resolve exactly as they do on the page.</param>
    /// <param name="notificationService">Toast channel used to warn when the open-pop-out cap is
    /// hit. May be null (no toast is shown; the cap is still enforced).</param>
    /// <param name="onReturned">Invoked when the user closes one pop-out window directly: receives
    /// the widget's instance id and its final geometry with <c>IsPoppedOut</c> false but
    /// <c>X</c>/<c>Y</c>/<c>Width</c>/<c>Height</c> retained, so the next pop-out of that widget
    /// reopens where it was left.</param>
    /// <param name="onPersistedForShutdown">Invoked once per open window by
    /// <see cref="PersistAndCloseAll"/>: receives the widget's instance id and its final geometry
    /// with <c>IsPoppedOut</c> still true, since the widget is conceptually still popped out —
    /// only the OS window is being torn down (app shutdown or profile switch).</param>
    public PopOutCoordinator(
        Window ownerWindow,
        object dashboardViewModel,
        IInAppNotificationService? notificationService,
        Action<Guid, PopOutState> onReturned,
        Action<Guid, PopOutState> onPersistedForShutdown)
    {
        _ownerWindow            = ownerWindow;
        _dashboardViewModel     = dashboardViewModel;
        _notificationService    = notificationService;
        _onReturned             = onReturned;
        _onPersistedForShutdown = onPersistedForShutdown;
    }

    /// <summary>
    /// Opens a pop-out window for <paramref name="widget"/>. Returns false without opening one if
    /// the open-pop-out cap (<see cref="MaxOpenPopOuts"/>) is already reached — a footprint-notice
    /// toast is shown in that case. If a window for this widget is already open, returns true
    /// without opening a second one.
    /// </summary>
    /// <param name="widget">The widget to pop out. If <see cref="WidgetInstance.PopOut"/> already
    /// holds remembered geometry with a positive Width and Height (from a previous pop-out), that
    /// geometry is reused, clamped to the live screen layout; otherwise — including a marked-popped-out
    /// widget whose geometry is all-zero (a crash between marking it and this coordinator's first
    /// capture; see <see cref="ViewModels.DashboardViewModel.PopOutWidget"/>) — the window is cascaded
    /// from the main window's position at the default size.</param>
    public bool TryPopOut(WidgetInstance widget)
    {
        if (_open.ContainsKey(widget.InstanceId))
            return true;

        if (_open.Count >= MaxOpenPopOuts)
        {
            _notificationService?.Show(new InAppNotification(
                Title:       "Too Many Pop-outs",
                Body:        $"Close one of the {MaxOpenPopOuts} open pop-out windows before opening another.",
                Severity:    InAppSeverity.Warning,
                AutoDismiss: TimeSpan.FromSeconds(6)));
            return false;
        }

        // Screen.Bounds and Window.Position are PHYSICAL pixels; Window.Width/Height are LOGICAL
        // DIPs. ScreenRect/PopOutState are standardized on physical pixels (see CaptureGeometry),
        // so the main window's logical size is converted to physical via its own RenderScaling
        // before it's used as the fallback rect or the cascade-default basis — otherwise clamp
        // math silently breaks whenever RenderScaling != 1 (e.g. a Retina display).
        var screens = _ownerWindow.Screens.All
            .Select(s => new ScreenRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))
            .ToList();
        var ownerScaling = _ownerWindow.RenderScaling;
        var fallback = new ScreenRect(
            _ownerWindow.Position.X, _ownerWindow.Position.Y,
            ToPhysical(_ownerWindow.Width, ownerScaling), ToPhysical(_ownerWindow.Height, ownerScaling));

        // Both branches clamp in physical space: remembered geometry may be stale (its monitor
        // could be gone), and a cascade default anchored near the main window can still land
        // partially off-screen close to an edge.
        //
        // Width/Height > 0 is required (not just PopOut being non-null): a widget can be marked
        // IsPoppedOut with all-zero geometry if the app crashes between PopOutWidget's initial
        // zeroed-placeholder mark and the coordinator's first CaptureGeometry (see
        // DashboardViewModel.PopOutWidget). ClampToScreens never grows a rect — ShiftTitleBarOnto
        // only shifts position, and CenterInFallback takes Math.Min(window.Width, fallback.Width) —
        // so feeding it a 0x0 rect would restore a 0x0 window instead of falling back to the normal
        // cascade default. Treating that as "no remembered geometry" recovers cleanly instead.
        var geometry = widget.PopOut is { Width: > 0, Height: > 0 } remembered
            ? WindowGeometry.ClampToScreens(
                new ScreenRect(remembered.X, remembered.Y, remembered.Width, remembered.Height),
                screens, fallback)
            : WindowGeometry.ClampToScreens(CascadeDefault(fallback), screens, fallback);

        // Position is applied in Opened, not here in the object initializer, so it takes effect
        // AFTER the native window is realized and its OS decorations (title bar) are attached —
        // symmetrical with CaptureGeometry, which likewise only ever reads Position from an
        // already-realized window (at Closing). Setting Position pre-Show instead is what caused
        // the launch-restore-only vertical drift this was fixed for: on macOS, a window's very
        // first realization in the process can attach decorations AFTER Show() in a way that
        // shifts an already-assigned Position by the title-bar's height (physical px) — a
        // fresh-session pop-out or a profile-switch restore never hit this because by then some
        // other window (at minimum the main window) had already gone through that one-time
        // decoration attachment.
        var window = new WidgetPopOutWindow(widget, _dashboardViewModel)
        {
            Width  = ToLogical(geometry.Width, ownerScaling),
            Height = ToLogical(geometry.Height, ownerScaling),
        };
        window.Closing += (_, _) => OnWindowClosing(window);
        window.Opened  += (_, _) => window.Position = new PixelPoint(geometry.X, geometry.Y);

        _open[widget.InstanceId] = window;
        window.Show();
        return true;
    }

    /// <summary>
    /// Captures each open pop-out's final geometry, hands it to the <c>onPersistedForShutdown</c>
    /// callback with <c>IsPoppedOut</c> still true, then closes the window without re-triggering
    /// <c>onReturned</c>. Call this on app shutdown or before switching workspace profiles, so
    /// pop-outs reopen in place next time rather than silently returning to the page.
    /// </summary>
    /// <remarks>
    /// Isolated per window: the suppression-flag add, the <c>onPersistedForShutdown</c> callback,
    /// and <see cref="Window.Close"/> are treated as one unit per window, and a callback throwing
    /// for one window can never stop the rest from being persisted and closed. If the callback
    /// throws for a window, that window is left open rather than closed with a stale flag, and its
    /// suppression flag is removed so a later close of it (manual or otherwise) still fires
    /// <c>onReturned</c> normally instead of silently losing its geometry.
    /// </remarks>
    public void PersistAndCloseAll()
    {
        foreach (var (instanceId, window) in _open.ToList())
        {
            _suppressReturnOnClose.Add(instanceId);
            try
            {
                _onPersistedForShutdown(instanceId, CaptureGeometry(window, isPoppedOut: true));
            }
            catch
            {
                // The callback threw before Close() ran: leave this window open (rather than
                // closing it with unpersisted state) and drop the suppression flag we just added
                // so it doesn't linger and get silently consumed by a later, unrelated close of
                // this same window. Every other open window still gets persisted and closed below.
                _suppressReturnOnClose.Remove(instanceId);
                continue;
            }

            window.Close();
        }
    }

    /// <summary>
    /// Closes the pop-out window hosting <paramref name="instanceId"/>, if one is currently open,
    /// and removes it from <see cref="Open"/>. Used when the widget itself is being removed from
    /// the page entirely (e.g. edit-mode Remove) so its OS window is never orphaned — left open
    /// with no coordinator entry and no way for the app to close it. No-op if no window is open
    /// for this id (e.g. a persisted-but-not-actually-open pop-out, such as after a crash between
    /// marking a widget popped-out and its window actually opening).
    /// </summary>
    /// <param name="suppressReturn">When true, engages the same suppression mechanism
    /// <see cref="PersistAndCloseAll"/> uses: <see cref="OnWindowClosing"/> will not invoke
    /// <c>onReturned</c> for this close. Pass true when the widget is being deleted outright —
    /// there is no page-side widget left to persist returned geometry into.</param>
    public void CloseWindow(Guid instanceId, bool suppressReturn)
    {
        if (!_open.TryGetValue(instanceId, out var window)) return;

        if (suppressReturn)
            _suppressReturnOnClose.Add(instanceId);

        window.Close(); // synchronously raises Closing → OnWindowClosing, which removes it from _open
    }

    /// <summary>Cascade placement (in physical pixels) for a widget with no remembered geometry:
    /// offset from <paramref name="fallback"/> (the main window's bounds, already physical) by
    /// <see cref="CascadeBaseOffsetPx"/> plus <see cref="CascadeStepPx"/> per already-open pop-out,
    /// at the default size — <see cref="DefaultWidth"/>/<see cref="DefaultHeight"/> are logical
    /// DIPs (matching <see cref="WidgetPopOutWindow"/>'s XAML-declared min size), so they're
    /// converted to physical pixels via the owner window's <see cref="Window.RenderScaling"/>
    /// before being placed into the returned <see cref="ScreenRect"/>.</summary>
    private ScreenRect CascadeDefault(ScreenRect fallback)
    {
        var offset = CascadeBaseOffsetPx + CascadeStepPx * _open.Count;
        var scaling = _ownerWindow.RenderScaling;
        return new ScreenRect(
            fallback.X + offset, fallback.Y + offset,
            ToPhysical(DefaultWidth, scaling), ToPhysical(DefaultHeight, scaling));
    }

    /// <summary>Shared handler wired to every pop-out window's <see cref="Window.Closing"/>:
    /// removes it from <see cref="Open"/> and, unless <see cref="PersistAndCloseAll"/> already
    /// claimed this close via the suppression set, reports it back through <c>onReturned</c>.</summary>
    private void OnWindowClosing(WidgetPopOutWindow window)
    {
        var instanceId = window.InstanceId;
        _open.Remove(instanceId);

        if (_suppressReturnOnClose.Remove(instanceId))
            return; // PersistAndCloseAll already persisted this one — don't fire onReturned too.

        _onReturned(instanceId, CaptureGeometry(window, isPoppedOut: false));
    }

    /// <summary>Reads a window's current position/size into a <see cref="PopOutState"/>, whose
    /// X/Y/Width/Height are all physical pixels. <see cref="Window.Position"/> is already
    /// physical; <see cref="Window.Width"/>/<see cref="Window.Height"/> are logical DIPs and are
    /// converted via the window's own <see cref="Window.RenderScaling"/> (falling back to the
    /// owner window's scaling if the pop-out's is unavailable, e.g. not yet realized on a
    /// screen). <c>Topmost</c> is always false — reserved, unused in v1.</summary>
    private PopOutState CaptureGeometry(WidgetPopOutWindow window, bool isPoppedOut)
    {
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : _ownerWindow.RenderScaling;
        return new(isPoppedOut, window.Position.X, window.Position.Y,
            ToPhysical(window.Width, scaling), ToPhysical(window.Height, scaling), Topmost: false);
    }

    /// <summary>Converts a logical DIP length (e.g. <see cref="Window.Width"/>/<see cref="Window.Height"/>)
    /// to physical pixels using <paramref name="scaling"/> (a <see cref="Window.RenderScaling"/> value)
    /// — the unit <see cref="ScreenRect"/> and <see cref="PopOutState"/> are standardized on.</summary>
    private static int ToPhysical(double logicalDips, double scaling) => (int)Math.Round(logicalDips * scaling);

    /// <summary>Converts a physical pixel length back to logical DIPs using <paramref name="scaling"/>
    /// (a <see cref="Window.RenderScaling"/> value), for assignment to <see cref="Window.Width"/>/
    /// <see cref="Window.Height"/>.</summary>
    private static double ToLogical(int physicalPx, double scaling) => physicalPx / scaling;
}
