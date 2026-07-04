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
}
