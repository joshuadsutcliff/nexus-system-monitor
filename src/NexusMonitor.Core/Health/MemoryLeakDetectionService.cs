using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Health;

public sealed class MemoryLeakDetectionService : IDisposable
{
    private readonly IProcessProvider _processProvider;
    private readonly AppSettings _settings;
    private readonly ILogger<MemoryLeakDetectionService> _logger;

    private readonly BehaviorSubject<IReadOnlyList<MemoryLeakSuspect>> _suspects =
        new(Array.Empty<MemoryLeakSuspect>());

    private readonly Dictionary<int, ProcessTracker> _trackers = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new();
    private readonly HashSet<string> _dismissedProcessNames = new(StringComparer.OrdinalIgnoreCase);

    private IDisposable? _subscription;
    private int _tickCount;
    private bool _disposed;

    // Tied to MonitoringCadence.Normal so this service's tick-rate arithmetic can't drift
    // from the actual cadence requested on the shared provider stream below.
    private static readonly int TickIntervalSeconds = (int)MonitoringCadence.Normal.TotalSeconds;
    private const int AnalysisEveryNTicks = 10;
    private const double MinFillFraction = 0.5;
    private const double MinRSquared = 0.7;
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);

    public IObservable<IReadOnlyList<MemoryLeakSuspect>> Suspects => _suspects.AsObservable();
    public IReadOnlyList<MemoryLeakSuspect> CurrentSuspects => _suspects.Value;

    public MemoryLeakDetectionService(
        IProcessProvider processProvider,
        AppSettings settings,
        ILogger<MemoryLeakDetectionService> logger)
    {
        _processProvider = processProvider;
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        if (_disposed) return;
        if (_subscription is not null) return;

        _logger.LogInformation("MemoryLeakDetectionService starting");

        int bufferSize = Math.Clamp(
            _settings.LeakObservationWindowMinutes * 60 / TickIntervalSeconds,
            30, 120);

        _subscription = _processProvider.GetProcessStream(MonitoringCadence.Normal)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "MemoryLeakDetectionService process stream faulted; retrying with backoff"))
            .Subscribe(processes => OnTick(processes, bufferSize),
                ex => _logger.LogError(ex, "Process stream faulted — memory leak detection stopped"));
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        lock (_trackers)
        {
            _trackers.Clear();
            if (!_disposed)
                _suspects.OnNext(Array.Empty<MemoryLeakSuspect>());
        }
        _logger.LogInformation("MemoryLeakDetectionService stopped");
    }

    public void Dismiss(string processName)
    {
        lock (_trackers)
        {
            _dismissedProcessNames.Add(processName);
            var updated = _suspects.Value
                .Where(s => !string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _suspects.OnNext(updated);
        }
    }

    private void OnTick(IReadOnlyList<ProcessInfo> processes, int bufferSize)
    {
        try
        {
        lock (_trackers)
        {
            var activePids = new HashSet<int>(processes.Select(p => p.Pid));

            // Evict dead PIDs
            var deadPids = _trackers.Keys.Where(pid => !activePids.Contains(pid)).ToList();
            foreach (var pid in deadPids)
                _trackers.Remove(pid);

            // Update trackers
            foreach (var proc in processes)
            {
                if (_trackers.TryGetValue(proc.Pid, out var tracker)
                    && !string.Equals(tracker.ProcessName, proc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // PID was reused by a different process — replace tracker
                    _trackers.Remove(proc.Pid);
                    tracker = null;
                }

                if (tracker is null)
                {
                    tracker = new ProcessTracker(proc.Name, bufferSize);
                    _trackers[proc.Pid] = tracker;
                }
                tracker.Push(proc.WorkingSetBytes, proc.HandleCount);
            }

            _tickCount++;
            if (_tickCount % AnalysisEveryNTicks != 0) return;

            // Clean up expired cooldowns
            var now = DateTimeOffset.UtcNow;
            var expiredCooldowns = _cooldowns
                .Where(kv => now - kv.Value > CooldownDuration)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var name in expiredCooldowns)
                _cooldowns.Remove(name);

            double leakThresholdBytesPerHour = _settings.LeakRateThresholdMbPerHour * 1024 * 1024;
            double handleThreshold = _settings.HandleLeakThresholdPerHour;

            var newSuspects = new List<MemoryLeakSuspect>();

            foreach (var (pid, tracker) in _trackers)
            {
                if (_dismissedProcessNames.Contains(tracker.ProcessName)) continue;
                if (_cooldowns.ContainsKey(tracker.ProcessName)) continue;

                int filled = tracker.FilledCount;
                if (filled < bufferSize * MinFillFraction) continue;

                var wsHistory = tracker.GetWorkingSetHistory();
                var hHistory  = tracker.GetHandleHistory();

                var (wsSlope, wsR2)  = LinearRegression.Fit(wsHistory);
                var (hSlope,  hR2)   = LinearRegression.Fit(hHistory);

                double wsSlopePerHour = wsSlope * (3600.0 / TickIntervalSeconds);
                double hSlopePerHour  = hSlope  * (3600.0 / TickIntervalSeconds);

                bool memLeak    = wsSlopePerHour > leakThresholdBytesPerHour && wsR2 > MinRSquared;
                bool handleLeak = hSlopePerHour  > handleThreshold            && hR2  > MinRSquared;

                if (!memLeak && !handleLeak) continue;

                double confidence = Math.Max(memLeak ? wsR2 : 0, handleLeak ? hR2 : 0);

                _cooldowns[tracker.ProcessName] = now;

                var suspect = new MemoryLeakSuspect
                {
                    Pid                      = pid,
                    ProcessName              = tracker.ProcessName,
                    LeakRateBytesPerHour     = wsSlopePerHour,
                    HandleLeakRatePerHour    = hSlopePerHour,
                    Confidence               = confidence,
                    ObservationWindowSeconds = filled * TickIntervalSeconds,
                    FirstDetected            = tracker.FirstSeen,
                    WorkingSetHistory        = wsHistory.ToArray(),
                    HandleHistory            = hHistory.ToArray(),
                };

                newSuspects.Add(suspect);
                _logger.LogWarning(
                    "Memory leak suspect: {Name} (PID {Pid}), +{Rate:F0} MB/hr, R²={R2:F2}",
                    tracker.ProcessName, pid, wsSlopePerHour / 1024 / 1024, confidence);
            }

            // Merge with existing suspects (keep existing if not re-detected, add/update new ones)
            var merged = _suspects.Value
                .Where(s => activePids.Contains(s.Pid) && !newSuspects.Any(n => n.Pid == s.Pid))
                .Concat(newSuspects)
                .OrderByDescending(s => s.Confidence)
                .ToList();

            _suspects.OnNext(merged);
        }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnTick error");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _suspects.Dispose();
    }

    private sealed class ProcessTracker
    {
        public string ProcessName { get; }
        public DateTimeOffset FirstSeen { get; } = DateTimeOffset.UtcNow;
        public int FilledCount { get; private set; }

        private readonly double[] _workingSet;
        private readonly double[] _handles;
        private readonly int _capacity;
        private int _head;

        public ProcessTracker(string name, int capacity)
        {
            ProcessName  = name;
            _capacity    = capacity;
            _workingSet  = new double[capacity];
            _handles     = new double[capacity];
        }

        public void Push(long workingSetBytes, int handleCount)
        {
            _workingSet[_head] = workingSetBytes;
            _handles[_head]    = handleCount;
            _head = (_head + 1) % _capacity;
            if (FilledCount < _capacity) FilledCount++;
        }

        public ReadOnlySpan<double> GetWorkingSetHistory() => GetOrdered(_workingSet);
        public ReadOnlySpan<double> GetHandleHistory()     => GetOrdered(_handles);

        private ReadOnlySpan<double> GetOrdered(double[] buf)
        {
            if (FilledCount < _capacity)
                return new ReadOnlySpan<double>(buf, 0, FilledCount);

            // Ring buffer: oldest is at _head
            var ordered = new double[_capacity];
            int tail = _head;
            for (int i = 0; i < _capacity; i++)
                ordered[i] = buf[(tail + i) % _capacity];
            return ordered;
        }
    }
}
