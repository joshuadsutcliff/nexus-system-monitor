using Avalonia;
using Avalonia.Controls;

namespace NexusMonitor.UI.Controls;

/// <summary>Standard chrome for one widget on a page. Phase 2 renders title + subtitle only;
/// Phase 3 replaces the subtitle body with real widget content.</summary>
public partial class WidgetTile : UserControl
{
    /// <summary>The tile's heading.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WidgetTile, string?>(nameof(Title));

    /// <summary>Secondary line under the heading.</summary>
    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<WidgetTile, string?>(nameof(Subtitle));

    /// <summary>The tile's heading.</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>Secondary line under the heading.</summary>
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>Initializes the tile and wires property changes to the text blocks.</summary>
    public WidgetTile()
    {
        InitializeComponent();
        this.GetObservable(TitleProperty).Subscribe(t => TitleText.Text = t);
        this.GetObservable(SubtitleProperty).Subscribe(s => SubtitleText.Text = s);
    }
}
