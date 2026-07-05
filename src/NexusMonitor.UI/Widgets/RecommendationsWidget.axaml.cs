using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// The recommendations list as a standalone page-engine widget.
/// Binds directly to the inherited <c>DashboardViewModel</c> DataContext
/// (<c>Recommendations</c>) — no factory binding required.
/// </summary>
public partial class RecommendationsWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public RecommendationsWidget()
    {
        InitializeComponent();
    }
}
