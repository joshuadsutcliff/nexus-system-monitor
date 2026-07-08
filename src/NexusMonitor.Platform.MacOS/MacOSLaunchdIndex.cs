using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusMonitor.Platform.MacOS;

/// <summary>Facts needed by <see cref="LaunchdStartType.Map"/>, keyed by service label.</summary>
public readonly record struct LaunchdPlistFacts(bool RunAtLoad, bool KeepAliveTruthy);

/// <summary>
/// An immutable snapshot of the label→plist-facts index built by <see cref="MacOSLaunchdIndex"/>.
/// </summary>
public sealed class LaunchdPlistIndexSnapshot
{
    private readonly IReadOnlyDictionary<string, LaunchdPlistFacts> _byFilenameLabel;
    private readonly IReadOnlyDictionary<string, LaunchdPlistFacts> _byInternalLabel;

    internal LaunchdPlistIndexSnapshot(
        IReadOnlyDictionary<string, LaunchdPlistFacts> byFilenameLabel,
        IReadOnlyDictionary<string, LaunchdPlistFacts> byInternalLabel)
    {
        _byFilenameLabel = byFilenameLabel;
        _byInternalLabel = byInternalLabel;
    }

    /// <summary>Distinct plist files successfully indexed (approx. total plists on disk).</summary>
    public int PlistCount => _byFilenameLabel.Count;

    /// <summary>
    /// Fast path: `launchctl list` labels usually equal the plist's filename (`&lt;label&gt;.plist`),
    /// so try that direct dictionary lookup first — no XML parsing happens here, it already
    /// happened once during the index build. Only falls back to the internal-<c>Label</c>-keyed
    /// map (also pre-built during the same single scan, so this fallback is still O(1) and adds
    /// no extra process spawns) for the rare plist whose filename doesn't match its own Label key.
    /// </summary>
    public bool TryGetFacts(string label, out LaunchdPlistFacts facts)
    {
        if (_byFilenameLabel.TryGetValue(label, out facts)) return true;
        return _byInternalLabel.TryGetValue(label, out facts);
    }
}

/// <summary>
/// Builds and caches the label→launchd-plist-facts index used to derive an honest
/// <see cref="Core.Models.ServiceStartType"/> for `launchctl list` output (Sym-1 Task 3).
///
/// Performance (binding constraint from the brief): `launchctl list` returns several hundred
/// labels, but NONE of the per-plist work here (`plutil -convert xml1 -o -` — ~890 files on the
/// verification machine) may run per-service, per-refresh. The full scan happens once, lazily, on
/// first use; every subsequent call only re-enumerates each directory (cheap stat calls) and
/// re-converts a file if it's new or its mtime changed since it was cached — untouched files never
/// get re-shelled-out to. This type is intended to be held as a long-lived field (the DI container
/// registers <see cref="MacOSServicesProvider"/> as a singleton), so the cache naturally persists
/// for the app's lifetime rather than being rebuilt per Services-tab visit.
///
/// `GetDisabledLabels()` is deliberately NOT cached — it's 2 cheap `launchctl print-disabled`
/// execs, and the brief explicitly allows those per refresh, since disabled state can change
/// between refreshes (e.g. via System Settings > Login Items &amp; Extensions) without any plist
/// mtime changing.
/// </summary>
public sealed class MacOSLaunchdIndex
{
    // Order matches the brief's listing. First match for a given filename-derived label wins in
    // the vanishingly rare case of a same-named plist appearing under two of these roots.
    private static readonly string[] s_plistDirs =
    [
        "/System/Library/LaunchDaemons",
        "/System/Library/LaunchAgents",
        "/Library/LaunchDaemons",
        "/Library/LaunchAgents",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
    ];

    private readonly object _lock = new();
    private readonly Dictionary<string, PlistCacheEntry> _byPath = new(StringComparer.Ordinal);

    private readonly record struct PlistCacheEntry(DateTime Mtime, bool RunAtLoad, bool KeepAliveTruthy, string? Label);

    [DllImport("libSystem.dylib")]
    private static extern uint getuid();

    /// <summary>
    /// Returns the current label→plist-facts index, rebuilding (re-running `plutil`) only for
    /// files that are new or whose mtime changed since the last call. Safe to call on every
    /// refresh — when nothing on disk changed since the last build, this does directory
    /// enumeration + mtime comparisons only, with zero process spawns.
    /// </summary>
    public LaunchdPlistIndexSnapshot GetOrBuildIndex()
    {
        lock (_lock)
        {
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            var toConvert = new List<(string Path, DateTime Mtime)>();

            foreach (var dir in s_plistDirs)
            {
                IEnumerable<string> files;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    files = Directory.EnumerateFiles(dir, "*.plist");
                }
                catch { continue; }

                foreach (var path in files)
                {
                    seenPaths.Add(path);

                    DateTime mtime;
                    try { mtime = File.GetLastWriteTimeUtc(path); }
                    catch { continue; }

                    if (_byPath.TryGetValue(path, out var cached) && cached.Mtime == mtime)
                        continue; // unchanged since last build — skip the plutil spawn entirely

                    toConvert.Add((path, mtime));
                }
            }

            // The first-ever build on this machine needs ~890 independent `plutil` spawns
            // (measured ~15s run strictly sequentially — see Task 3 report). Each is I/O/process-
            // spawn bound, not CPU bound, so bounded parallelism (capped at core count — this is
            // still a one-time background job, not something that should try to seize every core)
            // cuts wall-clock time substantially without violating the "no per-service spawn"
            // rule: this is still exactly one spawn per plist file, per changed-since-last-build
            // file, not per launchctl-list service.
            if (toConvert.Count > 0)
            {
                var results = new System.Collections.Concurrent.ConcurrentBag<(string Path, PlistCacheEntry Entry)>();
                Parallel.ForEach(
                    toConvert,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
                    item =>
                    {
                        var xml = RunPlutilConvertToXml(item.Path);
                        if (!LaunchdStartType.TryParsePlist(xml, out var parsedFacts)) return;
                        results.Add((item.Path, new PlistCacheEntry(item.Mtime, parsedFacts.RunAtLoad, parsedFacts.KeepAliveTruthy, parsedFacts.Label)));
                    });

                foreach (var (path, entry) in results)
                    _byPath[path] = entry;
            }

            // Drop entries for plists that disappeared since the last build (rare: an uninstall).
            foreach (var stalePath in _byPath.Keys.Where(p => !seenPaths.Contains(p)).ToList())
                _byPath.Remove(stalePath);

            // Rebuilding the label-keyed lookup maps from the (now current) path cache is pure
            // in-memory dictionary work — no I/O, no plutil — so it's cheap to do unconditionally
            // rather than trying to patch two derived indexes incrementally.
            var byFilenameLabel = new Dictionary<string, LaunchdPlistFacts>(StringComparer.Ordinal);
            var byInternalLabel = new Dictionary<string, LaunchdPlistFacts>(StringComparer.Ordinal);
            foreach (var (path, entry) in _byPath)
            {
                var facts = new LaunchdPlistFacts(entry.RunAtLoad, entry.KeepAliveTruthy);
                var filenameLabel = Path.GetFileNameWithoutExtension(path);
                byFilenameLabel.TryAdd(filenameLabel, facts);
                if (!string.IsNullOrEmpty(entry.Label))
                    byInternalLabel.TryAdd(entry.Label, facts);
            }

            return new LaunchdPlistIndexSnapshot(byFilenameLabel, byInternalLabel);
        }
    }

    /// <summary>
    /// Returns the union of labels disabled in the system and per-user (gui/&lt;uid&gt;) launchd
    /// override domains. Two `launchctl` execs, run fresh every call — see the class doc for why
    /// this is intentionally not cached.
    /// </summary>
    public IReadOnlySet<string> GetDisabledLabels()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        result.UnionWith(LaunchdStartType.ParseDisabledLabels(MacOSServicesProvider.RunLaunchctl("print-disabled system")));
        result.UnionWith(LaunchdStartType.ParseDisabledLabels(MacOSServicesProvider.RunLaunchctl($"print-disabled gui/{getuid()}")));
        return result;
    }

    private static string RunPlutilConvertToXml(string path)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("plutil")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    // ArgumentList (not Arguments/string ctor) so a plist path containing spaces
                    // is passed as a single argv element rather than needing manual quoting.
                    ArgumentList           = { "-convert", "xml1", "-o", "-", path },
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } }
            return outputTask.Result;
        }
        catch
        {
            return string.Empty;
        }
    }
}
