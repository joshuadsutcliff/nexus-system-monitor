using System.Text.Json;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Themes;

/// <summary>
/// Manages built-in and user-saved theme presets.
/// User presets are persisted to %APPDATA%/NexusMonitor/custom-themes.json.
/// </summary>
public class ThemePresetService
{
    private static readonly string _customPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusMonitor", "custom-themes.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly List<ThemePreset> _userPresets = new();

    public ThemePresetService()
    {
        LoadUserPresets();
    }

    /// <summary>All presets: built-ins first, then user-saved.</summary>
    public IReadOnlyList<ThemePreset> AllPresets =>
        [.. BuiltInThemePresets.All, .. _userPresets];

    /// <summary>Creates a new user preset from the given name and settings, saves it, and returns it.</summary>
    public ThemePreset SaveCurrentAsPreset(string name, AppSettings s)
    {
        var preset = new ThemePreset
        {
            Id                = Guid.NewGuid().ToString(),
            Name              = name,
            IsBuiltIn         = false,
            ThemeMode         = s.ThemeMode,
            AccentColorHex    = s.AccentColorHex,
            TextAccentColorHex = s.TextAccentColorHex,
            CustomWindowBgHex  = s.CustomWindowBgHex,
            CustomSurfaceBgHex = s.CustomSurfaceBgHex,
            CustomSidebarBgHex = s.CustomSidebarBgHex,
            IsGlassEnabled    = s.IsGlassEnabled,
            GlassOpacity      = s.GlassOpacity,
            BackdropBlurMode  = s.BackdropBlurMode,
            IsSpecularEnabled = s.IsSpecularEnabled,
            SpecularIntensity = s.SpecularIntensity,
            FontFamily        = s.FontFamily,
            FontSizeMultiplier = s.FontSizeMultiplier,
        };
        _userPresets.Add(preset);
        PersistUserPresets();
        return preset;
    }

    public void DeleteUserPreset(string id)
    {
        var idx = _userPresets.FindIndex(p => p.Id == id);
        if (idx < 0) return;
        _userPresets.RemoveAt(idx);
        PersistUserPresets();
    }

    private void LoadUserPresets()
    {
        try
        {
            if (!File.Exists(_customPath)) return;
            var json = File.ReadAllText(_customPath);
            var list = JsonSerializer.Deserialize<List<ThemePreset>>(json, _jsonOpts);
            if (list is not null)
            {
                foreach (var p in list)
                    p.IsBuiltIn = false;
                _userPresets.AddRange(list);
            }
        }
        catch { /* ignore corrupt file */ }
    }

    private void PersistUserPresets()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_customPath)!);
            var tmpPath = _customPath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(_userPresets, _jsonOpts));
            File.Move(tmpPath, _customPath, overwrite: true);
        }
        catch { /* ignore write failures */ }
    }
}
