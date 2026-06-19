using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.ViewModels;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI;

public partial class MainWindow : Window
{
    // ── Command Palette ───────────────────────────────────────────────────────
    private CommandPaletteViewModel? _commandPaletteVm;

    // ── Drag-to-reorder state ────────────────────────────────────────────────
    private NavItem? _dragItem;
    private int      _dragFromIndex;
    private int      _dropTargetIndex;
    private Point    _dragStart;
    private bool     _isDragging;
    private double   _itemHeight;
    private Control? _dragGrid;    // the dragged item's Grid (transition disabled)

    // Grip zone: the leftmost 18px of each nav item (the grip column)
    private const double GripWidth = 18.0;

    // ── Crystal Glass specular tracking ──────────────────────────────────────
    private double _specNx;  // smoothed normalised cursor x (0‥1)
    private double _specNy;  // smoothed normalised cursor y (0‥1)
    private DispatcherTimer? _shimmerTimer;
    private double _shimmerPhase; // 0‥2π, drives prismatic cycling
    private bool   _shimmerEnabled; // tracks whether glass is logically active

    public MainWindow()
    {
        InitializeComponent();

        // Default to opaque. Glass effects are opt-in via Settings → Backdrop Blur Mode.
        // SettingsViewModel.ApplyBackdropMode() will override this on load if the user
        // has previously enabled glass. Changing this from AcrylicBlur to None prevents
        // wallpaper bleed-through on light desktops before settings are applied.
        //
        // Crystal Glass effect: per-panel acrylic / smart-tint blur is deferred to
        // Phase 27 (v0.9.0 accessibility + UX pass). The specular shimmer + prismatic
        // layers below provide visual polish without requiring a window-wide AcrylicBlur
        // backdrop (which makes content unreadable over bright wallpapers).
        TransparencyLevelHint = [WindowTransparencyLevel.None];

        // Initialize the command palette ViewModel eagerly so that
        // CommandPaletteControl's IsVisible="{Binding IsOpen}" binding has a DataContext
        // from the moment the window is created. Without this, the binding has no source
        // and Avalonia defaults IsVisible to true — showing the backdrop overlay on startup.
        InitializeCommandPalette();

        // Dispose all cached ViewModels when the window closes.
        Closed += (_, _) =>
        {
            (DataContext as IDisposable)?.Dispose();
            _shimmerTimer?.Stop();
        };

        // Hook drag-to-reorder once the visual tree is ready.
        Loaded += (_, _) =>
        {
            // Restore window geometry from last session.
            var settings = App.Services.GetRequiredService<SettingsService>();
            if (settings.Current.LastWindowWidth > 0 && settings.Current.LastWindowHeight > 0)
            {
                Width  = settings.Current.LastWindowWidth;
                Height = settings.Current.LastWindowHeight;
            }
            if (settings.Current.LastWindowX >= 0 && settings.Current.LastWindowY >= 0)
            {
                Position = new PixelPoint(settings.Current.LastWindowX, settings.Current.LastWindowY);
            }
            if (Enum.TryParse<WindowState>(settings.Current.LastWindowState, out var savedState))
            {
                WindowState = savedState;
            }
            // Active tab is restored by MainViewModel via DefaultTab binding (already wired).

            SetupNavDrag();
            // Shimmer timer is started only if glass is enabled (via SetGlassActive).
            // SettingsViewModel.ApplyBackdropMode calls SetGlassActive after settings load.

            // On macOS, restore the native traffic-light buttons and pad the titlebar
            // content to clear them. The window XAML sets ExtendClientAreaChromeHints=
            // NoChrome for the custom Windows-style caption buttons; on macOS that also
            // suppresses the native traffic lights, leaving no window controls at all.
            // PreferSystemChrome keeps the close/minimize/zoom buttons over the extended
            // client area (the custom buttons are hidden in ApplyMacOSTitleBarPadding).
            if (OperatingSystem.IsMacOS())
            {
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
                ApplyMacOSTitleBarPadding();
            }

            // On Linux, some compositors (e.g. wlroots/Sway, KWin on X11) do not honour
            // ExtendClientAreaChromeHints=NoChrome and lose resize handling entirely.
            // Keep SystemDecorations=Full to retain native resize grips, but hide the
            // built-in title bar so our custom chrome remains the only visible title bar.
            if (OperatingSystem.IsLinux())
            {
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.SystemChrome;
                // Avalonia's system chrome gives native resize grips; our overlay custom
                // title bar stays on top via ZIndex so the native title bar is hidden.
            }
        };

        // Pause shimmer when minimized, resume when restored
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            if (WindowState == WindowState.Minimized)
                _shimmerTimer?.Stop();
            else if (_shimmerEnabled)
                _shimmerTimer?.Start();
        };
    }

    // ── Window chrome ────────────────────────────────────────────────────────

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── Global keyboard shortcuts ──────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        var platformMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool ctrl = (e.KeyModifiers & platformMod) != 0;

        switch (e.Key)
        {
            // Ctrl+Tab / Ctrl+Shift+Tab — cycle sidebar tabs
            case Key.Tab when ctrl:
                CycleTab(vm, shift: (e.KeyModifiers & KeyModifiers.Shift) != 0);
                e.Handled = true;
                break;

            // Ctrl+F — focus search box on current tab
            case Key.F when ctrl:
                FocusCurrentSearch();
                e.Handled = true;
                break;

            // Ctrl+Q — quit application (bypasses tray/minimize-to-tray behavior)
            case Key.Q when ctrl:
                _forceClose = true;
                Close();
                e.Handled = true;
                break;

            // Ctrl+, — open Settings
            case Key.OemComma when ctrl:
                var settingsNav = vm.NavItems.FirstOrDefault(n => n.Label == "Settings");
                if (settingsNav is not null) vm.Navigate(settingsNav);
                e.Handled = true;
                break;

            // Ctrl+K — toggle Command Palette
            case Key.K when ctrl:
                ToggleCommandPalette();
                e.Handled = true;
                break;
        }
    }

    private static void CycleTab(MainViewModel vm, bool shift)
    {
        var navigable = vm.NavItems.Where(n => !n.IsSeparator).ToList();
        int count   = navigable.Count;
        int current = navigable.IndexOf(vm.SelectedNavItem!);
        int next    = shift
            ? (current - 1 + count) % count
            : (current + 1) % count;
        vm.Navigate(navigable[next]);
    }

    /// <summary>
    /// Walks the visual tree to find a TextBox named "SearchBox" in the
    /// current page and focuses it.
    /// </summary>
    private void FocusCurrentSearch()
    {
        var searchBox = FindDescendant<TextBox>(this, "SearchBox");
        if (searchBox is not null)
        {
            searchBox.Focus();
            searchBox.SelectAll();
        }
    }

    private static T? FindDescendant<T>(Visual parent, string name) where T : Control
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is T control && control.Name == name)
                return control;
            if (child is Visual visual)
            {
                var found = FindDescendant<T>(visual, name);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ── Command Palette ───────────────────────────────────────────────────────

    /// <summary>
    /// Eagerly initializes the command palette ViewModel and sets DataContext on the
    /// overlay control. Called from the constructor so the IsVisible="{Binding IsOpen}"
    /// binding always has a source — preventing the overlay from appearing on startup
    /// when the DataContext would otherwise be null (causing IsVisible to default to true).
    /// </summary>
    private void InitializeCommandPalette()
    {
        var settingsSvc = App.Services.GetRequiredService<SettingsService>();
        var appSettings = settingsSvc.Current;
        var onSave      = (Action)settingsSvc.Save;

        // Navigation items are not yet available at construction time (DataContext not set),
        // so we start with a lightweight VM containing only settings-based items.
        // Navigation items are added lazily on first toggle (below) once DataContext is set.
        var items = new List<CommandPaletteItem>();

        // Toggle items — built with static helper
        items.Add(CommandPaletteViewModel.MakeToggle("Gaming Mode", "\uF451",
            () => appSettings.GamingModeEnabled,
            v => appSettings.GamingModeEnabled = v,
            onSave));
        items.Add(CommandPaletteViewModel.MakeToggle("ProBalance", "\uEA51",
            () => appSettings.ProBalanceEnabled,
            v => appSettings.ProBalanceEnabled = v,
            onSave));
        items.Add(CommandPaletteViewModel.MakeToggle("Desktop Notifications", "\uF115",
            () => appSettings.DesktopNotificationsEnabled,
            v => appSettings.DesktopNotificationsEnabled = v,
            onSave));

        // Theme items
        items.Add(CommandPaletteViewModel.MakeTheme("Dark Theme",   "\uF4A7", "Dark",   appSettings, onSave, ApplyTheme));
        items.Add(CommandPaletteViewModel.MakeTheme("Light Theme",  "\uF4A6", "Light",  appSettings, onSave, ApplyTheme));
        items.Add(CommandPaletteViewModel.MakeTheme("System Theme", "\uF08C", "System", appSettings, onSave, ApplyTheme));

        _commandPaletteVm = new CommandPaletteViewModel(
            items,
            appSettings,
            onSave,
            onThemeChanged: ApplyTheme);

        CommandPalette.DataContext = _commandPaletteVm;
    }

    private bool _navItemsAddedToPalette;

    private void ToggleCommandPalette()
    {
        var mainVm = DataContext as MainViewModel;
        if (mainVm is null) return;

        // Add navigation items once, on first toggle, when DataContext is available.
        if (!_navItemsAddedToPalette)
        {
            _navItemsAddedToPalette = true;
            var navItems = mainVm.NavItems
                .Where(n => !n.IsSeparator)
                .Select(nav =>
                {
                    var captured = nav;
                    return new CommandPaletteItem(nav.Label, nav.Icon, "Navigate",
                        execute: () => mainVm.Navigate(captured));
                })
                .ToList();
            // Prepend navigation items before the settings-based items already in the list
            _commandPaletteVm!.PrependItems(navItems);
        }

        _commandPaletteVm!.Toggle();
    }

    /// <summary>
    /// Applies a ThemeMode string ("Dark" | "Light" | "System") to the running application.
    /// Mirrors the pattern used in SettingsViewModel.ApplyAllVisuals().
    /// </summary>
    private static void ApplyTheme(string themeMode)
    {
        if (Application.Current is null) return;

        Application.Current.RequestedThemeVariant = themeMode switch
        {
            "Dark"  => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _       => App.DetectSystemTheme()
        };
    }

    // ── macOS: traffic light clearance ──────────────────────────────────────

    /// <summary>
    /// Adds left padding to the titlebar content grid so it doesn't overlap
    /// the macOS traffic light buttons (close/minimize/maximize, ~80px wide).
    /// </summary>
    private void ApplyMacOSTitleBarPadding()
    {
        // Bump left margin so content clears the traffic light buttons (~80px wide + 6px gap).
        var grid = FindDescendant<Grid>(this, "TitleBarGrid");
        if (grid is not null)
        {
            var m = grid.Margin;
            grid.Margin = new Thickness(86, m.Top, m.Right, m.Bottom);
        }

        // Hide custom min/max/close buttons — macOS shows native traffic lights instead.
        var controls = FindDescendant<StackPanel>(this, "WindowControls");
        if (controls is not null)
            controls.IsVisible = false;
    }

    // ── Crystal Glass: pointer-tracked specular + prismatic shimmer ─────────

    /// <summary>
    /// Moves the specular "bright spot" gradient direction toward the cursor,
    /// simulating a real-time light source that follows the user's pointer —
    /// a Crystal Glass material (specular rim highlights + refraction).
    /// </summary>
    private void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;

        var pos = e.GetCurrentPoint(this).Position;
        double rawNx = pos.X / Bounds.Width;   // 0..1
        double rawNy = pos.Y / Bounds.Height;  // 0..1

        // Smooth (lerp) to avoid jarring jumps — 30% blend toward target each frame
        const double k = 0.30;
        _specNx += (rawNx - _specNx) * k;
        _specNy += (rawNy - _specNy) * k;

        ApplySpecularGradient(TitleSpecularWash,   _specNx, _specNy, horizontal: true);
        ApplySpecularGradient(SideSpecularWash,    _specNx, _specNy, horizontal: false);
        ApplySpecularGradient(ContentSpecularWash, _specNx, _specNy, horizontal: true);
    }

    private static void ApplySpecularGradient(Border? target, double nx, double ny, bool horizontal)
    {
        if (target?.Background is not LinearGradientBrush brush || brush.GradientStops.Count < 3) return;

        if (horizontal)
        {
            // Titlebar / content: light slides left–right following cursor
            double sx = nx * 0.85;                        // bright spot reaches right side
            double sy = ny * 0.5;                         // moves vertically more
            brush.StartPoint = new RelativePoint(sx, sy, RelativeUnit.Relative);
            brush.EndPoint   = new RelativePoint(
                Math.Min(1.0, sx + 0.35),                 // tighter cone
                Math.Min(1.0, sy + 0.7),
                RelativeUnit.Relative);
        }
        else
        {
            // Sidebar: light slides top-to-bottom following cursor
            double sy = ny * 0.7;
            double sx = nx * 0.5;
            brush.StartPoint = new RelativePoint(sx, sy, RelativeUnit.Relative);
            brush.EndPoint   = new RelativePoint(
                Math.Min(1.0, sx + 0.45),
                Math.Min(1.0, sy + 0.5),
                RelativeUnit.Relative);
        }
    }

    /// <summary>
    /// Called by <see cref="SettingsViewModel"/> when the glass/backdrop setting changes.
    /// Starts the shimmer timer when glass is active, stops it otherwise.
    /// Also pauses automatically when the window is minimized.
    /// </summary>
    public void SetGlassActive(bool active)
    {
        _shimmerEnabled = active;
        if (active)
        {
            if (_shimmerTimer is null)
            {
                _shimmerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _shimmerTimer.Tick += (_, _) =>
                {
                    _shimmerPhase += 0.015;        // full cycle ≈ 420 ticks ≈ 21s
                    if (_shimmerPhase > Math.PI * 2) _shimmerPhase -= Math.PI * 2;

                    double angle = _shimmerPhase;
                    double sx = 0.5 + 0.5 * Math.Cos(angle);
                    double sy = 0.5 + 0.5 * Math.Sin(angle);
                    double ex = 0.5 + 0.5 * Math.Cos(angle + Math.PI);
                    double ey = 0.5 + 0.5 * Math.Sin(angle + Math.PI);

                    RotatePrismatic(TitlePrismatic,   sx, sy, ex, ey);
                    RotatePrismatic(SidePrismatic,    sx, sy, ex, ey);
                    RotatePrismatic(ContentPrismatic, sx, sy, ex, ey);
                };
            }
            // Only start if not minimized
            if (WindowState != WindowState.Minimized)
                _shimmerTimer.Start();
        }
        else
        {
            _shimmerTimer?.Stop();
        }
    }

    private static void RotatePrismatic(Border? target, double sx, double sy, double ex, double ey)
    {
        if (target?.Background is not LinearGradientBrush brush) return;
        brush.StartPoint = new RelativePoint(sx, sy, RelativeUnit.Relative);
        brush.EndPoint   = new RelativePoint(ex, ey, RelativeUnit.Relative);
    }

    // ── Sidebar drag-to-reorder (iOS-style gap animation) ──────────────────

    private void SetupNavDrag()
    {
        // Use tunneling so we intercept before the Button children handle the event
        NavItemsControl.AddHandler(
            PointerPressedEvent,
            OnNavPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: false);

        NavItemsControl.AddHandler(
            PointerMovedEvent,
            OnNavPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: false);

        NavItemsControl.AddHandler(
            PointerReleasedEvent,
            OnNavPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: false);
    }

    private void OnNavPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(NavItemsControl).Properties.IsLeftButtonPressed) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        // Find which container was pressed and whether it's in the grip zone
        for (int i = 0; i < vm.NavItems.Count; i++)
        {
            var item = vm.NavItems[i];
            if (item.IsSeparator || item.IsPinned) continue;  // separators and pinned are not draggable

            var container = NavItemsControl.ContainerFromIndex(i) as Control;
            if (container is null) continue;

            var localPt = e.GetCurrentPoint(container).Position;
            if (localPt.Y < 0 || localPt.Y > container.Bounds.Height) continue;

            // Only start drag when pressing in the grip column (left GripWidth px)
            if (localPt.X < 0 || localPt.X >= GripWidth) continue;

            _dragFromIndex   = i;
            _dropTargetIndex = i;
            _dragItem        = item;
            _dragStart       = e.GetCurrentPoint(NavItemsControl).Position;
            _isDragging      = false;
            e.Handled        = true;
            break;
        }
    }

    private void OnNavPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null) return;

        var pos = e.GetCurrentPoint(NavItemsControl).Position;

        // Activate drag mode once the user has moved >= 5px vertically
        if (!_isDragging)
        {
            if (Math.Abs(pos.Y - _dragStart.Y) < 5) return;
            _isDragging = true;
            _dragItem.IsDragging = true;

            // Cache item height from first non-separator container
            var vm0 = DataContext as MainViewModel;
            if (vm0 is not null)
            {
                for (int i = 0; i < vm0.NavItems.Count; i++)
                {
                    if (vm0.NavItems[i].IsSeparator) continue;
                    var c0 = NavItemsControl.ContainerFromIndex(i) as Control;
                    _itemHeight = c0?.Bounds.Height ?? 42;
                    break;
                }
            }
            else
            {
                _itemHeight = 42;
            }

            // Disable transition on dragged item so it tracks the cursor instantly
            var dragContainer = NavItemsControl.ContainerFromIndex(_dragFromIndex) as Control;
            _dragGrid = FindNavRowGrid(dragContainer);
            _dragGrid?.SetValue(Animatable.TransitionsProperty, new Transitions());
        }

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        // Find group boundaries (non-separator items in the same group)
        var dragGroup = _dragItem.Group;
        int groupMin = _dragFromIndex;
        int groupMax = _dragFromIndex;
        for (int i = 0; i < vm.NavItems.Count; i++)
        {
            if (vm.NavItems[i].IsSeparator) continue;
            if (vm.NavItems[i].Group == dragGroup)
            {
                if (i < groupMin) groupMin = i;
                if (i > groupMax) groupMax = i;
            }
        }

        // Calculate drop target from pointer position, clamped to group bounds
        int rawTarget = GetNavIndexAt(e, vm.NavItems.Count);
        _dropTargetIndex = Math.Clamp(rawTarget, groupMin, groupMax);

        double deltaY = pos.Y - _dragStart.Y;
        int dy = (int)Math.Round(deltaY);
        int h  = (int)Math.Round(_itemHeight);

        // Apply visual transforms — NO Move() during drag, just visual displacement
        for (int i = 0; i < vm.NavItems.Count; i++)
        {
            if (vm.NavItems[i].IsSeparator) continue;  // separators have no transform

            var container = NavItemsControl.ContainerFromIndex(i) as Control;
            var grid = FindNavRowGrid(container);
            if (grid is null) continue;

            if (i == _dragFromIndex)
            {
                // Dragged item follows cursor directly
                grid.RenderTransform = TransformOperations.Parse(
                    $"translate(0px, {dy}px) scale(1.01)");
            }
            else
            {
                int shiftY = 0;
                if (_dragFromIndex < _dropTargetIndex &&
                    i > _dragFromIndex && i <= _dropTargetIndex)
                {
                    shiftY = -h;   // items between drag→drop shift UP
                }
                else if (_dragFromIndex > _dropTargetIndex &&
                         i >= _dropTargetIndex && i < _dragFromIndex)
                {
                    shiftY = h;    // items between drop→drag shift DOWN
                }

                grid.RenderTransform = shiftY != 0
                    ? TransformOperations.Parse($"translate(0px, {shiftY}px)")
                    : TransformOperations.Parse("scale(1.0)");
            }
        }
    }

    private void OnNavPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _isDragging;
        var draggedItem = _dragItem;
        int dropIndex   = _dropTargetIndex;

        if (draggedItem is not null)
            draggedItem.IsDragging = false;

        // Restore spring transition on the dragged item
        _dragGrid?.ClearValue(Animatable.TransitionsProperty);

        if (draggedItem is not null && wasDragging)
        {
            var vm = DataContext as MainViewModel;
            if (vm is not null)
            {
                // Reset all transforms to identity — spring transition animates the settle
                for (int i = 0; i < vm.NavItems.Count; i++)
                {
                    if (vm.NavItems[i].IsSeparator) continue;
                    var container = NavItemsControl.ContainerFromIndex(i) as Control;
                    var grid = FindNavRowGrid(container);
                    if (grid is not null)
                        grid.RenderTransform = TransformOperations.Parse("scale(1.0)");
                }

                // Perform the actual collection move
                if (dropIndex != _dragFromIndex)
                    vm.NavItems.Move(_dragFromIndex, dropIndex);

                vm.SaveNavOrder();
            }

            // Gentle settle: subtle pulse on the dropped item
            DispatcherTimer.RunOnce(() =>
            {
                var settleContainer = NavItemsControl.ContainerFromIndex(dropIndex) as Control;
                var settleGrid = FindNavRowGrid(settleContainer);
                if (settleGrid is not null)
                {
                    settleGrid.RenderTransform = TransformOperations.Parse("scale(1.015)");
                    DispatcherTimer.RunOnce(() =>
                    {
                        settleGrid.RenderTransform = TransformOperations.Parse("scale(1.0)");
                    }, TimeSpan.FromMilliseconds(150));
                }
            }, TimeSpan.FromMilliseconds(30));
        }

        _dragItem   = null;
        _dragGrid   = null;
        _isDragging = false;
    }

    /// <summary>Finds the Grid.nav-row inside a nav ItemsControl container.</summary>
    private static Control? FindNavRowGrid(Control? container)
    {
        if (container is ContentPresenter cp && cp.Child is Control child)
            return child;  // The Grid is the direct child of the ContentPresenter
        return null;
    }

    /// <summary>
    /// Returns the index of the nav container whose midpoint is below the current
    /// pointer Y position — used to determine the drop target during drag.
    /// </summary>
    private int GetNavIndexAt(PointerEventArgs e, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var container = NavItemsControl.ContainerFromIndex(i) as Control;
            if (container is null) continue;

            var localPt = e.GetCurrentPoint(container).Position;
            if (localPt.Y >= 0 && localPt.Y < container.Bounds.Height)
                return i;
        }
        return count - 1;
    }
}
