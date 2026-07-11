using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.UI.Messages;
using Serilog;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Alerts;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using NexusMonitor.Core.Gaming;
using NexusMonitor.Core.Rules;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Storage;
using NexusMonitor.Core.Telemetry;
using NexusMonitor.UI.Controls;
using NexusMonitor.UI.Services;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Pages;
using NexusMonitor.UI.ViewModels;
using NexusMonitor.UI.Views;
using NexusMonitor.Hosting;

namespace NexusMonitor.UI;

public class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    // Keep tray icon alive for the app lifetime
    private TrayIcon? _trayIcon;

    // 4F: Rx subscriptions that must be disposed on shutdown
    private readonly CompositeDisposable _subscriptions = new();

    // Periodic GC collect + memory diagnostics
    private System.Threading.Timer? _memTrimTimer;

    // Guards ShutdownRequested against firing more than once per quit (observed on macOS —
    // see the guard at the top of the handler below for the full explanation).
    private bool _shutdownHandled;

    // Guards RequestAppExit against being invoked more than once per app lifetime (e.g.
    // MainWindow's OnClosing "Exit" case racing a tray Exit click, or the close-prompt's "Close
    // Application" choice arriving after a quit is already in flight). Distinct from
    // _shutdownHandled above, which guards the ShutdownRequested handler's BODY — this guards
    // the request itself, so a second RequestAppExit() call is a silent no-op rather than
    // posting a redundant TryShutdown() to the dispatcher.
    private bool _exitRequested;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── Log any exception that escapes to the Avalonia UI-thread dispatcher ──
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled exception on Avalonia UI thread");
            CrashLogger.Write(e.Exception, "Avalonia Dispatcher UI Thread");
            // Do NOT set e.Handled = true here — let Avalonia's default crash behaviour run
            // so the user isn't left with a silent, frozen window.
        };

        Services = BuildServices();

        // Page engine Phase 5: one-time migration of P3's legacy per-page dashboard.json into a
        // "Default" workspace profile. Must run here — right after the container is built and
        // before ANY ViewModel is resolved (DashboardViewModel's constructor below reads
        // WorkspaceProfileStore.LoadActive() for its initial EnginePage, and it eagerly resolves
        // as soon as MainViewModel is constructed a few lines down) — otherwise DashboardViewModel
        // would load before the legacy layout had a chance to become the active profile, silently
        // falling back to the factory default instead of the user's existing saved layout.
        Services.GetRequiredService<WorkspaceProfileStore>().MigrateLegacyIfNeeded();

        var saved = Services.GetRequiredService<SettingsService>();
        RequestedThemeVariant = saved.Current.ThemeMode switch
        {
            "Dark"  => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _       => DetectSystemTheme(),
        };

        // Phase 8 UI polish: compute MotionFast/Base/Slow duration resources from the saved
        // AnimationSpeed and write them into this Application's resources BEFORE MainWindow is
        // constructed below, so the very first render already reflects the user's saved speed
        // instead of the Themes/Motion.axaml design-time defaults.
        Services.GetRequiredService<MotionSettingsService>().Apply(saved.Current);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // v0.6.1 hotfix: the app must never quit-or-not based on how many windows happen to
            // be open. Avalonia's default (OnLastWindowClose) only auto-shuts-down when the
            // OverlayWindow and every WidgetPopOutWindow are ALSO closed — but those are only
            // ever closed from INSIDE the ShutdownRequested handler below, which OnLastWindowClose
            // needs to have already fired to reach. That circularity is exactly why closing the
            // main window left the process alive whenever the overlay widget (or a pop-out) was
            // showing. Exit is now explicit-only: every real "quit" affordance funnels through
            // RequestAppExit() (see below), which calls TryShutdown() directly regardless of
            // window count.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
            desktop.MainWindow = mainWindow;

            // Attach in-app notification host to service
            var inAppService = Services.GetRequiredService<IInAppNotificationService>();
            mainWindow.FindControl<NotificationHost>("AppNotificationHost")?.Attach(inAppService);

            // Overlay widget: create the window and wire the Settings toggle
            var overlayVm  = Services.GetRequiredService<OverlayViewModel>();
            var overlayWin = new OverlayWindow { DataContext = overlayVm };
            Services.GetRequiredService<SettingsViewModel>().OverlayWindow = overlayWin;

            // Show immediately if the user left it enabled last session
            if (saved.Current.ShowOverlayWidget)
                overlayWin.Show();

            // Start health service (always on — powers the Dashboard tab)
            Services.GetRequiredService<SystemHealthService>()
                .Start(TimeSpan.FromMilliseconds(saved.Current.UpdateIntervalMs));

            // Start automation engines
            // AutoBalance only starts if it was enabled in settings
            if (saved.Current.AutoBalanceEnabled)
                Services.GetRequiredService<AutoBalanceService>().Start();

            // Start rules engine if rules exist OR there are saved process preferences
            var prefStore = Services.GetRequiredService<ProcessPreferenceStore>();
            if (saved.Current.Rules.Count > 0 || prefStore.GetAll().Count > 0)
                Services.GetRequiredService<RulesEngine>().Start();
            if (saved.Current.AlertRules.Count > 0)
                Services.GetRequiredService<AlertsService>().Start();

            // Start metrics persistence and incident monitoring
            if (saved.Current.MetricsEnabled)
            {
                Services.GetRequiredService<MetricsStore>().Start(
                    TimeSpan.FromMilliseconds(saved.Current.UpdateIntervalMs));
                Services.GetRequiredService<MetricsRollupService>().Start();
                Services.GetRequiredService<EventMonitorService>().Start();
                Services.GetRequiredService<HealthSnapshotPersistenceService>().Start();
                if (saved.Current.PredictionsEnabled)
                    Services.GetRequiredService<PredictionService>().Start();
            }

            // Start anomaly detection if enabled
            var anomalyService = Services.GetRequiredService<AnomalyDetectionService>();
            if (saved.Current.AnomalyDetectionEnabled)
                anomalyService.Start();

            // Start memory leak detection if enabled
            if (saved.Current.MemoryLeakDetectionEnabled)
                Services.GetRequiredService<MemoryLeakDetectionService>().Start();

            // Restore active performance profile if one was saved
            if (saved.Current.ActiveProfileId.HasValue)
                Services.GetRequiredService<PerformanceProfileService>()
                    .ActivateProfile(saved.Current.ActiveProfileId.Value);

            // Start Phase 18 automation services
            Services.GetRequiredService<SleepPreventionService>().Start();
            if (saved.Current.ForegroundBoostEnabled)
                Services.GetRequiredService<ForegroundBoostService>().Start();
            if (saved.Current.IdleThrottleEnabled)
                Services.GetRequiredService<IdleThrottleService>().Start();
            if (saved.Current.MemoryReclaimEnabled)
                Services.GetRequiredService<MemoryReclaimService>().Start();
            if (saved.Current.CpuLimiterEnabled)
                Services.GetRequiredService<CpuLimiterService>().Start();
            if (saved.Current.InstanceBalancerEnabled)
                Services.GetRequiredService<InstanceBalancerService>().Start();
            if (saved.Current.QuietHoursEnabled)
                Services.GetRequiredService<QuietHoursService>().Start();

            // Start smart glass adaptive service if enabled
            if (saved.Current.SmartTintEnabled)
                Services.GetRequiredService<GlassAdaptiveService>().Start();

            // Start Prometheus endpoint if enabled
            var prometheusExporter = Services.GetRequiredService<PrometheusExporter>();
            if (saved.Current.PrometheusEnabled)
                prometheusExporter.Start(saved.Current.PrometheusPort);

            // Start update checker if enabled (passive — never auto-downloads/installs)
            if (saved.Current.CheckForUpdates)
                Services.GetRequiredService<UpdateCheckService>().Start();

            // Page engine Phase 6 shutdown-crash fix: resolve every service the
            // ShutdownRequested handler below touches into a local BEFORE wiring the handler —
            // the exact cure MainWindow.axaml.cs applies for its `_settings` field (see that
            // field's doc comment for the full root-cause writeup). On macOS, Cmd+Q can reach
            // this handler at a point where App.Services is already disposed; calling
            // GetService/GetRequiredService on a disposed IServiceProvider throws
            // ObjectDisposedException regardless of whether the requested singleton was already
            // constructed, so EVERY resolve in the handler was equally exposed — not just the
            // DashboardViewModel one that actually crashed. Resolving here, while Services is
            // still guaranteed live, means the handler itself never calls back into Services
            // except to Dispose() it as the very last step. DashboardViewModel is an eager
            // singleton (already constructed above via MainViewModel's eager NavItem), so this
            // capture is effectively free.
            var dashboardViewModel               = Services.GetRequiredService<DashboardViewModel>();
            var foregroundBoostService           = Services.GetService<ForegroundBoostService>();
            var idleThrottleService                 = Services.GetService<IdleThrottleService>();
            var memoryReclaimService                 = Services.GetService<MemoryReclaimService>();
            var cpuLimiterService                = Services.GetService<CpuLimiterService>();
            var instanceBalancerService          = Services.GetService<InstanceBalancerService>();
            var quietHoursServiceForShutdown     = Services.GetService<QuietHoursService>();
            var updateCheckServiceForShutdown    = Services.GetService<UpdateCheckService>();
            var sleepPreventionService           = Services.GetService<SleepPreventionService>();
            var gamingModeService                = Services.GetService<GamingModeService>();
            var autoBalanceService                = Services.GetService<AutoBalanceService>();
            var performanceProfileService        = Services.GetService<PerformanceProfileService>();
            var memoryLeakDetectionService       = Services.GetService<MemoryLeakDetectionService>();
            var alertsServiceForShutdown         = Services.GetService<AlertsService>();
            var rulesEngine                      = Services.GetService<RulesEngine>();
            var eventMonitorService              = Services.GetRequiredService<EventMonitorService>();
            var predictionService                = Services.GetService<PredictionService>();
            var healthSnapshotPersistenceService = Services.GetService<HealthSnapshotPersistenceService>();
            var metricsRollupService             = Services.GetService<MetricsRollupService>();
            var metricsStore                     = Services.GetRequiredService<MetricsStore>();
            var systemHealthService              = Services.GetRequiredService<SystemHealthService>();
            var systemMetricsProvider            = Services.GetService(typeof(ISystemMetricsProvider));
            var glassAdaptiveService             = Services.GetService<GlassAdaptiveService>();
            var webhookNotificationService       = Services.GetService<WebhookNotificationService>();

            // 4A: Flush MetricsStore + dispose services on shutdown so the last buffered
            //     data points are persisted and Rx subscriptions are released cleanly.
            desktop.ShutdownRequested += (_, _) =>
            {
                // Shutdown re-entrancy guard: IClassicDesktopStyleApplicationLifetime.ShutdownRequested
                // fires TWICE for a single quit on macOS (Cmd+Q / dock quit). c093bdf hardened this
                // handler's captured locals against the second pass finding a torn-down DI container,
                // but the second pass still ran in full — including the terminal
                // `(Services as IDisposable)?.Dispose()` at the bottom of this handler, which throws
                // on an already-disposed ServiceProvider. Everything in this handler, from here to
                // the closing brace, must execute exactly once per app lifetime.
                if (_shutdownHandled) return;
                _shutdownHandled = true;

                // Page engine Phase 6 shutdown-crash fix: persisting/closing pop-outs is wrapped
                // in its own try/catch (log, don't rethrow) so this path can never crash shutdown —
                // window/geometry state at quit time is inherently less predictable than at any
                // other point in the app's lifetime (a pop-out's native window may already be
                // tearing itself down), and there's no user-facing recovery from an exception
                // thrown here. Every step below still needs to run either way.
                try
                {
                    // Close every open pop-out window FIRST, before anything else in this handler —
                    // pop-out windows host live widget bindings into the services being torn down
                    // below, so they must be gone before any of those services stop. This also
                    // persists each pop-out's final geometry (still marked popped-out) so it reopens
                    // in the same place on next launch (DashboardViewModel.AttachOwnerWindow's
                    // restore path). Uses the instance captured above — never App.Services directly.
                    dashboardViewModel.PersistAndCloseAllPopOuts();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error persisting/closing pop-out windows during shutdown");
                }

                // Remove tray icon immediately so it doesn't ghost after process exit
                _trayIcon?.Dispose();
                _trayIcon = null;

                // Automation services (hold OS handles / modified process state)
                foregroundBoostService?.Stop();
                idleThrottleService?.Stop();
                memoryReclaimService?.Stop();
                cpuLimiterService?.Stop();
                instanceBalancerService?.Stop();
                quietHoursServiceForShutdown?.Stop();
                updateCheckServiceForShutdown?.Stop();
                sleepPreventionService?.Stop();
                gamingModeService?.Stop();
                autoBalanceService?.Stop();
                performanceProfileService?.DeactivateProfile();
                memoryLeakDetectionService?.Stop();

                // Monitoring services
                alertsServiceForShutdown?.Stop();
                anomalyService.Stop();
                rulesEngine?.Stop();

                // Data persistence (flush buffers last)
                eventMonitorService.Stop();
                predictionService?.Stop();
                healthSnapshotPersistenceService?.Stop();
                metricsRollupService?.Stop();
                metricsStore.Stop();
                systemHealthService.Stop();

                // Stop the metrics provider's upstream Observable.Timer before tearing down
                // subscribers, so a late tick can't fire into a half-disposed system.
                try { (systemMetricsProvider as IDisposable)?.Dispose(); }
                catch (Exception ex) { Log.Error(ex, "Error disposing ISystemMetricsProvider during shutdown"); }

                // Infrastructure
                glassAdaptiveService?.Stop();
                prometheusExporter.Stop();
                _subscriptions.Dispose();
                _memTrimTimer?.Dispose();
                (webhookNotificationService as IDisposable)?.Dispose();
                (Services as IDisposable)?.Dispose();
            };

            // Wire alert events → Prometheus counter
            var alertsService = Services.GetRequiredService<AlertsService>();
            _subscriptions.Add(alertsService.Events.Subscribe(_ => prometheusExporter.RecordAlertFired()));

            // Wire alert events → events table (bridge)
            var eventWriter = Services.GetRequiredService<IEventWriter>();
            _subscriptions.Add(alertsService.Events.Subscribe(alert =>
            {
                var eventType = alert.Rule.Metric switch
                {
                    AlertMetric.CpuPercent  => EventType.CpuHigh,
                    AlertMetric.RamPercent  => EventType.MemHigh,
                    AlertMetric.GpuPercent  => EventType.GpuHigh,
                    _                       => "alert"
                };
                var severity = alert.Rule.Severity switch
                {
                    AlertSeverity.Critical => EventSeverity.Critical,
                    AlertSeverity.Warning  => EventSeverity.Warning,
                    _                      => EventSeverity.Info
                };
                _ = eventWriter.InsertEventAsync(
                    eventType, severity,
                    alert.Rule.Metric.ToString().ToLowerInvariant(), alert.Value,
                    alert.Rule.Threshold, alert.Description);
            }));

            // Wire anomaly detection → Prometheus counter + OS + in-app notifications
            var notificationService     = Services.GetRequiredService<INotificationService>();
            var inAppNotifications      = Services.GetRequiredService<IInAppNotificationService>();

            // Wire QuietHoursService → IsSuppressed on IInAppNotificationService
            var quietHours = Services.GetRequiredService<QuietHoursService>();
            inAppNotifications.IsSuppressed = quietHours.IsActive;
            _subscriptions.Add(quietHours.IsActiveChanged.Subscribe(active => inAppNotifications.IsSuppressed = active));

            _subscriptions.Add(anomalyService.AnomalyDetected.Subscribe(evt =>
            {
                // Re-check enabled state at fire time (user may have toggled it off)
                if (!saved.Current.AnomalyDetectionEnabled) return;

                // Prometheus counters: total + per-type
                prometheusExporter.RecordAnomalyDetected(evt.EventType);

                // OS desktop notification (gated on desktop notifications setting)
                if (saved.Current.DesktopNotificationsEnabled && notificationService.IsSupported)
                    notificationService.ShowAnomaly(
                        evt.EventType,
                        evt.Description ?? string.Empty,
                        evt.Severity);

                // In-app pill notification (gated on anomaly notifications setting)
                if (saved.Current.AnomalyNotificationsEnabled)
                {
                    var inAppSeverity = evt.Severity switch
                    {
                        EventSeverity.Critical => InAppSeverity.Critical,
                        EventSeverity.Warning  => InAppSeverity.Warning,
                        _                      => InAppSeverity.Info,
                    };
                    inAppNotifications.Show(new InAppNotification(
                        Title:       $"Anomaly \u2014 {evt.EventType}",
                        Body:        evt.Description ?? string.Empty,
                        Severity:    inAppSeverity,
                        AutoDismiss: TimeSpan.FromSeconds(5)));
                }
            }));

            // Wire update checker → one toast per newer version discovered (never re-toasts the
            // same version across cycles; DistinctUntilChanged relies on UpdateInfo record equality).
            var updateCheckService = Services.GetRequiredService<UpdateCheckService>();
            _subscriptions.Add(updateCheckService.Updates
                .Where(u => u is not null)
                .DistinctUntilChanged()
                .Subscribe(info => inAppNotifications.Show(new InAppNotification(
                    Title:       "Update Available",
                    Body:        $"Nexus {info!.Version} is available",
                    Severity:    InAppSeverity.Info,
                    AutoDismiss: TimeSpan.FromSeconds(8)))));

            // Wire alert events → webhook
            var webhookService = Services.GetRequiredService<WebhookNotificationService>();
            _subscriptions.Add(alertsService.Events
                .Where(_ => saved.Current.WebhookEnabled && saved.Current.WebhookEvents.Contains("alerts"))
                .Subscribe(alert => _ = webhookService.SendAsync(new WebhookPayload(
                    Alert:     alert.Description,
                    Severity:  alert.Rule.Severity.ToString().ToLowerInvariant(),
                    Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                    Hostname:  Environment.MachineName,
                    Metrics:   new Dictionary<string, object> { [alert.Rule.Metric.ToString()] = alert.Value }))));

            // Wire anomaly events → webhook
            _subscriptions.Add(anomalyService.AnomalyDetected
                .Where(_ => saved.Current.WebhookEnabled && saved.Current.WebhookEvents.Contains("anomalies"))
                .Subscribe(evt => _ = webhookService.SendAsync(new WebhookPayload(
                    Alert:     evt.Description ?? evt.EventType,
                    Severity:  evt.Severity switch
                    {
                        EventSeverity.Critical => "critical",
                        EventSeverity.Warning  => "warning",
                        _                      => "info"
                    },
                    Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                    Hostname:  Environment.MachineName,
                    Metrics:   null))));

            // System-tray icon (only if enabled)
            if (saved.Current.MinimizeToTray)
                SetupTrayIcon(desktop);

            // Periodic gen2 GC to return empty segments to the OS + diagnostic logging.
            // EmptyWorkingSet is NOT used — the 3s metric ticks re-fault all pages immediately
            // after any trim, making it a no-op that only adds CPU overhead.
            _memTrimTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    var gcInfo = GC.GetGCMemoryInfo();
                    var proc   = System.Diagnostics.Process.GetCurrentProcess();
                    Log.Information(
                        "MEM: managed={Managed:F1}MB gcCommitted={Committed:F1}MB private={Private:F1}MB ws={WS:F1}MB threads={Threads}",
                        GC.GetTotalMemory(false) / 1048576.0,
                        gcInfo.TotalCommittedBytes / 1048576.0,
                        proc.PrivateMemorySize64 / 1048576.0,
                        proc.WorkingSet64 / 1048576.0,
                        proc.Threads.Count);
                }
                catch { /* non-fatal */ }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── App exit ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Single entry point every "really quit the app" affordance must route through instead of
    /// calling desktop.Shutdown()/TryShutdown() directly: MainWindow's OnClosing "Exit" case, the
    /// close-prompt's "Close Application" choice, and the tray icon's Exit item. Idempotent — a
    /// second call is a silent no-op (see <see cref="_exitRequested"/>).
    ///
    /// Two things this deliberately gets right that a naive <c>desktop.Shutdown()</c> call
    /// (v0.6.0's tray Exit handler; also the first-draft ruling for this hotfix) does not:
    ///
    /// 1. Calls <see cref="IClassicDesktopStyleApplicationLifetime.TryShutdown"/> — NOT
    ///    <c>Shutdown()</c>. Verified against Avalonia 11.2.3 source
    ///    (ClassicDesktopStyleApplicationLifetime.cs): <c>Shutdown(exitCode)</c> calls
    ///    <c>DoShutdown(..., force: true, ...)</c>, and <c>DoShutdown</c> only raises
    ///    <see cref="IClassicDesktopStyleApplicationLifetime.ShutdownRequested"/> when
    ///    <c>!force</c>. So the OLD tray Exit handler's <c>desktop.Shutdown()</c> call never ran
    ///    the ShutdownRequested handler below at all — no automation service Stop(), no
    ///    MetricsStore flush, no DI container Dispose(). <c>TryShutdown()</c> passes
    ///    <c>force: false</c>, so ShutdownRequested fires exactly like it already does on the
    ///    native OS quit path (Cmd+Q / dock quit), which is what makes the single
    ///    <see cref="_shutdownHandled"/>-guarded handler below the one true cleanup path for
    ///    every exit affordance.
    ///
    /// 2. Defers the actual TryShutdown() call off the current call stack via
    ///    <c>Dispatcher.UIThread.Post</c>. MainWindow's OnClosing case calls this method WHILE
    ///    still inside its own Close() call, at a point where MainWindow is still present in the
    ///    lifetime's internal window list (it's only removed once its own Closed event fires,
    ///    which hasn't happened yet). A synchronous TryShutdown() call from there would let
    ///    DoShutdown's window sweep invoke CloseCore() on that SAME window a second time while
    ///    its first OnClosing call is still on the stack — a re-entrancy hazard of exactly the
    ///    class PR #20's <see cref="_shutdownHandled"/> guard exists to prevent, just on a new
    ///    path. Posting to the dispatcher lets the current Close() call (and OnClosing) finish
    ///    unwinding first, so by the time TryShutdown() actually runs, MainWindow's own close has
    ///    already resolved one way or the other.
    /// </summary>
    public static void RequestAppExit()
    {
        if (Current is App app)
            app.RequestExitCore();
    }

    private void RequestExitCore()
    {
        if (_exitRequested) return;
        _exitRequested = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.TryShutdown();
        });
    }

    // ── System-tray icon ────────────────────────────────────────────

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _trayIcon = new TrayIcon
        {
            Icon        = CreateAppIcon(),
            ToolTipText = "Nexus Monitor",
        };

        // Single-click -> restore main window
        _trayIcon.Clicked += (_, _) =>
        {
            if (desktop.MainWindow is { } win)
            {
                win.Show();
                win.Activate();
                if (win.WindowState == WindowState.Minimized)
                    win.WindowState = WindowState.Normal;
                WeakReferenceMessenger.Default.Send(new WindowVisibilityChangedMessage(true));
            }
        };

        var settingsVm = Services.GetRequiredService<SettingsViewModel>();

        var showItem = new NativeMenuItem("Show Nexus Monitor");
        showItem.Click += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
            if (desktop.MainWindow?.WindowState == WindowState.Minimized)
                desktop.MainWindow.WindowState = WindowState.Normal;
            WeakReferenceMessenger.Default.Send(new WindowVisibilityChangedMessage(true));
        };

        var widgetItem = new NativeMenuItem("Toggle Desktop Widget");
        widgetItem.Click += (_, _) =>
            settingsVm.ShowOverlayWidget = !settingsVm.ShowOverlayWidget;

        var aboutItem = new NativeMenuItem("About Nexus System Monitor");
        aboutItem.Click += (_, _) =>
        {
            var dlg = new AboutWindow();
            if (desktop.MainWindow is { } owner)
                dlg.ShowDialog(owner);
            else
                dlg.Show();
        };

        var exitItem = new NativeMenuItem("Exit Nexus Monitor");
        exitItem.Click += (_, _) =>
        {
            MainWindow.ForceQuitFromTray = true;
            RequestAppExit();
        };

        _trayIcon.Menu = new NativeMenu();
        _trayIcon.Menu.Add(showItem);
        _trayIcon.Menu.Add(new NativeMenuItemSeparator());
        _trayIcon.Menu.Add(widgetItem);
        _trayIcon.Menu.Add(new NativeMenuItemSeparator());
        _trayIcon.Menu.Add(aboutItem);
        _trayIcon.Menu.Add(new NativeMenuItemSeparator());
        _trayIcon.Menu.Add(exitItem);

        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    /// <summary>Loads the embedded Nexus Hub app icon from the Assets directory.</summary>
    private static WindowIcon CreateAppIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://NexusMonitor/Assets/nexus-icon-256.png"));
        return new WindowIcon(stream);
    }

    // ── System theme detection ──────────────────────────────────────────────

    /// <summary>
    /// Returns the OS dark/light preference. Falls back to Dark on any failure.
    /// </summary>
    public static ThemeVariant DetectSystemTheme()
    {
        try
        {
            var v = Current?.PlatformSettings?.GetColorValues().ThemeVariant;
            return v == PlatformThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        catch { return ThemeVariant.Dark; }
    }

    // -- DI --

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddNexusPlatformProviders();
        services.AddNexusCoreServices();

        // Page engine (Phase 3) — GUI-only, not part of AddNexusCoreServices since the CLI
        // host has no page-editing surface. DI disposes IDisposable singletons on shutdown,
        // which flushes PageLayoutStore's debounced save.
        // Phase 5 superseded this as DashboardViewModel's read/write path (see WorkspaceProfileStore
        // below) — kept registered only as the legacy-pages migration source; no code path saves
        // through it anymore, so its own debounced flush on shutdown is now always a no-op.
        services.AddSingleton<PageLayoutStore>();

        // Page engine Phase 5 — named workspace profiles (layout + theme bundles). Registered
        // singleton so App.OnFrameworkInitializationCompleted can run MigrateLegacyIfNeeded()
        // once, before any ViewModel resolves.
        services.AddSingleton<WorkspaceProfileStore>();

        // UI-only registrations
        services.AddSingleton<InAppNotificationService>();
        services.AddSingleton<IInAppNotificationService>(sp =>
            sp.GetRequiredService<InAppNotificationService>());
        services.AddSingleton<GlassAdaptiveService>();
        // Phase 8 UI polish — motion duration tokens (MotionFast/Base/Slow) + per-effect gating.
        // No Start()/Stop()/IDisposable — nothing to capture in the shutdown handler below.
        services.AddSingleton<MotionSettingsService>();
        // Phase 8 UI polish (Task 7) — per-OS TransparencyLevelHint chains + ActualTransparencyLevel
        // rejection detection. No Start()/Stop()/IDisposable — nothing to capture in the shutdown
        // handler below.
        services.AddSingleton<BackdropService>();

        // -- ViewModels --
        services.AddSingleton<HealthTrendsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<SystemInfoViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<AutoBalanceViewModel>();
        services.AddSingleton<RulesViewModel>();
        services.AddSingleton<GamingModeViewModel>();
        services.AddSingleton<AlertsViewModel>();
        services.AddSingleton<DiskAnalyzerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<LanScannerViewModel>();
        services.AddSingleton<PerformanceProfilesViewModel>();
        services.AddSingleton<AutomationViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<ProcessGroupsViewModel>();

        return services.BuildServiceProvider();
    }
}
