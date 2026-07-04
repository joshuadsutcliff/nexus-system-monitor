using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Storage;

namespace NexusMonitor.Core.Rules;

/// <summary>
/// Background service that applies persistent ProcessRules to newly-launched
/// processes and evaluates watchdog conditions on every process snapshot.
/// Also applies ProcessPreference settings to new processes (preferences are
/// lower priority than explicit rules — rules win when both match).
/// </summary>
public sealed class RulesEngine : IDisposable
{
    private readonly IProcessProvider        _processProvider;
    private readonly AppSettings             _settings;
    private readonly ProcessPreferenceStore? _preferenceStore;
    private readonly ProcessGroupStore?      _groupStore;
    private readonly ILogger<RulesEngine>    _logger;
    private readonly HashSet<int> _seenPids = new();
    // watchdog tracking: (pid, ruleId) → first-seen-over-threshold time
    private readonly Dictionary<(int pid, Guid ruleId), DateTime> _conditionFirstSeen = new();
    private IDisposable? _subscription;
    private bool _running;
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    // Cached enabled-rules list — rebuilt only when _settings.Rules reference changes
    private List<ProcessRule>? _cachedRules;
    private IReadOnlyList<ProcessRule>? _cachedRulesSource;

    // KeepRunning state: normalized exe name → state record
    private sealed class KeepRunningState
    {
        public string?   ImagePath    { get; set; }
        public string?   CommandLine  { get; set; }
        public DateTime  LastExitTime { get; set; }
        public int       RestartCount { get; set; }
        public DateTime  WindowStart  { get; set; } = DateTime.UtcNow;
    }
    private readonly Dictionary<string, KeepRunningState> _keepRunningState = new();

    public RulesEngine(IProcessProvider processProvider, AppSettings settings,
        ILogger<RulesEngine> logger,
        ProcessPreferenceStore? preferenceStore = null,
        ProcessGroupStore? groupStore = null)
    {
        _processProvider  = processProvider;
        _settings         = settings;
        _logger           = logger;
        _preferenceStore  = preferenceStore;
        _groupStore       = groupStore;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _subscription = _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "RulesEngine process stream faulted; retrying with backoff"))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "RulesEngine stream faulted");
                _running = false;
            });
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _subscription?.Dispose();
        _subscription = null;
        // Clear _seenPids while holding _tickLock so an in-flight async tick cannot race on it.
        Task.Run(async () =>
        {
            await _tickLock.WaitAsync();
            try { _seenPids.Clear(); }
            finally { _tickLock.Release(); }
        });
    }

    private async void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        if (!await _tickLock.WaitAsync(0)) return;
        try
        {
        // Rebuild cached enabled-rules list only when the source collection reference changes.
        var srcRules = _settings.Rules;
        if (!ReferenceEquals(srcRules, _cachedRulesSource))
        {
            _cachedRules       = srcRules?.Where(r => r.IsEnabled).ToList();
            _cachedRulesSource = srcRules;
        }
        var rules = _cachedRules;
        if (rules is null || rules.Count == 0) return;

        var alive = new HashSet<int>(processes.Select(p => p.Pid));

        foreach (var proc in processes)
        {
            bool isNew = _seenPids.Add(proc.Pid);
            bool ruleMatched = false;
            foreach (var rule in rules.Where(r => RuleMatchesProcess(r, proc)))
            {
                // ── Disallowed: terminate immediately ─────────────────────
                if (rule.Disallowed && isNew)
                {
                    ruleMatched = true;
                    try { await _processProvider.KillProcessAsync(proc.Pid); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Kill disallowed process {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    continue;
                }

                // ── Persistent actions on new process ─────────────────────
                if (isNew)
                {
                    ruleMatched = true;
                    if (rule.Priority.HasValue)
                        try { await _processProvider.SetPriorityAsync(proc.Pid, rule.Priority.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Set priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (rule.AffinityMask.HasValue)
                        try { await _processProvider.SetAffinityAsync(proc.Pid, rule.AffinityMask.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Set affinity on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (rule.IoPriority.HasValue)
                        try { await _processProvider.SetIoPriorityAsync(proc.Pid, rule.IoPriority.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Set IO priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (rule.MemoryPriority.HasValue)
                        try { await _processProvider.SetMemoryPriorityAsync(proc.Pid, rule.MemoryPriority.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Set memory priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (rule.EfficiencyMode.HasValue)
                        try { await _processProvider.SetEfficiencyModeAsync(proc.Pid, rule.EfficiencyMode.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Set efficiency mode on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (rule.CpuSetIds is { Length: > 0 })
                        try { await _processProvider.SetCpuSetsAsync(proc.Pid, rule.CpuSetIds); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Set CPU sets on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                }

                // ── KeepRunning: record image path while alive ─────────────
                if (rule.KeepRunning)
                {
                    var key = rule.ProcessNamePattern.ToLowerInvariant();
                    if (!_keepRunningState.TryGetValue(key, out var ks))
                        _keepRunningState[key] = ks = new KeepRunningState();
                    // Update image path from the running process snapshot
                    if (!string.IsNullOrEmpty(proc.ImagePath))
                        ks.ImagePath = proc.ImagePath;
                    ks.RestartCount = 0; // process is alive — reset counter
                }

                // ── Watchdog conditions ────────────────────────────────────
                if (rule.WatchdogAction != WatchdogAction.None && rule.Condition is not null)
                    await EvaluateWatchdog(proc, rule);
            }

            // ── Apply saved preferences if no explicit rule matched ────────
            if (isNew && !ruleMatched && _preferenceStore is not null)
            {
                var pref = _preferenceStore.Get(proc.Name);
                if (pref is not null)
                {
                    if (pref.Priority.HasValue)
                        try { await _processProvider.SetPriorityAsync(proc.Pid, pref.Priority.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Apply pref priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (pref.AffinityMask.HasValue)
                        try { await _processProvider.SetAffinityAsync(proc.Pid, pref.AffinityMask.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Apply pref affinity on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (pref.IoPriority.HasValue)
                        try { await _processProvider.SetIoPriorityAsync(proc.Pid, pref.IoPriority.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Apply pref IO priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (pref.MemoryPriority.HasValue)
                        try { await _processProvider.SetMemoryPriorityAsync(proc.Pid, pref.MemoryPriority.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Apply pref memory priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                    if (pref.EfficiencyMode.HasValue)
                        try { await _processProvider.SetEfficiencyModeAsync(proc.Pid, pref.EfficiencyMode.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Apply pref efficiency mode on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
                }
            }
        }

        // ── Evict dead PIDs ────────────────────────────────────────────────
        var deadPids = _seenPids.Where(pid => !alive.Contains(pid)).ToList();
        foreach (var dead in deadPids)
        {
            _seenPids.Remove(dead);
            foreach (var key in _conditionFirstSeen.Keys.Where(k => k.pid == dead).ToList())
                _conditionFirstSeen.Remove(key);
        }

        // ── KeepRunning: detect exits and restart ─────────────────────────
        await EvaluateKeepRunning(processes, rules);

        // ── Instance count limits ─────────────────────────────────────────
        await EvaluateInstanceLimits(processes, rules);
        } // end try
        catch (Exception ex) { _logger.LogDebug(ex, "RulesEngine OnTick error"); }
        finally { _tickLock.Release(); }
    }

    // ── Rule matching ────────────────────────────────────────────────────────

    private bool RuleMatchesProcess(ProcessRule rule, ProcessInfo proc)
    {
        // Direct pattern match (existing behavior)
        if (rule.Matches(proc.Name)) return true;
        // Group-based match (new)
        if (!string.IsNullOrEmpty(rule.GroupName) && _groupStore is not null)
        {
            var group = _groupStore.FindGroupForProcess(proc.Name);
            if (group is not null &&
                group.Name.Equals(rule.GroupName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── KeepRunning ──────────────────────────────────────────────────────────

    private Task EvaluateKeepRunning(
        IReadOnlyList<ProcessInfo> processes,
        List<ProcessRule> rules)
    {
        var now = DateTime.UtcNow;
        var aliveNames = new HashSet<string>(
            processes.Select(p => p.Name.ToLowerInvariant()));

        foreach (var rule in rules.Where(r => r.KeepRunning))
        {
            var key = rule.ProcessNamePattern.ToLowerInvariant();
            if (!_keepRunningState.TryGetValue(key, out var ks)) continue;

            // Check if process has exited (we had image path, but it's no longer running)
            bool isAlive = aliveNames.Any(n =>
                rule.Matches(n) || n.Contains(key.TrimEnd('*').TrimStart('*'),
                    StringComparison.OrdinalIgnoreCase));

            if (isAlive || string.IsNullOrEmpty(ks.ImagePath)) continue;

            // Process exited — record exit time on first observation
            if (ks.LastExitTime == default)
            {
                ks.LastExitTime = now;
                continue;
            }

            // Cooldown check
            var cooldown = TimeSpan.FromSeconds(
                Math.Max(1, rule.KeepRunningCooldownSeconds));
            if ((now - ks.LastExitTime) < cooldown) continue;

            // Retry cap: max N restarts per 60-second window
            if ((now - ks.WindowStart).TotalSeconds > 60)
            {
                ks.WindowStart  = now;
                ks.RestartCount = 0;
            }
            if (ks.RestartCount >= rule.KeepRunningMaxRetries) continue;

            // Restart
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = ks.ImagePath,
                    UseShellExecute = true
                });
                ks.RestartCount++;
                ks.LastExitTime = default;
                _logger.LogInformation("KeepRunning: restarted {ImagePath} (attempt {Count})", ks.ImagePath, ks.RestartCount);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "KeepRunning: restart failed for {ImagePath}", ks.ImagePath); }
        }

        return Task.CompletedTask;
    }

    // ── Instance count limits ─────────────────────────────────────────────────

    private async Task EvaluateInstanceLimits(
        IReadOnlyList<ProcessInfo> processes,
        List<ProcessRule> rules)
    {
        foreach (var rule in rules.Where(r => r.MaxInstances.HasValue))
        {
            var max = rule.MaxInstances!.Value;
            var matching = processes
                .Where(p => RuleMatchesProcess(rule, p))
                .OrderBy(p => p.StartTime)    // oldest first → kill newest excess
                .ToList();

            if (matching.Count <= max) continue;

            var excess = matching.Skip(max).ToList();
            foreach (var proc in excess)
            {
                try { await _processProvider.KillProcessAsync(proc.Pid); }
                catch (Exception ex) { _logger.LogWarning(ex, "Instance limit: kill {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
            }
        }
    }

    // ── Watchdog ─────────────────────────────────────────────────────────────

    private async Task EvaluateWatchdog(ProcessInfo proc, ProcessRule rule)
    {
        var cond = rule.Condition!;
        bool over = cond.Type switch
        {
            ConditionType.CpuAbove => proc.CpuPercent > cond.CpuThresholdPercent,
            ConditionType.RamAbove => proc.WorkingSetBytes > cond.RamThresholdBytes,
            _                      => true
        };

        var key = (proc.Pid, rule.Id);
        if (over)
        {
            var now = DateTime.UtcNow;
            if (!_conditionFirstSeen.TryGetValue(key, out var first))
            {
                _conditionFirstSeen[key] = now;
                return;
            }
            if ((now - first).TotalSeconds < cond.DurationSeconds) return;

            // Condition sustained long enough — fire action
            _conditionFirstSeen.Remove(key);
            try
            {
                switch (rule.WatchdogAction)
                {
                    case WatchdogAction.SetBelowNormal:
                        await _processProvider.SetPriorityAsync(proc.Pid, ProcessPriority.BelowNormal);
                        break;
                    case WatchdogAction.SetIdle:
                        await _processProvider.SetPriorityAsync(proc.Pid, ProcessPriority.Idle);
                        break;
                    case WatchdogAction.Terminate:
                        await _processProvider.KillProcessAsync(proc.Pid);
                        break;
                    case WatchdogAction.SetIoPriorityLow:
                        await _processProvider.SetIoPriorityAsync(proc.Pid, IoPriority.Low);
                        break;
                    case WatchdogAction.SetEfficiencyMode:
                        await _processProvider.SetEfficiencyModeAsync(proc.Pid, true);
                        break;
                    case WatchdogAction.TrimWorkingSet:
                        await _processProvider.TrimWorkingSetAsync(proc.Pid);
                        break;
                    case WatchdogAction.Restart:
                        await _processProvider.KillProcessAsync(proc.Pid);
                        // Process restart (best-effort via image path if available)
                        if (!string.IsNullOrEmpty(proc.ImagePath))
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName        = proc.ImagePath,
                                    UseShellExecute = true
                                });
                                _logger.LogInformation("Watchdog: restarted {Name} ({ImagePath})", proc.Name, proc.ImagePath);
                            }
                            catch (Exception restartEx) { _logger.LogWarning(restartEx, "Watchdog: restart of {Name} failed", proc.Name); }
                        }
                        break;
                    case WatchdogAction.ReduceAffinity:
                        await ApplyReduceAffinityAsync(proc.Pid, rule.ActionParams?.ReduceCoreCount ?? 1);
                        break;
                    case WatchdogAction.LogOnly:
                        // No action — the condition-sustained log is implicit
                        break;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Watchdog action {Action} on {Name} (PID {Pid}) failed", rule.WatchdogAction, proc.Name, proc.Pid); }
        }
        else
        {
            _conditionFirstSeen.Remove(key);
        }
    }

    private async Task ApplyReduceAffinityAsync(int pid, int reduceCores)
    {
        try
        {
            var (procMask, sysMask) = await _processProvider.GetAffinityMasksAsync(pid);
            int coreCount = CountBits(procMask);
            int newCount  = Math.Max(1, coreCount - reduceCores);
            long newMask  = BuildMaskWithNCores(newCount, sysMask);
            await _processProvider.SetAffinityAsync(pid, newMask);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ReduceAffinity on PID {Pid} failed", pid); }
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
            if ((sysMask & (1L << i)) != 0) { result |= (1L << i); added++; }
        }
        return result == 0 ? 1 : result;
    }

    public void Dispose() => Stop();
}
