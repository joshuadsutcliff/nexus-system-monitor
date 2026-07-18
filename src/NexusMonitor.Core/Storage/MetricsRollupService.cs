using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NexusMonitor.Core.Storage;

/// <summary>
/// Background service that runs every 60 seconds to:
///   1. Aggregate raw data into 1m/5m/1h rollup tables
///   2. Prune data older than configured retention periods
///   3. Run PRAGMA optimize periodically
/// </summary>
public sealed class MetricsRollupService : IDisposable
{
    private readonly MetricsDatabase    _db;
    private readonly MetricsStoreConfig _config;
    private readonly ILogger<MetricsRollupService> _logger;
    private Timer?  _timer;
    private bool    _disposed;

    private static readonly TimeSpan OneMinute  = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OneHour    = TimeSpan.FromHours(1);

    // Track when we last ran PRAGMA optimize (run once per hour)
    private DateTime _lastOptimize = DateTime.MinValue;

    public MetricsRollupService(MetricsDatabase db, MetricsStoreConfig config,
        ILogger<MetricsRollupService>? logger = null)
    {
        _db     = db;
        _config = config;
        _logger = logger ?? NullLogger<MetricsRollupService>.Instance;
    }

    public void Start()
    {
        // Use infinite period: the timer re-arms itself at the END of RunCycle,
        // which prevents overlapping callbacks if RunCycle takes more than 60 s.
        _timer = new Timer(_ => RunCycle(), null,
            dueTime: TimeSpan.FromSeconds(60),
            period:  Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void RunCycle()
    {
        try
        {
            ProcessRollup1m();
            ProcessRollup5m();
            ProcessRollup1h();
            Prune();

            if ((DateTime.UtcNow - _lastOptimize) > OneHour)
            {
                using var opt = _db.Connection.CreateCommand();
                opt.CommandText = "PRAGMA optimize;";
                opt.ExecuteNonQuery();
                _lastOptimize = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rollup cycle failed");
        }
        finally
        {
            // Re-arm only after work completes — guarantees no overlapping callbacks
            _timer?.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        }
    }

    // ── 1-minute rollups ───────────────────────────────────────────────────────
    private void ProcessRollup1m()
    {
        var lastTs    = _db.GetMeta("last_rollup_1m_ts");
        var nowMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bucketMs  = (long)OneMinute.TotalMilliseconds;

        // Process complete 1-minute buckets that haven't been rolled up yet
        var fromBucket = RoundDownToMinute(lastTs == 0
            ? nowMs - (long)_config.RawRetention.TotalMilliseconds
            : lastTs);
        var toBucket   = RoundDownToMinute(nowMs) - bucketMs; // exclude current (incomplete) minute

        if (fromBucket > toBucket) return;

        using var tx  = _db.Connection.BeginTransaction();
        using var ins = _db.Connection.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT OR REPLACE INTO rollups_1m
                (ts, cpu_avg, cpu_max, mem_avg_bytes, mem_max_bytes,
                 disk_read_avg, disk_write_avg, net_send_avg, net_recv_avg,
                 gpu_avg, gpu_max, sample_count)
            SELECT
                $bucket,
                AVG(cpu_percent), MAX(cpu_percent),
                AVG(mem_used_bytes), MAX(mem_used_bytes),
                AVG(disk_read_bps), AVG(disk_write_bps),
                AVG(net_send_bps),  AVG(net_recv_bps),
                AVG(gpu_percent),   MAX(gpu_percent),
                COUNT(*)
            FROM system_metrics
            WHERE ts >= $from AND ts < $to
            HAVING COUNT(*) > 0";

        var pBucket = ins.Parameters.Add("$bucket", SqliteType.Integer);
        var pFrom   = ins.Parameters.Add("$from",   SqliteType.Integer);
        var pTo     = ins.Parameters.Add("$to",     SqliteType.Integer);

        for (var bucket = fromBucket; bucket <= toBucket; bucket += bucketMs)
        {
            pBucket.Value = bucket;
            pFrom.Value   = bucket;
            pTo.Value     = bucket + bucketMs;
            ins.ExecuteNonQuery();
        }

        _db.SetMeta("last_rollup_1m_ts", toBucket + bucketMs);
        tx.Commit();
    }

    // ── 5-minute rollups ───────────────────────────────────────────────────────
    // internal (not private): lets MetricsRollupServiceTests (InternalsVisibleTo
    // NexusMonitor.Core.Tests) drive a single rollup pass directly instead of waiting on the
    // real 60s timer.
    internal void ProcessRollup5m()
    {
        var lastTs    = _db.GetMeta("last_rollup_5m_ts");
        var nowMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bucketMs  = (long)FiveMinutes.TotalMilliseconds;

        var fromBucket = RoundDownToN(lastTs == 0
            ? nowMs - (long)_config.Rollup1mRetention.TotalMilliseconds
            : lastTs, bucketMs);
        var toBucket   = RoundDownToN(nowMs, bucketMs) - bucketMs;

        if (fromBucket > toBucket) return;

        using var tx  = _db.Connection.BeginTransaction();
        using var ins = _db.Connection.CreateCommand();
        ins.Transaction = tx;
        // Sample-weighted averages (C3): each source bucket's own average is weighted by how many
        // raw samples fed it, so a 2-sample bucket (e.g. right after resume from sleep) doesn't
        // weigh the same as a 58-sample bucket. NULLIF guards the (practically unreachable, since
        // HAVING COUNT(*) > 0 implies SUM(sample_count) >= 1) degenerate all-zero-sample_count
        // case so division yields NULL instead of erroring. MAX columns are unaffected — a max is
        // already weight-independent.
        ins.CommandText = @"
            INSERT OR REPLACE INTO rollups_5m
                (ts, cpu_avg, cpu_max, mem_avg_bytes, mem_max_bytes,
                 disk_read_avg, disk_write_avg, net_send_avg, net_recv_avg,
                 gpu_avg, gpu_max, sample_count)
            SELECT
                $bucket,
                SUM(cpu_avg * sample_count)       / NULLIF(SUM(sample_count), 0), MAX(cpu_max),
                SUM(mem_avg_bytes * sample_count)  / NULLIF(SUM(sample_count), 0), MAX(mem_max_bytes),
                SUM(disk_read_avg * sample_count)  / NULLIF(SUM(sample_count), 0),
                SUM(disk_write_avg * sample_count) / NULLIF(SUM(sample_count), 0),
                SUM(net_send_avg * sample_count)   / NULLIF(SUM(sample_count), 0),
                SUM(net_recv_avg * sample_count)   / NULLIF(SUM(sample_count), 0),
                SUM(gpu_avg * sample_count)        / NULLIF(SUM(sample_count), 0), MAX(gpu_max),
                SUM(sample_count)
            FROM rollups_1m
            WHERE ts >= $from AND ts < $to
            HAVING COUNT(*) > 0";

        var pBucket = ins.Parameters.Add("$bucket", SqliteType.Integer);
        var pFrom   = ins.Parameters.Add("$from",   SqliteType.Integer);
        var pTo     = ins.Parameters.Add("$to",     SqliteType.Integer);

        for (var bucket = fromBucket; bucket <= toBucket; bucket += bucketMs)
        {
            pBucket.Value = bucket;
            pFrom.Value   = bucket;
            pTo.Value     = bucket + bucketMs;
            ins.ExecuteNonQuery();
        }

        _db.SetMeta("last_rollup_5m_ts", toBucket + bucketMs);
        tx.Commit();
    }

    // ── 1-hour rollups ─────────────────────────────────────────────────────────
    // internal (not private): lets MetricsRollupServiceTests (InternalsVisibleTo
    // NexusMonitor.Core.Tests) drive a single rollup pass directly instead of waiting on the
    // real 60s timer.
    internal void ProcessRollup1h()
    {
        var lastTs    = _db.GetMeta("last_rollup_1h_ts");
        var nowMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bucketMs  = (long)OneHour.TotalMilliseconds;

        var fromBucket = RoundDownToN(lastTs == 0
            ? nowMs - (long)_config.Rollup5mRetention.TotalMilliseconds
            : lastTs, bucketMs);
        var toBucket   = RoundDownToN(nowMs, bucketMs) - bucketMs;

        if (fromBucket > toBucket) return;

        using var tx  = _db.Connection.BeginTransaction();
        using var ins = _db.Connection.CreateCommand();
        ins.Transaction = tx;
        // Sample-weighted averages (C3) — same rationale as the 1m->5m rollup above, applied to
        // 5m->1h.
        ins.CommandText = @"
            INSERT OR REPLACE INTO rollups_1h
                (ts, cpu_avg, cpu_max, mem_avg_bytes, mem_max_bytes,
                 disk_read_avg, disk_write_avg, net_send_avg, net_recv_avg,
                 gpu_avg, gpu_max, sample_count)
            SELECT
                $bucket,
                SUM(cpu_avg * sample_count)       / NULLIF(SUM(sample_count), 0), MAX(cpu_max),
                SUM(mem_avg_bytes * sample_count)  / NULLIF(SUM(sample_count), 0), MAX(mem_max_bytes),
                SUM(disk_read_avg * sample_count)  / NULLIF(SUM(sample_count), 0),
                SUM(disk_write_avg * sample_count) / NULLIF(SUM(sample_count), 0),
                SUM(net_send_avg * sample_count)   / NULLIF(SUM(sample_count), 0),
                SUM(net_recv_avg * sample_count)   / NULLIF(SUM(sample_count), 0),
                SUM(gpu_avg * sample_count)        / NULLIF(SUM(sample_count), 0), MAX(gpu_max),
                SUM(sample_count)
            FROM rollups_5m
            WHERE ts >= $from AND ts < $to
            HAVING COUNT(*) > 0";

        var pBucket = ins.Parameters.Add("$bucket", SqliteType.Integer);
        var pFrom   = ins.Parameters.Add("$from",   SqliteType.Integer);
        var pTo     = ins.Parameters.Add("$to",     SqliteType.Integer);

        for (var bucket = fromBucket; bucket <= toBucket; bucket += bucketMs)
        {
            pBucket.Value = bucket;
            pFrom.Value   = bucket;
            pTo.Value     = bucket + bucketMs;
            ins.ExecuteNonQuery();
        }

        _db.SetMeta("last_rollup_1h_ts", toBucket + bucketMs);
        tx.Commit();
    }

    // ── Retention pruning ──────────────────────────────────────────────────────
    private void Prune()
    {
        var now         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoffRaw   = now - (long)_config.RawRetention.TotalMilliseconds;
        var cutoff1m    = now - (long)_config.Rollup1mRetention.TotalMilliseconds;
        var cutoff5m    = now - (long)_config.Rollup5mRetention.TotalMilliseconds;
        var cutoff1h    = now - (long)_config.Rollup1hRetention.TotalMilliseconds;
        var cutoffEvents = now - (long)_config.EventsRetention.TotalMilliseconds;
        var cutoffHealth = now - (long)_config.Rollup1mRetention.TotalMilliseconds;

        using var tx  = _db.Connection.BeginTransaction();
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;

        Execute(cmd, "DELETE FROM system_metrics    WHERE ts < $c", cutoffRaw);
        Execute(cmd, "DELETE FROM process_snapshots WHERE ts < $c", cutoffRaw);
        Execute(cmd, "DELETE FROM network_snapshots WHERE ts < $c", cutoffRaw);
        Execute(cmd, "DELETE FROM rollups_1m        WHERE ts < $c", cutoff1m);
        Execute(cmd, "DELETE FROM rollups_5m        WHERE ts < $c", cutoff5m);
        Execute(cmd, "DELETE FROM rollups_1h        WHERE ts < $c", cutoff1h);
        Execute(cmd, "DELETE FROM events            WHERE ts < $c", cutoffEvents);
        Execute(cmd, "DELETE FROM health_snapshots  WHERE ts < $c", cutoffHealth);

        tx.Commit();
    }

    private static void Execute(SqliteCommand cmd, string sql, long cutoff)
    {
        cmd.CommandText = sql;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$c", cutoff);
        cmd.ExecuteNonQuery();
    }

    // ── Bucket helpers ─────────────────────────────────────────────────────────
    private static long RoundDownToMinute(long ms)
    {
        const long minMs = 60_000L;
        return (ms / minMs) * minMs;
    }

    private static long RoundDownToN(long ms, long bucketMs) =>
        (ms / bucketMs) * bucketMs;

    // ── IDisposable ────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
