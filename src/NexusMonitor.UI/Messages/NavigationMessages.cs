namespace NexusMonitor.UI.Messages;

public record NavigateToProcessMessage(int Pid);

/// <summary>Broadcast when the user changes the metrics polling interval in Settings.</summary>
public record MetricsIntervalChangedMessage(TimeSpan Interval);

/// <summary>Broadcast when the user toggles metrics recording on/off in Settings.</summary>
public record MetricsEnabledChangedMessage(bool Enabled);

/// <summary>
/// Broadcast when the main window becomes hidden (minimized, or hidden to the system tray)
/// or visible again (restored, or shown from the tray). Background enforcement services
/// (rules, ProBalance, CPU limiter, gaming mode, alerts, anomaly detection, metrics
/// persistence, etc.) never react to this — only UI-only tab ViewModels pause their
/// display-refresh subscriptions on <c>IsVisible = false</c> and resume on <c>true</c>.
/// </summary>
public record WindowVisibilityChangedMessage(bool IsVisible);

/// <summary>Broadcast when the Dashboard page enters/leaves edit mode (page engine).</summary>
public record PageEditModeChangedMessage(bool IsEditMode); // No consumers yet by design: Phase 4 registers this to lock tab navigation during edits.

/// <summary>Broadcast when the user switches the active workspace profile in Settings (page
/// engine Phase 5). Appearance is applied synchronously by SettingsViewModel before this is sent;
/// DashboardViewModel registers for this to reload its EnginePage from the same profile's pages.</summary>
public record WorkspaceProfileSwitchedMessage(string Name);
