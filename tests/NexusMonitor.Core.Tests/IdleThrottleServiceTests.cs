using System.Reactive.Subjects;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Tests.Helpers;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Covers C1 for IdleThrottleService: before throttling an idle process it must read the
/// process's REAL current priority (never hardcode Normal), skip entirely when the read fails
/// or the process is already at/below BelowNormal, and restore to the REAL saved value.
/// </summary>
public class IdleThrottleServiceTests
{
    private static ProcessInfo MakeProcess(int pid, double cpu) => new()
    {
        Pid = pid,
        Name = $"proc{pid}",
        Category = ProcessCategory.UserApplication,
        CpuPercent = cpu,
    };

    private (IdleThrottleService svc,
             Subject<IReadOnlyList<ProcessInfo>> processSubject,
             Mock<IProcessProvider> mockProcess)
        CreateService()
    {
        var mockProcess = Helpers.MockFactory.CreateProcessProvider();
        var mockFg      = new Mock<IForegroundWindowProvider>(MockBehavior.Loose);
        mockFg.Setup(f => f.GetForegroundProcessId()).Returns(0);
        var logger    = Helpers.MockFactory.CreateLogger<IdleThrottleService>();
        var actionLock = new ProcessActionLock();

        var settings = new AppSettings
        {
            IdleThrottleEnabled           = true,
            IdleThrottleCpuThreshold      = 1.0,
            IdleThrottleIdleTicksRequired = 1,
            IdleThrottleUseEfficiencyMode = false,
        };

        var processSubject = new Subject<IReadOnlyList<ProcessInfo>>();
        mockProcess.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>())).Returns(processSubject);

        var svc = new IdleThrottleService(mockProcess.Object, mockFg.Object, settings, actionLock, logger.Object);
        return (svc, processSubject, mockProcess);
    }

    [Fact]
    public async Task Throttle_PriorityReadReturnsNull_SkipsEntirely()
    {
        var (svc, processSubject, mockProcess) = CreateService();
        const int pid = 6001;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)null);

        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid, 0.1) }); // below threshold -> idle tick
        await Task.Delay(150);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }

    [Fact]
    public async Task Throttle_AlreadyBelowNormal_SkipsEntirely()
    {
        var (svc, processSubject, mockProcess) = CreateService();
        const int pid = 6002;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.BelowNormal);

        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid, 0.1) });
        await Task.Delay(150);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }

    [Fact]
    public async Task Throttle_RestoresRealOriginal_NeverNormal()
    {
        var (svc, processSubject, mockProcess) = CreateService();
        const int pid = 6003;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.High);

        svc.Start();

        // Tick 1: idle -> throttled, saving the REAL original (High).
        processSubject.OnNext(new[] { MakeProcess(pid, 0.1) });
        await Task.Delay(150);
        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()), Times.Once);

        // Tick 2: CPU spikes well above threshold*2 -> restore branch fires.
        processSubject.OnNext(new[] { MakeProcess(pid, 10.0) });
        await Task.Delay(150);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.High, It.IsAny<CancellationToken>()), Times.Once);
        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.Normal, It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }
}
