using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// The bottleneck analysis card as a standalone page-engine widget.
/// The root does not set DataContext, so it stays bound to the inherited
/// <c>DashboardViewModel</c> — the "Run Analysis" button's
/// <c>$parent[UserControl].DataContext.RunAnalysisCommand</c> binding resolves against it.
/// An inner Border binds <c>BottleneckCard</c> (a <c>BottleneckCardViewModel</c>) for the
/// rest of the card's content, including <c>DisableMemoryIntegrityCommand</c>.
/// </summary>
public partial class BottleneckWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public BottleneckWidget()
    {
        InitializeComponent();
    }
}
