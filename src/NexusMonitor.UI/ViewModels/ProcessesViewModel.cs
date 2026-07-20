using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Formatting;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI;
using NexusMonitor.UI.Messages;
using NexusMonitor.UI.Helpers;
using NexusMonitor.UI.Views;

namespace NexusMonitor.UI.ViewModels;

public partial class ProcessesViewModel : ViewModelBase, IActivatable, IDisposable
{
    private readonly IProcessProvider            _processProvider;
    private readonly AppSettings                 _appSettings;
    private readonly ProcessPreferenceStore?     _preferenceStore;
    private readonly MemoryLeakDetectionService? _leakService;
    private readonly ProcessGroupStore?          _groupStore;
    private readonly SettingsService?            _settingsService;
    private readonly CancellationTokenSource _cts = new();
    private readonly BatchProcessActions _batchActions;

    /// <summary>Exposes platform capability flags for binding in the View.</summary>
    public IPlatformCapabilities Platform { get; }

    /// <summary>
    /// True when the active process provider actually populates <see cref="ProcessRowViewModel.UserName"/>.
    /// Sym-1 Task 4 (2026-07-08): MacOSProcessProvider now resolves each process's owner via
    /// sysctl(KERN_PROC_PID) -> uid -> getpwuid_r, so the column is populated (and shown) on
    /// macOS too. Linux and Windows both already populated it. This is effectively "always true"
    /// today but is kept as its own flag rather than removed, in case a future platform needs the
    /// gate again.
    /// </summary>
    public bool ShowUserColumn => true;

    /// <summary>
    /// True when the active process provider actually populates <see cref="ProcessRowViewModel.Description"/>.
    /// False on macOS — MacOSProcessProvider always leaves Description empty.
    /// </summary>
    public bool ShowDescriptionColumn => !OperatingSystem.IsMacOS();

    // 4E: Per-selection CTS — cancelled each time SelectedProcess changes to abort stale detail loads
    private CancellationTokenSource _detailCts = new();
    private IDisposable? _subscription;
    private IDisposable? _leakSubscription;
    private bool _disposed;

    // Master cache: all live processes keyed by PID.
    // Allows in-place property updates so the DataGrid never loses sort state or selection.
    private readonly Dictionary<int, ProcessRowViewModel> _allRows = new();
    // Pre-allocated working dictionary reused each tick to avoid 300-entry allocs
    private readonly Dictionary<int, ProcessInfo> _liveByPid = new(400);

    [ObservableProperty]
    private ObservableCollection<ProcessRowViewModel> _processes = [];

    [ObservableProperty]
    private ProcessRowViewModel? _selectedProcess;

    // ── Multi-select (Ctrl/Cmd+click, Shift+click) ───────────────────────────
    // DataGrid.SelectedItems (Avalonia 11.2.3) is a get-only IList, not a bindable
    // AvaloniaProperty, so the View syncs it into this collection via a SelectionChanged
    // handler (same code-behind-wires-grid-events pattern as OnGridSorting). SelectedProcess
    // above keeps tracking the DataGrid's single "current" item — its own binding is untouched,
    // so single-click UX (details pane, etc.) is unchanged.
    public ObservableCollection<ProcessRowViewModel> SelectedProcesses { get; } = [];

    /// <summary>True when 2 or more processes are selected — gates batch confirmation/labeling.</summary>
    public bool HasMultiSelection => SelectedProcesses.Count > 1;

    public string EndProcessMenuLabel =>
        HasMultiSelection ? $"End {SelectedProcesses.Count} Processes" : "End Process";

    public string EndProcessTreeMenuLabel =>
        HasMultiSelection ? $"End {SelectedProcesses.Count} Process Trees" : "End Process Tree";

    /// <summary>
    /// Called by the View's DataGrid.SelectionChanged handler to sync the live multi-selection.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<ProcessRowViewModel> selected)
    {
        SelectedProcesses.Clear();
        foreach (var row in selected) SelectedProcesses.Add(row);
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(EndProcessMenuLabel));
        OnPropertyChanged(nameof(EndProcessTreeMenuLabel));
    }

    private static BatchTarget ToBatchTarget(ProcessRowViewModel row) => new(row.Pid, row.Name, row.Category);

    /// <summary>
    /// Resolves the current action target set: the live multi-selection when populated, falling
    /// back to the single anchor <see cref="SelectedProcess"/> otherwise (covers any
    /// programmatic selection — e.g. <see cref="NavigateToProcessMessage"/> — that
    /// hasn't round-tripped through the View's DataGrid.SelectionChanged sync yet).
    /// </summary>
    private List<BatchTarget> GetSelectionTargets()
    {
        IEnumerable<ProcessRowViewModel> rows = SelectedProcesses.Count > 0
            ? SelectedProcesses
            : SelectedProcess is not null ? [SelectedProcess] : [];
        return rows.Select(ToBatchTarget).ToList();
    }

    /// <summary>Shows a single OK/Cancel confirmation dialog; returns true iff the user confirmed.</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var mainWin = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWin is null) return false;
        var dialog = new ConfirmationDialog(title, message);
        var result = await dialog.ShowDialog<bool?>(mainWin);
        return result == true;
    }

    [ObservableProperty]
    private ProcessDetailViewModel? _selectedDetails;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSimplifiedView = false;

    [ObservableProperty]
    private int _totalProcessCount;

    [ObservableProperty]
    private int _totalThreadCount;

    [ObservableProperty]
    private double _totalCpuPercent;

    [ObservableProperty]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private bool _isDetailPanelVisible = false;

    [ObservableProperty]
    private bool _isGroupPanelVisible = false;

    /// <summary>True when the selected process has a saved persistent preference.</summary>
    [ObservableProperty]
    private bool _hasPreference;

    [ObservableProperty]
    private bool _isTreeViewActive;

    /// <summary>True when the detail panel should be shown (has selection AND toggle is on).</summary>
    public bool IsDetailPanelShown => SelectedDetails is not null && IsDetailPanelVisible;

    public ObservableCollection<GroupSummary> GroupSummaries { get; } = [];

    /// <summary>True when there is at least one group summary to display.</summary>
    public bool HasGroupSummaries => GroupSummaries.Count > 0;

    /// <summary>
    /// Tracks the set of PIDs shown in the last tree-mode render so we avoid
    /// clearing + rebuilding the list on every tick when nothing structural changed.
    /// </summary>
    private readonly HashSet<int> _currentTreePids = new();

    [ObservableProperty]
    private IReadOnlyList<ModuleInfo> _processModules = [];

    [ObservableProperty]
    private IReadOnlyList<ThreadInfo> _processThreads = [];

    [ObservableProperty]
    private IReadOnlyList<EnvironmentEntry> _processEnvironment = [];

    [ObservableProperty]
    private IReadOnlyList<HandleInfo> _processHandles = [];

    [ObservableProperty]
    private IReadOnlyList<MemoryRegionInfo> _processMemoryMap = [];

    [ObservableProperty]
    private string _handleCountLabel = "";

    /// <summary>Sort column path persisted here so it survives tab switches (the View is recreated each time).</summary>
    public string? SortMemberPath { get; set; }
    /// <summary>Sort direction persisted here so it survives tab switches.</summary>
    public System.ComponentModel.ListSortDirection SortDirection { get; set; } = System.ComponentModel.ListSortDirection.Ascending;

    public ProcessGroupsViewModel? GroupsViewModel { get; }

    // ── Column customization (Processes grid only, v1) ──────────────────────────────────────
    // The 12 hideable columns, in the exact order and with the exact header text used by
    // ProcessesView.axaml's DataGrid.Columns. Name is intentionally absent — it is never
    // hideable. Key is the stable identifier persisted in AppSettings.ProcessColumnsHidden
    // (never Header) so a future copy tweak can't silently strand a user's saved preference.
    private static readonly (string Key, string Header)[] _hideableColumns =
    {
        ("pid",      "PID"),
        ("cpu",      "CPU"),
        ("memory",   "Memory"),
        ("leak",     "Leak"),
        ("io",       "I/O"),
        ("impact",   "Impact"),
        ("rules",    "Rules"),
        ("group",    "Group"),
        ("priority", "Priority"),
        ("threads",  "Threads"),
        ("handles",  "Handles"),
        ("user",     "User"),
    };

    /// <summary>
    /// Show/hide options for the Processes grid's hideable columns (Task 2 binds each option's
    /// IsVisible to its DataGridColumn.IsVisible from a header context menu). Initialized once
    /// from <see cref="AppSettings.ProcessColumnsHidden"/>; toggling an option here updates that
    /// list and saves settings (see <see cref="OnColumnOptionVisibilityChanged"/>).
    /// </summary>
    public ObservableCollection<ProcessColumnOption> ColumnOptions { get; } = [];

    // Set while ResetColumns() is bulk-flipping every option back to visible, so each
    // individual flip's VisibilityChanged handler updates AppSettings.ProcessColumnsHidden
    // (needed for UI binding correctness) without also firing a redundant Save() per column —
    // ResetColumns() clears the list and saves exactly once at the end instead.
    private bool _suppressColumnPersist;

    public ProcessesViewModel(IProcessProvider processProvider, AppSettings appSettings,
        IPlatformCapabilities? platformCapabilities = null,
        ProcessPreferenceStore? preferenceStore = null,
        MemoryLeakDetectionService? leakService = null,
        ProcessGroupStore? groupStore = null,
        ProcessGroupsViewModel? groupsViewModel = null,
        SettingsService? settingsService = null)
    {
        _processProvider  = processProvider;
        _appSettings      = appSettings;
        _preferenceStore  = preferenceStore;
        _leakService      = leakService;
        _groupStore       = groupStore;
        _settingsService  = settingsService;
        _batchActions     = new BatchProcessActions(processProvider);
        GroupsViewModel   = groupsViewModel;
        Platform          = platformCapabilities ?? new MockPlatformCapabilities();
        Title = "Processes";
        StartMonitoring(_appSettings.UpdateIntervalMs);

        foreach (var (key, header) in _hideableColumns)
        {
            var option = new ProcessColumnOption(key, header,
                isVisible: !_appSettings.ProcessColumnsHidden.Contains(key));
            option.VisibilityChanged += OnColumnOptionVisibilityChanged;
            ColumnOptions.Add(option);
        }

        WeakReferenceMessenger.Default.Register<NavigateToProcessMessage>(this, (_, msg) =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_allRows.TryGetValue(msg.Pid, out var row))
                    SelectedProcess = row;
            }));

        // Restart process stream when the global update interval changes
        WeakReferenceMessenger.Default.Register<MetricsIntervalChangedMessage>(this, (_, msg) =>
            Dispatcher.UIThread.InvokeAsync(() => StartMonitoring((int)msg.Interval.TotalMilliseconds)));

        // Re-evaluate Category converters when the theme changes so light-mode colors apply.
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeChanged;

        // Subscribe to leak suspects and update matching rows
        if (_leakService is not null)
        {
            _leakSubscription = _leakService.Suspects
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(UpdateLeakIndicators);
        }
    }

    /// <summary>
    /// Fires whenever a <see cref="ProcessColumnOption.IsVisible"/> actually changes (the
    /// CommunityToolkit-generated setter no-ops on an unchanged value, so this never fires for
    /// the constructor's initial state or for ResetColumns() re-flipping already-visible
    /// columns). Keeps AppSettings.ProcessColumnsHidden in sync and persists it — unless
    /// <see cref="_suppressColumnPersist"/> is set, in which case ResetColumns() owns the save.
    /// </summary>
    private void OnColumnOptionVisibilityChanged(ProcessColumnOption option)
    {
        ApplyColumnVisibility(option);
        if (!_suppressColumnPersist)
            _settingsService?.Save();
    }

    private void ApplyColumnVisibility(ProcessColumnOption option)
    {
        var hidden = _appSettings.ProcessColumnsHidden;
        if (option.IsVisible)
            hidden.Remove(option.Key);
        else if (!hidden.Contains(option.Key))
            hidden.Add(option.Key);
    }

    /// <summary>Shows every hideable column again and clears the persisted hidden-column list.</summary>
    [RelayCommand]
    private void ResetColumns()
    {
        _suppressColumnPersist = true;
        try
        {
            foreach (var option in ColumnOptions)
                option.IsVisible = true;
        }
        finally { _suppressColumnPersist = false; }

        _appSettings.ProcessColumnsHidden.Clear();
        _settingsService?.Save();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var row in _allRows.Values)
                row.NotifyThemeChanged();
        });
    }

    private void UpdateLeakIndicators(IReadOnlyList<NexusMonitor.Core.Health.MemoryLeakSuspect> suspects)
    {
        // Clear all leak indicators first
        foreach (var row in _allRows.Values)
        {
            row.LeakRateMbPerHour = 0;
            row.IsLeakCritical    = false;
            row.IsLeakWarning     = false;
        }

        // Apply new leak data
        foreach (var suspect in suspects)
        {
            if (_allRows.TryGetValue(suspect.Pid, out var row))
            {
                row.LeakRateMbPerHour = suspect.LeakRateBytesPerHour / 1024.0 / 1024.0;
                row.IsLeakCritical    = suspect.Confidence > 0.8;
                row.IsLeakWarning     = !row.IsLeakCritical;
            }
        }
    }

    private void StartMonitoring(int intervalMs)
    {
        _subscription?.Dispose();
        _subscription = null;
        _subscription = _processProvider
            .GetProcessStream(TimeSpan.FromMilliseconds(intervalMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateProcessList);
    }

    /// <inheritdoc/>
    public void Activate()
    {
        if (_subscription is not null) return;  // idempotent guard
        // Clear stale row cache so the GC can reclaim ProcessRowViewModels that
        // accumulated while the tab was visible. DataGrid repopulates on first tick (~2s).
        _allRows.Clear();
        Processes.Clear();
        StartMonitoring(_appSettings.UpdateIntervalMs);
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    // Already on UI thread via ObserveOn(RxApp.MainThreadScheduler) — no inner Post needed.
    private void UpdateProcessList(IReadOnlyList<ProcessInfo> processes)
    {
        // Reuse pre-allocated dictionary to avoid 300-entry heap alloc each tick
        _liveByPid.Clear();
        foreach (var p in processes) _liveByPid[p.Pid] = p;
        var liveByPid = _liveByPid;

        // ── Compute impact score totals once per tick ──────────────────────────
        var totals = ImpactScoreCalculator.ComputeTotals(processes);

        // ── 1. Update existing rows in-place; create rows for new PIDs ─────────
        foreach (var (pid, info) in liveByPid)
        {
            var impact     = ImpactScoreCalculator.Calculate(info, totals);
            var activeRule = _appSettings.Rules.FirstOrDefault(r => r.IsEnabled && r.Matches(info.Name));
            var group      = _groupStore?.FindGroupForProcess(info.Name);

            if (_allRows.TryGetValue(pid, out var row))
            {
                // Mutate observable properties — DataGrid keeps its sort & selection
                row.CpuPercent      = info.CpuPercent;
                row.WorkingSetBytes = info.WorkingSetBytes;
                row.SetIoRates(info.IoReadBytesPerSec, info.IoWriteBytesPerSec);
                row.ThreadCount     = info.ThreadCount;
                row.HandleCount     = info.HandleCount;
                row.BasePriority    = info.BasePriority;
                row.PushCpuHistory(info.CpuPercent);
                row.ImpactScore     = impact;
                row.HasActiveRule   = activeRule is not null;
                row.RuleSummary     = activeRule?.Summary ?? string.Empty;
                row.GroupName       = group?.Name  ?? string.Empty;
                row.GroupColor      = group?.Color ?? string.Empty;
            }
            else
            {
                var newRow = new ProcessRowViewModel(info)
                {
                    ImpactScore   = impact,
                    HasActiveRule = activeRule is not null,
                    RuleSummary   = activeRule?.Summary ?? string.Empty,
                    GroupName     = group?.Name  ?? string.Empty,
                    GroupColor    = group?.Color ?? string.Empty,
                };
                _allRows[pid] = newRow;
            }
        }

        // ── 2. Evict dead processes ────────────────────────────────────────────
        var deadPids = _allRows.Keys.Where(pid => !liveByPid.ContainsKey(pid)).ToList();
        foreach (var pid in deadPids)
            _allRows.Remove(pid);

        // ── 3. Update totals ──────────────────────────────────────────────────
        TotalProcessCount = processes.Count;
        TotalThreadCount  = processes.Sum(p => p.ThreadCount);
        TotalCpuPercent   = Math.Round(processes.Sum(p => p.CpuPercent), 1);

        // ── 3b. Update group summaries ────────────────────────────────────────
        var summaries = _allRows.Values
            .Where(r => !string.IsNullOrEmpty(r.GroupName))
            .GroupBy(r => r.GroupName)
            .Select(g =>
            {
                var first = g.First();
                return new GroupSummary(
                    g.Key,
                    first.GroupColor,
                    g.Count(),
                    g.Sum(r => r.CpuPercent),
                    g.Sum(r => r.WorkingSetBytes));
            })
            .OrderBy(s => s.Name)
            .ToList();

        // Sync to ObservableCollection (avoid full clear+re-add to reduce flicker)
        for (int i = GroupSummaries.Count - 1; i >= 0; i--)
            if (!summaries.Any(s => s.Name == GroupSummaries[i].Name))
                GroupSummaries.RemoveAt(i);
        foreach (var s in summaries)
        {
            var idx = -1;
            for (int i = 0; i < GroupSummaries.Count; i++)
                if (GroupSummaries[i].Name == s.Name) { idx = i; break; }
            if (idx >= 0) GroupSummaries[idx] = s;
            else GroupSummaries.Add(s);
        }
        OnPropertyChanged(nameof(HasGroupSummaries));

        // ── 4. Sync the visible collection with the current filter ────────────
        ApplyFilter();
    }

    /// <summary>
    /// Filters <see cref="_allRows"/> against <see cref="SearchText"/> and syncs
    /// the <see cref="Processes"/> collection.  Must be called on the UI thread.
    /// In flat mode: in-place add/remove preserves DataGrid sort state.
    /// In tree mode: rebuilds only when the PID set changes, keeping row order stable.
    /// </summary>
    private void ApplyFilter()
    {
        int selectedPid = SelectedProcess?.Pid ?? -1;

        var wantPids = new HashSet<int>(
            string.IsNullOrWhiteSpace(SearchText)
                ? _allRows.Keys
                : _allRows.Values
                    .Where(r =>
                        r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)        ||
                        r.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        r.Pid.ToString().Contains(SearchText)                                  ||
                        r.GroupName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Pid));

        if (IsTreeViewActive)
            ApplyTreeFilter(wantPids, selectedPid);
        else
            ApplyFlatFilter(wantPids, selectedPid);
    }

    /// <summary>Standard flat-list filter: add / remove rows in-place preserving DataGrid sort.</summary>
    private void ApplyFlatFilter(HashSet<int> wantPids, int selectedPid)
    {
        // Reset tree depths so indents disappear when leaving tree mode
        foreach (var r in _allRows.Values) r.TreeDepth = 0;

        // Remove rows that left the visible set
        for (int i = Processes.Count - 1; i >= 0; i--)
            if (!wantPids.Contains(Processes[i].Pid))
                Processes.RemoveAt(i);

        // Add rows newly in the visible set
        var currentPids = new HashSet<int>(Processes.Select(r => r.Pid));
        foreach (var pid in wantPids)
            if (!currentPids.Contains(pid) && _allRows.TryGetValue(pid, out var row))
                Processes.Add(row);

        if (selectedPid >= 0 && SelectedProcess is null)
            SelectedProcess = Processes.FirstOrDefault(r => r.Pid == selectedPid);
    }

    /// <summary>
    /// Tree-ordered filter: processes appear in depth-first parent→child order with
    /// <see cref="ProcessRowViewModel.TreeDepth"/> set for visual indentation.
    /// Only rebuilds the collection when the set of visible PIDs changes to avoid
    /// scroll-position disruption on every 1-second tick.
    /// </summary>
    private void ApplyTreeFilter(HashSet<int> wantPids, int selectedPid)
    {
        // Structural rebuild only when the visible PID set actually changed
        if (_currentTreePids.SetEquals(wantPids)) return;
        _currentTreePids.Clear();
        foreach (var p in wantPids) _currentTreePids.Add(p);

        // Build parent → children map from the full live set
        var allPids    = new HashSet<int>(_allRows.Keys);
        var childrenOf = new Dictionary<int, List<ProcessRowViewModel>>();
        foreach (var row in _allRows.Values)
        {
            if (!childrenOf.TryGetValue(row.ParentPid, out var list))
                childrenOf[row.ParentPid] = list = new List<ProcessRowViewModel>();
            list.Add(row);
        }

        // Roots: processes whose parent is not a live process
        var roots = _allRows.Values
            .Where(r => !allPids.Contains(r.ParentPid))
            .OrderBy(r => r.Name)
            .ToList();

        // Depth-first traversal with cycle guard and depth limit
        var ordered = new List<ProcessRowViewModel>();
        var visited = new HashSet<int>();
        void Traverse(ProcessRowViewModel row, int depth)
        {
            if (!visited.Add(row.Pid)) return;  // cycle guard
            if (depth > 64) return;              // depth limit safety net
            if (wantPids.Contains(row.Pid))
            {
                row.TreeDepth = depth;
                ordered.Add(row);
            }
            if (childrenOf.TryGetValue(row.Pid, out var kids))
                foreach (var kid in kids.OrderBy(k => k.Name))
                    Traverse(kid, depth + 1);
        }
        foreach (var root in roots) Traverse(root, 0);

        // Rebuild collection (only happens on structural change, not every tick)
        Processes.Clear();
        foreach (var row in ordered) Processes.Add(row);

        if (selectedPid >= 0)
            SelectedProcess = Processes.FirstOrDefault(r => r.Pid == selectedPid);
    }

    [RelayCommand]
    private Task KillProcess() => KillSelection(killTree: false);

    [RelayCommand]
    private Task KillProcessTree() => KillSelection(killTree: true);

    /// <summary>
    /// Shared kill/kill-tree body. A single-target selection runs the exact pre-multi-select
    /// code path (same call, same error message, no confirmation dialog — requirement: "single
    /// process flow keeps today's behavior exactly"). Two or more targets show ONE confirmation
    /// dialog, then run through <see cref="_batchActions"/> so one process's failure never
    /// aborts the rest of the batch, ending in one honest summary in <see cref="LastError"/>.
    /// </summary>
    private async Task KillSelection(bool killTree)
    {
        var targets = GetSelectionTargets();
        if (targets.Count == 0) return;

        if (targets.Count == 1)
        {
            try
            {
                LastError = string.Empty;
                await _processProvider.KillProcessAsync(targets[0].Pid, killTree, _cts.Token);
            }
            catch (Exception ex) { LastError = $"{(killTree ? "Kill tree" : "Kill")} failed: {ex.Message}"; }
            return;
        }

        var names = targets.Select(t => t.Name).ToList();
        if (!await ConfirmAsync("End Processes", BatchConfirmationText.BuildKillConfirmationMessage(names)))
            return;

        LastError = string.Empty;
        var result = await _batchActions.KillAsync(targets, killTree, _cts.Token);
        LastError = result.Summarize("Ended");
    }

    [RelayCommand]
    private async Task SuspendProcess()
    {
        if (SelectedProcess is null) return;
        try
        {
            LastError = string.Empty;
            await _processProvider.SuspendProcessAsync(SelectedProcess.Pid, _cts.Token);
        }
        catch (Exception ex) { LastError = $"Suspend failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ResumeProcess()
    {
        if (SelectedProcess is null) return;
        try
        {
            LastError = string.Empty;
            await _processProvider.ResumeProcessAsync(SelectedProcess.Pid, _cts.Token);
        }
        catch (Exception ex) { LastError = $"Resume failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SetPriority(string priorityName)
    {
        var targets = GetSelectionTargets();
        if (targets.Count == 0) return;
        if (!Enum.TryParse<ProcessPriority>(priorityName, out var priority)) return;

        if (targets.Count == 1)
        {
            try
            {
                LastError = string.Empty;
                await _processProvider.SetPriorityAsync(targets[0].Pid, priority, _cts.Token);
            }
            catch (Exception ex) { LastError = $"Set priority failed: {ex.Message}"; }
            return;
        }

        LastError = string.Empty;
        var result = await _batchActions.SetPriorityAsync(targets, priority, _cts.Token);
        LastError = result.Summarize("Updated priority for");
    }

    [RelayCommand]
    private async Task TrimMemory()
    {
        if (SelectedProcess is null) return;
        try
        {
            LastError = string.Empty;
            await _processProvider.TrimWorkingSetAsync(SelectedProcess.Pid, _cts.Token);
            LastError = $"Working set trimmed for {SelectedProcess.Name}";
        }
        catch (Exception ex) { LastError = $"Trim failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        var path = SelectedProcess?.ImagePath ?? string.Empty;
        if (string.IsNullOrEmpty(path)) return;
        ShellHelper.OpenFileLocation(path);
    }

    [RelayCommand]
    private void SearchOnline()
    {
        var name = SelectedProcess?.Name ?? string.Empty;
        if (name.Length > 0)
            ShellHelper.OpenUrl($"https://www.google.com/search?q={Uri.EscapeDataString(name + " process")}");
    }

    [RelayCommand]
    private async Task CreateDumpFile()
    {
        if (SelectedProcess is null) return;
        try
        {
            LastError = string.Empty;
            var mainWin = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWin is null) return;

            var ext = Platform.DumpFileExtension;
            var sp = mainWin.StorageProvider;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save dump file for {SelectedProcess.Name}",
                SuggestedFileName = $"{SelectedProcess.Name}_{SelectedProcess.Pid}.{ext}",
                FileTypeChoices =
                [
                    new FilePickerFileType("Dump files") { Patterns = [$"*.{ext}"] },
                    new FilePickerFileType("All files")  { Patterns = ["*"] },
                ],
            });
            if (file is null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            await _processProvider.CreateDumpFileAsync(SelectedProcess.Pid, path, _cts.Token);
            LastError = $"Dump saved: {path}";
        }
        catch (Exception ex) { LastError = $"Dump failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SetAffinity()
    {
        var targets = GetSelectionTargets();
        if (targets.Count == 0) return;
        try
        {
            LastError = string.Empty;

            // The CPU checklist is seeded from the anchor (first-selected) process's current
            // mask — the system mask (which CPUs exist) is machine-wide and identical for every
            // target, so one dialog covers the whole selection.
            var anchor = targets[0];
            var (procMask, sysMask) = await _processProvider.GetAffinityMasksAsync(anchor.Pid, _cts.Token);

            var mainWin = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWin is null) return;

            var label = targets.Count == 1
                ? $"{anchor.Name} (PID {anchor.Pid})"
                : $"{targets.Count} processes";

            var dialog = new AffinityDialog(label, procMask, sysMask);

            var result = await dialog.ShowDialog<long?>(mainWin);
            if (result is not long newMask || newMask <= 0) return;

            if (targets.Count == 1)
            {
                await _processProvider.SetAffinityAsync(anchor.Pid, newMask, _cts.Token);
                return;
            }

            var batchResult = await _batchActions.SetAffinityAsync(targets, newMask, _cts.Token);
            LastError = batchResult.Summarize("Set affinity for");
        }
        catch (Exception ex) { LastError = $"Set affinity failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task FindWindow()
    {
        try
        {
            LastError = string.Empty;
            var overlay = new FindWindowOverlay();

            var mainWin = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            var result = await overlay.ShowDialog<int?>(mainWin!);
            int pid = result ?? 0;
            if (pid > 0)
            {
                // Navigate to that PID in the process list
                if (_allRows.TryGetValue(pid, out var row))
                    SelectedProcess = row;
                else
                    LastError = $"PID {pid} not found in process list";
            }
        }
        catch (Exception ex) { LastError = $"Find window failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ShowProperties()
    {
        if (SelectedProcess is null) return;
        // Ensure the detail panel is visible and selected process is focused
        IsDetailPanelVisible = true;
    }

    [RelayCommand]
    private void SavePreference()
    {
        if (SelectedProcess is null || _preferenceStore is null) return;
        var pref = new ProcessPreference
        {
            ExeName       = SelectedProcess.Name,
            Priority      = null, // populated below from the process's current state
        };
        // We don't have current priority on ProcessRowViewModel directly;
        // save what the user has set via context menu in this session if any,
        // otherwise just mark the exe for auto-priority-Normal on next launch.
        // For now, persist the current row's display values as-is.
        _preferenceStore.Upsert(pref);
        HasPreference = true;
        LastError = $"Settings remembered for {SelectedProcess.Name}";
    }

    [RelayCommand]
    private void SavePreferenceWithCurrentPriority(string priorityName)
    {
        if (SelectedProcess is null || _preferenceStore is null) return;
        if (!Enum.TryParse<ProcessPriority>(priorityName, out var priority)) return;
        var existing = _preferenceStore.Get(SelectedProcess.Name) ?? new ProcessPreference { ExeName = SelectedProcess.Name };
        existing.Priority = priority;
        _preferenceStore.Upsert(existing);
        HasPreference = true;
        LastError = $"Priority {priority} remembered for {SelectedProcess.Name}";
    }

    [RelayCommand]
    private void ClearPreference()
    {
        if (SelectedProcess is null || _preferenceStore is null) return;
        _preferenceStore.Delete(SelectedProcess.Name);
        HasPreference = false;
        LastError = $"Saved settings cleared for {SelectedProcess.Name}";
    }

    // Filter from the in-memory cache — no async round-trip to the provider needed.
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsDetailPanelVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsDetailPanelShown));

    partial void OnIsTreeViewActiveChanged(bool value)
    {
        // When leaving tree mode, reset depths and force a full rebuild on next filter pass
        if (!value) foreach (var r in _allRows.Values) r.TreeDepth = 0;
        _currentTreePids.Clear();
        ApplyFilter();
    }

    partial void OnSelectedProcessChanged(ProcessRowViewModel? value)
    {
        // Update HasPreference for the newly selected process
        HasPreference = value is not null && _preferenceStore?.Get(value.Name) is not null;

        // Dispose the old detail view model to unsubscribe its PropertyChanged handler
        (SelectedDetails as IDisposable)?.Dispose();
        SelectedDetails = value is null ? null : new ProcessDetailViewModel(value);
        OnPropertyChanged(nameof(IsDetailPanelShown));
        ProcessModules     = [];
        ProcessThreads     = [];
        ProcessEnvironment = [];
        ProcessHandles     = [];
        ProcessMemoryMap   = [];
        HandleCountLabel   = "";

        // 4E: Cancel any in-flight detail loads from the previous selection
        _detailCts.Cancel();
        _detailCts.Dispose();
        _detailCts = new CancellationTokenSource();

        if (value is not null)
        {
            _ = LoadModulesAsync(value.Pid, _detailCts.Token);
            _ = LoadThreadsAsync(value.Pid, _detailCts.Token);
            _ = LoadEnvironmentAsync(value.Pid, _detailCts.Token);
            _ = LoadHandlesAsync(value.Pid, _detailCts.Token);
            _ = LoadMemoryMapAsync(value.Pid, _detailCts.Token);
        }
    }

    private async Task LoadModulesAsync(int pid, CancellationToken ct)
    {
        try
        {
            var modules = await _processProvider.GetModulesAsync(pid, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessModules = modules;
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessModules = [];
            });
        }
    }

    private async Task LoadThreadsAsync(int pid, CancellationToken ct)
    {
        try
        {
            var threads = await _processProvider.GetThreadsAsync(pid, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessThreads = threads;
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessThreads = [];
            });
        }
    }

    private async Task LoadEnvironmentAsync(int pid, CancellationToken ct)
    {
        try
        {
            var env = await _processProvider.GetEnvironmentAsync(pid, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessEnvironment = env;
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessEnvironment = [];
            });
        }
    }

    private async Task LoadHandlesAsync(int pid, CancellationToken ct)
    {
        try
        {
            var handles = await _processProvider.GetHandlesAsync(pid, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                {
                    ProcessHandles   = handles;
                    HandleCountLabel = $"{handles.Count} handles";
                }
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessHandles = [];
            });
        }
    }

    private async Task LoadMemoryMapAsync(int pid, CancellationToken ct)
    {
        try
        {
            var regions = await _processProvider.GetMemoryMapAsync(pid, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessMemoryMap = regions;
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProcess?.Pid == pid)
                    ProcessMemoryMap = [];
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _detailCts.Cancel();
        _detailCts.Dispose();
        _subscription?.Dispose();
        _leakSubscription?.Dispose();
        (SelectedDetails as IDisposable)?.Dispose();
        _allRows.Clear();
        foreach (var option in ColumnOptions)
            option.VisibilityChanged -= OnColumnOptionVisibilityChanged;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnThemeChanged;
    }
}

/// <summary>
/// Show/hide state for one hideable column in the Processes grid. <see cref="Key"/> is the
/// stable identifier persisted in <see cref="AppSettings.ProcessColumnsHidden"/> — never
/// <see cref="Header"/>, so a future display-text change never breaks a user's saved
/// preference. Owned by <see cref="ProcessesViewModel.ColumnOptions"/>; Task 2's header
/// context menu binds each option's Header/IsVisible directly.
/// </summary>
public partial class ProcessColumnOption : ObservableObject
{
    public string Key    { get; }
    public string Header { get; }

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>
    /// Raised after <see cref="IsVisible"/> actually changes value — CommunityToolkit's
    /// generated setter no-ops (and never raises this) when the new value equals the current
    /// one, so this never fires from the constructor or from re-setting an already-matching
    /// value. The owning <see cref="ProcessesViewModel"/> subscribes to persist the change.
    /// </summary>
    public event Action<ProcessColumnOption>? VisibilityChanged;

    public ProcessColumnOption(string key, string header, bool isVisible)
    {
        Key = key;
        Header = header;
        _isVisible = isVisible;
    }

    partial void OnIsVisibleChanged(bool value) => VisibilityChanged?.Invoke(this);
}

public partial class ProcessRowViewModel : ObservableObject
{
    public int Pid { get; }
    public int ParentPid { get; }
    public string Name { get; }
    public string Description { get; }
    public string UserName { get; }
    public string ImagePath { get; }
    public string CommandLine { get; }
    public DateTime StartTime { get; }
    public bool IsElevated { get; }
    public ProcessCategory Category { get; }
    public ProcessState State { get; }
    public bool IsAccessDenied { get; }
    public long PrivateBytes { get; }
    public long VirtualBytes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuDisplay))]
    private double _cpuPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemDisplay))]
    private long _workingSetBytes;

    // No [NotifyPropertyChangedFor(nameof(IoDisplay))] here — batched via SetIoRates()
    // to avoid firing IoDisplay PropertyChanged twice per tick.
    private long _ioReadBytesPerSec;
    private long _ioWriteBytesPerSec;

    public long IoReadBytesPerSec
    {
        get => _ioReadBytesPerSec;
        set => SetProperty(ref _ioReadBytesPerSec, value);
    }

    public long IoWriteBytesPerSec
    {
        get => _ioWriteBytesPerSec;
        set => SetProperty(ref _ioWriteBytesPerSec, value);
    }

    /// <summary>
    /// Updates both I/O rate fields and fires <see cref="IoDisplay"/> only once.
    /// Avoids double PropertyChanged notification from the two backing fields.
    /// </summary>
    public void SetIoRates(long readPerSec, long writePerSec)
    {
        bool changed = SetProperty(ref _ioReadBytesPerSec,  readPerSec,  nameof(IoReadBytesPerSec))
                     | SetProperty(ref _ioWriteBytesPerSec, writePerSec, nameof(IoWriteBytesPerSec));
        if (changed) OnPropertyChanged(nameof(IoDisplay));
    }

    [ObservableProperty] private int _threadCount;
    [ObservableProperty] private int _handleCount;
    [ObservableProperty] private int _basePriority;

    // ── Impact score (Phase 1 Dashboard) ─────────────────────────────────────
    /// <summary>Composite 0-100 score of how much this process affects system performance.</summary>
    [ObservableProperty] private double _impactScore;

    // ── Rule indicators (Phase 1 Dashboard) ──────────────────────────────────
    /// <summary>True when at least one active rule applies to this process.</summary>
    [ObservableProperty] private bool _hasActiveRule;
    /// <summary>Human-readable rule summary for tooltip display.</summary>
    [ObservableProperty] private string _ruleSummary = string.Empty;

    /// <summary>Depth in the process tree (0 = root). Used for indentation in tree-view mode.</summary>
    [ObservableProperty] private int _treeDepth;

    // ── CPU history for the detail-panel sparkline ───────────────────────────
    private readonly double[] _cpuHistory = new double[60];
    private int _histIdx; // monotonically increasing write cursor

    /// <summary>Appends the latest CPU sample to the ring buffer (called every tick).</summary>
    public void PushCpuHistory(double cpu)
    {
        _cpuHistory[_histIdx % 60] = cpu;
        _histIdx++;
    }

    /// <summary>Returns up to 60 CPU samples ordered oldest-first.</summary>
    public double[] GetCpuHistory()
    {
        int filled = Math.Min(_histIdx, 60);
        if (filled == 0) return [];
        var result = new double[filled];
        int start  = _histIdx > 60 ? _histIdx % 60 : 0;
        for (int i = 0; i < filled; i++)
            result[i] = _cpuHistory[(start + i) % 60];
        return result;
    }

    // ── Process group membership ──────────────────────────────────────────────
    [ObservableProperty] private string _groupName  = string.Empty;
    [ObservableProperty] private string _groupColor = string.Empty;

    // ── Memory leak indicators ────────────────────────────────────────────────
    [ObservableProperty] private double _leakRateMbPerHour;
    [ObservableProperty] private bool   _isLeakCritical;
    [ObservableProperty] private bool   _isLeakWarning;

    private static readonly IBrush _dangerBrush  = new SolidColorBrush(Color.Parse("#FF453A"));
    private static readonly IBrush _warningBrush = new SolidColorBrush(Color.Parse("#FF9F0A"));

    public string LeakRateDisplay => LeakRateMbPerHour switch
    {
        <= 0 => "",
        < 1  => $"↑ {LeakRateMbPerHour * 1024:F0} KB/hr",
        _    => $"↑ {LeakRateMbPerHour:F0} MB/hr",
    };

    public IBrush LeakRateBrush => IsLeakCritical ? _dangerBrush
        : IsLeakWarning ? _warningBrush : Brushes.Transparent;

    partial void OnLeakRateMbPerHourChanged(double value)
    {
        OnPropertyChanged(nameof(LeakRateDisplay));
        OnPropertyChanged(nameof(LeakRateBrush));
    }

    partial void OnIsLeakCriticalChanged(bool value)
    {
        OnPropertyChanged(nameof(LeakRateBrush));
    }

    partial void OnIsLeakWarningChanged(bool value)
    {
        OnPropertyChanged(nameof(LeakRateBrush));
    }

    public string CpuDisplay => CpuPercent < 0.1 ? "" : $"{CpuPercent:F1}%";
    public string MemDisplay => FormatBytes(WorkingSetBytes);
    public string IoDisplay  => IoReadBytesPerSec + IoWriteBytesPerSec > 0
        ? $"R:{FormatBytes(IoReadBytesPerSec)}/s W:{FormatBytes(IoWriteBytesPerSec)}/s" : "";

    public ProcessRowViewModel(ProcessInfo p)
    {
        Pid                = p.Pid;
        ParentPid          = p.ParentPid;
        Name               = p.Name;
        Description        = p.Description;
        UserName           = p.UserName;
        ImagePath          = p.ImagePath;
        CommandLine        = p.CommandLine;
        StartTime          = p.StartTime;
        IsElevated         = p.IsElevated;
        Category           = p.Category;
        State              = p.State;
        IsAccessDenied     = p.AccessDenied;
        PrivateBytes       = p.PrivateBytesBytes;
        VirtualBytes       = p.VirtualBytesBytes;
        _cpuPercent        = p.CpuPercent;
        _workingSetBytes   = p.WorkingSetBytes;
        _ioReadBytesPerSec = p.IoReadBytesPerSec;
        _ioWriteBytesPerSec= p.IoWriteBytesPerSec;
        _threadCount       = p.ThreadCount;
        _handleCount       = p.HandleCount;
        _basePriority      = p.BasePriority;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Forces the DataGrid to re-evaluate Category-based converters (foreground/background brushes)
    /// after a theme switch.  Called by ProcessesViewModel when ActualThemeVariantChanged fires.
    /// </summary>
    public void NotifyThemeChanged() => OnPropertyChanged(nameof(Category));
}

/// <summary>
/// Wraps a <see cref="ProcessRowViewModel"/> and forwards its PropertyChanged
/// events so the details side panel stays live without requiring a re-selection.
/// Uses null/"" property name to tell Avalonia "re-read every binding".
/// Implements IDisposable to cleanly unsubscribe and prevent memory leaks.
/// </summary>
public class ProcessDetailViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProcessRowViewModel _row;
    private readonly PropertyChangedEventHandler _handler;
    public event PropertyChangedEventHandler? PropertyChanged;

    // Maps ProcessRowViewModel property names → ProcessDetailViewModel property names
    // Only includes properties that update at runtime (static ones like Name/ImagePath excluded)
    private static readonly Dictionary<string, string[]> _propMap = new()
    {
        [nameof(ProcessRowViewModel.CpuPercent)]        = [nameof(CpuPercent)],
        [nameof(ProcessRowViewModel.WorkingSetBytes)]   = [nameof(WorkingSet)],
        [nameof(ProcessRowViewModel.IoReadBytesPerSec)] = [nameof(ReadRate)],
        [nameof(ProcessRowViewModel.IoWriteBytesPerSec)]= [nameof(WriteRate)],
        // IoDisplay fires once (batched via SetIoRates) — map to both read/write display
        [nameof(ProcessRowViewModel.IoDisplay)]         = [nameof(ReadRate), nameof(WriteRate)],
        [nameof(ProcessRowViewModel.ThreadCount)]       = [nameof(Threads)],
        [nameof(ProcessRowViewModel.HandleCount)]       = [nameof(Handles)],
        [nameof(ProcessRowViewModel.PrivateBytes)]      = [nameof(PrivateBytes)],
        [nameof(ProcessRowViewModel.VirtualBytes)]      = [nameof(VirtualBytes)],
    };

    public ProcessDetailViewModel(ProcessRowViewModel row)
    {
        _row = row;
        // Forward only the properties that actually changed rather than re-evaluating all 20+
        _handler = (_, e) =>
        {
            if (e.PropertyName is null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                return;
            }
            if (_propMap.TryGetValue(e.PropertyName, out var targets))
            {
                foreach (var t in targets)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(t));
            }
            // Other properties (Name, ImagePath, etc.) don't change for a running process
        };
        _row.PropertyChanged += _handler;
    }

    /// <summary>Unsubscribes from the source row's PropertyChanged event.</summary>
    public void Dispose() => _row.PropertyChanged -= _handler;

    public string Name        => _row.Name;
    public int    Pid         => _row.Pid;
    public int    ParentPid   => _row.ParentPid;
    public string User        => MetricFormatting.OrDash(_row.UserName);
    // Null when a real value is showing (no tooltip); explains WHY the corresponding property
    // above has fallen back to "—" instead (Nexus couldn't read this attribute — e.g. access
    // denied on a protected process). See UnavailableMetricCopy.
    public string? UserUnavailableTooltip =>
        User == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
    public string State       => _row.State.ToString();
    public string Category    => _row.Category.ToString();
    public bool   IsElevated  => _row.IsElevated;

    public string CpuPercent  => $"{_row.CpuPercent:F1}%";
    public int    Threads     => _row.ThreadCount;
    public int    Handles     => _row.HandleCount;

    public string WorkingSet  => ProcessRowViewModel.FormatBytes(_row.WorkingSetBytes);
    public string PrivateBytes=> ProcessRowViewModel.FormatBytes(_row.PrivateBytes);
    public string VirtualBytes=> ProcessRowViewModel.FormatBytes(_row.VirtualBytes);

    public string ReadRate    => ProcessRowViewModel.FormatBytes(_row.IoReadBytesPerSec)  + "/s";
    public string WriteRate   => ProcessRowViewModel.FormatBytes(_row.IoWriteBytesPerSec) + "/s";

    // Kept as a hand-written ternary (not MetricFormatting.OrDash(DateTime,string)): the sentinel
    // check must run on the raw _row.StartTime, before ToLocalTime() — converting
    // default(DateTime) first can shift it away from (or throw converting) default depending on
    // the local UTC offset, so the availability check and the display formatting can't share the
    // same converted value the way the helper assumes.
    public string StartTime   => _row.StartTime == default ? MetricFormatting.Dash : _row.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string ImagePath   => MetricFormatting.OrDash(_row.ImagePath);
    public string CommandLine => MetricFormatting.OrDash(_row.CommandLine);
    public string Description => MetricFormatting.OrDash(_row.Description);

    public string? StartTimeUnavailableTooltip =>
        StartTime == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
    public string? ImagePathUnavailableTooltip =>
        ImagePath == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
    public string? CommandLineUnavailableTooltip =>
        CommandLine == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
    public string? DescriptionUnavailableTooltip =>
        Description == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;

    /// <summary>
    /// Polyline points for the CPU history sparkline.
    /// Re-evaluated each time the source row fires PropertyChanged (which happens every tick).
    /// Coordinates are normalised to a 240 × 44 canvas (Viewbox scales to fit the container).
    /// </summary>
    public IList<Avalonia.Point> CpuSparkPoints
    {
        get
        {
            var history = _row.GetCpuHistory();
            var pts     = new List<Avalonia.Point>();
            int n = history.Length;
            if (n < 2) return pts;
            const double W = 240, H = 44;
            for (int i = 0; i < n; i++)
            {
                double x = (double)i / (n - 1) * W;
                double y = H - Math.Clamp(history[i] / 100.0, 0, 1) * H;
                pts.Add(new Avalonia.Point(x, y));
            }
            return pts;
        }
    }
}

/// <summary>
/// Immutable snapshot of aggregate CPU and RAM for a single process group,
/// used to populate the summary strip above the process list.
/// </summary>
public sealed class GroupSummary
{
    public string Name     { get; }
    public string Color    { get; }
    public int    Count    { get; }
    public double CpuPct   { get; }
    public long   RamBytes { get; }

    public string CpuDisplay => $"{CpuPct:F1}%";
    public string RamDisplay => ProcessRowViewModel.FormatBytes(RamBytes);

    public GroupSummary(string name, string color, int count, double cpuPct, long ramBytes)
    {
        Name     = name;
        Color    = color;
        Count    = count;
        CpuPct   = cpuPct;
        RamBytes = ramBytes;
    }
}
