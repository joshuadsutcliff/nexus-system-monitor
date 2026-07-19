using FluentAssertions;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using Xunit;
using MockFactory = NexusMonitor.Core.Tests.Helpers.MockFactory;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="BatchProcessActions"/> — the batch-semantics engine behind the process
/// table's multi-select actions (Ctrl/Cmd+click, Shift+click "act on the whole selection"). Lives
/// in Core (with a pure <see cref="IProcessProvider"/> dependency) because the UI assembly has no
/// test project of its own — the same "Core-adjacent logic in a UI assembly" carve-out
/// <c>GpuMemoryDisplayMathTests</c>/<c>BackdropMathTests</c> already document.
///
/// Design constraints this suite pins down:
/// - Never abort mid-batch on one item's failure — every target gets an attempt and a result.
/// - Protected processes (<see cref="ProcessCategory.SystemKernel"/> — mirrors
///   <c>AutoBalanceService</c>'s throttle-candidate exclusion) are skipped without ever touching
///   the provider for that pid.
/// - Reuses the "read the real state before mutating, skip if unknown" principle
///   (<c>AutoBalanceService</c>/<c>IdleThrottleService</c>'s <c>GetPriorityAsync</c>-or-skip
///   pattern) as a liveness/accessibility probe before every mutating call — IProcessProvider
///   has no lighter-weight "is this pid still valid" primitive, so GetPriorityAsync doubles as
///   the pre-flight existence check. A null result is reported as AlreadyExited.
/// - Genuine failures during the mutating call itself (the interface only exposes generic
///   exceptions — no typed Win32/errno error codes) are reported as AccessDenied, since a
///   process that answered the pre-flight probe but then rejected the actual privileged
///   operation is overwhelmingly a permission failure in practice. The raw exception message is
///   preserved in Detail for anyone who wants the specifics.
/// </summary>
public class BatchProcessActionsTests
{
    private static BatchTarget Target(int pid, string name, ProcessCategory category = ProcessCategory.UserApplication) =>
        new(pid, name, category);

    // ── Kill: happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task KillAsync_AllAlive_AllSucceed()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "a"), Target(2, "b"), Target(3, "c") };
        var result = await sut.KillAsync(targets, killTree: false);

        result.TotalCount.Should().Be(3);
        result.SuccessCount.Should().Be(3);
        result.FailureCount.Should().Be(0);
        result.AllSucceeded.Should().BeTrue();
        result.Items.Should().OnlyContain(i => i.Status == BatchItemStatus.Success);

        mock.Verify(p => p.KillProcessAsync(1, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.KillProcessAsync(2, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.KillProcessAsync(3, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KillAsync_ResultsPreserveInputOrder()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(30, "c"), Target(10, "a"), Target(20, "b") };
        var result = await sut.KillAsync(targets, killTree: false);

        result.Items.Select(i => i.Pid).Should().ContainInOrder(30, 10, 20);
    }

    [Fact]
    public async Task KillAsync_PassesKillTreeThrough()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        await sut.KillAsync(new[] { Target(1, "a") }, killTree: true);

        mock.Verify(p => p.KillProcessAsync(1, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Kill: per-item failure tolerance (requirement 4) ───────────────────

    [Fact]
    public async Task KillAsync_OneItemThrows_OtherItemsStillAttempted()
    {
        var mock = MockFactory.CreateProcessProvider();
        mock.Setup(p => p.KillProcessAsync(2, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cannot open process 2: 5"));
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "a"), Target(2, "b"), Target(3, "c") };
        var result = await sut.KillAsync(targets, killTree: false);

        result.TotalCount.Should().Be(3);
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(1);
        result.AllSucceeded.Should().BeFalse();

        mock.Verify(p => p.KillProcessAsync(1, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.KillProcessAsync(2, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.KillProcessAsync(3, false, It.IsAny<CancellationToken>()), Times.Once);

        result.Items.Single(i => i.Pid == 2).Status.Should().Be(BatchItemStatus.AccessDenied);
        result.Items.Single(i => i.Pid == 2).Detail.Should().Contain("Cannot open process 2");
    }

    [Fact]
    public async Task KillAsync_MultipleFailures_AllCaptured_NeverAborts()
    {
        var mock = MockFactory.CreateProcessProvider();
        mock.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "a"), Target(2, "b"), Target(3, "c") };
        var result = await sut.KillAsync(targets, killTree: false);

        result.TotalCount.Should().Be(3);
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(3);
        result.Items.Should().OnlyContain(i => i.Status == BatchItemStatus.AccessDenied);
    }

    // ── Kill: protected-process guard (per item, never blocks the rest) ───

    [Fact]
    public async Task KillAsync_ProtectedProcess_SkippedWithoutTouchingProvider()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[]
        {
            Target(1, "a", ProcessCategory.UserApplication),
            Target(2, "kernel_task", ProcessCategory.SystemKernel),
            Target(3, "c", ProcessCategory.UserApplication),
        };
        var result = await sut.KillAsync(targets, killTree: false);

        result.SuccessCount.Should().Be(2);
        result.Items.Single(i => i.Pid == 2).Status.Should().Be(BatchItemStatus.ProtectedSkipped);

        mock.Verify(p => p.KillProcessAsync(2, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(p => p.GetPriorityAsync(2, It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(p => p.KillProcessAsync(1, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.KillProcessAsync(3, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Kill: read-real-priority-or-skip liveness probe ────────────────────

    [Fact]
    public async Task KillAsync_ProbeReturnsNull_SkippedAsAlreadyExited_KillNeverCalled()
    {
        var mock = MockFactory.CreateProcessProvider();
        mock.Setup(p => p.GetPriorityAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessPriority?)null);
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "a"), Target(2, "gone"), Target(3, "c") };
        var result = await sut.KillAsync(targets, killTree: false);

        result.SuccessCount.Should().Be(2);
        result.Items.Single(i => i.Pid == 2).Status.Should().Be(BatchItemStatus.AlreadyExited);

        mock.Verify(p => p.KillProcessAsync(2, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Kill: cancellation propagates (not swallowed as a per-item failure) ─

    [Fact]
    public async Task KillAsync_Cancelled_ThrowsOperationCanceled_NotSwallowed()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var targets = new[] { Target(1, "a"), Target(2, "b") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.KillAsync(targets, killTree: false, cts.Token));
    }

    // ── Empty / single-item edge cases ─────────────────────────────────────

    [Fact]
    public async Task KillAsync_EmptySelection_ReturnsEmptyResult()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var result = await sut.KillAsync(Array.Empty<BatchTarget>(), killTree: false);

        result.TotalCount.Should().Be(0);
        result.SuccessCount.Should().Be(0);
        result.AllSucceeded.Should().BeFalse();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task KillAsync_SingleItem_BehavesLikeOneElementBatch()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var result = await sut.KillAsync(new[] { Target(1, "a") }, killTree: false);

        result.TotalCount.Should().Be(1);
        result.SuccessCount.Should().Be(1);
        result.AllSucceeded.Should().BeTrue();
    }

    // ── SetPriority: reuses the same engine ────────────────────────────────

    [Fact]
    public async Task SetPriorityAsync_AllAlive_AllSucceed()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "a"), Target(2, "b") };
        var result = await sut.SetPriorityAsync(targets, ProcessPriority.High);

        result.SuccessCount.Should().Be(2);
        mock.Verify(p => p.SetPriorityAsync(1, ProcessPriority.High, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.SetPriorityAsync(2, ProcessPriority.High, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetPriorityAsync_ProtectedProcess_SkippedWithoutTouchingProvider()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "kernel_task", ProcessCategory.SystemKernel), Target(2, "b") };
        var result = await sut.SetPriorityAsync(targets, ProcessPriority.High);

        result.Items.Single(i => i.Pid == 1).Status.Should().Be(BatchItemStatus.ProtectedSkipped);
        mock.Verify(p => p.SetPriorityAsync(1, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetPriorityAsync_ProbeNull_SkippedAsAlreadyExited()
    {
        var mock = MockFactory.CreateProcessProvider();
        mock.Setup(p => p.GetPriorityAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessPriority?)null);
        var sut = new BatchProcessActions(mock.Object);

        var result = await sut.SetPriorityAsync(new[] { Target(1, "gone") }, ProcessPriority.High);

        result.Items.Single().Status.Should().Be(BatchItemStatus.AlreadyExited);
        mock.Verify(p => p.SetPriorityAsync(1, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetPriorityAsync_ThrowsForOneItem_CapturedAsAccessDenied_OthersUnaffected()
    {
        var mock = MockFactory.CreateProcessProvider();
        mock.Setup(p => p.SetPriorityAsync(1, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cannot open process 1"));
        var sut = new BatchProcessActions(mock.Object);

        var result = await sut.SetPriorityAsync(new[] { Target(1, "a"), Target(2, "b") }, ProcessPriority.Idle);

        result.Items.Single(i => i.Pid == 1).Status.Should().Be(BatchItemStatus.AccessDenied);
        result.Items.Single(i => i.Pid == 2).Status.Should().Be(BatchItemStatus.Success);
    }

    // ── SetAffinity: reuses the same engine ────────────────────────────────

    [Fact]
    public async Task SetAffinityAsync_AllAlive_AllSucceed()
    {
        var mock = MockFactory.CreateProcessProvider();
        var sut = new BatchProcessActions(mock.Object);

        var targets = new[] { Target(1, "a"), Target(2, "b") };
        var result = await sut.SetAffinityAsync(targets, 0b11);

        result.SuccessCount.Should().Be(2);
        mock.Verify(p => p.SetAffinityAsync(1, 0b11, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(p => p.SetAffinityAsync(2, 0b11, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAffinityAsync_OneFails_OtherStillApplied()
    {
        var mock = MockFactory.CreateProcessProvider();
        mock.Setup(p => p.SetAffinityAsync(1, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("denied"));
        var sut = new BatchProcessActions(mock.Object);

        var result = await sut.SetAffinityAsync(new[] { Target(1, "a"), Target(2, "b") }, 0b1);

        result.Items.Single(i => i.Pid == 1).Status.Should().Be(BatchItemStatus.AccessDenied);
        result.Items.Single(i => i.Pid == 2).Status.Should().Be(BatchItemStatus.Success);
    }

    // ── Summary message formatting (requirement 4's "one honest summary") ──

    [Fact]
    public void Summarize_AllSucceeded_Multi()
    {
        var result = new BatchActionResult(new[]
        {
            new BatchItemResult(1, "a", BatchItemStatus.Success),
            new BatchItemResult(2, "b", BatchItemStatus.Success),
            new BatchItemResult(3, "c", BatchItemStatus.Success),
        });

        result.Summarize("Ended").Should().Be("Ended all 3 processes.");
    }

    [Fact]
    public void Summarize_SingleSuccess_NamesTheProcess()
    {
        var result = new BatchActionResult(new[] { new BatchItemResult(1, "chrome", BatchItemStatus.Success) });
        result.Summarize("Ended").Should().Be("Ended chrome.");
    }

    [Fact]
    public void Summarize_SingleFailure_AccessDenied()
    {
        var result = new BatchActionResult(new[] { new BatchItemResult(1, "chrome", BatchItemStatus.AccessDenied) });
        result.Summarize("Ended").Should().Be("chrome — access denied.");
    }

    [Fact]
    public void Summarize_SingleFailure_AlreadyExited()
    {
        var result = new BatchActionResult(new[] { new BatchItemResult(1, "chrome", BatchItemStatus.AlreadyExited) });
        result.Summarize("Ended").Should().Be("chrome had already exited.");
    }

    [Fact]
    public void Summarize_SingleFailure_ProtectedSkipped()
    {
        var result = new BatchActionResult(new[] { new BatchItemResult(1, "kernel_task", BatchItemStatus.ProtectedSkipped) });
        result.Summarize("Ended").Should().Be("kernel_task is a protected system process — skipped.");
    }

    [Fact]
    public void Summarize_PartialFailure_SingleReason_MatchesSpecExample()
    {
        // "Ended 3 of 5 — 2 failed: access denied" — the exact example from the requirement.
        var result = new BatchActionResult(new[]
        {
            new BatchItemResult(1, "a", BatchItemStatus.Success),
            new BatchItemResult(2, "b", BatchItemStatus.Success),
            new BatchItemResult(3, "c", BatchItemStatus.Success),
            new BatchItemResult(4, "d", BatchItemStatus.AccessDenied),
            new BatchItemResult(5, "e", BatchItemStatus.AccessDenied),
        });

        result.Summarize("Ended").Should().Be("Ended 3 of 5 — 2 failed: access denied.");
    }

    [Fact]
    public void Summarize_PartialFailure_MixedReasons_ListsEachWithCount()
    {
        var result = new BatchActionResult(new[]
        {
            new BatchItemResult(1, "a", BatchItemStatus.Success),
            new BatchItemResult(2, "b", BatchItemStatus.AccessDenied),
            new BatchItemResult(3, "c", BatchItemStatus.AlreadyExited),
            new BatchItemResult(4, "d", BatchItemStatus.ProtectedSkipped),
        });

        result.Summarize("Ended").Should().Be(
            "Ended 1 of 4 — 3 failed: 1 access denied, 1 already exited, 1 protected.");
    }

    [Fact]
    public void Summarize_AllFailed_SingleReason()
    {
        var result = new BatchActionResult(new[]
        {
            new BatchItemResult(1, "a", BatchItemStatus.ProtectedSkipped),
            new BatchItemResult(2, "b", BatchItemStatus.ProtectedSkipped),
        });

        result.Summarize("Ended").Should().Be("Ended 0 of 2 — 2 failed: protected.");
    }

    [Fact]
    public void Summarize_EmptySelection()
    {
        var result = new BatchActionResult(Array.Empty<BatchItemResult>());
        result.Summarize("Ended").Should().Be("No processes selected.");
    }

    // ── Name-list truncation for the confirmation dialog ───────────────────

    [Theory]
    [InlineData(new string[] { }, "")]
    public void FormatNameList_Empty(string[] names, string expected)
    {
        BatchConfirmationText.FormatNameList(names).Should().Be(expected);
    }

    [Fact]
    public void FormatNameList_UnderLimit_JoinsAllWithNoTruncation()
    {
        var names = new[] { "chrome", "notepad", "explorer" };
        BatchConfirmationText.FormatNameList(names).Should().Be("chrome, notepad, explorer");
    }

    [Fact]
    public void FormatNameList_ExactlyEight_NoTruncationSuffix()
    {
        var names = Enumerable.Range(1, 8).Select(i => $"proc{i}").ToArray();
        var result = BatchConfirmationText.FormatNameList(names);

        result.Should().Be("proc1, proc2, proc3, proc4, proc5, proc6, proc7, proc8");
        result.Should().NotContain("more");
    }

    [Fact]
    public void FormatNameList_NineItems_ShowsEightPlusAndOneMore()
    {
        var names = Enumerable.Range(1, 9).Select(i => $"proc{i}").ToArray();
        var result = BatchConfirmationText.FormatNameList(names);

        result.Should().Be("proc1, proc2, proc3, proc4, proc5, proc6, proc7, proc8, and 1 more");
    }

    [Fact]
    public void FormatNameList_ManyItems_ShowsEightPlusRemainingCount()
    {
        var names = Enumerable.Range(1, 20).Select(i => $"proc{i}").ToArray();
        var result = BatchConfirmationText.FormatNameList(names);

        result.Should().EndWith("and 12 more");
        result.Split(", ").Should().HaveCount(9); // 8 names + "and 12 more" trailing segment
    }

    // ── Confirmation message builder (requirement 3) ───────────────────────

    [Fact]
    public void BuildKillConfirmationMessage_TwoProcesses()
    {
        var msg = BatchConfirmationText.BuildKillConfirmationMessage(new[] { "chrome", "notepad" });
        msg.Should().Contain("2 processes");
        msg.Should().Contain("chrome, notepad");
    }

    [Fact]
    public void BuildKillConfirmationMessage_TruncatesAtEight()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"proc{i}").ToArray();
        var msg = BatchConfirmationText.BuildKillConfirmationMessage(names);

        msg.Should().Contain("10 processes");
        msg.Should().Contain("and 2 more");
    }
}
