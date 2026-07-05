namespace NexusMonitor.Core.Pages;

/// <summary>A device-independent pixel rectangle. Plain doubles so Core stays UI-framework-free.</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height);

/// <summary>Pure math mapping grid cells to pixels. The UI panel owns the inputs
/// (available width, cell height, gap); this class owns no constants.</summary>
public static class PageGeometry
{
    /// <summary>Width of one cell: the available width minus interior gaps, divided evenly.</summary>
    public static double CellWidth(double availableWidth, int gridColumns, double gap) =>
        (availableWidth - gap * (gridColumns - 1)) / gridColumns;

    /// <summary>Pixel rect for a grid rect: spans cover their cells plus interior gaps.</summary>
    public static PixelRect ToPixelRect(GridRect rect, double availableWidth, int gridColumns, double cellHeight, double gap)
    {
        var cellWidth = CellWidth(availableWidth, gridColumns, gap);
        var x = rect.Col * (cellWidth + gap);
        var y = rect.Row * (cellHeight + gap);
        var width = rect.ColSpan * cellWidth + (rect.ColSpan - 1) * gap;
        var height = rect.RowSpan * cellHeight + (rect.RowSpan - 1) * gap;
        return new PixelRect(x, y, width, height);
    }

    /// <summary>Total content height for a page of the given row count (0 rows → 0).</summary>
    public static double TotalHeight(int rowCount, double cellHeight, double gap) =>
        rowCount <= 0 ? 0 : rowCount * cellHeight + (rowCount - 1) * gap;
}
