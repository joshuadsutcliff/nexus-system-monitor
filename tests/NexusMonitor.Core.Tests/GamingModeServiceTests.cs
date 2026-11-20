using System.Diagnostics;
using System.Reactive.Subjects;
using FluentAssertions;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Gaming;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Tests.Helpers;
using Xunit;

namespace NexusMonitor.Core.Tests;

public sealed class GamingModeServiceTests
{
    private static ProcessInfo MakeProcess(string name, int pid) => new()
    {
        Pid = pid,
        Name = name,
        Category = ProcessCategory.UserApplication,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Timed out waiting for the service operation.");
            await Task.Delay(10);
        }
    }

    private static (GamingModeService service,
                    Subject<IReadOnlyList<ProcessInfo>> processSubject,
                    Mock<IProcessProvider> process,
                    Mock<IPowerPlanProvider> power)
        CreateService(IReadOnlyList<ProcessInfo> initialProcesses)
    {
        var process = Helpers.MockFactory.CreateProcessProvider();
        var power = new Mock<IPowerPlanProvider>(MockBehavior.Loose);
        var logger = Helpers.MockFactory.CreateLogger<GamingModeService>();
        var platform = new Mock<IPlatformCapabilities>(MockBehavior.Loose);
        platform.SetupGet(p => p.SupportsPowerPlan).Returns(false);

        IReadOnlyList<ProcessInfo> currentProcesses = initialProcesses;
        process.Setup(p => p.GetProcessesAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(currentProcesses));

        var processSubject = new Subject<IReadOnlyList<ProcessInfo>>();
        process.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>()))
            .Returns(processSubject);

        var service = new GamingModeService(
            process.Object, power.Object, new AppSettings(), logger.Object, platform.Object);
        return (service, processSubject, process, power);
    }

    [Fact]
    public async Task Start_NonNormalNativePriority_RestoresReceiptExactly()
    {
        const int pid = 2100;
        var receipt = new PriorityRestoreReceipt(pid, "test-native", 17);
        var (service, _, process, _) = CreateService(new[] { MakeProcess("background", pid) });
        process.Setup(p => p.ApplyTemporaryPriorityAsync(
                pid, ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityApplyResult.Success(receipt));

        service.Start();
        await WaitUntilAsync(() => service.PendingRestoreCount == 1);

        var report = await service.StopAsync();

        report.Restored.Should().Be(1);
        process.Verify(p => p.RestorePriorityAsync(
                It.Is<PriorityRestoreReceipt>(r =>
                    r.Pid == pid && r.ProviderKind == "test-native" && r.NativeValue == 17),
                It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(p => p.SetPriorityAsync(
                pid, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Dispose();
    }

    [Fact]
    public async Task ApplyPriorityFailure_DoesNotCreateRestoreState()
    {
        const int pid = 2101;
        var failure = new ProcessPriorityFailure(
            ProcessPriorityFailureKind.PermissionDenied, "test", 5, "denied");
        var (service, _, process, _) = CreateService(new[] { MakeProcess("background", pid) });
        process.Setup(p => p.ApplyTemporaryPriorityAsync(
                pid, ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityApplyResult.Failed(failure));
        var messages = new List<string>();
        service.StatusMessages.Subscribe(messages.Add);

        service.Start();
        await WaitUntilAsync(() => messages.Any(m => m.Contains("Throttle failed")));

        service.PendingRestoreCount.Should().Be(0);
        var report = await service.StopAsync();

        report.Attempted.Should().Be(0);
        process.Verify(p => p.RestorePriorityAsync(
                It.IsAny<PriorityRestoreReceipt>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Dispose();
    }

    [Fact]
    public async Task RestoreProcessExited_IsTerminalAndNotPending()
    {
        const int pid = 2102;
        var receipt = new PriorityRestoreReceipt(pid, "test-native", 9);
        var failure = new ProcessPriorityFailure(
            ProcessPriorityFailureKind.ProcessExited, "test", 3, "process exited");
        var (service, _, process, _) = CreateService(new[] { MakeProcess("background", pid) });
        process.Setup(p => p.ApplyTemporaryPriorityAsync(
                pid, ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityApplyResult.Success(receipt));
        process.Setup(p => p.RestorePriorityAsync(
                It.Is<PriorityRestoreReceipt>(r => r.Pid == pid), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityRestoreResult.Exited(failure));

        service.Start();
        await WaitUntilAsync(() => service.PendingRestoreCount == 1);

        var report = await service.StopAsync();

        report.Exited.Should().Be(1);
        report.Pending.Should().Be(0);
        service.PendingRestoreCount.Should().Be(0);
        process.Verify(p => p.RestorePriorityAsync(
                It.Is<PriorityRestoreReceipt>(r => r.NativeValue == 9),
                It.IsAny<CancellationToken>()), Times.Once);
        service.Dispose();
    }

    [Fact]
    public async Task PartialRestoreFailure_IsReportedPendingAndRetryClearsIt()
    {
        const int firstPid = 2103;
        const int secondPid = 2104;
        var firstReceipt = new PriorityRestoreReceipt(firstPid, "test-native", 7);
        var secondReceipt = new PriorityRestoreReceipt(secondPid, "test-native", 13);
        var (service, _, process, _) = CreateService(new[]
        {
            MakeProcess("first", firstPid),
            MakeProcess("second", secondPid),
        });
        process.Setup(p => p.ApplyTemporaryPriorityAsync(
                It.IsAny<int>(), ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pid, ProcessPriority _, CancellationToken _) =>
                PriorityApplyResult.Success(pid == firstPid ? firstReceipt : secondReceipt));

        var attempts = new Dictionary<int, int>();
        var failure = new ProcessPriorityFailure(
            ProcessPriorityFailureKind.PermissionDenied, "test", 13, "restore denied");
        process.Setup(p => p.RestorePriorityAsync(
                It.IsAny<PriorityRestoreReceipt>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PriorityRestoreReceipt receipt, CancellationToken _) =>
            {
                attempts[receipt.Pid] = attempts.GetValueOrDefault(receipt.Pid) + 1;
                return receipt.Pid == secondPid && attempts[receipt.Pid] == 1
                    ? PriorityRestoreResult.Failed(failure)
                    : PriorityRestoreResult.Restored();
            });
        var messages = new List<string>();
        service.StatusMessages.Subscribe(messages.Add);

        service.Start();
        await WaitUntilAsync(() => service.PendingRestoreCount == 2);

        var firstReport = await service.StopAsync();

        firstReport.Restored.Should().Be(1);
        firstReport.Pending.Should().Be(1);
        firstReport.IsPartial.Should().BeTrue();
        service.PendingRestoreCount.Should().Be(1);
        messages.Should().Contain(m => m.Contains("Priority restore incomplete"));

        var retryReport = await service.RetryPendingRestoresAsync();

        retryReport.Restored.Should().Be(1);
        retryReport.Pending.Should().Be(0);
        service.PendingRestoreCount.Should().Be(0);
        attempts[secondPid].Should().Be(2);
        service.Dispose();
    }
}
