using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// Thin page-engine wrapper hosting the existing <c>HealthTrendsView</c> (which owns its
/// own card chrome), bound to the inherited <c>DashboardViewModel</c>'s
/// <c>HealthTrendsViewModel</c> — a DI-owned singleton whose lifecycle this widget does
/// not manage or affect.
/// </summary>
public partial class HealthTrendsWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public HealthTrendsWidget()
    {
        InitializeComponent();
    }
}
