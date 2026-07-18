using System.Diagnostics;
using System.Reactive.Subjects;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Tests.Helpers;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Covers C1 for ForegroundBoostService: before boosting the new foreground process it must
/// read the process's REAL current priority (never hardcode Normal), skip a process already at
/// or above AboveNormal (never downgrade a High/RealTime foreground process), skip when the
/// read fails, and restore the previous foreground process to its REAL saved value.
/// </summary>
public class ForegroundBoostServiceTests
{
    private static ProcessInfo MakeProcess(int pid) => new()
    {
        Pid = pid,
        Name = $"proc{pid}",
        Category = ProcessCategory.UserApplication,
    };

    private (ForegroundBoostService svc,
             Subject<IReadOnlyList<ProcessInfo>> processSubject,
             Mock<IProcessProvider> mockProcess,
             Mock<IForegroundWindowProvider> mockFg)
        CreateService()
    {
        var mockProcess = Helpers.MockFactory.CreateProcessProvider();
        var mockFg      = new Mock<IForegroundWindowProvider>(MockBehavior.Loose);
        var logger      = Helpers.MockFactory.CreateLogger<ForegroundBoostService>();
        var actionLock  = new ProcessActionLock();

        var settings = new AppSettings { ForegroundBoostEnabled = true };

        var processSubject = new Subject<IReadOnlyList<ProcessInfo>>();
        mockProcess.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>())).Returns(processSubject);

        var svc = new ForegroundBoostService(mockProcess.Object, mockFg.Object, settings, actionLock, logger.Object);
        return (svc, processSubject, mockProcess, mockFg);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(25);
    }

    [Fact]
    public async Task NormalForegroundProcess_BoostedThenRestoredToNormal_OnFocusChange()
    {
        var (svc, processSubject, mockProcess, mockFg) = CreateService();
        const int pidA = 7001;
        const int pidB = 7002;
        mockProcess.Setup(p => p.GetPriorityAsync(pidA, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.Normal);
        mockProcess.Setup(p => p.GetPriorityAsync(pidB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.Normal);

        mockFg.Setup(f => f.GetForegroundProcessId()).Returns(pidA);
        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pidA) });

        await WaitUntilAsync(
            () => mockProcess.Invocations.Any(i =>
                i.Method.Name == nameof(IProcessProvider.SetPriorityAsync) &&
                (int)i.Arguments[0] == pidA && (ProcessPriority)i.Arguments[1] == ProcessPriority.AboveNormal),
            TimeSpan.FromSeconds(3));
        mockProcess.Verify(p => p.SetPriorityAsync(pidA, ProcessPriority.AboveNormal, It.IsAny<CancellationToken>()), Times.Once);

        // Focus moves to pidB — pidA must still be present so it isn't evicted as dead before
        // the restore-previous-foreground step runs.
        mockFg.Setup(f => f.GetForegroundProcessId()).Returns(pidB);
        processSubject.OnNext(new[] { MakeProcess(pidA), MakeProcess(pidB) });

        await WaitUntilAsync(
            () => mockProcess.Invocations.Any(i =>
                i.Method.Name == nameof(IProcessProvider.SetPriorityAsync) &&
                (int)i.Arguments[0] == pidA && (ProcessPriority)i.Arguments[1] == ProcessPriority.Normal),
            TimeSpan.FromSeconds(3));
        mockProcess.Verify(p => p.SetPriorityAsync(pidA, ProcessPriority.Normal, It.IsAny<CancellationToken>()), Times.Once);

        svc.Dispose();
    }

    [Fact]
    public async Task HighPriorityForegroundProcess_NeverBoosted()
    {
        var (svc, processSubject, mockProcess, mockFg) = CreateService();
        const int pid = 7003;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.High);

        mockFg.Setup(f => f.GetForegroundProcessId()).Returns(pid);
        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid) });

        await WaitUntilAsync(
            () => mockProcess.Invocations.Any(i => i.Method.Name == nameof(IProcessProvider.GetPriorityAsync)),
            TimeSpan.FromSeconds(3));
        await Task.Delay(200);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }

    [Fact]
    public async Task PriorityReadReturnsNull_NeverBoosted()
    {
        var (svc, processSubject, mockProcess, mockFg) = CreateService();
        const int pid = 7004;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)null);

        mockFg.Setup(f => f.GetForegroundProcessId()).Returns(pid);
        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid) });

        await WaitUntilAsync(
            () => mockProcess.Invocations.Any(i => i.Method.Name == nameof(IProcessProvider.GetPriorityAsync)),
            TimeSpan.FromSeconds(3));
        await Task.Delay(200);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }
}
