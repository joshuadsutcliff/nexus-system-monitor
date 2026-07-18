using NexusMonitor.Core.Alerts;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Rules;

namespace NexusMonitor.Core.Models;

public class AppSettings
{
    public bool   IsDarkTheme        { get; set; } = true;   // kept for migration only
    /// <summary>"System" | "Dark" | "Light"</summary>
    public string ThemeMode          { get; set; } = "System";

    // Crystal Glass — quiet-defaults ruling (owner-approved 2026-07-11): fresh installs get a
    // restrained, Apple-grade first impression (decorative glass is opt-in, not opt-out). See
    // SettingsService.MigrateQuietDefaultsGap for the existing-user protection this depends on —
    // Save() always serializes the FULL AppSettings object, so any settings.json already written
    // by a version that had these keys already pins the user's real (possibly old-default) value
    // explicitly; only a settings.json that PREDATES a key entirely needs the migration guard.
    public bool   IsGlassEnabled     { get; set; } = false;
    public double GlassOpacity       { get; set; } = 0.80;   // 0 = fully transparent, 1 = fully opaque; inert while IsGlassEnabled is false
    public string BackdropBlurMode   { get; set; } = "None"; // None | Blur | Acrylic | Mica
    public bool   IsSpecularEnabled  { get; set; } = false;
    public double SpecularIntensity  { get; set; } = 0.55;   // inert while IsSpecularEnabled is false

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

    // AutoBalance
    public bool          AutoBalanceEnabled      { get; set; } = false;
    public double        AutoBalanceCpuThreshold { get; set; } = 80.0;
    public List<string>  AutoBalanceExclusions   { get; set; } = new();

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

    // Theme Presets — defaults to the redefined quiet "nexus-default" preset (owner ruling
    // 2026-07-11) so a fresh install's picker shows a real selection instead of "(Custom)".
    public string ActiveThemePresetId { get; set; } = "nexus-default";  // "" = custom/none

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

    // IdleThrottle (Phase 18)
    public bool          IdleThrottleEnabled           { get; set; } = false;
    public double        IdleThrottleCpuThreshold      { get; set; } = 1.0;
    public int           IdleThrottleIdleTicksRequired { get; set; } = 3;
    public List<string>  IdleThrottleExclusions        { get; set; } = new();
    public bool          IdleThrottleUseEfficiencyMode { get; set; } = true;

    // MemoryReclaim Automation (Phase 18)
    public bool   MemoryReclaimEnabled         { get; set; } = false;
    public int    MemoryReclaimIntervalSeconds { get; set; } = 60;
    public double MemoryReclaimPressurePercent { get; set; } = 20.0;
    public int    MemoryReclaimMinWorkingSetMB { get; set; } = 100;

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

    // Motion & Depth (Phase 8 — UI design polish)
    /// <summary>
    /// Global animation speed multiplier consumed by MotionSettingsService/MotionMath.Scale:
    /// base durations (MotionFast/Base/Slow — 120/180/280 ms) are divided by this value, so
    /// higher = faster. 1.0 = default speed. 0.0 = animations off entirely (all durations
    /// become <see cref="TimeSpan.Zero"/> and every per-effect toggle below is overridden to
    /// disabled). Range 0.0–2.0.
    /// </summary>
    public double AnimationSpeed { get; set; } = 1.0;
    /// <summary>Dashboard tab/page cross-fade transitions.</summary>
    public bool   AnimatePageTransitions { get; set; } = true;
    /// <summary>Button/control hover-state brush and lift transitions.</summary>
    public bool   AnimateHoverEffects { get; set; } = true;
    /// <summary>Widget pop-out window open/close scale motion.</summary>
    public bool   AnimatePopOutMotion { get; set; } = true;
    /// <summary>Edit-mode chrome and widget-gallery fade transitions.</summary>
    public bool   AnimateEditChrome { get; set; } = true;
    /// <summary>Animated numeric value changes on widget tiles. Off by default (quiet-defaults
    /// ruling, 2026-07-11) — see the Crystal Glass fields' doc comment above.</summary>
    public bool   AnimateValueChanges { get; set; } = false;
    /// <summary>Crystal Glass specular shimmer sweep timer. Off by default (quiet-defaults
    /// ruling, 2026-07-11) — inert anyway while <see cref="IsSpecularEnabled"/> is false.</summary>
    public bool   AnimateSpecularShimmer { get; set; } = false;
    /// <summary>
    /// Strength of elevation shadows (ElevationRaised/Floating/Modal tokens) — scales shadow
    /// alpha at apply time. 0 = flat/no shadow, 1 = full depth. Default 0.5 (subtle-Apple look).
    /// Range 0–1.
    /// </summary>
    public double DepthIntensity { get; set; } = 0.5;
    /// <summary>When true, widget tile headline/value text scales with the tile's resized bounds
    /// (interacts multiplicatively with <see cref="FontSizeMultiplier"/>).</summary>
    public bool   ScaleTextWithWidgetSize { get; set; } = true;

    // ── Session persistence ─────────────────────────────────────────────────────
    public string LastActiveTab    { get; set; } = string.Empty;
    public double LastWindowWidth  { get; set; } = 0;
    public double LastWindowHeight { get; set; } = 0;
    public int    LastWindowX      { get; set; } = -1;
    public int    LastWindowY      { get; set; } = -1;
    public string LastWindowState  { get; set; } = "Normal";
}
