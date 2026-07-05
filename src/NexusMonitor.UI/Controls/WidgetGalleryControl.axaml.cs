using Avalonia.Controls;
using Avalonia.Input;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// Full-screen glass overlay listing <see cref="WidgetCatalog.Entries"/> for the user to add
/// to the page being edited. Visibility is driven by <see cref="DashboardViewModel.IsGalleryOpen"/>.
/// Backdrop click dismisses the gallery; the card itself stops the click from reaching the backdrop.
/// </summary>
public partial class WidgetGalleryControl : UserControl
{
    public WidgetGalleryControl()
    {
        InitializeComponent();
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
