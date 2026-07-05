using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class BuiltInPageLayoutsTests
{
    [Fact]
    public void Dashboard_LoadsFromEmbeddedResource()
    {
        var page = BuiltInPageLayouts.Load("dashboard");

        page.PageId.Should().Be("dashboard");
        page.GridColumns.Should().Be(12);
        page.Widgets.Count.Should().BeGreaterOrEqualTo(3);

        // Factory layouts must themselves be valid: no overlaps, everything in-grid.
        for (var i = 0; i < page.Widgets.Count; i++)
        {
            page.Widgets[i].Rect.FitsWithinColumns(page.GridColumns).Should().BeTrue();
            for (var j = i + 1; j < page.Widgets.Count; j++)
                page.Widgets[i].Rect.Intersects(page.Widgets[j].Rect).Should().BeFalse();
        }
    }

    [Fact]
    public void UnknownPageId_Throws()
    {
        var act = () => BuiltInPageLayouts.Load("nope");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BuiltInPageIds_ListsDashboard()
    {
        BuiltInPageLayouts.BuiltInPageIds.Should().Contain("dashboard");
    }
}
