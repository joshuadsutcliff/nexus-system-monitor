using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageLayoutEngineCompactTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();
    private static readonly Guid IdC = Guid.NewGuid();

    [Fact]
    public void Compact_PullsWidgetsUpIntoGaps()
    {
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 3, 4, 2)),  // floating with gap above
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 5, 4, 1)),  // floating deeper
        });

        var compacted = PageLayoutEngine.Compact(page);

        compacted.FindWidget(IdA)!.Rect.Should().Be(new GridRect(0, 0, 4, 2));
        compacted.FindWidget(IdB)!.Rect.Should().Be(new GridRect(4, 0, 4, 1)); // different columns → row 0 too
    }

    [Fact]
    public void Compact_StopsAtBlockers_SameColumns()
    {
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)),
            new WidgetInstance(IdB, "nexus.test", new GridRect(0, 5, 4, 2)),  // must land at row 2, not 0
        });

        var compacted = PageLayoutEngine.Compact(page);

        compacted.FindWidget(IdA)!.Rect.Should().Be(new GridRect(0, 0, 4, 2));
        compacted.FindWidget(IdB)!.Rect.Should().Be(new GridRect(0, 2, 4, 2));
    }

    [Fact]
    public void Compact_AlreadyCompact_IsStable()
    {
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)),
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 0, 4, 2)),
            new WidgetInstance(IdC, "nexus.test", new GridRect(0, 2, 8, 1)),
        });

        var compacted = PageLayoutEngine.Compact(page);

        compacted.FindWidget(IdA)!.Rect.Should().Be(page.FindWidget(IdA)!.Rect);
        compacted.FindWidget(IdB)!.Rect.Should().Be(page.FindWidget(IdB)!.Rect);
        compacted.FindWidget(IdC)!.Rect.Should().Be(page.FindWidget(IdC)!.Rect);
    }
}
