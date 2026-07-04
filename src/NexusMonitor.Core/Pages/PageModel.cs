namespace NexusMonitor.Core.Pages;

/// <summary>Persisted geometry/state of a widget torn off into its own OS window.</summary>
public sealed record PopOutState(bool IsPoppedOut, int X, int Y, int Width, int Height, bool Topmost);

/// <summary>One placed widget on a page. ConfigJson is opaque raw JSON owned by the widget type (preserved verbatim by serialization).</summary>
public sealed record WidgetInstance(
    Guid InstanceId,
    string WidgetTypeId,
    GridRect Rect,
    string? ConfigJson = null,
    PopOutState? PopOut = null);

/// <summary>A page's complete layout. Immutable; engine operations return new instances.</summary>
public sealed record PageLayout(
    string PageId,
    string Title,
    string IconKey,
    int GridColumns,
    IReadOnlyList<WidgetInstance> Widgets)
{
    public const int DefaultGridColumns = 12;

    public PageLayout WithWidgets(IReadOnlyList<WidgetInstance> widgets) => this with { Widgets = widgets };

    public WidgetInstance? FindWidget(Guid instanceId)
    {
        foreach (var w in Widgets)
            if (w.InstanceId == instanceId) return w;
        return null;
    }
}
