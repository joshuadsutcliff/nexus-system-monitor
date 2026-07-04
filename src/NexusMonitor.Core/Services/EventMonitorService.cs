using System.Reactive.Subjects;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Storage;

namespace NexusMonitor.Core.Services;

/// <summary>
/// Monitors live metric streams and classifies resource incidents as they occur.
///
/// Unlike <see cref="AnomalyDetectionService"/> (which fires statistical anomaly events
/// per data point), EventMonitorService tracks the full lifecycle of a resource incident
/// — start, peak, resolution — and produces a classified <see cref="ResourceEvent"/>
/// when the incident ends.
///
/// Classification distinguishes:
///   <list type="bullet">
///     <item><see cref="EventClassification.ApplicationSpike"/> — transient spike by one process (e.g., game startup)</item>
///     <item><see cref="EventClassification.HardwareBottleneck"/> — sustained system-wide saturation</item>
///     <item><see cref="EventClassification.ApplicationLeak"/> — single process memory growing monotonically</item>
///     <item><see cref="EventClassification.ThermalThrottle"/> — temperature-driven performance reduction</item>
///   </list>
/// </summary>
public sealed class EventMonitorService : IDisposable
{
    // ── Incident thresholds ────────────────────────────────────────────────────
    private const double CpuThreshold   = 90.0;
    private const double RamThreshold   = 90.0;
    private const double GpuThreshold   = 92.0;
    private const double VramThreshold  = 92.0;
    private const double DiskThreshold  = 92.0;

    // ── Incident lifecycle ─────────────────────────────────────────────────────
    // Must drop below (threshold − HysteresisDown) for TailTicksClose consecutive ticks to end incident.
    private const double HysteresisDown  = 5.0;
    private const int    TailTicksClose  = 3;
    // Force-close an incident after this many seconds regardless (avoids hung incidents).
    private const double MaxDurationSec  = 600.0;

    // ── Classification boundaries ─────────────────────────────────────────────
    // < TransientSec → ApplicationSpike candidate; ≥ → HardwareBottleneck candidate.
    private const double TransientSec        = 60.0;
    // A process is "dominant" if it appears as top consumer in ≥ DominanceThreshold of ticks.
    private const double DominanceThreshold  = 0.50;
    // Leak: last-half avg memory > first-half avg × LeakGrowthRatio.
    private const double LeakGrowthRatio     = 1.20;

    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly IResourceEventWriter   _writer;
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IProcessProvider       _processProvider;

    // ── Subscriptions ─────────────────────────────────────────────────────────
    private IDisposable? _metricsSub;
    private IDisposable? _processSub;
    private bool _disposed;

    // ── Latest process snapshot (written by process sub, read by metrics sub) ─
    private volatile IReadOnlyList<ProcessInfo> _latestProcesses = [];

    // ── Per-resource active incident ───────────────────────────────────────────
    private sealed class Incident
    {
        public DateTime StartTime        { get; init; }
        public double   PeakPct          { get; set; }
        public double   SumPct           { get; set; }
        public int      SampleCount      { get; set; }
        public int      TailCount        { get; set; }
        public bool     IsThermal        { get; set; }

        // Process attribution: track which process appears most as the top consumer.
        public string TopProcess      { get; set; } = string.Empty;
        public int    TopProcessPid   { get; set; }
        public int    TopProcessTicks { get; set; }

        // Memory growth tracking (RAM incidents only): samples of top-memory-process WorkingSet (MB).
        public readonly List<double> MemorySamples          = new();
        public          string       MemoryTrackedProcess   { get; set; } = string.Empty;
    }

    private readonly Dictionary<ResourceType, Incident?> _incidents = new()
    {
        [ResourceType.Cpu]  = null,
        [ResourceType.Ram]  = null,
        [ResourceType.Gpu]  = null,
        [ResourceType.Vram] = null,
        [ResourceType.Disk] = null,
    };

    // ── Observable output ──────────────────────────────────────────────────────
    private readonly Subject<ResourceEvent> _eventFired = new();
    public IObservable<ResourceEvent> EventFired => _eventFired;

    public EventMonitorService(
        IResourceEventWriter   writer,
        ISystemMetricsProvider metricsProvider,
        IProcessProvider       processProvider)
    {
        _writer          = writer;
        _metricsProvider = metricsProvider;
        _processProvider = processProvider;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public void Start()
    {
        // No logger is injected into this service; the resilience wrapper still shields the
        // subscription from a permanent provider fault by resubscribing with backoff.
        _processSub = _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
            .Subscribe(procs => _latestProcesses = procs, _ => { });

        _metricsSub = _metricsProvider
            .GetMetricsStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
            .Subscribe(OnMetricsTick, _ => { });
    }

    public void Stop()
    {
        _metricsSub?.Dispose();
        _processSub?.Dispose();
        _metricsSub = _processSub = null;
    }

    // ── Metrics processing ────────────────────────────────────────────────────

    private void OnMetricsTick(SystemMetrics m)
    {
        var now = DateTime.UtcNow;

        double cpu  = m.Cpu.TotalPercent;
        double ram  = m.Memory.TotalBytes > 0
            ? m.Memory.UsedBytes * 100.0 / m.Memory.TotalBytes : 0;
        double gpu  = m.Gpus.Count > 0 ? m.Gpus[0].UsagePercent       : -1;
        double vram = m.Gpus.Count > 0 ? m.Gpus[0].MemoryUsedPercent  : -1;
        double disk = m.Disks.Count > 0 ? m.Disks.Max(d => d.ActivePercent) : -1;

        double cpuTemp = m.Cpu.TemperatureCelsius;
        double gpuTemp = m.Gpus.Count > 0 ? m.Gpus[0].TemperatureCelsius : 0;
        double cpuFreq = m.Cpu.FrequencyMhz;
        double cpuBase = m.Cpu.BaseSpeedMhz;
        bool isThermal = cpuTemp > 95 || gpuTemp > 90
            || (cpuBase > 0 && cpuFreq > 0 && cpu > 60 && cpuFreq / cpuBase < 0.90);

        // Snapshot current process list (volatile read — stale by up to one tick is fine)
        var procs = _latestProcesses;

        var topCpuProc  = procs.Count > 0 ? procs.OrderByDescending(p => p.CpuPercent).First()       : null;
        var topMemProc  = procs.Count > 0 ? procs.OrderByDescending(p => p.WorkingSetBytes).First()  : null;
        var topGpuProc  = procs.Count > 0 ? procs.OrderByDescending(p => p.GpuPercent).First()       : null;

        Tick(ResourceType.Cpu,  cpu,  CpuThreshold,  topCpuProc, isThermal, topMemProc, now);
        Tick(ResourceType.Ram,  ram,  RamThreshold,  topMemProc, isThermal, topMemProc, now);
        if (gpu  >= 0) Tick(ResourceType.Gpu,  gpu,  GpuThreshold,  topGpuProc, isThermal, null, now);
        if (vram >= 0) Tick(ResourceType.Vram, vram, VramThreshold, topGpuProc, isThermal, null, now);
        if (disk >= 0) Tick(ResourceType.Disk, disk, DiskThreshold, topCpuProc, isThermal, null, now);
    }

    private void Tick(
        ResourceType resource,
        double       value,
        double       threshold,
        ProcessInfo? topProcess,
        bool         isThermal,
        ProcessInfo? memoryProcess,
        DateTime     now)
    {
        bool above      = value >= threshold;
        bool aboveClose = value >= (threshold - HysteresisDown);

        var incident = _incidents[resource];

        // Start a new incident when threshold is first crossed
        if (incident == null && above)
        {
            incident = new Incident { StartTime = now };
            _incidents[resource] = incident;
        }

        if (incident == null) return;

        // Accumulate stats
        incident.PeakPct      = Math.Max(incident.PeakPct, value);
        incident.SumPct      += value;
        incident.SampleCount++;
        incident.IsThermal   |= isThermal;

        // Process attribution: keep a running "leader" — whichever process appears most often
        if (topProcess != null)
        {
            if (string.IsNullOrEmpty(incident.TopProcess))
            {
                incident.TopProcess    = topProcess.Name;
                incident.TopProcessPid = topProcess.Pid;
                incident.TopProcessTicks = 1;
            }
            else if (topProcess.Name == incident.TopProcess)
            {
                incident.TopProcessTicks++;
            }
            // If a different process appears, keep the current leader (simplified majority vote)
        }

        // RAM-specific: track top-memory-process working set for leak detection
        if (resource == ResourceType.Ram && memoryProcess != null)
        {
            if (string.IsNullOrEmpty(incident.MemoryTrackedProcess))
                incident.MemoryTrackedProcess = memoryProcess.Name;

            if (memoryProcess.Name == incident.MemoryTrackedProcess)
                incident.MemorySamples.Add(memoryProcess.WorkingSetBytes / 1_048_576.0);
        }

        // Tail-close: drop below hysteresis for N consecutive ticks → end incident
        if (!aboveClose)
        {
            incident.TailCount++;
            if (incident.TailCount >= TailTicksClose)
            {
                _ = FinalizeAsync(resource, incident, now);
                _incidents[resource] = null;
            }
        }
        else
        {
            incident.TailCount = 0;
        }

        // Force-close if incident has run too long
        if (_incidents[resource] != null &&
            (now - incident.StartTime).TotalSeconds > MaxDurationSec)
        {
            _ = FinalizeAsync(resource, incident, now);
            _incidents[resource] = null;
        }
    }

    // ── Finalization & classification ──────────────────────────────────────────

    private async Task FinalizeAsync(ResourceType resource, Incident incident, DateTime endTime)
    {
        var duration  = endTime - incident.StartTime;
        double avgPct = incident.SampleCount > 0
            ? incident.SumPct / incident.SampleCount : 0;
        double dominance = incident.SampleCount > 0
            ? (double)incident.TopProcessTicks / incident.SampleCount : 0;

        var classification = Classify(resource, incident, duration, dominance);

        int severity = incident.PeakPct >= 98 ? EventSeverity.Critical
                     : incident.PeakPct >= 90 ? EventSeverity.Warning
                     : EventSeverity.Info;

        var summary = BuildSummary(resource, classification, incident, duration);

        var evt = new ResourceEvent
        {
            Timestamp           = incident.StartTime,
            EndTimestamp        = endTime,
            Resource            = resource,
            PeakUsagePercent    = Math.Round(incident.PeakPct, 1),
            AverageUsagePercent = Math.Round(avgPct, 1),
            Duration            = duration,
            PrimaryProcess      = incident.TopProcess,
            PrimaryProcessPid   = incident.TopProcessPid,
            Classification      = classification,
            Severity            = severity,
            Summary             = summary,
        };

        try
        {
            await _writer.InsertResourceEventAsync(evt);
            _eventFired.OnNext(evt);
        }
        catch { /* never crash the monitoring loop */ }
    }

    private static EventClassification Classify(
        ResourceType resource,
        Incident     incident,
        TimeSpan     duration,
        double       dominance)
    {
        if (incident.IsThermal)
            return EventClassification.ThermalThrottle;

        bool transient  = duration.TotalSeconds < TransientSec;
        bool dominant   = dominance >= DominanceThreshold;

        if (transient && dominant)
            return EventClassification.ApplicationSpike;

        if (!transient && dominant && resource == ResourceType.Ram &&
            IsLikelyLeak(incident.MemorySamples))
            return EventClassification.ApplicationLeak;

        if (!transient && !dominant)
            return EventClassification.HardwareBottleneck;

        if (!transient && dominant)
            return EventClassification.ApplicationSpike;  // sustained single-app spike

        return EventClassification.Unknown;
    }

    private static bool IsLikelyLeak(List<double> samples)
    {
        if (samples.Count < 4) return false;
        int half         = samples.Count / 2;
        double firstHalf = samples.Take(half).Average();
        double lastHalf  = samples.Skip(half).Average();
        return lastHalf > firstHalf * LeakGrowthRatio;
    }

    private static string BuildSummary(
        ResourceType       resource,
        EventClassification classification,
        Incident           incident,
        TimeSpan           duration)
    {
        string res = resource switch
        {
            ResourceType.Cpu  => "CPU",
            ResourceType.Ram  => "RAM",
            ResourceType.Gpu  => "GPU",
            ResourceType.Vram => "VRAM",
            ResourceType.Disk => "Disk",
            _                 => resource.ToString(),
        };

        string dur = duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:F0}s"
            : $"{duration.TotalMinutes:F1}m";

        string proc = string.IsNullOrEmpty(incident.TopProcess) ? "unknown process" : incident.TopProcess;

        return classification switch
        {
            EventClassification.HardwareBottleneck =>
                $"{res} sustained at {incident.PeakPct:F0}% peak for {dur} — hardware bottleneck",
            EventClassification.ApplicationSpike =>
                $"{res} spike to {incident.PeakPct:F0}% for {dur} caused by {proc}",
            EventClassification.ApplicationLeak =>
                $"Memory leak suspected in {proc} — RAM grew over {dur}",
            EventClassification.ThermalThrottle =>
                $"{res} thermal event — {incident.PeakPct:F0}% peak with thermal throttling active",
            _ =>
                $"{res} incident: {incident.PeakPct:F0}% peak over {dur}",
        };
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _eventFired.Dispose();
    }
}
