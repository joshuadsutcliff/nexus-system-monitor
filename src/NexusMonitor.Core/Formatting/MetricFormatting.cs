namespace NexusMonitor.Core.Formatting;

/// <summary>
/// Single source of "honest placeholder" truth for the scattered
/// <c>value &gt; 0 ? $"{value:F0}..." : "—"</c>-shaped ternaries across the UI ViewModels. Nexus's
/// honesty convention shows nothing (an em dash) rather than fabricate/estimate a value when a
/// metric couldn't be read — this class centralizes that "sentinel/null → dash, otherwise format"
/// logic so it's unit-testable in one place instead of duplicated at every display site.
///
/// This is a behavior-preserving extraction: every overload reproduces exactly what the ternary
/// it replaces already rendered (see <c>NexusMonitor.UI.ViewModels.PerformanceViewModel.Update</c>,
/// <c>SystemInfoViewModel.OnInfoChanged</c>, etc. for the call sites), not a new formatting scheme.
/// </summary>
public static class MetricFormatting
{
    /// <summary>The single placeholder string used everywhere a metric couldn't be read.</summary>
    public const string Dash = "—";

    /// <summary>
    /// Formats <paramref name="value"/> via <see cref="string.Format(string,object?)"/> with
    /// <paramref name="format"/> when it is greater than <paramref name="sentinelThreshold"/>
    /// (default 0) — the common "no sensor data comes back as ≤ 0, never as a real measurement"
    /// pattern. Otherwise renders <see cref="Dash"/>. <see cref="double.NaN"/> always falls to
    /// <see cref="Dash"/> (NaN &gt; anything is false in IEEE 754), no special-casing needed.
    /// </summary>
    public static string FormatOrDash(double value, string format, double sentinelThreshold = 0) =>
        value > sentinelThreshold ? string.Format(format, value) : Dash;

    /// <summary>
    /// Same sentinel logic as <see cref="FormatOrDash(double,string,double)"/>, but checks
    /// availability on <paramref name="sentinelValue"/> while formatting a separately-derived
    /// <paramref name="displayValue"/> — matches call sites that sentinel-check a raw provider
    /// reading (e.g. <c>FrequencyMhz</c>) while displaying an already-rounded value
    /// (e.g. <c>CpuFrequencyGhz</c>) derived from it.
    /// </summary>
    public static string FormatOrDash(
        double sentinelValue, double displayValue, string format, double sentinelThreshold = 0) =>
        sentinelValue > sentinelThreshold ? string.Format(format, displayValue) : Dash;

    /// <summary>Integer counterpart of <see cref="FormatOrDash(double,string,double)"/> for
    /// call sites sentinel-checking an <see langword="int"/> field (e.g. cache sizes in KB).</summary>
    public static string FormatOrDash(int value, string format, int sentinelThreshold = 0) =>
        value > sentinelThreshold ? string.Format(format, value) : Dash;

    /// <summary>Renders <paramref name="value"/> unchanged, or <see cref="Dash"/> when it is
    /// <see langword="null"/> or empty. Matches call sites keyed off <c>.Length &gt; 0</c> or a
    /// null-reference default (e.g. process metadata Nexus couldn't read for a protected
    /// process). Does not treat whitespace-only strings as missing — call sites that need that
    /// stricter check (e.g. a CPU socket string that's legitimately meaningless rather than
    /// unread) apply <see cref="string.IsNullOrWhiteSpace(string?)"/> themselves before reaching
    /// this helper.</summary>
    public static string OrDash(string? value) =>
        string.IsNullOrEmpty(value) ? Dash : value;

    /// <summary>Renders <paramref name="value"/> formatted with <paramref name="format"/>, or
    /// <see cref="Dash"/> when it is <c>default(DateTime)</c> — the synthetic/never-set sentinel
    /// used across the codebase instead of fabricating an epoch date like "0001-01-01".</summary>
    public static string OrDash(DateTime value, string format) =>
        value == default ? Dash : value.ToString(format);
}
