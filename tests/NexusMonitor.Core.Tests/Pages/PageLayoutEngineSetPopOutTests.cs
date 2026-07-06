using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

/// <summary>Tests for <see cref="PageLayoutEngine.SetPopOut"/>: set, clear, unknown-id no-op,
/// and that the resulting state survives a serializer round-trip.</summary>
public class PageLayoutEngineSetPopOutTests
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
    public void SetPopOut_WithState_SetsPopOutOnlyOnTargetWidget()
    {
        var popOut = new PopOutState(true, 10, 20, 300, 200, Topmost: true);

        var page = PageLayoutEngine.SetPopOut(TwoWidgets(), IdA, popOut);

        page.FindWidget(IdA)!.PopOut.Should().Be(popOut);
        page.FindWidget(IdB)!.PopOut.Should().BeNull();
    }

    [Fact]
    public void SetPopOut_WithNull_ClearsExistingPopOut()
    {
        var existingPopOut = new PopOutState(true, 1, 2, 3, 4, Topmost: false);
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2), PopOut: existingPopOut),
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 0, 4, 2)),
        });

        var result = PageLayoutEngine.SetPopOut(page, IdA, null);

        result.FindWidget(IdA)!.PopOut.Should().BeNull();
    }

    [Fact]
    public void SetPopOut_UnknownId_ReturnsPageUnchanged()
    {
        var page = TwoWidgets();
        PageLayoutEngine.SetPopOut(page, Guid.NewGuid(), new PopOutState(true, 0, 0, 100, 100, Topmost: false))
            .Should().BeSameAs(page);
    }

    [Fact]
    public void SetPopOut_RoundTripsThroughSerializer()
    {
        var popOut = new PopOutState(true, 55, 66, 640, 480, Topmost: true);
        var page = PageLayoutEngine.SetPopOut(TwoWidgets(), IdB, popOut);

        var json = PageLayoutSerializer.Serialize(page);
        var ok = PageLayoutSerializer.TryDeserialize(json, out var roundTripped, out var error);

        ok.Should().BeTrue(error);
        PageLayoutComparer.Instance.Equals(page, roundTripped).Should().BeTrue();
    }
}
