using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Alerts;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Storage;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using NexusMonitor.Core.Telemetry;
using NexusMonitor.Core.Themes;
using NexusMonitor.UI.Messages;
using NexusMonitor.UI.Services;
using SkiaSharp;

namespace NexusMonitor.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService              _settings;
    private readonly PrometheusExporter           _exporter;
    private readonly AnomalyDetectionService      _anomalyService;
    private readonly AnomalyDetectionConfig       _anomalyConfig;
    private readonly GlassAdaptiveService         _glassAdaptive;
    private readonly ThemePresetService           _presetService;
    private readonly ProcessPreferenceStore?      _preferenceStore;
    private readonly WebhookNotificationService              _webhookService;
    private readonly PredictionService?                      _predictionService;
    private readonly HealthSnapshotPersistenceService?       _healthSnapshotService;
    private readonly EventMonitorService?                    _eventMonitorService;
    private readonly UpdateCheckService?                     _updateCheckService;
    private IDisposable?                                     _updateSubscription;

    // ── Saved Process Preferences ────────────────────────────────────────────
    public ObservableCollection<ProcessPreference> SavedPreferences { get; } = new();

    // Luminance-derived min alpha floor (0x80–0xE0); null = feature disabled
    private byte? _luminanceMinAlpha;

    // ── Batch-apply guard ─────────────────────────────────────────────────────
    // When true, OnXxxChanged callbacks skip Apply* calls (a single ApplyAllVisuals()
    // is called after all properties are set).
    private bool _suppressApply;

    // ── Appearance ────────────────────────────────────────────────────────────
    [ObservableProperty] private int    _themeModeIndex; // 0=System, 1=Dark, 2=Light

    private static readonly string[] _themeModeValues = ["System", "Dark", "Light"];
    public static IReadOnlyList<string> ThemeModeLabels => _themeModeValues;

    private Action? _osThemeCleanup;

    // ── Theme Presets ─────────────────────────────────────────────────────────
    // Manual property (not [ObservableProperty]) so we can set the backing field
    // directly in MarkCustomPreset/RebuildPresetNames without triggering OnSelectedPresetIndexChanged.
    private int _selectedPresetIndex;
    private bool _suppressPresetCallback;

    public int SelectedPresetIndex
    {
        get => _selectedPresetIndex;
        set
        {
            if (!SetProperty(ref _selectedPresetIndex, value)) return;
            OnPropertyChanged(nameof(CanDeleteSelectedPreset));
            if (!_suppressPresetCallback && value >= 0 && value < _availablePresets.Count)
                ApplyPreset(_availablePresets[value]);
        }
    }

    /// <summary>All preset names plus "(Custom)" as the final entry.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> PresetNames { get; } = new();

    /// <summary>Dynamic surface swatch palette for dark mode — updated when the active preset changes.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SwatchColor> DarkSurfaceSwatches { get; } = new();

    private IReadOnlyList<NexusMonitor.Core.Models.ThemePreset> _availablePresets = Array.Empty<NexusMonitor.Core.Models.ThemePreset>();

    /// <summary>True when the selected theme mode resolves to dark (for AXAML surface swatch visibility).</summary>
    public bool IsDarkActive
    {
        get
        {
            if (ThemeModeIndex == 1) return true;   // Dark
            if (ThemeModeIndex == 2) return false;  // Light
            // System — check app variant
            return Application.Current?.RequestedThemeVariant != ThemeVariant.Light;
        }
    }

    public bool CanDeleteSelectedPreset =>
        _selectedPresetIndex >= 0 &&
        _selectedPresetIndex < _availablePresets.Count &&
        !_availablePresets[_selectedPresetIndex].IsBuiltIn;

    // Font name → avares:// URI for bundled fonts
    private static readonly Dictionary<string, string> _bundledFontUris = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Orbitron"]        = "avares://NexusMonitor/Assets/Fonts/Orbitron-Regular.ttf#Orbitron",
        ["Quicksand"]       = "avares://NexusMonitor/Assets/Fonts/Quicksand-Regular.ttf#Quicksand",
        ["Nunito"]          = "avares://NexusMonitor/Assets/Fonts/Nunito-Regular.ttf#Nunito",
        ["Rajdhani"]        = "avares://NexusMonitor/Assets/Fonts/Rajdhani-Regular.ttf#Rajdhani",
        ["Space Grotesk"]   = "avares://NexusMonitor/Assets/Fonts/SpaceGrotesk-Regular.ttf#Space Grotesk",
        ["DM Sans"]         = "avares://NexusMonitor/Assets/Fonts/DMSans-Regular.ttf#DM Sans 14pt",
        ["Cinzel"]          = "avares://NexusMonitor/Assets/Fonts/Cinzel-Regular.ttf#Cinzel",
        ["Share Tech Mono"] = "avares://NexusMonitor/Assets/Fonts/ShareTechMono-Regular.ttf#Share Tech Mono",
        ["Exo 2"]           = "avares://NexusMonitor/Assets/Fonts/Exo2-Regular.ttf#Exo 2",
        ["Poppins"]         = "avares://NexusMonitor/Assets/Fonts/Poppins-Regular.ttf#Poppins",
        ["Fira Code"]       = "avares://NexusMonitor/Assets/Fonts/FiraCode-Regular.ttf#Fira Code",
        ["Source Code Pro"] = "avares://NexusMonitor/Assets/Fonts/SourceCodePro-Regular.ttf#Source Code Pro",
        ["Zen Maru Gothic"] = "avares://NexusMonitor/Assets/Fonts/ZenMaruGothic-Regular.ttf#Zen Maru Gothic",
    };

    // ── Crystal Glass ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isGlassEnabled;
    [ObservableProperty] private double _glassOpacity    = 0.80;
    [ObservableProperty] private string _backdropBlurMode = "Acrylic";
    [ObservableProperty] private bool   _isSpecularEnabled;
    [ObservableProperty] private double _specularIntensity = 0.55;

    // ── Accent ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _accentColorHex     = "#0A84FF";
    [ObservableProperty] private string _textAccentColorHex = "";

    // ── Custom surface colors ─────────────────────────────────────────────────
    [ObservableProperty] private string _customWindowBgHex  = "";
    [ObservableProperty] private string _customSurfaceBgHex = "";
    [ObservableProperty] private string _customSidebarBgHex = "";

    // ── Color picker state ────────────────────────────────────────────────────
    // Unified current-picker color — routes to the appropriate hex property based on ActivePickerTarget.
    [ObservableProperty] private Color _pickerCurrentColor = Color.Parse("#0A84FF");

    internal enum ColorPickerTarget { PrimaryAccent, TextAccent, WindowBg, SurfaceBg, SidebarBg }

    private ColorPickerTarget _activePickerTarget;
    internal ColorPickerTarget ActivePickerTarget
    {
        get => _activePickerTarget;
        set
        {
            _activePickerTarget = value;
            OnPropertyChanged(nameof(PickerWindowTitle));
            OnPropertyChanged(nameof(PickerCurrentHex));
        }
    }

    /// <summary>Dynamic title for the color picker window.</summary>
    public string PickerWindowTitle => _activePickerTarget switch
    {
        ColorPickerTarget.TextAccent => "Custom Text Accent",
        ColorPickerTarget.WindowBg   => "Custom Window Chrome",
        ColorPickerTarget.SurfaceBg  => "Custom Surface Color",
        ColorPickerTarget.SidebarBg  => "Custom Sidebar Color",
        _                            => "Custom Accent Color",
    };

    /// <summary>Live hex string shown in the color picker window hex readout.</summary>
    public string PickerCurrentHex => _activePickerTarget switch
    {
        ColorPickerTarget.TextAccent => TextAccentColorHex,
        ColorPickerTarget.WindowBg   => CustomWindowBgHex,
        ColorPickerTarget.SurfaceBg  => CustomSurfaceBgHex,
        ColorPickerTarget.SidebarBg  => CustomSidebarBgHex,
        _                            => AccentColorHex,
    };

    private bool _suppressColorSync;

    // ── Typography ────────────────────────────────────────────────────────────
    [ObservableProperty] private string _fontFamily          = "";
    [ObservableProperty] private double _fontSizeMultiplier  = 1.0;

    // ── Performance ───────────────────────────────────────────────────────────
    [ObservableProperty] private int _updateIntervalIndex = 1; // 0=500ms 1=1s 2=2s 3=5s

    // ── Tray / close behaviour ────────────────────────────────────────────────
    /// <summary>Index into <see cref="CloseActionLabels"/>: 0=Ask, 1=Tray, 2=Exit.</summary>
    [ObservableProperty] private int  _closeActionIndex;
    [ObservableProperty] private bool _hideWidgetOnMinimize;
    [ObservableProperty] private bool _minimizeToTray = true;

    // ── Default tab ───────────────────────────────────────────────────────────
    [ObservableProperty] private int _defaultTabIndex;

    // ── Other ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _showOverlayWidget;

    // ── Notifications ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _desktopNotificationsEnabled  = true;
    [ObservableProperty] private bool   _anomalyNotificationsEnabled  = true;

    // ── Smart Glass Tint ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _smartTintEnabled;

    // ── Metrics & History ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _metricsEnabled;

    // ── Anomaly Detection ─────────────────────────────────────────────────────
    [ObservableProperty] private bool    _anomalyDetectionEnabled;
    [ObservableProperty] private string  _anomalySensitivity = "Medium";
    [ObservableProperty] private decimal _anomalyCooldownSeconds = 60m;
    [ObservableProperty] private decimal _metricsEventsRetentionDays = 90m;

    public static IReadOnlyList<string> AnomalySensitivities { get; } = ["Low", "Medium", "High"];

    // ── Telemetry ─────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrometheusStatusText))]
    private bool    _prometheusEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrometheusStatusText))]
    [NotifyPropertyChangedFor(nameof(TelegrafConfig))]
    private decimal _prometheusPort = 9182m;

    public string PrometheusStatusText => PrometheusEnabled
        ? $"Active — scrape at http://localhost:{(int)PrometheusPort}/metrics"
        : "Disabled";

    public string TelegrafConfig      => TelegrafConfigGenerator.Generate((int)PrometheusPort);
    public string GrafanaDashboardJson => GrafanaDashboard.Json;

    // ── Webhook Notifications ─────────────────────────────────────────────────
    [ObservableProperty] private bool   _webhookEnabled;
    [ObservableProperty] private string _webhookUrl     = "";
    [ObservableProperty] private string _webhookSecret  = "";
    [ObservableProperty] private bool   _webhookAlerts;
    [ObservableProperty] private bool   _webhookAnomalies;
    [ObservableProperty] private string _webhookTestStatus = "";

    // ── Resource Predictions ──────────────────────────────────────────────────
    [ObservableProperty] private bool _predictionsEnabled;

    // ── Update Checker ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _checkForUpdates;
    [ObservableProperty] private string _updateAvailableText = "";

    // ── Static lists ─────────────────────────────────────────────────────────

    public static IReadOnlyList<string> AccentPresets { get; } =
    [
        "#0A84FF", // Blue (default)
        "#BF5AF2", // Purple
        "#FF375F", // Pink
        "#FF9F0A", // Orange
        "#FFD60A", // Yellow
        "#34C759", // Green
        "#5AC8FA", // Teal
        "#FF453A", // Red
    ];

    public static IReadOnlyList<string> BackdropModes { get; } =
        ["None", "Blur", "Acrylic", "Mica"];

    public static IReadOnlyList<string> UpdateIntervalLabels { get; } =
        ["500 ms", "1 second", "2 seconds", "5 seconds"];

    private static readonly int[] _intervalValues = [500, 1000, 2000, 5000];

    public static IReadOnlyList<string> CloseActionLabels { get; } =
        ["Always Ask", "Minimize to Tray", "Close Application"];

    private static readonly string[] _closeActionValues = ["", "Tray", "Exit"];

    public static IReadOnlyList<string> DefaultTabLabels { get; } =
        ["(Last Used)", "Dashboard", "Network", "Performance", "Processes", "Services",
         "Startup", "System Info", "Automation", "Diagnostics", "Disk Analyzer", "Gaming Mode",
         "LAN Scanner", "Optimization", "Profiles", "ProBalance", "Alerts", "History",
         "Rules", "Settings"];

    private static readonly string[] _defaultTabValues =
        ["", "Dashboard", "Network", "Performance", "Processes", "Services",
         "Startup", "System Info", "Automation", "Diagnostics", "Disk Analyzer", "Gaming Mode",
         "LAN Scanner", "Optimization", "Profiles", "ProBalance", "Alerts", "History",
         "Rules", "Settings"];

    /// <summary>All system font families — enumerated lazily on first access (deferred past startup).</summary>
    private static readonly Lazy<IReadOnlyList<string>> _lazySystemFonts = new(LoadSystemFonts);
    public static IReadOnlyList<string> SystemFonts => _lazySystemFonts.Value;

    private static IReadOnlyList<string> LoadSystemFonts()
    {
        try
        {
            var fonts = SKFontManager.Default.FontFamilies
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            fonts.Insert(0, "(System Default)");
            return fonts;
        }
        catch { return ["(System Default)"]; }
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public SettingsViewModel(
        SettingsService              settings,
        PrometheusExporter           exporter,
        AnomalyDetectionService      anomalyService,
        AnomalyDetectionConfig       anomalyConfig,
        GlassAdaptiveService         glassAdaptive,
        ThemePresetService           presetService,
        WebhookNotificationService   webhookService,
        PredictionService?                      predictionService = null,
        ProcessPreferenceStore?                 preferenceStore = null,
        HealthSnapshotPersistenceService?       healthSnapshotService = null,
        EventMonitorService?                    eventMonitorService = null,
        UpdateCheckService?                     updateCheckService = null)
    {
        Title             = "Settings";
        _settings         = settings;
        _exporter         = exporter;
        _anomalyService   = anomalyService;
        _anomalyConfig    = anomalyConfig;
        _glassAdaptive    = glassAdaptive;
        _presetService    = presetService;
        _webhookService        = webhookService;
        _predictionService     = predictionService;
        _preferenceStore       = preferenceStore;
        _healthSnapshotService = healthSnapshotService;
        _eventMonitorService   = eventMonitorService;
        _updateCheckService    = updateCheckService;

        // Load saved preferences
        if (preferenceStore is not null)
            foreach (var p in preferenceStore.GetAll())
                SavedPreferences.Add(p);

        // Subscribe to luminance changes from GlassAdaptiveService
        _glassAdaptive.LuminanceChanged += OnLuminanceChanged;

        // Subscribe to update checker — populates the passive "vX.Y.Z available" text line
        if (_updateCheckService is not null)
        {
            _updateSubscription = _updateCheckService.Updates
                .Where(u => u is not null)
                .Subscribe(info => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    UpdateAvailableText = $"v{info!.Version} available — {info.ReleaseUrl}"));
        }

        // Load saved values via backing fields so partial callbacks don't fire during init
        _themeModeIndex     = Array.IndexOf(_themeModeValues, settings.Current.ThemeMode);
        if (_themeModeIndex < 0) _themeModeIndex = 0;
        _isGlassEnabled     = settings.Current.IsGlassEnabled;
        _glassOpacity       = settings.Current.GlassOpacity;
        _backdropBlurMode   = settings.Current.BackdropBlurMode;
        _isSpecularEnabled  = settings.Current.IsSpecularEnabled;
        _specularIntensity  = settings.Current.SpecularIntensity;
        _accentColorHex     = settings.Current.AccentColorHex;
        _textAccentColorHex = settings.Current.TextAccentColorHex;
        // Initialise picker from stored accent
        try
        {
            _pickerCurrentColor = Color.Parse(settings.Current.AccentColorHex);
        }
        catch { _pickerCurrentColor = Color.Parse("#0A84FF"); }
        _customWindowBgHex  = settings.Current.CustomWindowBgHex;
        _customSurfaceBgHex = settings.Current.CustomSurfaceBgHex;
        _customSidebarBgHex = settings.Current.CustomSidebarBgHex;
        _fontFamily              = settings.Current.FontFamily;
        _fontSizeMultiplier      = settings.Current.FontSizeMultiplier;
        _showOverlayWidget             = settings.Current.ShowOverlayWidget;
        _desktopNotificationsEnabled   = settings.Current.DesktopNotificationsEnabled;
        _anomalyNotificationsEnabled   = settings.Current.AnomalyNotificationsEnabled;
        _smartTintEnabled              = settings.Current.SmartTintEnabled;

        // Map stored CloseAction → index
        _closeActionIndex = Array.IndexOf(_closeActionValues, settings.Current.CloseAction);
        if (_closeActionIndex < 0) _closeActionIndex = 0;
        _hideWidgetOnMinimize = settings.Current.HideWidgetOnMinimize;
        _minimizeToTray       = settings.Current.MinimizeToTray;

        // Map stored DefaultTab → index
        _defaultTabIndex = Array.IndexOf(_defaultTabValues, settings.Current.DefaultTab);
        if (_defaultTabIndex < 0) _defaultTabIndex = 0;

        // Map stored UpdateIntervalMs → index
        _updateIntervalIndex = Array.IndexOf(_intervalValues, settings.Current.UpdateIntervalMs);
        if (_updateIntervalIndex < 0) _updateIntervalIndex = 1;

        // ── Metrics & History ─────────────────────────────────────────────────
        _metricsEnabled = settings.Current.MetricsEnabled;

        // ── Anomaly Detection ─────────────────────────────────────────────────
        _anomalyDetectionEnabled     = settings.Current.AnomalyDetectionEnabled;
        _anomalySensitivity          = settings.Current.AnomalySensitivity;
        _anomalyCooldownSeconds      = settings.Current.AnomalyCooldownSeconds;
        _metricsEventsRetentionDays  = settings.Current.MetricsEventsRetentionDays;

        // ── Telemetry ────────────────────────────────────────────────────────
        _prometheusEnabled = settings.Current.PrometheusEnabled;
        _prometheusPort    = settings.Current.PrometheusPort;

        // ── Webhook Notifications ─────────────────────────────────────────────
        _webhookEnabled   = settings.Current.WebhookEnabled;
        _webhookUrl       = settings.Current.WebhookUrl;
        _webhookSecret    = settings.Current.WebhookSecret;
        _webhookAlerts    = settings.Current.WebhookEvents.Contains("alerts");
        _webhookAnomalies = settings.Current.WebhookEvents.Contains("anomalies");

        // ── Resource Predictions ──────────────────────────────────────────────
        _predictionsEnabled = settings.Current.PredictionsEnabled;

        // ── Update Checker ─────────────────────────────────────────────────────
        _checkForUpdates = settings.Current.CheckForUpdates;

        // ── Theme preset list ──────────────────────────────────────────────────
        RebuildPresetNames();

        // Restore active preset selection (dropdown only; visuals already loaded from individual settings)
        _suppressPresetCallback = true;
        var savedPresetId = settings.Current.ActiveThemePresetId;
        if (!string.IsNullOrEmpty(savedPresetId))
        {
            var match = _availablePresets
                .Select((p, i) => (p, i))
                .FirstOrDefault(t => t.p.Id == savedPresetId);
            SelectedPresetIndex = match.p is not null ? match.i : PresetNames.Count - 1;
        }
        else
        {
            SelectedPresetIndex = PresetNames.Count - 1; // "(Custom)"
        }
        _suppressPresetCallback = false;

        // Restore at startup
        ApplyGlass(_isGlassEnabled, _glassOpacity,
            _customWindowBgHex, _customSurfaceBgHex, _customSidebarBgHex);
        ApplySpecular(_isGlassEnabled, _isSpecularEnabled, _specularIntensity);
        ApplyBackdropMode(_isGlassEnabled, _backdropBlurMode);
        ApplyAccentColor(_accentColorHex);
        ApplyTextAccent(_accentColorHex, _textAccentColorHex);
        ApplyFont(_fontFamily, _fontSizeMultiplier);
        UpdateSurfaceSwatches();
    }

    private void RebuildPresetNames()
    {
        _availablePresets = _presetService.AllPresets;
        PresetNames.Clear();
        foreach (var p in _availablePresets)
            PresetNames.Add(p.Name);
        PresetNames.Add("(Custom)");
    }

    private void UpdateSurfaceSwatches()
    {
        var presetId = _settings.Current.ActiveThemePresetId;
        var palette  = SurfaceSwatchPalettes.GetPalette(presetId, IsDarkActive);
        DarkSurfaceSwatches.Clear();
        foreach (var s in palette) DarkSurfaceSwatches.Add(s);
    }

    // ── Partial callbacks ─────────────────────────────────────────────────────

    partial void OnThemeModeIndexChanged(int value)
    {
        var mode = _themeModeValues[Math.Clamp(value, 0, _themeModeValues.Length - 1)];
        _settings.Current.ThemeMode = mode;
        _settings.Save();

        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyAllVisuals();
            OnPropertyChanged(nameof(IsDarkActive));
            UpdateSurfaceSwatches();
        }
    }

    partial void OnIsGlassEnabledChanged(bool value)
    {
        _settings.Current.IsGlassEnabled = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyGlass(value, GlassOpacity, CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex, _luminanceMinAlpha);
            ApplySpecular(value, IsSpecularEnabled, SpecularIntensity);
            ApplyBackdropMode(value, BackdropBlurMode);
        }
    }

    partial void OnGlassOpacityChanged(double value)
    {
        _settings.Current.GlassOpacity = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyGlass(IsGlassEnabled, value, CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex, _luminanceMinAlpha);
        }
    }

    partial void OnCustomWindowBgHexChanged(string value)
    {
        _settings.Current.CustomWindowBgHex = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyGlass(IsGlassEnabled, GlassOpacity, value, CustomSurfaceBgHex, CustomSidebarBgHex, _luminanceMinAlpha);
        }
        if (_activePickerTarget == ColorPickerTarget.WindowBg) OnPropertyChanged(nameof(PickerCurrentHex));
    }

    partial void OnCustomSurfaceBgHexChanged(string value)
    {
        _settings.Current.CustomSurfaceBgHex = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyGlass(IsGlassEnabled, GlassOpacity, CustomWindowBgHex, value, CustomSidebarBgHex, _luminanceMinAlpha);
        }
        if (_activePickerTarget == ColorPickerTarget.SurfaceBg) OnPropertyChanged(nameof(PickerCurrentHex));
    }

    partial void OnCustomSidebarBgHexChanged(string value)
    {
        _settings.Current.CustomSidebarBgHex = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyGlass(IsGlassEnabled, GlassOpacity, CustomWindowBgHex, CustomSurfaceBgHex, value, _luminanceMinAlpha);
        }
        if (_activePickerTarget == ColorPickerTarget.SidebarBg) OnPropertyChanged(nameof(PickerCurrentHex));
    }

    partial void OnBackdropBlurModeChanged(string value)
    {
        _settings.Current.BackdropBlurMode = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyBackdropMode(IsGlassEnabled, value);
        }
    }

    partial void OnIsSpecularEnabledChanged(bool value)
    {
        _settings.Current.IsSpecularEnabled = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplySpecular(IsGlassEnabled, value, SpecularIntensity);
        }
    }

    partial void OnSpecularIntensityChanged(double value)
    {
        _settings.Current.SpecularIntensity = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplySpecular(IsGlassEnabled, IsSpecularEnabled, value);
        }
    }

    partial void OnAccentColorHexChanged(string value)
    {
        _settings.Current.AccentColorHex = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyAccentColor(value);
            ApplyTextAccent(value, TextAccentColorHex);
        }

        // Keep the color-wheel picker in sync when accent is changed via presets
        if (!_suppressColorSync && _activePickerTarget == ColorPickerTarget.PrimaryAccent)
        {
            try
            {
                _suppressColorSync = true;
                PickerCurrentColor = Color.Parse(value);
            }
            catch { /* invalid hex — leave picker unchanged */ }
            finally { _suppressColorSync = false; }
        }
        OnPropertyChanged(nameof(PickerCurrentHex));
    }

    partial void OnTextAccentColorHexChanged(string value)
    {
        _settings.Current.TextAccentColorHex = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyTextAccent(AccentColorHex, value);
        }
        if (_activePickerTarget == ColorPickerTarget.TextAccent) OnPropertyChanged(nameof(PickerCurrentHex));
    }

    partial void OnFontFamilyChanged(string value)
    {
        _settings.Current.FontFamily = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyFont(value, FontSizeMultiplier);
        }
    }

    partial void OnFontSizeMultiplierChanged(double value)
    {
        _settings.Current.FontSizeMultiplier = value;
        _settings.Save();
        if (!_suppressApply)
        {
            MarkCustomPreset();
            ApplyFont(FontFamily, value);
        }
    }

    partial void OnCloseActionIndexChanged(int value)
    {
        _settings.Current.CloseAction =
            _closeActionValues[Math.Clamp(value, 0, _closeActionValues.Length - 1)];
        _settings.Save();
    }

    partial void OnHideWidgetOnMinimizeChanged(bool value)
    {
        _settings.Current.HideWidgetOnMinimize = value;
        _settings.Save();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.Current.MinimizeToTray = value;
        _settings.Save();
    }

    partial void OnDefaultTabIndexChanged(int value)
    {
        _settings.Current.DefaultTab =
            _defaultTabValues[Math.Clamp(value, 0, _defaultTabValues.Length - 1)];
        _settings.Save();
    }

    partial void OnUpdateIntervalIndexChanged(int value)
    {
        var ms = _intervalValues[Math.Clamp(value, 0, _intervalValues.Length - 1)];
        _settings.Current.UpdateIntervalMs = ms;
        _settings.Save();
        WeakReferenceMessenger.Default.Send(
            new MetricsIntervalChangedMessage(TimeSpan.FromMilliseconds(ms)));
    }

    // ── Overlay window ────────────────────────────────────────────────────────

    // Set by App.axaml.cs after the overlay window is created
    private Window? _overlayWindow;
    internal Window? OverlayWindow
    {
        get => _overlayWindow;
        set
        {
            _overlayWindow = value;
            // Apply current glass/backdrop/font settings to the newly assigned overlay
            if (value is not null)
            {
                ApplyBackdropMode(IsGlassEnabled, BackdropBlurMode);
                ApplyFont(FontFamily, FontSizeMultiplier);
            }
        }
    }

    partial void OnShowOverlayWidgetChanged(bool value)
    {
        _settings.Current.ShowOverlayWidget = value;
        _settings.Save();
        if (value) OverlayWindow?.Show();
        else        OverlayWindow?.Hide();
    }

    partial void OnDesktopNotificationsEnabledChanged(bool value)
    {
        _settings.Current.DesktopNotificationsEnabled = value;
        _settings.Save();
    }

    partial void OnAnomalyNotificationsEnabledChanged(bool value)
    {
        _settings.Current.AnomalyNotificationsEnabled = value;
        _settings.Save();
    }

    partial void OnSmartTintEnabledChanged(bool value)
    {
        _settings.Current.SmartTintEnabled = value;
        _settings.Save();
        if (value)
            _glassAdaptive.Start();
        else
        {
            _glassAdaptive.Stop();
            _luminanceMinAlpha = null;
            ApplyGlass(IsGlassEnabled, GlassOpacity, CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex);
        }
    }

    private void OnLuminanceChanged(byte minAlpha)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _luminanceMinAlpha = minAlpha;
            ApplyGlass(IsGlassEnabled, GlassOpacity, CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex, minAlpha);
        });
    }

    partial void OnMetricsEnabledChanged(bool value)
    {
        _settings.Current.MetricsEnabled = value;
        _settings.Save();
        var store  = App.Services.GetRequiredService<MetricsStore>();
        var rollup = App.Services.GetRequiredService<MetricsRollupService>();
        if (value)
        {
            store.Start(TimeSpan.FromMilliseconds(_settings.Current.UpdateIntervalMs));
            rollup.Start();
            _healthSnapshotService?.Start();
            _eventMonitorService?.Start();
            if (_settings.Current.PredictionsEnabled)
                _predictionService?.Start();
        }
        else
        {
            store.Stop();
            rollup.Stop();
            _healthSnapshotService?.Stop();
            _eventMonitorService?.Stop();
            _predictionService?.Stop();
        }
        WeakReferenceMessenger.Default.Send(new MetricsEnabledChangedMessage(value));
    }

    partial void OnAnomalyDetectionEnabledChanged(bool value)
    {
        _settings.Current.AnomalyDetectionEnabled = value;
        _settings.Save();
        _anomalyConfig.Enabled = value;
        if (value)
            _anomalyService.Start();
        else
            _anomalyService.Stop();
    }

    partial void OnAnomalySensitivityChanged(string value)
    {
        _settings.Current.AnomalySensitivity = value;
        _settings.Save();
        _anomalyConfig.ApplySensitivity(value);   // mutates in-place, takes effect next tick
    }

    partial void OnAnomalyCooldownSecondsChanged(decimal value)
    {
        _settings.Current.AnomalyCooldownSeconds = (int)value;
        _settings.Save();
        _anomalyConfig.CooldownSeconds = (int)value;
    }

    partial void OnMetricsEventsRetentionDaysChanged(decimal value)
    {
        _settings.Current.MetricsEventsRetentionDays = (int)value;
        _settings.Save();
    }

    partial void OnPrometheusEnabledChanged(bool value)
    {
        _settings.Current.PrometheusEnabled = value;
        _settings.Save();
        if (value)
            _exporter.Start((int)PrometheusPort);
        else
            _exporter.Stop();
    }

    partial void OnPrometheusPortChanged(decimal value)
    {
        _settings.Current.PrometheusPort = (int)value;
        _settings.Save();
        if (PrometheusEnabled)
        {
            _exporter.Stop();
            _exporter.Start((int)value);
        }
    }

    partial void OnWebhookEnabledChanged(bool value)      { _settings.Current.WebhookEnabled     = value; _settings.Save(); }
    partial void OnWebhookUrlChanged(string value)        { _settings.Current.WebhookUrl         = value; _settings.Save(); }
    partial void OnWebhookSecretChanged(string value)     { _settings.Current.WebhookSecret      = value; _settings.Save(); }
    partial void OnWebhookAlertsChanged(bool value)       { SyncWebhookEvents(); _settings.Save(); }
    partial void OnWebhookAnomaliesChanged(bool value)    { SyncWebhookEvents(); _settings.Save(); }
    partial void OnPredictionsEnabledChanged(bool value)
    {
        _settings.Current.PredictionsEnabled = value;
        _settings.Save();
        if (value)
            _predictionService?.Start();
        else
            _predictionService?.Stop();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.Current.CheckForUpdates = value;
        _settings.Save();
        if (value)
            _updateCheckService?.Start();
        else
            _updateCheckService?.Stop();
    }

    private void SyncWebhookEvents()
    {
        var events = new List<string>();
        if (WebhookAlerts)    events.Add("alerts");
        if (WebhookAnomalies) events.Add("anomalies");
        _settings.Current.WebhookEvents = events;
    }

    [RelayCommand]
    private async Task SendTestWebhook()
    {
        var ok = await _webhookService.SendTestAsync();
        WebhookTestStatus = ok
            ? "✓ Test webhook sent successfully"
            : "✗ Test webhook failed — check URL and connectivity";
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand] private void SetAccentColor(string h)       => AccentColorHex = h;
    [RelayCommand] private void SetTextAccentColor(string? h) => TextAccentColorHex = h ?? "";
    [RelayCommand] private void ResetWindowBg()               => CustomWindowBgHex  = "";
    [RelayCommand] private void ResetSurfaceBg()          => CustomSurfaceBgHex = "";
    [RelayCommand] private void ResetSidebarBg()          => CustomSidebarBgHex = "";
    [RelayCommand] private void SetWindowBg(string h)     => CustomWindowBgHex  = h;
    [RelayCommand] private void SetSurfaceBg(string h)    => CustomSurfaceBgHex = h;
    [RelayCommand] private void SetSidebarBg(string h)    => CustomSidebarBgHex = h;

    /// <summary>Called by the ColorWheelControl binding when the user picks a color.
    /// Routes to the appropriate hex property based on <see cref="ActivePickerTarget"/>.</summary>
    partial void OnPickerCurrentColorChanged(Color value)
    {
        if (_suppressColorSync) return;
        var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        _suppressColorSync = true;
        try
        {
            switch (_activePickerTarget)
            {
                case ColorPickerTarget.TextAccent:
                    if (!string.Equals(hex, TextAccentColorHex, StringComparison.OrdinalIgnoreCase)) TextAccentColorHex = hex;
                    break;
                case ColorPickerTarget.WindowBg:
                    if (!string.Equals(hex, CustomWindowBgHex, StringComparison.OrdinalIgnoreCase)) CustomWindowBgHex = hex;
                    break;
                case ColorPickerTarget.SurfaceBg:
                    if (!string.Equals(hex, CustomSurfaceBgHex, StringComparison.OrdinalIgnoreCase)) CustomSurfaceBgHex = hex;
                    break;
                case ColorPickerTarget.SidebarBg:
                    if (!string.Equals(hex, CustomSidebarBgHex, StringComparison.OrdinalIgnoreCase)) CustomSidebarBgHex = hex;
                    break;
                default:
                    if (!string.Equals(hex, AccentColorHex, StringComparison.OrdinalIgnoreCase)) AccentColorHex = hex;
                    break;
            }
        }
        finally { _suppressColorSync = false; }
    }

    // ── Preset apply ─────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a theme preset: batch-sets all 13 visual properties, then calls
    /// <see cref="ApplyAllVisuals"/> once to update the UI in a single pass.
    /// </summary>
    private void ApplyPreset(NexusMonitor.Core.Models.ThemePreset preset)
    {
        _suppressApply = true;
        try
        {
            // Map ThemeMode string to index
            var modeIdx = Array.IndexOf(_themeModeValues, preset.ThemeMode);
            ThemeModeIndex      = modeIdx < 0 ? 0 : modeIdx;

            AccentColorHex      = preset.AccentColorHex;
            TextAccentColorHex  = preset.TextAccentColorHex;
            CustomWindowBgHex   = preset.CustomWindowBgHex;
            CustomSurfaceBgHex  = preset.CustomSurfaceBgHex;
            CustomSidebarBgHex  = preset.CustomSidebarBgHex;
            IsGlassEnabled      = preset.IsGlassEnabled;
            GlassOpacity        = preset.GlassOpacity;
            BackdropBlurMode    = preset.BackdropBlurMode;
            IsSpecularEnabled   = preset.IsSpecularEnabled;
            SpecularIntensity   = preset.SpecularIntensity;
            FontFamily          = preset.FontFamily;
            FontSizeMultiplier  = preset.FontSizeMultiplier;
        }
        finally
        {
            _suppressApply = false;
        }

        // Record active preset ID
        _settings.Current.ActiveThemePresetId = preset.Id;
        _settings.Save();

        ApplyAllVisuals();
        UpdateSurfaceSwatches();
    }

    /// <summary>
    /// Applies all visual settings in one consolidated call.
    /// Sets the theme variant (including OS subscription if System mode),
    /// then calls ApplyGlass/Specular/Backdrop/Accent/Font.
    /// </summary>
    private void ApplyAllVisuals()
    {
        // OS theme subscription
        _osThemeCleanup?.Invoke();
        _osThemeCleanup = null;

        var mode = _themeModeValues[Math.Clamp(ThemeModeIndex, 0, _themeModeValues.Length - 1)];
        ThemeVariant variant;
        if (mode == "System")
        {
            variant = NexusMonitor.UI.App.DetectSystemTheme();
            if (Application.Current?.PlatformSettings is { } ps)
            {
                void Handler(object? s, PlatformColorValues e)
                {
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = NexusMonitor.UI.App.DetectSystemTheme();
                    ApplyGlass(IsGlassEnabled, GlassOpacity,
                        CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex, _luminanceMinAlpha);
                }
                ps.ColorValuesChanged += Handler;
                _osThemeCleanup = () => ps.ColorValuesChanged -= Handler;
            }
        }
        else
        {
            variant = mode == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = variant;

        ApplyGlass(IsGlassEnabled, GlassOpacity,
            CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex, _luminanceMinAlpha);
        ApplySpecular(IsGlassEnabled, IsSpecularEnabled, SpecularIntensity);
        ApplyBackdropMode(IsGlassEnabled, BackdropBlurMode);
        ApplyAccentColor(AccentColorHex);
        ApplyTextAccent(AccentColorHex, TextAccentColorHex);
        ApplyFont(FontFamily, FontSizeMultiplier);
        OnPropertyChanged(nameof(IsDarkActive));
    }

    /// <summary>
    /// Clears the active preset (shows "(Custom)" in dropdown).
    /// Called when the user manually edits any visual setting.
    /// </summary>
    private void MarkCustomPreset()
    {
        if (_suppressApply) return;
        _settings.Current.ActiveThemePresetId = "";
        var customIndex = PresetNames.Count - 1;
        if (_selectedPresetIndex != customIndex)
        {
            // Set backing field directly to avoid re-triggering ApplyPreset
            _suppressPresetCallback = true;
            SelectedPresetIndex = customIndex;
            _suppressPresetCallback = false;
        }
        UpdateSurfaceSwatches();
    }

    // ── Preset commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveCurrentAsPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _presetService.SaveCurrentAsPreset(name, _settings.Current);
        RebuildPresetNames();
        // Select the newly created preset (suppress callback to avoid re-applying)
        _suppressPresetCallback = true;
        SelectedPresetIndex = _availablePresets.Count - 1; // last user preset
        _suppressPresetCallback = false;
    }

    [RelayCommand]
    private void DeleteSelectedPreset()
    {
        if (!CanDeleteSelectedPreset) return;
        var preset = _availablePresets[_selectedPresetIndex];
        _presetService.DeleteUserPreset(preset.Id);
        RebuildPresetNames();
        // Reset to "(Custom)"
        _settings.Current.ActiveThemePresetId = "";
        _settings.Save();
        _suppressPresetCallback = true;
        SelectedPresetIndex = PresetNames.Count - 1;
        _suppressPresetCallback = false;
    }

    // ── Static resource helpers ───────────────────────────────────────────────
    //   Write directly into Application.Current.Resources so every
    //   {DynamicResource …} binding across the whole app updates instantly.

    /// <summary>
    /// Updates ALL background and glass-surface brushes.
    /// Custom color overrides (from the Settings color pickers) are respected when non-empty.
    /// Empty strings fall back to the built-in dark-theme defaults.
    /// <paramref name="opacity"/> 0 = fully transparent, 1 = fully opaque.
    /// </summary>
    private static void ApplyGlass(
        bool enabled, double opacity,
        string? customWindowBgHex  = null,
        string? customSurfaceBgHex = null,
        string? customSidebarBgHex = null,
        byte?   luminanceMinAlpha  = null)
    {
        if (Application.Current is null) return;

        // Determine current theme so fallback colours match the active palette.
        bool isDark = Application.Current.RequestedThemeVariant != ThemeVariant.Light;

        Color defaultBgBase    = isDark ? Color.Parse("#0A0A12") : Color.Parse("#F5F5FA");
        Color defaultBgSurface = isDark ? Color.Parse("#131318") : Color.Parse("#FFFFFF");

        // Resolve base colors — custom hex wins if valid; else use theme-appropriate defaults.
        Color bgBase    = TryParseColor(customWindowBgHex,  defaultBgBase);
        Color bgSurface = TryParseColor(customSurfaceBgHex, defaultBgSurface);
        Color bgSidebar = TryParseColor(customSidebarBgHex, defaultBgSurface);

        // Derive surface variants: lighten for dark mode, darken for light mode.
        // AdjustBrightness clamps to [0,255] in both directions.
        int secondaryDelta = isDark ?  9 : -15;
        int elevatedDelta  = isDark ? 16 : -23;
        int hoverDelta     = isDark ? 26 : -34;
        Color bgSecondary = AdjustBrightness(bgSurface, secondaryDelta);
        Color bgElevated  = AdjustBrightness(bgSurface, elevatedDelta);
        Color bgHover     = AdjustBrightness(bgSurface, hoverDelta);

        // Window-frame brush can go fully transparent (that IS the glass effect)
        byte bgAlpha = enabled ? (byte)Math.Round(opacity * 255) : (byte)0xFF;
        SetBrush("BgBaseBrush", bgBase, bgAlpha);

        // Content-area brushes: floor raised by wallpaper luminance when Smart Tint is active.
        // Cap at 0xCC (80%) so OS acrylic blur always bleeds through; without it, opacity=1.0
        // yields alpha=255 (fully opaque) and no glass shows in the content area.
        byte floor = luminanceMinAlpha ?? 0xA0;
        byte contentAlpha = enabled
            ? (byte)Math.Min(0xCC, Math.Max(floor, (int)Math.Round(opacity * 255)))
            : (byte)0xFF;
        SetBrush("BgPrimaryBrush",   bgSurface,   contentAlpha);
        SetBrush("BgSecondaryBrush", bgSecondary, contentAlpha);
        SetBrush("BgElevatedBrush",  bgElevated,  contentAlpha);
        SetBrush("BgHoverBrush",     bgHover,     contentAlpha);

        // Sidebar / nav glass layer.
        // Floor at 0xA0 (63%) so the sidebar is never so transparent that text
        // becomes unreadable over bright wallpapers (e.g. pure white desktop).
        byte glassAlpha = enabled
            ? (byte)Math.Max(0xA0, (int)Math.Round(opacity * 0xB2))
            : (byte)0xB2;
        SetBrush("GlassBgBrush", bgSidebar, glassAlpha);

        // Glass border — derive from sidebar base with low alpha; use light or dark tint.
        byte borderAlpha = enabled ? (byte)0x60 : (byte)0x40;
        Application.Current.Resources["GlassBorderBrush"] = isDark
            ? new SolidColorBrush(new Color(borderAlpha,
                (byte)Math.Min(255, bgSidebar.R + 0x20),
                (byte)Math.Min(255, bgSidebar.G + 0x20),
                (byte)Math.Min(255, bgSidebar.B + 0x20)))
            : new SolidColorBrush(new Color(borderAlpha,
                (byte)Math.Max(0, bgSidebar.R - 0x20),
                (byte)Math.Max(0, bgSidebar.G - 0x20),
                (byte)Math.Max(0, bgSidebar.B - 0x20)));

        // Overlay widget: always semi-transparent; use theme-appropriate base colour.
        // Floor is higher in light mode so labels remain legible over bright wallpapers.
        byte overlayFloor = luminanceMinAlpha ?? (isDark ? (byte)0x80 : (byte)0xA0);
        byte overlayAlpha = enabled
            ? (byte)Math.Max(overlayFloor, (int)Math.Round(opacity * 0xCC))
            : (byte)0xCC;
        (byte oR, byte oG, byte oB) = isDark ? ((byte)0x05, (byte)0x05, (byte)0x08)
                                              : ((byte)0xF0, (byte)0xF0, (byte)0xF5);
        Application.Current.Resources["OverlayBgBrush"] =
            new SolidColorBrush(new Color(overlayAlpha, oR, oG, oB));

        // Text readability: dark glow in dark mode, no effect in light mode
        // (light backgrounds don't need an outline to stay legible).
        Application.Current.Resources["GlassTextEffect"] = (enabled && isDark)
            ? (object)new DropShadowDirectionEffect
              {
                  BlurRadius  = 6,
                  Color       = Colors.Black,
                  ShadowDepth = 0,   // centred → expands in all directions = outline
                  Direction   = 315,
                  Opacity     = 1.0,
              }
            : null;
    }

    private static Color TryParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return Color.Parse(hex); }
        catch { return fallback; }
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to each R, G, B channel, clamped to [0, 255].
    /// Positive values lighten; negative values darken.
    /// </summary>
    private static Color AdjustBrightness(Color c, int amount) =>
        new Color(c.A,
            (byte)Math.Clamp(c.R + amount, 0, 255),
            (byte)Math.Clamp(c.G + amount, 0, 255),
            (byte)Math.Clamp(c.B + amount, 0, 255));

    /// <summary>
    /// Controls the opacity of ALL specular highlight overlays in MainWindow and OverlayWindow.
    /// Writes the <c>GlassSpecularOpacity</c> double resource consumed by
    /// <c>Opacity="{DynamicResource GlassSpecularOpacity}"</c> in XAML.
    /// </summary>
    private static void ApplySpecular(bool glassEnabled, bool specularEnabled, double intensity)
    {
        if (Application.Current is null) return;
        Application.Current.Resources["GlassSpecularOpacity"] =
            (glassEnabled && specularEnabled) ? Math.Clamp(intensity, 0, 1) : 0.0;
    }

    /// <summary>
    /// Sets the <see cref="WindowTransparencyLevel"/> hint on BOTH the main window
    /// and the overlay widget so the OS provides the correct backdrop blur type.
    /// </summary>
    private void ApplyBackdropMode(bool glassEnabled, string mode)
    {
        IReadOnlyList<WindowTransparencyLevel> hints = (!glassEnabled || mode == "None")
            ? [WindowTransparencyLevel.None]
            : mode switch
            {
                "Blur"   => [WindowTransparencyLevel.Blur,
                             WindowTransparencyLevel.None],
                "Mica"   => [WindowTransparencyLevel.Mica,
                             WindowTransparencyLevel.AcrylicBlur,
                             WindowTransparencyLevel.Blur,
                             WindowTransparencyLevel.None],
                _        => [WindowTransparencyLevel.AcrylicBlur,  // Acrylic (default)
                             WindowTransparencyLevel.Blur,
                             WindowTransparencyLevel.None],
            };

        if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window main)
        {
            main.TransparencyLevelHint = hints;
            // Gate shimmer timer: run only when glass is visually active
            if (main is NexusMonitor.UI.MainWindow nexusMain)
                nexusMain.SetGlassActive(glassEnabled && mode != "None");
        }

        if (_overlayWindow is not null)
        {
            // Overlay always needs Transparent as last-resort fallback (never None) so
            // the CornerRadius clips correctly — None would give a square opaque window.
            IReadOnlyList<WindowTransparencyLevel> overlayHints = (!glassEnabled || mode == "None")
                ? [WindowTransparencyLevel.Transparent]
                : mode switch
                {
                    "Blur" => [WindowTransparencyLevel.Blur,
                               WindowTransparencyLevel.Transparent],
                    "Mica" => [WindowTransparencyLevel.Mica,
                               WindowTransparencyLevel.AcrylicBlur,
                               WindowTransparencyLevel.Blur,
                               WindowTransparencyLevel.Transparent],
                    _      => [WindowTransparencyLevel.AcrylicBlur,
                               WindowTransparencyLevel.Blur,
                               WindowTransparencyLevel.Transparent],
                };
            _overlayWindow.TransparencyLevelHint = overlayHints;
        }
    }

    private static void ApplyAccentColor(string hex)
    {
        if (Application.Current is null) return;
        try
        {
            var c = Color.Parse(hex);
            Application.Current.Resources["AccentBlueBrush"]      = new SolidColorBrush(c);
            Application.Current.Resources["AccentBlueDimBrush"]   =
                new SolidColorBrush(new Color(0x1A, c.R, c.G, c.B));
            Application.Current.Resources["AccentBlueHoverBrush"] =
                new SolidColorBrush(new Color(c.A,
                    (byte)Math.Min(255, c.R + 25),
                    (byte)Math.Min(255, c.G + 25),
                    (byte)Math.Min(255, c.B + 25)));
        }
        catch { /* ignore invalid hex */ }
    }

    /// <summary>
    /// Writes <c>TextAccentBrush</c>.  If <paramref name="textHex"/> is empty the
    /// brush derives from the primary accent at 90% opacity for a softer text look.
    /// </summary>
    private static void ApplyTextAccent(string accentHex, string textHex)
    {
        if (Application.Current is null) return;
        try
        {
            Color base_ = Color.Parse(accentHex);
            Color c = string.IsNullOrWhiteSpace(textHex)
                ? new Color(0xE6, base_.R, base_.G, base_.B)  // 90% opacity accent
                : Color.Parse(textHex);
            Application.Current.Resources["TextAccentBrush"] = new SolidColorBrush(c);
        }
        catch { /* ignore invalid hex */ }
    }

    /// <summary>
    /// Applies <paramref name="family"/> as the <c>FontFamily</c> on every open
    /// window.  An empty string or "(System Default)" restores the system default.
    /// <paramref name="multiplier"/> scales the base font size (1.0 = default).
    /// </summary>
    private void ApplyFont(string family, double multiplier = 1.0)
    {
        if (Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop) return;

        // Resolve bundled font URI if this is a bundled font name
        string resolvedFamily = family;
        if (!string.IsNullOrWhiteSpace(family) && family != "(System Default)"
            && _bundledFontUris.TryGetValue(family, out var uri))
            resolvedFamily = uri;

        Avalonia.Media.FontFamily ff;
        try
        {
            ff = string.IsNullOrWhiteSpace(resolvedFamily) || resolvedFamily == "(System Default)"
                ? Avalonia.Media.FontFamily.Default
                : new Avalonia.Media.FontFamily(resolvedFamily);
        }
        catch
        {
            ff = Avalonia.Media.FontFamily.Default;
        }

        const double BaseFontSize = 15.0; // matches NxFont13 token (bumped from 14→15)
        double fontSize = BaseFontSize * Math.Clamp(multiplier, 0.5, 3.0);
        try
        {
            if (desktop.MainWindow is Window main) { main.FontFamily = ff; main.FontSize = fontSize; }
            if (_overlayWindow      is Window ow)  { ow.FontFamily   = ff; ow.FontSize   = fontSize; }
        }
        catch { /* ignore font application failures — leave current font in place */ }

        // Scale all NxFont* and FontSize* resource tokens so {DynamicResource} bindings update instantly.
        double m = Math.Clamp(multiplier, 0.5, 3.0);
        (string Key, double Base)[] fontTokens =
        [
            ("NxFont10", 12), ("NxFont11", 13), ("NxFont12", 14), ("NxFont13", 15),
            ("NxFont14", 16), ("NxFont16", 18), ("NxFont18", 21), ("NxFont24", 30),
            ("NxFontSm", 13), ("NxFontBase", 14), ("NxFontMd", 15),
            ("FontSizeXS", 12), ("FontSizeSM", 13), ("FontSizeMD", 14),
            ("FontSizeBase", 15), ("FontSizeLG", 17), ("FontSizeXL", 19),
            ("FontSize2XL", 23), ("FontSize3XL", 30),
        ];
        foreach (var (key, baseVal) in fontTokens)
            Application.Current!.Resources[key] = Math.Round(baseVal * m, 1);
    }

    private static void SetBrush(string key, Color baseColor, byte alpha)
    {
        if (Application.Current is null) return;
        Application.Current.Resources[key] =
            new SolidColorBrush(new Color(alpha, baseColor.R, baseColor.G, baseColor.B));
    }

    // ── Process Preferences commands ─────────────────────────────────────────

    [RelayCommand]
    private void DeletePreference(ProcessPreference pref)
    {
        if (_preferenceStore is null) return;
        _preferenceStore.Delete(pref.ExeName);
        SavedPreferences.Remove(pref);
    }

    [RelayCommand]
    private void ClearAllPreferences()
    {
        if (_preferenceStore is null) return;
        foreach (var p in SavedPreferences.ToList())
            _preferenceStore.Delete(p.ExeName);
        SavedPreferences.Clear();
    }

    [RelayCommand]
    private void RefreshPreferences()
    {
        if (_preferenceStore is null) return;
        SavedPreferences.Clear();
        foreach (var p in _preferenceStore.GetAll())
            SavedPreferences.Add(p);
    }

    public void Dispose()
    {
        _osThemeCleanup?.Invoke();
        _glassAdaptive.LuminanceChanged -= OnLuminanceChanged;
        _updateSubscription?.Dispose();
    }
}
