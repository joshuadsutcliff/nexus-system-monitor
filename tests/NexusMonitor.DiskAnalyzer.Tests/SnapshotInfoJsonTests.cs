using System.Text.Json;
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

/// <summary>
/// Fast-follow #3: `disk scan --format json` without `--diff` previously printed
/// nothing at all (the table-summary branch is skipped for json, and there was no
/// json branch for the no-diff path). These tests pin the deliberate, stable shape
/// now shared with `disk snapshots list --format json` (SnapshotInfoJson).
/// </summary>
public class SnapshotInfoJsonTests
{
    private static SnapshotInfo Info(long id = 7) => new(
        Id: id, RootPath: "/data", RootKey: "/DATA",
        CreatedAtUtc: DateTime.Parse("2026-07-19T00:00:00.0000000Z", null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        Scanner: "recursive", FileSystem: "TESTFS",
        VolumeTotal: 1_000_000_000, VolumeFree: 400_000_000,
        TotalSize: 10_000, TotalFiles: 3, TotalDirs: 1,
        ThresholdBytes: 1_048_576, AppVersion: "1.0.0");

    [Fact]
    public void ToJson_SingleSnapshot_HasStableShape()
    {
        using var doc = JsonDocument.Parse(SnapshotInfoJson.ToJson(Info()));
        var r = doc.RootElement;

        r.GetProperty("id").GetInt64().Should().Be(7);
        r.GetProperty("rootPath").GetString().Should().Be("/data");
        r.GetProperty("createdAt").GetString().Should().Be("2026-07-19T00:00:00.0000000Z");
        r.GetProperty("totalSize").GetInt64().Should().Be(10_000);
        r.GetProperty("totalFiles").GetInt64().Should().Be(3);
        r.GetProperty("thresholdBytes").GetInt64().Should().Be(1_048_576);
        r.GetProperty("fileSystem").GetString().Should().Be("TESTFS");
        r.GetProperty("volumeTotal").GetInt64().Should().Be(1_000_000_000);
        r.GetProperty("volumeFree").GetInt64().Should().Be(400_000_000);
    }

    [Fact]
    public void ToJson_Sequence_ProducesArray_WithSamePerItemShapeAsSingle()
    {
        // The list shape (`disk snapshots list --format json`) and the single-object
        // shape (`disk scan --format json`, no --diff) must describe a snapshot
        // identically — same field set, same field names — so a caller scripting
        // against one already knows the other.
        var single = JsonDocument.Parse(SnapshotInfoJson.ToJson(Info(id: 7))).RootElement;
        var arr = JsonDocument.Parse(SnapshotInfoJson.ToJson(new[] { Info(id: 7) })).RootElement;

        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().Be(1);
        var item = arr[0];

        var singleProps = single.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        var itemProps = item.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        itemProps.Should().Equal(singleProps);
        item.GetProperty("id").GetInt64().Should().Be(7);
    }
}
