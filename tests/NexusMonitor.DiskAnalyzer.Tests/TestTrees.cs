using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Tests;

/// <summary>Builds in-memory DiskNode trees — no real filesystem needed (spec §10).</summary>
public static class TestTrees
{
    public static DiskNode Dir(string name, params DiskNode[] children)
    {
        var d = new DiskNode { Name = name, IsDirectory = true };
        foreach (var c in children)
        {
            c.Parent = d;
            d.Children.Add(c);
            d.Size += c.Size;
            d.AllocatedSize += c.AllocatedSize;
            d.FileCount += c.IsDirectory ? c.FileCount : 1;
            d.FolderCount += c.IsDirectory ? c.FolderCount + 1 : 0;
        }
        return d;
    }

    public static DiskNode File(string name, long size) => new()
    {
        Name = name, IsDirectory = false, Size = size, AllocatedSize = size,
        LastModified = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static ScanResult Result(DiskNode root, string scannedPath) => new()
    {
        Root = root, ScannedPath = scannedPath,
        TotalFiles = root.FileCount, TotalFolders = root.FolderCount, TotalSize = root.Size,
        FileSystem = "TESTFS", VolumeTotal = 1_000_000_000, VolumeFree = 400_000_000,
    };
}
