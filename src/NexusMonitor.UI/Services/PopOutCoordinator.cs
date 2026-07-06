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
    /// holds remembered geometry (X/Y/Width/Height from a previous pop-out), that geometry is
    /// reused, clamped to the live screen layout; otherwise the window is cascaded from the main
    /// window's position at the default size.</param>
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

        var screens = _ownerWindow.Screens.All
            .Select(s => new ScreenRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))
            .ToList();
        var fallback = new ScreenRect(
            _ownerWindow.Position.X, _ownerWindow.Position.Y,
            (int)_ownerWindow.Width, (int)_ownerWindow.Height);

        var geometry = widget.PopOut is { } remembered
            ? WindowGeometry.ClampToScreens(
                new ScreenRect(remembered.X, remembered.Y, remembered.Width, remembered.Height),
                screens, fallback)
            : CascadeDefault(fallback);

        var window = new WidgetPopOutWindow(widget, _dashboardViewModel)
        {
            Position = new PixelPoint(geometry.X, geometry.Y),
            Width    = geometry.Width,
            Height   = geometry.Height,
        };
        window.Closing += (_, _) => OnWindowClosing(window);

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
    public void PersistAndCloseAll()
    {
        foreach (var (instanceId, window) in _open.ToList())
        {
            _suppressReturnOnClose.Add(instanceId);
            _onPersistedForShutdown(instanceId, CaptureGeometry(window, isPoppedOut: true));
            window.Close();
        }
    }

    /// <summary>Cascade placement for a widget with no remembered geometry: offset from
    /// <paramref name="fallback"/> (the main window's bounds) by <see cref="CascadeBaseOffsetPx"/>
    /// plus <see cref="CascadeStepPx"/> per already-open pop-out, at the default size.</summary>
    private ScreenRect CascadeDefault(ScreenRect fallback)
    {
        var offset = CascadeBaseOffsetPx + CascadeStepPx * _open.Count;
        return new ScreenRect(fallback.X + offset, fallback.Y + offset, DefaultWidth, DefaultHeight);
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

    /// <summary>Reads a window's current position/size into a <see cref="PopOutState"/>.
    /// <c>Topmost</c> is always false — reserved, unused in v1.</summary>
    private static PopOutState CaptureGeometry(WidgetPopOutWindow window, bool isPoppedOut) =>
        new(isPoppedOut, window.Position.X, window.Position.Y,
            (int)window.Width, (int)window.Height, Topmost: false);
}
