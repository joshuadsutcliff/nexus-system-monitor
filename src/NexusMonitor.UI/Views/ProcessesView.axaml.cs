using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NexusMonitor.UI.ViewModels;
using Serilog;

namespace NexusMonitor.UI.Views;

public partial class ProcessesView : UserControl
{
    private bool _restoringSort; // guards against RestoreSort→Sorting→OnGridSorting feedback loop

    // Task 2 (column customization): DataGridColumn is an AvaloniaObject, not a Visual/Control —
    // it's never in the visual tree, so it can't inherit DataContext (a {Binding} on
    // DataGridColumn.IsVisible wouldn't resolve) AND, tried first, it turns out the Avalonia
    // XAML compiler doesn't generate a code-behind field for x:Name on it either (CS0103 at
    // build) since field generation is a StyledElement feature DataGridColumn doesn't have.
    // So columns are looked up at runtime instead: each hideable column's XAML Header text is a
    // static string that matches ProcessColumnOption.Header exactly (both ultimately come from
    // the same _hideableColumns literals in ProcessesViewModel), so BuildHideableColumnsByKey()
    // maps ProcessColumnOption.Key -> DataGridColumn by scanning ProcessGrid.Columns for that
    // Header text. Built once the first time it's needed (ProcessGrid.Columns is already
    // populated by then — the columns are declared statically in XAML, added during
    // InitializeComponent()). ApplyColumnVisibility() then pushes each option's IsVisible onto
    // its column, driven by subscribing to ProcessColumnOption.PropertyChanged in
    // OnLoaded/OnUnloaded — the same code-behind-subscribes-to-VM-events pattern this file
    // already uses for grid sorting (OnGridSorting) and multi-select (OnGridSelectionChanged)
    // below.
    //
    // Three sources of truth have to agree for this lookup to succeed: this dictionary's
    // values, ProcessesViewModel._hideableColumns' headers, and the actual Header="..." literals
    // in ProcessesView.axaml. internal (not private) + InternalsVisibleTo so
    // ProcessesViewHeaderSyncTests can catch the three of them drifting apart at test time
    // instead of a user hitting the crash below at runtime.
    internal static readonly Dictionary<string, string> _headerByColumnKey = new()
    {
        ["pid"]      = "PID",
        ["cpu"]      = "CPU",
        ["memory"]   = "Memory",
        ["leak"]     = "Leak",
        ["io"]       = "I/O",
        ["impact"]   = "Impact",
        ["rules"]    = "Rules",
        ["group"]    = "Group",
        ["priority"] = "Priority",
        ["threads"]  = "Threads",
        ["handles"]  = "Handles",
        ["user"]     = "User",
    };

    private Dictionary<string, DataGridColumn>? _hideableColumnsByKey;

    private Dictionary<string, DataGridColumn> HideableColumnsByKey => _hideableColumnsByKey ??= BuildHideableColumnsByKey();

    /// <summary>
    /// Resolves each <see cref="_headerByColumnKey"/> entry to its <see cref="DataGridColumn"/>
    /// by Header text. Uses <c>FirstOrDefault</c> rather than <c>First</c> deliberately: if a
    /// column's Header="..." literal in ProcessesView.axaml ever drifts from this dictionary
    /// (e.g. someone renames it in the XAML only), the affected key is skipped instead of
    /// throwing InvalidOperationException out of OnLoaded — that used to crash the whole
    /// Processes view on tab load. A skipped key just means ApplyColumnVisibility's
    /// TryGetValue lookup for it misses, so the toggle is a silent no-op for that one column
    /// instead of taking down the view. Logged so the drift is still visible in the logs even
    /// though nothing crashes.
    /// </summary>
    private Dictionary<string, DataGridColumn> BuildHideableColumnsByKey()
    {
        var result = new Dictionary<string, DataGridColumn>();
        foreach (var (key, header) in _headerByColumnKey)
        {
            var column = ProcessGrid.Columns.FirstOrDefault(c => (c.Header as string) == header);
            if (column is null)
            {
                Log.Warning(
                    "Processes column {Key} (expected header {Header}) not found on ProcessGrid — " +
                    "its show/hide toggle will be a no-op until the header text is fixed",
                    key, header);
                continue;
            }
            result[key] = column;
        }
        return result;
    }

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

            foreach (var option in vm.ColumnOptions)
            {
                option.PropertyChanged += OnColumnOptionPropertyChanged;
                ApplyColumnVisibility(vm, option);
            }
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

            foreach (var option in vm.ColumnOptions)
                option.PropertyChanged -= OnColumnOptionPropertyChanged;
        }
    }

    /// <summary>
    /// Applies one <see cref="ProcessColumnOption"/>'s visibility to its matching DataGridColumn
    /// (via <see cref="HideableColumnsByKey"/>). The "user" column additionally ANDs in
    /// <see cref="ProcessesViewModel.ShowUserColumn"/> — a platform-capability gate, not a user
    /// preference — so toggling the option back on can never force the column visible on a
    /// platform whose provider doesn't actually populate it.
    /// </summary>
    private void ApplyColumnVisibility(ProcessesViewModel vm, ProcessColumnOption option)
    {
        if (!HideableColumnsByKey.TryGetValue(option.Key, out var column)) return;
        column.IsVisible = option.Key == "user"
            ? option.IsVisible && vm.ShowUserColumn
            : option.IsVisible;
    }

    private void OnColumnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProcessColumnOption.IsVisible)) return;
        if (sender is not ProcessColumnOption option) return;
        if (DataContext is not ProcessesViewModel vm) return;
        ApplyColumnVisibility(vm, option);
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
