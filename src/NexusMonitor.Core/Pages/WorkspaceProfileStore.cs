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
    /// Falls back to "Default" when no pointer file exists or it cannot be read.</summary>
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
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch (Exception) { /* fall through to the default — never throw from a property getter */ }
            return DefaultProfileName;
        }
    }

    /// <summary>Loads the active profile (per <see cref="ActiveProfileName"/>), falling back to a
    /// factory default when nothing is saved under that name: all <see cref="BuiltInPageLayouts"/>
    /// pages plus a neutral <see cref="ThemeRef"/> (PresetId null — "leave appearance as-is") and no
    /// pop-out states.</summary>
    public WorkspaceProfile LoadActive() => Load(ActiveProfileName) ?? FactoryDefault();

    /// <summary>Loads the named profile, or null when no file exists or it is corrupt. A corrupt
    /// file is renamed to .bak and never thrown from — callers should treat null the same as
    /// "not found".</summary>
    public WorkspaceProfile? Load(string name)
    {
        var path = PathFor(name);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (WorkspaceProfileSerializer.TryDeserialize(json, out var profile, out _))
                    return profile;
                File.Move(path, path + ".bak", overwrite: true);
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
    /// restriction as <see cref="Save"/>.</summary>
    public void Delete(string name)
    {
        ValidateName(name);
        if (string.Equals(name, ActiveProfileName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot delete '{name}' because it is the active profile.");

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
