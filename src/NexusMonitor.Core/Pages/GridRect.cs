namespace NexusMonitor.Core.Pages;

/// <summary>A cell-aligned rectangle on a page grid. Col/Row are 0-based; Right/Bottom are exclusive.</summary>
public sealed record GridRect(int Col, int Row, int ColSpan, int RowSpan)
{
    /// <summary>Exclusive right edge: column where the rectangle ends (Col + ColSpan).</summary>
    public int Right => Col + ColSpan;

    /// <summary>Exclusive bottom edge: row where the rectangle ends (Row + RowSpan).</summary>
    public int Bottom => Row + RowSpan;

    /// <summary>True when the rectangles share at least one cell. Touching edges do not intersect.</summary>
    public bool Intersects(GridRect other) =>
        Col < other.Right && other.Col < Right &&
        Row < other.Bottom && other.Row < Bottom;

    /// <summary>True when the rect lies fully inside a grid of the given column count (rows are unbounded downward).</summary>
    public bool FitsWithinColumns(int gridColumns) =>
        Col >= 0 && Row >= 0 && ColSpan >= 1 && RowSpan >= 1 && Right <= gridColumns;

    /// <summary>Returns the nearest valid rect inside the grid: origin clamped to 0, span capped to the column count, then shifted left if it spills.</summary>
    public GridRect ClampTo(int gridColumns)
    {
        var colSpan = Math.Clamp(ColSpan, 1, gridColumns);
        var rowSpan = Math.Max(1, RowSpan);
        var col = Math.Max(0, Col);
        var row = Math.Max(0, Row);
        if (col + colSpan > gridColumns) col = gridColumns - colSpan;
        return new GridRect(col, row, colSpan, rowSpan);
    }
}
