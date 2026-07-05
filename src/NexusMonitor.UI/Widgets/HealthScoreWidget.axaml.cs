using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// The overall system health ring card as a standalone page-engine widget.
/// Binds directly to the inherited <c>DashboardViewModel</c> DataContext
/// (<c>HealthRingBrush</c>/<c>OverallScore</c>/<c>TrendArrow</c>/<c>OverallLabel</c>/
/// <c>OverallDescription</c>/<c>AutomationStatus</c>) — no factory binding required.
/// </summary>
public partial class HealthScoreWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public HealthScoreWidget()
    {
        InitializeComponent();
    }
}
