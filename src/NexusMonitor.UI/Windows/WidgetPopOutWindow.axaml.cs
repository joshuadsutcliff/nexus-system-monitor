using System.Linq;
using Avalonia.Controls;
using NexusMonitor.Core.Pages;
using NexusMonitor.UI.Controls;

namespace NexusMonitor.UI.Windows;

/// <summary>
/// A single widget torn off into its own OS window. Hosts the same live control
/// <see cref="WidgetTileFactory"/> would place on the page grid, inside the same card-chrome
/// border used there, so a popped-out widget looks identical to its on-page appearance.
/// <para>
/// DataContext is set to the owning DashboardViewModel (passed in, not resolved here) so the
/// hosted control's inherited bindings resolve exactly as they do on the page — this dual
/// instantiation (one control on the page, one here) is safe by design because both bind to the
/// same shared VM instance rather than owning any state of their own.
/// </para>
/// <para>
/// Unlike <c>OverlayWindow</c> (shown/hidden but never truly closed), a pop-out closes
/// explicitly: <see cref="PopOutCoordinator"/> owns this window's lifetime and wires
/// <see cref="Window.Closing"/> to capture final geometry before tearing it down. This class
/// performs no suppression logic itself — that lives entirely in the coordinator, which is the
/// only place that needs to distinguish a user-driven close (return the widget to the page) from
/// a shutdown/profile-switch close (geometry kept, still marked popped out).
/// </para>
/// </summary>
public partial class WidgetPopOutWindow : Window
{
    /// <summary>The instance id of the widget hosted in this window — the same id
    /// <see cref="PopOutCoordinator"/>'s <c>Open</c> dictionary and close/return callbacks key on.</summary>
    public Guid InstanceId { get; private set; }

    /// <summary>
    /// Required by the Avalonia XAML loader (a public parameterless constructor must exist for
    /// the compiled resource to be reachable — see AVLN3001). Not intended for direct use; always
    /// construct via <see cref="WidgetPopOutWindow(WidgetInstance, object)"/> instead.
    /// </summary>
    public WidgetPopOutWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the window for one popped-out widget instance. Position, Width and Height are left
    /// for the caller (<see cref="PopOutCoordinator"/>) to set before <see cref="Window.Show()"/>.
    /// </summary>
    /// <param name="widget">The widget instance being popped out. Its control is built via
    /// <see cref="WidgetTileFactory.Create"/> and hosted inside the card-chrome border; its
    /// <see cref="WidgetInstance.WidgetTypeId"/> resolves this window's title via the widget
    /// catalog.</param>
    /// <param name="dashboardViewModel">The owning DashboardViewModel, set as this window's
    /// DataContext so the hosted control's inherited bindings resolve exactly as they do on the
    /// page.</param>
    public WidgetPopOutWindow(WidgetInstance widget, object dashboardViewModel) : this()
    {
        InstanceId     = widget.InstanceId;
        Title          = ResolveTitle(widget.WidgetTypeId);
        DataContext    = dashboardViewModel;
        CardHost.Child = WidgetTileFactory.Create(widget);
    }

    /// <summary>Looks up the widget catalog's display Name for <paramref name="widgetTypeId"/>;
    /// falls back to the raw type id for any TypeId the catalog doesn't recognize (mirrors
    /// <see cref="WidgetTileFactory"/>'s never-broken-tile guarantee — an unrecognized type still
    /// gets a readable title instead of a blank one).</summary>
    private static string ResolveTitle(string widgetTypeId) =>
        WidgetCatalog.Entries.FirstOrDefault(e => e.TypeId == widgetTypeId)?.Name ?? widgetTypeId;
}
