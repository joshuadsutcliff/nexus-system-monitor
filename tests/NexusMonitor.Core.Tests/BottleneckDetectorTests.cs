using FluentAssertions;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using Xunit;

namespace NexusMonitor.Core.Tests;

// ── WorkloadClassifier tests ──────────────────────────────────────────────────

/// <summary>
/// Tests for <see cref="WorkloadClassifier.Classify"/> — pure static logic, no shared state.
/// </summary>
public class WorkloadClassifierTests
{
    private static ProcessInfo MakeProcess(int pid, string name,
        double cpu = 10, double gpu = 10) =>
        new() { Pid = pid, Name = name, CpuPercent = cpu, GpuPercent = gpu };

    [Fact]
    public void Classify_NoProcesses_ReturnsUnknown()
    {
        var result = WorkloadClassifier.Classify([], gpuPercent: 50, cpuPercent: 50,
            out var primary);

        result.Should().Be(WorkloadType.Unknown);
        primary.Should().Be(string.Empty);
    }

    [Fact]
    public void Classify_ObsProcess_ReturnsStreaming()
    {
        var procs = new List<ProcessInfo> { MakeProcess(100, "obs64", cpu: 10, gpu: 50) };

        var result = WorkloadClassifier.Classify(procs, gpuPercent: 50, cpuPercent: 20,
            out var primary);

        result.Should().Be(WorkloadType.Streaming);
        primary.Should().Be("obs64");
    }

    [Fact]
    public void Classify_BlenderProcess_Returns3DRendering()
    {
        var procs = new List<ProcessInfo> { MakeProcess(200, "blender", cpu: 80, gpu: 10) };

        var result = WorkloadClassifier.Classify(procs, gpuPercent: 10, cpuPercent: 80,
            out var primary);

        result.Should().Be(WorkloadType.ThreeDRendering);
        primary.Should().Be("blender");
    }

    [Fact]
    public void Classify_HighGpuHighCpu_ReturnsGaming()
    {
        // No known app name — falls through to gaming heuristic
        var procs = new List<ProcessInfo> { MakeProcess(300, "game.exe", cpu: 50, gpu: 80) };

        var result = WorkloadClassifier.Classify(procs, gpuPercent: 70, cpuPercent: 45,
            out _);

        result.Should().Be(WorkloadType.Gaming);
    }

    [Fact]
    public void Classify_HighCpuLowGpu_ReturnsGeneralCompute()
    {
        var procs = new List<ProcessInfo> { MakeProcess(400, "myapp", cpu: 90, gpu: 5) };

        var result = WorkloadClassifier.Classify(procs, gpuPercent: 10, cpuPercent: 80,
            out _);

        result.Should().Be(WorkloadType.GeneralCompute);
    }

    [Fact]
    public void Classify_SystemProcessFiltered_DoesNotContribute()
    {
        // "system" has pid 4 which is <= 8, but name "system" is also filtered by IsSystemProcess.
        // Use pid=4 AND name="system" — this process must be filtered out entirely.
        // With no valid processes remaining, and no high load heuristic, result is Unknown.
        var procs = new List<ProcessInfo>
        {
            new() { Pid = 4, Name = "system", CpuPercent = 90, GpuPercent = 90 }
        };

        var result = WorkloadClassifier.Classify(procs, gpuPercent: 10, cpuPercent: 10,
            out var primary);

        result.Should().Be(WorkloadType.Unknown);
        primary.Should().Be(string.Empty);
    }
}

// ── BottleneckDetector tests ───────────────────────────────────────────────────

/// <summary>
/// Tests for <see cref="BottleneckDetector.Analyse"/>.
///
/// IMPORTANT: The five smoothing queues in BottleneckDetector are static and shared
/// across all test runs in the same process. The <see cref="Prime"/> helper calls
/// Analyse 5 times with the same metrics object so that all 5 smoothing slots
/// contain the target values, ensuring smooth ≈ raw when asserting.
/// </summary>
public class BottleneckDetectorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SystemMetrics MakeMetrics(
        double cpu = 50, double mem = 50,
        double gpu = 0, double vram = 0,
        double disk = 0,
        double cpuTemp = 50, double gpuTemp = 50,
        double cpuFreq = 0, double cpuBase = 0) =>
        new()
        {
            Cpu = new CpuMetrics
            {
                TotalPercent = cpu,
                TemperatureCelsius = cpuTemp,
                FrequencyMhz = cpuFreq,
                BaseSpeedMhz = cpuBase,
            },
            Memory = new MemoryMetrics
            {
                TotalBytes = 16_000_000_000L,
                UsedBytes = (long)(16_000_000_000L * mem / 100.0),
            },
            Gpus = gpu > 0 || vram > 0
                ? [new GpuMetrics
                   {
                       UsagePercent = gpu,
                       TemperatureCelsius = gpuTemp,
                       DedicatedMemoryTotalBytes = 8_000_000_000L,
                       DedicatedMemoryUsedBytes = (long)(8_000_000_000L * vram / 100.0),
                   }]
                : [],
            Disks = disk > 0
                ? [new DiskMetrics { ActivePercent = disk }]
                : [],
        };

    /// <summary>
    /// Fills all 5 smoothing slots with the same values by calling Analyse 5 times,
    /// then returns the final result. This ensures smoothed ≈ raw for assertions.
    /// </summary>
    private static BottleneckReport Prime(SystemMetrics m, List<ProcessInfo>? procs = null)
    {
        var p = (IReadOnlyList<ProcessInfo>)(procs ?? []);
        BottleneckReport result = null!;
        for (int i = 0; i < 5; i++)
            result = BottleneckDetector.Analyse(m, p);
        return result;
    }

    // ── 1. Idle ───────────────────────────────────────────────────────────────

    [Fact]
    public void Analyse_LowLoad_ReturnsIdle()
    {
        // cpu=20, no GPU → maxLoad=20 < 35 (MinWorkloadLoad)
        var m = MakeMetrics(cpu: 20, mem: 40);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.Idle);
    }

    // ── 2. ThermalThrottle — CPU temperature ─────────────────────────────────

    [Fact]
    public void Analyse_CpuThermalAlert_ReturnsThermalThrottle()
    {
        // cpuTemp > 95 triggers cpuThermalAlert
        var m = MakeMetrics(cpu: 70, mem: 50, cpuTemp: 96);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.ThermalThrottle);
        result.Severity.Should().Be(BottleneckSeverity.Severe);
    }

    // ── 3. ThermalThrottle — GPU temperature ─────────────────────────────────

    [Fact]
    public void Analyse_GpuThermalAlert_ReturnsThermalThrottle()
    {
        // gpuTemp > 90 triggers gpuThermalAlert; need non-empty Gpus list
        // cpu=70 to stay above idle guard (maxLoad >= 35)
        var m = MakeMetrics(cpu: 50, gpu: 50, gpuTemp: 91);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.ThermalThrottle);
        result.Severity.Should().Be(BottleneckSeverity.Severe);
    }

    // ── 4. ThermalThrottle — CPU frequency throttling ─────────────────────────

    [Fact]
    public void Analyse_CpuFrequencyThrottling_ReturnsThermalThrottle()
    {
        // cpuFreq/cpuBase = 2800/3500 = 0.8 < 0.9 (ThrottleRatio)
        // cpu > 60 required for cpuThrottling check
        var m = MakeMetrics(cpu: 70, mem: 50, cpuFreq: 2800, cpuBase: 3500);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.ThermalThrottle);
        result.CpuIsThrottling.Should().BeTrue();
    }

    // ── 5. VramBound ──────────────────────────────────────────────────────────

    [Fact]
    public void Analyse_VramSaturated_ReturnsVramBound()
    {
        // vram=95 > 92 (MaxedHigh), gpu=50 < 80 (MaxedModerate)
        var m = MakeMetrics(cpu: 50, gpu: 50, vram: 95);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.VramBound);
    }

    // ── 6. GpuBound — Severe ─────────────────────────────────────────────────

    [Fact]
    public void Analyse_GpuMaxedCpuIdle_ReturnsGpuBoundSevere()
    {
        // gpu=95 >= 92, cpu=20 < 70 (MaxedModerate-10) and cpu < 30 (IdleThreshold)
        var m = MakeMetrics(cpu: 20, gpu: 95);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.GpuBound);
        result.Severity.Should().Be(BottleneckSeverity.Severe);
    }

    // ── 7. GpuBound — Moderate ───────────────────────────────────────────────

    [Fact]
    public void Analyse_GpuMaxedCpuModerate_ReturnsGpuBoundModerate()
    {
        // gpu=95 >= 92, cpu=60 < 70 but cpu >= 30 → Moderate
        var m = MakeMetrics(cpu: 60, gpu: 95);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.GpuBound);
        result.Severity.Should().Be(BottleneckSeverity.Moderate);
    }

    // ── 8. CpuBound — Severe ─────────────────────────────────────────────────

    [Fact]
    public void Analyse_CpuMaxedGpuIdle_ReturnsCpuBoundSevere()
    {
        // cpu=95 >= 92, gpu=20 < 70 (MaxedModerate-10) and gpu < 30 → Severe
        var m = MakeMetrics(cpu: 95, gpu: 20);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.CpuBound);
        result.Severity.Should().Be(BottleneckSeverity.Severe);
    }

    // ── 9. CpuBound — Moderate ───────────────────────────────────────────────

    [Fact]
    public void Analyse_CpuMaxedGpuModerate_ReturnsCpuBoundModerate()
    {
        // cpu=95 >= 92, gpu=60 < 70 but gpu >= 30 → Moderate
        var m = MakeMetrics(cpu: 95, gpu: 60);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.CpuBound);
        result.Severity.Should().Be(BottleneckSeverity.Moderate);
    }

    // ── 10. MemoryBound ───────────────────────────────────────────────────────

    [Fact]
    public void Analyse_RamFull_ReturnsMemoryBound()
    {
        // mem=95 > 92 (MaxedHigh), cpu=50 and gpu=50 (neither maxed at >= 92)
        var m = MakeMetrics(cpu: 50, mem: 95, gpu: 50);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.MemoryBound);
    }

    // ── 11. StorageBound ──────────────────────────────────────────────────────

    [Fact]
    public void Analyse_DiskMaxed_ReturnsStorageBound()
    {
        // disk=95 > 92, cpu=50 < 80, gpu=50 < 80
        var m = MakeMetrics(cpu: 50, mem: 50, gpu: 50, disk: 95);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.StorageBound);
    }

    // ── 12. Mild GpuBound ─────────────────────────────────────────────────────

    [Fact]
    public void Analyse_MildGpuBottleneck_ReturnsMildGpuBound()
    {
        // gpu=82 >= 80 (MaxedModerate), cpu=35 < 40 (IdleThreshold+10)
        var m = MakeMetrics(cpu: 35, gpu: 82);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.GpuBound);
        result.Severity.Should().Be(BottleneckSeverity.Mild);
    }

    // ── 13. Mild CpuBound ─────────────────────────────────────────────────────

    [Fact]
    public void Analyse_MildCpuBottleneck_ReturnsMildCpuBound()
    {
        // cpu=82 >= 80 (MaxedModerate), gpu=35 < 40 (IdleThreshold+10)
        var m = MakeMetrics(cpu: 82, gpu: 35);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.CpuBound);
        result.Severity.Should().Be(BottleneckSeverity.Mild);
    }

    // ── 14. Balanced ──────────────────────────────────────────────────────────

    [Fact]
    public void Analyse_BothModerate_ReturnsBalanced()
    {
        // cpu=60, gpu=60 — both above idle, neither triggers any prior branch
        var m = MakeMetrics(cpu: 60, gpu: 60);
        var result = Prime(m);

        result.Bottleneck.Should().Be(BottleneckType.Balanced);
    }

    // ── 15. Metric values populated in report ────────────────────────────────

    [Fact]
    public void Analyse_PopulatesMetricValues_InReport()
    {
        var m = MakeMetrics(cpu: 70, mem: 60, gpu: 70, vram: 50, disk: 30,
            cpuTemp: 65, gpuTemp: 70);
        var result = Prime(m);

        result.CpuPercent.Should().BeApproximately(70, 1.0);
        result.GpuPercent.Should().BeApproximately(70, 1.0);
        result.MemoryPercent.Should().BeApproximately(60, 1.0);
        result.GpuVramPercent.Should().BeApproximately(50, 1.0);
        result.DiskPercent.Should().BeApproximately(30, 1.0);
        result.CpuTempCelsius.Should().BeApproximately(65, 1.0);
        result.GpuTempCelsius.Should().BeApproximately(70, 1.0);
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── 16. ThermalThrottle takes priority over VramBound ────────────────────

    [Fact]
    public void Analyse_ThermalThrottlePriority_BeforeVramCheck()
    {
        // vram=95 > 92 would trigger VramBound, but gpuTemp=91 > 90 means ThermalThrottle wins
        var m = MakeMetrics(cpu: 50, gpu: 50, vram: 95, gpuTemp: 91);
        var result = Prime(m);

        // ThermalThrottle must win over VramBound
        result.Bottleneck.Should().Be(BottleneckType.ThermalThrottle);
    }

    // ── 17. HasLiveGpuData ────────────────────────────────────────────────────

    [Fact]
    public void HasLiveGpuData_EmptyList_ReturnsFalse()
    {
        BottleneckDetector.HasLiveGpuData([]).Should().BeFalse();
    }

    [Fact]
    public void HasLiveGpuData_StaticIdentityOnly_AllZero_ReturnsFalse()
    {
        // Mirrors macOS: GPU identity known (name/VRAM capacity) but no utilization API —
        // UsagePercent and DedicatedMemoryUsedBytes are hardcoded to 0 every sample.
        var gpus = new List<GpuMetrics>
        {
            new() { Name = "Apple M2 Pro", UsagePercent = 0, DedicatedMemoryUsedBytes = 0,
                    DedicatedMemoryTotalBytes = 16_000_000_000L },
        };

        BottleneckDetector.HasLiveGpuData(gpus).Should().BeFalse();
    }

    [Fact]
    public void HasLiveGpuData_NonZeroUsage_ReturnsTrue()
    {
        var gpus = new List<GpuMetrics> { new() { UsagePercent = 12 } };

        BottleneckDetector.HasLiveGpuData(gpus).Should().BeTrue();
    }

    [Fact]
    public void HasLiveGpuData_NonZeroMemoryUsedOnly_ReturnsTrue()
    {
        // Zero utilization but nonzero memory-used still counts as live telemetry (a real,
        // genuinely-idle GPU almost always shows some VRAM in use).
        var gpus = new List<GpuMetrics>
        {
            new() { UsagePercent = 0, DedicatedMemoryUsedBytes = 512_000_000L },
        };

        BottleneckDetector.HasLiveGpuData(gpus).Should().BeTrue();
    }
}
