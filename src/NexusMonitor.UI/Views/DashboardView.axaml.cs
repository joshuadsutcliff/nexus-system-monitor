using Avalonia.Controls;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        // MoveCommitted wiring arrives in Task 6 alongside the event declaration (see
        // EditAdornerControl's comment: an unraised field-like event trips CS0067 here).
        EditAdorner.RemoveRequested += id => (DataContext as DashboardViewModel)?.EditRemove(id);
    }
}
