using Avalonia;
using Avalonia.Controls;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Arranges widget tiles on the page grid. Pure view-mode renderer: layout math is
/// delegated to Core's PageGeometry, children come from WidgetTileFactory, and this control
/// never mutates the PageLayout itself (edit mode lives elsewhere in the stack); it does
/// dispose outgoing children on rebuild so a future disposable widget tears down cleanly.</summary>
public sealed class PageHostControl : Panel
{
    /// <summary>The page to render. Reassigning rebuilds all children.</summary>
    public static readonly StyledProperty<PageLayout?> PageProperty =
        AvaloniaProperty.Register<PageHostControl, PageLayout?>(nameof(Page));

    /// <summary>Height of one grid row in pixels.</summary>
    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<PageHostControl, double>(nameof(CellHeight), PageMetrics.DefaultCellHeight);

    /// <summary>Gap between cells in pixels.</summary>
    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<PageHostControl, double>(nameof(CellGap), PageMetrics.DefaultCellGap);

    /// <summary>True while the dashboard is in edit mode. Phase 8 UI polish: toggles the
    /// "edit-mode" style class (mirroring <see cref="EditAdornerControl.IsActiveProperty"/>'s own
    /// class-handler idiom) so <c>Themes/Controls.axaml</c>'s widget-tile hover-lift style can gate
    /// on <c>PageHostControl:not(.edit-mode)</c> — this control is the one visual-tree ancestor
    /// common to every rendered widget tile regardless of the tile's own DataContext (several
    /// widget types rebind DataContext away from DashboardViewModel, e.g. the four SubsystemCard
    /// instances, so a per-tile <c>{Binding !IsEditMode}</c> isn't reachable uniformly — gating via
    /// this control's own class instead sidesteps that). Bound from <see cref="Views.DashboardView"/>
    /// to <c>DashboardViewModel.IsEditMode</c>.</summary>
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<PageHostControl, bool>(nameof(IsEditMode));

    /// <summary>True when widget-tile hover lift should be able to animate — Phase 8 UI polish:
    /// toggles the "hover-lift-enabled" style class, gating the same hover-lift style referenced by
    /// <see cref="IsEditModeProperty"/>'s doc independently of edit mode. Bound from <see
    /// cref="Views.DashboardView"/> to <c>DashboardViewModel.HoverEffectsEnabled</c>, which forwards
    /// to <c>MotionSettingsService.EffectEnabled(settings, MotionEffect.HoverEffects)</c>.</summary>
    public static readonly StyledProperty<bool> HoverLiftEnabledProperty =
        AvaloniaProperty.Register<PageHostControl, bool>(nameof(HoverLiftEnabled));

    static PageHostControl()
    {
        PageProperty.Changed.AddClassHandler<PageHostControl>((c, _) => c.RebuildChildren());
        AffectsMeasure<PageHostControl>(PageProperty, CellHeightProperty, CellGapProperty);
        IsEditModeProperty.Changed.AddClassHandler<PageHostControl>((c, e) => c.Classes.Set("edit-mode", (bool?)e.NewValue ?? false));
        HoverLiftEnabledProperty.Changed.AddClassHandler<PageHostControl>((c, e) => c.Classes.Set("hover-lift-enabled", (bool?)e.NewValue ?? false));
    }

    /// <summary>The page to render.</summary>
    public PageLayout? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>Height of one grid row in pixels.</summary>
    public double CellHeight { get => GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }

    /// <summary>Gap between cells in pixels.</summary>
    public double CellGap { get => GetValue(CellGapProperty); set => SetValue(CellGapProperty, value); }

    /// <summary>True while the dashboard is in edit mode.</summary>
    public bool IsEditMode { get => GetValue(IsEditModeProperty); set => SetValue(IsEditModeProperty, value); }

    /// <summary>True when widget-tile hover lift is allowed to animate.</summary>
    public bool HoverLiftEnabled { get => GetValue(HoverLiftEnabledProperty); set => SetValue(HoverLiftEnabledProperty, value); }

    private void RebuildChildren()
    {
        // INVARIANT: a widget's Dispose may release only widget-owned resources — NEVER its
        // bound VM. Widgets bind shared singleton VMs (card VMs, HealthTrendsViewModel);
        // disposing those from a rebuild would corrupt live state app-wide.
        foreach (var child in Children)
            (child as IDisposable)?.Dispose();
        Children.Clear();
        if (Page is null) return;
        foreach (var widget in Page.Widgets)
            Children.Add(WidgetTileFactory.Create(widget));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Page is null || Page.Widgets.Count == 0 || Children.Count != Page.Widgets.Count)
            return new Size(0, 0);

        // Width must be finite (the hosting ScrollViewer disables horizontal scrolling).
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;

        var rows = 0;
        for (var i = 0; i < Page.Widgets.Count; i++)
        {
            var px = PageGeometry.ToPixelRect(Page.Widgets[i].Rect, width, Page.GridColumns, CellHeight, CellGap);
            Children[i].Measure(new Size(px.Width, px.Height));
            if (Page.Widgets[i].Rect.Bottom > rows) rows = Page.Widgets[i].Rect.Bottom;
        }
        return new Size(width, PageGeometry.TotalHeight(rows, CellHeight, CellGap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Page is null || Children.Count != Page.Widgets.Count)
            return finalSize;

        for (var i = 0; i < Page.Widgets.Count; i++)
        {
            var px = PageGeometry.ToPixelRect(Page.Widgets[i].Rect, finalSize.Width, Page.GridColumns, CellHeight, CellGap);
            Children[i].Arrange(new Rect(px.X, px.Y, px.Width, px.Height));
        }
        return finalSize;
    }
}
