using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class SystemHealthServiceTests
{
    private static SystemHealthService CreateService(
        IObservable<SystemMetrics> metrics,
        IObservable<IReadOnlyList<ProcessInfo>> processes,
        out TestScheduler scheduler)
    {
        scheduler = new TestScheduler();
        var metricsProvider = new Mock<ISystemMetricsProvider>();
        metricsProvider.Setup(m => m.GetMetricsStream(It.IsAny<TimeSpan>())).Returns(metrics);
        var processProvider = new Mock<IProcessProvider>();
        processProvider.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>())).Returns(processes);
        return new SystemHealthService(
            metricsProvider.Object, processProvider.Object, new AppSettings(),
            NullLogger<SystemHealthService>.Instance, scheduler);
    }

    // ── Decoupling ──────────────────────────────────────────────────────────

    [Fact]
    public void HealthSnapshots_KeepTicking_WhenProcessStreamNeverEmits()
    {
        var metrics   = new Subject<SystemMetrics>();
        var processes = new Subject<IReadOnlyList<ProcessInfo>>();
        using var svc = CreateService(metrics, processes, out _);

        var snapshots = new List<SystemHealthSnapshot>();
        using var sub = svc.HealthStream.Subscribe(snapshots.Add); // replays initial snapshot
        var initialCount = snapshots.Count;

        svc.Start(TimeSpan.FromSeconds(2));

        // Process stream is silent for the entire run.
        metrics.OnNext(new SystemMetrics());
        metrics.OnNext(new SystemMetrics());

        snapshots.Count.Should().Be(initialCount + 2);
    }

    [Fact]
    public void HealthSnapshots_KeepTicking_AfterProcessStreamErrors()
    {
        var metrics   = new Subject<SystemMetrics>();
        var processes = new Subject<IReadOnlyList<ProcessInfo>>();
        using var svc = CreateService(metrics, processes, out _);

        var snapshots = new List<SystemHealthSnapshot>();
        using var sub = svc.HealthStream.Subscribe(snapshots.Add);
        var initialCount = snapshots.Count;

        svc.Start(TimeSpan.FromSeconds(2));

        // One good process list, then the process stream faults hard (e.g. D-state /proc read).
        processes.OnNext(new List<ProcessInfo>());
        processes.OnError(new InvalidOperationException("process stream hung"));

        // The metrics-driven health cadence is unaffected.
        metrics.OnNext(new SystemMetrics());
        metrics.OnNext(new SystemMetrics());

        snapshots.Count.Should().Be(initialCount + 2);
    }

    // ── Staleness ───────────────────────────────────────────────────────────

    [Fact]
    public void IsStaleStream_FlipsTrue_WhenSnapshotsStop_AndClearsWhenTheyResume()
    {
        var metrics   = new Subject<SystemMetrics>();
        var processes = new Subject<IReadOnlyList<ProcessInfo>>();
        using var svc = CreateService(metrics, processes, out var scheduler);

        var staleValues = new List<bool>();
        using var sub = svc.IsStaleStream.Subscribe(staleValues.Add);

        svc.Start(TimeSpan.FromSeconds(2)); // stale threshold = 3× = 6s

        // A snapshot arrives → fresh.
        metrics.OnNext(new SystemMetrics());
        staleValues.Last().Should().BeFalse();

        // Just short of the threshold — still fresh.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(5).Ticks);
        staleValues.Last().Should().BeFalse();

        // Cross the 6s threshold with no new snapshot → stale.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);
        staleValues.Last().Should().BeTrue();

        // A fresh snapshot clears the stale flag.
        metrics.OnNext(new SystemMetrics());
        staleValues.Last().Should().BeFalse();
    }

    // ── GPU exclusion (static-identity-only telemetry) ────────────────────────

    [Fact]
    public void HealthSnapshot_StaticOnlyGpu_ExcludedFromScore_AndNotReportedAsExcellent()
    {
        var metrics   = new Subject<SystemMetrics>();
        var processes = new Subject<IReadOnlyList<ProcessInfo>>();
        using var svc = CreateService(metrics, processes, out _);

        SystemHealthSnapshot? snapshot = null;
        using var sub = svc.HealthStream.Subscribe(s => snapshot = s);

        svc.Start(TimeSpan.FromSeconds(2));

        // Mirrors macOS: GPU identity known (name/VRAM capacity) but no utilization API —
        // UsagePercent and DedicatedMemoryUsedBytes are hardcoded to 0 every sample even
        // though a real GPU is present. A moderately loaded CPU keeps the composite from
        // hitting the idle/no-op default snapshot.
        var staticGpuMetrics = new SystemMetrics
        {
            Cpu    = new CpuMetrics { TotalPercent = 50 },
            Memory = new MemoryMetrics { TotalBytes = 16_000_000_000L, UsedBytes = 8_000_000_000L },
            Gpus   = [new GpuMetrics
            {
                Name                      = "Apple M2 Pro",
                UsagePercent              = 0,
                DedicatedMemoryUsedBytes  = 0,
                DedicatedMemoryTotalBytes = 16_000_000_000L,
            }],
        };

        metrics.OnNext(staticGpuMetrics);

        snapshot.Should().NotBeNull();
        snapshot!.Gpu.HasData.Should().BeFalse("a static-identity-only GPU sample carries no live telemetry to score");
        snapshot.Gpu.Level.Should().NotBe(HealthLevel.Excellent, "a fabricated 0% reading must not be reported as a perfect GPU score");
        snapshot.Gpu.Summary.Should().Contain("unavailable");

        // Overall composite must match the GPU-excluded, renormalized calculation — not the
        // old formula that would fold the fabricated (perfect-scoring) GPU reading in at 15%.
        // gpuTemp is 0 (not -1/"unknown") here: a GPU is present, its TemperatureCelsius just
        // defaults to 0 like the rest of the static-only sample.
        var expectedOverall = HealthScoring.CompositeScore(
            HealthScoring.ScoreCpu(50),
            HealthScoring.ScoreMemory(50),
            HealthScoring.ScoreDisk(0, 0),
            gpuScore: 0,
            HealthScoring.ScoreThermal(cpuTempC: 0, gpuTempC: 0),
            includeGpu: false);
        snapshot.OverallScore.Should().BeApproximately(expectedOverall, 0.01);
    }

    [Fact]
    public void HealthSnapshot_LiveGpuData_StillScoredNormally()
    {
        var metrics   = new Subject<SystemMetrics>();
        var processes = new Subject<IReadOnlyList<ProcessInfo>>();
        using var svc = CreateService(metrics, processes, out _);

        SystemHealthSnapshot? snapshot = null;
        using var sub = svc.HealthStream.Subscribe(s => snapshot = s);

        svc.Start(TimeSpan.FromSeconds(2));

        var liveGpuMetrics = new SystemMetrics
        {
            Cpu    = new CpuMetrics { TotalPercent = 50 },
            Memory = new MemoryMetrics { TotalBytes = 16_000_000_000L, UsedBytes = 8_000_000_000L },
            Gpus   = [new GpuMetrics { Name = "RTX 4080", UsagePercent = 10, DedicatedMemoryUsedBytes = 1_000_000_000L, DedicatedMemoryTotalBytes = 16_000_000_000L }],
        };

        metrics.OnNext(liveGpuMetrics);

        snapshot.Should().NotBeNull();
        snapshot!.Gpu.HasData.Should().BeTrue("a nonzero utilization reading is genuine live telemetry");
        snapshot.Gpu.Summary.Should().Be("10% used");
    }
}
