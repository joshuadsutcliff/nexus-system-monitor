using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

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
/// Gate-review follow-up (Task 3 fix pass): the in-memory cache above only helps *within* one
/// process's lifetime — every fresh app launch still paid the full ~890-plutil cold build (5–15s
/// of "Loading services…" in the UI). The label→(mtime, facts) cache is now also persisted to disk
/// (see <see cref="LoadCacheFromDisk"/> / <see cref="PersistCacheToDisk"/>) in the same settings
/// directory <see cref="Core.Services.SettingsService"/> uses, so a fresh process only re-parses
/// plists whose mtime changed since the last run. A schema-version mismatch or any load failure
/// (corrupt file, permission error, etc.) silently falls back to a full rebuild — the cache is
/// purely an optimization, never a correctness dependency.
///
/// `GetDisabledLabels()` is deliberately NOT cached — it's 2 cheap `launchctl print-disabled`
/// execs, and the brief explicitly allows those per refresh, since disabled state can change
/// between refreshes (e.g. via System Settings > Login Items &amp; Extensions) without any plist
/// mtime changing.
/// </summary>
public sealed class MacOSLaunchdIndex
{
    // Order matches the brief's listing. First match for a given filename-derived label wins in
    // the vanishingly rare case of a same-named plist appearing under two of these roots — see
    // BuildLabelMaps, which walks this list in order to make that precedence deterministic rather
    // than dependent on Dictionary<TKey,TValue>'s unspecified enumeration order.
    private static readonly string[] s_plistDirs =
    [
        "/System/Library/LaunchDaemons",
        "/System/Library/LaunchAgents",
        "/Library/LaunchDaemons",
        "/Library/LaunchAgents",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
    ];

    // Disk-persisted cache: same settings directory convention as SettingsService, one JSON file,
    // written atomically (temp-file-then-rename) after a build that actually changed something.
    private const int CacheSchemaVersion = 1;

    private static readonly string s_cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusMonitor", "launchd-plist-cache.json");

    private static readonly JsonSerializerOptions s_jsonOpts = new() { WriteIndented = true };

    /// <summary>Test-only accessor for the disk-persisted cache file path, so integration tests can
    /// delete it before measuring a genuine cold build (a prior test run — or an earlier test in the
    /// same run — may have already persisted a cache at this path).</summary>
    internal static string CachePathForTesting => s_cachePath;

    private readonly object _lock = new();
    private readonly Dictionary<string, PlistCacheEntry> _byPath = new(StringComparer.Ordinal);

    // ParseFailed is the finding-5 negative-caching sentinel: a plist that fails to parse is
    // cached keyed by mtime exactly like a successfully-parsed one, so an unmodified bad plist is
    // never re-shelled-out to plutil on every rebuild. Entries with ParseFailed=true contribute no
    // facts to either label map (see BuildLabelMaps) — behaviorally identical to "no plist found".
    private readonly record struct PlistCacheEntry(DateTime Mtime, bool RunAtLoad, bool KeepAliveTruthy, string? Label, bool ParseFailed);

    // ── on-disk cache file shape (plain DTOs — kept private, only System.Text.Json needs them) ──

    private sealed class PersistedCacheFile
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, PersistedCacheEntry> Entries { get; set; } = new();
    }

    private sealed class PersistedCacheEntry
    {
        public long MtimeTicks { get; set; }
        public bool RunAtLoad { get; set; }
        public bool KeepAliveTruthy { get; set; }
        public string? Label { get; set; }
        public bool ParseFailed { get; set; }
    }

    [DllImport("libSystem.dylib")]
    private static extern uint getuid();

    public MacOSLaunchdIndex()
    {
        LoadCacheFromDisk();
    }

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
                        continue; // unchanged since last build (good or negative-cached bad) — skip the plutil spawn entirely

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
            var mutated = toConvert.Count > 0;
            if (mutated)
            {
                var results = new System.Collections.Concurrent.ConcurrentBag<(string Path, PlistCacheEntry Entry)>();
                Parallel.ForEach(
                    toConvert,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
                    item =>
                    {
                        // Per-item try/catch (matching RunPlutilConvertToXml's defensive pattern):
                        // TryParsePlist now catches broadly internally too (see LaunchdStartType),
                        // but this outer guard ensures that even an exception from somewhere else
                        // in this lambda can never escape Parallel.ForEach and empty the ENTIRE
                        // services list for the refresh — it degrades exactly one plist instead.
                        try
                        {
                            var xml = RunPlutilConvertToXml(item.Path);
                            if (LaunchdStartType.TryParsePlist(xml, out var parsedFacts))
                            {
                                results.Add((item.Path, new PlistCacheEntry(
                                    item.Mtime, parsedFacts.RunAtLoad, parsedFacts.KeepAliveTruthy, parsedFacts.Label, ParseFailed: false)));
                            }
                            else
                            {
                                // Negative cache (finding 5): a known-bad plist gets the same
                                // mtime-keyed cache lifecycle as a good one, so it isn't retried
                                // via plutil on every subsequent rebuild until it actually changes.
                                results.Add((item.Path, new PlistCacheEntry(
                                    item.Mtime, false, false, null, ParseFailed: true)));
                            }
                        }
                        catch
                        {
                            // Truly unanticipated failure: don't cache anything for this path, so
                            // it's simply retried (not permanently poisoned) on the next rebuild.
                        }
                    });

                foreach (var (path, entry) in results)
                    _byPath[path] = entry;
            }

            // Drop entries for plists that disappeared since the last build (rare: an uninstall).
            foreach (var stalePath in _byPath.Keys.Where(p => !seenPaths.Contains(p)).ToList())
            {
                _byPath.Remove(stalePath);
                mutated = true;
            }

            if (mutated)
                PersistCacheToDisk();

            // Rebuilding the label-keyed lookup maps from the (now current) path cache is pure
            // in-memory dictionary work — no I/O, no plutil — so it's cheap to do unconditionally
            // rather than trying to patch two derived indexes incrementally.
            var entries = _byPath.Select(kv => (
                kv.Key, kv.Value.RunAtLoad, kv.Value.KeepAliveTruthy, kv.Value.Label, kv.Value.ParseFailed));
            var (byFilenameLabel, byInternalLabel) = BuildLabelMaps(entries, s_plistDirs);

            return new LaunchdPlistIndexSnapshot(byFilenameLabel, byInternalLabel);
        }
    }

    /// <summary>
    /// Pure, dependency-free construction of the two label-keyed lookup maps from the flat
    /// path→facts cache. Deliberately walks <paramref name="orderedDirs"/> in the caller-supplied
    /// order (rather than iterating the cache's own — unordered — storage) so that "first match
    /// for a filename-derived label wins" is actually deterministic across the 5 launchd
    /// directories, matching the precedence the class doc promises. Kept as an internal static
    /// method (no field access) so it's unit-testable with plain fixture paths.
    /// </summary>
    internal static (Dictionary<string, LaunchdPlistFacts> ByFilenameLabel, Dictionary<string, LaunchdPlistFacts> ByInternalLabel) BuildLabelMaps(
        IEnumerable<(string Path, bool RunAtLoad, bool KeepAliveTruthy, string? Label, bool ParseFailed)> entries,
        IReadOnlyList<string> orderedDirs)
    {
        var byFilenameLabel = new Dictionary<string, LaunchdPlistFacts>(StringComparer.Ordinal);
        var byInternalLabel = new Dictionary<string, LaunchdPlistFacts>(StringComparer.Ordinal);

        // Group entries by containing directory once (O(n)), then walk orderedDirs below — this
        // is what makes cross-directory precedence deterministic instead of depending on
        // Dictionary<TKey,TValue>'s unspecified enumeration order (the bug this method pins:
        // _byPath used to be walked directly, populated via a ConcurrentBag merge with no
        // ordering guarantee at all).
        var byDir = new Dictionary<string, List<(string Path, bool RunAtLoad, bool KeepAliveTruthy, string? Label)>>(StringComparer.Ordinal);
        foreach (var dir in orderedDirs)
            byDir.TryAdd(dir, new List<(string, bool, bool, string?)>());

        foreach (var (path, runAtLoad, keepAliveTruthy, label, parseFailed) in entries)
        {
            if (parseFailed) continue; // known-bad sentinel — no facts to contribute (same as "no plist found")

            var dir = PosixDirectoryName(path);
            if (dir is not null && byDir.TryGetValue(dir, out var list))
                list.Add((path, runAtLoad, keepAliveTruthy, label));
        }

        foreach (var dir in orderedDirs)
        {
            foreach (var (path, runAtLoad, keepAliveTruthy, label) in byDir[dir])
            {
                var facts = new LaunchdPlistFacts(runAtLoad, keepAliveTruthy);
                var filenameLabel = PosixFileNameWithoutExtension(path);
                byFilenameLabel.TryAdd(filenameLabel, facts);
                if (!string.IsNullOrEmpty(label))
                    byInternalLabel.TryAdd(label, facts);
            }
        }

        return (byFilenameLabel, byInternalLabel);
    }

    // launchd plist paths are POSIX by definition ("/Library/LaunchDaemons/foo.plist") regardless
    // of what OS this code happens to be JIT'd on. System.IO.Path's behavior is host-OS-sensitive
    // (Windows recognizes '\' as the primary separator, and depending on runtime specifics can
    // disagree with a pure '/'-delimited split), which made cross-directory precedence matching
    // above a Windows-vs-macOS/Linux correctness bug: unit tests exercising this pure function
    // with POSIX fixture paths passed on macOS/Linux runners but failed on Windows CI. These two
    // helpers do explicit ordinal '/'-based string slicing instead of calling into Path.*, so the
    // result is identical on every host OS by construction — there is no branch, flag, or
    // OS-conditional logic to keep in sync, because the code never asks the host what a directory
    // separator is.
    private static string? PosixDirectoryName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[..slash];
    }

    private static string PosixFileNameWithoutExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var fileName = slash < 0 ? path : path[(slash + 1)..];
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
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

    /// <summary>
    /// Loads the disk-persisted path→facts cache written by <see cref="PersistCacheToDisk"/> on a
    /// prior run, so the very first <see cref="GetOrBuildIndex"/> call in THIS process only
    /// re-converts plists whose mtime changed since then, instead of paying the full ~890-plutil
    /// cold build every app launch. Called once from the constructor. Any failure — file missing,
    /// corrupt JSON, schema-version mismatch, permission error — silently leaves <see cref="_byPath"/>
    /// empty, which is exactly the pre-existing "no cache yet" cold-start behavior; this is purely
    /// an optimization, never allowed to crash or block startup.
    /// </summary>
    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(s_cachePath)) return;

            var json = File.ReadAllText(s_cachePath);
            var persisted = JsonSerializer.Deserialize<PersistedCacheFile>(json);
            if (persisted is null || persisted.SchemaVersion != CacheSchemaVersion) return;

            foreach (var (path, entry) in persisted.Entries)
            {
                _byPath[path] = new PlistCacheEntry(
                    new DateTime(entry.MtimeTicks, DateTimeKind.Utc),
                    entry.RunAtLoad, entry.KeepAliveTruthy, entry.Label, entry.ParseFailed);
            }
        }
        catch
        {
            _byPath.Clear();
        }
    }

    /// <summary>
    /// Atomically persists the current path→facts cache to <see cref="s_cachePath"/> (write to a
    /// `.tmp` file then rename over the target — same convention <see cref="Core.Services.SettingsService"/>
    /// uses — so a crash mid-write never leaves a corrupt file for the next load to choke on).
    /// Called only when a build actually changed <see cref="_byPath"/>, from within the
    /// <see cref="_lock"/> already held by <see cref="GetOrBuildIndex"/>. Best-effort: any failure
    /// just means the next process pays a (partial or full) cold rebuild — never lets a disk error
    /// surface past this method.
    /// </summary>
    private void PersistCacheToDisk()
    {
        try
        {
            var persisted = new PersistedCacheFile { SchemaVersion = CacheSchemaVersion };
            foreach (var (path, entry) in _byPath)
            {
                persisted.Entries[path] = new PersistedCacheEntry
                {
                    MtimeTicks      = entry.Mtime.Ticks,
                    RunAtLoad       = entry.RunAtLoad,
                    KeepAliveTruthy = entry.KeepAliveTruthy,
                    Label           = entry.Label,
                    ParseFailed     = entry.ParseFailed,
                };
            }

            var json = JsonSerializer.Serialize(persisted, s_jsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(s_cachePath)!);
            var tmp = s_cachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, s_cachePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence only — never let a failed write break the in-memory result
            // already computed for this call.
        }
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
