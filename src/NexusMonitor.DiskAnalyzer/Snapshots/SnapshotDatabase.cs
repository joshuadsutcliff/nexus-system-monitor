using Microsoft.Data.Sqlite;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

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
        Connection = new SqliteConnection($"Data Source={dbPath}");
        Connection.Open();
        try
        {
            ConfigurePragmas();
            InitSchema();
            // Second connection for reads: WAL supports concurrent readers with one
            // writer, and SqliteConnection itself is NOT thread-safe — UI-thread reads
            // must never share the write connection with background saves (review
            // consensus 2026-07-19; mirrors the MetricsStore two-connection pattern).
            ReadConnection = new SqliteConnection($"Data Source={dbPath}");
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
            // Release only THIS file's handles. ClearAllPools() is process-wide and
            // would disturb the metrics DB's pooling (review finding, 2026-07-19).
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
