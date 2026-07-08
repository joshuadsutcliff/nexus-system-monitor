using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Alerts;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Gaming;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Mock;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Network;
using NexusMonitor.Core.Rules;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Storage;
using NexusMonitor.Core.Telemetry;
using NexusMonitor.Core.Themes;
using Serilog;
#if WINDOWS
using NexusMonitor.Platform.Windows;
using NexusMonitor.Platform.Windows.Shell;
#elif MACOS
using NexusMonitor.Platform.MacOS;
#elif LINUX
using NexusMonitor.Platform.Linux;
#endif

namespace NexusMonitor.Hosting;

/// <summary>
/// Extension methods that register platform providers and core services into an
/// <see cref="IServiceCollection"/>.  Both the GUI and CLI hosts call these so
/// the DI configuration lives in one place.
/// </summary>
public static class NexusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-specific provider implementations.
    /// On non-Windows/macOS/Linux builds the Mock/Null providers are used.
    /// </summary>
    public static IServiceCollection AddNexusPlatformProviders(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<IProcessProvider,             WindowsProcessProvider>();
        services.AddSingleton<ISystemMetricsProvider,       WindowsSystemMetricsProvider>();
        services.AddSingleton<IServicesProvider,            WindowsServicesProvider>();
        services.AddSingleton<INetworkConnectionsProvider,  WindowsNetworkConnectionsProvider>();
        services.AddSingleton<IStartupProvider,             WindowsStartupProvider>();
        services.AddSingleton<IForegroundWindowProvider,    WindowsForegroundWindowProvider>();
        services.AddSingleton<IPowerPlanProvider,           WindowsPowerPlanProvider>();
        services.AddSingleton<INotificationService,         WindowsNotificationService>();
        services.AddSingleton<IWallpaperService,            WindowsWallpaperService>();
        services.AddSingleton<ISleepPreventionProvider,     WindowsSleepPreventionProvider>();
        services.AddSingleton<IShellContextMenuService,     WindowsShellContextMenuService>();
        services.AddSingleton<WindowsHardwareInfoProvider>();
        services.AddSingleton<IPlatformCapabilities,        WindowsPlatformCapabilities>();
#elif MACOS
        services.AddSingleton<IProcessProvider,             MacOSProcessProvider>();
        services.AddSingleton<ISystemMetricsProvider,       MacOSSystemMetricsProvider>();
        services.AddSingleton<IServicesProvider,            MacOSServicesProvider>();
        services.AddSingleton<INetworkConnectionsProvider,  MacOSNetworkConnectionsProvider>();
        services.AddSingleton<IStartupProvider,             MacOSStartupProvider>();
        services.AddSingleton<IForegroundWindowProvider,    MacOSForegroundWindowProvider>();
        services.AddSingleton<IPowerPlanProvider,           MacOSPowerPlanProvider>();
        services.AddSingleton<INotificationService,         MacOSNotificationService>();
        services.AddSingleton<IWallpaperService,            MacOSWallpaperService>();
        services.AddSingleton<ISleepPreventionProvider,     MacOSSleepPreventionProvider>();
        services.AddSingleton<IShellContextMenuService,     MacOSShellContextMenuService>();
        services.AddSingleton<MacOSHardwareInfoProvider>();
        services.AddSingleton<IPlatformCapabilities,        MacOSPlatformCapabilities>();
#elif LINUX
        services.AddSingleton<IProcessProvider,             LinuxProcessProvider>();
        services.AddSingleton<ISystemMetricsProvider,       LinuxSystemMetricsProvider>();
        services.AddSingleton<IServicesProvider,            LinuxServicesProvider>();
        services.AddSingleton<INetworkConnectionsProvider,  LinuxNetworkConnectionsProvider>();
        services.AddSingleton<IStartupProvider,             LinuxStartupProvider>();
        services.AddSingleton<IForegroundWindowProvider,    LinuxForegroundWindowProvider>();
        services.AddSingleton<IPowerPlanProvider,           LinuxPowerPlanProvider>();
        services.AddSingleton<INotificationService,         LinuxNotificationService>();
        services.AddSingleton<IWallpaperService,            LinuxWallpaperService>();
        services.AddSingleton<ISleepPreventionProvider,     LinuxSleepPreventionProvider>();
        services.AddSingleton<IShellContextMenuService,     LinuxShellContextMenuService>();
        services.AddSingleton<LinuxHardwareInfoProvider>();
        services.AddSingleton<IPlatformCapabilities,        LinuxPlatformCapabilities>();
#else
        services.AddSingleton<IProcessProvider,             MockProcessProvider>();
        services.AddSingleton<ISystemMetricsProvider,       MockSystemMetricsProvider>();
        services.AddSingleton<IServicesProvider,            MockServicesProvider>();
        services.AddSingleton<INetworkConnectionsProvider,  MockNetworkConnectionsProvider>();
        services.AddSingleton<IStartupProvider,             MockStartupProvider>();
        services.AddSingleton<IForegroundWindowProvider,    MockForegroundWindowProvider>();
        services.AddSingleton<IPowerPlanProvider,           MockPowerPlanProvider>();
        services.AddSingleton<INotificationService,         NullNotificationService>();
        services.AddSingleton<IWallpaperService,            NullWallpaperService>();
        services.AddSingleton<ISleepPreventionProvider,     NullSleepPreventionProvider>();
        services.AddSingleton<IShellContextMenuService,     NullShellContextMenuService>();
        services.AddSingleton<IPlatformCapabilities>(_ => new MockPlatformCapabilities());
#endif
        return services;
    }

    /// <summary>
    /// Registers all core (non-UI) services.  Excludes InAppNotificationService,
    /// GlassAdaptiveService, and ViewModels which are UI-only.
    /// </summary>
    public static IServiceCollection AddNexusCoreServices(this IServiceCollection services)
    {
        // Logging (Serilog → Microsoft.Extensions.Logging)
        services.AddLogging(b => b.AddSerilog(dispose: true));

        // Settings
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemePresetService>();
        // Register the live AppSettings instance so services receive the same
        // object that SettingsService mutates on save.
        services.AddSingleton<AppSettings>(sp =>
            sp.GetRequiredService<SettingsService>().Current);

        // Metrics persistence
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusMonitor", "metrics.db");
        services.AddSingleton<MetricsDatabase>(_ => new MetricsDatabase(dbPath));
        services.AddSingleton<MetricsStoreConfig>(sp =>
        {
            var s = sp.GetRequiredService<AppSettings>();
            return new MetricsStoreConfig
            {
                TopNProcesses          = s.MetricsTopNProcesses,
                RecordNetworkSnapshots = s.MetricsRecordNetwork,
                RawRetention           = TimeSpan.FromHours(s.MetricsRawRetentionHours),
                Rollup1mRetention      = TimeSpan.FromDays(s.MetricsRollup1mDays),
                Rollup5mRetention      = TimeSpan.FromDays(s.MetricsRollup5mDays),
                Rollup1hRetention      = TimeSpan.FromDays(s.MetricsRollup1hDays),
                EventsRetention        = TimeSpan.FromDays(s.MetricsEventsRetentionDays),
            };
        });
        services.AddSingleton<MetricsStore>();
        services.AddSingleton<IMetricsReader>(sp => sp.GetRequiredService<MetricsStore>());
        services.AddSingleton<IEventWriter>(sp => sp.GetRequiredService<MetricsStore>());
        services.AddSingleton<MetricsRollupService>();

        // Resource incident repository
        services.AddSingleton<EventRepository>();
        services.AddSingleton<IResourceEventReader>(sp => sp.GetRequiredService<EventRepository>());
        services.AddSingleton<IResourceEventWriter>(sp => sp.GetRequiredService<EventRepository>());

        // Event monitor (incident classification)
        services.AddSingleton<EventMonitorService>();

        // Anomaly detection
        services.AddSingleton<AnomalyDetectionConfig>(sp =>
        {
            var s = sp.GetRequiredService<AppSettings>();
            var cfg = new AnomalyDetectionConfig
            {
                Enabled                         = s.AnomalyDetectionEnabled,
                CooldownSeconds                 = s.AnomalyCooldownSeconds,
                NewConnectionGracePeriodSeconds = s.AnomalyNewConnGracePeriodSec,
            };
            cfg.ApplySensitivity(s.AnomalySensitivity);
            return cfg;
        });
        services.AddSingleton<AnomalyDetectionService>();

        // Telemetry
        services.AddSingleton<PrometheusExporter>();

        // Process preferences store
        services.AddSingleton<ProcessPreferenceStore>();
        services.AddSingleton<ProcessGroupStore>();

        // Automation services
        services.AddSingleton<ProcessActionLock>();
        services.AddSingleton<AutoBalanceService>();
        services.AddSingleton<RulesEngine>();
        services.AddSingleton<RulesPersistence>();
        services.AddSingleton<PerformanceProfileService>();
        services.AddSingleton<SleepPreventionService>();
        services.AddSingleton<ForegroundBoostService>();
        services.AddSingleton<IdleThrottleService>();
        services.AddSingleton<MemoryReclaimService>();
        services.AddSingleton<CpuLimiterService>();
        services.AddSingleton<InstanceBalancerService>();
        services.AddSingleton<QuietHoursService>();

        // Gaming and Alerts services
        services.AddSingleton<GamingModeService>();
        services.AddSingleton<AlertsService>();
        services.AddSingleton<WebhookNotificationService>();

        // Network tools
        services.AddSingleton<NmapScannerService>();

        // Health service
        services.AddSingleton<SystemHealthService>();
        services.AddSingleton<HealthSnapshotPersistenceService>();
        services.AddSingleton<PredictionService>();

        // Memory leak detection
        services.AddSingleton<MemoryLeakDetectionService>();

        // Update checker (Phase 24)
        services.AddSingleton<UpdateCheckService>();

        return services;
    }
}
