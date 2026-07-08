using System.Linq;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// One selectable entry in the add-widget gallery: the widget's engine type id,
/// its display name, a short description shown under the name, its NexusIcons glyph
/// (rendered on the gallery card's icon tile), and the section (<see cref="Kind"/>)
/// it's grouped under in the gallery.
/// </summary>
/// <param name="TypeId">The engine widget type id (e.g. <c>nexus.widget.healthScore</c>).</param>
/// <param name="Name">Display name shown in the gallery row.</param>
/// <param name="Description">Short description shown under the name.</param>
/// <param name="Icon">NexusIcons glyph char shown on the card's icon tile.</param>
/// <param name="Kind">Gallery section this entry is grouped under — one of
/// <see cref="WidgetCatalog.KindMonitoring"/>, <see cref="WidgetCatalog.KindAnalysis"/>,
/// <see cref="WidgetCatalog.KindInsights"/>.</param>
public sealed record WidgetCatalogEntry(string TypeId, string Name, string Description, string Icon, string Kind);

/// <summary>One quiet section header + its entries, as presented in the add-widget gallery.
/// Purely a display-time grouping of <see cref="WidgetCatalog.Entries"/> — never a second
/// source of truth for which widgets exist.</summary>
public sealed record WidgetCatalogGroup(string Title, IReadOnlyList<WidgetCatalogEntry> Items);

/// <summary>
/// The fixed list of widget types the add-widget gallery offers.
/// Real-widget extraction (a later plan) grows this list; the gallery itself
/// only ever reads <see cref="Entries"/> (or the <see cref="Groups"/> derived from it)
/// and never hardcodes widget types.
/// </summary>
public static class WidgetCatalog
{
    public const string KindMonitoring = "Monitoring";
    public const string KindAnalysis   = "Analysis";
    public const string KindInsights   = "Insights";

    /// <summary>The known widget types, in the order they should be presented within their
    /// gallery section (see <see cref="Groups"/> for the grouped, section-ordered view).
    /// Icon glyphs are NexusIcons (FluentSystemIcons) codepoints; where a widget already has
    /// an icon elsewhere in the app (CPU/Memory/Disk/GPU on their dashboard cards, Predictions
    /// on its own Recommendations-severity glyph) the SAME codepoint is reused here rather than
    /// inventing a second one.</summary>
    public static IReadOnlyList<WidgetCatalogEntry> Entries { get; } = new[]
    {
        new WidgetCatalogEntry(
            "nexus.widget.healthScore",
            "Health Score",
            "Overall system health ring with trend.",
            "\ue701", KindMonitoring), // ic_fluent_heart_pulse_20_regular
        new WidgetCatalogEntry(
            "nexus.widget.cpuCard",
            "CPU",
            "Processor usage level and status.",
            "\ue9d5", KindMonitoring), // same glyph SubsystemCardViewModel uses for the CPU card
        new WidgetCatalogEntry(
            "nexus.widget.memoryCard",
            "Memory",
            "RAM utilization and status.",
            "\ue9d6", KindMonitoring), // same glyph SubsystemCardViewModel uses for the Memory card
        new WidgetCatalogEntry(
            "nexus.widget.diskCard",
            "Disk",
            "Storage usage and status.",
            "\ue9d7", KindMonitoring), // same glyph SubsystemCardViewModel uses for the Disk card
        new WidgetCatalogEntry(
            "nexus.widget.gpuCard",
            "GPU",
            "Graphics processor usage and status.",
            "\ue9d9", KindMonitoring), // same glyph SubsystemCardViewModel uses for the GPU card
        new WidgetCatalogEntry(
            "nexus.widget.bottleneck",
            "Bottleneck Analysis",
            "System resource bottleneck detection.",
            "\ue456", KindAnalysis), // ic_fluent_data_funnel_20_regular
        new WidgetCatalogEntry(
            "nexus.widget.topConsumers",
            "Top Consumers",
            "Processes using the most resources.",
            "\uf39b", KindAnalysis), // ic_fluent_data_bar_horizontal_20_regular
        new WidgetCatalogEntry(
            "nexus.widget.recommendations",
            "Recommendations",
            "Actionable system optimization suggestions.",
            "\uf4d6", KindInsights), // ic_fluent_lightbulb_20_regular
        new WidgetCatalogEntry(
            "nexus.widget.predictions",
            "Predictions",
            "Forecasted system trends and alerts.",
            "\ue9b0", KindInsights), // ic_fluent_predictions_20_regular — same glyph already used
                                     // in-app as RecommendationViewModel's info-severity icon
        new WidgetCatalogEntry(
            "nexus.widget.healthTrends",
            "Health Trends",
            "Historical health metrics over time.",
            "\ue11c", KindInsights), // ic_fluent_arrow_trending_lines_20_regular
    };

    /// <summary>
    /// <see cref="Entries"/> grouped by <see cref="WidgetCatalogEntry.Kind"/> for the gallery's
    /// section headers, in section-presentation order (Monitoring, Analysis, Insights). Each
    /// group preserves its members' relative order from <see cref="Entries"/>. Purely derived —
    /// adding a widget only ever means adding one line to <see cref="Entries"/>.
    /// </summary>
    public static IReadOnlyList<WidgetCatalogGroup> Groups { get; } = new[]
    {
        new WidgetCatalogGroup(KindMonitoring, Entries.Where(e => e.Kind == KindMonitoring).ToList()),
        new WidgetCatalogGroup(KindAnalysis,   Entries.Where(e => e.Kind == KindAnalysis).ToList()),
        new WidgetCatalogGroup(KindInsights,   Entries.Where(e => e.Kind == KindInsights).ToList()),
    };
}
