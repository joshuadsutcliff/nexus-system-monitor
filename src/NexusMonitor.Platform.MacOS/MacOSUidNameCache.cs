namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// In-memory uid → account-name cache. The resolver is injected so the caching policy (resolve at
/// most once per uid, honor honest-empty on lookup failure, never throw) is unit-testable without
/// any P/Invoke. <see cref="MacOSProcessProvider"/> wires in a real <c>getpwuid_r</c>-backed
/// resolver; tests wire in a fake to verify call counts and failure handling deterministically.
/// </summary>
public sealed class MacOSUidNameCache
{
    private readonly Dictionary<uint, string> _cache = new();
    private readonly Func<uint, string?> _resolve;

    public MacOSUidNameCache(Func<uint, string?> resolve) => _resolve = resolve;

    /// <summary>Number of times the injected resolver has actually been invoked — exposed so
    /// tests can assert cache hits skip the resolver entirely.</summary>
    public int ResolveCallCount { get; private set; }

    /// <summary>
    /// Returns the cached/resolved account name for <paramref name="uid"/>. A resolver failure
    /// (returns null) is cached as an empty string — the honest "unknown" convention used
    /// elsewhere in this provider (mirrors the CPU%/AccessDenied boundary) — rather than
    /// retried every call or fabricated.
    /// </summary>
    public string GetName(uint uid)
    {
        if (_cache.TryGetValue(uid, out var cached))
            return cached;

        ResolveCallCount++;
        var name = _resolve(uid) ?? string.Empty;
        _cache[uid] = name;
        return name;
    }
}
