using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// Full-screen glass overlay listing <see cref="WidgetCatalog.Entries"/> for the user to add
/// to the page being edited. Visibility is driven by <see cref="DashboardViewModel.IsGalleryOpen"/>.
/// Backdrop click dismisses the gallery; the card itself stops the click from reaching the backdrop.
/// Focus management mirrors <see cref="CommandPaletteControl"/>: opening the gallery captures the
/// previously-focused element and moves focus to the card so Escape is routed here immediately;
/// closing restores focus to whatever was focused beforehand.
/// </summary>
public partial class WidgetGalleryControl : UserControl
{
    /// <summary>
    /// The element that held focus before the gallery opened.
    /// Restored when the gallery closes so the user's context is preserved.
    /// </summary>
    private IInputElement? _previousFocus;

    public WidgetGalleryControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Auto-focus the gallery card whenever the control becomes visible, so keyboard input
    /// (Escape) is routed to this control without an extra click.
    /// Caches the previously-focused element so it can be restored on close.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
            {
                // Capture whoever had focus before we open, then steal it.
                _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                Dispatcher.UIThread.Post(
                    () => GalleryCard?.Focus(),
                    DispatcherPriority.Input);
            }
            else
            {
                // Restore focus to the element that was active before the gallery opened.
                var prev = _previousFocus;
                _previousFocus = null;
                Dispatcher.UIThread.Post(() => prev?.Focus(), DispatcherPriority.Input);
            }
        }
    }

    /// <summary>
    /// Escape closes the gallery via the same code path as the backdrop click.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            if (DataContext is DashboardViewModel vm)
            {
                vm.IsGalleryOpen = false;
            }
            e.Handled = true;
        }
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
        {
            vm.IsGalleryOpen = false;
        }
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Stop the pointer event reaching the backdrop border so it doesn't close the gallery.
        e.Handled = true;
    }
}
