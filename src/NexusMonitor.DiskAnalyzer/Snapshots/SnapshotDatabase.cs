using Microsoft.Data.Sqlite;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// Thrown when an existing disk-snapshots.db reports a stored schema_version newer
/// than this build's <see cref="SnapshotDatabase.SchemaVersion"/> — e.g. after a
/// downgrade, or a file synced in from a machine on a newer install. This is
/// deliberately NOT a <see cref="SqliteException"/>: <see cref="SnapshotDatabase.OpenOrRecover"/>
/// only catches that type for genuine corruption, so a too-new (but perfectly valid)
/// file is never destructively renamed aside — it is simply refused, honestly.
/// </summary>
public sealed class SnapshotSchemaTooNewException : Exception
{
    public int StoredVersion { get; }
    public int SupportedVersion { get; }

    public SnapshotSchemaTooNewException(int storedVersion, int supportedVersion)
        : base($"Snapshot database schema version {storedVersion} is newer than this version of " +
               $"Nexus supports (max {supportedVersion}). Update Nexus to read this snapshot " +
               "history, or use a build that supports it — the existing file has been left untouched.")
    {
        StoredVersion = storedVersion;
        SupportedVersion = supportedVersion;
    }
}

/// <summary>
/// Owns the disk-snapshots.db connection. Mirrors MetricsDatabase conventions
/// (WAL, NORMAL sync, busy timeout, meta version table) but is a separate file
/// by design — retention/vacuum/schema evolve independently and corruption here
/// can never damage metrics history (spec §3).
/// </summary>
public sealed class SnapshotDatabase : IDisposable
{
    public const int SchemaVersion = 1;

    public SqliteConnection Connection { get; }
    private readonly string _dbPath;

    public SnapshotDatabase(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        // Pooling=False: this class holds both connections open for the app's
        // lifetime, so connection pooling buys nothing — but Microsoft.Data.Sqlite's
        // pool keeps the OS file handle open even after SqliteConnection.Dispose()
        // returns it to the pool. POSIX advisory locking hides that (macOS/Linux
        // stay green); Windows mandatory sharing throws IOException on any
        // post-dispose file op (rename-aside recovery, File.ReadAllBytes in tests).
        // Disabling pooling makes Dispose() actually release the handle.
        Connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        Connection.Open();
        try
        {
            ConfigurePragmas();
            // Guard BEFORE InitSchema/any write: a stored schema newer than this
            // build understands must never be touched by our (older) migrations or
            // writes — that's how you'd actually corrupt a forward-compatible file.
            // This is the guard that must exist before any future schema v2 ships.
            EnsureSchemaVersionSupported();
            InitSchema();
            // Second connection for reads: WAL supports concurrent readers with one
            // writer, and SqliteConnection itself is NOT thread-safe — UI-thread reads
            // must never share the write connection with background saves (review
            // consensus 2026-07-19; mirrors the MetricsStore two-connection pattern).
            ReadConnection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            ReadConnection.Open();
            ConfigureReadPragmas();
        }
        catch
        {
            ReadConnection?.Dispose(); // never leak an open handle on a corrupt file
            Connection.Dispose();
            throw;
        }
    }

    /// <summary>Reads meta.schema_version, if present, and throws
    /// <see cref="SnapshotSchemaTooNewException"/> when it's newer than this build
    /// supports. A no-op (not an error) on a brand-new file — InitSchema hasn't run
    /// yet, so there is no meta table, which just means "current version, create it".</summary>
    private void EnsureSchemaVersionSupported()
    {
        using (var check = Connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0) return; // brand-new file
        }

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        var raw = cmd.ExecuteScalar();
        if (raw is null or DBNull) return; // meta exists but no version row yet — treat as current

        if (!int.TryParse(Convert.ToString(raw), out var stored)) return; // unparseable: not this guard's problem

        if (stored > SchemaVersion)
            throw new SnapshotSchemaTooNewException(stored, SchemaVersion);
    }

    public SqliteConnection ReadConnection { get; private set; } = null!;

    /// <summary>Spec §8: corrupt DB → rename aside, recreate empty, report recovery.</summary>
    public static SnapshotDatabase OpenOrRecover(string dbPath, out bool recovered)
    {
        recovered = false;
        SnapshotDatabase? db = null;
        try
        {
            db = new SnapshotDatabase(dbPath);
            // A file can open yet still be corrupt; force a real read.
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
            cmd.ExecuteScalar();
            return db;
        }
        catch (SqliteException)
        {
            // Belt-and-suspenders: both connections above are opened with
            // Pooling=False, so there's nothing in the pool to release for this
            // file — this call is inert. Left in (rather than removed) in case a
            // future edit reintroduces a pooled connection for this dbPath; scoped
            // to THIS file only, since ClearAllPools() is process-wide and would
            // disturb the metrics DB's pooling (review finding, 2026-07-19).
            db?.Dispose();
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));
            var aside = $"{dbPath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

            // WAL-mode databases leave sidecar files; move them aside before moving the main file.
            // SQLite may clean up these files when detecting corruption, so we move what exists.
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var sidePath = dbPath + suffix;
                var asideSidePath = aside + suffix;
                if (File.Exists(sidePath))
                {
                    try
                    {
                        File.Move(sidePath, asideSidePath, overwrite: true);
                    }
                    catch (Exception)
                    {
                        // Best-effort: a locked sidecar must not abort recovery; the
                        // fresh DB ignores stale sidecars.
                    }
                }
            }

            File.Move(dbPath, aside, overwrite: true);
            recovered = true;
            return new SnapshotDatabase(dbPath);
        }
    }

    private void ConfigurePragmas()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode  = WAL;
            PRAGMA synchronous   = NORMAL;
            PRAGMA busy_timeout  = 5000;
            PRAGMA foreign_keys  = OFF;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Pragmas are per-connection, not per-file: ReadConnection needs its
    /// own busy_timeout so a UI read racing another process's VACUUM/write waits
    /// instead of throwing instantly, plus query_only as a defense-in-depth guard
    /// against accidental writes through the read handle (review finding, 2026-07-19).</summary>
    private void ConfigureReadPragmas()
    {
        using var cmd = ReadConnection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA busy_timeout = 5000;
            PRAGMA query_only   = ON;";
        cmd.ExecuteNonQuery();
    }

    private void InitSchema()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                root_path       TEXT NOT NULL,
                root_key        TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                scanner         TEXT NOT NULL,
                file_system     TEXT,
                volume_total    INTEGER,
                volume_free     INTEGER,
                total_size      INTEGER,
                total_files     INTEGER,
                total_dirs      INTEGER,
                threshold_bytes INTEGER NOT NULL,
                app_version     TEXT,
                complete        INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_snap_rootkey ON snapshots(root_key, created_at);
            CREATE TABLE IF NOT EXISTS nodes (
                snapshot_id       INTEGER NOT NULL,
                id                INTEGER NOT NULL,
                parent_id         INTEGER,
                name              TEXT NOT NULL,
                is_dir            INTEGER NOT NULL,
                size              INTEGER,
                allocated_size    INTEGER,
                file_count        INTEGER,
                folder_count      INTEGER,
                last_modified     TEXT,
                created           TEXT,
                last_accessed     TEXT,
                small_files_size  INTEGER,
                small_files_count INTEGER,
                PRIMARY KEY (snapshot_id, id)
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_parent ON nodes(snapshot_id, parent_id);
            INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '{SchemaVersion}');";
        cmd.ExecuteNonQuery();
    }

    public long GetDatabaseSizeBytes()
    {
        var f = new FileInfo(_dbPath);
        return f.Exists ? f.Length : 0;
    }

    /// <summary>Live (non-free) bytes. Unlike file length, this shrinks as rows are
    /// deleted, without needing VACUUM — the retention cap loop depends on that.</summary>
    public long GetLiveSizeBytes()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = @"SELECT ((SELECT * FROM pragma_page_count) -
                                    (SELECT * FROM pragma_freelist_count))
                                 * (SELECT * FROM pragma_page_size);";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Checkpoint()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Checkpoint(); } catch { /* best effort */ }
        ReadConnection?.Dispose();
        Connection.Dispose();
    }
}
