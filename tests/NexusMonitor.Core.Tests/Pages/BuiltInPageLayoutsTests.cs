using System.Linq;
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
        page.Widgets.Count.Should().BeGreaterOrEqualTo(10);

        // Factory layouts must themselves be valid: no overlaps, everything in-grid.
        for (var i = 0; i < page.Widgets.Count; i++)
        {
            page.Widgets[i].Rect.FitsWithinColumns(page.GridColumns).Should().BeTrue();
            for (var j = i + 1; j < page.Widgets.Count; j++)
                page.Widgets[i].Rect.Intersects(page.Widgets[j].Rect).Should().BeFalse();
        }
    }

    [Fact]
    public void Dashboard_TopBlockMirrorsClassicDashboardProportions()
    {
        // The classic (non-page-engine) dashboard renders: a full-width health banner,
        // then CPU+Memory as a half/half row, then Disk+GPU as a half/half row. The
        // page-engine default must mirror those proportions on the 12-column grid.
        var page = BuiltInPageLayouts.Load("dashboard");

        RectOf(page, "nexus.widget.healthScore").Should().Be(new GridRect(0, 0, 12, 2));
        RectOf(page, "nexus.widget.cpuCard").Should().Be(new GridRect(0, 2, 6, 2));
        RectOf(page, "nexus.widget.memoryCard").Should().Be(new GridRect(6, 2, 6, 2));
        RectOf(page, "nexus.widget.diskCard").Should().Be(new GridRect(0, 4, 6, 2));
        RectOf(page, "nexus.widget.gpuCard").Should().Be(new GridRect(6, 4, 6, 2));

        static GridRect RectOf(PageLayout page, string widgetTypeId) =>
            page.Widgets.Single(w => w.WidgetTypeId == widgetTypeId).Rect;
    }

    [Fact]
    public void Dashboard_AllWidgetsAreValidPlacementsPerEngine()
    {
        // Belt-and-suspenders on top of the pairwise Intersects check above: run every
        // widget's rect through the same PageLayoutEngine.IsValidPlacement the UI uses
        // for drag/drop, ignoring the widget's own instance so it only reports
        // grid-fit + collisions against the *other* widgets.
        var page = BuiltInPageLayouts.Load("dashboard");

        foreach (var widget in page.Widgets)
            PageLayoutEngine.IsValidPlacement(page, widget.Rect, widget.InstanceId).Should().BeTrue();
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
