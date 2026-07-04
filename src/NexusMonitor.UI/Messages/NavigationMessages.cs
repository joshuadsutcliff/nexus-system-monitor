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
