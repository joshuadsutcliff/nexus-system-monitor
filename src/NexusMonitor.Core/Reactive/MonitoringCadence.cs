namespace NexusMonitor.Core.Reactive;

/// <summary>
/// Canonical background sampling cadences for the ~12 long-lived Core services that each
/// hold their own <c>IProcessProvider.GetProcessStream</c> / <c>ISystemMetricsProvider.
/// GetMetricsStream</c> subscription. Every provider caches its shared, self-healing
/// multicast (<see cref="SelfHealingSharedStream{T}"/>) keyed by the requested interval and
/// re-creates the underlying timer whenever a *different* interval is requested (see e.g.
/// <c>WindowsSystemMetricsProvider.GetMetricsStream</c>) — before this consolidation, ad-hoc
/// literals (mostly 1s or 2s, but not consistently) meant subscribers could thrash that
/// per-interval cache, tearing down and rebuilding the timer instead of everyone sharing one
/// warm sample loop. Requesting one of these two named constants everywhere guarantees each
/// provider runs at most one underlying sample per cadence.
/// </summary>
/// <remarks>
/// Deliberately NOT coupled to the user-configurable <c>UpdateIntervalMs</c> setting — that
/// setting governs UI refresh/display cadence. These constants govern background
/// enforcement sampling, which must keep running (at these fixed rates) regardless of what
/// the UI is displaying or whether any window is even visible.
/// </remarks>
public static class MonitoringCadence
{
    /// <summary>
    /// 1-second cadence for automation that reacts to fast-changing foreground/gaming
    /// state: <c>ForegroundBoostService</c>, <c>GamingModeService</c>,
    /// <c>PerformanceProfileService</c>, <c>AutoBalanceService</c>.
    /// </summary>
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 2-second cadence for the remaining background services: <c>AlertsService</c>,
    /// <c>RulesEngine</c>, <c>EventMonitorService</c>, <c>AnomalyDetectionService</c>,
    /// <c>MemoryReclaimService</c>, <c>InstanceBalancerService</c>, <c>SleepPreventionService</c>,
    /// <c>CpuLimiterService</c>, <c>MemoryLeakDetectionService</c>.
    /// </summary>
    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(2);
}
