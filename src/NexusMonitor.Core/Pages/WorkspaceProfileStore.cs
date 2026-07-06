namespace NexusMonitor.Core.Pages;

using System.Text.RegularExpressions;

/// <summary>Named-profile persistence: one JSON file per <see cref="WorkspaceProfile"/> plus an
/// `active-profile` pointer file recording the current selection. Mirrors <see cref="PageLayoutStore"/>'s
/// shape: debounced (250 ms) atomic writes (tmp + move), synchronous flush on dispose, IO failures
/// logged-by-silence (never thrown). A corrupt profile file is renamed to .bak and treated as absent
/// — never a blank profile. Note: the debounce holds a single pending profile — concurrent saves of
/// DIFFERENT profiles within one window would drop the earlier one; saves are whole-profile so this
/// mirrors PageLayoutStore's single-page limitation and carries the same caveat.</summary>
public sealed class WorkspaceProfileStore : IDisposable
{
    private const string ActivePointerFileName = "active-profile";
    private const string DefaultProfileName = "Default";
    private const string LegacyDashboardFileName = "dashboard.json";

    private static readonly Regex ValidNamePattern = new("^[A-Za-z0-9 _-]+$", RegexOptions.Compiled);

    private readonly string _dir;
    private readonly string _legacyPagesDir;
    private readonly object _lock = new();
    private Timer? _debounce;
    private WorkspaceProfile? _pending;

    /// <summary>Fired when a saved layout could not be recovered from disk and the caller silently
    /// fell back to something else — without this signal the user gets zero feedback that their
    /// own profile didn't apply. The argument is the profile name that failed to load. Two
    /// distinct triggers both surface through this one event:
    /// <list type="bullet">
    /// <item>A profile file failed to parse and was preserved by renaming it to <c>.bak</c> (see
    /// <see cref="Load"/>) — fires for every caller of <see cref="Load"/>, whether called directly
    /// or indirectly through <see cref="LoadActive"/>.</item>
    /// <item><see cref="LoadActive"/>'s active-profile pointer resolved to a name with no loadable
    /// profile even though other profiles already exist on disk (e.g. a stale/tampered pointer
    /// naming a since-deleted profile) — fires once from <see cref="LoadActive"/> itself, and only
    /// when the failure wasn't already reported by the corrupt-file trigger above (never both, for
    /// the same fallback).</item>
    /// </list>
    /// NOT fired for a genuine fresh/first-run setup (nothing has ever been saved): materializing
    /// the factory default there is normal, not recovery.</summary>
    public event Action<string>? ProfileRecovered;

    /// <summary>Creates a store rooted at <paramref name="baseDirectory"/> (tests) or the per-user
    /// app-data profiles directory (production, when null). <paramref name="legacyPagesDirectory"/>
    /// is the P3 per-page directory consulted by <see cref="MigrateLegacyIfNeeded"/> (tests), or the
    /// production pages directory when null.</summary>
    public WorkspaceProfileStore(string? baseDirectory = null, string? legacyPagesDirectory = null)
    {
        _dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusMonitor", "profiles");
        _legacyPagesDir = legacyPagesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusMonitor", "pages");
    }

    /// <summary>Lists the names of all saved profiles (file-system scan of the profiles directory,
    /// sorted ordinally by name). Empty when the directory does not yet exist.</summary>
    public IReadOnlyList<string> ListProfiles()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<string>();

        return Directory.GetFiles(_dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The currently active profile's name, read from the `active-profile` pointer file.
    /// Falls back to "Default" when no pointer file exists, it cannot be read, or its content
    /// fails the same <c>[A-Za-z0-9 _-]</c> validation <see cref="Save"/>/<see cref="SetActive"/>/
    /// <see cref="Delete"/> enforce on write — a tampered pointer (e.g. a path-traversal payload)
    /// is treated exactly like a missing pointer file rather than propagated to <see cref="Load"/>.</summary>
    public string ActiveProfileName
    {
        get
        {
            try
            {
                var pointerPath = Path.Combine(_dir, ActivePointerFileName);
                if (File.Exists(pointerPath))
                {
                    var name = File.ReadAllText(pointerPath).Trim();
                    if (!string.IsNullOrWhiteSpace(name) && ValidNamePattern.IsMatch(name)) return name;
                }
            }
            catch (Exception) { /* fall through to the default — never throw from a property getter */ }
            return DefaultProfileName;
        }
    }

    /// <summary>Loads the active profile (per <see cref="ActiveProfileName"/>), falling back to a
    /// factory default when nothing is saved under that name: all <see cref="BuiltInPageLayouts"/>
    /// pages plus a neutral <see cref="ThemeRef"/> (PresetId null — "leave appearance as-is") and no
    /// pop-out states. Delegates to <see cref="Load"/>, so it inherits the same read-your-own-writes
    /// guarantee against a still-debouncing <see cref="Save"/> for the active profile.
    /// On a fresh (never-saved, non-migrated) setup — nothing resolves for the active name AND
    /// <see cref="ListProfiles"/> is empty — the factory default is MATERIALIZED onto disk (saved
    /// synchronously as "Default" and set active) rather than returned as an in-memory-only stand-in:
    /// otherwise <see cref="ListProfiles"/> stays empty forever while <see cref="ActiveProfileName"/>
    /// says "Default", and any UI profile picker bound to the former renders blank. When some OTHER
    /// named profile already exists but the active pointer resolves to nothing (e.g. a since-deleted
    /// profile), the factory default is still returned but NOT persisted — that scenario isn't a
    /// fresh setup and silently materializing a competing "Default" would be surprising; it instead
    /// raises <see cref="ProfileRecovered"/> (fresh-start materialization above never does).</summary>
    public WorkspaceProfile LoadActive()
    {
        var name = ActiveProfileName;
        var loaded = LoadCore(name, out var recoveredFromCorruption);
        if (recoveredFromCorruption) ProfileRecovered?.Invoke(name);
        if (loaded is not null) return loaded;

        var factoryDefault = FactoryDefault();
        if (ListProfiles().Count == 0)
        {
            SaveSynchronously(factoryDefault);
            SetActive(DefaultProfileName);
        }
        else if (!recoveredFromCorruption)
        {
            // Not a fresh start (other profiles exist) and this exact failure wasn't already
            // reported above — a stale/tampered pointer resolved to nothing. Signal once so the
            // caller isn't silently handed a non-persisted factory default with no explanation.
            ProfileRecovered?.Invoke(name);
        }
        return factoryDefault;
    }

    /// <summary>Loads the named profile, or null when no file exists, the name is invalid, or the
    /// file is corrupt. A name failing the same <c>[A-Za-z0-9 _-]</c> validation <see cref="Save"/>/
    /// <see cref="SetActive"/>/<see cref="Delete"/> enforce (e.g. a path-traversal payload reaching
    /// here via a tampered <see cref="ActiveProfileName"/> pointer) returns null immediately without
    /// touching the file system — a tampered name must never crash or corrupt-rename something
    /// outside the profiles directory. A corrupt (but validly-named) file is renamed to .bak and
    /// never thrown from — callers should treat null the same as "not found" in both cases. A
    /// corrupt-file rename raises <see cref="ProfileRecovered"/> with <paramref name="name"/> AFTER
    /// the rename succeeds, so the .bak file is already on disk by the time subscribers observe it.
    /// Read-your-own-writes: if a <see cref="Save"/> for this same name is still sitting in the
    /// 250 ms debounce window (not yet flushed to disk), that pending profile is returned directly
    /// instead of racing the flush — without this, a read landing inside the debounce window would
    /// silently observe the stale pre-save file.</summary>
    public WorkspaceProfile? Load(string name)
    {
        var profile = LoadCore(name, out var recoveredFromCorruption);
        if (recoveredFromCorruption) ProfileRecovered?.Invoke(name);
        return profile;
    }

    /// <summary>Shared implementation behind <see cref="Load"/>, also called directly by
    /// <see cref="LoadActive"/> (rather than through the public <see cref="Load"/>) so the latter
    /// can inspect <paramref name="recoveredFromCorruption"/> itself — that lets it decide whether
    /// it still needs to raise <see cref="ProfileRecovered"/> for a pointer that resolved to
    /// nothing WITHOUT double-firing when this method already handled a corrupt file.</summary>
    private WorkspaceProfile? LoadCore(string name, out bool recoveredFromCorruption)
    {
        recoveredFromCorruption = false;
        if (string.IsNullOrWhiteSpace(name) || !ValidNamePattern.IsMatch(name)) return null;

        lock (_lock)
        {
            // OrdinalIgnoreCase to match every on-disk file guard in this store (profile files
            // live on case-insensitive filesystems) — a Ordinal-only comparison here let a
            // case-variant read during the debounce window fall through to the stale on-disk copy.
            if (_pending is not null && string.Equals(_pending.Name, name, StringComparison.OrdinalIgnoreCase))
                return _pending;
        }

        var path = PathFor(name);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (WorkspaceProfileSerializer.TryDeserialize(json, out var profile, out _))
                    return profile;
                File.Move(path, path + ".bak", overwrite: true);
                recoveredFromCorruption = true;
            }
        }
        catch (Exception) { /* fall through to null — never throw from load */ }
        return null;
    }

    /// <summary>Queues a debounced save (250 ms, restart-on-call) of the whole profile. Dispose
    /// flushes synchronously. The profile's <see cref="WorkspaceProfile.Name"/> becomes the file
    /// name (sanitized): only <c>[A-Za-z0-9 _-]</c> is accepted — callers must validate/sanitize
    /// user-entered names before calling; anything else throws <see cref="ArgumentException"/>.</summary>
    public void Save(WorkspaceProfile profile)
    {
        ValidateName(profile.Name);
        lock (_lock)
        {
            _pending = profile;
            _debounce?.Dispose();
            _debounce = new Timer(_ => WriteToDisk(), null,
                dueTime: TimeSpan.FromMilliseconds(250), period: Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Sets the active profile pointer immediately (not debounced — this is a small,
    /// latency-sensitive switch action, unlike whole-profile <see cref="Save"/>). Same name
    /// restriction as <see cref="Save"/>.</summary>
    public void SetActive(string name)
    {
        ValidateName(name);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ActivePointerFileName), name);
    }

    /// <summary>Deletes the named profile's file, if any. Throws <see cref="InvalidOperationException"/>
    /// when asked to delete the currently active profile — switch active elsewhere first. Same name
    /// restriction as <see cref="Save"/>.
    /// <para>
    /// Also cancels a still-debouncing <see cref="Save"/> for this same name (OrdinalIgnoreCase —
    /// the file-guard convention this class uses everywhere else): <see cref="_pending"/> is never
    /// cleared once a flush succeeds (see <see cref="WriteToDisk"/>), so without this, deleting a
    /// profile shortly after saving it left the deleted profile sitting in <c>_pending</c> forever —
    /// a later flush (the debounce timer firing, or <see cref="Dispose"/>'s unconditional
    /// flush-on-shutdown) would silently re-write the file this method just removed. The pending
    /// slot is cleared and the timer disposed BEFORE the file is removed so no interleaving flush
    /// (from a timer callback already queued on the threadpool) can land in between and recreate
    /// the file after this method returns.
    /// </para></summary>
    public void Delete(string name)
    {
        ValidateName(name);
        if (string.Equals(name, ActiveProfileName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot delete '{name}' because it is the active profile.");

        lock (_lock)
        {
            if (_pending is not null && string.Equals(_pending.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _debounce?.Dispose();
                _debounce = null;
                _pending = null;
            }
        }

        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>One-time migration of P3's single-page persistence into the profile store: when no
    /// profiles exist yet AND a legacy <c>dashboard.json</c> is present in the legacy pages
    /// directory, builds a "Default" profile from it (theme = neutral <see cref="ThemeRef"/>),
    /// saves it synchronously, renames the legacy file to `.migrated`, and sets it active. Returns
    /// whether migration actually ran (false when profiles already exist or there is nothing to
    /// migrate).</summary>
    public bool MigrateLegacyIfNeeded()
    {
        if (ListProfiles().Count > 0) return false;

        var legacyPath = Path.Combine(_legacyPagesDir, LegacyDashboardFileName);
        if (!File.Exists(legacyPath)) return false;

        // NOTE: save -> rename -> SetActive is not atomic. A failure partway through leaves
        // partial effects on disk but still returns false (caught below), which looks like
        // "migration didn't happen" even though it partly did. This is accepted rather than
        // redesigned because every partial outcome self-heals on the next call/launch:
        //  - SaveSynchronously throws before the profile file lands: nothing changed (the
        //    legacy file is untouched), so the next call retries the whole migration.
        //  - SaveSynchronously succeeds but the rename or SetActive throws after it: the
        //    "Default" profile file now exists, so ListProfiles().Count > 0 makes the next
        //    call's guard (above) no-op immediately rather than retry — but that is still
        //    consistent state, not a stuck/broken one: ActiveProfileName already falls back
        //    to "Default" whenever the pointer file is missing or invalid, which is exactly
        //    what SetActive(DefaultProfileName) would have written, so LoadActive() resolves
        //    to the same profile either way. The only visible residue is the legacy file not
        //    being renamed to .migrated when the rename step itself is what failed — harmless
        //    clutter, not a correctness problem.
        try
        {
            var json = File.ReadAllText(legacyPath);
            if (!PageLayoutSerializer.TryDeserialize(json, out var page, out _) || page is null)
                return false;

            var pages = new Dictionary<string, PageLayout> { [page.PageId] = page };
            var profile = new WorkspaceProfile(DefaultProfileName, pages, new ThemeRef(), Array.Empty<PopOutState>());

            SaveSynchronously(profile);
            File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
            SetActive(DefaultProfileName);
            return true;
        }
        catch (Exception) { return false; }
    }

    private WorkspaceProfile FactoryDefault()
    {
        var pages = BuiltInPageLayouts.BuiltInPageIds.ToDictionary(id => id, BuiltInPageLayouts.Load);
        return new WorkspaceProfile(DefaultProfileName, pages, new ThemeRef(PresetId: null), Array.Empty<PopOutState>());
    }

    private void WriteToDisk()
    {
        try
        {
            WorkspaceProfile? profile;
            lock (_lock) { profile = _pending; }
            if (profile is null) return;
            SaveSynchronously(profile);
        }
        catch (Exception) { /* timer-thread escape would be process-fatal; mirror PageLayoutStore's broad catch */ }
    }

    private void SaveSynchronously(WorkspaceProfile profile)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(profile.Name);
        File.WriteAllText(path + ".tmp", WorkspaceProfileSerializer.Serialize(profile));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ValidNamePattern.IsMatch(name))
            throw new ArgumentException(
                $"Profile name '{name}' is invalid — only letters, digits, spaces, underscores, and hyphens are allowed. Caller-validated: sanitize user input before calling.",
                nameof(name));
    }

    private string PathFor(string name) => Path.Combine(_dir, name + ".json");

    /// <summary>Stops the debounce timer and flushes any pending save synchronously.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        WriteToDisk();
    }
}
