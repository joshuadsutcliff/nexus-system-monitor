using System.Text;
using System.Text.Json;
using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

public static class DiffFormatter
{
    public sealed record Line(string Path, string Kind, bool IsDir,
        long? SizeBefore, long? SizeAfter, long Delta, long SmallFilesDelta);

    public static IReadOnlyList<Line> Flatten(DiffResult diff, int? top = null) =>
        Truncate(BuildSortedLines(diff), top);

    /// <summary>Walks the diff tree into flat, sorted (by |delta| descending) lines. The one
    /// place path-building/sort order lives — Flatten and ToJson both build off this so they
    /// can never disagree on ordering or path spelling.</summary>
    private static List<Line> BuildSortedLines(DiffResult diff)
    {
        var lines = new List<Line>();
        void Walk(DiffNode n, string path)
        {
            var p = path.Length == 0 ? diff.Older.RootPath : $"{path}/{n.Name}";
            if (n.Kind != ChangeKind.Unchanged || n.SmallFilesDelta != 0)
                lines.Add(new Line(p, n.Kind.ToString().ToLowerInvariant(), n.IsDirectory,
                    n.SizeBefore, n.SizeAfter, n.Delta, n.SmallFilesDelta));
            foreach (var c in n.Children) Walk(c, p);
        }
        Walk(diff.Root, string.Empty);
        lines.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return lines;
    }

    /// <summary>
    /// The single place "--top N" truncation happens (after sorting by |delta|
    /// descending). Both ToJson and ToTable (via Flatten) go through this so the
    /// two output paths can never drift on what "top N" means or disagree on the
    /// truncated line count.
    /// </summary>
    private static List<Line> Truncate(List<Line> sorted, int? top) =>
        top is int t && sorted.Count > t ? sorted.Take(t).ToList() : sorted;

    public static string ToJson(DiffResult diff, int? top = null)
    {
        var all = BuildSortedLines(diff); // untruncated — needed to compute `truncated` below
        var shown = Truncate(all, top);
        var payload = new
        {
            older = new { id = diff.Older.Id, createdAt = diff.Older.CreatedAtUtc.ToString("o") },
            newer = new { id = diff.Newer.Id, createdAt = diff.Newer.CreatedAtUtc.ToString("o") },
            root = diff.Older.RootPath,
            threshold = new
            {
                effectiveBytes = diff.EffectiveThreshold,
                mismatch = diff.ThresholdMismatch,
                olderBytes = diff.Older.ThresholdBytes,
                newerBytes = diff.Newer.ThresholdBytes,
            },
            namesMatchedCaseInsensitively = diff.NamesMatchedCaseInsensitively,
            changes = shown.Select(l => new
            {
                path = l.Path, kind = l.Kind, isDir = l.IsDir,
                sizeBefore = l.SizeBefore, sizeAfter = l.SizeAfter,
                delta = l.Delta, smallFilesDelta = l.SmallFilesDelta,
            }),
            truncated = shown.Count < all.Count,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToTable(DiffResult diff, int? top = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"CHANGE",-9} {"DELTA",12} {"BEFORE",12} {"AFTER",12}  PATH");
        foreach (var l in Flatten(diff, top))
        {
            sb.AppendLine($"{l.Kind,-9} {Signed(l.Delta),12} " +
                          $"{(l.SizeBefore is long b ? DiskNode.FormatSize(b) : "—"),12} " +
                          $"{(l.SizeAfter is long a ? DiskNode.FormatSize(a) : "—"),12}  {l.Path}");
        }
        sb.AppendLine();
        sb.AppendLine(ThresholdFooter(diff));
        return sb.ToString();
    }

    public static string ThresholdFooter(DiffResult diff)
    {
        var caseNote = diff.NamesMatchedCaseInsensitively
            ? "names matched case-insensitively" : "names matched case-sensitively";
        if (!diff.ThresholdMismatch)
            return $"Files under {DiskNode.FormatSize(diff.EffectiveThreshold)} aggregated per folder · " +
                   $"renames appear as removed + added · {caseNote}.";
        var lo = Math.Min(diff.Older.ThresholdBytes, diff.Newer.ThresholdBytes);
        return $"Snapshots used different thresholds ({DiskNode.FormatSize(diff.Older.ThresholdBytes)} and " +
               $"{DiskNode.FormatSize(diff.Newer.ThresholdBytes)}); diff uses " +
               $"{DiskNode.FormatSize(diff.EffectiveThreshold)} — files between {DiskNode.FormatSize(lo)} and " +
               $"{DiskNode.FormatSize(diff.EffectiveThreshold)} are aggregated · " +
               $"renames appear as removed + added · {caseNote}.";
    }

    private static string Signed(long v) =>
        v >= 0 ? "+" + DiskNode.FormatSize(v) : "-" + DiskNode.FormatSize(-v);
}
