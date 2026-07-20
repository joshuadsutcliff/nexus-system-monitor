using Microsoft.Data.Sqlite;
using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// ISnapshotStore over SnapshotDatabase. Writer/reader are private concerns of
/// this one class (plan note: consolidation of the spec's SnapshotWriter/Reader).
/// All writes are transactional; 'complete' is set last so a crash can never
/// leave a snapshot that looks valid (spec §4/§8).
/// </summary>
public sealed class SnapshotStore : ISnapshotStore
{
    internal SnapshotDatabase Database { get; }
    public bool WasRecovered { get; }
    private readonly object _writeLock = new();

    public SnapshotStore(string dbPath)
    {
        Database = SnapshotDatabase.OpenOrRecover(dbPath, out var recovered);
        WasRecovered = recovered;
    }

    public long Save(ScanResult result, SnapshotOptions options, string? appVersion = null)
    {
        lock (_writeLock)
        {
            long snapshotId;
            using (var tx = Database.Connection.BeginTransaction())
            {
                using (var cmd = Database.Connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO snapshots
                        (root_path, root_key, created_at, scanner, file_system,
                         volume_total, volume_free, total_size, total_files, total_dirs,
                         threshold_bytes, app_version, complete)
                        VALUES ($rp, $rk, $ca, $sc, $fs, $vt, $vf, $ts, $tf, $td, $th, $av, 0);
                        SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$rp", PathKeys.NormalizeDisplay(result.ScannedPath));
                    cmd.Parameters.AddWithValue("$rk", PathKeys.ToRootKey(result.ScannedPath));
                    cmd.Parameters.AddWithValue("$ca", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$sc", OperatingSystem.IsWindows() ? "mft-or-recursive" : "recursive");
                    cmd.Parameters.AddWithValue("$fs", (object?)NullIfEmpty(result.FileSystem) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$vt", result.VolumeTotal);
                    cmd.Parameters.AddWithValue("$vf", result.VolumeFree);
                    cmd.Parameters.AddWithValue("$ts", result.TotalSize);
                    cmd.Parameters.AddWithValue("$tf", result.TotalFiles);
                    cmd.Parameters.AddWithValue("$td", result.TotalFolders);
                    cmd.Parameters.AddWithValue("$th", options.ThresholdBytes);
                    cmd.Parameters.AddWithValue("$av", (object?)appVersion ?? DBNull.Value);
                    snapshotId = Convert.ToInt64(cmd.ExecuteScalar());
                }

                InsertNodes(tx, snapshotId, result.Root, options.ThresholdBytes);

                using (var done = Database.Connection.CreateCommand())
                {
                    done.Transaction = tx;
                    done.CommandText = "UPDATE snapshots SET complete = 1 WHERE id = $id";
                    done.Parameters.AddWithValue("$id", snapshotId);
                    done.ExecuteNonQuery();
                }
                tx.Commit();
            }

            try
            {
                ApplyRetention(options);
            }
            catch
            {
                // Spec §8: retention is non-fatal and retried on the next write;
                // the snapshot itself already committed above.
            }
            return snapshotId;
        }
    }

    private void InsertNodes(SqliteTransaction tx, long snapshotId, DiskNode root, long threshold)
    {
        using var cmd = Database.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO nodes
            (snapshot_id, id, parent_id, name, is_dir, size, allocated_size,
             file_count, folder_count, last_modified, created, last_accessed,
             small_files_size, small_files_count)
            VALUES ($sid, $id, $pid, $n, $d, $s, $a, $fc, $dc, $lm, $cr, $la, $sfs, $sfc)";
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pId  = cmd.Parameters.Add("$id",  SqliteType.Integer);
        var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var pN   = cmd.Parameters.Add("$n",   SqliteType.Text);
        var pD   = cmd.Parameters.Add("$d",   SqliteType.Integer);
        var pS   = cmd.Parameters.Add("$s",   SqliteType.Integer);
        var pA   = cmd.Parameters.Add("$a",   SqliteType.Integer);
        var pFc  = cmd.Parameters.Add("$fc",  SqliteType.Integer);
        var pDc  = cmd.Parameters.Add("$dc",  SqliteType.Integer);
        var pLm  = cmd.Parameters.Add("$lm",  SqliteType.Text);
        var pCr  = cmd.Parameters.Add("$cr",  SqliteType.Text);
        var pLa  = cmd.Parameters.Add("$la",  SqliteType.Text);
        var pSfs = cmd.Parameters.Add("$sfs", SqliteType.Integer);
        var pSfc = cmd.Parameters.Add("$sfc", SqliteType.Integer);
        pSid.Value = snapshotId;

        long nextId = 0;
        // Iterative DFS: (node, parentRowId). Only dirs and files >= threshold get rows.
        var stack = new Stack<(DiskNode Node, long? ParentRowId)>();
        stack.Push((root, null));
        while (stack.Count > 0)
        {
            var (node, parentRowId) = stack.Pop();
            var rowId = nextId++;
            long smallSize = 0, smallCount = 0;
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    if (!child.IsDirectory && child.Size < threshold)
                    {
                        smallSize += child.Size;
                        smallCount++;
                    }
                }
            }

            pId.Value  = rowId;
            pPid.Value = (object?)parentRowId ?? DBNull.Value;
            pN.Value   = node.Name;
            pD.Value   = node.IsDirectory ? 1 : 0;
            pS.Value   = node.Size;
            pA.Value   = node.AllocatedSize;
            pFc.Value  = node.FileCount;
            pDc.Value  = node.FolderCount;
            pLm.Value  = IsoOrNull(node.LastModified);
            pCr.Value  = IsoOrNull(node.Created);
            pLa.Value  = IsoOrNull(node.LastAccessed);
            pSfs.Value = smallSize;
            pSfc.Value = smallCount;
            cmd.ExecuteNonQuery();

            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    if (child.IsDirectory || child.Size >= threshold)
                        stack.Push((child, rowId));
                }
            }
        }
    }

    private static object IsoOrNull(DateTime dt) =>
        dt == default ? DBNull.Value : dt.ToUniversalTime().ToString("o");

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    public IReadOnlyList<SnapshotInfo> ListSnapshots(string? rootPath = null)
    {
        using var cmd = Database.ReadConnection.CreateCommand();
        cmd.CommandText = @"SELECT id, root_path, root_key, created_at, scanner, file_system,
                                   volume_total, volume_free, total_size, total_files, total_dirs,
                                   threshold_bytes, app_version
                            FROM snapshots WHERE complete = 1"
                          + (rootPath != null ? " AND root_key = $rk" : "")
                          + " ORDER BY created_at DESC, id DESC";
        if (rootPath != null) cmd.Parameters.AddWithValue("$rk", PathKeys.ToRootKey(rootPath));
        var list = new List<SnapshotInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadInfo(r));
        return list;
    }

    public SnapshotInfo? GetSnapshot(long id)
    {
        using var cmd = Database.ReadConnection.CreateCommand();
        cmd.CommandText = @"SELECT id, root_path, root_key, created_at, scanner, file_system,
                                   volume_total, volume_free, total_size, total_files, total_dirs,
                                   threshold_bytes, app_version
                            FROM snapshots WHERE id = $id AND complete = 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadInfo(r) : null;
    }

    private static SnapshotInfo ReadInfo(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        RootPath: r.GetString(1),
        RootKey: r.GetString(2),
        CreatedAtUtc: DateTime.Parse(r.GetString(3), null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        Scanner: r.GetString(4),
        FileSystem: r.IsDBNull(5) ? null : r.GetString(5),
        VolumeTotal: r.IsDBNull(6) ? 0 : r.GetInt64(6),
        VolumeFree: r.IsDBNull(7) ? 0 : r.GetInt64(7),
        TotalSize: r.IsDBNull(8) ? 0 : r.GetInt64(8),
        TotalFiles: r.IsDBNull(9) ? 0 : r.GetInt64(9),
        TotalDirs: r.IsDBNull(10) ? 0 : r.GetInt64(10),
        ThresholdBytes: r.GetInt64(11),
        AppVersion: r.IsDBNull(12) ? null : r.GetString(12));

    public IReadOnlyList<SnapshotNode> LoadNodes(long snapshotId)
    {
        using var cmd = Database.ReadConnection.CreateCommand();
        cmd.CommandText = @"SELECT id, parent_id, name, is_dir, size, allocated_size,
                                   file_count, folder_count, last_modified, created, last_accessed,
                                   small_files_size, small_files_count
                            FROM nodes WHERE snapshot_id = $sid";
        cmd.Parameters.AddWithValue("$sid", snapshotId);
        var list = new List<SnapshotNode>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SnapshotNode
            {
                Id = r.GetInt64(0),
                ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
                Name = r.GetString(2),
                IsDirectory = r.GetInt64(3) == 1,
                Size = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                AllocatedSize = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                FileCount = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                FolderCount = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                LastModified = ParseIso(r, 8),
                Created = ParseIso(r, 9),
                LastAccessed = ParseIso(r, 10),
                SmallFilesSize = r.IsDBNull(11) ? 0 : r.GetInt64(11),
                SmallFilesCount = r.IsDBNull(12) ? 0 : r.GetInt64(12),
            });
        }
        return list;
    }

    private static DateTime? ParseIso(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), null,
            System.Globalization.DateTimeStyles.RoundtripKind);

    public void Delete(long snapshotId)
    {
        lock (_writeLock)
        {
            DeleteCore(snapshotId);
        }
    }

    private void DeleteCore(long snapshotId)
    {
        using var tx = Database.Connection.BeginTransaction();
        using var cmd = Database.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"DELETE FROM nodes WHERE snapshot_id = $id;
                            DELETE FROM snapshots WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public int SweepIncomplete()
    {
        lock (_writeLock)
        {
            using var tx = Database.Connection.BeginTransaction();
            using var cmd = Database.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"DELETE FROM nodes WHERE snapshot_id IN
                                    (SELECT id FROM snapshots WHERE complete = 0);
                                DELETE FROM snapshots WHERE complete = 0;
                                SELECT changes();";
            var removed = Convert.ToInt32(cmd.ExecuteScalar());
            tx.Commit();
            return removed;
        }
    }

    public long GetStoreSizeBytes() => Database.GetDatabaseSizeBytes();

    internal void ApplyRetention(SnapshotOptions options)
    {
        // Defensive floor (review finding, 2026-07-19): a non-positive RetentionPerRoot
        // must never be allowed to prune the snapshot Save just inserted.
        var keep = Math.Max(1, options.RetentionPerRoot);

        // Pass 1 — per-root count (spec §4: default 26 ≈ six months of weekly scans).
        // Delete over-limit snapshot rows directly, then orphan-sweep their nodes.
        int pass1Rows;
        using (var tx = Database.Connection.BeginTransaction())
        {
            using (var cmd = Database.Connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    DELETE FROM snapshots WHERE id IN (
                        SELECT id FROM snapshots s
                        WHERE (SELECT COUNT(*) FROM snapshots n
                               WHERE n.root_key = s.root_key
                                 AND (n.created_at > s.created_at
                                      OR (n.created_at = s.created_at AND n.id > s.id)))
                              >= $keep);";
                cmd.Parameters.AddWithValue("$keep", keep);
                pass1Rows = cmd.ExecuteNonQuery();
            }
            using (var orphans = Database.Connection.CreateCommand())
            {
                orphans.Transaction = tx;
                orphans.CommandText = "DELETE FROM nodes WHERE snapshot_id NOT IN (SELECT id FROM snapshots);";
                orphans.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // Pass 2 — fair global size cap (spec §4): while over cap, delete the OLDEST
        // snapshot from the root that currently holds the MOST snapshots; never drop
        // a root's last remaining snapshot. Size is LIVE bytes — file length doesn't
        // shrink until VACUUM (the loop would otherwise over-delete every root to 1),
        // and a VACUUM per iteration rewrites the whole file each time (review
        // consensus, 2026-07-19). One VACUUM after the loop reclaims the space.
        // deletedAny also captures pass 1's deletions (review finding, 2026-07-19) —
        // pass-1-only prunes free pages too and must not skip the reclaim.
        var deletedAny = pass1Rows > 0;
        while (Database.GetLiveSizeBytes() > options.MaxDbSizeBytes)
        {
            long victim;
            using (var pick = Database.Connection.CreateCommand())
            {
                pick.CommandText = @"
                    SELECT s.id FROM snapshots s
                    WHERE s.root_key = (
                        SELECT root_key FROM snapshots
                        GROUP BY root_key HAVING COUNT(*) > 1
                        ORDER BY COUNT(*) DESC LIMIT 1)
                    ORDER BY s.created_at ASC, s.id ASC LIMIT 1";
                var res = pick.ExecuteScalar();
                if (res is null || res is DBNull) break; // every root is down to 1 — stop
                victim = Convert.ToInt64(res);
            }
            DeleteCore(victim);
            deletedAny = true;
        }
        if (deletedAny)
        {
            Database.Checkpoint();
            RunVacuum();
        }
    }

    private void RunVacuum()
    {
        using var cmd = Database.Connection.CreateCommand();
        cmd.CommandText = "VACUUM;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Database.Dispose();
}
