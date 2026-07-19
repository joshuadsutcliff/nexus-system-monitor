using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NexusMonitor.Core.Onboarding;
using NexusMonitor.Core.ViewModels;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// One-time first-run welcome overlay (1.0 gate item): a single glass-styled card shown
/// centered over the dashboard on first launch only, then never again. Visibility is driven by
/// <see cref="FirstRunOrientationViewModel.IsOpen"/>. "Get started" (button, Enter, or Escape)
/// all dismiss it; dismissal persists <see cref="Core.Models.AppSettings.HasSeenFirstRunOrientation"/>
/// immediately so the overlay never constructs again, including within the same session.
///
/// Hosted only on <c>MainWindow</c> over the main dashboard — deliberately not added to
/// <c>WidgetPopOutWindow</c>, so it never appears in a pop-out window.
/// </summary>
public partial class FirstRunOverlay : UserControl
{
    /// <summary>The element that held focus before the overlay opened, restored on close —
    /// same pattern as <see cref="CommandPaletteControl"/>.</summary>
    private IInputElement? _previousFocus;

    public FirstRunOverlay()
    {
        InitializeComponent();

        // Text is set here from FirstRunCopy (not hardcoded in the AXAML) so the deployed card
        // can never drift from the TDD-locked copy constants in NexusMonitor.Core.Tests.
        TitleText.Text       = FirstRunCopy.Title;
        Row1Text.Text        = FirstRunCopy.Row1;
        Row2Text.Text        = FirstRunCopy.Row2;
        Row3Text.Text        = FirstRunCopy.Row3;
        Row4Text.Text        = FirstRunCopy.Row4;
        ButtonLabelText.Text = FirstRunCopy.ButtonLabel;
    }

    /// <summary>
    /// Focuses "Get started" whenever the overlay becomes visible, so Enter/Space activate it
    /// immediately and Escape (handled below via bubbling from the focused button) works without
    /// requiring an extra click first. No fade/opacity animation — a static show/hide is
    /// sufficient per this feature's own spec (no animation requirements).
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsVisibleProperty) return;

        if (IsVisible)
        {
            _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => GetStartedButton?.Focus(), DispatcherPriority.Input);
        }
        else
        {
            var prev = _previousFocus;
            _previousFocus = null;
            Dispatcher.UIThread.Post(() => prev?.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnGetStartedClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as FirstRunOrientationViewModel)?.Dismiss();
    }

    /// <summary>
    /// Escape dismisses via this UserControl-level override, mirroring
    /// <see cref="CommandPaletteControl"/>'s "belt-and-suspenders" pattern — it fires as the
    /// bubbling KeyDown routes up from whichever element has focus (normally the "Get started"
    /// button, focused in <see cref="OnPropertyChanged"/> above). Enter is deliberately NOT
    /// handled here: with the button focused, Avalonia's own Button already turns Enter into a
    /// Click (invoking <see cref="OnGetStartedClick"/>) before this override ever sees it —
    /// handling it a second time here would be redundant, not additive.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled) return;
        if (DataContext is not FirstRunOrientationViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            vm.Dismiss();
            e.Handled = true;
        }
    }
}
