using System.Globalization;

namespace NexusMonitor.Platform.Linux;

/// <summary>
/// One parsed line of <c>/proc/&lt;pid&gt;/maps</c>: <c>start-end perms offset dev inode path</c>.
/// <see cref="Path"/> is empty for anonymous mappings that carry no pathname field at all,
/// an absolute filesystem path for file-backed mappings, or a kernel pseudo-name such as
/// <c>[heap]</c>, <c>[stack]</c>, <c>[stack:1234]</c>, or <c>[vdso]</c>.
/// </summary>
public readonly record struct ProcMapsEntry(
    ulong  Start,
    ulong  End,
    string Perms,
    ulong  Offset,
    string Dev,
    ulong  Inode,
    string Path)
{
    /// <summary>True when this line carried a pathname field (real path or bracketed pseudo-name).</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);
}

/// <summary>
/// Pure parser for <c>/proc/&lt;pid&gt;/maps</c> lines. Shared by <c>ReadModules</c> (module
/// enumeration) and <c>ReadMemoryMap</c> (Memory Map view) so the two features never drift on
/// how a maps line is tokenized. No file I/O here — takes a line, returns a parsed entry — so
/// it is unit-testable on every OS.
/// </summary>
public static class ProcMapsParser
{
    /// <summary>
    /// Parses one <c>/proc/&lt;pid&gt;/maps</c> line. Returns null only when the line doesn't
    /// even have the five fixed fields (address range, perms, offset, dev, inode) — malformed
    /// individual fields (e.g. an unparsable address) degrade gracefully to 0 rather than
    /// rejecting the whole line, matching the tolerant behavior <c>ReadModules</c> already had
    /// before this parser existed.
    /// </summary>
    public static ProcMapsEntry? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return null;

        ulong start = 0, end = 0;
        var dashIdx = parts[0].IndexOf('-');
        if (dashIdx > 0)
        {
            _ = ulong.TryParse(parts[0][..dashIdx], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out start);
            _ = ulong.TryParse(parts[0][(dashIdx + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out end);
        }

        var perms = parts[1];
        _ = ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset);
        var dev = parts[3];
        _ = ulong.TryParse(parts[4], out var inode);

        // Pathname is taken as the final whitespace-delimited token — mirrors the field
        // extraction ReadModules already used. /proc/<pid>/maps does not quote pathnames, so a
        // path containing a literal space, or the kernel's " (deleted)" suffix on an unlinked
        // file, will not round-trip correctly here. That is an existing limitation carried over
        // unchanged, not introduced by this parser.
        var path = parts.Length >= 6 ? parts[^1] : string.Empty;

        return new ProcMapsEntry(start, end, perms, offset, dev, inode, path);
    }
}

/// <summary>
/// Pure classification of a parsed <see cref="ProcMapsEntry"/> into the honest-UI vocabulary
/// <see cref="NexusMonitor.Core.Models.MemoryRegionInfo"/> expects (region type, protection
/// string). Kept separate from the provider so it is unit-testable without touching the
/// filesystem.
/// </summary>
public static class ProcMapsClassifier
{
    /// <summary>
    /// Classifies a mapping's path into a (RegionType, Description) pair, echoing the Windows
    /// MEM_IMAGE / MEM_MAPPED / MEM_PRIVATE split as closely as the data honestly supports:
    ///   - no path at all → "Private" (true anonymous mapping, e.g. malloc'd heap growth).
    ///   - bracketed pseudo-name ("[heap]", "[stack]", "[vdso]", ...) → "Private"; the tag
    ///     itself is surfaced as Description since it is real data straight from the kernel,
    ///     not a fabricated guess.
    ///   - absolute file path with the exec bit set → "Image" (the executable/code segment of
    ///     the binary or a shared library — the closest Linux analog to a loaded Windows image).
    ///   - absolute file path without the exec bit → "Mapped" (a data segment of the same file,
    ///     or an mmap'd data file).
    /// Unlike Windows (which only resolves a mapped filename for MEM_IMAGE, at the cost of an
    /// extra query), the path is already free on Linux from the maps line itself, so it is
    /// populated for both Image and Mapped rather than withheld to mimic a Windows limitation
    /// that doesn't apply here.
    /// </summary>
    public static (string RegionType, string Description) ClassifyPath(string path, string perms)
    {
        if (string.IsNullOrEmpty(path))
            return ("Private", string.Empty);

        if (path[0] == '[')
            return ("Private", path);

        if (path[0] == '/')
            return (HasExecBit(perms) ? "Image" : "Mapped", path);

        // Anything else observed in the wild — treat as private/anonymous rather than
        // guessing at a more specific classification (honest-UI: no fabrication).
        return ("Private", path);
    }

    /// <summary>
    /// Decodes the <c>rwxp</c>/<c>rwxs</c> permission field into a protection string built from
    /// whichever of R/W/X are set (e.g. "RWX", "RX", "R", "No Access"). The private/shared flag
    /// (4th char) does not have a dedicated field in <see cref="NexusMonitor.Core.Models.MemoryRegionInfo"/>
    /// and is not encoded here.
    /// </summary>
    public static string DecodeProtection(string perms)
    {
        bool r = HasBit(perms, 0, 'r');
        bool w = HasBit(perms, 1, 'w');
        bool x = HasBit(perms, 2, 'x');

        if (!r && !w && !x) return "No Access";

        var sb = new System.Text.StringBuilder(3);
        if (r) sb.Append('R');
        if (w) sb.Append('W');
        if (x) sb.Append('X');
        return sb.ToString();
    }

    private static bool HasExecBit(string perms) => HasBit(perms, 2, 'x');

    private static bool HasBit(string perms, int index, char expected) =>
        perms.Length > index && perms[index] == expected;
}
