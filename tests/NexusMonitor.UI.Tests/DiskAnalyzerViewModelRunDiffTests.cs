using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Models;
using NexusMonitor.DiskAnalyzer.Snapshots;
using NexusMonitor.UI.ViewModels;
using Xunit;

namespace NexusMonitor.UI.Tests;

/// <summary>
/// Fast-follow #4: a failed compare (RunDiff, backing CompareSelected /
/// CompareWithPrevious) must clear any previously-displayed diff rather than
/// leave stale rows next to the new error notification. Exercises the real
/// SnapshotStore + SnapshotDiffer (same failure path as
/// SnapshotDifferTests.Diff_DifferentRoots_Throws) through the ViewModel —
/// no Avalonia application bootstrap is required because RunDiff/CompareSelected
/// never touch Dispatcher.UIThread or Application.Current (only
/// SaveSnapshotAsync/BrowseForFolder do, and neither is exercised here).
/// </summary>
public class DiskAnalyzerViewModelRunDiffTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;

    public DiskAnalyzerViewModelRunDiffTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SnapshotStore(Path.Combine(_dir, "disk-snapshots.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly SnapshotOptions Opts = new(ThresholdBytes: 1000);

    private static DiskNode Dir(string name, params DiskNode[] children)
    {
        var d = new DiskNode { Name = name, IsDirectory = true };
        foreach (var c in children)
        {
            c.Parent = d;
            d.Children.Add(c);
            d.Size += c.Size;
        }
        return d;
    }

    private static DiskNode File(string name, long size) =>
        new() { Name = name, IsDirectory = false, Size = size, AllocatedSize = size };

    private long Snap(string rootName, string rootPath, params DiskNode[] children)
    {
        var root = Dir(rootName, children);
        var result = new ScanResult
        {
            Root = root, ScannedPath = rootPath,
            TotalFiles = root.FileCount, TotalFolders = root.FolderCount, TotalSize = root.Size,
            FileSystem = "TESTFS", VolumeTotal = 1_000_000_000, VolumeFree = 400_000_000,
        };
        return _store.Save(result, Opts);
    }

    [Fact]
    public void RunDiff_Failure_ClearsPreviouslyDisplayedDiff()
    {
        var rootA = Path.Combine(_dir, "A");
        var rootB = Path.Combine(_dir, "B");

        // Two snapshots of the SAME root — a valid pair — so the first compare succeeds
        // and leaves a real diff on screen.
        var aOlder = Snap("A", rootA, File("f.bin", 1000));
        var aNewer = Snap("A", rootA, File("f.bin", 5000));

        // A snapshot of a DIFFERENT root — pairing it with aOlder is the same
        // different-roots failure SnapshotDifferTests.Diff_DifferentRoots_Throws pins.
        var bSnap = Snap("B", rootB, File("g.bin", 1000));

        var vm = new DiskAnalyzerViewModel(snapshotStore: _store);

        // Arrange: a successful compare first, so there IS a previous diff to go stale.
        vm.Snapshots.Add(new SnapshotRow { Info = _store.GetSnapshot(aOlder)!, IsSelected = true });
        vm.Snapshots.Add(new SnapshotRow { Info = _store.GetSnapshot(aNewer)!, IsSelected = true });
        vm.CompareSelectedCommand.Execute(null);

        vm.HasDiff.Should().BeTrue("the same-root pair is a valid compare");
        vm.DiffRows.Should().NotBeEmpty();
        vm.DiffHeader.Should().NotBeEmpty();

        // Act: a failing compare (different roots) using the previous selection state.
        foreach (var row in vm.Snapshots) row.IsSelected = false;
        vm.Snapshots.Clear();
        vm.Snapshots.Add(new SnapshotRow { Info = _store.GetSnapshot(aOlder)!, IsSelected = true });
        vm.Snapshots.Add(new SnapshotRow { Info = _store.GetSnapshot(bSnap)!, IsSelected = true });
        vm.CompareSelectedCommand.Execute(null);

        // Assert: the stale diff from the earlier successful compare must be gone,
        // not left displayed next to the new "Cannot compare" state.
        vm.HasDiff.Should().BeFalse("the failed compare must not leave HasDiff true");
        vm.DiffRows.Should().BeEmpty("stale rows from the previous successful diff must be cleared");
        vm.DiffHeader.Should().BeEmpty("the stale header must be cleared, not left describing the old pair");
    }
}
