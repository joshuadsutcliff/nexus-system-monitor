using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexusMonitor.Core.Storage;
using Xunit;
using TestMetricsDatabase = NexusMonitor.Core.Tests.Helpers.TestMetricsDatabase;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Coverage for C3: rollup aggregation must be sample-count-weighted, not a plain unweighted AVG
/// of already-averaged buckets. A 2-sample bucket (e.g. right after resume from sleep) must not
/// weigh the same as a 58-sample bucket when rolling 1m buckets up into 5m (or 5m into 1h) —
/// otherwise History/Health-Trends displays are skewed toward sparse buckets.
///
/// Tests write directly into the source rollup table (rollups_1m / rollups_5m) via raw SQL to
/// control sample_count precisely, then invoke the (internal, for testability —
/// InternalsVisibleTo NexusMonitor.Core.Tests) Process* method directly rather than waiting on the
/// real 60s timer. Uses the same real-SQLite <see cref="TestMetricsDatabase"/> fixture as
/// MetricsStoreTests.
/// </summary>
public class MetricsRollupServiceTests
{
    private const long BucketMs5m = 5 * 60_000L;
    private const long BucketMs1h = 60 * 60_000L;

    /// <summary>A bucket start a few slots behind "now" and aligned to <paramref name="bucketMs"/>,
    /// so ProcessRollup5m/1h only has a couple of buckets to iterate (fast) and the target bucket
    /// is guaranteed "complete" (safely in the past).</summary>
    private static long AlignedBucketFewSlotsAgo(long bucketMs, int slotsAgo = 4)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return ((nowMs / bucketMs) - slotsAgo) * bucketMs;
    }

    private static void InsertRollup1mRow(
        SqliteConnection conn, long ts, double cpuAvg, long sampleCount,
        double memAvg = 0, double diskReadAvg = 0, double diskWriteAvg = 0,
        double netSendAvg = 0, double netRecvAvg = 0, double? gpuAvg = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO rollups_1m
                (ts, cpu_avg, cpu_max, mem_avg_bytes, mem_max_bytes,
                 disk_read_avg, disk_write_avg, net_send_avg, net_recv_avg,
                 gpu_avg, gpu_max, sample_count)
            VALUES
                ($ts, $cpu, $cpu, $mem, $mem, $dr, $dw, $ns, $nr, $gpu, $gpu, $sc)";
        cmd.Parameters.AddWithValue("$ts",  ts);
        cmd.Parameters.AddWithValue("$cpu", cpuAvg);
        cmd.Parameters.AddWithValue("$mem", memAvg);
        cmd.Parameters.AddWithValue("$dr",  diskReadAvg);
        cmd.Parameters.AddWithValue("$dw",  diskWriteAvg);
        cmd.Parameters.AddWithValue("$ns",  netSendAvg);
        cmd.Parameters.AddWithValue("$nr",  netRecvAvg);
        cmd.Parameters.AddWithValue("$gpu", (object?)gpuAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sc",  sampleCount);
        cmd.ExecuteNonQuery();
    }

    private static void InsertRollup5mRow(
        SqliteConnection conn, long ts, double cpuAvg, long sampleCount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO rollups_5m
                (ts, cpu_avg, cpu_max, mem_avg_bytes, mem_max_bytes,
                 disk_read_avg, disk_write_avg, net_send_avg, net_recv_avg,
                 gpu_avg, gpu_max, sample_count)
            VALUES
                ($ts, $cpu, $cpu, 0, 0, 0, 0, 0, 0, NULL, NULL, $sc)";
        cmd.Parameters.AddWithValue("$ts",  ts);
        cmd.Parameters.AddWithValue("$cpu", cpuAvg);
        cmd.Parameters.AddWithValue("$sc",  sampleCount);
        cmd.ExecuteNonQuery();
    }

    private static (double cpuAvg, long sampleCount)? ReadRollupRow(SqliteConnection conn, string table, long ts)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT cpu_avg, sample_count FROM {table} WHERE ts = $ts";
        cmd.Parameters.AddWithValue("$ts", ts);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var cpuAvg = reader.IsDBNull(0) ? (double?)null : reader.GetDouble(0);
        var sampleCount = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return cpuAvg is null ? null : (cpuAvg.Value, sampleCount);
    }

    // ── 1m → 5m ──────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessRollup5m_WeightsBySampleCount_NotUnweightedAverage()
    {
        using var db = new TestMetricsDatabase();
        var config = new MetricsStoreConfig();
        using var service = new MetricsRollupService(db.Database, config);

        var bucketStart = AlignedBucketFewSlotsAgo(BucketMs5m);

        // Two 1m buckets in the same 5m window: one sparse (2 samples), one dense (58 samples).
        // Unweighted AVG(cpu_avg) would give (100 + 0) / 2 = 50 — wrong.
        // Sample-weighted average must give (100*2 + 0*58) / 60 = 3.333...
        InsertRollup1mRow(db.Database.Connection, bucketStart,               cpuAvg: 100.0, sampleCount: 2);
        InsertRollup1mRow(db.Database.Connection, bucketStart + 60_000,      cpuAvg: 0.0,   sampleCount: 58);

        db.Database.SetMeta("last_rollup_5m_ts", bucketStart);

        service.ProcessRollup5m();

        var row = ReadRollupRow(db.Database.Connection, "rollups_5m", bucketStart);
        row.Should().NotBeNull();
        row!.Value.cpuAvg.Should().BeApproximately(200.0 / 60.0, 0.001,
            "the rollup must weight each 1m bucket's average by its sample_count, not treat a 2-sample bucket the same as a 58-sample bucket");
        row.Value.cpuAvg.Should().NotBeApproximately(50.0, 0.001,
            "an unweighted AVG(cpu_avg) across buckets is the bug this test guards against");
        row.Value.sampleCount.Should().Be(60);
    }

    [Fact]
    public void ProcessRollup5m_EvenSampleCounts_MatchesSimpleAverage()
    {
        // Sanity check: when sample counts are equal, weighted and unweighted averages coincide —
        // proves the fix doesn't change behavior for the (previously untested) common case.
        using var db = new TestMetricsDatabase();
        var config = new MetricsStoreConfig();
        using var service = new MetricsRollupService(db.Database, config);

        var bucketStart = AlignedBucketFewSlotsAgo(BucketMs5m);

        InsertRollup1mRow(db.Database.Connection, bucketStart,          cpuAvg: 20.0, sampleCount: 30);
        InsertRollup1mRow(db.Database.Connection, bucketStart + 60_000, cpuAvg: 40.0, sampleCount: 30);

        db.Database.SetMeta("last_rollup_5m_ts", bucketStart);

        service.ProcessRollup5m();

        var row = ReadRollupRow(db.Database.Connection, "rollups_5m", bucketStart);
        row.Should().NotBeNull();
        row!.Value.cpuAvg.Should().BeApproximately(30.0, 0.001);
    }

    // ── 5m → 1h ──────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessRollup1h_WeightsBySampleCount_NotUnweightedAverage()
    {
        using var db = new TestMetricsDatabase();
        var config = new MetricsStoreConfig();
        using var service = new MetricsRollupService(db.Database, config);

        var bucketStart = AlignedBucketFewSlotsAgo(BucketMs1h);

        // Two 5m buckets in the same 1h window: sparse (10 samples) vs. dense (290 samples).
        // Unweighted AVG(cpu_avg) would give (90 + 0) / 2 = 45 — wrong.
        // Sample-weighted average must give (90*10 + 0*290) / 300 = 3.0
        InsertRollup5mRow(db.Database.Connection, bucketStart,                sampleCount: 10,  cpuAvg: 90.0);
        InsertRollup5mRow(db.Database.Connection, bucketStart + 5 * 60_000L,  sampleCount: 290, cpuAvg: 0.0);

        db.Database.SetMeta("last_rollup_1h_ts", bucketStart);

        service.ProcessRollup1h();

        var row = ReadRollupRow(db.Database.Connection, "rollups_1h", bucketStart);
        row.Should().NotBeNull();
        row!.Value.cpuAvg.Should().BeApproximately(3.0, 0.001,
            "the 5m->1h rollup must also weight by sample_count, not average the already-averaged 5m buckets unweighted");
        row.Value.sampleCount.Should().Be(300);
    }

    // ── Degenerate case ──────────────────────────────────────────────────────

    [Fact]
    public void ProcessRollup5m_ZeroSampleCountBucket_DoesNotThrow()
    {
        // A defensive guard: a source row with sample_count = 0 (shouldn't occur in practice,
        // since 1m rows are always written with COUNT(*) > 0, but the aggregate must not blow up
        // with a division error if it ever does).
        using var db = new TestMetricsDatabase();
        var config = new MetricsStoreConfig();
        using var service = new MetricsRollupService(db.Database, config);

        var bucketStart = AlignedBucketFewSlotsAgo(BucketMs5m);
        InsertRollup1mRow(db.Database.Connection, bucketStart, cpuAvg: 42.0, sampleCount: 0);

        db.Database.SetMeta("last_rollup_5m_ts", bucketStart);

        var act = () => service.ProcessRollup5m();
        act.Should().NotThrow("a degenerate zero-sample-count source row must not cause a division error");
    }
}
