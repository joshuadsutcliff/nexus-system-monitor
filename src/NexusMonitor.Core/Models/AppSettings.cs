using NexusMonitor.Core.Alerts;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Rules;

namespace NexusMonitor.Core.Models;

public class AppSettings
{
    public bool   IsDarkTheme        { get; set; } = true;   // kept for migration only
    /// <summary>"System" | "Dark" | "Light"</summary>
    public string ThemeMode          { get; set; } = "System";

    // Crystal Glass
    public bool   IsGlassEnabled     { get; set; } = true;
    public double GlassOpacity       { get; set; } = 0.80;   // 0 = fully transparent, 1 = fully opaque
    public string BackdropBlurMode   { get; set; } = "Acrylic"; // None | Blur | Acrylic | Mica
    public bool   IsSpecularEnabled  { get; set; } = true;
    public double SpecularIntensity  { get; set; } = 0.55;   // default raised for visibility

    // Accent
    public string AccentColorHex     { get; set; } = "#0A84FF";
    public string TextAccentColorHex { get; set; } = "";     // "" = derive from AccentColorHex

    // Custom surface colors — "" = use built-in theme defaults
    public string CustomWindowBgHex  { get; set; } = "";   // BgBaseBrush  (window chrome)
    public string CustomSurfaceBgHex { get; set; } = "";   // BgPrimary/Secondary/Elevated (cards/panels)
    public string CustomSidebarBgHex { get; set; } = "";   // GlassBgBrush (left sidebar / nav)

    // Typography
    public string FontFamily         { get; set; } = "";     // "" = system default
    public double FontSizeMultiplier { get; set; } = 1.0;   // 1.0 = default size

    // Performance
    public int    UpdateIntervalMs   { get; set; } = 3000;   // 500 | 1000 | 2000 | 3000 | 5000

    // Tray / window close behaviour
    /// <summary>"" = always ask, "Tray" = minimize to tray, "Exit" = close application.</summary>
    public string CloseAction          { get; set; } = "";
    public bool   HideWidgetOnMinimize { get; set; } = false;

    // Other
    public bool   ShowOverlayWidget  { get; set; } = false;

    // Notifications
    public bool   DesktopNotificationsEnabled  { get; set; } = false;
    public bool   AnomalyNotificationsEnabled  { get; set; } = false;

    // ProBalance
    public bool          ProBalanceEnabled      { get; set; } = false;
    public double        ProBalanceCpuThreshold { get; set; } = 80.0;
    public List<string>  ProBalanceExclusions   { get; set; } = new();

    // Rules
    public List<ProcessRule> Rules { get; set; } = new();

    // Gaming Mode
    public bool          GamingModeEnabled     { get; set; } = false;
    public string        GamingModeGameProcess { get; set; } = "";
    public List<string>  GamingModeExclusions  { get; set; } = new();

    // Alerts
    public List<AlertRule> AlertRules { get; set; } = new();

    // Sidebar navigation order — empty = default order
    public List<string> NavOrder { get; set; } = new();

    // Metrics persistence (Phase 11)
    public bool MetricsEnabled           { get; set; } = false;
    public int  MetricsTopNProcesses     { get; set; } = 15;
    public bool MetricsRecordNetwork     { get; set; } = true;
    public int  MetricsRawRetentionHours { get; set; } = 1;
    public int  MetricsRollup1mDays      { get; set; } = 7;
    public int  MetricsRollup5mDays      { get; set; } = 30;
    public int  MetricsRollup1hDays      { get; set; } = 365;

    // Telemetry — Prometheus endpoint (Phase 14)
    public bool PrometheusEnabled { get; set; } = false;
    public int  PrometheusPort    { get; set; } = 9182;

    // Smart Glass Tint (Phase 4 enhancements)
    public bool SmartTintEnabled { get; set; } = false;

    // Dashboard (Phase 1)
    public bool DashboardEnabled { get; set; } = true;

    // Theme Presets
    public string ActiveThemePresetId { get; set; } = "";  // "" = custom/none

    // Performance Profiles (Phase 17)
    public List<PerformanceProfile> PerformanceProfiles { get; set; } = new();
    public Guid? ActiveProfileId { get; set; }

    // Anomaly Detection (Phase 13)
    public bool   AnomalyDetectionEnabled     { get; set; } = false;
    /// <summary>"Low", "Medium", or "High" — maps to sigma preset in AnomalyDetectionConfig.</summary>
    public string AnomalySensitivity          { get; set; } = "Low";
    public int    AnomalyCooldownSeconds      { get; set; } = 60;
    public int    AnomalyNewConnGracePeriodSec{ get; set; } = 120;
    public int    MetricsEventsRetentionDays  { get; set; } = 90;

    // Foreground Boosting (Phase 18)
    public bool          ForegroundBoostEnabled    { get; set; } = false;
    public List<string>  ForegroundBoostExclusions { get; set; } = new();

    // IdleSaver (Phase 18)
    public bool          IdleSaverEnabled           { get; set; } = false;
    public double        IdleSaverCpuThreshold      { get; set; } = 1.0;
    public int           IdleSaverIdleTicksRequired { get; set; } = 3;
    public List<string>  IdleSaverExclusions        { get; set; } = new();
    public bool          IdleSaverUseEfficiencyMode { get; set; } = true;

    // SmartTrim Automation (Phase 18)
    public bool   SmartTrimEnabled         { get; set; } = false;
    public int    SmartTrimIntervalSeconds { get; set; } = 60;
    public double SmartTrimPressurePercent { get; set; } = 20.0;
    public int    SmartTrimMinWorkingSetMB { get; set; } = 100;

    // CPU Limiter (Phase 18)
    public bool                  CpuLimiterEnabled { get; set; } = false;
    public List<CpuLimiterRule>  CpuLimiterRules   { get; set; } = new();

    // Instance Balancer (Phase 18)
    public bool                       InstanceBalancerEnabled { get; set; } = false;
    public List<InstanceBalancerRule> InstanceBalancerRules   { get; set; } = new();

    // System tray / startup behaviour (Phase 20 feature request FR1/FR2)
    /// <summary>When true, minimizing/closing goes to system tray. When false, no tray icon is shown.</summary>
    public bool   MinimizeToTray { get; set; } = true;
    /// <summary>Tab label to navigate to on launch. Empty string = default (Dashboard).</summary>
    public string DefaultTab     { get; set; } = "";

    // Memory Leak Detection
    public bool   MemoryLeakDetectionEnabled   { get; set; } = false;
    public int    LeakObservationWindowMinutes { get; set; } = 10;
    public double LeakRateThresholdMbPerHour   { get; set; } = 50;
    public double HandleLeakThresholdPerHour   { get; set; } = 100;

    // Quiet Hours (Phase 22c)
    public bool            QuietHoursEnabled { get; set; } = false;
    public string          QuietHoursStart   { get; set; } = "22:00";  // HH:mm 24-hour
    public string          QuietHoursEnd     { get; set; } = "07:00";  // HH:mm 24-hour
    public List<DayOfWeek> QuietHoursDays    { get; set; } = new();    // empty = every day

    // Webhook Notifications (Phase 23)
    public bool         WebhookEnabled { get; set; } = false;
    public string       WebhookUrl     { get; set; } = "";
    public string       WebhookSecret  { get; set; } = "";
    /// <summary>
    /// Which event types to forward: "alerts", "anomalies".
    /// Filtering is applied by App.axaml.cs Rx wiring — WebhookNotificationService
    /// itself forwards all payloads unconditionally when called.
    /// </summary>
    public List<string> WebhookEvents  { get; set; } = new();

    // Resource Predictions (Phase 23)
    public bool PredictionsEnabled { get; set; } = false;

    // Update Checker
    /// <summary>When true, UpdateCheckService periodically polls GitHub for newer releases. Never auto-downloads/installs.</summary>
    public bool CheckForUpdates { get; set; } = true;

    // Page Engine (Phase 2)
    /// <summary>Retired (Phase 7): the page engine is now the unconditional Dashboard renderer.
    /// Kept for settings-file migration only; not read anywhere.</summary>
    public bool EnablePageEngine { get; set; } = false;

    // ── Session persistence ─────────────────────────────────────────────────────
    public string LastActiveTab    { get; set; } = string.Empty;
    public double LastWindowWidth  { get; set; } = 0;
    public double LastWindowHeight { get; set; } = 0;
    public int    LastWindowX      { get; set; } = -1;
    public int    LastWindowY      { get; set; } = -1;
    public string LastWindowState  { get; set; } = "Normal";
}
