using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// The resource predictions list as a standalone page-engine widget.
/// Binds directly to the inherited <c>DashboardViewModel</c> DataContext
/// (<c>PredictionCards</c>/<c>HasPredictions</c>). Unlike the classic section (which hides
/// the whole card when <c>HasPredictions</c> is false), this widget's root always renders —
/// a widget the user placed on the page should not disappear — showing a
/// "No active predictions" fallback instead.
/// </summary>
public partial class PredictionsWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public PredictionsWidget()
    {
        InitializeComponent();
    }
}
