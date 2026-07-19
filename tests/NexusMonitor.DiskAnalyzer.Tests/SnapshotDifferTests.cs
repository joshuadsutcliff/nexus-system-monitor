using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class SnapshotDifferTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;

    public SnapshotDifferTests()
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

    private string Root => Path.Combine(_dir, "R");
    private static readonly SnapshotOptions Opts = new(ThresholdBytes: 1000);

    private long Snap(params NexusMonitor.DiskAnalyzer.Models.DiskNode[] children) =>
        _store.Save(TestTrees.Result(TestTrees.Dir("R", children), Root), Opts);

    [Fact]
    public void Diff_DetectsAddedRemovedGrownShrunk()
    {
        var older = Snap(
            TestTrees.File("stays.bin", 5000),
            TestTrees.File("grows.bin", 2000),
            TestTrees.File("shrinks.bin", 9000),
            TestTrees.File("leaves.bin", 3000));
        var newer = Snap(
            TestTrees.File("stays.bin", 5000),
            TestTrees.File("grows.bin", 6000),
            TestTrees.File("shrinks.bin", 1500),
            TestTrees.File("arrives.bin", 4000));

        var diff = SnapshotDiffer.Diff(_store, older, newer);
        var byName = diff.Root.Children.ToDictionary(c => c.Name);

        byName["grows.bin"].Kind.Should().Be(ChangeKind.Grown);
        byName["grows.bin"].Delta.Should().Be(4000);
        byName["shrinks.bin"].Kind.Should().Be(ChangeKind.Shrunk);
        byName["shrinks.bin"].Delta.Should().Be(-7500);
        byName["leaves.bin"].Kind.Should().Be(ChangeKind.Removed);
        byName["leaves.bin"].Delta.Should().Be(-3000);
        byName["arrives.bin"].Kind.Should().Be(ChangeKind.Added);
        byName["arrives.bin"].Delta.Should().Be(4000);
        byName.Should().NotContainKey("stays.bin"); // Unchanged excluded from output
    }

    [Fact]
    public void Diff_ChildrenSortedByAbsoluteDeltaDescending()
    {
        var older = Snap(TestTrees.File("small-change.bin", 5000), TestTrees.File("big-change.bin", 5000));
        var newer = Snap(TestTrees.File("small-change.bin", 5100), TestTrees.File("big-change.bin", 90000));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        diff.Root.Children.Select(c => c.Name).Should()
            .ContainInOrder("big-change.bin", "small-change.bin");
    }

    [Fact]
    public void Diff_SmallFilesRollupDelta_SurfacesOnDirectory()
    {
        var older = Snap(TestTrees.Dir("cache", TestTrees.File("t1", 100)));
        var newer = Snap(TestTrees.Dir("cache",
            TestTrees.File("t1", 100), TestTrees.File("t2", 300), TestTrees.File("t3", 200)));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        var cache = diff.Root.Children.Single(c => c.Name == "cache");
        cache.SmallFilesDelta.Should().Be(500);
        cache.Kind.Should().Be(ChangeKind.Grown);
    }

    [Fact]
    public void Diff_CaseRename_FollowsPlatformRule()
    {
        var older = Snap(TestTrees.File("README.bin", 5000));
        var newer = Snap(TestTrees.File("readme.bin", 5000));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        if (PathKeys.NamesAreCaseInsensitive)
            diff.Root.Children.Should().BeEmpty(); // same file, unchanged
        else
            diff.Root.Children.Should().HaveCount(2); // Removed + Added
        diff.NamesMatchedCaseInsensitively.Should().Be(PathKeys.NamesAreCaseInsensitive);
    }

    [Fact]
    public void Diff_MismatchedThresholds_UsesMaxAndFlags()
    {
        var t = TestTrees.Dir("R", TestTrees.File("mid.bin", 500));
        var older = _store.Save(TestTrees.Result(t, Root), new SnapshotOptions(ThresholdBytes: 100));
        var newer = _store.Save(TestTrees.Result(t, Root), new SnapshotOptions(ThresholdBytes: 1000));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        diff.ThresholdMismatch.Should().BeTrue();
        diff.EffectiveThreshold.Should().Be(1000);
        // mid.bin is a row only in the older snapshot; at effective threshold it must
        // NOT appear as a Removed file — it is re-aggregated instead.
        diff.Root.Children.Should().NotContain(c => c.Name == "mid.bin");
        // Full accounting: same file, same size, both sides at effective resolution —
        // the re-aggregated small-files delta must net to zero (review finding).
        diff.Root.SmallFilesDelta.Should().Be(0);
    }

    [Fact]
    public void Diff_DifferentRoots_Throws()
    {
        var a = _store.Save(TestTrees.Result(TestTrees.Dir("A", TestTrees.File("x", 5000)),
            Path.Combine(_dir, "A")), Opts);
        var b = _store.Save(TestTrees.Result(TestTrees.Dir("B", TestTrees.File("x", 5000)),
            Path.Combine(_dir, "B")), Opts);
        var act = () => SnapshotDiffer.Diff(_store, a, b);
        act.Should().Throw<InvalidOperationException>().WithMessage("*root*");
    }
}
