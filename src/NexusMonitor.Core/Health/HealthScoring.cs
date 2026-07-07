using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Health;

/// <summary>
/// Stateless scoring functions. All inputs produce a 0-100 score where 100 = perfectly healthy.
/// Composite = CPU 30% + Memory 25% + Disk 20% + GPU 15% + Thermal 10%.
/// </summary>
public static class HealthScoring
{
    // ── CPU ────────────────────────────────────────────────────────────────────

    /// <param name="cpuPercent">0-100</param>
    public static double ScoreCpu(double cpuPercent)
    {
        // 0-50% → 100-80, 50-80% → 80-40, 80-95% → 40-10, 95-100% → 10-0
        return cpuPercent switch
        {
            <= 50  => 100 - cpuPercent * 0.4,
            <= 80  => 80  - (cpuPercent - 50) * (40.0 / 30),
            <= 95  => 40  - (cpuPercent - 80) * (30.0 / 15),
            _      => Math.Max(0, 10 - (cpuPercent - 95) * 2),
        };
    }

    // ── Memory ─────────────────────────────────────────────────────────────────

    /// <param name="memPercent">0-100</param>
    public static double ScoreMemory(double memPercent)
    {
        return memPercent switch
        {
            <= 60  => 100 - memPercent * (20.0 / 60),
            <= 80  => 80  - (memPercent - 60) * (40.0 / 20),
            <= 90  => 40  - (memPercent - 80) * (30.0 / 10),
            _      => Math.Max(0, 10 - (memPercent - 90) * 1),
        };
    }

    // ── Disk ───────────────────────────────────────────────────────────────────

    /// <param name="diskActivePercent">0-100 average activity across disks</param>
    /// <param name="diskUsedPercent">0-100 used space on the busiest/fullest disk</param>
    public static double ScoreDisk(double diskActivePercent, double diskUsedPercent)
    {
        var activityScore = diskActivePercent switch
        {
            <= 30  => 100 - diskActivePercent * (20.0 / 30),
            <= 60  => 80  - (diskActivePercent - 30) * (30.0 / 30),
            <= 85  => 50  - (diskActivePercent - 60) * (30.0 / 25),
            _      => Math.Max(0, 20 - (diskActivePercent - 85) * 1.3),
        };
        var spaceScore = diskUsedPercent switch
        {
            <= 70  => 100,
            <= 85  => 100 - (diskUsedPercent - 70) * (30.0 / 15),
            <= 95  => 70  - (diskUsedPercent - 85) * (50.0 / 10),
            _      => Math.Max(0, 20 - (diskUsedPercent - 95) * 4),
        };
        return activityScore * 0.6 + spaceScore * 0.4;
    }

    // ── GPU ────────────────────────────────────────────────────────────────────

    /// <param name="gpuPercent">0-100</param>
    public static double ScoreGpu(double gpuPercent)
    {
        // GPU being highly used is fine for gaming/rendering — penalise lightly
        return gpuPercent switch
        {
            <= 70  => 100 - gpuPercent * 0.2,
            <= 90  => 86  - (gpuPercent - 70) * (36.0 / 20),
            _      => Math.Max(20, 50 - (gpuPercent - 90) * 3),
        };
    }

    // ── Thermal ────────────────────────────────────────────────────────────────

    /// <param name="cpuTempC">-1 = unknown</param>
    /// <param name="gpuTempC">-1 = unknown</param>
    public static double ScoreThermal(double cpuTempC, double gpuTempC)
    {
        if (cpuTempC <= 0 && gpuTempC <= 0) return 100; // no data → neutral
        var scores = new List<double>();
        if (cpuTempC > 0) scores.Add(ScoreTemp(cpuTempC, warnAt: 80, critAt: 95));
        if (gpuTempC > 0) scores.Add(ScoreTemp(gpuTempC, warnAt: 85, critAt: 100));
        return scores.Average();
    }

    private static double ScoreTemp(double temp, double warnAt, double critAt)
    {
        if (temp <= warnAt)      return 100 - (temp / warnAt) * 20;
        if (temp <= critAt)      return 80  - (temp - warnAt) / (critAt - warnAt) * 60;
        return Math.Max(0, 20   - (temp - critAt) * 2);
    }

    // ── Composite ──────────────────────────────────────────────────────────────

    private const double CpuWeight     = 0.30;
    private const double MemWeight     = 0.25;
    private const double DiskWeight    = 0.20;
    private const double GpuWeight     = 0.15;
    private const double ThermalWeight = 0.10;

    /// <param name="includeGpu">
    /// Pass <c>false</c> when the sample has no live GPU telemetry (see
    /// <see cref="BottleneckDetector.HasLiveGpuData"/>) — the GPU's 15% weight is dropped
    /// entirely and the remaining subsystem weights are renormalized to sum to 100%, rather
    /// than crediting a fabricated (always-idle) GPU reading with a perfect score.
    /// <paramref name="gpuScore"/> is ignored in that case.
    /// </param>
    public static double CompositeScore(double cpuScore, double memScore, double diskScore, double gpuScore, double thermalScore, bool includeGpu = true)
    {
        if (!includeGpu)
        {
            const double totalWeight = CpuWeight + MemWeight + DiskWeight + ThermalWeight; // 0.85
            return (cpuScore * CpuWeight + memScore * MemWeight + diskScore * DiskWeight + thermalScore * ThermalWeight) / totalWeight;
        }
        return cpuScore * CpuWeight + memScore * MemWeight + diskScore * DiskWeight + gpuScore * GpuWeight + thermalScore * ThermalWeight;
    }

    public static HealthLevel ScoreToLevel(double score) => score switch
    {
        >= 80 => HealthLevel.Excellent,
        >= 60 => HealthLevel.Good,
        >= 40 => HealthLevel.Fair,
        >= 20 => HealthLevel.Poor,
        _     => HealthLevel.Critical,
    };

    public static TrendDirection ComputeTrend(IReadOnlyList<double> history)
    {
        if (history.Count < 5) return TrendDirection.Stable;
        var n    = history.Count;
        var half = n / 2;
        var first = history.Take(half).Average();
        var last  = history.Skip(n - half).Average();
        var delta = last - first;
        return delta switch
        {
            > 3  => TrendDirection.Improving,
            < -3 => TrendDirection.Degrading,
            _    => TrendDirection.Stable,
        };
    }
}
