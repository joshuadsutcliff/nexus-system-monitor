using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Services;

public class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    private readonly object _saveLock = new();
    private readonly ILogger<SettingsService> _logger;
    private readonly string _path;
    private Timer? _debounceTimer;

    // JSON snapshot captured on the Save() caller's thread. Mutations to Current happen
    // un-locked all over the UI, so serializing later on the debounce (threadpool) thread
    // could observe a half-mutated object; serializing at Save() time on the mutator's own
    // thread makes the background write race-free — it only ever writes this immutable string.
    private string? _pendingJson;

    public AppSettings Current { get; private set; } = new();

    /// <param name="logger">Standard DI-resolved logger.</param>
    /// <param name="settingsPath">
    /// Overrides the settings file path. Defaults to the real per-user path
    /// (%AppData%/NexusMonitor/settings.json on Windows, ~/Library/Application
    /// Support/NexusMonitor/settings.json on macOS) when omitted — production callers (DI via
    /// <c>services.AddSingleton&lt;SettingsService&gt;()</c>) get exactly the prior behavior
    /// with zero change. Exists so tests can redirect every read/write to a throwaway temp
    /// directory instead of clobbering the developer's live settings file (see
    /// SettingsServiceTests.cs — this was a confirmed, repeated real-data-loss incident before
    /// this parameter existed).
    /// </param>
    public SettingsService(ILogger<SettingsService> logger, string? settingsPath = null)
    {
        _logger = logger;
        _path = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusMonitor", "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new();

            // Migrate old settings: if "ThemeMode" was absent, derive from IsDarkTheme
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ThemeMode", out _))
                Current.ThemeMode = Current.IsDarkTheme ? "Dark" : "Light";

            MigrateLegacyBrandedKeys(doc.RootElement);
            MigrateQuietDefaultsGap(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", _path);
            Current = new();
        }
    }

    // ── De-branding settings-key migration (added 2026-07-07) ───────────────────
    // ProBalance/IdleSaver/SmartTrim were renamed to AutoBalance/IdleThrottle/MemoryReclaim.
    // Existing settings.json files on disk still use the old flat key names, and
    // System.Text.Json silently leaves the new (renamed) properties at their default
    // value when it doesn't find a matching key — it does NOT fall back to the old
    // key. This table copies each old key's raw JSON value onto the new property,
    // but only when the new key isn't already present (so a settings.json that's
    // already been re-saved under the new names is left alone).
    //
    // The next Save() persists only the new key names, so this shim is self-healing:
    // once a user's settings.json has been loaded+saved once post-upgrade, this table
    // never matches anything for them again. Safe to delete outright once pre-rename
    // settings.json files are no longer expected in the wild (post-1.x).
    private static readonly (string OldKey, string NewKey, Action<AppSettings, JsonElement> Apply)[]
        _legacyBrandedKeyMigrations =
    {
        ("ProBalanceEnabled",      "AutoBalanceEnabled",      (s, v) => s.AutoBalanceEnabled      = v.GetBoolean()),
        ("ProBalanceCpuThreshold", "AutoBalanceCpuThreshold", (s, v) => s.AutoBalanceCpuThreshold = v.GetDouble()),
        ("ProBalanceExclusions",   "AutoBalanceExclusions",   (s, v) => s.AutoBalanceExclusions   = v.Deserialize<List<string>>() ?? new()),

        ("IdleSaverEnabled",           "IdleThrottleEnabled",           (s, v) => s.IdleThrottleEnabled           = v.GetBoolean()),
        ("IdleSaverCpuThreshold",      "IdleThrottleCpuThreshold",      (s, v) => s.IdleThrottleCpuThreshold      = v.GetDouble()),
        ("IdleSaverIdleTicksRequired", "IdleThrottleIdleTicksRequired", (s, v) => s.IdleThrottleIdleTicksRequired = v.GetInt32()),
        ("IdleSaverExclusions",        "IdleThrottleExclusions",        (s, v) => s.IdleThrottleExclusions        = v.Deserialize<List<string>>() ?? new()),
        ("IdleSaverUseEfficiencyMode", "IdleThrottleUseEfficiencyMode", (s, v) => s.IdleThrottleUseEfficiencyMode = v.GetBoolean()),

        ("SmartTrimEnabled",         "MemoryReclaimEnabled",         (s, v) => s.MemoryReclaimEnabled         = v.GetBoolean()),
        ("SmartTrimIntervalSeconds", "MemoryReclaimIntervalSeconds", (s, v) => s.MemoryReclaimIntervalSeconds = v.GetInt32()),
        ("SmartTrimPressurePercent", "MemoryReclaimPressurePercent", (s, v) => s.MemoryReclaimPressurePercent = v.GetDouble()),
        ("SmartTrimMinWorkingSetMB", "MemoryReclaimMinWorkingSetMB", (s, v) => s.MemoryReclaimMinWorkingSetMB = v.GetInt32()),
    };

    private void MigrateLegacyBrandedKeys(JsonElement root)
    {
        foreach (var (oldKey, newKey, apply) in _legacyBrandedKeyMigrations)
        {
            if (root.TryGetProperty(newKey, out _)) continue;          // already on the new key — nothing to do
            if (!root.TryGetProperty(oldKey, out var oldValue)) continue; // no legacy value to migrate

            try { apply(Current, oldValue); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate legacy setting {OldKey} -> {NewKey}", oldKey, newKey);
            }
        }

        // Value-level migration: NavOrder entries and DefaultTab persist the sidebar LABEL text,
        // so a pre-rename settings.json contains the literal "ProBalance" — without this rewrite
        // the user's saved nav order / default tab silently resets after the rebrand.
        for (int i = 0; i < Current.NavOrder.Count; i++)
            if (Current.NavOrder[i] == "ProBalance") Current.NavOrder[i] = "Auto-Balance";
        if (Current.DefaultTab == "ProBalance") Current.DefaultTab = "Auto-Balance";
    }

    // ── First-launch quiet-defaults migration guard (added 2026-07-11) ─────────────────────
    // AppSettings' property initializers changed 6 fields' defaults from "loud" to "quiet"
    // (IsGlassEnabled, BackdropBlurMode, IsSpecularEnabled, AnimateSpecularShimmer,
    // AnimateValueChanges, ActiveThemePresetId) so a FRESH install gets a restrained first
    // impression. WriteToDisk() always serializes the FULL AppSettings object (every public
    // settable property, not a diff), so any settings.json written by a version that already
    // had these keys already carries the user's actual (old-default) value explicitly —
    // Deserialize reproduces it untouched and there is nothing to migrate for those users.
    //
    // The only gap is a settings.json that PREDATES one of these keys entirely (the property
    // didn't exist yet when that file was last written, so it was never serialized) — for those
    // keys only, Deserialize silently falls back to the NEW initializer default instead of the
    // OLD one the user has been living with. This table detects exactly that gap (key absent
    // from the raw JSON) and pins the OLD default so upgrading an existing install never
    // silently reskins it. A settings.json that doesn't exist AT ALL is a genuinely fresh
    // install (see the `if (!File.Exists(_path)) return;` guard above this method's only
    // caller) and is deliberately NOT migrated — those users get the new quiet defaults, which
    // is the entire point of this change.
    private static readonly (string Key, Action<AppSettings> ApplyOldDefault)[] _quietDefaultsMigrations =
    {
        ("IsGlassEnabled",         s => s.IsGlassEnabled         = true),
        ("BackdropBlurMode",       s => s.BackdropBlurMode       = "Acrylic"),
        ("IsSpecularEnabled",      s => s.IsSpecularEnabled      = true),
        ("AnimateSpecularShimmer", s => s.AnimateSpecularShimmer = true),
        ("AnimateValueChanges",    s => s.AnimateValueChanges    = true),
        ("ActiveThemePresetId",    s => s.ActiveThemePresetId    = ""),
    };

    private void MigrateQuietDefaultsGap(JsonElement root)
    {
        foreach (var (key, applyOldDefault) in _quietDefaultsMigrations)
        {
            if (root.TryGetProperty(key, out _)) continue; // key present — file already pins a real value, nothing to do
            applyOldDefault(Current);
        }
    }

    /// <summary>
    /// Saves settings with 250 ms debounce to coalesce rapid slider updates.
    /// Thread-safe: concurrent calls are serialized via _saveLock.
    /// </summary>
    public void Save()
    {
        // Restart the debounce timer on every call; the actual write happens 250 ms after the last call.
        lock (_saveLock)
        {
            _pendingJson = JsonSerializer.Serialize(Current, _opts);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => WriteToDisk(), null,
                dueTime: TimeSpan.FromMilliseconds(250),
                period:  Timeout.InfiniteTimeSpan);
        }
    }

    private void WriteToDisk()
    {
        try
        {
            string json;
            lock (_saveLock)
                json = _pendingJson ??= JsonSerializer.Serialize(Current, _opts);

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write to a temp file then rename to prevent partial writes corrupting the file
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to write settings to {Path}", _path); }
    }

    public void Dispose()
    {
        lock (_saveLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            // Final flush persists the LIVE object (not a stale debounce snapshot): re-serialize
            // now, on the disposing thread — services are already stopped at shutdown, so this
            // is the one moment the un-locked mutators are known to be quiet.
            _pendingJson = JsonSerializer.Serialize(Current, _opts);
        }
        WriteToDisk();
    }
}
