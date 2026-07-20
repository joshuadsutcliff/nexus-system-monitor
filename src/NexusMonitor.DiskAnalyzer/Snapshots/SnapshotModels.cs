namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>Snapshot header row. Volume/filesystem fields captured at scan time — cannot be re-captured later (spec §3.1).</summary>
public sealed record SnapshotInfo(
    long Id,
    string RootPath,
    string RootKey,
    DateTime CreatedAtUtc,
    string Scanner,
    string? FileSystem,
    long VolumeTotal,
    long VolumeFree,
    long TotalSize,
    long TotalFiles,
    long TotalDirs,
    long ThresholdBytes,
    string? AppVersion);

/// <summary>One persisted node. Directories carry rollups + sub-threshold aggregates; files ≥ threshold are individual rows.</summary>
public sealed class SnapshotNode
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public DateTime? LastModified { get; set; }
    public DateTime? Created { get; set; }        // schema insurance; never diffed in v1 (spec §3.1)
    public DateTime? LastAccessed { get; set; }   // schema insurance; never diffed in v1 (spec §3.1)
    public long SmallFilesSize { get; set; }      // dirs only
    public long SmallFilesCount { get; set; }     // dirs only
}

public sealed record SnapshotOptions(
    long ThresholdBytes = 1_048_576,
    int RetentionPerRoot = 26,
    long MaxDbSizeBytes = 500L * 1024 * 1024);

public enum ChangeKind { Added, Removed, Grown, Shrunk, Unchanged }

/// <summary>Diff output node. Children are sorted by |Delta| descending (spec §5 rename-pair clustering).</summary>
public sealed class DiffNode
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public ChangeKind Kind { get; set; }
    public long? SizeBefore { get; set; }
    public long? SizeAfter { get; set; }
    public long Delta { get; set; }
    public long SmallFilesDelta { get; set; }     // dirs only
    public List<DiffNode> Children { get; } = new();
}

public sealed class DiffResult
{
    public required SnapshotInfo Older { get; init; }
    public required SnapshotInfo Newer { get; init; }
    public required DiffNode Root { get; init; }
    public long EffectiveThreshold { get; init; }
    public bool ThresholdMismatch { get; init; }
    public bool NamesMatchedCaseInsensitively { get; init; }
}
