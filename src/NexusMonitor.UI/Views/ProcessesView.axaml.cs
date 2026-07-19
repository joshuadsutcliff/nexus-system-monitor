using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NexusMonitor.UI.ViewModels;

namespace NexusMonitor.UI.Views;

public partial class ProcessesView : UserControl
{
    private bool _restoringSort; // guards against RestoreSort→Sorting→OnGridSorting feedback loop

    public ProcessesView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ProcessGrid.Sorting          += OnGridSorting;
        ProcessGrid.SelectionChanged += OnGridSelectionChanged;

        // Restore sort indicator from the VM — survives tab switches (View is recreated, VM is not).
        RestoreSort();

        if (DataContext is ProcessesViewModel vm)
        {
            vm.Processes.CollectionChanged += OnProcessesCollectionChanged;
            vm.PropertyChanged            += OnVmPropertyChanged;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        ProcessGrid.Sorting          -= OnGridSorting;
        ProcessGrid.SelectionChanged -= OnGridSelectionChanged;

        if (DataContext is ProcessesViewModel vm)
        {
            vm.Processes.CollectionChanged -= OnProcessesCollectionChanged;
            vm.PropertyChanged            -= OnVmPropertyChanged;
        }
    }

    /// <summary>
    /// Syncs the DataGrid's live multi-selection (SelectionMode="Extended": Ctrl/Cmd+click
    /// toggles, Shift+click ranges) into the ViewModel. DataGrid.SelectedItems (Avalonia 11.2.3)
    /// is a get-only IList, not a bindable AvaloniaProperty, so this event-driven sync is the
    /// standard way to expose it — the same pattern this file already uses for OnGridSorting.
    /// SelectedItem stays bound directly in XAML and keeps driving the details pane/anchor
    /// selection unchanged.
    /// </summary>
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ProcessesViewModel vm) return;
        vm.UpdateSelection(ProcessGrid.SelectedItems.OfType<ProcessRowViewModel>().ToList());
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_restoringSort) return;
        if (e.Column?.SortMemberPath is not { } path) return;
        if (DataContext is not ProcessesViewModel vm) return;

        if (vm.SortMemberPath == path)
        {
            // Toggle: Ascending → Descending → cleared
            if (vm.SortDirection == ListSortDirection.Ascending)
                vm.SortDirection = ListSortDirection.Descending;
            else
                vm.SortMemberPath = null; // third click clears sort
        }
        else
        {
            // New column — starts ascending
            vm.SortMemberPath = path;
            vm.SortDirection  = ListSortDirection.Ascending;
        }
    }

    private void OnProcessesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // After Processes.Clear() (tree-mode rebuild), re-apply the column sort.
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            RestoreSort();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // After switching view modes, restore the sort indicator.
        if (e.PropertyName == nameof(ProcessesViewModel.IsTreeViewActive))
            RestoreSort();
    }

    private void RestoreSort()
    {
        if (DataContext is not ProcessesViewModel vm) return;
        if (vm.SortMemberPath is null) return;

        var col = ProcessGrid.Columns.FirstOrDefault(c => c.SortMemberPath == vm.SortMemberPath);
        if (col is null) return;

        // col.Sort() posts ProcessSort asynchronously (same dispatcher priority).
        // Keep _restoringSort=true until all those callbacks have fired so OnGridSorting
        // ignores them. Posting the reset at the same priority guarantees FIFO ordering.
        _restoringSort = true;
        col.Sort(vm.SortDirection);
        Dispatcher.UIThread.Post(() => _restoringSort = false);
    }
}
