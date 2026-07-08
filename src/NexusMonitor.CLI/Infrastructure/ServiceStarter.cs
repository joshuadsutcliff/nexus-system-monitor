using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Alerts;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Rules;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Storage;

namespace NexusMonitor.CLI.Infrastructure;

/// <summary>
/// Starts and stops the core background services based on current AppSettings.
/// Mirrors the startup logic in App.axaml.cs but excludes UI-only services
/// (GlassAdaptiveService, PrometheusExporter — controlled by CLI commands).
/// </summary>
internal static class ServiceStarter
{
    /// <summary>
    /// Starts all core background services that are enabled in <paramref name="settings"/>.
    /// </summary>
    public static void StartCoreServices(IServiceProvider services, AppSettings settings)
    {
        // Health service is always on
        services.GetRequiredService<SystemHealthService>()
            .Start(TimeSpan.FromMilliseconds(settings.UpdateIntervalMs));

        // AutoBalance only starts if enabled
        if (settings.AutoBalanceEnabled)
            services.GetRequiredService<AutoBalanceService>().Start();

        // Rules engine if rules exist OR there are saved process preferences
        var prefStore = services.GetRequiredService<ProcessPreferenceStore>();
        if (settings.Rules.Count > 0 || prefStore.GetAll().Count > 0)
            services.GetRequiredService<RulesEngine>().Start();

        // Alerts engine
        if (settings.AlertRules.Count > 0)
            services.GetRequiredService<AlertsService>().Start();

        // Metrics persistence
        if (settings.MetricsEnabled)
        {
            services.GetRequiredService<MetricsStore>().Start(
                TimeSpan.FromMilliseconds(settings.UpdateIntervalMs));
            services.GetRequiredService<MetricsRollupService>().Start();
            services.GetRequiredService<EventMonitorService>().Start();
        }

        // Anomaly detection
        if (settings.AnomalyDetectionEnabled)
            services.GetRequiredService<AnomalyDetectionService>().Start();

        // Memory leak detection
        if (settings.MemoryLeakDetectionEnabled)
            services.GetRequiredService<MemoryLeakDetectionService>().Start();

        // Restore active performance profile
        if (settings.ActiveProfileId.HasValue)
            services.GetRequiredService<PerformanceProfileService>()
                .ActivateProfile(settings.ActiveProfileId.Value);

        // Phase 18 automation services
        services.GetRequiredService<SleepPreventionService>().Start();
        if (settings.ForegroundBoostEnabled)
            services.GetRequiredService<ForegroundBoostService>().Start();
        if (settings.IdleThrottleEnabled)
            services.GetRequiredService<IdleThrottleService>().Start();
        if (settings.MemoryReclaimEnabled)
            services.GetRequiredService<MemoryReclaimService>().Start();
        if (settings.CpuLimiterEnabled)
            services.GetRequiredService<CpuLimiterService>().Start();
        if (settings.InstanceBalancerEnabled)
            services.GetRequiredService<InstanceBalancerService>().Start();
    }

    /// <summary>
    /// Stops all core background services and flushes any buffered data.
    /// </summary>
    public static void StopCoreServices(IServiceProvider services)
    {
        // Automation services (hold OS handles / modified process state)
        services.GetService<ForegroundBoostService>()?.Stop();
        services.GetService<IdleThrottleService>()?.Stop();
        services.GetService<MemoryReclaimService>()?.Stop();
        services.GetService<CpuLimiterService>()?.Stop();
        services.GetService<InstanceBalancerService>()?.Stop();
        services.GetService<SleepPreventionService>()?.Stop();
        services.GetService<AutoBalanceService>()?.Stop();
        services.GetService<PerformanceProfileService>()?.DeactivateProfile();
        services.GetService<MemoryLeakDetectionService>()?.Stop();

        // Monitoring services
        services.GetService<AlertsService>()?.Stop();
        services.GetService<AnomalyDetectionService>()?.Stop();
        services.GetService<RulesEngine>()?.Stop();

        // Data persistence (flush buffers last)
        services.GetRequiredService<EventMonitorService>().Stop();
        services.GetService<MetricsRollupService>()?.Stop();
        services.GetRequiredService<MetricsStore>().Stop();
        services.GetRequiredService<SystemHealthService>().Stop();

        if (services is IDisposable disposable)
            disposable.Dispose();
    }
}
