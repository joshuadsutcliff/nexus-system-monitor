using FluentAssertions;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Pure-math tests for <see cref="HealthScoring"/> and <see cref="ImpactScoreCalculator"/>.
/// No mocks or I/O — all assertions are on deterministic arithmetic.
/// </summary>
public class HealthScoringTests
{
    // ── ScoreCpu ─────────────────────────────────────────────────────────────

    [Fact]
    public void ScoreCpu_Zero_Returns100()
    {
        HealthScoring.ScoreCpu(0).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void ScoreCpu_50Percent_Returns80()
    {
        HealthScoring.ScoreCpu(50).Should().BeApproximately(80, 0.001);
    }

    [Fact]
    public void ScoreCpu_80Percent_Returns40()
    {
        HealthScoring.ScoreCpu(80).Should().BeApproximately(40, 0.001);
    }

    [Fact]
    public void ScoreCpu_95Percent_Returns10()
    {
        HealthScoring.ScoreCpu(95).Should().BeApproximately(10, 0.001);
    }

    [Fact]
    public void ScoreCpu_100Percent_ReturnsZero()
    {
        // 10 - (100-95)*2 = 10 - 10 = 0
        HealthScoring.ScoreCpu(100).Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void ScoreCpu_Over100_ClampsToZero()
    {
        // 10 - (110-95)*2 = 10 - 30 = -20 → clamped to 0
        HealthScoring.ScoreCpu(110).Should().BeGreaterThanOrEqualTo(0);
    }

    // ── ScoreMemory ───────────────────────────────────────────────────────────

    [Fact]
    public void ScoreMemory_Zero_Returns100()
    {
        HealthScoring.ScoreMemory(0).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void ScoreMemory_60Percent_Returns80()
    {
        // 100 - 60*(20/60) = 100 - 20 = 80
        HealthScoring.ScoreMemory(60).Should().BeApproximately(80, 0.001);
    }

    [Fact]
    public void ScoreMemory_90Percent_Returns10()
    {
        // 40 - (90-80)*(30/10) = 40 - 30 = 10
        HealthScoring.ScoreMemory(90).Should().BeApproximately(10, 0.001);
    }

    // ── ScoreDisk ─────────────────────────────────────────────────────────────

    [Fact]
    public void ScoreDisk_BothZero_Returns100()
    {
        // activityScore(0)=100, spaceScore(0)=100 → 100*0.6 + 100*0.4 = 100
        HealthScoring.ScoreDisk(0, 0).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void ScoreDisk_HighActivity_LowSpace_ExpectedBlend()
    {
        // diskActive=90: max(0, 20-(90-85)*1.3) = max(0, 13.5) = 13.5
        // diskUsed=20: spaceScore=100
        // blend = 13.5*0.6 + 100*0.4 = 8.1 + 40 = 48.1
        HealthScoring.ScoreDisk(90, 20).Should().BeApproximately(48.1, 0.1);
    }

    [Fact]
    public void ScoreDisk_LowActivity_HighUsage_SpaceDominates()
    {
        // diskActive=10: 100 - 10*(20/30) = 100 - 6.667 = 93.333
        // diskUsed=96: max(0, 20-(96-95)*4) = 16
        // blend = 93.333*0.6 + 16*0.4 = 56.0 + 6.4 = 62.4
        HealthScoring.ScoreDisk(10, 96).Should().BeApproximately(62.4, 0.1);
    }

    // ── ScoreGpu ──────────────────────────────────────────────────────────────

    [Fact]
    public void ScoreGpu_Zero_Returns100()
    {
        HealthScoring.ScoreGpu(0).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void ScoreGpu_100Percent_AtLeast20()
    {
        // GPU usage is lenient — never penalised below 20
        HealthScoring.ScoreGpu(100).Should().BeGreaterThanOrEqualTo(20);
    }

    // ── ScoreThermal ──────────────────────────────────────────────────────────

    [Fact]
    public void ScoreThermal_BothUnknown_Returns100()
    {
        // -1 signals unknown → neutral score of 100
        HealthScoring.ScoreThermal(-1, -1).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void ScoreThermal_CpuAt80C_Returns80()
    {
        // ScoreTemp(80, warnAt=80): 100 - (80/80)*20 = 80
        HealthScoring.ScoreThermal(80, -1).Should().BeApproximately(80, 0.001);
    }

    [Fact]
    public void ScoreThermal_HighTemp_LowScore()
    {
        // cpuTempC=100 > critAt=95: max(0, 20-(100-95)*2) = max(0,10) = 10
        HealthScoring.ScoreThermal(100, -1).Should().BeApproximately(10, 0.001);
    }

    // ── CompositeScore ────────────────────────────────────────────────────────

    [Fact]
    public void CompositeScore_AllPerfect_Returns100()
    {
        HealthScoring.CompositeScore(100, 100, 100, 100, 100).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void CompositeScore_AllZero_Returns0()
    {
        HealthScoring.CompositeScore(0, 0, 0, 0, 0).Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void CompositeScore_Weights_CorrectBlend()
    {
        // cpu=60, mem=50, disk=40, gpu=30, thermal=20
        // 60*0.30 + 50*0.25 + 40*0.20 + 30*0.15 + 20*0.10
        // = 18 + 12.5 + 8 + 4.5 + 2 = 45
        HealthScoring.CompositeScore(60, 50, 40, 30, 20).Should().BeApproximately(45, 0.001);
    }

    [Fact]
    public void CompositeScore_IncludeGpuDefaultsTrue_MatchesExplicitTrue()
    {
        // The includeGpu parameter defaults to true — existing call sites (no 6th arg) must
        // keep behaving exactly as before this parameter was added.
        HealthScoring.CompositeScore(60, 50, 40, 30, 20)
            .Should().Be(HealthScoring.CompositeScore(60, 50, 40, 30, 20, includeGpu: true));
    }

    [Fact]
    public void CompositeScore_GpuExcluded_RenormalizesOverRemainingWeights()
    {
        // All remaining subsystems perfect (100) → renormalized composite must still be 100,
        // i.e. dropping GPU's 15% weight doesn't leave the composite permanently capped below 100.
        HealthScoring.CompositeScore(100, 100, 100, gpuScore: 0, thermalScore: 100, includeGpu: false)
            .Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void CompositeScore_GpuExcluded_IgnoresGpuScoreParameter()
    {
        // Whatever gpuScore is passed must not affect the result when includeGpu is false —
        // a fabricated "excellent" GPU score must not sneak into the composite via this param.
        var withZeroGpu    = HealthScoring.CompositeScore(60, 50, 40, gpuScore: 0,   thermalScore: 20, includeGpu: false);
        var withPerfectGpu = HealthScoring.CompositeScore(60, 50, 40, gpuScore: 100, thermalScore: 20, includeGpu: false);

        withZeroGpu.Should().BeApproximately(withPerfectGpu, 0.001);
    }

    [Fact]
    public void CompositeScore_GpuExcluded_KnownBlend()
    {
        // cpu=60, mem=50, disk=40, thermal=20, gpu excluded.
        // Weighted sum over remaining subsystems: 60*.30 + 50*.25 + 40*.20 + 20*.10
        // = 18 + 12.5 + 8 + 2 = 40.5; renormalized over 0.85 total weight: 40.5/0.85 = 47.6470...
        HealthScoring.CompositeScore(60, 50, 40, gpuScore: 999, thermalScore: 20, includeGpu: false)
            .Should().BeApproximately(47.647, 0.01);
    }

    // ── ScoreToLevel ──────────────────────────────────────────────────────────

    [Fact]
    public void ScoreToLevel_80_ReturnsExcellent()
    {
        HealthScoring.ScoreToLevel(80).Should().Be(HealthLevel.Excellent);
    }

    [Fact]
    public void ScoreToLevel_60_ReturnsGood()
    {
        HealthScoring.ScoreToLevel(60).Should().Be(HealthLevel.Good);
    }

    [Fact]
    public void ScoreToLevel_20_ReturnsPoor()
    {
        HealthScoring.ScoreToLevel(20).Should().Be(HealthLevel.Poor);
    }

    [Fact]
    public void ScoreToLevel_10_ReturnsCritical()
    {
        HealthScoring.ScoreToLevel(10).Should().Be(HealthLevel.Critical);
    }

    // ── ComputeTrend ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeTrend_FewerThan5_ReturnsStable()
    {
        var history = new List<double> { 10, 20, 30, 40 };
        HealthScoring.ComputeTrend(history).Should().Be(TrendDirection.Stable);
    }

    [Fact]
    public void ComputeTrend_Improving_ReturnsImproving()
    {
        // Rising sequence: first half avg ≪ second half avg, delta > 3
        var history = new List<double> { 40, 45, 50, 55, 60, 65, 70 };
        HealthScoring.ComputeTrend(history).Should().Be(TrendDirection.Improving);
    }

    [Fact]
    public void ComputeTrend_Degrading_ReturnsDegrading()
    {
        var history = new List<double> { 70, 65, 60, 55, 50, 45, 40 };
        HealthScoring.ComputeTrend(history).Should().Be(TrendDirection.Degrading);
    }

    [Fact]
    public void ComputeTrend_Flat_ReturnsStable()
    {
        var history = new List<double> { 50, 50, 50, 50, 50, 50, 50 };
        HealthScoring.ComputeTrend(history).Should().Be(TrendDirection.Stable);
    }
}

public class ImpactScoreCalculatorTests
{
    // ── Calculate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_AllZeroTotals_ReturnsZero()
    {
        // SystemTotals with all 1s (minimum clamp) and process with all-zero fields
        var totals  = new SystemTotals(1, 1, 1, 1, 1);
        var process = new ProcessInfo();  // all defaults = 0

        ImpactScoreCalculator.Calculate(process, totals).Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void Calculate_SingleProcess_100PercentCpu_Returns40()
    {
        // cpuNorm = 10/10 = 1.0; all others = 0
        // raw = 1.0*0.40 = 0.40 → *100 = 40
        var totals  = new SystemTotals(TotalCpuPercent: 10, TotalMemoryBytes: 1,
                                       TotalIoBytesPerSec: 1, TotalGpuPercent: 1,
                                       TotalHandleCount: 1);
        var process = new ProcessInfo { CpuPercent = 10 };

        ImpactScoreCalculator.Calculate(process, totals).Should().BeApproximately(40, 0.001);
    }

    [Fact]
    public void Calculate_MaxValues_ClampsTo100()
    {
        // All norms = 1.0 → raw = 0.40+0.30+0.15+0.10+0.05 = 1.0 → *100 = 100
        var totals  = new SystemTotals(TotalCpuPercent: 10, TotalMemoryBytes: 1000,
                                       TotalIoBytesPerSec: 500, TotalGpuPercent: 10,
                                       TotalHandleCount: 100);
        var process = new ProcessInfo
        {
            CpuPercent          = 10,
            WorkingSetBytes     = 1000,
            IoReadBytesPerSec   = 500,
            GpuPercent          = 10,
            HandleCount         = 100,
        };

        ImpactScoreCalculator.Calculate(process, totals).Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void Calculate_WeightedFormula_CorrectResult()
    {
        // cpuNorm = 5/10 = 0.5, memNorm = 200/1000 = 0.2, rest = 0
        // raw = 0.5*0.40 + 0.2*0.30 = 0.20 + 0.06 = 0.26 → *100 = 26
        var totals  = new SystemTotals(TotalCpuPercent: 10, TotalMemoryBytes: 1000,
                                       TotalIoBytesPerSec: 1, TotalGpuPercent: 1,
                                       TotalHandleCount: 1);
        var process = new ProcessInfo { CpuPercent = 5, WorkingSetBytes = 200 };

        ImpactScoreCalculator.Calculate(process, totals).Should().BeApproximately(26, 0.001);
    }

    // ── ComputeTotals ─────────────────────────────────────────────────────────

    [Fact]
    public void ComputeTotals_SumsCpuMemIoGpuHandles()
    {
        var processes = new List<ProcessInfo>
        {
            new() { CpuPercent = 10, WorkingSetBytes = 1000, IoReadBytesPerSec = 200,
                    IoWriteBytesPerSec = 100, GpuPercent = 5, HandleCount = 50 },
            new() { CpuPercent = 20, WorkingSetBytes = 2000, IoReadBytesPerSec = 300,
                    IoWriteBytesPerSec = 200, GpuPercent = 10, HandleCount = 75 },
        };

        var totals = ImpactScoreCalculator.ComputeTotals(processes);

        totals.TotalCpuPercent.Should().BeApproximately(30, 0.001);
        totals.TotalMemoryBytes.Should().Be(3000);
        totals.TotalIoBytesPerSec.Should().Be(800);
        totals.TotalGpuPercent.Should().BeApproximately(15, 0.001);
        totals.TotalHandleCount.Should().Be(125);
    }

    [Fact]
    public void ComputeTotals_ClampsToMinimumOne()
    {
        // Single process with all-zero values → each total clamped to 1
        var processes = new List<ProcessInfo> { new ProcessInfo() };

        var totals = ImpactScoreCalculator.ComputeTotals(processes);

        totals.TotalCpuPercent.Should().BeGreaterThanOrEqualTo(1);
        totals.TotalMemoryBytes.Should().BeGreaterThanOrEqualTo(1);
        totals.TotalIoBytesPerSec.Should().BeGreaterThanOrEqualTo(1);
        totals.TotalGpuPercent.Should().BeGreaterThanOrEqualTo(1);
        totals.TotalHandleCount.Should().BeGreaterThanOrEqualTo(1);
    }
}
