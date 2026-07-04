using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Temporarily reduces a process's CPU affinity when it exceeds a configured
/// CPU threshold for a sustained period, then restores it after a duration.
/// </summary>
public sealed class CpuLimiterService : IDisposable
{
    private readonly IProcessProvider  _processProvider;
    private readonly AppSettings       _settings;
    private readonly ProcessActionLock _actionLock;
    private readonly ILogger<CpuLimiterService> _logger;

    private sealed class LimiterState
    {
        public int       OverThresholdTicks;     // consecutive ticks over threshold
        public long      OriginalAffinity;       // mask before we reduced it
        public DateTime? LimitEndTime;           // when to restore (null = not limited)
    }

    private readonly Dictionary<int, LimiterState> _state = new();
    private IDisposable? _subscription;
    private bool _running;

    private readonly SemaphoreSlim _tickLock = new(1, 1);

    private const string Owner = "CpuLimiter";

    public CpuLimiterService(
        IProcessProvider  processProvider,
        AppSettings       settings,
        ProcessActionLock actionLock,
        ILogger<CpuLimiterService>? logger = null)
    {
        _processProvider = processProvider;
        _settings        = settings;
        _actionLock      = actionLock;
        _logger          = logger ?? NullLogger<CpuLimiterService>.Instance;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _subscription = _processProvider
            .GetProcessStream(MonitoringCadence.Normal)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "CpuLimiterService process stream faulted; retrying with backoff"))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "CpuLimiterService stream faulted");
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
            catch (Exception ex) { _logger.LogError(ex, "CpuLimiterService restore failed during stop"); }
            finally { _tickLock.Release(); }
        });
    }

    private async void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        if (!await _tickLock.WaitAsync(0)) return;
        try
        {
            if (!_settings.CpuLimiterEnabled) return;

            var rules = _settings.CpuLimiterRules?.Where(r => r.IsEnabled).ToList();
            if (rules is null || rules.Count == 0) return;

            var alive = new HashSet<int>(processes.Select(p => p.Pid));

            // Evict dead PIDs
            foreach (var pid in _state.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _actionLock.Release(pid, Owner);
                _state.Remove(pid);
            }

            var now = DateTime.UtcNow;

            foreach (var proc in processes)
            {
                var matchingRule = rules.FirstOrDefault(r =>
                    !string.IsNullOrEmpty(r.ProcessNamePattern) &&
                    proc.Name.Contains(r.ProcessNamePattern.TrimEnd('*').TrimStart('*'),
                        StringComparison.OrdinalIgnoreCase));
                if (matchingRule is null) continue;

                if (!_state.TryGetValue(proc.Pid, out var st))
                {
                    st = new LimiterState();
                    _state[proc.Pid] = st;
                }

                // If currently limited, check if duration expired
                if (st.LimitEndTime.HasValue)
                {
                    if (now >= st.LimitEndTime.Value || proc.CpuPercent < matchingRule.CpuThresholdPercent)
                    {
                        await RestorePidAsync(proc.Pid, st);
                    }
                    continue;
                }

                // Track consecutive ticks over threshold (each tick = ~2s)
                if (proc.CpuPercent > matchingRule.CpuThresholdPercent)
                {
                    st.OverThresholdTicks++;
                    int secondsOver = st.OverThresholdTicks * 2;
                    if (secondsOver >= matchingRule.OverLimitSeconds && !_actionLock.IsLocked(proc.Pid))
                    {
                        await ApplyLimitAsync(proc.Pid, matchingRule, st);
                    }
                }
                else
                {
                    st.OverThresholdTicks = 0;
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "CpuLimiterService OnTick error"); }
        finally { _tickLock.Release(); }
    }

    private async Task ApplyLimitAsync(int pid, CpuLimiterRule rule, LimiterState st)
    {
        if (!_actionLock.TryLock(pid, Owner)) return;
        try
        {
            var (procMask, sysMask) = await _processProvider.GetAffinityMasksAsync(pid);
            st.OriginalAffinity = procMask;

            // Count set bits and remove ReduceByCores from the high end
            int coreCount = CountBits(procMask);
            int newCount  = Math.Max(1, coreCount - rule.ReduceByCores);
            long newMask  = BuildMaskWithNCores(newCount, sysMask);

            await _processProvider.SetAffinityAsync(pid, newMask);
            st.LimitEndTime       = DateTime.UtcNow.AddSeconds(rule.LimitDurationSeconds);
            st.OverThresholdTicks = 0;
        }
        catch
        {
            _actionLock.Release(pid, Owner);
        }
    }

    private async Task RestorePidAsync(int pid, LimiterState st)
    {
        try { await _processProvider.SetAffinityAsync(pid, st.OriginalAffinity); } catch { }
        st.LimitEndTime       = null;
        st.OriginalAffinity   = 0;
        st.OverThresholdTicks = 0;
        _actionLock.Release(pid, Owner);
    }

    private async Task RestoreAllAsync()
    {
        foreach (var (pid, st) in _state.ToList())
        {
            if (st.LimitEndTime.HasValue)
                await RestorePidAsync(pid, st);
        }
        _state.Clear();
    }

    private static int CountBits(long mask)
    {
        int count = 0;
        while (mask != 0) { count += (int)(mask & 1); mask >>= 1; }
        return count;
    }

    private static long BuildMaskWithNCores(int n, long sysMask)
    {
        long result = 0;
        int  added  = 0;
        for (int i = 0; i < 64 && added < n; i++)
        {
            if ((sysMask & (1L << i)) != 0)
            {
                result |= (1L << i);
                added++;
            }
        }
        return result == 0 ? 1 : result;
    }

    public void Dispose() => Stop();
}
