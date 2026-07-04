using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageModelTests
{
    private static WidgetInstance W(Guid id, int col, int row) =>
        new(id, "nexus.test", new GridRect(col, row, 2, 2));

    [Fact]
    public void DefaultGridColumns_Is12()
    {
        PageLayout.DefaultGridColumns.Should().Be(12);
    }

    [Fact]
    public void FindWidget_ReturnsMatchOrNull()
    {
        var id = Guid.NewGuid();
        var page = new PageLayout("dash", "Dashboard", "icon.dashboard",
            PageLayout.DefaultGridColumns, new[] { W(id, 0, 0) });

        page.FindWidget(id).Should().NotBeNull();
        page.FindWidget(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void WithWidgets_ReplacesListOnly()
    {
        var page = new PageLayout("dash", "Dashboard", "icon.dashboard",
            PageLayout.DefaultGridColumns, Array.Empty<WidgetInstance>());
        var updated = page.WithWidgets(new[] { W(Guid.NewGuid(), 0, 0) });

        updated.Widgets.Should().ContainSingle();
        page.Widgets.Should().BeEmpty();            // original untouched (immutability)
        updated.PageId.Should().Be("dash");  // identity fields preserved
    }
}
