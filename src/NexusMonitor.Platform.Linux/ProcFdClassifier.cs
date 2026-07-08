namespace NexusMonitor.Platform.Linux;

/// <summary>
/// Pure classifier for <c>/proc/&lt;pid&gt;/fd/*</c> symlink targets (as returned by
/// <c>readlink(2)</c>) into a handle "type name" for the Handle Viewer. These targets are
/// "magic link" text, not real filesystem paths, so classification is string-pattern based —
/// no I/O here, so this is unit-testable on every OS.
/// </summary>
public static class ProcFdClassifier
{
    /// <summary>
    /// Classifies one fd symlink target:
    ///   - <c>socket:[12345]</c> → "Socket"
    ///   - <c>pipe:[12345]</c> → "Pipe"
    ///   - <c>anon_inode:[eventfd]</c> / <c>anon_inode:inotify</c> → its subtype
    ///     ("eventfd", "inotify", ...), stripping brackets when present
    ///   - an absolute path → "File"
    ///   - anything else (including an empty/unresolved target) → "Unknown" (honest-UI: never
    ///     fabricate a type for a target we couldn't classify or couldn't resolve)
    /// </summary>
    public static string ClassifyTypeName(string linkTarget)
    {
        if (string.IsNullOrEmpty(linkTarget)) return "Unknown";

        if (linkTarget.StartsWith("socket:", StringComparison.Ordinal)) return "Socket";
        if (linkTarget.StartsWith("pipe:", StringComparison.Ordinal)) return "Pipe";

        const string anonPrefix = "anon_inode:";
        if (linkTarget.StartsWith(anonPrefix, StringComparison.Ordinal))
        {
            var subtype = linkTarget[anonPrefix.Length..].Trim('[', ']');
            return string.IsNullOrEmpty(subtype) ? "Anonymous" : subtype;
        }

        if (linkTarget.StartsWith('/')) return "File";

        return "Unknown";
    }
}
