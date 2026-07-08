using System.Collections.Concurrent;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Lightweight registry that prevents multiple automation services from
/// simultaneously modifying the same process's priority/affinity.
/// Registered as a singleton; injected into AutoBalance, ForegroundBoost,
/// IdleThrottle, CpuLimiter, and InstanceBalancer.
/// </summary>
public sealed class ProcessActionLock
{
    private readonly ConcurrentDictionary<int, string> _locks = new();

    public bool TryLock(int pid, string owner)   => _locks.TryAdd(pid, owner);
    public void Release(int pid, string owner)   => _locks.TryRemove(new KeyValuePair<int, string>(pid, owner));
    public bool IsLockedBy(int pid, string owner) =>
        _locks.TryGetValue(pid, out var o) && o == owner;
    public bool IsLocked(int pid) => _locks.ContainsKey(pid);
}
