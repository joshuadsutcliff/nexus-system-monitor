using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Background daemon that dynamically lowers CPU-hogging background processes
/// to restore system responsiveness, then restores them when load drops.
/// Inspired by Process Lasso's Auto-Balance algorithm.
/// </summary>
public sealed class AutoBalanceService : IDisposable
{
    private readonly IProcessProvider _processProvider;
    private readonly IForegroundWindowProvider _foregroundWindow;
    private readonly AppSettings _settings; // live reference — reads current options each tick
    private readonly ILogger<AutoBalanceService> _logger;

    // pid → original priority (before we throttled them)
    private readonly Dictionary<int, ProcessPriority> _throttled = new();
    private readonly Subject<AutoBalanceEvent> _events = new();
    private IDisposable? _subscription;
    private bool _running;
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private Task? _restoreTask;

    public IObservable<AutoBalanceEvent> Events => _events.AsObservable();
    public bool IsRunning => _running;

    public AutoBalanceService(
        IProcessProvider processProvider,
        IForegroundWindowProvider foregroundWindow,
        AppSettings settings,
        ILogger<AutoBalanceService> logger)
    {
        _processProvider  = processProvider;
        _foregroundWindow = foregroundWindow;
        _settings         = settings;
        _logger           = logger;
    }

    /// <summary>Start the monitoring loop. Safe to call multiple times.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        // Subscribe to the shared multicast stream (FAST/1 s cadence) and sample at 500 ms.
        // This reuses the existing provider stream rather than creating an independent poll.
        _subscription = _processProvider
            .GetProcessStream(MonitoringCadence.Fast)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "AutoBalanceService process stream faulted; retrying with backoff"))
            .Sample(TimeSpan.FromMilliseconds(500))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "AutoBalance stream faulted");
                _running = false;
            });
        _events.OnNext(new AutoBalanceEvent(
            AutoBalanceEventType.Started, 0, string.Empty,
            ProcessPriority.Normal, ProcessPriority.Normal, DateTime.UtcNow));
    }

    /// <summary>Stop the monitoring loop and restore all throttled processes.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _subscription?.Dispose();
        _subscription = null;
        // Task.Run (rather than a bare fire-and-forget `_ = RestoreAllAsync()`) both avoids any
        // captured-context deadlock and gives Dispose() a task it can bound-wait on so an
        // in-flight restore isn't abandoned at shutdown (see Dispose below).
        _restoreTask = Task.Run(() => RestoreAllAsync());
        _events.OnNext(new AutoBalanceEvent(
            AutoBalanceEventType.Stopped, 0, string.Empty,
            ProcessPriority.Normal, ProcessPriority.Normal, DateTime.UtcNow));
    }

    private async void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        if (!await _tickLock.WaitAsync(0)) return;
        try
        {
        if (!_settings.AutoBalanceEnabled) return;

        double totalCpu = processes.Sum(p => p.CpuPercent);
        double threshold = _settings.AutoBalanceCpuThreshold;
        int fgPid = _foregroundWindow.GetForegroundProcessId();
        var exclusions = _settings.AutoBalanceExclusions;

        if (totalCpu >= threshold)
        {
            // Identify background hogs to throttle
            var candidates = processes
                .Where(p =>
                    p.CpuPercent > 5.0 &&
                    p.Pid != fgPid &&
                    p.Pid != Environment.ProcessId &&
                    p.Category != ProcessCategory.SystemKernel &&
                    !_throttled.ContainsKey(p.Pid) &&
                    !exclusions.Any(ex => p.Name.Equals(ex, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => p.CpuPercent)
                .Take(5);

            foreach (var proc in candidates)
            {
                // Read the process's REAL current priority before throttling it — never assume
                // Normal. Skip entirely when it can't be determined (process gone, access
                // denied, platform error) so we never fabricate a restore value.
                var original = await _processProvider.GetPriorityAsync(proc.Pid);
                if (original is null) continue;
                if (original <= ProcessPriority.BelowNormal) continue; // already low
                try
                {
                    await _processProvider.SetPriorityAsync(proc.Pid, ProcessPriority.BelowNormal);
                    _throttled[proc.Pid] = original.Value;
                    _events.OnNext(new AutoBalanceEvent(
                        AutoBalanceEventType.Throttled, proc.Pid, proc.Name,
                        original.Value, ProcessPriority.BelowNormal, DateTime.UtcNow));
                }
                catch (Exception ex) { _logger.LogDebug(ex, "AutoBalance: throttle {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
            }
        }
        else if (totalCpu < threshold * 0.7) // restore at 70% of threshold to avoid oscillation
        {
            // Restore all throttled processes
            var toRestore = _throttled.ToList();
            foreach (var (pid, original) in toRestore)
            {
                try
                {
                    await _processProvider.SetPriorityAsync(pid, original);
                    _throttled.Remove(pid);
                    var name = processes.FirstOrDefault(p => p.Pid == pid)?.Name ?? $"PID {pid}";
                    _events.OnNext(new AutoBalanceEvent(
                        AutoBalanceEventType.Restored, pid, name,
                        ProcessPriority.BelowNormal, original, DateTime.UtcNow));
                }
                catch (Exception ex) { _throttled.Remove(pid); _logger.LogDebug(ex, "AutoBalance: restore PID {Pid} failed", pid); }
            }
        }
        else
        {
            // Mid-zone: only restore processes that are no longer using CPU
            var inactive = _throttled.Keys
                .Where(pid => processes.All(p => p.Pid != pid || p.CpuPercent < 2.0))
                .ToList();
            foreach (var pid in inactive)
            {
                var original = _throttled[pid];
                try { await _processProvider.SetPriorityAsync(pid, original); }
                catch (Exception ex) { _logger.LogDebug(ex, "AutoBalance: mid-zone restore PID {Pid} failed", pid); }
                _throttled.Remove(pid);
                var name = processes.FirstOrDefault(p => p.Pid == pid)?.Name ?? $"PID {pid}";
                _events.OnNext(new AutoBalanceEvent(
                    AutoBalanceEventType.Restored, pid, name,
                    ProcessPriority.BelowNormal, original, DateTime.UtcNow));
            }
        }

        // Clean up entries for processes that have exited
        var alive = new HashSet<int>(processes.Select(p => p.Pid));
        foreach (var pid in _throttled.Keys.Where(k => !alive.Contains(k)).ToList())
            _throttled.Remove(pid);
        } // end try
        catch (Exception ex) { _logger.LogDebug(ex, "AutoBalance OnTick error"); }
        finally { _tickLock.Release(); }
    }

    private async Task RestoreAllAsync()
    {
        foreach (var (pid, original) in _throttled.ToList())
        {
            try { await _processProvider.SetPriorityAsync(pid, original); }
            catch (Exception ex) { _logger.LogDebug(ex, "AutoBalance: RestoreAll PID {Pid} failed", pid); }
        }
        _throttled.Clear();
    }

    public void Dispose()
    {
        Stop();
        // Bound-wait for the restore Stop() kicked off so we don't dispose _events (or let
        // teardown proceed) while a restore is still in flight — an abandoned restore would
        // permanently strand a throttled process at BelowNormal.
        try { _restoreTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _events.Dispose();
    }
}
