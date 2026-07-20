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

    // ── Fast-follow #1: type-flip + deep-nesting pin tests ─────────────────────

    [Fact]
    public void Diff_TypeFlip_FileBecomesDirectory_EmitsRemovedFileAndAddedDirectory()
    {
        // Same path key ("thing"), different node kind across snapshots. The
        // differ must never try to recurse into one side as a directory when the
        // other side is a file — it should report a clean Removed + Added pair
        // (matching the documented rename/replace-as-remove+add honesty rule),
        // not throw, merge, or silently drop one side.
        var older = Snap(TestTrees.File("thing", 5000));
        var newer = Snap(TestTrees.Dir("thing", TestTrees.File("inner.bin", 3000)));

        var diff = SnapshotDiffer.Diff(_store, older, newer);
        var matches = diff.Root.Children.Where(c => c.Name == "thing").ToList();

        matches.Should().HaveCount(2);
        matches.Should().ContainSingle(c =>
            c.Kind == ChangeKind.Removed && !c.IsDirectory && c.SizeBefore == 5000 && c.SizeAfter == null);
        matches.Should().ContainSingle(c =>
            c.Kind == ChangeKind.Added && c.IsDirectory && c.SizeAfter == 3000 && c.SizeBefore == null);
    }

    [Fact]
    public void Diff_TypeFlip_DirectoryBecomesFile_EmitsRemovedDirectoryAndAddedFile()
    {
        // Mirror of the above in the opposite direction.
        var older = Snap(TestTrees.Dir("thing", TestTrees.File("inner.bin", 3000)));
        var newer = Snap(TestTrees.File("thing", 7000));

        var diff = SnapshotDiffer.Diff(_store, older, newer);
        var matches = diff.Root.Children.Where(c => c.Name == "thing").ToList();

        matches.Should().HaveCount(2);
        matches.Should().ContainSingle(c =>
            c.Kind == ChangeKind.Removed && c.IsDirectory && c.SizeBefore == 3000 && c.SizeAfter == null);
        matches.Should().ContainSingle(c =>
            c.Kind == ChangeKind.Added && !c.IsDirectory && c.SizeAfter == 7000 && c.SizeBefore == null);
    }

    [Fact]
    public void Diff_DeepNesting_AttributesGrowthAtEveryAncestorLevel()
    {
        // A change four levels down (R/A/B/C/leaf.bin) must roll up correctly at
        // EVERY ancestor, not just the immediate parent — each directory's own
        // Delta/Kind/SizeBefore/SizeAfter reflects the same underlying change.
        var older = Snap(TestTrees.Dir("A", TestTrees.Dir("B", TestTrees.Dir("C", TestTrees.File("leaf.bin", 1000)))));
        var newer = Snap(TestTrees.Dir("A", TestTrees.Dir("B", TestTrees.Dir("C", TestTrees.File("leaf.bin", 5000)))));

        var diff = SnapshotDiffer.Diff(_store, older, newer);

        diff.Root.Delta.Should().Be(4000);
        diff.Root.Kind.Should().Be(ChangeKind.Grown);
        diff.Root.SizeBefore.Should().Be(1000);
        diff.Root.SizeAfter.Should().Be(5000);

        var a = diff.Root.Children.Single(c => c.Name == "A");
        a.Delta.Should().Be(4000);
        a.Kind.Should().Be(ChangeKind.Grown);
        a.SizeBefore.Should().Be(1000);
        a.SizeAfter.Should().Be(5000);

        var b = a.Children.Single(c => c.Name == "B");
        b.Delta.Should().Be(4000);
        b.Kind.Should().Be(ChangeKind.Grown);

        var c = b.Children.Single(c => c.Name == "C");
        c.Delta.Should().Be(4000);
        c.Kind.Should().Be(ChangeKind.Grown);

        var leaf = c.Children.Single(c => c.Name == "leaf.bin");
        leaf.Kind.Should().Be(ChangeKind.Grown);
        leaf.SizeBefore.Should().Be(1000);
        leaf.SizeAfter.Should().Be(5000);
        leaf.Delta.Should().Be(4000);
    }

    [Fact]
    public void Diff_DeepNesting_UnrelatedSiblingSubtreeStaysExcluded()
    {
        // A deep change in one branch must not spuriously surface an entirely
        // unchanged sibling branch anywhere in the output (Unchanged subtrees are
        // pruned at every level, not just the root).
        var older = Snap(
            TestTrees.Dir("changed", TestTrees.Dir("mid", TestTrees.File("leaf.bin", 1000))),
            TestTrees.Dir("untouched", TestTrees.Dir("mid2", TestTrees.File("stable.bin", 2000))));
        var newer = Snap(
            TestTrees.Dir("changed", TestTrees.Dir("mid", TestTrees.File("leaf.bin", 9000))),
            TestTrees.Dir("untouched", TestTrees.Dir("mid2", TestTrees.File("stable.bin", 2000))));

        var diff = SnapshotDiffer.Diff(_store, older, newer);

        diff.Root.Children.Select(c => c.Name).Should().ContainSingle().Which.Should().Be("changed");
    }
}
