using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Reactive;

namespace NexusMonitor.Core.Health;

/// <summary>
/// Subscribes to the metrics and process streams, computes a
/// <see cref="SystemHealthSnapshot"/> every tick, and exposes it as an Rx observable.
/// </summary>
public sealed class SystemHealthService : IDisposable
{
    private readonly ISystemMetricsProvider _metrics;
    private readonly IProcessProvider       _processes;
    private readonly AppSettings            _settings;
    private readonly ILogger<SystemHealthService> _logger;
    private readonly IScheduler             _scheduler;

    private static readonly TimeSpan RetryInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryMaxDelay     = TimeSpan.FromSeconds(30);

    private readonly BehaviorSubject<SystemHealthSnapshot> _subject =
        new(new SystemHealthSnapshot());

    // Staleness signal for the UI: true when no snapshot has arrived for > 3× the interval.
    private readonly BehaviorSubject<bool> _isStaleSubject = new(false);

    // Latest process list, fed by the (decoupled) process stream. Read on every metrics
    // tick. If the process stream stalls/faults, this simply retains its last value so
    // health snapshots keep ticking with last-known (or empty) process data.
    private volatile IReadOnlyList<ProcessInfo> _latestProcesses = Array.Empty<ProcessInfo>();

    private IDisposable? _subscription;
    private IDisposable? _processSubscription;
    private IDisposable? _staleSubscription;

    // Rolling history for trend computation (60 samples ≈ 2 min at 2s interval)
    private const int HistorySize = 60;
    private readonly Queue<double> _cpuHistory    = new(HistorySize);
    private readonly Queue<double> _memHistory    = new(HistorySize);
    private readonly Queue<double> _diskHistory   = new(HistorySize);
    private readonly Queue<double> _gpuHistory    = new(HistorySize);
    private readonly Queue<double> _overallHistory = new(HistorySize);
    private readonly object _histLock = new();

    public IObservable<SystemHealthSnapshot> HealthStream => _subject.AsObservable();
    public SystemHealthSnapshot Current => _subject.Value;

    /// <summary>
    /// Emits <c>true</c> when no health snapshot has been produced for more than 3× the
    /// configured metrics interval (i.e. the pipeline has stalled), and <c>false</c> while
    /// snapshots are flowing. A cheap signal the UI can bind to surface a "data stale" state.
    /// </summary>
    public IObservable<bool> IsStaleStream => _isStaleSubject.DistinctUntilChanged();

    public SystemHealthService(
        ISystemMetricsProvider metrics,
        IProcessProvider       processes,
        AppSettings            settings,
        ILogger<SystemHealthService>? logger = null,
        IScheduler?            scheduler = null)
    {
        _metrics   = metrics;
        _processes = processes;
        _settings  = settings;
        _logger    = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemHealthService>.Instance;
        _scheduler = scheduler ?? Scheduler.Default;
    }

    public void Start(TimeSpan interval)
    {
        // Dispose any previous subscription first so interval changes take effect
        Stop();

        var metricsObs  = _metrics.GetMetricsStream(interval);
        var processObs  = _processes.GetProcessStream(interval);

        // Process stream is DECOUPLED from the health cadence: it only feeds the
        // latest-known process list. If it goes silent, stalls (e.g. a D-state /proc read),
        // or faults, health snapshots keep ticking off the metrics stream using the
        // last-known (or empty) process data — the pipeline can no longer stall on it.
        _processSubscription = processObs
            .RetryWithBackoff(RetryInitialDelay, RetryMaxDelay, _scheduler,
                ex => _logger.LogWarning(ex, "SystemHealthService process stream faulted; retrying with backoff"))
            .Subscribe(
                p => _latestProcesses = p,
                ex => _logger.LogError(ex, "SystemHealthService process stream terminated"));

        // Metrics stream drives the health cadence. Resilience wrapper resubscribes on a
        // provider fault instead of letting the pipeline die silently.
        _subscription = metricsObs
            .RetryWithBackoff(RetryInitialDelay, RetryMaxDelay, _scheduler,
                ex => _logger.LogWarning(ex, "SystemHealthService metrics stream faulted; retrying with backoff"))
            .Subscribe(
                m => Update(m, _latestProcesses),
                ex => _logger.LogError(ex, "SystemHealthService metrics stream terminated"));

        // Staleness signal: emit false whenever a snapshot arrives, then true if no further
        // snapshot appears within 3× the interval. Switch() cancels the pending "true" as
        // soon as the next snapshot arrives. All timing runs on the injectable scheduler.
        var staleThreshold = TimeSpan.FromTicks(interval.Ticks * 3);
        _staleSubscription = _subject
            .Select(_ => Observable.Return(false)
                .Concat(Observable.Timer(staleThreshold, _scheduler).Select(_ => true)))
            .Switch()
            .DistinctUntilChanged()
            .Subscribe(stale => _isStaleSubject.OnNext(stale));
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        _processSubscription?.Dispose();
        _processSubscription = null;
        _staleSubscription?.Dispose();
        _staleSubscription = null;
    }

    private void Update(SystemMetrics m, IReadOnlyList<ProcessInfo> processes)
    {
        // ── Raw values ────────────────────────────────────────────────────────
        var cpuVal  = m.Cpu.TotalPercent;
        var memVal  = m.Memory.UsedPercent;
        // Guard against pseudo/zero-capacity volumes poisoning the scores if a
        // provider's enumeration filter ever regresses (macOS /dev devfs class).
        var realDisks = m.Disks.Where(d => d.TotalBytes > 0).ToList();
        var diskVal = realDisks.Count > 0 ? realDisks.Average(d => d.ActivePercent) : 0;
        var diskUsed = realDisks.Count > 0 ? realDisks.Max(d => d.UsedPercent) : 0;
        var gpuVal  = m.Gpus.Count  > 0 ? m.Gpus.Average(g => g.UsagePercent) : 0;
        var cpuTemp = m.Cpu.TemperatureCelsius;
        var gpuTemp = m.Gpus.Count  > 0 ? m.Gpus.Average(g => g.TemperatureCelsius) : -1;

        // ── Scores ────────────────────────────────────────────────────────────
        var cpuScore    = HealthScoring.ScoreCpu(cpuVal);
        var memScore    = HealthScoring.ScoreMemory(memVal);
        var diskScore   = HealthScoring.ScoreDisk(diskVal, diskUsed);
        var gpuScore    = HealthScoring.ScoreGpu(gpuVal);
        var thermalScore = HealthScoring.ScoreThermal(cpuTemp, gpuTemp);
        var overall     = HealthScoring.CompositeScore(cpuScore, memScore, diskScore, gpuScore, thermalScore);

        // ── History + trends ─────────────────────────────────────────────────
        lock (_histLock)
        {
            Enqueue(_cpuHistory,     cpuScore);
            Enqueue(_memHistory,     memScore);
            Enqueue(_diskHistory,    diskScore);
            Enqueue(_gpuHistory,     gpuScore);
            Enqueue(_overallHistory, overall);
        }

        // ── Top consumers (by impact) ─────────────────────────────────────────
        var totals = ImpactScoreCalculator.ComputeTotals(processes);
        var top5 = processes
            .Where(p => p.Pid > 4)   // skip idle / system
            .Select(p => new ProcessImpact
            {
                Pid         = p.Pid,
                Name        = p.Name,
                ImpactScore = ImpactScoreCalculator.Calculate(p, totals),
                CpuPercent  = p.CpuPercent,
                MemoryBytes = p.WorkingSetBytes,
            })
            .OrderByDescending(x => x.ImpactScore)
            .Take(5)
            .ToList();

        // ── Active automation count ───────────────────────────────────────────
        var automations = 0;
        if (_settings.ProBalanceEnabled)   automations++;
        if (_settings.GamingModeEnabled)   automations++;
        if (_settings.Rules.Count > 0)     automations++;
        if (_settings.AlertRules.Count > 0) automations++;

        // ── Bottleneck analysis ───────────────────────────────────────────────
        var bottleneck = BottleneckDetector.Analyse(m, processes);

        // ── Build snapshot ─────────────────────────────────────────────────────
        var snapshot = new SystemHealthSnapshot
        {
            OverallHealth   = HealthScoring.ScoreToLevel(overall),
            OverallScore    = overall,
            OverallTrend    = HealthScoring.ComputeTrend(_overallHistory.ToList()),
            Cpu = new SubsystemHealth
            {
                Name         = "CPU",
                Score        = cpuScore,
                Level        = HealthScoring.ScoreToLevel(cpuScore),
                Trend        = HealthScoring.ComputeTrend(_cpuHistory.ToList()),
                CurrentValue = cpuVal,
                Summary      = $"{cpuVal:F0}% used",
            },
            Memory = new SubsystemHealth
            {
                Name         = "Memory",
                Score        = memScore,
                Level        = HealthScoring.ScoreToLevel(memScore),
                Trend        = HealthScoring.ComputeTrend(_memHistory.ToList()),
                CurrentValue = memVal,
                Summary      = $"{memVal:F0}% used ({FormatBytes(m.Memory.UsedBytes)} / {FormatBytes(m.Memory.TotalBytes)})",
            },
            Disk = new SubsystemHealth
            {
                Name         = "Disk",
                Score        = diskScore,
                Level        = HealthScoring.ScoreToLevel(diskScore),
                Trend        = HealthScoring.ComputeTrend(_diskHistory.ToList()),
                CurrentValue = diskUsed,
                Summary      = m.Disks.Count > 0 ? $"{diskVal:F0}% active · {diskUsed:F0}% full" : "No disks",
            },
            Gpu = new SubsystemHealth
            {
                Name         = "GPU",
                Score        = gpuScore,
                Level        = HealthScoring.ScoreToLevel(gpuScore),
                Trend        = HealthScoring.ComputeTrend(_gpuHistory.ToList()),
                CurrentValue = gpuVal,
                Summary      = m.Gpus.Count > 0 ? $"{gpuVal:F0}% used" : "No GPU data",
            },
            TopConsumers      = top5,
            ActiveAutomations = automations,
            Bottleneck        = bottleneck,
            Timestamp         = DateTime.UtcNow,
        };

        _subject.OnNext(snapshot);
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        if (queue.Count >= HistorySize) queue.Dequeue();
        queue.Enqueue(value);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    public void Dispose()
    {
        Stop();
        _subject.Dispose();
        _isStaleSubject.Dispose();
    }
}
