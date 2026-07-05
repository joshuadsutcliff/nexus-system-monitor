using Avalonia.Controls;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Maps a WidgetInstance to its rendered control. Phase 2: every TypeId renders as a
/// placeholder WidgetTile; Phase 3 replaces this with the real widget registry. Unknown TypeIds
/// intentionally render the same placeholder path (spec §8: never a broken/blank tile).</summary>
public static class WidgetTileFactory
{
    private static readonly Dictionary<string, string> KnownTitles = new()
    {
        ["nexus.widget.healthScore"] = "Health Score",
        ["nexus.widget.cpuChart"] = "CPU",
        ["nexus.widget.memoryChart"] = "Memory",
    };

    /// <summary>Creates the control for one widget instance. Never returns null and never throws.</summary>
    public static Control Create(WidgetInstance widget)
    {
        var known = KnownTitles.TryGetValue(widget.WidgetTypeId, out var title);
        return new WidgetTile
        {
            Title = known ? title : "Unknown widget",
            Subtitle = known
                ? "Widget content arrives in a future update."
                : $"'{widget.WidgetTypeId}' isn't available in this version. Its place and settings are preserved.",
        };
    }
}
