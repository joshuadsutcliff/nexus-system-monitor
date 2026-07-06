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

/// <summary>Broadcast by <c>SettingsViewModel.SwitchWorkspaceProfile</c> BEFORE it flips the active
/// workspace profile pointer (page engine Phase 6 — pop-out windows). DashboardViewModel registers
/// for this to persist every open pop-out's geometry into the still-active OUTGOING profile and
/// close its windows, ahead of <see cref="WorkspaceProfileSwitchedMessage"/> reloading the page from
/// the newly-active INCOMING profile. Sending (and handling) this message is synchronous — the
/// persist-and-close completes fully before <c>SwitchWorkspaceProfile</c>'s next line runs — which
/// is exactly what makes the ordering safe: reversing it would read/write the wrong profile.</summary>
public record WorkspaceProfileSwitchingMessage(string Name);
