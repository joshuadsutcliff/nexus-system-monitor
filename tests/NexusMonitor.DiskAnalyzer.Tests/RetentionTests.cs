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

    // A "wide" tree (many threshold-eligible files -> many node rows) so a single
    // snapshot occupies enough dedicated B-tree pages that deleting it measurably
    // frees live bytes without VACUUM. Tiny 1-2-node snapshots share pages so
    // tightly that deleting several never drops GetLiveSizeBytes() at all — the
    // size-cap loop would run to its "every root down to 1" floor instead of
    // stopping on the cap, defeating any test that wants to observe a single
    // discriminating deletion.
    private long SnapWide(string root, SnapshotOptions opts, int fileCount = 300) =>
        _store.Save(TestTrees.Result(
            TestTrees.Dir("R", Enumerable.Range(0, fileCount)
                .Select(i => TestTrees.File($"f{i}.bin", 5000)).ToArray()),
            Path.Combine(_dir, root)), opts);

    [Fact]
    public void Save_PrunesOldestBeyondPerRootLimit()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 3);
        var ids = Enumerable.Range(0, 5).Select(_ => Snap("A", 5000, opts)).ToList();

        var kept = _store.ListSnapshots(Path.Combine(_dir, "A")).Select(s => s.Id).ToList();
        kept.Should().HaveCount(3);
        kept.Should().BeEquivalentTo(ids.TakeLast(3));

        // Orphan sweep (review finding, 2026-07-19): pass-1 pruning must not leave
        // nodes rows behind for snapshots it deleted.
        using var cmd = _store.Database.ReadConnection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE snapshot_id NOT IN (SELECT id FROM snapshots)";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(0);
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
        // Arrange with a generous cap so no pruning happens during the saves
        // themselves — we want full control over when ApplyRetention fires.
        // Root A gets 3 WIDE snapshots (real dedicated pages per snapshot); root B
        // gets 2 minimal ones. A has the most snapshots, so DESC must take from A.
        var generous = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 50);
        var aIds = Enumerable.Range(0, 3).Select(_ => SnapWide("A", generous)).ToList(); // oldest..newest
        var bIds = Enumerable.Range(0, 2).Select(_ => Snap("B", 5000, generous)).ToList();

        // Act: force the size-cap loop to fire. A single deletion of one wide A
        // snapshot frees far more than 1 byte, so the loop should stop after
        // exactly one victim — the discriminator is *which* root that victim
        // comes from, not how many are removed.
        var live = _store.Database.GetLiveSizeBytes();
        _store.ApplyRetention(new SnapshotOptions(
            ThresholdBytes: 100, RetentionPerRoot: 50, MaxDbSizeBytes: live - 1));

        var a = _store.ListSnapshots(Path.Combine(_dir, "A")).Select(s => s.Id).ToList();
        var b = _store.ListSnapshots(Path.Combine(_dir, "B")).Select(s => s.Id).ToList();

        // Fairness (spec §4): the cap takes from the root with the MOST snapshots (A),
        // never touching B — this is what discriminates DESC from ASC.
        b.Should().HaveCount(2, "root B (fewer snapshots) must be untouched");
        bIds.Should().BeEquivalentTo(b, "root B must lose nothing");
        a.Count.Should().BeLessThan(3, "root A (most snapshots) must lose at least its oldest");

        // Deletions come oldest-first: whatever remains of A must be the newest
        // suffix of the recorded insertion order.
        var expectedRemainder = aIds.TakeLast(a.Count).ToList();
        a.Should().BeEquivalentTo(expectedRemainder, "A must lose oldest-first, never its newest");
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
