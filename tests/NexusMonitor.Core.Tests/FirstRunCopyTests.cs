using System.Reflection;
using FluentAssertions;
using NexusMonitor.Core.Onboarding;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Locks the first-run welcome overlay's copy to the conductor-approved draft (2026-07-19).
/// Also guards against the three banned legacy feature names ever reappearing in this class —
/// ProBalance/IdleSaver/SmartTrim were renamed to AutoBalance/IdleThrottle/MemoryReclaim and the
/// old names are banned from any user-facing copy (see SettingsService's de-branding migration).
/// </summary>
public class FirstRunCopyTests
{
    private static readonly string[] BannedLegacyNames = { "ProBalance", "IdleSaver", "SmartTrim" };

    // ── Exact wording ────────────────────────────────────────────────────────

    [Fact]
    public void Title_MatchesApprovedDraft()
    {
        FirstRunCopy.Title.Should().Be("Welcome to Nexus");
    }

    [Fact]
    public void Row1_MatchesApprovedDraft()
    {
        FirstRunCopy.Row1.Should().Be(
            "This is your dashboard — a starting layout with the essentials: health, usage, and top consumers.");
    }

    [Fact]
    public void Row2_MatchesApprovedDraft()
    {
        FirstRunCopy.Row2.Should().Be(
            "Everything is rearrangeable — open Edit mode to drag, resize, add, or remove widgets, and save layouts as profiles.");
    }

    [Fact]
    public void Row3_MatchesApprovedDraft()
    {
        FirstRunCopy.Row3.Should().Be(
            "Nexus only shows data it can actually read — when a sensor isn't available on your hardware, you'll see \"—\" rather than an estimate.");
    }

    [Fact]
    public void Row4_MatchesApprovedDraft()
    {
        FirstRunCopy.Row4.Should().Be(
            "The sidebar covers the rest — processes, automation, disks, network, and more.");
    }

    [Fact]
    public void ButtonLabel_MatchesApprovedDraft()
    {
        FirstRunCopy.ButtonLabel.Should().Be("Get started");
    }

    // ── Non-empty ────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllCopyStrings()
    {
        foreach (var field in typeof(FirstRunCopy).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = field.GetValue(null) as string;
            yield return new object[] { field.Name, value! };
        }
    }

    [Theory]
    [MemberData(nameof(AllCopyStrings))]
    public void EveryCopyString_IsNotNullOrWhiteSpace(string fieldName, string value)
    {
        value.Should().NotBeNullOrWhiteSpace($"{fieldName} must have real copy");
    }

    // ── Banned legacy names ──────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllCopyStrings))]
    public void EveryCopyString_NeverContainsBannedLegacyNames(string fieldName, string value)
    {
        foreach (var banned in BannedLegacyNames)
        {
            value.Should().NotContain(banned, $"{fieldName} must never reference the banned legacy name '{banned}'");
        }
    }

    [Fact]
    public void AllCopyStrings_CoversEveryPublicStaticField()
    {
        // Sanity check on the reflection helper itself: if a future edit adds a field to
        // FirstRunCopy, this pins that the MemberData source actually picks it up (currently 6:
        // Title, Row1-4, ButtonLabel) rather than silently exercising a stale subset.
        AllCopyStrings().Should().HaveCount(6);
    }
}
