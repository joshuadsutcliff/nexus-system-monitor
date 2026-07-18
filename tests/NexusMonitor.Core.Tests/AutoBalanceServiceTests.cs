using System.Diagnostics;
using System.Reactive.Subjects;
using FluentAssertions;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Tests.Helpers;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Covers C1 (AutoBalanceService must read a throttled process's REAL priority before
/// throttling it, never assume Normal — see the deleted InferPriority) and C2 (Stop/Dispose
/// must not abandon an in-flight restore).
/// </summary>
public class AutoBalanceServiceTests
{
    private const double Threshold = 10.0;

    private static ProcessInfo MakeProcess(int pid, double cpu) => new()
    {
        Pid = pid,
        Name = $"proc{pid}",
        Category = ProcessCategory.UserApplication,
        CpuPercent = cpu,
    };

    private (AutoBalanceService svc,
             Subject<IReadOnlyList<ProcessInfo>> processSubject,
             Mock<IProcessProvider> mockProcess,
             Mock<IForegroundWindowProvider> mockFg)
        CreateService()
    {
        var mockProcess = Helpers.MockFactory.CreateProcessProvider();
        var mockFg      = new Mock<IForegroundWindowProvider>(MockBehavior.Loose);
        mockFg.Setup(f => f.GetForegroundProcessId()).Returns(0);
        var logger = Helpers.MockFactory.CreateLogger<AutoBalanceService>();

        var settings = new AppSettings
        {
            AutoBalanceEnabled      = true,
            AutoBalanceCpuThreshold = Threshold,
        };

        var processSubject = new Subject<IReadOnlyList<ProcessInfo>>();
        mockProcess.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>())).Returns(processSubject);

        var svc = new AutoBalanceService(mockProcess.Object, mockFg.Object, settings, logger.Object);
        return (svc, processSubject, mockProcess, mockFg);
    }

    /// <summary>Polls until <paramref name="condition"/> is true or <paramref name="timeout"/> elapses.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(25);
    }

    private static bool WasCalledWith(Mock<IProcessProvider> mock, int pid, ProcessPriority priority) =>
        mock.Invocations.Any(i =>
            i.Method.Name == nameof(IProcessProvider.SetPriorityAsync) &&
            (int)i.Arguments[0] == pid &&
            (ProcessPriority)i.Arguments[1] == priority);

    [Fact]
    public async Task Throttle_HighPriorityHog_RestoresToHigh_NeverNormal()
    {
        var (svc, processSubject, mockProcess, _) = CreateService();
        const int pid = 5001;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.High);

        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid, 50.0) }); // totalCpu > Threshold

        await WaitUntilAsync(() => WasCalledWith(mockProcess, pid, ProcessPriority.BelowNormal), TimeSpan.FromSeconds(3));
        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()), Times.Once);

        // Drive totalCpu below Threshold * 0.7 to trigger the restore branch.
        processSubject.OnNext(Array.Empty<ProcessInfo>());

        await WaitUntilAsync(() => WasCalledWith(mockProcess, pid, ProcessPriority.High), TimeSpan.FromSeconds(3));
        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.High, It.IsAny<CancellationToken>()), Times.Once);
        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.Normal, It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }

    [Fact]
    public async Task Throttle_PriorityReadReturnsNull_NeverSetsThatPid()
    {
        var (svc, processSubject, mockProcess, _) = CreateService();
        const int pid = 5002;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)null);

        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid, 50.0) });

        // Give the sample interval time to fire at least once.
        await WaitUntilAsync(() => mockProcess.Invocations.Any(i => i.Method.Name == nameof(IProcessProvider.GetPriorityAsync)),
            TimeSpan.FromSeconds(3));
        await Task.Delay(200);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }

    [Fact]
    public async Task Throttle_AlreadyBelowNormal_LeftUntouched()
    {
        var (svc, processSubject, mockProcess, _) = CreateService();
        const int pid = 5003;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.BelowNormal);

        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid, 50.0) });

        await WaitUntilAsync(() => mockProcess.Invocations.Any(i => i.Method.Name == nameof(IProcessProvider.GetPriorityAsync)),
            TimeSpan.FromSeconds(3));
        await Task.Delay(200);

        mockProcess.Verify(p => p.SetPriorityAsync(pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);

        svc.Dispose();
    }

    // ── C2: shutdown must not abandon an in-flight restore ──────────────────

    [Fact]
    public async Task Dispose_BlocksUntilInFlightRestoreCompletes()
    {
        var (svc, processSubject, mockProcess, _) = CreateService();
        const int pid = 5004;
        mockProcess.Setup(p => p.GetPriorityAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)ProcessPriority.High);

        svc.Start();
        processSubject.OnNext(new[] { MakeProcess(pid, 50.0) });
        await WaitUntilAsync(() => WasCalledWith(mockProcess, pid, ProcessPriority.BelowNormal), TimeSpan.FromSeconds(3));

        // From this point, SetPriorityAsync blocks on a TCS that a background timer completes
        // ~150ms later. Dispose() must not return until the restore that call is part of has
        // actually finished — that's the C2 fix (bounded Wait on a tracked restore task).
        var tcs = new TaskCompletionSource();
        mockProcess.Setup(p => p.SetPriorityAsync(pid, ProcessPriority.High, It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);
        _ = Task.Run(async () => { await Task.Delay(150); tcs.SetResult(); });

        var sw = Stopwatch.StartNew();
        svc.Dispose(); // triggers Stop() -> RestoreAllAsync() -> SetPriorityAsync(pid, High, ...)
        sw.Stop();

        mockProcess.Verify(p => p.SetPriorityAsync(pid, ProcessPriority.High, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(120));
    }
}
