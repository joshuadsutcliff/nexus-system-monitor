using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

public interface ISnapshotStore : IDisposable
{
    /// <summary>Persist a completed scan. Returns the new snapshot id. Applies retention afterwards.</summary>
    long Save(ScanResult result, SnapshotOptions options, string? appVersion = null);
    IReadOnlyList<SnapshotInfo> ListSnapshots(string? rootPath = null); // newest first; null = all roots
    SnapshotInfo? GetSnapshot(long id);
    IReadOnlyList<SnapshotNode> LoadNodes(long snapshotId);
    void Delete(long snapshotId);
    /// <summary>Delete rows of any snapshot with complete = 0 (crash recovery). Call at startup.</summary>
    int SweepIncomplete();
    long GetStoreSizeBytes();
    bool WasRecovered { get; } // true when the DB file was corrupt and recreated
}
