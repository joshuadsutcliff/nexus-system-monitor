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
}
