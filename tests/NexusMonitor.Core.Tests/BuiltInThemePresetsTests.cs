using System.Linq;
using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Themes;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Pins the quiet-defaults redesign's two touched presets (task brief, 2026-07-11):
/// "nexus-default" redefined to match AppSettings' new fresh-install defaults exactly, and the
/// new "crystal-glass" preset carrying the previous showcase look verbatim. Also locks the
/// invariant that every other built-in preset (Dark Sakura et al.) is untouched by this change.
/// </summary>
public class BuiltInThemePresetsTests
{
    private static ThemePreset Find(string id) =>
        BuiltInThemePresets.All.Should().ContainSingle(p => p.Id == id).Subject;

    [Fact]
    public void NexusDefault_MatchesNewQuietAppSettingsDefaultsExactly()
    {
        var preset = Find("nexus-default");

        preset.Name.Should().Be("Nexus Default");
        preset.ThemeMode.Should().Be("System");
        preset.AccentColorHex.Should().Be("#0A84FF");
        preset.TextAccentColorHex.Should().Be("");
        preset.CustomWindowBgHex.Should().Be("");
        preset.CustomSurfaceBgHex.Should().Be("");
        preset.CustomSidebarBgHex.Should().Be("");
        preset.IsGlassEnabled.Should().BeFalse();
        preset.BackdropBlurMode.Should().Be("None");
        preset.IsSpecularEnabled.Should().BeFalse();
        preset.FontFamily.Should().Be("");
        preset.FontSizeMultiplier.Should().Be(1.0);
    }

    [Fact]
    public void NexusDefault_CustomSurfaceHexesAreEmpty_NotLiteralHex()
    {
        // The known theme-flip stale-color bug: a literal hex here (instead of "") would freeze
        // the surface colors across a System/Dark/Light theme switch instead of deriving from the
        // active theme. Dedicated assertion so a future edit can't silently reintroduce it.
        var preset = Find("nexus-default");
        preset.CustomWindowBgHex.Should().BeEmpty();
        preset.CustomSurfaceBgHex.Should().BeEmpty();
        preset.CustomSidebarBgHex.Should().BeEmpty();
    }

    [Fact]
    public void CrystalGlass_CarriesThePreviousNexusDefaultShowcaseLookVerbatim()
    {
        var preset = Find("crystal-glass");

        preset.Name.Should().Be("Crystal Glass");
        preset.ThemeMode.Should().Be("Dark");
        preset.AccentColorHex.Should().Be("#0A84FF");
        preset.TextAccentColorHex.Should().Be("");
        preset.CustomWindowBgHex.Should().Be("");
        preset.CustomSurfaceBgHex.Should().Be("");
        preset.CustomSidebarBgHex.Should().Be("");
        preset.IsGlassEnabled.Should().BeTrue();
        preset.GlassOpacity.Should().Be(0.80);
        preset.BackdropBlurMode.Should().Be("Acrylic");
        preset.IsSpecularEnabled.Should().BeTrue();
        preset.SpecularIntensity.Should().Be(0.55);
        preset.FontFamily.Should().Be("");
        preset.FontSizeMultiplier.Should().Be(1.0);
    }

    [Fact]
    public void CrystalGlass_IsAdjacentToNexusDefaultInTheList()
    {
        var ids = BuiltInThemePresets.All.Select(p => p.Id).ToList();
        var nexusIdx   = ids.IndexOf("nexus-default");
        var crystalIdx = ids.IndexOf("crystal-glass");

        crystalIdx.Should().Be(nexusIdx + 1);
    }

    [Fact]
    public void AllPresets_HaveUniqueIds()
    {
        BuiltInThemePresets.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void OtherPresets_AreUntouchedByThisChange()
    {
        // Dark Sakura et al. — spot-check a handful of pre-existing presets to lock that this
        // task didn't reorder/rename/re-value anything beyond nexus-default + the new
        // crystal-glass insertion.
        var darkSakura = Find("dark-sakura");
        darkSakura.Name.Should().Be("Dark Sakura");
        darkSakura.ThemeMode.Should().Be("Dark");
        darkSakura.IsGlassEnabled.Should().BeTrue();
        darkSakura.BackdropBlurMode.Should().Be("Acrylic");

        var cleanLight = Find("clean-light");
        cleanLight.Name.Should().Be("Clean Light");
        cleanLight.IsGlassEnabled.Should().BeFalse();

        // 19 pre-existing presets + the new crystal-glass insertion.
        BuiltInThemePresets.All.Should().HaveCount(20);
    }
}
