using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Boosts the foreground window's process to AboveNormal priority and restores
/// previous processes when focus moves away.
/// </summary>
public sealed class ForegroundBoostService : IDisposable
{
    private readonly IProcessProvider          _processProvider;
    private readonly IForegroundWindowProvider _foregroundWindow;
    private readonly AppSettings               _settings;
    private readonly ProcessActionLock         _actionLock;
    private readonly ILogger<ForegroundBoostService> _logger;

    // pid → priority before we boosted it
    private readonly Dictionary<int, ProcessPriority> _savedPriorities = new();
    private int _lastFgPid = -1;
    private IDisposable? _subscription;
    private bool _running;
    private Task? _restoreTask;

    private readonly SemaphoreSlim _tickLock = new(1, 1);

    private const string Owner = "ForegroundBoost";

    public ForegroundBoostService(
        IProcessProvider          processProvider,
        IForegroundWindowProvider foregroundWindow,
        AppSettings               settings,
        ProcessActionLock         actionLock,
        ILogger<ForegroundBoostService>? logger = null)
    {
        _processProvider  = processProvider;
        _foregroundWindow = foregroundWindow;
        _settings         = settings;
        _actionLock       = actionLock;
        _logger           = logger ?? NullLogger<ForegroundBoostService>.Instance;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _subscription = _processProvider
            .GetProcessStream(MonitoringCadence.Fast)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "ForegroundBoostService process stream faulted; retrying with backoff"))
            .Sample(TimeSpan.FromSeconds(1))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "ForegroundBoostService stream faulted");
                _running = false;
            });
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _subscription?.Dispose();
        _subscription = null;
        // Captured (not discarded) so Dispose() can bound-wait on it — an abandoned restore
        // here would permanently strand the boosted process at AboveNormal.
        _restoreTask = Task.Run(async () =>
        {
            await _tickLock.WaitAsync();
            try { await RestoreAllAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "ForegroundBoostService restore failed during stop"); }
            finally { _tickLock.Release(); }
        });
    }

    private async void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        if (!await _tickLock.WaitAsync(0)) return;
        try
        {
            if (!_settings.ForegroundBoostEnabled) return;

            var fgPid      = _foregroundWindow.GetForegroundProcessId();
            var exclusions = _settings.ForegroundBoostExclusions;
            var alive      = new HashSet<int>(processes.Select(p => p.Pid));

            // Clean dead PIDs
            foreach (var pid in _savedPriorities.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _savedPriorities.Remove(pid);
                _actionLock.Release(pid, Owner);
            }

            if (fgPid == _lastFgPid || fgPid <= 0) return;

            var prevFgPid = _lastFgPid;
            _lastFgPid = fgPid;

            // Restore previous foreground process
            if (prevFgPid > 0 && _savedPriorities.TryGetValue(prevFgPid, out var prevPriority))
            {
                try { await _processProvider.SetPriorityAsync(prevFgPid, prevPriority); } catch { }
                _savedPriorities.Remove(prevFgPid);
                _actionLock.Release(prevFgPid, Owner);
            }

            // Boost new foreground process
            var fgProc = processes.FirstOrDefault(p => p.Pid == fgPid);
            if (fgProc is null) return;
            if (fgProc.Category == ProcessCategory.SystemKernel) return;
            if (exclusions.Any(ex => fgProc.Name.Equals(ex, StringComparison.OrdinalIgnoreCase))) return;
            if (fgProc.Pid == Environment.ProcessId) return;

            if (!_actionLock.TryLock(fgPid, Owner)) return;

            // Read the process's REAL current priority before boosting it — never assume Normal.
            // Skip entirely if it can't be determined, and never downgrade a foreground process
            // that's already at or above AboveNormal.
            var actual = await _processProvider.GetPriorityAsync(fgPid);
            if (actual is null || actual >= ProcessPriority.AboveNormal)
            {
                _actionLock.Release(fgPid, Owner);
                return;
            }

            _savedPriorities[fgPid] = actual.Value;
            try { await _processProvider.SetPriorityAsync(fgPid, ProcessPriority.AboveNormal); }
            catch
            {
                _savedPriorities.Remove(fgPid);
                _actionLock.Release(fgPid, Owner);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "ForegroundBoostService OnTick error"); }
        finally { _tickLock.Release(); }
    }

    private async Task RestoreAllAsync()
    {
        foreach (var (pid, priority) in _savedPriorities.ToList())
        {
            try { await _processProvider.SetPriorityAsync(pid, priority); } catch { }
            _actionLock.Release(pid, Owner);
        }
        _savedPriorities.Clear();
    }

    public void Dispose()
    {
        Stop();
        // Bound-wait for the restore Stop() kicked off so teardown doesn't proceed while a
        // restore is still in flight.
        try { _restoreTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}
