using System.Globalization;

namespace NexusMonitor.Core.Services;

/// <summary>
/// Lightweight, dependency-free comparer for GitHub release tags against the running
/// assembly version. Deliberately tolerant of malformed input — every method returns a
/// failure result instead of throwing, since tag strings originate from an external API
/// (GitHub release names) and are never trustworthy input.
/// </summary>
internal static class UpdateVersionComparer
{
    private static readonly char[] MetadataSeparators = ['-', '+'];

    /// <summary>
    /// Attempts to parse <paramref name="latestTag"/> and <paramref name="runningVersion"/>
    /// and compare them. Returns <see langword="false"/> (with <paramref name="isNewer"/> set
    /// to <see langword="false"/>) if either value cannot be parsed as a dotted numeric version.
    /// </summary>
    public static bool TryCompare(string? latestTag, string? runningVersion, out bool isNewer)
    {
        isNewer = false;

        if (!TryParse(latestTag, out var latestParts))
            return false;
        if (!TryParse(runningVersion, out var runningParts))
            return false;

        isNewer = Compare(latestParts, runningParts) > 0;
        return true;
    }

    /// <summary>Strips a leading "v"/"V" from a tag, e.g. "v0.6.0" → "0.6.0". Returns the input unchanged when there's no prefix.</summary>
    public static string StripPrefix(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return tag;
        return tag[0] is 'v' or 'V' ? tag[1..] : tag;
    }

    /// <summary>
    /// Parses a dotted numeric version string (e.g. "1.2.3", "v0.1.8.2", "1.2.3-beta.1+build5")
    /// into its component integers. Tolerates an optional leading "v"/"V" and strips any
    /// pre-release/build metadata suffix. Never throws — returns <see langword="false"/> on
    /// empty input or non-numeric segments.
    /// </summary>
    public static bool TryParse(string? raw, out int[] parts)
    {
        parts = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = StripPrefix(raw.Trim());

        var cut = s.IndexOfAny(MetadataSeparators);
        if (cut >= 0)
            s = s[..cut];

        var segments = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var result = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
                return false;
        }

        parts = result;
        return true;
    }

    /// <summary>
    /// Compares two version-part arrays left to right (major, minor, patch, revision, …),
    /// treating any missing trailing segment as 0 — so "1.0" and "1.0.0.0" compare equal.
    /// </summary>
    public static int Compare(int[] a, int[] b)
    {
        var len = Math.Max(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = i < a.Length ? a[i] : 0;
            var bi = i < b.Length ? b[i] : 0;
            var cmp = ai.CompareTo(bi);
            if (cmp != 0) return cmp;
        }
        return 0;
    }
}
