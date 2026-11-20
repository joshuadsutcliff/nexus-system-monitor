use crate::{DiskNode, DiskScanReport, SnapshotDbError};
use compact_str::CompactString;
use nexus_core::{Metric, MetricReading, SystemSnapshot};
use rusqlite::{Connection, params};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiskSnapshotInfo {
    pub id: i64,
    pub label: CompactString,
    pub root_path: CompactString,
    pub created_at_unix_ms: i64,
    pub total_bytes: u64,
    pub total_files: u64,
    pub total_dirs: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiskPathDelta {
    pub path: CompactString,
    pub baseline_bytes: u64,
    pub target_bytes: u64,
    pub delta_bytes: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiskSnapshotDiff {
    pub baseline_id: i64,
    pub target_id: i64,
    pub root_path: CompactString,
    pub total_delta_bytes: i64,
    pub total_delta_files: i64,
    pub added_paths: Vec<DiskPathDelta>,
    pub removed_paths: Vec<DiskPathDelta>,
    pub grown_paths: Vec<DiskPathDelta>,
    pub shrunk_paths: Vec<DiskPathDelta>,
}

/// Persistent SQLite store for disk directory scans (`disk_snapshots` & `disk_nodes`).
pub struct DiskSnapshotStore {
    conn: Connection,
}

impl DiskSnapshotStore {
    pub fn open_in_memory() -> Result<Self, SnapshotDbError> {
        let conn = Connection::open_in_memory()?;
        let store = Self { conn };
        store.init_schema()?;
        Ok(store)
    }

    pub fn open_path(path: &Path) -> Result<Self, SnapshotDbError> {
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let conn = Connection::open(path)?;
        let store = Self { conn };
        store.init_schema()?;
        Ok(store)
    }

    fn init_schema(&self) -> Result<(), SnapshotDbError> {
        self.conn.execute_batch(
            "PRAGMA journal_mode = WAL;
             CREATE TABLE IF NOT EXISTS disk_snapshots (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 label TEXT NOT NULL,
                 root_path TEXT NOT NULL,
                 created_at_unix_ms INTEGER NOT NULL,
                 total_bytes INTEGER NOT NULL,
                 total_files INTEGER NOT NULL,
                 total_dirs INTEGER NOT NULL,
                 complete INTEGER NOT NULL DEFAULT 0
             );
             CREATE TABLE IF NOT EXISTS disk_nodes (
                 snapshot_id INTEGER NOT NULL,
                 path TEXT NOT NULL,
                 name TEXT NOT NULL,
                 is_dir INTEGER NOT NULL,
                 size INTEGER NOT NULL,
                 file_count INTEGER NOT NULL,
                 PRIMARY KEY (snapshot_id, path)
             );
             DELETE FROM disk_nodes WHERE snapshot_id IN (SELECT id FROM disk_snapshots WHERE complete = 0);
             DELETE FROM disk_snapshots WHERE complete = 0;",
        )?;
        Ok(())
    }

    pub fn save_disk_snapshot(
        &mut self,
        report: &DiskScanReport,
        label: Option<&str>,
    ) -> Result<i64, SnapshotDbError> {
        let now_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(0);
        let lbl = label.unwrap_or("Scan");

        let tx = self.conn.transaction()?;
        tx.execute(
            "INSERT INTO disk_snapshots
             (label, root_path, created_at_unix_ms, total_bytes, total_files, total_dirs, complete)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, 0)",
            params![
                lbl,
                report.root_path.as_str(),
                now_ms,
                report.total_bytes as i64,
                report.total_files as i64,
                report.root_node.folder_count as i64,
            ],
        )?;
        let sid = tx.last_insert_rowid();

        {
            let mut stmt = tx.prepare(
                "INSERT OR REPLACE INTO disk_nodes (snapshot_id, path, name, is_dir, size, file_count)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            )?;
            insert_tree_nodes(&mut stmt, sid, &report.root_node)?;
        }

        tx.execute(
            "UPDATE disk_snapshots SET complete = 1 WHERE id = ?1",
            params![sid],
        )?;
        tx.commit()?;
        Ok(sid)
    }

    pub fn list_disk_snapshots(
        &self,
        root_filter: Option<&str>,
    ) -> Result<Vec<DiskSnapshotInfo>, SnapshotDbError> {
        let mut out = Vec::new();
        let sql = if root_filter.is_some() {
            "SELECT id, label, root_path, created_at_unix_ms, total_bytes, total_files, total_dirs
             FROM disk_snapshots WHERE complete = 1 AND root_path = ?1 ORDER BY id DESC"
        } else {
            "SELECT id, label, root_path, created_at_unix_ms, total_bytes, total_files, total_dirs
             FROM disk_snapshots WHERE complete = 1 ORDER BY id DESC"
        };
        let mut stmt = self.conn.prepare(sql)?;
        let map_row = |row: &rusqlite::Row<'_>| -> rusqlite::Result<DiskSnapshotInfo> {
            Ok(DiskSnapshotInfo {
                id: row.get(0)?,
                label: CompactString::from(row.get::<_, String>(1)?),
                root_path: CompactString::from(row.get::<_, String>(2)?),
                created_at_unix_ms: row.get(3)?,
                total_bytes: row.get::<_, i64>(4)?.max(0) as u64,
                total_files: row.get::<_, i64>(5)?.max(0) as u64,
                total_dirs: row.get::<_, i64>(6)?.max(0) as u64,
            })
        };
        if let Some(rf) = root_filter {
            let rows = stmt.query_map(params![rf], map_row)?;
            for r in rows {
                out.push(r?);
            }
        } else {
            let rows = stmt.query_map([], map_row)?;
            for r in rows {
                out.push(r?);
            }
        }
        Ok(out)
    }

    pub fn delete_disk_snapshot(&mut self, id: i64) -> Result<bool, SnapshotDbError> {
        let tx = self.conn.transaction()?;
        tx.execute("DELETE FROM disk_nodes WHERE snapshot_id = ?1", params![id])?;
        let deleted = tx.execute("DELETE FROM disk_snapshots WHERE id = ?1", params![id])?;
        tx.commit()?;
        Ok(deleted > 0)
    }

    pub fn diff_disk_snapshots(
        &self,
        baseline_id: i64,
        target_id: i64,
    ) -> Result<DiskSnapshotDiff, SnapshotDbError> {
        let base_info = self.get_info(baseline_id)?;
        let targ_info = self.get_info(target_id)?;
        let base_nodes = self.load_nodes_map(baseline_id)?;
        let targ_nodes = self.load_nodes_map(target_id)?;

        let mut added_paths = Vec::new();
        let mut removed_paths = Vec::new();
        let mut grown_paths = Vec::new();
        let mut shrunk_paths = Vec::new();

        for (path, &t_size) in &targ_nodes {
            match base_nodes.get(path) {
                None => {
                    added_paths.push(DiskPathDelta {
                        path: CompactString::from(path.as_str()),
                        baseline_bytes: 0,
                        target_bytes: t_size,
                        delta_bytes: t_size as i64,
                    });
                }
                Some(&b_size) if t_size > b_size => {
                    grown_paths.push(DiskPathDelta {
                        path: CompactString::from(path.as_str()),
                        baseline_bytes: b_size,
                        target_bytes: t_size,
                        delta_bytes: (t_size - b_size) as i64,
                    });
                }
                Some(&b_size) if t_size < b_size => {
                    shrunk_paths.push(DiskPathDelta {
                        path: CompactString::from(path.as_str()),
                        baseline_bytes: b_size,
                        target_bytes: t_size,
                        delta_bytes: -((b_size - t_size) as i64),
                    });
                }
                Some(_) => {}
            }
        }

        for (path, &b_size) in &base_nodes {
            if !targ_nodes.contains_key(path) {
                removed_paths.push(DiskPathDelta {
                    path: CompactString::from(path.as_str()),
                    baseline_bytes: b_size,
                    target_bytes: 0,
                    delta_bytes: -(b_size as i64),
                });
            }
        }

        added_paths.sort_by_key(|a| std::cmp::Reverse(a.delta_bytes));
        removed_paths.sort_by_key(|a| a.delta_bytes);
        grown_paths.sort_by_key(|a| std::cmp::Reverse(a.delta_bytes));
        shrunk_paths.sort_by_key(|a| a.delta_bytes);

        Ok(DiskSnapshotDiff {
            baseline_id,
            target_id,
            root_path: targ_info.root_path,
            total_delta_bytes: (targ_info.total_bytes as i64) - (base_info.total_bytes as i64),
            total_delta_files: (targ_info.total_files as i64) - (base_info.total_files as i64),
            added_paths,
            removed_paths,
            grown_paths,
            shrunk_paths,
        })
    }

    fn get_info(&self, id: i64) -> Result<DiskSnapshotInfo, SnapshotDbError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, label, root_path, created_at_unix_ms, total_bytes, total_files, total_dirs
             FROM disk_snapshots WHERE id = ?1 AND complete = 1",
        )?;
        let mut rows = stmt.query(params![id])?;
        if let Some(row) = rows.next()? {
            Ok(DiskSnapshotInfo {
                id: row.get(0)?,
                label: CompactString::from(row.get::<_, String>(1)?),
                root_path: CompactString::from(row.get::<_, String>(2)?),
                created_at_unix_ms: row.get(3)?,
                total_bytes: row.get::<_, i64>(4)?.max(0) as u64,
                total_files: row.get::<_, i64>(5)?.max(0) as u64,
                total_dirs: row.get::<_, i64>(6)?.max(0) as u64,
            })
        } else {
            Err(SnapshotDbError::NotFound(id))
        }
    }

    fn load_nodes_map(&self, sid: i64) -> Result<HashMap<String, u64>, SnapshotDbError> {
        let mut stmt = self
            .conn
            .prepare("SELECT path, size FROM disk_nodes WHERE snapshot_id = ?1")?;
        let rows = stmt.query_map(params![sid], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, i64>(1)?.max(0) as u64,
            ))
        })?;
        let mut map = HashMap::new();
        for r in rows {
            let (p, sz) = r?;
            map.insert(p, sz);
        }
        Ok(map)
    }
}

fn insert_tree_nodes(
    stmt: &mut rusqlite::Statement<'_>,
    sid: i64,
    root: &DiskNode,
) -> Result<(), rusqlite::Error> {
    let mut stack = vec![root];
    while let Some(node) = stack.pop() {
        stmt.execute(params![
            sid,
            node.full_path.as_str(),
            node.name.as_str(),
            i32::from(node.is_dir),
            node.size as i64,
            node.file_count as i64,
        ])?;
        for child in &node.children {
            if child.is_dir || child.size >= 4096 {
                stack.push(child);
            }
        }
    }
    Ok(())
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct MetricsHistoryRow {
    pub id: i64,
    pub sampled_at_unix_ms: i64,
    pub generation: u64,
    pub cpu_percent: Option<f64>,
    pub mem_used_bytes: Option<f64>,
    pub mem_used_percent: Option<f64>,
    pub gpu_percent: Option<f64>,
    pub package_watts: Option<f64>,
    pub max_die_celsius: Option<f64>,
    pub disk_read_bps: Option<f64>,
    pub disk_write_bps: Option<f64>,
    pub net_rx_bps: Option<f64>,
    pub net_tx_bps: Option<f64>,
}

/// Rolling SQLite history store (`telemetry.db`) preserving `NULL` for unavailable metrics.
pub struct MetricsHistoryStore {
    conn: Connection,
}

impl MetricsHistoryStore {
    pub fn open_in_memory() -> Result<Self, SnapshotDbError> {
        let conn = Connection::open_in_memory()?;
        let s = Self { conn };
        s.init_schema()?;
        Ok(s)
    }

    pub fn open_path(path: &Path) -> Result<Self, SnapshotDbError> {
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let conn = Connection::open(path)?;
        let s = Self { conn };
        s.init_schema()?;
        Ok(s)
    }

    fn init_schema(&self) -> Result<(), SnapshotDbError> {
        self.conn.execute_batch(
            "PRAGMA journal_mode = WAL;
             PRAGMA synchronous = NORMAL;
             CREATE TABLE IF NOT EXISTS metrics_history (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 sampled_at_unix_ms INTEGER NOT NULL,
                 generation INTEGER NOT NULL,
                 cpu_percent REAL,
                 mem_used_bytes REAL,
                 mem_used_percent REAL,
                 gpu_percent REAL,
                 package_watts REAL,
                 max_die_celsius REAL,
                 disk_read_bps REAL,
                 disk_write_bps REAL,
                 net_rx_bps REAL,
                 net_tx_bps REAL
             );",
        )?;
        Ok(())
    }

    pub fn insert_snapshot(
        &self,
        snap: &SystemSnapshot,
        sampled_at_unix_ms: i64,
    ) -> Result<i64, SnapshotDbError> {
        let opt = |m: &Metric<f64>| match m.reading {
            MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
            _ => None,
        };
        let disk0 = snap.disks.first();
        let net0 = snap
            .networks
            .iter()
            .find(|n| n.interface.as_str() == "en0")
            .or_else(|| snap.networks.first());

        self.conn.execute(
            "INSERT INTO metrics_history (
                sampled_at_unix_ms, generation, cpu_percent, mem_used_bytes, mem_used_percent,
                gpu_percent, package_watts, max_die_celsius, disk_read_bps, disk_write_bps,
                net_rx_bps, net_tx_bps
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                sampled_at_unix_ms,
                snap.generation as i64,
                opt(&snap.system.cpu_percent),
                opt(&snap.system.mem_used_bytes),
                opt(&snap.system.mem_used_percent),
                opt(&snap.system.gpu_utilization_percent),
                opt(&snap.hardware_telemetry.soc_power.package_watts),
                opt(&snap.hardware_telemetry.thermals.max_die_celsius),
                disk0.and_then(|d| opt(&d.read_bytes_per_sec)),
                disk0.and_then(|d| opt(&d.write_bytes_per_sec)),
                net0.and_then(|n| opt(&n.rx_bytes_per_sec)),
                net0.and_then(|n| opt(&n.tx_bytes_per_sec)),
            ],
        )?;
        Ok(self.conn.last_insert_rowid())
    }

    pub fn query_recent(&self, limit: usize) -> Result<Vec<MetricsHistoryRow>, SnapshotDbError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, sampled_at_unix_ms, generation, cpu_percent, mem_used_bytes,
                    mem_used_percent, gpu_percent, package_watts, max_die_celsius,
                    disk_read_bps, disk_write_bps, net_rx_bps, net_tx_bps
             FROM metrics_history
             ORDER BY id DESC
             LIMIT ?1",
        )?;
        let rows = stmt.query_map(params![limit.max(1) as i64], |r| {
            Ok(MetricsHistoryRow {
                id: r.get(0)?,
                sampled_at_unix_ms: r.get(1)?,
                generation: r.get::<_, i64>(2)?.max(0) as u64,
                cpu_percent: r.get(3)?,
                mem_used_bytes: r.get(4)?,
                mem_used_percent: r.get(5)?,
                gpu_percent: r.get(6)?,
                package_watts: r.get(7)?,
                max_die_celsius: r.get(8)?,
                disk_read_bps: r.get(9)?,
                disk_write_bps: r.get(10)?,
                net_rx_bps: r.get(11)?,
                net_tx_bps: r.get(12)?,
            })
        })?;
        let mut out = Vec::new();
        for row in rows {
            out.push(row?);
        }
        out.reverse();
        Ok(out)
    }

    pub fn row_count(&self) -> Result<usize, SnapshotDbError> {
        let cnt: i64 = self
            .conn
            .query_row("SELECT COUNT(*) FROM metrics_history", [], |r| r.get(0))?;
        Ok(cnt.max(0) as usize)
    }

    pub fn prune_to_max_rows(&self, max_rows: usize) -> Result<usize, SnapshotDbError> {
        let deleted = self.conn.execute(
            "DELETE FROM metrics_history WHERE id NOT IN (
                 SELECT id FROM metrics_history ORDER BY id DESC LIMIT ?1
             )",
            params![max_rows.max(1) as i64],
        )?;
        Ok(deleted)
    }

    /// Queries time-bucketed `(bucket_ts_ms, avg_cpu, avg_mem, avg_temp)` points from `metrics_history`
    /// since `since_unix_ms`, downsampled to at most `max_buckets`.
    pub fn query_health_trend_points(
        &self,
        since_unix_ms: i64,
        max_buckets: usize,
    ) -> Result<Vec<(i64, f64, f64, f64)>, SnapshotDbError> {
        let mut stmt = self.conn.prepare(
            "SELECT sampled_at_unix_ms, cpu_percent, mem_used_percent, max_die_celsius
             FROM metrics_history
             WHERE sampled_at_unix_ms >= ?1
             ORDER BY sampled_at_unix_ms ASC",
        )?;
        let rows = stmt.query_map(params![since_unix_ms], |r| {
            Ok((
                r.get::<_, i64>(0)?,
                r.get::<_, Option<f64>>(1)?,
                r.get::<_, Option<f64>>(2)?,
                r.get::<_, Option<f64>>(3)?,
            ))
        })?;
        let mut raw = Vec::new();
        for row in rows {
            raw.push(row?);
        }
        if raw.is_empty() {
            return Ok(Vec::new());
        }
        let buckets = max_buckets.max(1);
        let chunk_size = raw.len().div_ceil(buckets).max(1);
        let mut out = Vec::with_capacity(raw.len().div_ceil(chunk_size));
        for chunk in raw.chunks(chunk_size) {
            let mut ts_sum = 0_i128;
            let mut cpu_sum = 0.0_f64;
            let mut cpu_n = 0usize;
            let mut mem_sum = 0.0_f64;
            let mut mem_n = 0usize;
            let mut temp_sum = 0.0_f64;
            let mut temp_n = 0usize;
            for &(ts, cpu, mem, temp) in chunk {
                ts_sum += i128::from(ts);
                if let Some(v) = cpu.filter(|x| x.is_finite()) {
                    cpu_sum += v;
                    cpu_n += 1;
                }
                if let Some(v) = mem.filter(|x| x.is_finite()) {
                    mem_sum += v;
                    mem_n += 1;
                }
                if let Some(v) = temp.filter(|x| x.is_finite()) {
                    temp_sum += v;
                    temp_n += 1;
                }
            }
            let avg_ts = (ts_sum / (chunk.len() as i128)) as i64;
            let avg_cpu = if cpu_n > 0 {
                cpu_sum / (cpu_n as f64)
            } else {
                0.0
            };
            let avg_mem = if mem_n > 0 {
                mem_sum / (mem_n as f64)
            } else {
                0.0
            };
            let avg_temp = if temp_n > 0 {
                temp_sum / (temp_n as f64)
            } else {
                0.0
            };
            out.push((avg_ts, avg_cpu, avg_mem, avg_temp));
        }
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn query_health_trend_points_buckets_time_series_cleanly() {
        let store = MetricsHistoryStore::open_in_memory().expect("in-memory history store");
        let snap = SystemSnapshot::new_empty(1, 1_000_000);
        for i in 0..10_i64 {
            let mut s = snap.clone();
            let prov = nexus_core::MetricProvenance {
                source: nexus_core::SourceId(1),
                unit_tag: Some(nexus_core::UnitTag::PERCENT),
                sampled_at_ns: (i as u64 + 1) * 1_000_000_000,
                window_ns: 250_000_000,
                quality: nexus_core::MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            s.system.cpu_percent = Metric::measured(20.0 + (i as f64) * 2.0, prov);
            s.system.mem_used_percent = Metric::measured(50.0, prov);
            store
                .insert_snapshot(&s, 1_000 + i * 1_000)
                .expect("insert");
        }
        let pts = store
            .query_health_trend_points(1_000, 5)
            .expect("query trend");
        assert_eq!(pts.len(), 5);
        assert!(pts[0].1 < pts[4].1);
        assert!((pts[0].2 - 50.0).abs() < 1e-6);
    }
}
