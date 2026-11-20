pub use nexus_core::UncachedDiskIo;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::{Duration, Instant};
use thiserror::Error;

/// Strict minimum free space required before running the disk benchmark (`0x84000000` bytes).
pub const DISK_BENCH_MIN_FREE_BYTES: u64 = 2_214_592_512;
pub const DISK_BENCH_RESERVE_BYTES: u64 = 0x8000_0000;
pub const DISK_BENCH_TARGET_BYTES: u64 = 0x4000_0000;
pub const DISK_BENCH_MAX_BYTES: u64 = 0x8_0000_0000;
pub const DISK_BENCH_BUFFER_BYTES: usize = 0x80_0000; // 8 MiB
pub const DISK_BENCH_SEED: u64 = 0x2545_f491_4f6c_dd1d;

/// Maximum persisted history entries per benchmark category.
pub const MAX_BENCHMARK_HISTORY_ENTRIES: usize = 100;

/// Maximum UTF-8 character count for persisted benchmark entry notes.
pub const MAX_BENCHMARK_NOTE_CHARS: usize = 1024;

static BENCH_FILE_COUNTER: AtomicU64 = AtomicU64::new(1);

#[derive(Debug, Error)]
pub enum DiskBenchError {
    #[error("At least 2 GB of free disk space is required (available: {available} bytes).")]
    InsufficientSpace { available: u64 },
    #[error("Disk benchmark cancelled.")]
    Cancelled,
    #[error("Disk benchmark I/O error: {0}")]
    Io(#[from] std::io::Error),
}

/// Portable safe fallback implementation of `UncachedDiskIo` (used on non-macOS or tests).
#[derive(Debug, Default, Clone, Copy)]
pub struct StdDiskIo {
    pub fallback_available_bytes: Option<u64>,
}

impl UncachedDiskIo for StdDiskIo {
    fn available_space_bytes(&self, _dir: &Path) -> std::io::Result<u64> {
        Ok(self.fallback_available_bytes.unwrap_or(0))
    }

    fn open_uncached_rw(&self, path: &Path) -> std::io::Result<File> {
        OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(path)
    }

    fn full_sync(&self, file: &File) -> std::io::Result<()> {
        file.sync_all()
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DiskBenchmarkResult {
    pub read_mb_per_sec: f64,
    pub write_mb_per_sec: f64,
    pub payload_bytes: u64,
    pub bytes_written: u64,
    pub bytes_read: u64,
}

struct ScratchFileGuard(PathBuf);

impl Drop for ScratchFileGuard {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

/// Fills `buf` with incompressible pseudo-random bytes using Xorshift64* (`seed = 0x2545f4914f6cdd1d`).
pub fn fill_incompressible_buffer(buf: &mut [u8]) {
    let mut x = DISK_BENCH_SEED;
    for chunk in buf.as_chunks_mut::<8>().0 {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        chunk.copy_from_slice(&x.to_le_bytes());
    }
    let rem = buf.len() % 8;
    if rem > 0 {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        let tail_start = buf.len() - rem;
        buf[tail_start..].copy_from_slice(&x.to_le_bytes()[..rem]);
    }
}

/// Computes target payload bytes from free space while preserving a 2 GiB safety margin.
pub fn compute_disk_payload_bytes(
    available_bytes: u64,
    target_override: Option<u64>,
) -> Result<u64, DiskBenchError> {
    if available_bytes <= DISK_BENCH_MIN_FREE_BYTES {
        return Err(DiskBenchError::InsufficientSpace {
            available: available_bytes,
        });
    }
    let headroom = available_bytes.saturating_sub(DISK_BENCH_RESERVE_BYTES);
    let desired = target_override.unwrap_or(DISK_BENCH_TARGET_BYTES);
    Ok(desired.min(headroom).clamp(4096, DISK_BENCH_MAX_BYTES))
}

/// Runs the uncached disk write (`right`) and read (`left`) benchmark with `ScratchFileGuard` cleanup.
pub fn run_disk_benchmark(
    io: &dyn UncachedDiskIo,
    dir: &Path,
    duration: Duration,
    cancel: &AtomicBool,
    target_bytes_override: Option<u64>,
) -> Result<DiskBenchmarkResult, DiskBenchError> {
    let avail = io.available_space_bytes(dir)?;
    let payload_bytes = compute_disk_payload_bytes(avail, target_bytes_override)?;

    fs::create_dir_all(dir)?;
    let seq = BENCH_FILE_COUNTER.fetch_add(1, Ordering::Relaxed);
    let scratch_path = dir.join(format!(
        "nexus-disk-benchmark-{}-{seq}.bin",
        std::process::id()
    ));
    let _guard = ScratchFileGuard(scratch_path.clone());

    let mut file = io.open_uncached_rw(&scratch_path)?;
    let mut buf = vec![0u8; DISK_BENCH_BUFFER_BYTES];
    fill_incompressible_buffer(&mut buf);

    // Write pass (`right` metric): flush hardware write-back cache before stopping timer.
    let write_start = Instant::now();
    let mut bytes_written: u64 = 0;
    let mut file_offset: u64 = 0;
    loop {
        if cancel.load(Ordering::Relaxed) {
            return Err(DiskBenchError::Cancelled);
        }
        let remaining_in_file = payload_bytes.saturating_sub(file_offset);
        let chunk_len = (remaining_in_file as usize).min(buf.len());
        if chunk_len == 0 {
            file.seek(SeekFrom::Start(0))?;
            file_offset = 0;
            continue;
        }
        file.write_all(&buf[..chunk_len])?;
        bytes_written += chunk_len as u64;
        file_offset += chunk_len as u64;
        if write_start.elapsed() >= duration {
            break;
        }
    }
    io.full_sync(&file)?;
    let write_secs = write_start.elapsed().as_secs_f64().max(1e-6);
    let write_mb_per_sec = (bytes_written as f64) / (write_secs * 1_000_000.0);

    // Read pass (`left` metric): rewind to offset 0 on EOF.
    file.seek(SeekFrom::Start(0))?;
    let read_start = Instant::now();
    let mut bytes_read: u64 = 0;
    loop {
        if cancel.load(Ordering::Relaxed) {
            return Err(DiskBenchError::Cancelled);
        }
        let n = file.read(&mut buf)?;
        if n == 0 {
            if bytes_read == 0 {
                break;
            }
            file.seek(SeekFrom::Start(0))?;
            continue;
        }
        bytes_read += n as u64;
        if read_start.elapsed() >= duration {
            break;
        }
    }
    let read_secs = read_start.elapsed().as_secs_f64().max(1e-6);
    let read_mb_per_sec = (bytes_read as f64) / (read_secs * 1_000_000.0);

    Ok(DiskBenchmarkResult {
        read_mb_per_sec,
        write_mb_per_sec,
        payload_bytes,
        bytes_written,
        bytes_read,
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
pub enum BenchmarkCategory {
    CPU,
    GPU,
    Disk,
    Internet,
}

impl BenchmarkCategory {
    pub const ALL: [Self; 4] = [Self::CPU, Self::GPU, Self::Disk, Self::Internet];

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::CPU => "CPU",
            Self::GPU => "GPU",
            Self::Disk => "Disk",
            Self::Internet => "Internet",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BenchmarkHistoryEntry {
    pub recorded_at: i64,
    pub left: f64,
    pub right: f64,
    pub note: String,
}

/// Truncates `s` to at most `max_chars` Unicode scalar values.
pub fn truncate_utf8_codepoints(s: &str, max_chars: usize) -> String {
    s.chars().take(max_chars).collect()
}

/// Persistent benchmark history store (`benchmark_history.json`), capped at 100 entries per category
/// sorted descending by `recorded_at`.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct BenchmarkHistoryStore {
    pub categories: BTreeMap<BenchmarkCategory, Vec<BenchmarkHistoryEntry>>,
}

pub fn default_benchmark_history_path() -> PathBuf {
    crate::default_app_support_dir().join("benchmark_history.json")
}

impl BenchmarkHistoryStore {
    pub fn load(path: &Path) -> Self {
        let Ok(raw) = fs::read_to_string(path) else {
            return Self::default();
        };
        let mut store: Self = serde_json::from_str(&raw).unwrap_or_default();
        store.normalize_all();
        store
    }

    pub fn load_or_default(path: &Path) -> Self {
        Self::load(path)
    }

    pub fn save(&mut self, path: &Path) -> std::io::Result<()> {
        self.normalize_all();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(self)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        fs::write(path, json)
    }

    pub fn record(
        &mut self,
        category: BenchmarkCategory,
        recorded_at: i64,
        left: f64,
        right: f64,
        note: &str,
    ) {
        let entries = self.categories.entry(category).or_default();
        entries.push(BenchmarkHistoryEntry {
            recorded_at,
            left: if left.is_finite() { left.max(0.0) } else { 0.0 },
            right: if right.is_finite() {
                right.max(0.0)
            } else {
                0.0
            },
            note: truncate_utf8_codepoints(note, MAX_BENCHMARK_NOTE_CHARS),
        });
        Self::normalize_entries(entries);
    }

    pub fn entries(&self, category: BenchmarkCategory) -> &[BenchmarkHistoryEntry] {
        self.categories
            .get(&category)
            .map(Vec::as_slice)
            .unwrap_or(&[])
    }

    pub fn latest(&self, category: BenchmarkCategory) -> Option<&BenchmarkHistoryEntry> {
        self.entries(category).first()
    }

    fn normalize_all(&mut self) {
        for entries in self.categories.values_mut() {
            Self::normalize_entries(entries);
        }
    }

    fn normalize_entries(entries: &mut Vec<BenchmarkHistoryEntry>) {
        for e in entries.iter_mut() {
            e.note = truncate_utf8_codepoints(&e.note, MAX_BENCHMARK_NOTE_CHARS);
        }
        entries.sort_by_key(|a| std::cmp::Reverse(a.recorded_at));
        entries.truncate(MAX_BENCHMARK_HISTORY_ENTRIES);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn disk_bench_rejects_under_2gb_free_space() {
        let io = StdDiskIo {
            fallback_available_bytes: Some(DISK_BENCH_MIN_FREE_BYTES),
        };
        let cancel = AtomicBool::new(false);
        let dir = std::env::temp_dir().join("nexus_disk_bench_low");
        let err =
            run_disk_benchmark(&io, &dir, Duration::from_millis(20), &cancel, None).unwrap_err();
        assert!(matches!(err, DiskBenchError::InsufficientSpace { .. }));
    }

    #[test]
    fn disk_bench_writes_syncs_reads_and_cleans_up_scratch_file() {
        let io = StdDiskIo {
            fallback_available_bytes: Some(10 * 1024 * 1024 * 1024),
        };
        let cancel = AtomicBool::new(false);
        let dir = std::env::temp_dir().join(format!("nexus_disk_bench_ok_{}", std::process::id()));
        let res = run_disk_benchmark(
            &io,
            &dir,
            Duration::from_millis(40),
            &cancel,
            Some(8 * 1024 * 1024),
        )
        .expect("disk bench succeeds");
        assert!(res.write_mb_per_sec > 0.0);
        assert!(res.read_mb_per_sec > 0.0);
        assert!(res.bytes_written > 0);
        assert!(res.bytes_read > 0);

        // Scratch file must be removed by Drop guard.
        let leftover: Vec<_> = fs::read_dir(&dir).unwrap().filter_map(Result::ok).collect();
        assert!(leftover.is_empty());
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn benchmark_history_store_caps_at_100_sorts_desc_and_truncates_notes() {
        let mut store = BenchmarkHistoryStore::default();
        let long_note = "x".repeat(1500);
        for i in 0..120_i64 {
            store.record(
                BenchmarkCategory::CPU,
                i,
                i as f64,
                (i * 2) as f64,
                &long_note,
            );
        }
        let entries = store.entries(BenchmarkCategory::CPU);
        assert_eq!(entries.len(), 100);
        assert_eq!(entries[0].recorded_at, 119);
        assert_eq!(entries[99].recorded_at, 20);
        assert_eq!(entries[0].note.chars().count(), 1024);

        let corrupt_path =
            std::env::temp_dir().join(format!("nexus_corrupt_bench_{}.json", std::process::id()));
        fs::write(&corrupt_path, "{invalid json").expect("write corrupt bench");
        let recovered = BenchmarkHistoryStore::load_or_default(&corrupt_path);
        assert!(recovered.entries(BenchmarkCategory::CPU).is_empty());
        let _ = fs::remove_file(&corrupt_path);
    }
}
