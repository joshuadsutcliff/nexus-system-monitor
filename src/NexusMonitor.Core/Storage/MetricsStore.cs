using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Storage;

/// <summary>
/// Subscribes to live metric streams and persists them to SQLite in batched transactions.
/// Also implements IMetricsReader for historical queries.
/// </summary>
public sealed class MetricsStore : IMetricsReader, IEventWriter, IDisposable
{
    private readonly MetricsDatabase            _db;
    private readonly MetricsStoreConfig         _config;
    private readonly ISystemMetricsProvider     _metricsProvider;
    private readonly IProcessProvider           _processProvider;
    private readonly INetworkConnectionsProvider _networkProvider;
    private readonly ILogger<MetricsStore>      _logger;

    // Dedicated read-only connection — WAL mode allows concurrent readers alongside the writer.
    // This avoids SqliteConnection thread-safety issues when reader queries run on Task.Run threads
    // while FlushAll() is executing on the writer connection under _lock.
    private readonly SqliteConnection _readConn;
    // Serializes concurrent Task.Run calls that share _readConn (SqliteConnection is not thread-safe).
    private readonly SemaphoreSlim _readLock = new(1, 1);

    private IDisposable? _metricsSub;
    private IDisposable? _processSub;
    private IDisposable? _networkSub;

    // Write buffers — guarded by _lock
    private readonly List<SystemMetrics>                               _metricsBuffer = new();
    // Process buffer stores top-N only (pre-filtered at ingest) to avoid holding 300 rows × 30 ticks in memory
    private readonly List<(long ts, IReadOnlyList<ProcessInfo> procs)> _processBuffer = new();
    private readonly List<(long ts, IReadOnlyList<NetworkConnection> conns)> _networkBuffer = new();
    private readonly object _lock = new();

    private bool _disposed;

    public MetricsStore(
        MetricsDatabase             db,
        MetricsStoreConfig          config,
        ISystemMetricsProvider      metricsProvider,
        IProcessProvider            processProvider,
        INetworkConnectionsProvider networkProvider,
        ILogger<MetricsStore>       logger)
    {
        _db              = db;
        _config          = config;
        _metricsProvider = metricsProvider;
        _processProvider = processProvider;
        _networkProvider = networkProvider;
        _logger          = logger;

        // Open a second connection to the same DB file for read queries.
        // SQLite WAL mode supports concurrent readers on separate connections.
        _readConn = new SqliteConnection($"Data Source={_db.Connection.DataSource}");
        _readConn.Open();
        using var pragma = _readConn.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = ON; PRAGMA journal_mode = WAL; PRAGMA cache_size = -500;";
        pragma.ExecuteNonQuery();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    public void Start(TimeSpan interval)
    {
        if (_metricsSub != null) return;

        _metricsSub = _metricsProvider
            .GetMetricsStream(interval)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "MetricsStore metrics stream faulted; retrying with backoff"))
            .Subscribe(OnMetricsTick);

        _processSub = _processProvider
            .GetProcessStream(interval)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "MetricsStore process stream faulted; retrying with backoff"))
            .Subscribe(procs => OnProcessTick(procs));

        if (_config.RecordNetworkSnapshots)
        {
            _networkSub = _networkProvider
                .GetConnectionStream(TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds, 2)))
                .Subscribe(conns => OnNetworkTick(conns));
        }
    }

    public void Stop()
    {
        _metricsSub?.Dispose();
        _metricsSub = null;
        _processSub?.Dispose();
        _processSub = null;
        _networkSub?.Dispose();
        _networkSub = null;
        lock (_lock) FlushAll();
    }

    // ── Incoming ticks ─────────────────────────────────────────────────────────
    private void OnMetricsTick(SystemMetrics m)
    {
        lock (_lock)
        {
            _metricsBuffer.Add(m);
            int maxBuf = _config.MaxBufferSize > 0 ? _config.MaxBufferSize : _config.WriteBufferSize * 3;
            if (_metricsBuffer.Count > maxBuf)
            {
                _metricsBuffer.RemoveAt(0);
                _logger.LogWarning(
                    "MetricsStore buffer overflow — oldest sample dropped (cap={Cap})", maxBuf);
            }
            if (_metricsBuffer.Count >= _config.WriteBufferSize)
                FlushAll();
        }
    }

    private void OnProcessTick(IReadOnlyList<ProcessInfo> procs)
    {
        // Pre-filter to top-N at ingest time: avoids holding ~300 ProcessInfo × 30 ticks in memory.
        // Single-pass partial sort using min-heaps: O(N log n) instead of O(N log N) × 2 + Union.
        var n = _config.TopNProcesses;
        var cpuHeap = new PriorityQueue<ProcessInfo, double>(n + 1);
        var memHeap = new PriorityQueue<ProcessInfo, long>(n + 1);

        foreach (var p in procs)
        {
            cpuHeap.Enqueue(p, p.CpuPercent);
            if (cpuHeap.Count > n) cpuHeap.Dequeue(); // drop lowest-CPU entry

            memHeap.Enqueue(p, p.WorkingSetBytes);
            if (memHeap.Count > n) memHeap.Dequeue(); // drop lowest-memory entry
        }

        var seen = new HashSet<int>(n * 2);
        var topN = new List<ProcessInfo>(n * 2);
        while (cpuHeap.Count > 0) { var p = cpuHeap.Dequeue(); if (seen.Add(p.Pid)) topN.Add(p); }
        while (memHeap.Count > 0) { var p = memHeap.Dequeue(); if (seen.Add(p.Pid)) topN.Add(p); }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _processBuffer.Add((ts, topN));
            int maxBuf = _config.MaxBufferSize > 0 ? _config.MaxBufferSize : _config.WriteBufferSize * 3;
            if (_processBuffer.Count > maxBuf)
            {
                _processBuffer.RemoveAt(0);
                _logger.LogWarning(
                    "MetricsStore process buffer overflow — oldest snapshot dropped (cap={Cap})", maxBuf);
            }
        }
    }

    private void OnNetworkTick(IReadOnlyList<NetworkConnection> conns)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _networkBuffer.Add((ts, conns));
            int maxBuf = _config.MaxBufferSize > 0 ? _config.MaxBufferSize : _config.WriteBufferSize * 3;
            if (_networkBuffer.Count > maxBuf)
            {
                _networkBuffer.RemoveAt(0);
                _logger.LogWarning(
                    "MetricsStore network buffer overflow — oldest snapshot dropped (cap={Cap})", maxBuf);
            }
        }
    }

    // ── Flush ──────────────────────────────────────────────────────────────────
    private void FlushAll()
    {
        // Called inside _lock
        if (_metricsBuffer.Count == 0 && _processBuffer.Count == 0 && _networkBuffer.Count == 0)
            return;

        try
        {
            using var tx = _db.Connection.BeginTransaction();
            FlushMetrics(tx);
            FlushProcesses(tx);
            FlushNetwork(tx);
            tx.Commit();
            // Clear buffers only after a successful commit — data is safe on disk
            _metricsBuffer.Clear();
            _processBuffer.Clear();
            _networkBuffer.Clear();
        }
        catch (Exception ex) { _logger.LogError(ex, "MetricsStore: FlushAll failed"); }
    }

    private void FlushMetrics(SqliteTransaction tx)
    {
        if (_metricsBuffer.Count == 0) return;

        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO system_metrics
                (ts, cpu_percent, cpu_freq_mhz, cpu_temp_c, process_count, thread_count, handle_count,
                 mem_used_bytes, mem_total_bytes, mem_cached_bytes, mem_committed_bytes,
                 disk_read_bps, disk_write_bps, net_send_bps, net_recv_bps,
                 gpu_percent, gpu_mem_used, gpu_mem_total, gpu_temp_c)
            VALUES
                ($ts, $cpu, $freq, $temp, $procs, $threads, $handles,
                 $memUsed, $memTotal, $memCached, $memCommitted,
                 $diskR, $diskW, $netS, $netR,
                 $gpu, $gpuMem, $gpuMemTotal, $gpuTemp)";

        var pTs          = cmd.Parameters.Add("$ts",          SqliteType.Integer);
        var pCpu         = cmd.Parameters.Add("$cpu",         SqliteType.Real);
        var pFreq        = cmd.Parameters.Add("$freq",        SqliteType.Real);
        var pTemp        = cmd.Parameters.Add("$temp",        SqliteType.Real);
        var pProcs       = cmd.Parameters.Add("$procs",       SqliteType.Integer);
        var pThreads     = cmd.Parameters.Add("$threads",     SqliteType.Integer);
        var pHandles     = cmd.Parameters.Add("$handles",     SqliteType.Integer);
        var pMemUsed     = cmd.Parameters.Add("$memUsed",     SqliteType.Integer);
        var pMemTotal    = cmd.Parameters.Add("$memTotal",    SqliteType.Integer);
        var pMemCached   = cmd.Parameters.Add("$memCached",   SqliteType.Integer);
        var pMemCommit   = cmd.Parameters.Add("$memCommitted",SqliteType.Integer);
        var pDiskR       = cmd.Parameters.Add("$diskR",       SqliteType.Integer);
        var pDiskW       = cmd.Parameters.Add("$diskW",       SqliteType.Integer);
        var pNetS        = cmd.Parameters.Add("$netS",        SqliteType.Integer);
        var pNetR        = cmd.Parameters.Add("$netR",        SqliteType.Integer);
        var pGpu         = cmd.Parameters.Add("$gpu",         SqliteType.Real);
        var pGpuMem      = cmd.Parameters.Add("$gpuMem",      SqliteType.Integer);
        var pGpuMemTotal = cmd.Parameters.Add("$gpuMemTotal", SqliteType.Integer);
        var pGpuTemp     = cmd.Parameters.Add("$gpuTemp",     SqliteType.Real);

        foreach (var m in _metricsBuffer)
        {
            var diskR = m.Disks.Sum(d => d.ReadBytesPerSec);
            var diskW = m.Disks.Sum(d => d.WriteBytesPerSec);
            var netS  = m.NetworkAdapters.Sum(n => n.SendBytesPerSec);
            var netR  = m.NetworkAdapters.Sum(n => n.RecvBytesPerSec);
            var gpu   = m.Gpus.FirstOrDefault();

            pTs.Value          = new DateTimeOffset(m.Timestamp).ToUnixTimeMilliseconds();
            pCpu.Value         = m.Cpu.TotalPercent;
            pFreq.Value        = m.Cpu.FrequencyMhz;
            pTemp.Value        = m.Cpu.TemperatureCelsius > 0 ? (object)m.Cpu.TemperatureCelsius : DBNull.Value;
            pProcs.Value       = DBNull.Value;
            pThreads.Value     = DBNull.Value;
            pHandles.Value     = DBNull.Value;
            pMemUsed.Value     = m.Memory.UsedBytes;
            pMemTotal.Value    = m.Memory.TotalBytes;
            pMemCached.Value   = m.Memory.CachedBytes;
            pMemCommit.Value   = m.Memory.CommitTotalBytes;
            pDiskR.Value       = diskR;
            pDiskW.Value       = diskW;
            pNetS.Value        = netS;
            pNetR.Value        = netR;
            pGpu.Value         = gpu != null ? (object)gpu.UsagePercent              : DBNull.Value;
            pGpuMem.Value      = gpu != null ? (object)gpu.DedicatedMemoryUsedBytes  : DBNull.Value;
            pGpuMemTotal.Value = gpu != null ? (object)gpu.DedicatedMemoryTotalBytes : DBNull.Value;
            pGpuTemp.Value     = gpu != null && gpu.TemperatureCelsius > 0
                                     ? (object)gpu.TemperatureCelsius : DBNull.Value;

            cmd.ExecuteNonQuery();
        }
    }

    private void FlushProcesses(SqliteTransaction tx)
    {
        if (_processBuffer.Count == 0) return;

        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO process_snapshots
                (ts, pid, name, cpu_percent, mem_bytes,
                 io_read_bps, io_write_bps, gpu_percent, net_send_bps, net_recv_bps, category)
            VALUES
                ($ts, $pid, $name, $cpu, $mem, $ioR, $ioW, $gpu, $netS, $netR, $cat)";

        var pTs   = cmd.Parameters.Add("$ts",   SqliteType.Integer);
        var pPid  = cmd.Parameters.Add("$pid",  SqliteType.Integer);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pCpu  = cmd.Parameters.Add("$cpu",  SqliteType.Real);
        var pMem  = cmd.Parameters.Add("$mem",  SqliteType.Integer);
        var pIoR  = cmd.Parameters.Add("$ioR",  SqliteType.Integer);
        var pIoW  = cmd.Parameters.Add("$ioW",  SqliteType.Integer);
        var pGpu  = cmd.Parameters.Add("$gpu",  SqliteType.Real);
        var pNetS = cmd.Parameters.Add("$netS", SqliteType.Integer);
        var pNetR = cmd.Parameters.Add("$netR", SqliteType.Integer);
        var pCat  = cmd.Parameters.Add("$cat",  SqliteType.Integer);

        foreach (var (ts, procs) in _processBuffer)
        {
            // procs already contains the pre-filtered top-N from OnProcessTick
            pTs.Value = ts;
            foreach (var p in procs)
            {
                pPid.Value  = p.Pid;
                pName.Value = p.Name;
                pCpu.Value  = p.CpuPercent;
                pMem.Value  = p.WorkingSetBytes;
                pIoR.Value  = p.IoReadBytesPerSec;
                pIoW.Value  = p.IoWriteBytesPerSec;
                pGpu.Value  = p.GpuPercent > 0 ? (object)p.GpuPercent : DBNull.Value;
                pNetS.Value = p.NetworkSendBytesPerSec;
                pNetR.Value = p.NetworkRecvBytesPerSec;
                pCat.Value  = (int)p.Category;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void FlushNetwork(SqliteTransaction tx)
    {
        if (_networkBuffer.Count == 0) return;

        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO network_snapshots
                (ts, protocol, local_addr, local_port, remote_addr, remote_port,
                 state, pid, process_name, send_bps, recv_bps)
            VALUES
                ($ts, $proto, $lAddr, $lPort, $rAddr, $rPort,
                 $state, $pid, $name, $send, $recv)";

        var pTs    = cmd.Parameters.Add("$ts",    SqliteType.Integer);
        var pProto = cmd.Parameters.Add("$proto", SqliteType.Integer);
        var pLAddr = cmd.Parameters.Add("$lAddr", SqliteType.Text);
        var pLPort = cmd.Parameters.Add("$lPort", SqliteType.Integer);
        var pRAddr = cmd.Parameters.Add("$rAddr", SqliteType.Text);
        var pRPort = cmd.Parameters.Add("$rPort", SqliteType.Integer);
        var pState = cmd.Parameters.Add("$state", SqliteType.Integer);
        var pPid   = cmd.Parameters.Add("$pid",   SqliteType.Integer);
        var pName  = cmd.Parameters.Add("$name",  SqliteType.Text);
        var pSend  = cmd.Parameters.Add("$send",  SqliteType.Integer);
        var pRecv  = cmd.Parameters.Add("$recv",  SqliteType.Integer);

        foreach (var (ts, conns) in _networkBuffer)
        {
            // Skip LISTEN-only entries if over limit
            var filtered = conns
                .Where(c => c.State != TcpConnectionState.Listen || conns.Count <= _config.NetworkSnapshotMaxRows)
                .Take(_config.NetworkSnapshotMaxRows);

            pTs.Value = ts;
            foreach (var c in filtered)
            {
                pProto.Value = (int)c.Protocol;
                pLAddr.Value = c.LocalAddress;
                pLPort.Value = c.LocalPort;
                pRAddr.Value = c.RemoteAddress;
                pRPort.Value = c.RemotePort;
                pState.Value = (int)c.State;
                pPid.Value   = c.ProcessId;
                pName.Value  = c.ProcessName ?? (object)DBNull.Value;
                pSend.Value  = DBNull.Value;
                pRecv.Value  = DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ── IEventWriter ───────────────────────────────────────────────────────────
    public Task InsertEventAsync(
        string  eventType, int     severity,
        string? metricName, double? metricValue, double? threshold,
        string? description, string? metadataJson = null) =>
        Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    using var cmd = _db.Connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO events
                            (ts, event_type, severity, metric_name, metric_value,
                             threshold, description, metadata_json)
                        VALUES
                            ($ts, $type, $sev, $mname, $mval,
                             $thresh, $desc, $meta)";
                    cmd.Parameters.AddWithValue("$ts",    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$type",  eventType);
                    cmd.Parameters.AddWithValue("$sev",   severity);
                    cmd.Parameters.AddWithValue("$mname", (object?)metricName   ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$mval",  (object?)metricValue  ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$thresh",(object?)threshold    ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$desc",  (object?)description  ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$meta",  (object?)metadataJson ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "MetricsStore: InsertEventAsync failed for type {EventType}", eventType); }
            }
        });

    // ── IMetricsReader ─────────────────────────────────────────────────────────
    public Task<IReadOnlyList<MetricsDataPoint>> GetSystemMetricsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<MetricsDataPoint>>(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return QuerySystemMetrics(from, to);
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    private IReadOnlyList<MetricsDataPoint> QuerySystemMetrics(DateTimeOffset from, DateTimeOffset to)
    {
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs   = to.ToUnixTimeMilliseconds();
        var span   = to - from;

        // Auto-select granularity
        if (span < TimeSpan.FromHours(2))
            return QueryRaw(fromMs, toMs);
        if (span < TimeSpan.FromDays(1))
            return QueryRollup("rollups_1m", fromMs, toMs);
        if (span < TimeSpan.FromDays(7))
            return QueryRollup("rollups_5m", fromMs, toMs);
        return QueryRollup("rollups_1h", fromMs, toMs);
    }

    private IReadOnlyList<MetricsDataPoint> QueryRaw(long fromMs, long toMs)
    {
        var result = new List<MetricsDataPoint>();
        using var cmd = _readConn.CreateCommand();
        cmd.CommandText = @"
            SELECT ts, cpu_percent, mem_used_bytes, mem_total_bytes,
                   disk_read_bps, disk_write_bps, net_send_bps, net_recv_bps,
                   gpu_percent
            FROM system_metrics
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts";
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to",   toMs);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MetricsDataPoint(
                Timestamp:     DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                CpuPercent:    reader.GetDouble(1),
                CpuMaxPercent: null,
                MemUsedBytes:  reader.GetInt64(2),
                MemMaxBytes:   null,
                DiskReadBps:   reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                DiskWriteBps:  reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                NetSendBps:    reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                NetRecvBps:    reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                GpuPercent:    reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                GpuMaxPercent: null,
                SampleCount:   1));
        }
        return result;
    }

    private IReadOnlyList<MetricsDataPoint> QueryRollup(string table, long fromMs, long toMs)
    {
        var result = new List<MetricsDataPoint>();
        using var cmd = _readConn.CreateCommand();
        cmd.CommandText = $@"
            SELECT ts, cpu_avg, cpu_max, mem_avg_bytes, mem_max_bytes,
                   disk_read_avg, disk_write_avg, net_send_avg, net_recv_avg,
                   gpu_avg, gpu_max, sample_count
            FROM {table}
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts";
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to",   toMs);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MetricsDataPoint(
                Timestamp:     DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                CpuPercent:    reader.GetDouble(1),
                CpuMaxPercent: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                MemUsedBytes:  reader.GetInt64(3),
                MemMaxBytes:   reader.IsDBNull(4) ? null : reader.GetInt64(4),
                DiskReadBps:   reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                DiskWriteBps:  reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                NetSendBps:    reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                NetRecvBps:    reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                GpuPercent:    reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                GpuMaxPercent: reader.IsDBNull(10) ? null : reader.GetDouble(10),
                SampleCount:   reader.IsDBNull(11) ? 0 : reader.GetInt32(11)));
        }
        return result;
    }

    public Task<IReadOnlyList<ProcessDataPoint>> GetProcessHistoryAsync(
        string processName, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessDataPoint>>(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = new List<ProcessDataPoint>();
                using var cmd = _readConn.CreateCommand();
                cmd.CommandText = @"
                    SELECT ts, pid, name, cpu_percent, mem_bytes,
                           io_read_bps, io_write_bps, gpu_percent
                    FROM process_snapshots
                    WHERE name = $name AND ts >= $from AND ts <= $to
                    ORDER BY ts";
                cmd.Parameters.AddWithValue("$name", processName);
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$to",   to.ToUnixTimeMilliseconds());

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new ProcessDataPoint(
                        Timestamp:  DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                        Pid:        reader.GetInt32(1),
                        Name:       reader.GetString(2),
                        CpuPercent: reader.GetDouble(3),
                        MemBytes:   reader.GetInt64(4),
                        IoReadBps:  reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                        IoWriteBps: reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                        GpuPercent: reader.IsDBNull(7) ? 0 : reader.GetDouble(7)));
                }
                return (IReadOnlyList<ProcessDataPoint>)result;
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    public Task<IReadOnlyList<NetworkDataPoint>> GetNetworkHistoryAsync(
        string? remoteAddress, int? remotePort,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<NetworkDataPoint>>(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = new List<NetworkDataPoint>();
                using var cmd = _readConn.CreateCommand();

                var where = "ts >= $from AND ts <= $to";
                if (remoteAddress != null) where += " AND remote_addr = $rAddr";
                if (remotePort    != null) where += " AND remote_port = $rPort";

                cmd.CommandText = $@"
                    SELECT ts, protocol, local_addr, local_port, remote_addr, remote_port,
                           state, pid, process_name, send_bps, recv_bps
                    FROM network_snapshots
                    WHERE {where}
                    ORDER BY ts";
                cmd.Parameters.AddWithValue("$from",  from.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$to",    to.ToUnixTimeMilliseconds());
                if (remoteAddress != null) cmd.Parameters.AddWithValue("$rAddr", remoteAddress);
                if (remotePort    != null) cmd.Parameters.AddWithValue("$rPort", remotePort.Value);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new NetworkDataPoint(
                        Timestamp:   DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                        Protocol:    reader.GetInt32(1),
                        LocalAddr:   reader.IsDBNull(2) ? "" : reader.GetString(2),
                        LocalPort:   reader.IsDBNull(3) ? 0  : reader.GetInt32(3),
                        RemoteAddr:  reader.IsDBNull(4) ? "" : reader.GetString(4),
                        RemotePort:  reader.IsDBNull(5) ? 0  : reader.GetInt32(5),
                        State:       reader.IsDBNull(6) ? 0  : reader.GetInt32(6),
                        Pid:         reader.IsDBNull(7) ? 0  : reader.GetInt32(7),
                        ProcessName: reader.IsDBNull(8) ? "" : reader.GetString(8),
                        SendBps:     reader.IsDBNull(9) ? 0  : reader.GetInt64(9),
                        RecvBps:     reader.IsDBNull(10) ? 0 : reader.GetInt64(10)));
                }
                return (IReadOnlyList<NetworkDataPoint>)result;
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    public Task<IReadOnlyList<StoredEvent>> GetEventsAsync(
        DateTimeOffset from, DateTimeOffset to,
        string? eventType = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<StoredEvent>>(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = new List<StoredEvent>();
                using var cmd = _readConn.CreateCommand();
                var where = "ts >= $from AND ts <= $to";
                if (eventType != null) where += " AND event_type = $type";

                cmd.CommandText = $@"
                    SELECT id, ts, event_type, severity, metric_name, metric_value,
                           threshold, description, metadata_json
                    FROM events
                    WHERE {where}
                    ORDER BY ts";
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$to",   to.ToUnixTimeMilliseconds());
                if (eventType != null) cmd.Parameters.AddWithValue("$type", eventType);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new StoredEvent(
                        Id:           reader.GetInt64(0),
                        Timestamp:    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                        EventType:    reader.GetString(2),
                        Severity:     reader.GetInt32(3),
                        MetricName:   reader.IsDBNull(4) ? null : reader.GetString(4),
                        MetricValue:  reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        Threshold:    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        Description:  reader.IsDBNull(7) ? null : reader.GetString(7),
                        MetadataJson: reader.IsDBNull(8) ? null : reader.GetString(8)));
                }
                return (IReadOnlyList<StoredEvent>)result;
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    public Task<(DateTimeOffset oldest, DateTimeOffset newest)> GetDataRangeAsync(
        CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cmd = _readConn.CreateCommand();
                cmd.CommandText = "SELECT MIN(ts), MAX(ts) FROM system_metrics";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                    return (DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                return (
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    public Task<IReadOnlyList<ProcessSummary>> GetTopProcessSummariesAsync(
        DateTimeOffset from, DateTimeOffset to, int topN = 10, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var fromMs = from.ToUnixTimeMilliseconds();
                var toMs   = to.ToUnixTimeMilliseconds();

                using var cmd = _readConn.CreateCommand();
                cmd.CommandText = @"
                    SELECT name,
                           AVG(cpu_percent)      AS avg_cpu,
                           MAX(cpu_percent)      AS peak_cpu,
                           AVG(mem_bytes)/1048576.0 AS avg_mem_mb
                    FROM process_snapshots
                    WHERE ts >= $from AND ts < $to
                    GROUP BY name
                    ORDER BY avg_cpu DESC
                    LIMIT $topN";
                cmd.Parameters.AddWithValue("$from",  fromMs);
                cmd.Parameters.AddWithValue("$to",    toMs);
                cmd.Parameters.AddWithValue("$topN",  topN);

                var result = new List<ProcessSummary>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(new ProcessSummary(
                        Name:          reader.GetString(0),
                        AvgCpuPercent: reader.GetDouble(1),
                        PeakCpuPercent:reader.GetDouble(2),
                        AvgMemMb:      reader.GetDouble(3)));
                return (IReadOnlyList<ProcessSummary>)result;
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    public long GetDatabaseSizeBytes() => _db.GetDatabaseSizeBytes();

    // ── Health snapshots ───────────────────────────────────────────────────────
    public Task WriteHealthSnapshotAsync(SystemHealthSnapshot snapshot)
    {
        if (_disposed) return Task.CompletedTask;
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    using var cmd = _db.Connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO health_snapshots (ts, overall, cpu, memory, disk, gpu, bottleneck)
                        VALUES ($ts, $overall, $cpu, $memory, $disk, $gpu, $bottleneck)";
                    cmd.Parameters.AddWithValue("$ts",         DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$overall",    snapshot.OverallScore);
                    cmd.Parameters.AddWithValue("$cpu",        snapshot.Cpu.Score);
                    cmd.Parameters.AddWithValue("$memory",     snapshot.Memory.Score);
                    cmd.Parameters.AddWithValue("$disk",       snapshot.Disk.Score);
                    cmd.Parameters.AddWithValue("$gpu",        snapshot.Gpu.Score);
                    cmd.Parameters.AddWithValue("$bottleneck", (object?)snapshot.Bottleneck?.Bottleneck.ToString() ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "MetricsStore: WriteHealthSnapshotAsync failed"); }
            }
        });
    }

    public Task<IReadOnlyList<HealthDataPoint>> GetHealthHistoryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<HealthDataPoint>>(async () =>
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = new List<HealthDataPoint>();
                using var cmd = _readConn.CreateCommand();
                cmd.CommandText = @"
                    SELECT ts, overall, cpu, memory, disk, gpu, bottleneck
                    FROM health_snapshots
                    WHERE ts >= $from AND ts <= $to
                    ORDER BY ts";
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$to",   to.ToUnixTimeMilliseconds());

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new HealthDataPoint(
                        Timestamp:  DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                        Overall:    reader.GetDouble(1),
                        Cpu:        reader.GetDouble(2),
                        Memory:     reader.GetDouble(3),
                        Disk:       reader.GetDouble(4),
                        Gpu:        reader.GetDouble(5),
                        Bottleneck: reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
                return (IReadOnlyList<HealthDataPoint>)result;
            }
            finally
            {
                _readLock.Release();
            }
        }, ct);

    // ── IDisposable ────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _readConn.Dispose();
        _readLock.Dispose();
    }
}
