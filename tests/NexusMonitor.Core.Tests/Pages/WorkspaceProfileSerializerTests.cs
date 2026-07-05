using FluentAssertions;
using System.Linq;
using System.Text.Json;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

/// <summary>Round-trip and envelope-shape tests for <see cref="WorkspaceProfileSerializer"/>.</summary>
public class WorkspaceProfileSerializerTests
{
    private static ThemeSnapshot SampleSnapshot() => new(
        ThemeMode: "Dark",
        AccentColorHex: "#FF00FF",
        TextAccentColorHex: "#00FFFF",
        CustomWindowBgHex: "#111111",
        CustomSurfaceBgHex: "#222222",
        CustomSidebarBgHex: "#333333",
        IsGlassEnabled: true,
        GlassOpacity: 0.65,
        BackdropBlurMode: "Acrylic",
        IsSpecularEnabled: true,
        SpecularIntensity: 0.3,
        FontFamily: "Segoe UI",
        FontSizeMultiplier: 1.1,
        SmartTintEnabled: true);

    private static WorkspaceProfile SampleProfile()
    {
        var dashboard = BuiltInPageLayouts.Load("dashboard");
        var modified = dashboard with { PageId = "dashboard-2", Title = "Dashboard 2" };

        var pages = new Dictionary<string, PageLayout>
        {
            [dashboard.PageId] = dashboard,
            [modified.PageId] = modified,
        };

        return new WorkspaceProfile(
            Name: "Test Profile",
            Pages: pages,
            Theme: new ThemeRef(Snapshot: SampleSnapshot()),
            PopOutStates: Array.Empty<PopOutState>());
    }

    /// <summary>Serializing then deserializing a profile must reproduce it exactly, including its
    /// pages dictionary, the embedded theme snapshot, and the (empty) pop-out state list.</summary>
    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var json = WorkspaceProfileSerializer.Serialize(SampleProfile());
        var ok = WorkspaceProfileSerializer.TryDeserialize(json, out var profile, out var error);
        ok.Should().BeTrue(error);

        WorkspaceProfileComparer.Instance.Equals(SampleProfile(), profile).Should().BeTrue();
    }

    /// <summary>The written JSON must be the versioned envelope: a schemaVersion int and a nested profile object.</summary>
    [Fact]
    public void Serialize_WritesVersionEnvelope()
    {
        using var doc = JsonDocument.Parse(WorkspaceProfileSerializer.Serialize(SampleProfile()));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        doc.RootElement.TryGetProperty("profile", out _).Should().BeTrue();
    }

    /// <summary>Malformed, incomplete, or unsupported envelopes must never throw and must always report
    /// a human-readable error alongside a false result and a null profile.</summary>
    [Theory]
    [InlineData("")]                                        // empty
    [InlineData("not json at all {{{")]                     // garbage
    [InlineData("null")]                                     // JSON null
    [InlineData("""{"schemaVersion":1}""")]                  // missing profile
    [InlineData("""{"profile":null,"schemaVersion":1}""")]   // null profile
    [InlineData("""{"schemaVersion":999,"profile":{"name":"x","pages":{},"theme":{},"popOutStates":[]}}""")] // future version
    [InlineData("""{"profile":{"name":"x","pages":{},"theme":{},"popOutStates":[]}}""")]                     // missing schemaVersion, profile otherwise valid
    [InlineData(null)]                                        // null input
    public void TryDeserialize_HostileInput_ReturnsFalseWithError_NeverThrows(string? json)
    {
        var ok = WorkspaceProfileSerializer.TryDeserialize(json, out var profile, out var error);

        ok.Should().BeFalse();
        profile.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>Structural equality for profiles whose Pages/PopOutStates are collection-typed
/// (record Equals is reference-based for dictionaries/lists).</summary>
public sealed class WorkspaceProfileComparer : IEqualityComparer<WorkspaceProfile>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly WorkspaceProfileComparer Instance = new();

    /// <summary>True when both profiles have equal names, structurally-equal theme references,
    /// structurally-equal pop-out state lists, and page dictionaries with matching keys whose
    /// values are structurally equal per <see cref="PageLayoutComparer"/>.</summary>
    public bool Equals(WorkspaceProfile? x, WorkspaceProfile? y)
    {
        if (x is null || y is null) return ReferenceEquals(x, y);
        if (x.Name != y.Name) return false;
        if (!x.Theme.Equals(y.Theme)) return false;
        if (!x.PopOutStates.SequenceEqual(y.PopOutStates)) return false;
        if (x.Pages.Count != y.Pages.Count) return false;

        foreach (var (key, page) in x.Pages)
        {
            if (!y.Pages.TryGetValue(key, out var otherPage)) return false;
            if (!PageLayoutComparer.Instance.Equals(page, otherPage)) return false;
        }
        return true;
    }

    /// <summary>Hash code based on Name, consistent with <see cref="Equals"/>.</summary>
    public int GetHashCode(WorkspaceProfile obj) => obj.Name.GetHashCode();
}
