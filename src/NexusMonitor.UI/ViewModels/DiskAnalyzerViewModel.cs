using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Formatting;
using NexusMonitor.DiskAnalyzer.Analysis;
using NexusMonitor.DiskAnalyzer.Models;
using NexusMonitor.DiskAnalyzer.Scanning;

namespace NexusMonitor.UI.ViewModels;

/// <summary>
/// A single visible row in the flat-list folder tree.
/// Wraps a DiskNode with depth + expand/collapse state.
/// </summary>
public sealed class FolderTreeRow(DiskNode node, int depth, bool isExpanded = false)
    : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public DiskNode Node     { get; } = node;
    public int      Depth    { get; } = depth;

    private bool _isExpanded = isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool HasChildren => Node.IsDirectory && Node.Children.Count > 0;

    // Indentation: 16 px per depth level, +4 for spacing
    public Thickness NameMargin => new(Depth * 16 + 4, 0, 0, 0);

    // Column display values forwarded from Node
    public string PercentDisplay   => $"{Node.PercentOfParent:F1}%";
    public string SizeDisplay      => Node.SizeDisplay;
    public string AllocatedDisplay => Node.AllocatedDisplay;
    public string FileCount        => Node.FileCountDisplay;
    public string FolderCount      => Node.FolderCountDisplay;
    // The synthetic root node (MftScanner.cs) has no real MFT record and is left at
    // default(DateTime); render that as "—" instead of "0001-01-01" rather than fabricate a date.
    public string Modified         => MetricFormatting.OrDash(Node.LastModified, "yyyy-MM-dd");
    // Null when a real date is showing (no tooltip); explains WHY when Modified has fallen back
    // to "—" instead. See UnavailableMetricCopy.
    // NOTE: bound via a plain DataGridTextColumn — Avalonia's DataGridTextColumn has no
    // straightforward ToolTip.Tip attachment point without converting to a DataGridTemplateColumn
    // (a bigger, more invasive change than this PR's scope). Property is here and testable;
    // axaml wiring intentionally deferred.
    public string? ModifiedUnavailableTooltip =>
        Modified == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
}

/// <summary>Per-extension aggregated stats for the File Types panel.</summary>
public record FileTypeStatRow(string Extension, long SizeBytes, long FileCount, double Percent)
{
    public string SizeDisplay    => DiskNode.FormatSize(SizeBytes);
    public string PercentDisplay => $"{Percent:F1}%";
    public string CountDisplay   => $"{FileCount:N0}";
}

public partial class DiskAnalyzerViewModel : ViewModelBase, IDisposable
{
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    [ObservableProperty] private string _selectedPath = GetDefaultPath();
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasScanResult;
    [ObservableProperty] private string _scanStatus = "Choose a drive or folder to scan.";
    [ObservableProperty] private double _scanProgressValue;  // 0–100
    [ObservableProperty] private string _scanProgressText = string.Empty;

    // Scan result data
    [ObservableProperty] private DiskNode? _rootNode;
    [ObservableProperty] private DiskNode? _selectedNode;   // currently navigated-into folder
    [ObservableProperty] private DiskNode? _selectedFile;   // selected row in file list
    [ObservableProperty] private string _summaryText = string.Empty;

    // Folder view: flat-list tree (expand/collapse in-place via depth-indented rows)
    [ObservableProperty] private ObservableCollection<FolderTreeRow> _treeRows = [];
    [ObservableProperty] private FolderTreeRow? _selectedRow;

    // Folder view: flat view of SelectedNode's children sorted by size (kept for nav commands)
    [ObservableProperty] private ObservableCollection<DiskNode> _fileRows = [];

    // Breadcrumb navigation
    [ObservableProperty] private ObservableCollection<DiskNode> _breadcrumb = [];

    // ── Tab state ────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isFolderViewActive = true;
    [ObservableProperty] private bool _isFileViewActive;
    [ObservableProperty] private bool _isDuplicateViewActive;

    // ── Volume stats ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _volumeTotalDisplay = string.Empty;
    [ObservableProperty] private string _volumeUsedDisplay  = string.Empty;
    [ObservableProperty] private string _volumeFreeDisplay  = string.Empty;
    [ObservableProperty] private double _volumeUsedPercent;

    // ── File View ────────────────────────────────────────────────────────────
    // Flat sorted list of all files (top 50k, built once after scan).
    // 3C: Written atomically by BuildAllFiles on a background thread (reference swap);
    //     read only on the UI thread in FilterFiles after the Post() arrives.
    private volatile List<DiskNode> _allFilesSorted = new(0);
    [ObservableProperty] private ObservableCollection<DiskNode> _filteredFiles = [];
    [ObservableProperty] private string _fileSearchText  = string.Empty;
    [ObservableProperty] private string _fileViewSummary = string.Empty;

    // ── File Type stats ──────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<FileTypeStatRow> _fileTypeStats = [];

    // ── Duplicate finder ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isDuplicateScanning;
    [ObservableProperty] private ObservableCollection<DuplicateGroup> _duplicates = [];
    [ObservableProperty] private string _duplicateSummary = string.Empty;

    // ── Duplicate result flag ─────────────────────────────────────────────────
    [ObservableProperty] private bool _hasDuplicates;

    // ── Empty state ───────────────────────────────────────────────────────────
    public bool ShowEmptyState => !HasScanResult && !IsScanning;
    partial void OnHasScanResultChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));
    partial void OnIsScanningChanged(bool value)    => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnSelectedRowChanged(FolderTreeRow? value)
    {
        if (value is null) return;
        SelectedFile = value.Node;
        if (value.Node.IsDirectory)
        {
            SelectedNode = value.Node;
            BuildBreadcrumb(value.Node);
        }
    }

    // Available drives
    public IReadOnlyList<string> AvailableDrives { get; } = GetAvailableDrives();

    /// <summary>Exposes platform capability flags for binding in the View.</summary>
    public IPlatformCapabilities Platform { get; }

    public DiskAnalyzerViewModel(IPlatformCapabilities? platformCapabilities = null)
    {
        Title    = "Disk Analyzer";
        Platform = platformCapabilities ?? new MockPlatformCapabilities();
    }

    // ── Tab switching ────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectFolderView()
    {
        IsFolderViewActive    = true;
        IsFileViewActive      = false;
        IsDuplicateViewActive = false;
    }

    [RelayCommand]
    private void SelectFileView()
    {
        IsFolderViewActive    = false;
        IsFileViewActive      = true;
        IsDuplicateViewActive = false;
    }

    [RelayCommand]
    private void SelectDuplicateView()
    {
        IsFolderViewActive    = false;
        IsFileViewActive      = false;
        IsDuplicateViewActive = true;
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartScan()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;
        // 3D: Create new CTS before cancelling the old one so the old task's
        //     OperationCanceledException observe the new token, not a disposed one.
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts.Cancel();
        oldCts.Dispose();

        IsScanning        = true;
        HasScanResult     = false;
        ScanProgressValue = 0;
        ScanStatus        = $"Scanning {SelectedPath}\u2026";
        Duplicates.Clear();
        DuplicateSummary  = string.Empty;

        // Clear the previous scan's results up front so stale rows/selection never sit under
        // the UI mid-scan (previously TreeRows was left untouched until BuildTreeRows ran on
        // completion, which combined with the row-1 reflow read as the whole grid jittering).
        TreeRows.Clear();
        Breadcrumb.Clear();
        SelectedRow  = null;
        SelectedNode = null;
        SelectedFile = null;

        // Wall-clock throttle (not count-based): RecursiveScanner reports once per file, which
        // on a fast scan is far more often than the UI can usefully re-measure. Cap intermediate
        // updates to 5/sec; the final status after the scan completes is set unconditionally
        // below (outside this handler), so completion is never throttled away.
        long lastProgressUpdateMs = Environment.TickCount64;
        var progress = new Progress<ScanProgress>(p =>
        {
            long now = Environment.TickCount64;
            if (p.FilesScanned > 5 && now - lastProgressUpdateMs < 200) return;
            lastProgressUpdateMs = now;
            ScanProgressText = $"{p.FilesScanned:N0} files \u2014 {DiskNode.FormatSize(p.BytesCounted)}";
            ScanStatus = $"Scanning: {Path.GetFileName(p.CurrentPath)}";
        });

        try
        {
            var scanner = new MftScanner();
            var result  = await scanner.ScanAsync(SelectedPath, new ScanOptions(), progress, _cts.Token);

            RootNode      = result.Root;
            SelectedNode  = result.Root;
            HasScanResult = true;
            BuildBreadcrumb(result.Root);
            BuildTreeRows(result.Root);
            SummaryText = $"{result.TotalFiles:N0} files, {result.TotalFolders:N0} folders \u2014 " +
                          $"{DiskNode.FormatSize(result.TotalSize)} in {result.Duration.TotalSeconds:F1}s";
            ScanStatus  = SummaryText;

            // Volume stats
            if (result.VolumeTotal > 0)
            {
                long used = result.VolumeTotal - result.VolumeFree;
                VolumeTotalDisplay = DiskNode.FormatSize(result.VolumeTotal);
                VolumeUsedDisplay  = DiskNode.FormatSize(used);
                VolumeFreeDisplay  = DiskNode.FormatSize(result.VolumeFree);
                VolumeUsedPercent  = (double)used / result.VolumeTotal * 100.0;
            }
            else
            {
                VolumeTotalDisplay = DiskNode.FormatSize(result.TotalSize);
                VolumeUsedDisplay  = DiskNode.FormatSize(result.TotalSize);
                VolumeFreeDisplay  = "\u2014";
                VolumeUsedPercent  = 100.0;
            }

            // Build flat file list + type stats off the UI thread (can be slow on large drives)
            var capturedRoot = result.Root;
            var capturedSize = result.TotalSize;
            await Task.Run(() =>
            {
                BuildAllFiles(capturedRoot);
                BuildFileTypeStats(capturedRoot, capturedSize);
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning        = false;
            ScanProgressValue = 100;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts.Cancel();

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NavigateInto(DiskNode? node)
    {
        if (node is null || !node.IsDirectory) return;
        SelectedNode = node;
        BuildBreadcrumb(node);
        UpdateFileRows(node);
    }

    [RelayCommand]
    private void NavigateToBreadcrumb(DiskNode? node)
    {
        if (node is null) return;
        NavigateInto(node);
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (SelectedNode?.Parent is { } parent)
            NavigateInto(parent);
    }

    private void BuildBreadcrumb(DiskNode node)
    {
        var crumbs  = new List<DiskNode>();
        var current = node;
        while (current is not null) { crumbs.Insert(0, current); current = current.Parent; }
        Breadcrumb.Clear();
        foreach (var c in crumbs) Breadcrumb.Add(c);
    }

    private void UpdateFileRows(DiskNode node)
    {
        FileRows.Clear();
        foreach (var child in node.Children.OrderByDescending(c => c.Size))
            FileRows.Add(child);
        SelectedFile = null;
    }

    // ── Flat-list tree (expand/collapse in-place) ─────────────────────────────

    private void BuildTreeRows(DiskNode root)
    {
        TreeRows.Clear();
        var rootRow = new FolderTreeRow(root, depth: 0, isExpanded: true);
        TreeRows.Add(rootRow);
        InsertChildren(rootRow, afterIndex: 0);
    }

    [RelayCommand]
    private void ToggleRow(FolderTreeRow? row)
    {
        if (row is null || !row.HasChildren) return;
        int idx = TreeRows.IndexOf(row);
        if (idx < 0) return;

        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            CollapseDescendants(idx);
        }
        else
        {
            row.IsExpanded = true;
            InsertChildren(row, idx);
        }
    }

    private void InsertChildren(FolderTreeRow parent, int afterIndex)
    {
        int at = afterIndex + 1;
        foreach (var child in parent.Node.Children.OrderByDescending(c => c.Size))
            TreeRows.Insert(at++, new FolderTreeRow(child, parent.Depth + 1));
    }

    private void CollapseDescendants(int parentIdx)
    {
        int depth = TreeRows[parentIdx].Depth;
        while (parentIdx + 1 < TreeRows.Count && TreeRows[parentIdx + 1].Depth > depth)
            TreeRows.RemoveAt(parentIdx + 1);
    }

    // ── Treemap node click ────────────────────────────────────────────────────

    [RelayCommand]
    private void TreemapNodeClicked(DiskNode? node)
    {
        if (node is null) return;
        if (node.IsDirectory) NavigateInto(node);
        else SelectedFile = node;
    }

    // ── Duplicate finder ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (RootNode is null) return;
        IsDuplicateScanning = true;
        DuplicateSummary    = "Scanning for duplicates\u2026";
        Duplicates.Clear();

        try
        {
            var finder = new DuplicateFinder();
            var dupes  = await finder.FindDuplicatesAsync(RootNode, null, _cts.Token);
            foreach (var g in dupes) Duplicates.Add(g);
            long wasted = dupes.Sum(g => g.WastedBytes);
            HasDuplicates    = dupes.Count > 0;
            DuplicateSummary = dupes.Count == 0
                ? "No duplicates found."
                : $"Found {dupes.Count} duplicate group{(dupes.Count == 1 ? "" : "s")} \u2014 {DiskNode.FormatSize(wasted)} wasted";
        }
        catch (OperationCanceledException) { DuplicateSummary = "Cancelled."; }
        catch (Exception ex)               { DuplicateSummary = $"Error: {ex.Message}"; }
        finally                            { IsDuplicateScanning = false; }
    }

    // ── File search (File View tab) ───────────────────────────────────────────

    partial void OnFileSearchTextChanged(string value) => FilterFiles();

    private void FilterFiles()
    {
        FilteredFiles.Clear();
        IEnumerable<DiskNode> query = _allFilesSorted;
        if (!string.IsNullOrWhiteSpace(FileSearchText))
            query = query.Where(f =>
                f.Name.Contains(FileSearchText, StringComparison.OrdinalIgnoreCase) ||
                f.FullPath.Contains(FileSearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var f in query.Take(10_000))
            FilteredFiles.Add(f);

        FileViewSummary = _allFilesSorted.Count > 0
            ? $"Showing {FilteredFiles.Count:N0} of {_allFilesSorted.Count:N0} files"
            : string.Empty;
    }

    // ── File actions ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenFileLocation(DiskNode? node)
    {
        if (node is null) return;
        var path = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) ?? node.FullPath;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    [RelayCommand]
    private void CopyPath(DiskNode? node)
    {
        if (node is null) return;
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;
            _ = lifetime?.MainWindow?.Clipboard?.SetTextAsync(node.FullPath);
        }
        catch { }
    }

    // ── Private build helpers ────────────────────────────────────────────────

    /// <summary>
    /// Collects all non-directory nodes into a flat list sorted by size descending,
    /// capped at 50,000 entries for UI performance.
    /// Must be called from a background thread; calls FilterFiles which marshals to UI.
    /// </summary>
    private void BuildAllFiles(DiskNode root)
    {
        // 3C: Build into a local list on the background thread, then assign atomically
        //     before posting to UI. This prevents the UI thread seeing a partially-built list.
        var localList = new List<DiskNode>(1024);
        CollectFiles(root, localList);
        localList.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (localList.Count > 50_000)
            localList.RemoveRange(50_000, localList.Count - 50_000);

        _allFilesSorted = localList;  // atomic reference swap

        // FilterFiles must update ObservableCollection → run on UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(FilterFiles);
    }

    private static void CollectFiles(DiskNode root, List<DiskNode> files)
    {
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children)
            {
                if (!child.IsDirectory) files.Add(child);
                else                    stack.Push(child);
            }
        }
    }

    /// <summary>Builds per-extension aggregated stats; updates FileTypeStats on UI thread.</summary>
    private void BuildFileTypeStats(DiskNode root, long totalSize)
    {
        var dict = new Dictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
        AccumulateExtensions(root, dict);

        var rows = dict.OrderByDescending(k => k.Value.Size).Take(20)
            .Select(kvp =>
            {
                double pct = totalSize > 0 ? (double)kvp.Value.Size / totalSize * 100.0 : 0;
                return new FileTypeStatRow(
                    string.IsNullOrEmpty(kvp.Key) ? "(no ext)" : kvp.Key,
                    kvp.Value.Size,
                    kvp.Value.Count,
                    pct);
            }).ToList();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FileTypeStats.Clear();
            foreach (var r in rows) FileTypeStats.Add(r);
        });
    }

    private static void AccumulateExtensions(DiskNode root, Dictionary<string, (long Size, long Count)> dict)
    {
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children)
            {
                if (!child.IsDirectory)
                {
                    var ext = child.Extension;
                    dict.TryGetValue(ext, out var curr);
                    dict[ext] = (curr.Size + child.Size, curr.Count + 1);
                }
                else
                {
                    stack.Push(child);
                }
            }
        }
    }

    private static string GetDefaultPath()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            return "C:\\";
        return "/";
    }

    private static IReadOnlyList<string> GetAvailableDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
        catch { return [GetDefaultPath()]; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
