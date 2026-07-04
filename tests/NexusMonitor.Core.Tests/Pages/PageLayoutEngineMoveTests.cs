using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageLayoutEngineMoveTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();

    private static PageLayout TwoWidgets() =>
        new("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)),
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 0, 4, 2)),
        });

    [Fact]
    public void Move_ToEmptySpace_JustMoves()
    {
        var page = PageLayoutEngine.MoveWidget(TwoWidgets(), IdA, new GridRect(8, 0, 4, 2));
        page.FindWidget(IdA)!.Rect.Should().Be(new GridRect(8, 0, 4, 2));
        page.FindWidget(IdB)!.Rect.Should().Be(new GridRect(4, 0, 4, 2));
    }

    [Fact]
    public void Move_OntoOtherWidget_PushesItDown()
    {
        var page = PageLayoutEngine.MoveWidget(TwoWidgets(), IdA, new GridRect(4, 0, 4, 2));
        page.FindWidget(IdA)!.Rect.Should().Be(new GridRect(4, 0, 4, 2));
        page.FindWidget(IdB)!.Rect.Should().Be(new GridRect(4, 2, 4, 2));
    }

    [Fact]
    public void Move_UnknownId_ReturnsPageUnchanged()
    {
        var page = TwoWidgets();
        PageLayoutEngine.MoveWidget(page, Guid.NewGuid(), new GridRect(8, 0, 4, 2)).Should().BeSameAs(page);
    }

    [Fact]
    public void Remove_DeletesOnlyThatWidget()
    {
        var page = PageLayoutEngine.RemoveWidget(TwoWidgets(), IdA);
        page.FindWidget(IdA).Should().BeNull();
        page.FindWidget(IdB).Should().NotBeNull();
    }
}
