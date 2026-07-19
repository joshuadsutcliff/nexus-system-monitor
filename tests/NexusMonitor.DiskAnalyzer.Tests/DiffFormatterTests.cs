using System.Text.Json;
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class DiffFormatterTests
{
    private static SnapshotInfo Info(long id, long threshold = 1000, string created = "2026-07-12T00:00:00.0000000Z") =>
        new(id, "/data", "/data", DateTime.Parse(created, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            "recursive", "TESTFS", 100, 40, 10_000, 3, 1, threshold, "test");

    private static DiffResult SampleDiff(bool mismatch = false)
    {
        var root = new DiffNode { Name = "data", IsDirectory = true, Kind = ChangeKind.Grown, Delta = 2500,
                                  SizeBefore = 6000, SizeAfter = 8500 };
        root.Children.Add(new DiffNode { Name = "big.bin", Kind = ChangeKind.Added, SizeAfter = 4000, Delta = 4000 });
        root.Children.Add(new DiffNode { Name = "old.bin", Kind = ChangeKind.Removed, SizeBefore = 1500, Delta = -1500 });
        return new DiffResult
        {
            Older = Info(1, mismatch ? 100 : 1000), Newer = Info(2),
            Root = root, EffectiveThreshold = 1000, ThresholdMismatch = mismatch,
            NamesMatchedCaseInsensitively = true,
        };
    }

    [Fact]
    public void Flatten_SortsByAbsDelta_AndBuildsPaths()
    {
        var lines = DiffFormatter.Flatten(SampleDiff());
        // Sorted by |delta| desc: big.bin (4000) > root (2500) > old.bin (1500);
        // paths are rooted at the snapshot's RootPath.
        lines.Select(l => l.Path).Should().ContainInOrder("/data/big.bin", "/data", "/data/old.bin");
        lines[0].Kind.Should().Be("added");
        lines[2].Delta.Should().Be(-1500);
    }

    [Fact]
    public void Flatten_Top_TruncatesAfterSorting()
    {
        DiffFormatter.Flatten(SampleDiff(), top: 1).Should().HaveCount(1);
    }

    [Fact]
    public void ToJson_HasStableShape()
    {
        using var doc = JsonDocument.Parse(DiffFormatter.ToJson(SampleDiff(), top: 5));
        var r = doc.RootElement;
        r.GetProperty("older").GetProperty("id").GetInt64().Should().Be(1);
        r.GetProperty("newer").GetProperty("id").GetInt64().Should().Be(2);
        r.GetProperty("root").GetString().Should().Be("/data");
        r.GetProperty("threshold").GetProperty("effectiveBytes").GetInt64().Should().Be(1000);
        r.GetProperty("threshold").GetProperty("mismatch").GetBoolean().Should().BeFalse();
        r.GetProperty("changes").GetArrayLength().Should().Be(3);
        r.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ToTable_ContainsFooterDisclosure()
    {
        DiffFormatter.ToTable(SampleDiff()).Should().Contain("aggregated");
    }

    [Fact]
    public void ThresholdFooter_MismatchSentence_IsExplicit()
    {
        var footer = DiffFormatter.ThresholdFooter(SampleDiff(mismatch: true));
        footer.Should().Contain("different thresholds");
        footer.Should().Contain("diff uses");
    }

    [Fact]
    public void SnapshotRefs_ResolvesLatestIdAndDate()
    {
        var list = new List<SnapshotInfo>
        {
            Info(3, created: "2026-07-19T00:00:00.0000000Z"),
            Info(2, created: "2026-07-12T00:00:00.0000000Z"),
            Info(1, created: "2026-07-05T00:00:00.0000000Z"),
        };
        SnapshotRefs.Resolve(list, "latest")!.Id.Should().Be(3);
        SnapshotRefs.Resolve(list, "2")!.Id.Should().Be(2);
        SnapshotRefs.Resolve(list, "2026-07-14")!.Id.Should().Be(2);   // newest at-or-before date
        SnapshotRefs.Resolve(list, "2026-01-01").Should().BeNull();    // none before
        SnapshotRefs.Resolve(list, "999").Should().BeNull();
    }
}
