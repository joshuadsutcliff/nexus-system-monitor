using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageEditSessionTests
{
    private static PageLayout Factory() => BuiltInPageLayouts.Load("dashboard");

    [Fact]
    public void NewSession_CleanState()
    {
        var s = new PageEditSession(Factory());
        s.CanUndo.Should().BeFalse();
        s.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Move_ChangesCurrent_AndEnablesUndo()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);

        s.Move(id, new GridRect(0, 10, 4, 2));

        s.Current.FindWidget(id)!.Rect.Row.Should().Be(10);
        s.CanUndo.Should().BeTrue();
        s.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Move_UnknownId_IsNoOp_NoUndoEntry()
    {
        var s = new PageEditSession(Factory());
        s.Move(Guid.NewGuid(), new GridRect(0, 10, 4, 2));
        s.CanUndo.Should().BeFalse();
        s.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Undo_RestoresPreviousState_StepByStep()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);

        s.Move(id, new GridRect(0, 10, 4, 2));
        s.Remove(page.Widgets[1].InstanceId);
        s.Undo(); // undo the remove

        s.Current.Widgets.Count.Should().Be(page.Widgets.Count);
        s.Current.FindWidget(id)!.Rect.Row.Should().Be(10);

        s.Undo(); // undo the move
        s.Current.FindWidget(id)!.Rect.Should().Be(page.Widgets[0].Rect);
        s.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Cancel_ReturnsOriginal_RegardlessOfEdits()
    {
        var page = Factory();
        var s = new PageEditSession(page);
        s.Remove(page.Widgets[0].InstanceId);
        s.Cancel().Should().BeSameAs(page);
    }

    [Fact]
    public void Commit_ReturnsCurrent()
    {
        var page = Factory();
        var s = new PageEditSession(page);
        s.Remove(page.Widgets[0].InstanceId);
        s.Commit().Widgets.Count.Should().Be(page.Widgets.Count - 1);
    }

    [Fact]
    public void Add_PlacesWithPushDown()
    {
        var page = Factory();
        var s = new PageEditSession(page);
        var widget = new WidgetInstance(Guid.NewGuid(), "nexus.widget.cpuChart", new GridRect(0, 0, 6, 2));

        s.Add(widget);

        s.Current.Widgets.Count.Should().Be(page.Widgets.Count + 1);
        s.Current.FindWidget(widget.InstanceId)!.Rect.Row.Should().Be(0); // newcomer keeps its spot
    }

    [Fact]
    public void Remove_UnknownId_IsNoOp_NoUndoEntry()
    {
        var s = new PageEditSession(Factory());
        s.Remove(Guid.NewGuid());
        s.CanUndo.Should().BeFalse();
        s.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void CompactPage_ClosesGaps_AndPushesUndoEntry()
    {
        var page = Factory();
        var diskCard = page.Widgets[3]; // nexus.widget.diskCard at (4,2,4,2)
        var cpuCard = page.Widgets[1]; // nexus.widget.cpuCard at (4,0,4,2) — top row, directly above diskCard
        var s = new PageEditSession(page);
        s.Remove(cpuCard.InstanceId); // opens a gap at (4,0,4,2)

        s.CompactPage();

        s.Current.FindWidget(diskCard.InstanceId)!.Rect.Row.Should().Be(0); // pulled up to close the gap
        s.CanUndo.Should().BeTrue();
    }

    // ── RebaseAll (externally-applied changes, e.g. pop-out state) ─────────────

    [Fact]
    public void RebaseAll_AppliesTransform_ToCurrentImmediately()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);

        s.RebaseAll(p => PageLayoutEngine.SetPopOut(p, id, new PopOutState(true, 1, 2, 3, 4, Topmost: false)));

        s.Current.FindWidget(id)!.PopOut!.IsPoppedOut.Should().BeTrue();
    }

    [Fact]
    public void RebaseAll_ThenCommit_PreservesTransform()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);
        s.Move(id, new GridRect(0, 10, 4, 2)); // an ordinary edit-session op predating the rebase

        s.RebaseAll(p => PageLayoutEngine.SetPopOut(p, id, new PopOutState(true, 1, 2, 3, 4, Topmost: false)));

        var committed = s.Commit();
        committed.FindWidget(id)!.PopOut!.IsPoppedOut.Should().BeTrue();
        committed.FindWidget(id)!.Rect.Row.Should().Be(10); // the prior edit is still present too
    }

    [Fact]
    public void RebaseAll_ThenCancel_PreservesTransform()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);
        s.Move(id, new GridRect(0, 10, 4, 2));

        s.RebaseAll(p => PageLayoutEngine.SetPopOut(p, id, new PopOutState(true, 1, 2, 3, 4, Topmost: false)));

        var cancelled = s.Cancel();
        cancelled.FindWidget(id)!.PopOut!.IsPoppedOut.Should().BeTrue(); // survives Cancel
        cancelled.FindWidget(id)!.Rect.Row.Should().Be(page.Widgets[0].Rect.Row); // but the move does not
    }

    [Fact]
    public void RebaseAll_ThenUndo_PreservesTransform()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);
        s.Move(id, new GridRect(0, 10, 4, 2));

        s.RebaseAll(p => PageLayoutEngine.SetPopOut(p, id, new PopOutState(true, 1, 2, 3, 4, Topmost: false)));
        s.Undo(); // undo the move

        s.Current.FindWidget(id)!.PopOut!.IsPoppedOut.Should().BeTrue(); // survives Undo
        s.Current.FindWidget(id)!.Rect.Should().Be(page.Widgets[0].Rect); // move undone
    }
}
