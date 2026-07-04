namespace NexusMonitor.Core.Pages;

/// <summary>Pure tiling logic for page layouts: validation, placement with push-down, movement, compaction.
/// UI drag previews and commits both call these methods, so preview always equals final behavior.</summary>
public static class PageLayoutEngine
{
    /// <summary>True when <paramref name="rect"/> fits the grid and overlaps no widget
    /// (optionally ignoring one instance — pass the instance being moved).</summary>
    public static bool IsValidPlacement(PageLayout page, GridRect rect, Guid? ignoreInstanceId = null)
    {
        if (!rect.FitsWithinColumns(page.GridColumns)) return false;
        foreach (var w in page.Widgets)
        {
            if (w.InstanceId == ignoreInstanceId) continue;
            if (w.Rect.Intersects(rect)) return false;
        }
        return true;
    }

    /// <summary>Adds a widget at its rect (clamped into the grid), pushing overlapped widgets
    /// straight down — cascading — until no overlaps remain. The placed widget keeps its spot.</summary>
    public static PageLayout PlaceWidget(PageLayout page, WidgetInstance widget)
    {
        var clamped = widget with { Rect = widget.Rect.ClampTo(page.GridColumns) };
        var widgets = new List<WidgetInstance>(page.Widgets) { clamped };
        return page.WithWidgets(ResolveCollisions(widgets, pinnedId: clamped.InstanceId));
    }

    /// <summary>Moves a widget to the target rect (clamped). The mover is pinned; anything it
    /// now overlaps pushes down. Unknown ids return the page unchanged (same instance).</summary>
    public static PageLayout MoveWidget(PageLayout page, Guid instanceId, GridRect target)
    {
        var existing = page.FindWidget(instanceId);
        if (existing is null) return page;

        var moved = existing with { Rect = target.ClampTo(page.GridColumns) };
        var widgets = page.Widgets
            .Select(w => w.InstanceId == instanceId ? moved : w)
            .ToList();
        return page.WithWidgets(ResolveCollisions(widgets, pinnedId: instanceId));
    }

    /// <summary>Removes a widget. Remaining widgets keep their positions (compaction is separate/explicit).</summary>
    public static PageLayout RemoveWidget(PageLayout page, Guid instanceId) =>
        page.FindWidget(instanceId) is null
            ? page
            : page.WithWidgets(page.Widgets.Where(w => w.InstanceId != instanceId).ToList());

    /// <summary>Push-down resolution: while any widget overlaps the pinned/settled set, move the
    /// topmost-leftmost offender down to just below whatever it overlaps. Deterministic and terminating
    /// (rows only ever increase; the grid is unbounded downward).</summary>
    private static IReadOnlyList<WidgetInstance> ResolveCollisions(List<WidgetInstance> widgets, Guid pinnedId)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mover in widgets
                         .Where(w => w.InstanceId != pinnedId)
                         .OrderBy(w => w.Rect.Row).ThenBy(w => w.Rect.Col).ToList())
            {
                var collidesWith = widgets
                    .Where(o => o.InstanceId != mover.InstanceId && o.Rect.Intersects(mover.Rect))
                    .ToList();
                if (collidesWith.Count == 0) continue;

                var newRow = collidesWith.Max(o => o.Rect.Bottom);
                var idx = widgets.FindIndex(w => w.InstanceId == mover.InstanceId);
                widgets[idx] = mover with { Rect = mover.Rect with { Row = newRow } };
                changed = true;
            }
        }
        return widgets;
    }
}
