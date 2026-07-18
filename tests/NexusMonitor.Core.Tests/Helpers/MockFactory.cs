using Microsoft.Extensions.Logging;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using System.Reactive.Linq;

namespace NexusMonitor.Core.Tests.Helpers;

/// <summary>
/// Central factory for Moq mocks used across the test suite.
/// All mocks are pre-configured with safe, non-throwing defaults.
/// </summary>
public static class MockFactory
{
    /// <summary>
    /// Creates a <see cref="Mock{IProcessProvider}"/> with default setups:
    /// - GetProcessesAsync returns an empty list
    /// - GetProcessStream returns Observable.Empty
    /// - All mutation methods complete successfully (no-op Task.CompletedTask)
    /// </summary>
    public static Mock<IProcessProvider> CreateProcessProvider()
    {
        var mock = new Mock<IProcessProvider>(MockBehavior.Loose);

        mock.Setup(p => p.GetProcessesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessInfo>());

        mock.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>()))
            .Returns(Observable.Empty<IReadOnlyList<ProcessInfo>>());

        mock.Setup(p => p.GetModulesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ModuleInfo>());

        mock.Setup(p => p.GetThreadsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ThreadInfo>());

        mock.Setup(p => p.GetEnvironmentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EnvironmentEntry>());

        mock.Setup(p => p.GetHandlesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HandleInfo>());

        mock.Setup(p => p.GetMemoryMapAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemoryRegionInfo>());

        mock.Setup(p => p.GetAffinityMasksAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((0L, 0L));

        // Plain-Task mutation methods — must return Task.CompletedTask so awaiting them doesn't NRE
        mock.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.SuspendProcessAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.ResumeProcessAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.SetPriorityAsync(It.IsAny<int>(), It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.GetPriorityAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.Normal);

        mock.Setup(p => p.SetAffinityAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.SetIoPriorityAsync(It.IsAny<int>(), It.IsAny<IoPriority>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.SetMemoryPriorityAsync(It.IsAny<int>(), It.IsAny<MemoryPriority>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.TrimWorkingSetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.SetEfficiencyModeAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.CreateDumpFileAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.SetCpuSetsAsync(It.IsAny<int>(), It.IsAny<uint[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Creates a <see cref="Mock{ISystemMetricsProvider}"/> with default setups:
    /// - GetMetricsAsync returns a zero-valued SystemMetrics
    /// - GetMetricsStream returns Observable.Empty
    /// </summary>
    public static Mock<ISystemMetricsProvider> CreateMetricsProvider()
    {
        var mock = new Mock<ISystemMetricsProvider>(MockBehavior.Loose);

        mock.Setup(p => p.GetMetricsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemMetrics());

        mock.Setup(p => p.GetMetricsStream(It.IsAny<TimeSpan>()))
            .Returns(Observable.Empty<SystemMetrics>());

        return mock;
    }

    /// <summary>
    /// Creates a loose <see cref="Mock{ILogger}"/> so log calls never throw,
    /// and <c>IsEnabled</c> returns true for all log levels.
    /// </summary>
    public static Mock<ILogger<T>> CreateLogger<T>()
    {
        var mock = new Mock<ILogger<T>>(MockBehavior.Loose);
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return mock;
    }
}
