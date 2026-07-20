using System.Globalization;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

public static class SnapshotRefs
{
    public static SnapshotInfo? Resolve(IReadOnlyList<SnapshotInfo> newestFirst, string reference)
    {
        if (string.Equals(reference, "latest", StringComparison.OrdinalIgnoreCase))
            return newestFirst.FirstOrDefault();

        if (long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return newestFirst.FirstOrDefault(s => s.Id == id);

        if (DateTime.TryParse(reference, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            // Date-only input means "end of that day": newest snapshot at or before it.
            var cutoff = reference.Length <= 10 ? date.Date.AddDays(1).AddTicks(-1) : date;
            return newestFirst.FirstOrDefault(s => s.CreatedAtUtc <= cutoff);
        }
        return null;
    }
}
