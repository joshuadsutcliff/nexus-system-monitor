using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;

    public RetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SnapshotStore(Path.Combine(_dir, "disk-snapshots.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private long Snap(string root, long fileSize, SnapshotOptions opts) =>
        _store.Save(TestTrees.Result(
            TestTrees.Dir("R", TestTrees.File("data.bin", fileSize)),
            Path.Combine(_dir, root)), opts);

    [Fact]
    public void Save_PrunesOldestBeyondPerRootLimit()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 3);
        var ids = Enumerable.Range(0, 5).Select(_ => Snap("A", 5000, opts)).ToList();

        var kept = _store.ListSnapshots(Path.Combine(_dir, "A")).Select(s => s.Id).ToList();
        kept.Should().HaveCount(3);
        kept.Should().BeEquivalentTo(ids.TakeLast(3));
    }

    [Fact]
    public void PerRootLimit_DoesNotCrossRoots()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 2);
        Snap("A", 5000, opts); Snap("A", 5000, opts);
        Snap("B", 5000, opts);
        _store.ListSnapshots(Path.Combine(_dir, "A")).Should().HaveCount(2);
        _store.ListSnapshots(Path.Combine(_dir, "B")).Should().HaveCount(1);
    }

    [Fact]
    public void GlobalSizeCap_PrunesFromRootWithMostSnapshots()
    {
        // Cap tiny so it triggers immediately; root A has 3 snapshots, root B has 1.
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 50, MaxDbSizeBytes: 1);
        Snap("A", 5000, opts); Snap("A", 5000, opts); Snap("A", 5000, opts);
        Snap("B", 5000, opts);

        // Fairness (spec §4): the cap takes from the root with the MOST snapshots (A),
        // never starving the lone root B. B's single snapshot must survive at least as
        // long as A has more than one.
        var a = _store.ListSnapshots(Path.Combine(_dir, "A"));
        var b = _store.ListSnapshots(Path.Combine(_dir, "B"));
        b.Should().HaveCount(1);
        a.Count.Should().BeLessThan(3);
    }

    [Fact]
    public void GlobalSizeCap_AlwaysKeepsAtLeastOneSnapshotPerRoot()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 50, MaxDbSizeBytes: 1);
        Snap("A", 5000, opts);
        Snap("B", 5000, opts);
        // Even with an absurd cap, the newest snapshot of each root survives —
        // retention must never delete the snapshot that was just saved.
        _store.ListSnapshots(Path.Combine(_dir, "A")).Should().HaveCount(1);
        _store.ListSnapshots(Path.Combine(_dir, "B")).Should().HaveCount(1);
    }
}
