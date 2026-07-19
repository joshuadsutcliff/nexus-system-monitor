using FluentAssertions;
using NexusMonitor.Core.Formatting;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MetricFormatting"/> — the single source of "honest placeholder" truth
/// behind the scattered "value &gt; 0 ? format : &quot;—&quot;" ternaries across the UI ViewModels
/// (unavailable-metric-tooltips feature). Lives alongside <see cref="GpuMemoryDisplayMathTests"/>
/// in Core (with these tests) for the same "Core-adjacent display logic, no UI test project"
/// carve-out that file documents.
/// </summary>
public class MetricFormattingTests
{
    // ── Dash constant ────────────────────────────────────────────────────────

    [Fact]
    public void Dash_IsEmDash()
    {
        MetricFormatting.Dash.Should().Be("—");
    }

    // ── FormatOrDash(double value, string format, double sentinelThreshold = 0) ────────────

    [Fact]
    public void FormatOrDash_Double_RealValue_RendersFormatted()
    {
        MetricFormatting.FormatOrDash(45.0, "{0:F0}°C").Should().Be("45°C");
    }

    [Fact]
    public void FormatOrDash_Double_ZeroSentinel_RendersDash()
    {
        MetricFormatting.FormatOrDash(0.0, "{0:F0}°C").Should().Be("—");
    }

    [Fact]
    public void FormatOrDash_Double_NegativeSentinel_RendersDash()
    {
        MetricFormatting.FormatOrDash(-5.0, "{0:F0}°C").Should().Be("—");
    }

    [Fact]
    public void FormatOrDash_Double_NaN_RendersDash()
    {
        // NaN > 0 is false in IEEE 754 comparisons, so this falls out of the sentinel check
        // for free — no special-casing needed.
        MetricFormatting.FormatOrDash(double.NaN, "{0:F0}°C").Should().Be("—");
    }

    [Theory]
    [InlineData(1000.0, "{0:F2} GHz", "1000.00 GHz")]
    [InlineData(0.4, "{0:F1} GB", "0.4 GB")]
    [InlineData(42.3, "+{0:F0}/hr", "+42/hr")]
    [InlineData(8192.0, "{0} KB", "8192 KB")]
    public void FormatOrDash_Double_VariousFormatStrings_MatchStringFormat(
        double value, string format, string expected)
    {
        MetricFormatting.FormatOrDash(value, format).Should().Be(expected);
    }

    [Fact]
    public void FormatOrDash_Double_CustomThreshold_UsesThresholdNotZero()
    {
        MetricFormatting.FormatOrDash(90.0, "{0:F0}%", sentinelThreshold: 95.0).Should().Be("—");
        MetricFormatting.FormatOrDash(96.0, "{0:F0}%", sentinelThreshold: 95.0).Should().Be("96%");
    }

    // ── FormatOrDash(double sentinelValue, double displayValue, string format, threshold) ──

    [Fact]
    public void FormatOrDash_SentinelAndDisplay_SentinelPositive_FormatsDisplayValue()
    {
        // Matches PerformanceViewModel.CpuFrequencyLabel: sentinel-checks FrequencyMhz but
        // formats the already-rounded CpuFrequencyGhz.
        MetricFormatting.FormatOrDash(3400.0, 3.40, "{0:F2} GHz").Should().Be("3.40 GHz");
    }

    [Fact]
    public void FormatOrDash_SentinelAndDisplay_SentinelZero_RendersDashRegardlessOfDisplayValue()
    {
        MetricFormatting.FormatOrDash(0.0, 3.40, "{0:F2} GHz").Should().Be("—");
    }

    [Fact]
    public void FormatOrDash_SentinelAndDisplay_SentinelNaN_RendersDash()
    {
        MetricFormatting.FormatOrDash(double.NaN, 3.40, "{0:F2} GHz").Should().Be("—");
    }

    // ── FormatOrDash(int value, string format, int sentinelThreshold = 0) ──────────────────

    [Fact]
    public void FormatOrDash_Int_RealValue_RendersFormatted()
    {
        MetricFormatting.FormatOrDash(8192, "{0} KB").Should().Be("8192 KB");
    }

    [Fact]
    public void FormatOrDash_Int_ZeroSentinel_RendersDash()
    {
        MetricFormatting.FormatOrDash(0, "{0} KB").Should().Be("—");
    }

    [Fact]
    public void FormatOrDash_Int_NegativeSentinel_RendersDash()
    {
        MetricFormatting.FormatOrDash(-1, "{0} KB").Should().Be("—");
    }

    // ── OrDash(string? value) ───────────────────────────────────────────────────────────────

    [Fact]
    public void OrDash_String_Null_RendersDash()
    {
        MetricFormatting.OrDash((string?)null).Should().Be("—");
    }

    [Fact]
    public void OrDash_String_Empty_RendersDash()
    {
        MetricFormatting.OrDash(string.Empty).Should().Be("—");
    }

    [Fact]
    public void OrDash_String_RealValue_RendersValueUnchanged()
    {
        MetricFormatting.OrDash("SYSTEM").Should().Be("SYSTEM");
    }

    [Fact]
    public void OrDash_String_Whitespace_IsNotDashed()
    {
        // Only null/empty is dashed here — whitespace-as-missing is a distinct, stricter check
        // some call sites (SystemInfoViewModel.SocketDisplay) apply themselves via
        // string.IsNullOrWhiteSpace before ever reaching this helper.
        MetricFormatting.OrDash("   ").Should().Be("   ");
    }

    // ── OrDash(DateTime value, string format) ───────────────────────────────────────────────

    [Fact]
    public void OrDash_DateTime_Default_RendersDash()
    {
        MetricFormatting.OrDash(default, "yyyy-MM-dd").Should().Be("—");
    }

    [Fact]
    public void OrDash_DateTime_RealValue_RendersFormatted()
    {
        var dt = new DateTime(2026, 7, 19);
        MetricFormatting.OrDash(dt, "yyyy-MM-dd").Should().Be("2026-07-19");
    }
}
