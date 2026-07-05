using Avalonia.Controls;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        EditAdorner.RemoveRequested += id => (DataContext as DashboardViewModel)?.EditRemove(id);
        EditAdorner.MoveCommitted += (id, rect) => (DataContext as DashboardViewModel)?.EditMove(id, rect);
    }
}
