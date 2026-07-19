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
        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeTrue();
        Directory.GetFiles(_dir, "disk-snapshots.db.corrupt-*").Should().HaveCount(1);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(0);
    }

    [Fact]
    public void OpenOrRecover_HealthyDbIsNotRecovered()
    {
        new SnapshotDatabase(DbPath).Dispose();
        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeFalse();
    }
}
