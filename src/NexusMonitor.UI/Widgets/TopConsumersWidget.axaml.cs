using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// The top resource consumers list as a standalone page-engine widget.
/// Binds directly to the inherited <c>DashboardViewModel</c> DataContext
/// (<c>TopConsumers</c>/<c>RefreshConsumersCommand</c>) — no factory binding required.
/// </summary>
public partial class TopConsumersWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public TopConsumersWidget()
    {
        InitializeComponent();
    }
}
