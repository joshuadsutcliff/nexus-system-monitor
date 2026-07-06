namespace NexusMonitor.Core.Pages;

/// <summary>Appearance snapshot for a profile: either a preset reference or a full embedded
/// snapshot (the 13 ThemePreset fields + SmartTintEnabled). Exactly one of PresetId/Snapshot
/// should be set; when both are present, Snapshot wins.</summary>
public sealed record ThemeRef(string? PresetId = null, ThemeSnapshot? Snapshot = null);

/// <summary>Portable appearance state. Field names/types mirror the AppSettings appearance
/// surface byte-for-byte so capture/apply are trivial copies.</summary>
public sealed record ThemeSnapshot(
    string ThemeMode, string AccentColorHex, string TextAccentColorHex,
    string CustomWindowBgHex, string CustomSurfaceBgHex, string CustomSidebarBgHex,
    bool IsGlassEnabled, double GlassOpacity, string BackdropBlurMode,
    bool IsSpecularEnabled, double SpecularIntensity,
    string FontFamily, double FontSizeMultiplier, bool SmartTintEnabled);

/// <summary>A named workspace: page layouts + appearance. PopOutStates is schema-reserved for
/// Phase 6 (pop-out windows) and always empty today.</summary>
public sealed record WorkspaceProfile(
    string Name,
    IReadOnlyDictionary<string, PageLayout> Pages,   // key = pageId
    ThemeRef Theme,
    IReadOnlyList<PopOutState> PopOutStates);         // reserved, empty in Phase 5
