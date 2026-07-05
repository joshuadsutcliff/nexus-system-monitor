namespace NexusMonitor.UI.Controls;

/// <summary>
/// One selectable entry in the add-widget gallery: the widget's engine type id,
/// its display name, and a short description shown under the name.
/// </summary>
/// <param name="TypeId">The engine widget type id (e.g. <c>nexus.widget.healthScore</c>).</param>
/// <param name="Name">Display name shown in the gallery row.</param>
/// <param name="Description">Short description shown under the name.</param>
public sealed record WidgetCatalogEntry(string TypeId, string Name, string Description);

/// <summary>
/// The fixed list of widget types the add-widget gallery offers.
/// Real-widget extraction (a later plan) grows this list; the gallery itself
/// only ever reads <see cref="Entries"/> and never hardcodes widget types.
/// </summary>
public static class WidgetCatalog
{
    /// <summary>The known widget types, in the order they should be presented in the gallery.</summary>
    public static IReadOnlyList<WidgetCatalogEntry> Entries { get; } = new[]
    {
        new WidgetCatalogEntry(
            "nexus.widget.healthScore",
            "Health Score",
            "Overall system health ring with trend."),
        new WidgetCatalogEntry(
            "nexus.widget.cpuCard",
            "CPU",
            "Processor usage level and status."),
        new WidgetCatalogEntry(
            "nexus.widget.memoryCard",
            "Memory",
            "RAM utilization and status."),
        new WidgetCatalogEntry(
            "nexus.widget.diskCard",
            "Disk",
            "Storage usage and status."),
        new WidgetCatalogEntry(
            "nexus.widget.gpuCard",
            "GPU",
            "Graphics processor usage and status."),
        new WidgetCatalogEntry(
            "nexus.widget.bottleneck",
            "Bottleneck Analysis",
            "System resource bottleneck detection."),
        new WidgetCatalogEntry(
            "nexus.widget.topConsumers",
            "Top Consumers",
            "Processes using the most resources."),
        new WidgetCatalogEntry(
            "nexus.widget.recommendations",
            "Recommendations",
            "Actionable system optimization suggestions."),
        new WidgetCatalogEntry(
            "nexus.widget.predictions",
            "Predictions",
            "Forecasted system trends and alerts."),
        new WidgetCatalogEntry(
            "nexus.widget.healthTrends",
            "Health Trends",
            "Historical health metrics over time."),
    };
}
