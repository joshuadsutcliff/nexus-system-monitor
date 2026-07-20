using System.Text.Json;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// The one shared JSON shape for a bare <see cref="SnapshotInfo"/> (no diff attached) —
/// used by both `disk snapshots list --format json` and `disk scan --format json`
/// (when run without `--diff`, spec §7). Keeping this in one place means those two
/// CLI surfaces can never drift into two different "what does a snapshot look like
/// as JSON" shapes for the same underlying record.
/// </summary>
public static class SnapshotInfoJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static object ToPayload(SnapshotInfo info) => new
    {
        id = info.Id,
        rootPath = info.RootPath,
        createdAt = info.CreatedAtUtc.ToString("o"),
        totalSize = info.TotalSize,
        totalFiles = info.TotalFiles,
        thresholdBytes = info.ThresholdBytes,
        fileSystem = info.FileSystem,
        volumeTotal = info.VolumeTotal,
        volumeFree = info.VolumeFree,
    };

    /// <summary>Single-snapshot object — the shape `disk scan --format json` (no `--diff`) emits.</summary>
    public static string ToJson(SnapshotInfo info) =>
        JsonSerializer.Serialize(ToPayload(info), Options);

    /// <summary>Array of snapshot objects — the shape `disk snapshots list --format json` emits.</summary>
    public static string ToJson(IEnumerable<SnapshotInfo> infos) =>
        JsonSerializer.Serialize(infos.Select(ToPayload), Options);
}
