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

    static PageHostControl()
    {
        PageProperty.Changed.AddClassHandler<PageHostControl>((c, _) => c.RebuildChildren());
        AffectsMeasure<PageHostControl>(PageProperty, CellHeightProperty, CellGapProperty);
    }

    /// <summary>The page to render.</summary>
    public PageLayout? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>Height of one grid row in pixels.</summary>
    public double CellHeight { get => GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }

    /// <summary>Gap between cells in pixels.</summary>
    public double CellGap { get => GetValue(CellGapProperty); set => SetValue(CellGapProperty, value); }

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
