namespace NexusMonitor.UI.Controls;

/// <summary>Frozen layout constants for the page engine (spec §3.1: tuned during Phase 2 smoke, then fixed).</summary>
public static class PageMetrics
{
    /// <summary>Baseline height of one grid row in pixels (before any font-scale multiplier, applied in a later phase).</summary>
    public const double DefaultCellHeight = 72;

    /// <summary>Gap between cells in pixels, both axes.</summary>
    public const double DefaultCellGap = 12;
}
