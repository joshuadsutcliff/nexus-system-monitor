using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Alerts;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Pages;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Storage;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Text;
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
    private readonly WorkspaceProfileStore        _profileStore;
    private readonly ProcessPreferenceStore?      _preferenceStore;
    private readonly WebhookNotificationService              _webhookService;
    private readonly PredictionService?                      _predictionService;
    private readonly HealthSnapshotPersistenceService?       _healthSnapshotService;
    private readonly EventMonitorService?                    _eventMonitorService;
    private readonly UpdateCheckService?                     _updateCheckService;
    private readonly IInAppNotificationService?              _notificationService;
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
        WorkspaceProfileStore        profileStore,
        WebhookNotificationService   webhookService,
        PredictionService?                      predictionService = null,
        ProcessPreferenceStore?                 preferenceStore = null,
        HealthSnapshotPersistenceService?       healthSnapshotService = null,
        EventMonitorService?                    eventMonitorService = null,
        UpdateCheckService?                     updateCheckService = null,
        IInAppNotificationService?               notificationService = null)
    {
        Title             = "Settings";
        _settings         = settings;
        _exporter         = exporter;
        _anomalyService   = anomalyService;
        _anomalyConfig    = anomalyConfig;
        _glassAdaptive    = glassAdaptive;
        _presetService    = presetService;
        _profileStore     = profileStore;
        _webhookService        = webhookService;
        _predictionService     = predictionService;
        _preferenceStore       = preferenceStore;
        _healthSnapshotService = healthSnapshotService;
        _eventMonitorService   = eventMonitorService;
        _updateCheckService    = updateCheckService;
        _notificationService   = notificationService;

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

        // ── Workspace profiles (Phase 5) ───────────────────────────────────────
        RefreshWorkspaceProfileNames();
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

    // ── Workspace profiles (Phase 5) ───────────────────────────────────────────

    /// <summary>All saved workspace profile names (file-system scan via the store), refreshed
    /// after every switch/save/delete. ComboBox <c>ItemsSource</c> in the Settings "Workspace
    /// Profiles" card.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> WorkspaceProfileNames { get; } = new();

    private bool _suppressWorkspaceProfileCallback;
    private string? _selectedWorkspaceProfileName;

    /// <summary>
    /// Bound to the "Workspace Profiles" ComboBox's <c>SelectedItem</c>. Setting this — via user
    /// selection — switches the active workspace profile immediately (mirrors
    /// <see cref="SelectedPresetIndex"/>'s manual-property-drives-apply structure).
    /// </summary>
    public string? SelectedWorkspaceProfileName
    {
        get => _selectedWorkspaceProfileName;
        set
        {
            if (!SetProperty(ref _selectedWorkspaceProfileName, value)) return;
            if (!_suppressWorkspaceProfileCallback && !string.IsNullOrEmpty(value))
                SwitchWorkspaceProfile(value);
            // Fired after SwitchWorkspaceProfile (not before) so it reflects the post-switch
            // ActiveProfileName rather than a transient mid-switch mismatch.
            OnPropertyChanged(nameof(CanDeleteActiveWorkspaceProfile));
        }
    }

    /// <summary>Text entered in the "Save current as…" box; cleared after a successful save.</summary>
    [ObservableProperty] private string _newWorkspaceProfileName = "";

    /// <summary>True when the ACTIVE workspace profile is not "Default". Delete now targets the
    /// ACTIVE profile — <see cref="DeleteSelectedWorkspaceProfile"/> switches to Default first, then
    /// removes the file it just vacated — so Default itself must never be deletable (there would be
    /// nothing left to switch to). Compared case-insensitively: profile files live on case-insensitive
    /// filesystems (macOS/Windows), where "default" and "Default" are the same file.</summary>
    public bool CanDeleteActiveWorkspaceProfile =>
        !string.Equals(_profileStore.ActiveProfileName, "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-scans the store for saved profile names and re-syncs the selected/active name.
    /// Safe to call after any SYNCHRONOUS store mutation (SetActive, Delete); NOT called directly
    /// after <see cref="WorkspaceProfileStore.Save"/>, which is debounced — see
    /// <see cref="SaveCurrentWorkspaceProfile"/>.</summary>
    private void RefreshWorkspaceProfileNames()
    {
        WorkspaceProfileNames.Clear();
        foreach (var name in _profileStore.ListProfiles())
            WorkspaceProfileNames.Add(name);

        _suppressWorkspaceProfileCallback = true;
        SelectedWorkspaceProfileName = _profileStore.ActiveProfileName;
        _suppressWorkspaceProfileCallback = false;
    }

    /// <summary>
    /// Switches the active workspace profile: persists the new active pointer, applies its
    /// bundled appearance, then broadcasts <see cref="WorkspaceProfileSwitchedMessage"/> so
    /// DashboardViewModel reloads its layout from the same profile. Restart-free.
    /// Appearance handling (exactly one applies):
    /// <list type="bullet">
    /// <item>Embedded <see cref="ThemeSnapshot"/>: bulk-set the 14 appearance VM properties under
    /// <see cref="_suppressApply"/> then <see cref="ApplyAllVisuals"/> + <see cref="UpdateSurfaceSwatches"/>
    /// — mirrors <see cref="ApplyPreset"/>'s structure exactly.</item>
    /// <item>Named <see cref="ThemeRef.PresetId"/>: routed through the existing <see cref="ApplyPreset"/>.</item>
    /// <item>Neither set (neutral): appearance is left untouched.</item>
    /// </list>
    /// <para>
    /// Page engine Phase 6 (pop-out windows): <see cref="WorkspaceProfileSwitchingMessage"/> is sent
    /// FIRST, before <see cref="WorkspaceProfileStore.SetActive"/> flips the active pointer below.
    /// DashboardViewModel owns the pop-out coordinator (this VM deliberately doesn't reach into it),
    /// and its synchronous handler for that message persists every open pop-out's geometry into the
    /// still-active OUTGOING profile and closes its windows — sending it any later, after
    /// <c>SetActive</c>, would persist that same geometry into the newly-active INCOMING profile
    /// instead, corrupting it with the outgoing page's widget layout.
    /// </para>
    /// </summary>
    private void SwitchWorkspaceProfile(string name)
    {
        WeakReferenceMessenger.Default.Send(new WorkspaceProfileSwitchingMessage(name));

        _profileStore.SetActive(name);
        var profile = _profileStore.LoadActive();

        if (profile.Theme.Snapshot is { } snap)
        {
            _suppressApply = true;
            try
            {
                var modeIdx = Array.IndexOf(_themeModeValues, snap.ThemeMode);
                ThemeModeIndex      = modeIdx < 0 ? 0 : modeIdx;

                AccentColorHex      = snap.AccentColorHex;
                TextAccentColorHex  = snap.TextAccentColorHex;
                CustomWindowBgHex   = snap.CustomWindowBgHex;
                CustomSurfaceBgHex  = snap.CustomSurfaceBgHex;
                CustomSidebarBgHex  = snap.CustomSidebarBgHex;
                IsGlassEnabled      = snap.IsGlassEnabled;
                GlassOpacity        = snap.GlassOpacity;
                BackdropBlurMode    = snap.BackdropBlurMode;
                IsSpecularEnabled   = snap.IsSpecularEnabled;
                SpecularIntensity   = snap.SpecularIntensity;
                FontFamily          = snap.FontFamily;
                FontSizeMultiplier  = snap.FontSizeMultiplier;
                SmartTintEnabled    = snap.SmartTintEnabled;
            }
            finally
            {
                _suppressApply = false;
            }

            // A raw snapshot isn't a named preset — mark custom (same as any manual edit would),
            // so the theme-preset combo and surface swatches don't keep showing a stale, unrelated
            // preset's identity/palette against these just-applied colors.
            _settings.Current.ActiveThemePresetId = "";
            _settings.Save();

            ApplyAllVisuals();
            UpdateSurfaceSwatches();
        }
        else if (!string.IsNullOrEmpty(profile.Theme.PresetId))
        {
            var preset = _availablePresets.FirstOrDefault(p => p.Id == profile.Theme.PresetId);
            if (preset is not null)
                ApplyPreset(preset); // persists appearance into _settings.Current + Save, same as here
        }
        // else: neutral (Snapshot and PresetId both null) — appearance left untouched.

        WeakReferenceMessenger.Default.Send(new WorkspaceProfileSwitchedMessage(name));
    }

    [RelayCommand]
    private void SaveCurrentWorkspaceProfile()
    {
        var typedName = NewWorkspaceProfileName?.Trim();
        if (string.IsNullOrWhiteSpace(typedName)) return;

        // Run the typed name through the same sanitizer ImportWorkspaceProfile uses on untrusted
        // file content BEFORE the case-insensitive lookup — a user-typed name containing anything
        // outside [A-Za-z0-9 _-] would otherwise reach WorkspaceProfileStore.Save() unsanitized and
        // throw ArgumentException, which used to be swallowed as a silent no-op below.
        var sanitizedTypedName = SanitizeProfileName(typedName);

        // A typed name that collides case-insensitively with an existing profile must UPDATE that
        // profile in place, not silently clobber its file while the UI treats them as distinct
        // (profile files live on case-insensitive filesystems, so "test" and "Test" are the same
        // file on disk). Reusing the EXISTING stored casing keeps WorkspaceProfileStore.Save
        // targeting the same file and keeps the in-memory list at a single entry.
        var existingMatch = _profileStore.ListProfiles()
            .FirstOrDefault(p => string.Equals(p, sanitizedTypedName, StringComparison.OrdinalIgnoreCase));
        var name = existingMatch ?? sanitizedTypedName;

        var snapshot = new ThemeSnapshot(
            ThemeMode:           _themeModeValues[Math.Clamp(ThemeModeIndex, 0, _themeModeValues.Length - 1)],
            AccentColorHex:      AccentColorHex,
            TextAccentColorHex:  TextAccentColorHex,
            CustomWindowBgHex:   CustomWindowBgHex,
            CustomSurfaceBgHex:  CustomSurfaceBgHex,
            CustomSidebarBgHex:  CustomSidebarBgHex,
            IsGlassEnabled:      IsGlassEnabled,
            GlassOpacity:        GlassOpacity,
            BackdropBlurMode:    BackdropBlurMode,
            IsSpecularEnabled:   IsSpecularEnabled,
            SpecularIntensity:   SpecularIntensity,
            FontFamily:          FontFamily,
            FontSizeMultiplier:  FontSizeMultiplier,
            SmartTintEnabled:    SmartTintEnabled);

        // Pages come from the ACTIVE profile (not the on-screen EnginePage instance — SettingsViewModel
        // has no reference to it), per the plan: "the ACTIVE profile's pages via store.LoadActive().Pages".
        var pages = _profileStore.LoadActive().Pages;
        var profile = new WorkspaceProfile(name, pages, new ThemeRef(Snapshot: snapshot), Array.Empty<PopOutState>());

        // Belt-and-suspenders: sanitizedTypedName should always satisfy WorkspaceProfileStore's
        // charset now, so this should be unreachable — but if it ever isn't, surface a toast
        // instead of silently no-op'ing (the prior behavior, which left users wondering why
        // nothing happened).
        try { _profileStore.Save(profile); }
        catch (ArgumentException)
        {
            // Developer-facing exception detail intentionally not shown to the user.
            _notificationService?.Show(new InAppNotification(
                Title:       "Profile Not Saved",
                Body:        "That name can't be used. Try a different one.",
                Severity:    InAppSeverity.Warning,
                AutoDismiss: TimeSpan.FromSeconds(6)));
            return;
        }

        NewWorkspaceProfileName = "";

        // Save() is debounced (250 ms) — a disk rescan here would race the pending write and miss
        // the new profile. Add it to the in-memory list directly instead of re-scanning.
        // Case-insensitive: when existingMatch is set, `name` is already that exact stored casing,
        // so this Contains is true and correctly skips inserting a second, misleading row — the
        // update case must never grow the list past its existing single entry for this profile.
        if (!WorkspaceProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var insertAt = 0;
            while (insertAt < WorkspaceProfileNames.Count &&
                   string.CompareOrdinal(WorkspaceProfileNames[insertAt], name) < 0)
                insertAt++;
            WorkspaceProfileNames.Insert(insertAt, name);
            OnPropertyChanged(nameof(CanDeleteActiveWorkspaceProfile));
        }

        _notificationService?.Show(existingMatch is not null
            ? new InAppNotification(
                Title:       "Profile Updated",
                Body:        $"Updated profile '{name}'.",
                Severity:    InAppSeverity.Info,
                AutoDismiss: TimeSpan.FromSeconds(5))
            : new InAppNotification(
                Title:       "Profile Saved",
                Body:        $"Saved profile '{name}'.",
                Severity:    InAppSeverity.Info,
                AutoDismiss: TimeSpan.FromSeconds(5)));
    }

    /// <summary>Deletes the CURRENTLY ACTIVE workspace profile: switches to "Default" first (the
    /// full existing <see cref="SwitchWorkspaceProfile"/> path — theme apply, layout-reload message,
    /// SetActive), THEN removes the now-vacated profile's file. This order means
    /// <see cref="CanDeleteActiveWorkspaceProfile"/> is already false (active == Default) before the
    /// delete happens — there is no path where this can target Default itself.</summary>
    [RelayCommand]
    private void DeleteSelectedWorkspaceProfile()
    {
        if (!CanDeleteActiveWorkspaceProfile) return;

        var doomed = _profileStore.ActiveProfileName;

        SwitchWorkspaceProfile("Default");

        try { _profileStore.Delete(doomed); }
        catch (InvalidOperationException) { return; } // defensive — the switch-first order already guards this

        // Delete() is synchronous — remove the doomed name from the in-memory list directly rather
        // than rescanning disk. Case-insensitive: profile files live on case-insensitive filesystems.
        var match = WorkspaceProfileNames.FirstOrDefault(n => string.Equals(n, doomed, StringComparison.OrdinalIgnoreCase));
        if (match is not null) WorkspaceProfileNames.Remove(match);

        // Sync the picker to the new active selection ("Default") without re-triggering another switch.
        _suppressWorkspaceProfileCallback = true;
        SelectedWorkspaceProfileName = _profileStore.ActiveProfileName;
        _suppressWorkspaceProfileCallback = false;

        _notificationService?.Show(new InAppNotification(
            Title:       "Profile Deleted",
            Body:        $"Deleted profile '{doomed}' — switched to Default.",
            Severity:    InAppSeverity.Info,
            AutoDismiss: TimeSpan.FromSeconds(5)));
    }

    // ── Workspace profile export / import ──────────────────────────────────────

    /// <summary>Exports the selected profile as-is (full pages + theme) to a user-chosen
    /// <c>.nexusprofile</c> file. No-op when nothing is selected or the selection no longer
    /// resolves to a saved profile.</summary>
    [RelayCommand]
    private async Task ExportWorkspaceProfile()
    {
        if (_selectedWorkspaceProfileName is not { } name) return;

        // Load() (not the in-memory name list) so a still-debouncing Save for this exact
        // profile is read rather than a stale on-disk copy — same read-your-own-writes
        // guarantee SwitchWorkspaceProfile relies on.
        var profile = _profileStore.Load(name);
        if (profile is null) return;

        await ExportProfileToFileAsync(profile, profile.Name);
    }

    /// <summary>Exports a theme-only bundle derived from the selected profile: same name suffixed
    /// " theme" (no parens — parens fall outside <see cref="WorkspaceProfileStore"/>'s
    /// <c>[A-Za-z0-9 _-]</c> charset and would sanitize to "-theme-" on import), an EMPTY Pages
    /// dictionary, and the source's ThemeRef (+ empty PopOutStates). Documented convention:
    /// importing/switching to a pages-empty profile leaves the current dashboard layout untouched
    /// and applies only the appearance.</summary>
    [RelayCommand]
    private async Task ExportThemeOnlyProfile()
    {
        if (_selectedWorkspaceProfileName is not { } name) return;

        var source = _profileStore.Load(name);
        if (source is null) return;

        var themeOnly = new WorkspaceProfile(
            $"{source.Name} theme",
            new Dictionary<string, PageLayout>(),
            source.Theme,
            Array.Empty<PopOutState>());

        await ExportProfileToFileAsync(themeOnly, themeOnly.Name);
    }

    /// <summary>Shared Save-dialog + write path for both export commands. Resolving the main
    /// window's <see cref="IStorageProvider"/> is a silent no-op when unavailable (headless hosts).
    /// Everything from the picker call through the write is wrapped in one try/catch — mirroring
    /// <c>ProcessesViewModel.CreateDumpFile</c>'s single-catch shape — so a cancelled dialog still
    /// returns silently (<c>file is null</c>), but any real failure (no dialog support, a picker
    /// exception, or the write itself failing) is toasted via <see cref="IInAppNotificationService"/>
    /// — the same notification path <see cref="ImportWorkspaceProfile"/> uses — instead of vanishing
    /// into an unobserved task.</summary>
    private async Task ExportProfileToFileAsync(WorkspaceProfile profile, string suggestedName)
    {
        var sp = GetStorageProvider();
        if (sp is null) return;

        try
        {
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export Workspace Profile",
                SuggestedFileName = $"{suggestedName}.nexusprofile",
                DefaultExtension  = "nexusprofile",
                FileTypeChoices   =
                [
                    new FilePickerFileType("Nexus Profile") { Patterns = ["*.nexusprofile"] },
                    new FilePickerFileType("All Files")     { Patterns = ["*.*"] },
                ],
            });

            if (file is null) return; // user cancelled

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(WorkspaceProfileSerializer.Serialize(profile));
        }
        catch (Exception ex)
        {
            ToastExportError($"Could not export profile: {ex.Message}");
        }
    }

    /// <summary>Pushes an export-failure toast via the in-app notification channel — the same path
    /// <see cref="ToastImportError"/> uses. A no-op when the service wasn't injected (e.g. a
    /// lightweight test host) rather than throwing.</summary>
    private void ToastExportError(string message) =>
        _notificationService?.Show(new InAppNotification(
            Title:       "Profile Export Failed",
            Body:        message,
            Severity:    InAppSeverity.Warning,
            AutoDismiss: TimeSpan.FromSeconds(6)));

    /// <summary>Opens a file picker, reads the chosen <c>.nexusprofile</c>/<c>.json</c> file, and
    /// imports it as a NEW profile (never overwrites, never activates — the user switches
    /// explicitly via the profile picker). On any failure (unreadable file, malformed/hostile
    /// JSON, invalid schema version) the error is toasted via <see cref="IInAppNotificationService"/>
    /// instead of throwing.</summary>
    [RelayCommand]
    private async Task ImportWorkspaceProfile()
    {
        var sp = GetStorageProvider();
        if (sp is null) return;

        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Import Workspace Profile",
                AllowMultiple  = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Nexus Profile") { Patterns = ["*.nexusprofile", "*.json"] },
                    new FilePickerFileType("All Files")     { Patterns = ["*.*"] },
                ],
            });
        }
        catch (Exception) { return; } // platform has no file-dialog support

        if (files.Count == 0) return; // user cancelled

        string json;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            json = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            ToastImportError($"Could not read profile file: {ex.Message}");
            return;
        }

        if (!WorkspaceProfileSerializer.TryDeserialize(json, out var profile, out var error) || profile is null)
        {
            ToastImportError(error ?? "The selected file is not a valid workspace profile.");
            return;
        }

        // Imported names are untrusted (arbitrary file content): sanitize BEFORE dedupe, since
        // WorkspaceProfileStore.Save throws ArgumentException on anything outside [A-Za-z0-9 _-].
        var sanitizedName = SanitizeProfileName(profile.Name);
        var finalName = DedupeProfileName(sanitizedName, _profileStore.ListProfiles());

        var imported = profile with { Name = finalName };
        try { _profileStore.Save(imported); }
        catch (ArgumentException ex)
        {
            ToastImportError($"Could not import profile: {ex.Message}");
            return;
        }

        // Save() is debounced (250 ms) — a disk rescan here would race the pending write and miss
        // the new profile. Mirror SaveCurrentWorkspaceProfile's in-memory insert instead.
        // Case-insensitive for the same reason DedupeProfileName is: profile files live on
        // case-insensitive filesystems, so this must not treat "test" and "Test" as distinct.
        if (!WorkspaceProfileNames.Contains(finalName, StringComparer.OrdinalIgnoreCase))
        {
            var insertAt = 0;
            while (insertAt < WorkspaceProfileNames.Count &&
                   string.CompareOrdinal(WorkspaceProfileNames[insertAt], finalName) < 0)
                insertAt++;
            WorkspaceProfileNames.Insert(insertAt, finalName);
            OnPropertyChanged(nameof(CanDeleteActiveWorkspaceProfile));
        }
        // NEVER auto-activate — imported profile sits alongside existing ones until the user
        // explicitly selects it in the profile picker.
    }

    /// <summary>Pushes an import-failure toast via the in-app notification channel (same pattern as
    /// the update-checker's "Update Available" toast in App.axaml.cs). A no-op when the service
    /// wasn't injected (e.g. a lightweight test host) rather than throwing.</summary>
    private void ToastImportError(string message) =>
        _notificationService?.Show(new InAppNotification(
            Title:       "Profile Import Failed",
            Body:        message,
            Severity:    InAppSeverity.Warning,
            AutoDismiss: TimeSpan.FromSeconds(6)));

    /// <summary>Replaces any character outside <c>[A-Za-z0-9 _-]</c> — the exact charset
    /// <see cref="WorkspaceProfileStore.Save"/> enforces — with '-'. Imported profile names come
    /// from arbitrary file content and must never reach the store un-sanitized. Falls back to
    /// "Imported Profile" when nothing but disallowed/whitespace characters remain. Result is then
    /// capped at 64 characters (re-trimmed after truncation, since cutting mid-name can leave
    /// trailing whitespace, and re-checked for empty, since trimming an all-whitespace tail can
    /// leave nothing) — untrusted names must never reach the store/UI unbounded in length.</summary>
    private static string SanitizeProfileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Imported Profile";

        var sanitized = new string(name.Select(c =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is ' ' or '_' or '-'
                ? c
                : '-').ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized)) return "Imported Profile";

        const int MaxLength = 64;
        if (sanitized.Length > MaxLength)
            sanitized = sanitized[..MaxLength].Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "Imported Profile" : sanitized;
    }

    /// <summary>Appends " (2)", " (3)"… until the candidate name is absent from
    /// <paramref name="existing"/> — import-as-new never overwrites a saved profile. Compared
    /// case-insensitively: profile files live on case-insensitive filesystems (macOS/Windows), so an
    /// imported name differing only by case (e.g. "test" vs. an existing "Test") would otherwise pass
    /// this check and then silently clobber the existing profile's file on disk.</summary>
    private static string DedupeProfileName(string name, IReadOnlyList<string> existing)
    {
        if (!existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;

        var n = 2;
        string candidate;
        do { candidate = $"{name} ({n++})"; }
        while (existing.Contains(candidate, StringComparer.OrdinalIgnoreCase));
        return candidate;
    }

    /// <summary>Resolves the main window's <see cref="IStorageProvider"/> for file dialogs —
    /// codebase idiom shared with ProcessesViewModel.CreateDumpFile: the desktop lifetime's
    /// MainWindow, not a View-supplied TopLevel (SettingsViewModel has no control reference).
    /// Null on non-desktop lifetimes or before the main window exists (e.g. headless test hosts).</summary>
    private static IStorageProvider? GetStorageProvider() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.StorageProvider;

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
