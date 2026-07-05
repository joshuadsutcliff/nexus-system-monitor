using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageLayoutEnginePlacementTests
{
    private static WidgetInstance W(string tag, int col, int row, int cs = 4, int rs = 2) =>
        new(GuidFrom(tag), "nexus.test", new GridRect(col, row, cs, rs));

    // Stable ids so tests can find widgets after engine ops.
    private static Guid GuidFrom(string tag) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(tag)));

    private static GridRect RectOf(PageLayout page, string tag) => page.FindWidget(GuidFrom(tag))!.Rect;

    private static PageLayout Empty() =>
        new("p", "P", "icon.p", 12, Array.Empty<WidgetInstance>());

    [Fact]
    public void Place_OnEmptyArea_AddsWithoutMovingAnything()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 0, 0));

        page.Widgets.Should().ContainSingle();
        RectOf(page, "a").Should().Be(new GridRect(0, 0, 4, 2));
    }

    [Fact]
    public void Place_OntoOccupiedCells_PushesOccupantDown()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 0, 0));
        page = PageLayoutEngine.PlaceWidget(page, W("b", 0, 0)); // lands on top of a

        RectOf(page, "b").Should().Be(new GridRect(0, 0, 4, 2)); // newcomer keeps its spot
        RectOf(page, "a").Should().Be(new GridRect(0, 2, 4, 2)); // occupant pushed below newcomer
    }

    [Fact]
    public void Place_PushDown_Cascades()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 0, 0));
        page = PageLayoutEngine.PlaceWidget(page, W("b", 0, 2));
        page = PageLayoutEngine.PlaceWidget(page, W("c", 0, 0)); // pushes a, which must push b

        RectOf(page, "c").Should().Be(new GridRect(0, 0, 4, 2));
        RectOf(page, "a").Should().Be(new GridRect(0, 2, 4, 2));
        RectOf(page, "b").Should().Be(new GridRect(0, 4, 4, 2));
    }

    [Fact]
    public void Place_OutOfBoundsRect_IsClampedIntoGrid()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 11, 0)); // 4-wide at col 11 spills

        RectOf(page, "a").Should().Be(new GridRect(8, 0, 4, 2));
    }

    [Fact]
    public void Place_NeverLeavesOverlaps()
    {
        var page = Empty();
        foreach (var tag in new[] { "a", "b", "c", "d", "e" })
            page = PageLayoutEngine.PlaceWidget(page, W(tag, 0, 0, 6, 2)); // all dropped at origin

        var rects = page.Widgets.Select(w => w.Rect).ToList();
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++)
                rects[i].Intersects(rects[j]).Should().BeFalse($"widgets {i} and {j} overlap");
    }
}
