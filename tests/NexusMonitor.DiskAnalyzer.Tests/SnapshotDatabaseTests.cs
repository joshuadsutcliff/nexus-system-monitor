using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class SnapshotDatabaseTests : IDisposable
{
    private readonly string _dir;
    private string DbPath => Path.Combine(_dir, "disk-snapshots.db");

    public SnapshotDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Constructor_CreatesSchemaAndFile()
    {
        using var db = new SnapshotDatabase(DbPath);
        File.Exists(DbPath).Should().BeTrue();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
        tables.Should().Contain(new[] { "meta", "nodes", "snapshots" });
    }

    [Fact]
    public void OpenOrRecover_RenamesCorruptFileAndRecreates()
    {
        File.WriteAllText(DbPath, "this is not a sqlite database, not even close");
        // Also create sidecar files that might exist in a WAL-mode database
        File.WriteAllText(DbPath + "-wal", "wal junk");
        File.WriteAllText(DbPath + "-shm", "shm junk");

        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeTrue();

        // Corrupt file should be moved aside
        Directory.GetFiles(_dir, "disk-snapshots.db.corrupt-*").Where(p => !p.EndsWith("-wal") && !p.EndsWith("-shm") && !p.EndsWith("-journal")).Should().HaveCount(1);

        // New database should be healthy and have fresh WAL files created
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(0);

        File.Exists(DbPath).Should().BeTrue("new database file should exist");
        File.Exists(DbPath + "-wal").Should().BeTrue("fresh WAL file should be created by SQLite");
    }

    [Fact]
    public void OpenOrRecover_HealthyDbIsNotRecovered()
    {
        new SnapshotDatabase(DbPath).Dispose();
        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeFalse();
    }

}
