using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Health;

/// <summary>
/// Produces plain-English recommendations based on current system health.
/// Rule-based, ordered from most to least critical.
/// </summary>
public static class RecommendationEngine
{
    public static IReadOnlyList<Recommendation> Evaluate(
        SystemHealthSnapshot snapshot,
        AppSettings settings,
        IReadOnlyList<MemoryLeakSuspect>? leakSuspects = null)
    {
        var results = new List<Recommendation>();

        // ── Critical alerts first ──────────────────────────────────────────────

        if (snapshot.OverallHealth == HealthLevel.Critical)
        {
            results.Add(new Recommendation
            {
                Title    = "System under severe stress",
                Body     = "Multiple subsystems are overloaded. Consider closing unused applications.",
                Severity = RecommendationSeverity.Critical,
                Action   = RecommendationAction.ReviewProcesses,
            });
        }

        // ── CPU ───────────────────────────────────────────────────────────────

        if (snapshot.Cpu.CurrentValue > 85 && !settings.AutoBalanceEnabled)
        {
            results.Add(new Recommendation
            {
                Title    = "CPU is under heavy load",
                Body     = "Auto-Balance can automatically keep your computer responsive by slowing background apps. It's free and reversible.",
                Severity = RecommendationSeverity.Warning,
                Action   = RecommendationAction.EnableAutoBalance,
            });
        }
        else if (snapshot.Cpu.CurrentValue > 85 && settings.AutoBalanceEnabled)
        {
            var top = snapshot.TopConsumers.FirstOrDefault();
            if (top is not null)
            {
                results.Add(new Recommendation
                {
                    Title    = $"{top.Name} is using a lot of CPU",
                    Body     = $"{top.Name} is consuming {top.CpuPercent:F0}% of your processor. You can set a rule to throttle it automatically.",
                    Severity = RecommendationSeverity.Warning,
                    Action   = RecommendationAction.ReviewProcesses,
                });
            }
        }

        // ── Memory ────────────────────────────────────────────────────────────

        if (snapshot.Memory.CurrentValue > 85)
        {
            results.Add(new Recommendation
            {
                Title    = "Memory is nearly full",
                Body     = "Memory Reclaim can reclaim memory from background apps that aren't actively using it, without closing them.",
                Severity = snapshot.Memory.CurrentValue > 95
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning,
                Action   = RecommendationAction.EnableMemoryReclaim,
            });
        }

        // ── Memory leak suspects ──────────────────────────────────────────────

        if (leakSuspects is { Count: > 0 })
        {
            foreach (var suspect in leakSuspects)
            {
                double rateMb = suspect.LeakRateBytesPerHour / 1024.0 / 1024.0;
                bool isCritical = suspect.Confidence > 0.8 && rateMb > settings.LeakRateThresholdMbPerHour * 2;

                results.Add(new Recommendation
                {
                    Title       = isCritical
                        ? $"Memory leak detected in {suspect.ProcessName}"
                        : $"Possible memory leak in {suspect.ProcessName}",
                    Body        = $"+{rateMb:F0} MB/hr (confidence {suspect.Confidence:P0})",
                    Severity    = isCritical ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                    Action      = RecommendationAction.InvestigateMemoryLeak,
                });
            }
        }

        // ── Disk space ────────────────────────────────────────────────────────

        if (snapshot.Disk.Level is HealthLevel.Poor or HealthLevel.Critical &&
            snapshot.Disk.CurrentValue > 90)
        {
            results.Add(new Recommendation
            {
                Title    = "Disk space is critically low",
                Body     = "Your system drive is over 90% full. A full disk can cause system slowdowns and prevent updates.",
                Severity = RecommendationSeverity.Critical,
                Action   = RecommendationAction.CheckDiskSpace,
            });
        }

        // ── Thermal ───────────────────────────────────────────────────────────

        if (snapshot.Cpu.Level is HealthLevel.Poor or HealthLevel.Critical &&
            snapshot.Disk.Level != HealthLevel.Poor) // avoid duplicate if disk is the cause
        {
            results.Add(new Recommendation
            {
                Title    = "System may be running hot",
                Body     = "High temperatures can reduce performance and shorten hardware lifespan. Check that vents are clear.",
                Severity = RecommendationSeverity.Warning,
                Action   = RecommendationAction.CheckTemperatures,
            });
        }

        // ── Positive reinforcement (only when everything is healthy) ──────────

        if (results.Count == 0 && snapshot.OverallHealth is HealthLevel.Excellent or HealthLevel.Good)
        {
            results.Add(new Recommendation
            {
                Title    = "Your system looks healthy",
                Body     = "All subsystems are running within normal parameters. No action needed.",
                Severity = RecommendationSeverity.Info,
                Action   = RecommendationAction.None,
            });
        }

        return results;
    }
}
