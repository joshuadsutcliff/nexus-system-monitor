using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Throttles individual background processes that have been idle (low CPU) for
/// a sustained period. Unlike Auto-Balance (which reacts to system-wide load),
/// Idle Throttle targets individual idleness regardless of overall load.
/// </summary>
public sealed class IdleThrottleService : IDisposable
{
    private readonly IProcessProvider          _processProvider;
    private readonly IForegroundWindowProvider _foregroundWindow;
    private readonly AppSettings               _settings;
    private readonly ProcessActionLock         _actionLock;
    private readonly ILogger<IdleThrottleService> _logger;

    // pid → consecutive idle tick count
    private readonly Dictionary<int, int>             _idleTicks       = new();
    // pid → priority before we throttled it
    private readonly Dictionary<int, ProcessPriority> _savedPriorities = new();
    // pid → whether we also enabled efficiency mode
    private readonly HashSet<int>                     _efficiencyPids  = new();

    private IDisposable? _subscription;
    private bool _running;

    private readonly SemaphoreSlim _tickLock = new(1, 1);

    private const string Owner = "IdleThrottle";

    public bool IsRunning => _running;

    public IdleThrottleService(
        IProcessProvider          processProvider,
        IForegroundWindowProvider foregroundWindow,
        AppSettings               settings,
        ProcessActionLock         actionLock,
        ILogger<IdleThrottleService>? logger = null)
    {
        _processProvider  = processProvider;
        _foregroundWindow = foregroundWindow;
        _settings         = settings;
        _actionLock       = actionLock;
        _logger           = logger ?? NullLogger<IdleThrottleService>.Instance;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _subscription = _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "IdleThrottleService process stream faulted; retrying with backoff"))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "IdleThrottleService stream faulted");
                _running = false;
            });
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _subscription?.Dispose();
        _subscription = null;
        _ = Task.Run(async () =>
        {
            await _tickLock.WaitAsync();
            try { await RestoreAllAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "IdleThrottleService restore failed during stop"); }
            finally { _tickLock.Release(); }
        });
    }

    private async void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        if (!await _tickLock.WaitAsync(0)) return;
        try
        {
            if (!_settings.IdleThrottleEnabled) return;

            var fgPid      = _foregroundWindow.GetForegroundProcessId();
            var exclusions = _settings.IdleThrottleExclusions;
            var threshold  = _settings.IdleThrottleCpuThreshold;
            var ticksReq   = _settings.IdleThrottleIdleTicksRequired;
            var useEco     = _settings.IdleThrottleUseEfficiencyMode;
            var alive      = new HashSet<int>(processes.Select(p => p.Pid));

            // Evict dead PIDs
            foreach (var pid in _idleTicks.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _idleTicks.Remove(pid);
                _savedPriorities.Remove(pid);
                _efficiencyPids.Remove(pid);
                _actionLock.Release(pid, Owner);
            }

            foreach (var proc in processes)
            {
                if (proc.Pid == fgPid) continue;
                if (proc.Pid == Environment.ProcessId) continue;
                if (proc.Category == ProcessCategory.SystemKernel) continue;
                if (exclusions.Any(ex => proc.Name.Equals(ex, StringComparison.OrdinalIgnoreCase))) continue;

                bool isThrottled = _savedPriorities.ContainsKey(proc.Pid);

                if (isThrottled)
                {
                    // Check if process became active again (high CPU or became foreground)
                    bool restored = proc.CpuPercent > threshold * 2.0;
                    if (restored)
                        await RestorePidAsync(proc.Pid);
                    else
                        _idleTicks[proc.Pid] = 0; // stay throttled, reset counter
                }
                else
                {
                    // Track idle ticks
                    if (proc.CpuPercent < threshold)
                    {
                        _idleTicks.TryGetValue(proc.Pid, out var ticks);
                        ticks++;
                        _idleTicks[proc.Pid] = ticks;

                        if (ticks >= ticksReq && !_actionLock.IsLocked(proc.Pid))
                        {
                            if (_actionLock.TryLock(proc.Pid, Owner))
                            {
                                // NOTE: ProcessInfo.BasePriority is an int, not a ProcessPriority enum.
                                // We restore to Normal since we can't read the actual priority class here.
                                // Processes set to High/AboveNormal will be downgraded on restore.
                                // TODO: read actual priority class before throttling.
                                _savedPriorities[proc.Pid] = ProcessPriority.Normal;
                                try
                                {
                                    await _processProvider.SetPriorityAsync(proc.Pid, ProcessPriority.BelowNormal);
                                    if (useEco)
                                    {
                                        await _processProvider.SetEfficiencyModeAsync(proc.Pid, true);
                                        _efficiencyPids.Add(proc.Pid);
                                    }
                                }
                                catch
                                {
                                    _savedPriorities.Remove(proc.Pid);
                                    _efficiencyPids.Remove(proc.Pid);
                                    _actionLock.Release(proc.Pid, Owner);
                                }
                            }
                        }
                    }
                    else
                    {
                        _idleTicks.Remove(proc.Pid);
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "IdleThrottleService OnTick error"); }
        finally { _tickLock.Release(); }
    }

    private async Task RestorePidAsync(int pid)
    {
        if (_savedPriorities.TryGetValue(pid, out var priority))
        {
            try { await _processProvider.SetPriorityAsync(pid, priority); } catch { }
            _savedPriorities.Remove(pid);
        }
        if (_efficiencyPids.Remove(pid))
        {
            try { await _processProvider.SetEfficiencyModeAsync(pid, false); } catch { }
        }
        _idleTicks.Remove(pid);
        _actionLock.Release(pid, Owner);
    }

    private async Task RestoreAllAsync()
    {
        foreach (var pid in _savedPriorities.Keys.ToList())
            await RestorePidAsync(pid);
    }

    public void Dispose() => Stop();
}
