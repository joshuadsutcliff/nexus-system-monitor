namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// Path normalization and name-equality rules for snapshot grouping and diffing.
/// Spec §3.1: root_path keeps display casing; root_key is the case-folded grouping key.
/// Spec §5: names match ordinal-ignore-case on Windows/macOS, ordinal on Linux.
/// </summary>
public static class PathKeys
{
    public static bool NamesAreCaseInsensitive =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer NameComparer =>
        NamesAreCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Canonical absolute path, trailing separators stripped (except filesystem roots).</summary>
    public static string NormalizeDisplay(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        while (full.Length > root.Length &&
               (full[^1] == System.IO.Path.DirectorySeparatorChar ||
                full[^1] == System.IO.Path.AltDirectorySeparatorChar))
        {
            full = full[..^1];
        }
        return full;
    }

    /// <summary>Grouping key: normalized path, case-folded on case-insensitive platforms.</summary>
    public static string ToRootKey(string path)
    {
        var norm = NormalizeDisplay(path);
        return NamesAreCaseInsensitive ? norm.ToUpperInvariant() : norm;
    }
}
