using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;
    private static readonly SnapshotOptions Opts = new(ThresholdBytes: 1000);

    public SnapshotStoreTests()
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

    private string ScanRoot => Path.Combine(_dir, "ScanRoot");

    [Fact]
    public void Save_RoundTrips_MetadataAndVolumeInfo()
    {
        var tree = TestTrees.Dir("ScanRoot", TestTrees.File("big.bin", 5000));
        var id = _store.Save(TestTrees.Result(tree, ScanRoot), Opts, appVersion: "1.0-test");

        var info = _store.GetSnapshot(id)!;
        info.RootPath.Should().Be(PathKeys.NormalizeDisplay(ScanRoot));
        info.RootKey.Should().Be(PathKeys.ToRootKey(ScanRoot));
        info.FileSystem.Should().Be("TESTFS");
        info.VolumeTotal.Should().Be(1_000_000_000);
        info.VolumeFree.Should().Be(400_000_000);
        info.ThresholdBytes.Should().Be(1000);
        info.AppVersion.Should().Be("1.0-test");
    }

    [Fact]
    public void Save_AppliesThreshold_SmallFilesRollUpPerDirectory()
    {
        var tree = TestTrees.Dir("ScanRoot",
            TestTrees.File("big.bin", 5000),
            TestTrees.File("tiny1.txt", 10),
            TestTrees.File("tiny2.txt", 20),
            TestTrees.Dir("sub",
                TestTrees.File("also-big.bin", 2000),
                TestTrees.File("also-tiny.txt", 5)));
        var id = _store.Save(TestTrees.Result(tree, ScanRoot), Opts);

        var nodes = _store.LoadNodes(id);
        var names = nodes.Select(n => n.Name).ToList();
        names.Should().Contain(new[] { "ScanRoot", "big.bin", "sub", "also-big.bin" });
        names.Should().NotContain(new[] { "tiny1.txt", "tiny2.txt", "also-tiny.txt" });

        var root = nodes.Single(n => n.ParentId == null);
        root.SmallFilesSize.Should().Be(30);   // tiny1 + tiny2
        root.SmallFilesCount.Should().Be(2);
        root.Size.Should().Be(7035);           // dir rollup keeps the TRUE total

        var sub = nodes.Single(n => n.Name == "sub");
        sub.SmallFilesSize.Should().Be(5);
        sub.SmallFilesCount.Should().Be(1);
        sub.Size.Should().Be(2005);
    }

    [Fact]
    public void Save_FileExactlyAtThreshold_IsStoredIndividually()
    {
        var tree = TestTrees.Dir("ScanRoot", TestTrees.File("edge.bin", 1000));
        var id = _store.Save(TestTrees.Result(tree, ScanRoot), Opts);
        _store.LoadNodes(id).Select(n => n.Name).Should().Contain("edge.bin");
    }

    [Fact]
    public void ListSnapshots_GroupsByRootKey_CasingVariantsAreOneRoot()
    {
        var t1 = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var t2 = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 6000));
        _store.Save(TestTrees.Result(t1, ScanRoot), Opts);
        _store.Save(TestTrees.Result(t2, ScanRoot.ToUpperInvariant()), Opts);

        var forRoot = _store.ListSnapshots(ScanRoot);
        if (PathKeys.NamesAreCaseInsensitive) forRoot.Should().HaveCount(2);
        else                                  forRoot.Should().HaveCount(1);
    }

    [Fact]
    public void ListSnapshots_NewestFirst()
    {
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var id1 = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        var id2 = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        _store.ListSnapshots(ScanRoot).Select(s => s.Id).First().Should().Be(id2);
    }

    [Fact]
    public void Delete_RemovesSnapshotAndNodes()
    {
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var id = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        _store.Delete(id);
        _store.GetSnapshot(id).Should().BeNull();
        _store.LoadNodes(id).Should().BeEmpty();
    }

    [Fact]
    public void SweepIncomplete_RemovesOnlyIncompleteSnapshots()
    {
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var goodId = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        // Forge an incomplete snapshot the way a crash would leave one.
        using (var cmd = _store.Database.Connection.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO snapshots
                (root_path, root_key, created_at, scanner, threshold_bytes, complete)
                VALUES ('x', 'x', '2026-07-19T00:00:00.0000000Z', 'recursive', 1000, 0)";
            cmd.ExecuteNonQuery();
        }
        _store.SweepIncomplete().Should().Be(1);
        _store.GetSnapshot(goodId).Should().NotBeNull();
    }

    /// <summary>
    /// Pin for spec §8: retention pruning failure must not fail the Save that
    /// already committed. This is a smoke pin, not a throw-pin — these extreme
    /// options (RetentionPerRoot: 0 clamped to 1, MaxDbSizeBytes: 1 forcing the
    /// pass-2 delete/VACUUM loop) exercise the same code path a real retention
    /// failure would run through, but they don't reliably make ApplyRetention
    /// throw in a temp-dir test environment, so this test does not discriminate
    /// between "the try/catch swallows the exception" and "no exception occurred."
    /// The swallow itself (SnapshotStore.Save wrapping ApplyRetention in try/catch)
    /// is verified by code review, not by this test. What this test does pin is
    /// the contract: Save must still return a valid, readable snapshot id when
    /// retention runs under maximally aggressive options.
    /// </summary>
    [Fact]
    public void Save_ReturnsValidId_WhenRetentionRunsUnderExtremeOptions()
    {
        var extreme = new SnapshotOptions(ThresholdBytes: 1000, RetentionPerRoot: 0, MaxDbSizeBytes: 1);
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));

        var id = _store.Save(TestTrees.Result(t, ScanRoot), extreme);

        id.Should().BeGreaterThan(0);
        _store.GetSnapshot(id).Should().NotBeNull();
    }
}
