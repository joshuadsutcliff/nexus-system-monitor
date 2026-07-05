using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageLayoutEngineValidationTests
{
    private static readonly Guid IdA = Guid.NewGuid();

    private static PageLayout PageWithOneWidget() =>
        new("p", "P", "icon.p", 12,
            new[] { new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)) });

    [Fact]
    public void ValidPlacement_EmptyArea_IsAccepted()
    {
        PageLayoutEngine.IsValidPlacement(PageWithOneWidget(), new GridRect(4, 0, 4, 2)).Should().BeTrue();
    }

    [Fact]
    public void OverlappingPlacement_IsRejected()
    {
        PageLayoutEngine.IsValidPlacement(PageWithOneWidget(), new GridRect(2, 1, 4, 2)).Should().BeFalse();
    }

    [Fact]
    public void OutOfBounds_IsRejected()
    {
        PageLayoutEngine.IsValidPlacement(PageWithOneWidget(), new GridRect(10, 0, 4, 1)).Should().BeFalse();
    }

    [Fact]
    public void OverlapWithIgnoredInstance_IsAccepted()
    {
        // Moving A onto its own footprint must be legal.
        PageLayoutEngine.IsValidPlacement(
            PageWithOneWidget(), new GridRect(1, 0, 4, 2), ignoreInstanceId: IdA).Should().BeTrue();
    }
}
