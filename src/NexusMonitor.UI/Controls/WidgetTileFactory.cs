using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using NexusMonitor.Core.Pages;
using NexusMonitor.UI.Widgets;

namespace NexusMonitor.UI.Controls;

/// <summary>Maps a WidgetInstance to its rendered control: the live TypeId→widget registry.
/// Legacy TypeIds are resolved to their canonical replacement via <see cref="AliasMap"/> before
/// dispatch. Unknown/unrecognized TypeIds intentionally render the same placeholder
/// <see cref="WidgetTile"/> path as before (spec §8: never a broken/blank tile).</summary>
public static class WidgetTileFactory
{
    /// <summary>Legacy TypeIds (Phase-1 factory JSON + any saved layouts) mapped to their
    /// canonical replacement, so old layouts keep rendering their widget instead of falling
    /// back to the unknown-widget placeholder.</summary>
    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.Ordinal)
    {
        ["nexus.widget.cpuChart"] = "nexus.widget.cpuCard",
        ["nexus.widget.memoryChart"] = "nexus.widget.memoryCard",
    };

    /// <summary>Creates the control for one widget instance. Never returns null and never throws.</summary>
    public static Control Create(WidgetInstance widget)
    {
        var typeId = AliasMap.TryGetValue(widget.WidgetTypeId, out var canonical) ? canonical : widget.WidgetTypeId;

        return typeId switch
        {
            "nexus.widget.healthScore" => new HealthScoreWidget(),
            "nexus.widget.cpuCard" => CreateSubsystemCard("CpuCard"),
            "nexus.widget.memoryCard" => CreateSubsystemCard("MemoryCard"),
            "nexus.widget.diskCard" => CreateSubsystemCard("DiskCard"),
            "nexus.widget.gpuCard" => CreateSubsystemCard("GpuCard"),
            "nexus.widget.bottleneck" => new BottleneckWidget(),
            "nexus.widget.topConsumers" => new TopConsumersWidget(),
            "nexus.widget.recommendations" => new RecommendationsWidget(),
            "nexus.widget.predictions" => new PredictionsWidget(),
            "nexus.widget.healthTrends" => new HealthTrendsWidget(),
            _ => CreateUnknownPlaceholder(widget.WidgetTypeId),
        };
    }

    /// <summary>Creates a <see cref="SubsystemCardWidget"/> whose DataContext is bound to the
    /// named <see cref="NexusMonitor.UI.ViewModels.SubsystemCardViewModel"/> property on the
    /// inherited <c>DashboardViewModel</c> (e.g. <c>CpuCard</c>).</summary>
    private static Control CreateSubsystemCard(string cardPropertyName)
    {
        var control = new SubsystemCardWidget();
        control.Bind(Control.DataContextProperty, new Binding(cardPropertyName));
        return control;
    }

    /// <summary>Builds the placeholder tile for a TypeId this version doesn't recognize —
    /// its place and settings are preserved rather than dropped.</summary>
    private static WidgetTile CreateUnknownPlaceholder(string widgetTypeId) => new()
    {
        Title = "Unknown widget",
        Subtitle = $"'{widgetTypeId}' isn't available in this version. Its place and settings are preserved.",
    };
}
