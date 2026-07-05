using FluentAssertions;
using System.Text.Json;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

/// <summary>Round-trip and envelope-shape tests for <see cref="PageLayoutSerializer"/>.</summary>
public class PageLayoutSerializerTests
{
    private static PageLayout SamplePage()
    {
        var id = new Guid("11111111-2222-3333-4444-555555555555");
        return new PageLayout("dash", "Dashboard", "icon.dashboard", 12, new[]
        {
            new WidgetInstance(id, "nexus.cpu.chart", new GridRect(0, 0, 6, 3),
                ConfigJson: """{"timeWindowSeconds":60,"someFutureKey":[1,2,3]}""",
                PopOut: new PopOutState(true, 100, 200, 640, 360, Topmost: false)),
        });
    }

    /// <summary>Serializing then deserializing a page must reproduce it exactly, including nested widget/pop-out state.</summary>
    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var json = PageLayoutSerializer.Serialize(SamplePage());
        var ok = PageLayoutSerializer.TryDeserialize(json, out var page, out var error);
        ok.Should().BeTrue(error);

        PageLayoutComparer.Instance.Equals(SamplePage(), page).Should().BeTrue();
    }

    /// <summary>The written JSON must be the versioned envelope: a schemaVersion int and a nested page object.</summary>
    [Fact]
    public void Serialize_WritesVersionEnvelope()
    {
        using var doc = JsonDocument.Parse(PageLayoutSerializer.Serialize(SamplePage()));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        doc.RootElement.TryGetProperty("page", out _).Should().BeTrue();
    }

    /// <summary>ConfigJson is opaque widget-owned JSON; unknown keys within it must survive verbatim.</summary>
    [Fact]
    public void ConfigJson_SurvivesVerbatim_IncludingUnknownKeys()
    {
        var json = PageLayoutSerializer.Serialize(SamplePage());
        PageLayoutSerializer.TryDeserialize(json, out var page, out _);

        var config = JsonDocument.Parse(page!.Widgets[0].ConfigJson!);
        config.RootElement.GetProperty("someFutureKey").GetArrayLength().Should().Be(3);
    }

    /// <summary>Malformed, incomplete, or unsupported envelopes must never throw and must always report
    /// a human-readable error alongside a false result and a null page.</summary>
    [Theory]
    [InlineData("")]                                      // empty
    [InlineData("not json at all {{{")]                   // garbage
    [InlineData("null")]                                  // JSON null
    [InlineData("""{"schemaVersion":1}""")]               // missing page
    [InlineData("""{"page":null,"schemaVersion":1}""")]   // null page
    [InlineData("""{"schemaVersion":999,"page":{"pageId":"x","title":"X","iconKey":"i","gridColumns":12,"widgets":[]}}""")] // future version
    [InlineData("""{"page":{"pageId":"x","title":"X","iconKey":"i","gridColumns":12,"widgets":[]}}""")] // missing schemaVersion, page otherwise valid
    [InlineData(null)]                                    // null input
    public void TryDeserialize_HostileInput_ReturnsFalseWithError_NeverThrows(string? json)
    {
        var ok = PageLayoutSerializer.TryDeserialize(json, out var page, out var error);

        ok.Should().BeFalse();
        page.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>Structural equality for pages whose Widgets lists are IReadOnlyList (record Equals is reference-based for lists).</summary>
public sealed class PageLayoutComparer : IEqualityComparer<PageLayout>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PageLayoutComparer Instance = new();

    /// <summary>True when both pages have equal scalar fields and structurally-equal widget lists.</summary>
    public bool Equals(PageLayout? x, PageLayout? y)
    {
        if (x is null || y is null) return ReferenceEquals(x, y);
        return x.PageId == y.PageId && x.Title == y.Title && x.IconKey == y.IconKey
            && x.GridColumns == y.GridColumns
            && x.Widgets.SequenceEqual(y.Widgets);
    }

    /// <summary>Hash code based on PageId, consistent with <see cref="Equals"/>.</summary>
    public int GetHashCode(PageLayout obj) => obj.PageId.GetHashCode();
}
