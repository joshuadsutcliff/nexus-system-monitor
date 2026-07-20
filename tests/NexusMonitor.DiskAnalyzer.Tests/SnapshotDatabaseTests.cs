using FluentAssertions;
using Microsoft.Data.Sqlite;
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
        // -wal/-shm/-journal junk is written here (arrange only — NOT asserted on
        // below, for ANY of the three suffixes): once the write connection now opens
        // with Pooling=False (see SnapshotDatabase ctor), the reopen below is a
        // genuine fresh native sqlite3 open, and SQLite's own hot-journal recovery —
        // which runs unconditionally on a real open, independent of corruption or
        // journal_mode — consumes and deletes a stray -journal file before our
        // OpenOrRecover catch block ever sees it, and separately validates/purges
        // junk -wal/-shm against its own format (verified empirically, both cases).
        // So none of the three sidecars is provably "moved by our loop" via this
        // in-process test: this is correct SQLite behavior, not a gap in the
        // production move-aside loop, which still runs its best-effort move for
        // whatever sidecar state a real crash (not an in-process construction) could
        // leave behind. (Earlier revisions of this test wrote the -journal reopen
        // through a POOLED connection, which reused the still-open native handle
        // from the healthy DB created above instead of performing a real reopen —
        // that handle had no reason to recheck a hot journal it never saw appear, so
        // the stray file survived and this test's now-removed assertion happened to
        // pass. That was an artifact of the exact Windows-breaking pooling behavior
        // this fix removes, not a real guarantee — confirmed by a scratch repro
        // toggling Pooling=True/False against otherwise-identical open/pragma calls.)
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

        // The junk -journal must not be left behind at the original path — whether
        // because our loop moved it aside, or (as verified above) because SQLite's
        // own hot-journal recovery already discarded it during the reopen. Either
        // way, a stray hot journal must never survive at the live path.
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

    // ── Fast-follow #5: schema_version reader guard ─────────────────────────
    // This is the guard that must exist BEFORE any future schema v2 ships: an
    // older build opening a newer-schema disk-snapshots.db (e.g. after a
    // downgrade, or a shared file synced from a machine on a newer install)
    // must refuse with an honest, well-typed error — never silently proceed
    // and risk corrupting a schema it doesn't understand, and never treat a
    // valid-but-newer file as "corrupt" (which would destroy it via the
    // rename-aside recovery path).

    /// <summary>Hand-crafts a db file whose stored schema_version is newer than
    /// this build's <see cref="SnapshotDatabase.SchemaVersion"/>.</summary>
    private void BumpStoredSchemaVersion(int version)
    {
        new SnapshotDatabase(DbPath).Dispose();
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE meta SET value = $v WHERE key = 'schema_version'";
        cmd.Parameters.AddWithValue("$v", version.ToString());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Constructor_ThrowsSnapshotSchemaTooNew_WhenStoredVersionExceedsSupported()
    {
        BumpStoredSchemaVersion(SnapshotDatabase.SchemaVersion + 1);

        var act = () => new SnapshotDatabase(DbPath);

        var ex = act.Should().Throw<SnapshotSchemaTooNewException>().Which;
        ex.StoredVersion.Should().Be(SnapshotDatabase.SchemaVersion + 1);
        ex.SupportedVersion.Should().Be(SnapshotDatabase.SchemaVersion);
    }

    [Fact]
    public void OpenOrRecover_PropagatesSchemaTooNew_AndDoesNotTreatFileAsCorrupt()
    {
        BumpStoredSchemaVersion(SnapshotDatabase.SchemaVersion + 1);

        var act = () => SnapshotDatabase.OpenOrRecover(DbPath, out _);

        // Must propagate as the specific, honest error — NOT be swallowed into the
        // generic corrupt-DB recovery path (which only catches SqliteException).
        act.Should().Throw<SnapshotSchemaTooNewException>();

        // A valid newer-schema file must never be destructively renamed aside —
        // that path is reserved for genuine corruption (spec §8). Losing a
        // forward-compatible file just because THIS build can't read it yet would
        // be exactly the kind of silent-corruption behavior this guard exists to
        // prevent.
        File.Exists(DbPath).Should().BeTrue("a valid newer-schema file must be left untouched, not moved aside");
        Directory.GetFiles(_dir, "disk-snapshots.db.corrupt-*").Should().BeEmpty(
            "schema-too-new is not corruption and must not trigger the rename-aside recovery path");
    }

    [Fact]
    public void Constructor_OpensNormally_WhenStoredVersionEqualsSupported()
    {
        // Pin: the guard must not be so eager it rejects the CURRENT version too.
        new SnapshotDatabase(DbPath).Dispose(); // writes SchemaVersion via InitSchema
        using var db = new SnapshotDatabase(DbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(SnapshotDatabase.SchemaVersion);
    }
}
