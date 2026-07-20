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
        // File-content/existence reads on DbPath must happen only after the live
        // SnapshotDatabase (and its SQLite connections) is disposed — on Windows,
        // SQLite holds the file with write access while open, and a File.* read
        // requesting only read-share throws IOException (sharing violation).
        // POSIX advisory locking doesn't block this, so the bug is Windows-only.
        var tables = new List<string>();
        using (var db = new SnapshotDatabase(DbPath))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        File.Exists(DbPath).Should().BeTrue();
        tables.Should().Contain(new[] { "meta", "nodes", "snapshots" });
    }

    [Fact]
    public void OpenOrRecover_RenamesCorruptFileAndRecreates()
    {
        // Build a healthy db first, then dispose (checkpoints + truncates the WAL) so
        // we start from a known-clean on-disk file, then corrupt only the DATA pages
        // (offset 4096+) while leaving the page-1 header/schema intact. This matters:
        // if the header itself is unreadable, SQLite's own WAL-mode negotiation
        // discards ANY pre-existing -wal/-shm file (valid or junk) before our recovery
        // code ever runs — verified empirically; a garbage-header corruption can never
        // reach the sidecar-move loop with -wal/-shm still present. Keeping the header
        // valid avoids that, and is also a more realistic corruption model (partial
        // write / bit-rot in the data region, not the whole file).
        using (new SnapshotDatabase(DbPath)) { }
        var bytes = File.ReadAllBytes(DbPath);
        for (int i = 4096; i < bytes.Length; i++) bytes[i] = 0xAB;
        File.WriteAllBytes(DbPath, bytes);

        // Distinctive sidecar content: the ONLY way this content can show up at the
        // aside path is if the production move loop actually ran. A test that merely
        // checks File.Exists at the original path would pass even if the move never
        // happened, since a fresh WAL-mode DB creates its own -wal there regardless.
        //
        // -wal/-shm junk is written here (arrange only — NOT asserted on below): once
        // WAL mode is active, SQLite validates any pre-existing -wal/-shm against its own
        // internal format independent of the main file's health, and junk text fails that
        // validation and gets purged before our loop runs, on any header state (verified
        // empirically). Their presence still matters for the arrange, though: it forces
        // SQLite to perform WAL/hot-journal negotiation eagerly on open, which is what
        // reliably surfaces the page-2+ corruption as a SqliteException — without them,
        // this file opens leniently and the corruption isn't touched until a damaged page
        // is actually read, which doesn't reliably happen during the recovery probe.
        //
        // -journal is different: once WAL mode is active, SQLite doesn't touch legacy
        // rollback-journal files at all, so a stray -journal genuinely survives to reach
        // the production loop — making it the one suffix this in-process test can prove
        // end-to-end. The loop's -wal/-shm handling shares the exact same code path (same
        // loop body, only the suffix differs), so proving -journal moves is a genuine test
        // of the loop logic itself.
        File.WriteAllText(DbPath + "-wal", "junk-wal");
        File.WriteAllText(DbPath + "-shm", "junk-shm");
        File.WriteAllText(DbPath + "-journal", "junk-journal");

        // Everything below that needs the LIVE database (recovered flag, a fresh
        // read against the recreated db) is scoped to this using block. All direct
        // File.*/Directory.* reads on DbPath and its siblings run only after the
        // block closes and the SQLite connections are disposed — on Windows, SQLite
        // holds disk-snapshots.db with write access while open, and a File.* read
        // requesting only read-share throws IOException (sharing violation). POSIX
        // advisory locking doesn't block this, so the bug is Windows-only.
        using (var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered))
        {
            recovered.Should().BeTrue();

            // New database should be healthy
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
            Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(0);
        }

        // Corrupt file should be moved aside
        Directory.GetFiles(_dir, "disk-snapshots.db.corrupt-*").Where(p => !p.EndsWith("-wal") && !p.EndsWith("-shm") && !p.EndsWith("-journal")).Should().HaveCount(1);

        // The junk -journal sidecar must have been moved aside (not left, not lost) —
        // content equality is what discriminates a real move from a no-op.
        var asideJournal = Directory.GetFiles(_dir, "disk-snapshots.db.corrupt-*-journal");
        asideJournal.Should().HaveCount(1);
        File.ReadAllText(asideJournal[0]).Should().Be("junk-journal");
        File.Exists(DbPath + "-journal").Should().BeFalse("the junk journal must not be left behind at the original path");

        File.Exists(DbPath).Should().BeTrue("new database file should exist");
        // Whether a fresh -wal exists at the original path depends on SQLite's
        // checkpoint/WAL state at the time of assertion — not asserted either way.
    }

    [Fact]
    public void OpenOrRecover_HealthyDbIsNotRecovered()
    {
        new SnapshotDatabase(DbPath).Dispose();
        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeFalse();
    }

}
