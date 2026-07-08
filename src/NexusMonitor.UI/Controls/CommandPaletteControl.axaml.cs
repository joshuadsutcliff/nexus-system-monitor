using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.ViewModels;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// Full-screen glass overlay that renders the Command Palette.
/// Visibility is driven by <see cref="CommandPaletteViewModel.IsOpen"/>.
/// Keyboard routing (Up/Down/Enter/Escape) is handled in code-behind;
/// backdrop click dismisses the palette.
/// </summary>
public partial class CommandPaletteControl : UserControl
{
    /// <summary>
    /// The element that held focus before the palette opened.
    /// Restored when the palette closes so the user's context is preserved.
    /// </summary>
    private IInputElement? _previousFocus;

    public CommandPaletteControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Auto-focus the SearchBox whenever the control becomes visible
    /// so the user can type immediately without an extra click.
    /// Caches the previously-focused element so it can be restored on close.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
            {
                // Trigger opacity animation: start at 0, animate to 1.
                Opacity = 0;
                Dispatcher.UIThread.Post(() => Opacity = 1.0, DispatcherPriority.Render);

                // Capture whoever had focus before we open, then steal it.
                _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                Dispatcher.UIThread.Post(
                    () => SearchBox?.Focus(),
                    DispatcherPriority.Input);
            }
            else
            {
                // Restore focus to the element that was active before the palette opened.
                var prev = _previousFocus;
                _previousFocus = null;
                Dispatcher.UIThread.Post(() => prev?.Focus(), DispatcherPriority.Input);
            }
        }
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as CommandPaletteViewModel)?.Close();
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Stop the pointer event reaching the backdrop border so it doesn't close the palette.
        e.Handled = true;
    }

    /// <summary>
    /// Belt-and-suspenders Escape close, mirroring <see cref="WidgetGalleryControl"/>'s own
    /// UserControl-level <c>OnKeyDown</c> override: <see cref="OnSearchKeyDown"/> already closes
    /// on Escape while SearchBox has focus (the normal case — SearchBox is focused on open and
    /// nothing else in this overlay is independently focusable), but routing Escape here too
    /// means the palette still closes even if focus ever ends up elsewhere within it.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && DataContext is CommandPaletteViewModel vm)
        {
            vm.Close();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = DataContext as CommandPaletteViewModel;
        if (vm is null) return;

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                ScrollSelectedIntoView(vm);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                ScrollSelectedIntoView(vm);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Asks the ItemsControl to scroll the currently-selected container into view.
    /// Posted at Background priority so the visual tree has updated its selection
    /// state before we attempt to locate the container.
    /// </summary>
    private void ScrollSelectedIntoView(CommandPaletteViewModel vm)
    {
        var index = vm.SelectedIndex;
        Dispatcher.UIThread.Post(() =>
        {
            var container = ItemsList?.ContainerFromIndex(index);
            container?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void OnItemButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm) return;
        if (sender is Button { DataContext: CommandPaletteItem item })
        {
            var idx = vm.FilteredItems.IndexOf(item);
            if (idx >= 0)
            {
                vm.SelectedIndex = idx;
                vm.ExecuteSelected();
            }
        }
    }
}
