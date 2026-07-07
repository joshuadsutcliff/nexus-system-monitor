using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Views;

/// <summary>Code-behind for the Dashboard page: wires the edit adorner's gestures to the VM,
/// fades the edit-mode toolbar in on entry, and once loaded, hands the VM the owning main
/// <see cref="Window"/> reference it needs to open pop-out windows (unavailable at construction
/// time — this view isn't attached to a window yet).</summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        EditAdorner.RemoveRequested += id => (DataContext as DashboardViewModel)?.EditRemove(id);
        EditAdorner.MoveCommitted += (id, rect) => (DataContext as DashboardViewModel)?.EditMove(id, rect);
        EditAdorner.PopOutRequested += id => (DataContext as DashboardViewModel)?.PopOutWidget(id);

        // Phase 8 Task 3 gate fix: fade EditToolbar in when it becomes visible (entering edit
        // mode). EditToolbar is a plain StackPanel, not a custom subclass with its own
        // OnPropertyChanged override — the idiom CommandPaletteControl/WidgetGalleryControl use
        // for their own IsVisible-driven fades — so the same fade is instead wired externally via
        // its PropertyChanged event.
        EditToolbar.PropertyChanged += OnEditToolbarPropertyChanged;

        // The Dashboard view is recreated on every navigation to it (this constructor can run more
        // than once), but the main window itself never changes across the app's lifetime, so
        // re-attaching it here on each Loaded is harmless.
        Loaded += (_, _) =>
        {
            if (DataContext is DashboardViewModel vm && TopLevel.GetTopLevel(this) is Window window)
                vm.AttachOwnerWindow(window);
        };
    }

    /// <summary>
    /// Fades <see cref="EditToolbar"/> in when it becomes visible (entering edit mode) — mirrors
    /// <see cref="Controls.WidgetGalleryControl"/>'s own IsVisible-driven fade: Avalonia skips a
    /// control's rendering entirely while IsVisible is false, so nothing about its Opacity value
    /// changes when IsVisible flips true — the <c>DoubleTransition</c> declared on EditToolbar
    /// (see <see cref="Views.DashboardView"/>'s XAML) needs an explicit 0→1 Opacity kick, posted a
    /// frame later, to actually animate. Gated on <see cref="DashboardViewModel.EditChromeMotionEnabled"/>;
    /// when disabled, Opacity is set straight to 1 so no animated frame plays. There is
    /// deliberately no fade-out: IsVisible flips back to false the instant edit mode is
    /// exited/cancelled, collapsing the toolbar before any fade-out transition could play out —
    /// the same trade-off <see cref="Controls.CommandPaletteControl"/> and
    /// <see cref="Controls.WidgetGalleryControl"/> already accept for their own overlays.
    /// </summary>
    private void OnEditToolbarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty || !EditToolbar.IsVisible) return;

        if (DataContext is DashboardViewModel { EditChromeMotionEnabled: true })
        {
            EditToolbar.Opacity = 0;
            Dispatcher.UIThread.Post(() => EditToolbar.Opacity = 1.0, DispatcherPriority.Render);
        }
        else
        {
            EditToolbar.Opacity = 1;
        }
    }
}
