using Avalonia.Controls;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Views;

/// <summary>Code-behind for the Dashboard page: wires the edit adorner's gestures to the VM and,
/// once loaded, hands the VM the owning main <see cref="Window"/> reference it needs to open
/// pop-out windows (unavailable at construction time — this view isn't attached to a window yet).</summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        EditAdorner.RemoveRequested += id => (DataContext as DashboardViewModel)?.EditRemove(id);
        EditAdorner.MoveCommitted += (id, rect) => (DataContext as DashboardViewModel)?.EditMove(id, rect);
        EditAdorner.PopOutRequested += id => (DataContext as DashboardViewModel)?.PopOutWidget(id);

        // The Dashboard view is recreated on every navigation to it (this constructor can run more
        // than once), but the main window itself never changes across the app's lifetime, so
        // re-attaching it here on each Loaded is harmless.
        Loaded += (_, _) =>
        {
            if (DataContext is DashboardViewModel vm && TopLevel.GetTopLevel(this) is Window window)
                vm.AttachOwnerWindow(window);
        };
    }
}
