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
            "Placeholder tile — live content arrives with widget extraction."),
        new WidgetCatalogEntry(
            "nexus.widget.cpuChart",
            "CPU",
            "Placeholder tile — live content arrives with widget extraction."),
        new WidgetCatalogEntry(
            "nexus.widget.memoryChart",
            "Memory",
            "Placeholder tile — live content arrives with widget extraction."),
    };
}
