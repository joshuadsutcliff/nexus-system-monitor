using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Assigns non-overlapping CPU affinity masks to multiple instances of the same
/// process, either spreading them evenly or giving each a fixed core count.
/// Only rebalances when the instance count changes.
/// </summary>
public sealed class InstanceBalancerService : IDisposable
{
    private readonly IProcessProvider  _processProvider;
    private readonly AppSettings       _settings;
    private readonly ProcessActionLock _actionLock;
    private readonly ILogger<InstanceBalancerService> _logger;

    // ruleId → previous instance count (to detect when rebalance is needed)
    private readonly Dictionary<Guid, int> _prevInstanceCount = new();

    private IDisposable? _subscription;
    private bool _running;

    private readonly SemaphoreSlim _tickLock = new(1, 1);

    private const string Owner = "InstanceBalancer";

    public InstanceBalancerService(
        IProcessProvider  processProvider,
        AppSettings       settings,
        ProcessActionLock actionLock,
        ILogger<InstanceBalancerService>? logger = null)
    {
        _processProvider = processProvider;
        _settings        = settings;
        _actionLock      = actionLock;
        _logger          = logger ?? NullLogger<InstanceBalancerService>.Instance;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _subscription = _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "InstanceBalancerService process stream faulted; retrying with backoff"))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "InstanceBalancerService stream faulted");
                _running = false;
            });
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _subscription?.Dispose();
        _subscription = null;
        // InstanceBalancer does not restore affinities — OS releases handles when process exits
    }

    private async void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        if (!await _tickLock.WaitAsync(0)) return;
        try
        {
            if (!_settings.InstanceBalancerEnabled) return;

            var rules = _settings.InstanceBalancerRules?.Where(r => r.IsEnabled).ToList();
            if (rules is null || rules.Count == 0) return;

            // Need system affinity mask — use first process with a valid mask
            long sysMask = 0;
            if (processes.Count > 0)
            {
                try { (_, sysMask) = await _processProvider.GetAffinityMasksAsync(processes[0].Pid); }
                catch { return; }
            }
            if (sysMask == 0) return;

            int totalCores = CountBits(sysMask);

            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.ProcessNamePattern)) continue;

                var matching = processes
                    .Where(p => p.Name.Contains(
                        rule.ProcessNamePattern.TrimEnd('*').TrimStart('*'),
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Pid)
                    .ToList();

                int count = matching.Count;
                if (count == 0)
                {
                    _prevInstanceCount.Remove(rule.Id);
                    continue;
                }

                // Only rebalance when count changes
                if (_prevInstanceCount.TryGetValue(rule.Id, out var prev) && prev == count)
                    continue;
                _prevInstanceCount[rule.Id] = count;

                // Build affinity masks for each instance
                var masks = BuildMasks(rule, count, totalCores, sysMask);
                for (int i = 0; i < matching.Count; i++)
                {
                    var proc = matching[i];
                    if (_actionLock.IsLocked(proc.Pid)) continue;
                    if (!_actionLock.TryLock(proc.Pid, Owner)) continue;
                    try
                    {
                        await _processProvider.SetAffinityAsync(proc.Pid, masks[i]);
                    }
                    catch { }
                    finally
                    {
                        _actionLock.Release(proc.Pid, Owner);
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "InstanceBalancerService OnTick error"); }
        finally { _tickLock.Release(); }
    }

    private static List<long> BuildMasks(
        InstanceBalancerRule rule, int instanceCount, int totalCores, long sysMask)
    {
        var masks = new List<long>();

        if (rule.Algorithm == BalancerAlgorithm.SpreadEvenly)
        {
            int coresEach = Math.Max(1, totalCores / instanceCount);
            int coreIndex = 0;
            var coreBits  = GetCoreBits(sysMask);

            for (int i = 0; i < instanceCount; i++)
            {
                long mask = 0;
                for (int c = 0; c < coresEach && coreIndex < coreBits.Count; c++, coreIndex++)
                    mask |= coreBits[coreIndex];
                // If we've exhausted cores, wrap around
                if (mask == 0) mask = coreBits[0];
                masks.Add(mask);
            }
        }
        else // FixedCoreCount
        {
            var coreBits  = GetCoreBits(sysMask);
            int coresEach = Math.Max(1, Math.Min(rule.CoresPerInstance, totalCores));
            int coreIndex = 0;

            for (int i = 0; i < instanceCount; i++)
            {
                long mask = 0;
                for (int c = 0; c < coresEach; c++)
                {
                    mask |= coreBits[coreIndex % coreBits.Count];
                    coreIndex++;
                }
                masks.Add(mask);
            }
        }

        return masks;
    }

    private static List<long> GetCoreBits(long sysMask)
    {
        var bits = new List<long>();
        for (int i = 0; i < 64; i++)
        {
            long bit = 1L << i;
            if ((sysMask & bit) != 0)
                bits.Add(bit);
        }
        return bits.Count == 0 ? [1L] : bits;
    }

    private static int CountBits(long mask)
    {
        int count = 0;
        while (mask != 0) { count += (int)(mask & 1); mask >>= 1; }
        return count;
    }

    public void Dispose() => Stop();
}
