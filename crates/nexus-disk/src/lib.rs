#![forbid(unsafe_code)]

pub mod benchmark;
pub mod duplicates;
pub mod stores;
pub mod trace;
pub mod treemap;

use compact_str::CompactString;
use nexus_core::{Metric, MetricReading, ProcessKey, ProcessSnapshot, SystemSnapshot};
use rayon::prelude::*;
use rusqlite::{Connection, params};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use thiserror::Error;
use walkdir::WalkDir;

pub use benchmark::{
    BenchmarkCategory, BenchmarkHistoryEntry, BenchmarkHistoryStore, DISK_BENCH_BUFFER_BYTES,
    DISK_BENCH_MAX_BYTES, DISK_BENCH_MIN_FREE_BYTES, DISK_BENCH_RESERVE_BYTES, DISK_BENCH_SEED,
    DISK_BENCH_TARGET_BYTES, DiskBenchError, DiskBenchmarkResult, MAX_BENCHMARK_HISTORY_ENTRIES,
    MAX_BENCHMARK_NOTE_CHARS, StdDiskIo, UncachedDiskIo, compute_disk_payload_bytes,
    default_benchmark_history_path, fill_incompressible_buffer, run_disk_benchmark,
    truncate_utf8_codepoints,
};
pub use duplicates::{DuplicateGroup, find_duplicates, sha256_bytes_hex, sha256_file_hex};
pub use nexus_core::VolumeSpaceSummary;
pub use stores::{
    DiskPathDelta, DiskSnapshotDiff, DiskSnapshotInfo, DiskSnapshotStore, MetricsHistoryRow,
    MetricsHistoryStore,
};
pub use trace::{
    MAX_TRACE_BYTES, NEXUS_TRACE_FORMAT, NexusTrace, NexusTraceEncodedSize,
    NexusTraceJsonPreflight, NexusTracePlayer, NexusTraceRecorder, NexusTraceSample, TraceError,
    default_traces_dir, format_utc_filename_stamp, format_utc_iso8601, next_trace_file_path,
    read_trace_file, save_trace_unique, write_trace_atomic_no_overwrite,
};
pub use treemap::{RectBounds, TreemapRect, compute_squarified_treemap};

/// Returns the default persistent telemetry SQLite database path:
/// `~/Library/Application Support/NexusSystemMonitor/telemetry.db`.
pub fn default_telemetry_db_path() -> PathBuf {
    default_app_support_dir().join("telemetry.db")
}

/// Returns the default persistent disk snapshots SQLite database path:
/// `~/Library/Application Support/NexusSystemMonitor/disk-snapshots.db`.
pub fn default_disk_snapshots_db_path() -> PathBuf {
    default_app_support_dir().join("disk-snapshots.db")
}

fn default_app_support_dir() -> PathBuf {
    #[cfg(target_os = "windows")]
    {
        let base = std::env::var_os("APPDATA")
            .filter(|v| !v.is_empty())
            .or_else(|| std::env::var_os("LOCALAPPDATA").filter(|v| !v.is_empty()))
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir);
        base.join("NexusSystemMonitor")
    }
    #[cfg(target_os = "linux")]
    {
        if let Some(xdg) = std::env::var_os("XDG_DATA_HOME").filter(|v| !v.is_empty()) {
            return PathBuf::from(xdg).join("NexusSystemMonitor");
        }
        if let Some(home) = std::env::var_os("HOME").filter(|v| !v.is_empty()) {
            return PathBuf::from(home)
                .join(".local")
                .join("share")
                .join("NexusSystemMonitor");
        }
        std::env::temp_dir().join("NexusSystemMonitor")
    }
    #[cfg(not(any(target_os = "windows", target_os = "linux")))]
    {
        if let Ok(home) = std::env::var("HOME")
            && !home.is_empty()
        {
            return PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join("NexusSystemMonitor");
        }
        std::env::temp_dir().join("NexusSystemMonitor")
    }
}

/// 7 Nexus file categories for disk breakdown and GPU visualization.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum DiskCategory {
    Media = 0,
    Documents = 1,
    Code = 2,
    Archives = 3,
    Applications = 4,
    SystemAndTemp = 5,
    Other = 6,
}

impl DiskCategory {
    pub const ALL: [Self; 7] = [
        Self::Media,
        Self::Documents,
        Self::Code,
        Self::Archives,
        Self::Applications,
        Self::SystemAndTemp,
        Self::Other,
    ];

    #[inline]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Media => "Media",
            Self::Documents => "Documents",
            Self::Code => "Code & Config",
            Self::Archives => "Archives & Images",
            Self::Applications => "Apps & Bundles",
            Self::SystemAndTemp => "Libraries & Caches",
            Self::Other => "Other",
        }
    }

    #[inline]
    pub const fn default_color_srgb(self) -> [f32; 3] {
        match self {
            Self::Media => [0.31, 0.82, 0.77],
            Self::Documents => [0.50, 0.70, 0.94],
            Self::Code => [0.83, 0.55, 1.00],
            Self::Archives => [1.00, 0.71, 0.33],
            Self::Applications => [0.48, 0.88, 0.54],
            Self::SystemAndTemp => [1.00, 0.48, 0.45],
            Self::Other => [0.60, 0.66, 0.72],
        }
    }
}

/// Classifies an extension (or bundle directory extension) into one of the 7 `DiskCategory` buckets.
pub fn classify_disk_extension(ext: &str, is_directory: bool) -> Option<DiskCategory> {
    let trimmed = ext.strip_prefix('.').unwrap_or(ext).to_ascii_lowercase();
    if is_directory {
        if matches!(
            trimmed.as_str(),
            "app" | "framework" | "bundle" | "plugin" | "kext" | "xpc" | "appex"
        ) {
            return Some(DiskCategory::Applications);
        }
        return None;
    }
    if trimmed.is_empty() {
        return Some(DiskCategory::Other);
    }
    if matches!(
        trimmed.as_str(),
        "png"
            | "jpg"
            | "jpeg"
            | "gif"
            | "heic"
            | "heif"
            | "tiff"
            | "tif"
            | "bmp"
            | "webp"
            | "svg"
            | "raw"
            | "cr2"
            | "nef"
            | "dng"
            | "psd"
            | "mp3"
            | "m4a"
            | "aac"
            | "wav"
            | "aiff"
            | "flac"
            | "ogg"
            | "opus"
            | "mp4"
            | "m4v"
            | "mov"
            | "avi"
            | "mkv"
            | "webm"
            | "wmv"
            | "flv"
    ) {
        return Some(DiskCategory::Media);
    }
    if matches!(
        trimmed.as_str(),
        "pdf"
            | "doc"
            | "docx"
            | "pages"
            | "txt"
            | "rtf"
            | "md"
            | "odt"
            | "xls"
            | "xlsx"
            | "numbers"
            | "csv"
            | "tsv"
            | "ods"
            | "ppt"
            | "pptx"
            | "key"
            | "odp"
            | "epub"
            | "mobi"
    ) {
        return Some(DiskCategory::Documents);
    }
    if matches!(
        trimmed.as_str(),
        "swift"
            | "c"
            | "h"
            | "cpp"
            | "cc"
            | "hpp"
            | "m"
            | "mm"
            | "rs"
            | "go"
            | "py"
            | "rb"
            | "js"
            | "ts"
            | "jsx"
            | "tsx"
            | "java"
            | "kt"
            | "cs"
            | "php"
            | "sh"
            | "zsh"
            | "bash"
            | "pl"
            | "lua"
            | "sql"
            | "html"
            | "css"
            | "json"
            | "xml"
            | "yml"
            | "yaml"
            | "toml"
            | "plist"
            | "cmake"
    ) {
        return Some(DiskCategory::Code);
    }
    if matches!(
        trimmed.as_str(),
        "zip"
            | "tar"
            | "gz"
            | "bz2"
            | "xz"
            | "7z"
            | "rar"
            | "dmg"
            | "iso"
            | "pkg"
            | "jar"
            | "war"
            | "aar"
            | "cab"
            | "zst"
    ) {
        return Some(DiskCategory::Archives);
    }
    if matches!(
        trimmed.as_str(),
        "app" | "framework" | "bundle" | "xcappdata"
    ) {
        return Some(DiskCategory::Applications);
    }
    if matches!(
        trimmed.as_str(),
        "dylib"
            | "so"
            | "a"
            | "o"
            | "kext"
            | "log"
            | "cache"
            | "db"
            | "sqlite"
            | "sqlite3"
            | "shm"
            | "wal"
            | "swp"
            | "tmp"
    ) {
        return Some(DiskCategory::SystemAndTemp);
    }
    Some(DiskCategory::Other)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CategoryTotal {
    pub category: DiskCategory,
    pub bytes: u64,
    pub file_count: u64,
    pub share_fraction: f32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiskNode {
    pub name: CompactString,
    pub full_path: CompactString,
    pub is_dir: bool,
    pub size: u64,
    pub file_count: u64,
    pub folder_count: u64,
    pub category: DiskCategory,
    pub children: Vec<DiskNode>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiskNodeSummary {
    pub name: CompactString,
    pub full_path: CompactString,
    pub size: u64,
    pub file_count: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DiskScanReport {
    pub root_path: CompactString,
    pub total_bytes: u64,
    pub total_files: u64,
    pub categories: Vec<CategoryTotal>,
    pub root_node: DiskNode,
    pub top_directories: Vec<DiskNodeSummary>,
    pub largest_files: Vec<DiskNodeSummary>,
}

/// Permission and I/O omissions encountered while walking directories.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiskScanOmissions {
    pub permission_denied_count: u64,
    pub other_error_count: u64,
    pub sample_omitted_paths: Vec<PathBuf>,
    pub full_disk_access_granted: bool,
}

impl DiskScanOmissions {
    pub const MAX_SAMPLE_PATHS: usize = 32;

    pub fn record_io_error(&mut self, path: &Path, err: &std::io::Error) {
        if err.kind() == std::io::ErrorKind::PermissionDenied {
            self.permission_denied_count += 1;
        } else {
            self.other_error_count += 1;
        }
        if self.sample_omitted_paths.len() < Self::MAX_SAMPLE_PATHS {
            self.sample_omitted_paths.push(path.to_path_buf());
        }
    }

    pub fn merge_from(&mut self, other: &Self) {
        self.permission_denied_count = self
            .permission_denied_count
            .saturating_add(other.permission_denied_count);
        self.other_error_count = self
            .other_error_count
            .saturating_add(other.other_error_count);
        for p in &other.sample_omitted_paths {
            if self.sample_omitted_paths.len() >= Self::MAX_SAMPLE_PATHS {
                break;
            }
            self.sample_omitted_paths.push(p.clone());
        }
    }
}

/// Probes TCC-protected sentinel locations to estimate whether Full Disk Access is granted.
pub fn probe_full_disk_access() -> bool {
    let mut probes = vec![PathBuf::from("/Library/Application Support/com.apple.TCC")];
    if let Ok(home) = std::env::var("HOME")
        && !home.is_empty()
    {
        probes.push(PathBuf::from(home).join("Library").join("Safari"));
    }
    for probe in probes {
        if probe.exists()
            && let Err(err) = std::fs::read_dir(&probe)
            && err.kind() == std::io::ErrorKind::PermissionDenied
        {
            return false;
        }
    }
    true
}

/// Periodic streaming progress update emitted by `scan_directory_streaming` every 50 ms.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DiskScanUpdate {
    pub root: PathBuf,
    pub scanned_bytes: u64,
    pub volume_used_bytes: u64,
    pub volume_total_bytes: u64,
    pub file_count: u64,
    pub dir_count: u64,
    pub category_bytes: [u64; 7],
    pub category_counts: [u64; 7],
    pub omissions: DiskScanOmissions,
    pub report: Option<DiskScanReport>,
    pub is_complete: bool,
}

/// Mirrors `disk_space.wgsl` Mode 1 (Ocean Metaballs) and Mode 2 (48x26 Defrag Blocks) on the CPU
/// for mouse hover/click hit-testing (`uv` in shader coords where `uv.y = 1.0 - egui_ny`).
pub fn entry_at_normalized(
    uv: (f32, f32),
    aspect: f32,
    mode: u32,
    time: f32,
    category_fractions: &[f32; 7],
    progress: f32,
) -> Option<DiskCategory> {
    if !(0.0..=1.0).contains(&uv.0) || !(0.0..=1.0).contains(&uv.1) {
        return None;
    }
    if mode != 1 && mode != 2 {
        return None;
    }

    if mode == 2 {
        let columns: i32 = 48;
        let rows: i32 = 26;
        let col = ((uv.0 * columns as f32) as i32).clamp(0, columns - 1);
        let row = (((1.0 - uv.1) * rows as f32) as i32).clamp(0, rows - 1);
        let index = row * columns + col;
        let total = columns * rows;
        let position = (index as f32) / (total as f32);
        let front = progress.max(0.001);
        if position > front {
            return None;
        }
        let within_filled = position / front;
        let mut running = 0.0_f32;
        for (i, &frac) in category_fractions.iter().enumerate() {
            running += frac.max(0.0);
            if within_filled < running {
                return Some(DiskCategory::ALL[i]);
            }
        }
        return Some(DiskCategory::ALL[6]);
    }

    // Mode 1: Ocean Metaballs
    let aspect = aspect.max(0.01);
    let p_x = (uv.0 - 0.5) * aspect + 0.5;
    let p_y = uv.1;
    let mut total_field = 0.0_f32;
    let mut best_cat: Option<DiskCategory> = None;
    let mut best_contrib = 0.0_f32;

    for (idx, &frac) in category_fractions.iter().enumerate() {
        let angle = (idx as f32) * std::f32::consts::TAU / 7.0;
        let b_x = 0.5 + 0.24 * angle.cos();
        let b_y = 0.5 + 0.24 * angle.sin();
        let b_rad = (0.06 + frac.max(0.0).sqrt() * 0.22).clamp(0.04, 0.35);
        let b_seed = (idx as f32) * 1.73;

        let cx = (b_x - 0.5) * aspect + 0.5 + (time * 0.7 + b_seed).sin() * 0.006;
        let cy = b_y + (time * 0.5 + b_seed * 1.3).cos() * 0.006;
        let dx = p_x - cx;
        let dy = p_y - cy;
        let d2 = (dx * dx + dy * dy).max(1e-6);
        let contrib = (b_rad * b_rad) / d2;
        total_field += contrib;
        if contrib > best_contrib {
            best_contrib = contrib;
            best_cat = Some(DiskCategory::ALL[idx]);
        }
    }

    if total_field >= 0.75 { best_cat } else { None }
}

/// Streams progressive `DiskScanUpdate` batches every 50 ms while scanning `root`,
/// accounting for permission/IO omissions at all depths.
pub fn scan_directory_streaming(
    root: &Path,
    max_depth: usize,
    volume_used_bytes: u64,
    volume_total_bytes: u64,
    tx: std::sync::mpsc::Sender<DiskScanUpdate>,
    cancel: &std::sync::atomic::AtomicBool,
) -> DiskScanReport {
    use std::sync::atomic::Ordering;
    use std::time::{Duration, Instant};

    let fda_granted = probe_full_disk_access();
    let mut omissions = DiskScanOmissions {
        full_disk_access_granted: fda_granted,
        ..Default::default()
    };

    let mut top_paths = Vec::new();
    match std::fs::read_dir(root) {
        Ok(rd) => {
            for entry_res in rd {
                match entry_res {
                    Ok(e) => top_paths.push(e.path()),
                    Err(err) => omissions.record_io_error(root, &err),
                }
            }
        }
        Err(err) => omissions.record_io_error(root, &err),
    }

    let mut total_bytes_by_cat = [0u64; 7];
    let mut total_count_by_cat = [0u64; 7];
    let mut root_children = Vec::with_capacity(top_paths.len());
    let mut all_files = Vec::new();
    let mut all_dirs = Vec::new();
    let mut folder_count = 0u64;
    let mut last_emit = Instant::now();

    for path in top_paths {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        let mut bytes_by_cat = [0u64; 7];
        let mut count_by_cat = [0u64; 7];
        let mut files = Vec::new();
        let mut dirs = Vec::new();
        let node = scan_subtree_with_omissions(
            &path,
            0,
            max_depth.max(1),
            &mut bytes_by_cat,
            &mut count_by_cat,
            &mut files,
            &mut dirs,
            &mut omissions,
            cancel,
            &mut |added_bytes, added_cat, is_dir, cur_omissions| {
                total_bytes_by_cat[added_cat as usize] =
                    total_bytes_by_cat[added_cat as usize].saturating_add(added_bytes);
                if !is_dir {
                    total_count_by_cat[added_cat as usize] += 1;
                } else {
                    folder_count += 1;
                }
                if last_emit.elapsed() >= Duration::from_millis(50) {
                    let scanned: u64 = total_bytes_by_cat.iter().sum();
                    let fcnt: u64 = total_count_by_cat.iter().sum();
                    let _ = tx.send(DiskScanUpdate {
                        root: root.to_path_buf(),
                        scanned_bytes: scanned,
                        volume_used_bytes,
                        volume_total_bytes,
                        file_count: fcnt,
                        dir_count: folder_count,
                        category_bytes: total_bytes_by_cat,
                        category_counts: total_count_by_cat,
                        omissions: cur_omissions.clone(),
                        report: None,
                        is_complete: false,
                    });
                    last_emit = Instant::now();
                }
            },
        );
        root_children.push(node);
        all_files.extend(files);
        all_dirs.extend(dirs);
    }

    root_children.sort_by_key(|a| std::cmp::Reverse(a.size));
    let total_bytes: u64 = total_bytes_by_cat.iter().sum();
    let total_files: u64 = total_count_by_cat.iter().sum();
    all_files.sort_by_key(|a| std::cmp::Reverse(a.size));
    all_files.truncate(50);
    all_dirs.sort_by_key(|a| std::cmp::Reverse(a.size));
    all_dirs.truncate(50);

    let mut categories = Vec::with_capacity(7);
    for cat in DiskCategory::ALL {
        let b = total_bytes_by_cat[cat as usize];
        let c = total_count_by_cat[cat as usize];
        let share_fraction = if total_bytes > 0 {
            (b as f64 / total_bytes as f64) as f32
        } else {
            0.0
        };
        categories.push(CategoryTotal {
            category: cat,
            bytes: b,
            file_count: c,
            share_fraction,
        });
    }

    let root_name = root
        .file_name()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| root.to_string_lossy().into_owned());
    let root_node = DiskNode {
        name: CompactString::from(root_name),
        full_path: CompactString::from(root.to_string_lossy().as_ref()),
        is_dir: true,
        size: total_bytes,
        file_count: total_files,
        folder_count,
        category: DiskCategory::Other,
        children: root_children,
    };

    let report = DiskScanReport {
        root_path: CompactString::from(root.to_string_lossy().as_ref()),
        total_bytes,
        total_files,
        categories,
        root_node,
        top_directories: all_dirs,
        largest_files: all_files,
    };

    let _ = tx.send(DiskScanUpdate {
        root: root.to_path_buf(),
        scanned_bytes: total_bytes,
        volume_used_bytes,
        volume_total_bytes,
        file_count: total_files,
        dir_count: folder_count,
        category_bytes: total_bytes_by_cat,
        category_counts: total_count_by_cat,
        omissions,
        report: Some(report.clone()),
        is_complete: true,
    });

    report
}

#[allow(clippy::too_many_arguments)]
fn scan_subtree_with_omissions(
    path: &Path,
    depth: usize,
    max_depth: usize,
    bytes_by_cat: &mut [u64; 7],
    count_by_cat: &mut [u64; 7],
    largest_files: &mut Vec<DiskNodeSummary>,
    top_dirs: &mut Vec<DiskNodeSummary>,
    omissions: &mut DiskScanOmissions,
    cancel: &std::sync::atomic::AtomicBool,
    on_item: &mut impl FnMut(u64, DiskCategory, bool, &DiskScanOmissions),
) -> DiskNode {
    use std::sync::atomic::Ordering;

    let name = path
        .file_name()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| path.to_string_lossy().into_owned());
    let full_path = CompactString::from(path.to_string_lossy().as_ref());

    if cancel.load(Ordering::Relaxed) {
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: false,
            size: 0,
            file_count: 0,
            folder_count: 0,
            category: DiskCategory::Other,
            children: Vec::new(),
        };
    }

    let meta = match std::fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(err) => {
            omissions.record_io_error(path, &err);
            return DiskNode {
                name: CompactString::from(name),
                full_path,
                is_dir: false,
                size: 0,
                file_count: 0,
                folder_count: 0,
                category: DiskCategory::Other,
                children: Vec::new(),
            };
        }
    };

    if meta.file_type().is_symlink() {
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: false,
            size: 0,
            file_count: 0,
            folder_count: 0,
            category: DiskCategory::Other,
            children: Vec::new(),
        };
    }

    if meta.is_file() {
        let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
        let cat = classify_disk_extension(ext, false).unwrap_or(DiskCategory::Other);
        let len = meta.len();
        bytes_by_cat[cat as usize] = bytes_by_cat[cat as usize].saturating_add(len);
        count_by_cat[cat as usize] += 1;
        on_item(len, cat, false, omissions);
        if len > 0 {
            largest_files.push(DiskNodeSummary {
                name: CompactString::from(name.as_str()),
                full_path: full_path.clone(),
                size: len,
                file_count: 1,
            });
            if largest_files.len() > 250 {
                largest_files.sort_by_key(|a| std::cmp::Reverse(a.size));
                largest_files.truncate(64);
            }
        }
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: false,
            size: len,
            file_count: 1,
            folder_count: 0,
            category: cat,
            children: Vec::new(),
        };
    }

    let dir_ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
    let dir_cat = classify_disk_extension(dir_ext, true);
    on_item(0, DiskCategory::Other, true, omissions);

    if depth + 1 >= max_depth {
        let mut size = 0u64;
        let mut fcount = 0u64;
        let mut dcount = 0u64;
        for entry_res in WalkDir::new(path).follow_links(false) {
            if cancel.load(Ordering::Relaxed) {
                break;
            }
            match entry_res {
                Ok(item) => {
                    if item.file_type().is_file() {
                        match item.metadata() {
                            Ok(m) => {
                                let ext = item
                                    .path()
                                    .extension()
                                    .and_then(|s| s.to_str())
                                    .unwrap_or("");
                                let cat = classify_disk_extension(ext, false)
                                    .unwrap_or(DiskCategory::Other);
                                let len = m.len();
                                bytes_by_cat[cat as usize] =
                                    bytes_by_cat[cat as usize].saturating_add(len);
                                count_by_cat[cat as usize] += 1;
                                size = size.saturating_add(len);
                                fcount += 1;
                                on_item(len, cat, false, omissions);
                            }
                            Err(err) => {
                                let io_err = err
                                    .io_error()
                                    .map(|e| std::io::Error::new(e.kind(), err.to_string()))
                                    .unwrap_or_else(|| std::io::Error::other(err.to_string()));
                                omissions.record_io_error(item.path(), &io_err);
                            }
                        }
                    } else if item.file_type().is_dir() && item.path() != path {
                        dcount += 1;
                        on_item(0, DiskCategory::Other, true, omissions);
                    }
                }
                Err(err) => {
                    let err_path = err.path().unwrap_or(path);
                    let io_err = err
                        .io_error()
                        .map(|e| std::io::Error::new(e.kind(), err.to_string()))
                        .unwrap_or_else(|| std::io::Error::other(err.to_string()));
                    omissions.record_io_error(err_path, &io_err);
                }
            }
        }
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: true,
            size,
            file_count: fcount,
            folder_count: dcount,
            category: dir_cat.unwrap_or(DiskCategory::Other),
            children: Vec::new(),
        };
    }

    let mut children = Vec::new();
    let mut size = 0u64;
    let mut file_count = 0u64;
    let mut folder_count = 0u64;
    let mut cat_sizes = [0u64; 7];

    match std::fs::read_dir(path) {
        Ok(read_dir) => {
            for child_res in read_dir {
                if cancel.load(Ordering::Relaxed) {
                    break;
                }
                match child_res {
                    Ok(child_entry) => {
                        let child = scan_subtree_with_omissions(
                            &child_entry.path(),
                            depth + 1,
                            max_depth,
                            bytes_by_cat,
                            count_by_cat,
                            largest_files,
                            top_dirs,
                            omissions,
                            cancel,
                            on_item,
                        );
                        size = size.saturating_add(child.size);
                        file_count = file_count.saturating_add(child.file_count);
                        if child.is_dir {
                            folder_count += 1 + child.folder_count;
                        }
                        cat_sizes[child.category as usize] =
                            cat_sizes[child.category as usize].saturating_add(child.size);
                        children.push(child);
                    }
                    Err(err) => omissions.record_io_error(path, &err),
                }
            }
        }
        Err(err) => omissions.record_io_error(path, &err),
    }
    children.sort_by_key(|a| std::cmp::Reverse(a.size));

    let dominant_cat = dir_cat.unwrap_or_else(|| {
        let mut best_idx = DiskCategory::Other as usize;
        let mut best_sz = 0u64;
        for (i, &sz) in cat_sizes.iter().enumerate() {
            if sz > best_sz {
                best_sz = sz;
                best_idx = i;
            }
        }
        DiskCategory::ALL[best_idx]
    });

    if size > 0 {
        top_dirs.push(DiskNodeSummary {
            name: CompactString::from(name.as_str()),
            full_path: full_path.clone(),
            size,
            file_count,
        });
        if top_dirs.len() > 250 {
            top_dirs.sort_by_key(|a| std::cmp::Reverse(a.size));
            top_dirs.truncate(64);
        }
    }

    DiskNode {
        name: CompactString::from(name),
        full_path,
        is_dir: true,
        size,
        file_count,
        folder_count,
        category: dominant_cat,
        children,
    }
}

/// Parallel streaming directory scanner using `walkdir` + `rayon` work-stealing across top-level entries,
/// producing the 7 `DiskCategory` totals, a hierarchical `DiskNode` directory tree (for Squarified Treemap
/// and 2-pass Duplicate Finder), top directories, and largest files.
pub fn scan_directory_parallel(root: &Path, max_depth: usize) -> DiskScanReport {
    let top_entries: Vec<_> = std::fs::read_dir(root)
        .ok()
        .into_iter()
        .flatten()
        .filter_map(Result::ok)
        .collect();

    struct EntryScan {
        node: DiskNode,
        bytes_by_cat: [u64; 7],
        count_by_cat: [u64; 7],
        files: Vec<DiskNodeSummary>,
        dirs: Vec<DiskNodeSummary>,
    }

    let partial_scans: Vec<EntryScan> = top_entries
        .par_iter()
        .map(|entry| {
            let mut bytes_by_cat = [0u64; 7];
            let mut count_by_cat = [0u64; 7];
            let mut files = Vec::new();
            let mut dirs = Vec::new();
            let path = entry.path();
            let node = scan_subtree(
                &path,
                0,
                max_depth.max(1),
                &mut bytes_by_cat,
                &mut count_by_cat,
                &mut files,
                &mut dirs,
            );
            EntryScan {
                node,
                bytes_by_cat,
                count_by_cat,
                files,
                dirs,
            }
        })
        .collect();

    let mut total_bytes_by_cat = [0u64; 7];
    let mut total_count_by_cat = [0u64; 7];
    let mut root_children = Vec::with_capacity(partial_scans.len());
    let mut all_files = Vec::new();
    let mut all_dirs = Vec::new();
    let mut folder_count = 0u64;

    for scan in partial_scans {
        for i in 0..7 {
            total_bytes_by_cat[i] = total_bytes_by_cat[i].saturating_add(scan.bytes_by_cat[i]);
            total_count_by_cat[i] = total_count_by_cat[i].saturating_add(scan.count_by_cat[i]);
        }
        if scan.node.is_dir {
            folder_count += 1 + scan.node.folder_count;
        }
        root_children.push(scan.node);
        all_files.extend(scan.files);
        all_dirs.extend(scan.dirs);
    }

    root_children.sort_by_key(|a| std::cmp::Reverse(a.size));

    let total_bytes: u64 = total_bytes_by_cat.iter().sum();
    let total_files: u64 = total_count_by_cat.iter().sum();

    all_files.sort_by_key(|a| std::cmp::Reverse(a.size));
    all_files.truncate(50);

    all_dirs.sort_by_key(|a| std::cmp::Reverse(a.size));
    all_dirs.truncate(50);

    let mut categories = Vec::with_capacity(7);
    for cat in DiskCategory::ALL {
        let b = total_bytes_by_cat[cat as usize];
        let c = total_count_by_cat[cat as usize];
        let share_fraction = if total_bytes > 0 {
            (b as f64 / total_bytes as f64) as f32
        } else {
            0.0
        };
        categories.push(CategoryTotal {
            category: cat,
            bytes: b,
            file_count: c,
            share_fraction,
        });
    }

    let root_name = root
        .file_name()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| root.to_string_lossy().into_owned());
    let root_node = DiskNode {
        name: CompactString::from(root_name),
        full_path: CompactString::from(root.to_string_lossy().as_ref()),
        is_dir: true,
        size: total_bytes,
        file_count: total_files,
        folder_count,
        category: DiskCategory::Other,
        children: root_children,
    };

    DiskScanReport {
        root_path: CompactString::from(root.to_string_lossy().as_ref()),
        total_bytes,
        total_files,
        categories,
        root_node,
        top_directories: all_dirs,
        largest_files: all_files,
    }
}

fn scan_subtree(
    path: &Path,
    depth: usize,
    max_depth: usize,
    bytes_by_cat: &mut [u64; 7],
    count_by_cat: &mut [u64; 7],
    largest_files: &mut Vec<DiskNodeSummary>,
    top_dirs: &mut Vec<DiskNodeSummary>,
) -> DiskNode {
    let name = path
        .file_name()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| path.to_string_lossy().into_owned());
    let full_path = CompactString::from(path.to_string_lossy().as_ref());

    let Ok(meta) = std::fs::symlink_metadata(path) else {
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: false,
            size: 0,
            file_count: 0,
            folder_count: 0,
            category: DiskCategory::Other,
            children: Vec::new(),
        };
    };

    if meta.file_type().is_symlink() {
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: false,
            size: 0,
            file_count: 0,
            folder_count: 0,
            category: DiskCategory::Other,
            children: Vec::new(),
        };
    }

    if meta.is_file() {
        let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
        let cat = classify_disk_extension(ext, false).unwrap_or(DiskCategory::Other);
        let len = meta.len();
        bytes_by_cat[cat as usize] = bytes_by_cat[cat as usize].saturating_add(len);
        count_by_cat[cat as usize] += 1;
        if len > 0 {
            largest_files.push(DiskNodeSummary {
                name: CompactString::from(name.as_str()),
                full_path: full_path.clone(),
                size: len,
                file_count: 1,
            });
            if largest_files.len() > 250 {
                largest_files.sort_by_key(|a| std::cmp::Reverse(a.size));
                largest_files.truncate(64);
            }
        }
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: false,
            size: len,
            file_count: 1,
            folder_count: 0,
            category: cat,
            children: Vec::new(),
        };
    }

    let dir_ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
    let dir_cat = classify_disk_extension(dir_ext, true);

    if depth + 1 >= max_depth {
        // Aggregate remaining subtree quickly via WalkDir without retaining deep AST nodes
        let mut size = 0u64;
        let mut fcount = 0u64;
        let mut dcount = 0u64;
        for item in WalkDir::new(path)
            .follow_links(false)
            .into_iter()
            .filter_map(Result::ok)
        {
            if item.file_type().is_file()
                && let Ok(m) = item.metadata()
            {
                let ext = item
                    .path()
                    .extension()
                    .and_then(|s| s.to_str())
                    .unwrap_or("");
                let cat = classify_disk_extension(ext, false).unwrap_or(DiskCategory::Other);
                let len = m.len();
                bytes_by_cat[cat as usize] = bytes_by_cat[cat as usize].saturating_add(len);
                count_by_cat[cat as usize] += 1;
                size = size.saturating_add(len);
                fcount += 1;
            } else if item.file_type().is_dir() && item.path() != path {
                dcount += 1;
            }
        }
        return DiskNode {
            name: CompactString::from(name),
            full_path,
            is_dir: true,
            size,
            file_count: fcount,
            folder_count: dcount,
            category: dir_cat.unwrap_or(DiskCategory::Other),
            children: Vec::new(),
        };
    }

    let mut children = Vec::new();
    let mut size = 0u64;
    let mut file_count = 0u64;
    let mut folder_count = 0u64;
    let mut cat_sizes = [0u64; 7];

    if let Ok(read_dir) = std::fs::read_dir(path) {
        for child_entry in read_dir.flatten() {
            let child = scan_subtree(
                &child_entry.path(),
                depth + 1,
                max_depth,
                bytes_by_cat,
                count_by_cat,
                largest_files,
                top_dirs,
            );
            size = size.saturating_add(child.size);
            file_count = file_count.saturating_add(child.file_count);
            if child.is_dir {
                folder_count += 1 + child.folder_count;
            }
            cat_sizes[child.category as usize] =
                cat_sizes[child.category as usize].saturating_add(child.size);
            children.push(child);
        }
    }
    children.sort_by_key(|a| std::cmp::Reverse(a.size));

    let dominant_cat = dir_cat.unwrap_or_else(|| {
        let mut best_idx = DiskCategory::Other as usize;
        let mut best_sz = 0u64;
        for (i, &sz) in cat_sizes.iter().enumerate() {
            if sz > best_sz {
                best_sz = sz;
                best_idx = i;
            }
        }
        DiskCategory::ALL[best_idx]
    });

    if size > 0 {
        top_dirs.push(DiskNodeSummary {
            name: CompactString::from(name.as_str()),
            full_path: full_path.clone(),
            size,
            file_count,
        });
        if top_dirs.len() > 250 {
            top_dirs.sort_by_key(|a| std::cmp::Reverse(a.size));
            top_dirs.truncate(64);
        }
    }

    DiskNode {
        name: CompactString::from(name),
        full_path,
        is_dir: true,
        size,
        file_count,
        folder_count,
        category: dominant_cat,
        children,
    }
}

#[derive(Debug, Error)]
pub enum SnapshotDbError {
    #[error("sqlite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    #[error("unsupported future schema version {0} (max supported is 1)")]
    UnsupportedFutureSchema(u32),
    #[error("serialization error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("snapshot id {0} not found")]
    NotFound(i64),
}

/// SQLite-backed `SnapshotDatabase` for storing and retrieving `SystemSnapshot` baselines and pinned exited processes.
/// Preserves `UnavailableReason` NULLs and enforces forward-compatible `schema_version` checks.
pub struct SnapshotDatabase {
    conn: Connection,
}

impl SnapshotDatabase {
    pub const CURRENT_SCHEMA_VERSION: u32 = 1;

    pub fn open_in_memory() -> Result<Self, SnapshotDbError> {
        let conn = Connection::open_in_memory()?;
        let db = Self { conn };
        db.init_schema()?;
        Ok(db)
    }

    pub fn open_path(path: &Path) -> Result<Self, SnapshotDbError> {
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let conn = Connection::open(path)?;
        let db = Self { conn };
        db.init_schema()?;
        Ok(db)
    }

    fn init_schema(&self) -> Result<(), SnapshotDbError> {
        self.conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                label TEXT NOT NULL,
                generation INTEGER NOT NULL,
                sampled_at_ns INTEGER NOT NULL,
                cpu_percent REAL,
                mem_used_bytes REAL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pinned_exited_processes (
                pid INTEGER NOT NULL,
                creation_token_us INTEGER NOT NULL,
                name TEXT NOT NULL,
                exited_at_ns INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY (pid, creation_token_us)
            );",
        )?;

        let existing_ver: Option<u32> = self
            .conn
            .query_row(
                "SELECT value FROM meta WHERE key = 'schema_version'",
                [],
                |row| row.get(0),
            )
            .ok();

        match existing_ver {
            None => {
                self.conn.execute(
                    "INSERT INTO meta (key, value) VALUES ('schema_version', ?1)",
                    params![Self::CURRENT_SCHEMA_VERSION],
                )?;
            }
            Some(v) if v > Self::CURRENT_SCHEMA_VERSION => {
                return Err(SnapshotDbError::UnsupportedFutureSchema(v));
            }
            Some(_) => {}
        }
        Ok(())
    }

    /// Saves a `SystemSnapshot` baseline, storing `NULL` in SQLite columns whenever a metric is `Unavailable`.
    pub fn save_snapshot(
        &self,
        label: &str,
        snapshot: &SystemSnapshot,
    ) -> Result<i64, SnapshotDbError> {
        let cpu_opt = match snapshot.system.cpu_percent.reading {
            MetricReading::Measured { value, .. } => Some(value),
            MetricReading::Unavailable(_) => None,
        };
        let mem_opt = match snapshot.system.mem_used_bytes.reading {
            MetricReading::Measured { value, .. } => Some(value),
            MetricReading::Unavailable(_) => None,
        };
        let json = serde_json::to_string(snapshot)?;
        self.conn.execute(
            "INSERT INTO snapshots (label, generation, sampled_at_ns, cpu_percent, mem_used_bytes, payload_json)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![
                label,
                snapshot.generation as i64,
                snapshot.sampled_at_ns as i64,
                cpu_opt,
                mem_opt,
                json
            ],
        )?;
        Ok(self.conn.last_insert_rowid())
    }

    pub fn load_snapshot(&self, id: i64) -> Result<(String, SystemSnapshot), SnapshotDbError> {
        let mut stmt = self
            .conn
            .prepare("SELECT label, payload_json FROM snapshots WHERE id = ?1")?;
        let mut rows = stmt.query(params![id])?;
        if let Some(row) = rows.next()? {
            let label: String = row.get(0)?;
            let json: String = row.get(1)?;
            let snap: SystemSnapshot = serde_json::from_str(&json)?;
            Ok((label, snap))
        } else {
            Err(SnapshotDbError::NotFound(id))
        }
    }

    /// Pins a recently exited process into the permanent SQLite baseline store.
    pub fn pin_exited_process(&self, proc_snap: &ProcessSnapshot) -> Result<(), SnapshotDbError> {
        let json = serde_json::to_string(proc_snap)?;
        self.conn.execute(
            "INSERT OR REPLACE INTO pinned_exited_processes
             (pid, creation_token_us, name, exited_at_ns, payload_json)
             VALUES (?1, ?2, ?3, ?4, ?5)",
            params![
                proc_snap.key.pid,
                proc_snap.key.creation_token_us as i64,
                proc_snap.name.as_str(),
                proc_snap.exited_at_ns.unwrap_or(0) as i64,
                json
            ],
        )?;
        Ok(())
    }

    pub fn list_pinned_exited_processes(&self) -> Result<Vec<ProcessSnapshot>, SnapshotDbError> {
        let mut stmt = self.conn.prepare(
            "SELECT payload_json FROM pinned_exited_processes ORDER BY exited_at_ns DESC",
        )?;
        let rows = stmt.query_map([], |row| row.get::<_, String>(0))?;
        let mut out = Vec::new();
        for r in rows {
            let json = r?;
            out.push(serde_json::from_str(&json)?);
        }
        Ok(out)
    }

    /// Computes a `SnapshotDiff` including any persisted `pinned_exited_processes`.
    pub fn diff_with_pinned(
        &self,
        baseline: &SystemSnapshot,
        target: &SystemSnapshot,
    ) -> Result<SnapshotDiff, SnapshotDbError> {
        let pinned = self.list_pinned_exited_processes()?;
        Ok(SnapshotDiffer::diff_with_pinned(baseline, target, &pinned))
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProcessDiffEntry {
    pub key: ProcessKey,
    pub name: CompactString,
    pub footprint_delta_bytes: Option<f64>,
    pub cpu_delta_percent: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SnapshotDiff {
    pub baseline_generation: u64,
    pub target_generation: u64,
    pub cpu_percent_delta: Option<f64>,
    pub mem_used_bytes_delta: Option<f64>,
    pub spawned_processes: Vec<ProcessKey>,
    pub terminated_processes: Vec<ProcessKey>,
    pub exited_retained_processes: Vec<ProcessSnapshot>,
    pub pinned_exited_processes: Vec<ProcessSnapshot>,
    pub modified_processes: Vec<ProcessDiffEntry>,
}

/// `SnapshotDiffer` comparing two `SystemSnapshot` instances by composite `ProcessKey` (`pid` + `creation_token_us`)
/// while retaining `exited_processes` and `pinned_exited_processes`.
pub struct SnapshotDiffer;

impl SnapshotDiffer {
    pub fn diff(baseline: &SystemSnapshot, target: &SystemSnapshot) -> SnapshotDiff {
        Self::diff_with_pinned(baseline, target, &[])
    }

    pub fn diff_with_pinned(
        baseline: &SystemSnapshot,
        target: &SystemSnapshot,
        pinned: &[ProcessSnapshot],
    ) -> SnapshotDiff {
        let cpu_percent_delta =
            metric_delta(&baseline.system.cpu_percent, &target.system.cpu_percent);
        let mem_used_bytes_delta = metric_delta(
            &baseline.system.mem_used_bytes,
            &target.system.mem_used_bytes,
        );

        let base_map: HashMap<ProcessKey, &ProcessSnapshot> =
            baseline.processes.iter().map(|p| (p.key, p)).collect();
        let targ_map: HashMap<ProcessKey, &ProcessSnapshot> =
            target.processes.iter().map(|p| (p.key, p)).collect();

        let mut spawned_processes = Vec::new();
        let mut terminated_processes = Vec::new();
        let mut modified_processes = Vec::new();

        for (&k, &tp) in &targ_map {
            if let Some(&bp) = base_map.get(&k) {
                let fp_delta = metric_delta(&bp.phys_footprint_bytes, &tp.phys_footprint_bytes);
                let cpu_delta = metric_delta(&bp.cpu_percent, &tp.cpu_percent);
                if fp_delta.unwrap_or(0.0).abs() > 0.0 || cpu_delta.unwrap_or(0.0).abs() > 0.01 {
                    modified_processes.push(ProcessDiffEntry {
                        key: k,
                        name: tp.name.clone(),
                        footprint_delta_bytes: fp_delta,
                        cpu_delta_percent: cpu_delta,
                    });
                }
            } else {
                spawned_processes.push(k);
            }
        }

        let mut terminated_set = HashSet::new();
        for &k in base_map.keys() {
            if !targ_map.contains_key(&k) {
                terminated_processes.push(k);
                terminated_set.insert(k);
            }
        }
        for ex in &target.exited_processes {
            if !targ_map.contains_key(&ex.key) && terminated_set.insert(ex.key) {
                terminated_processes.push(ex.key);
            }
        }

        let mut pinned_exited_processes = Vec::new();
        for p in pinned {
            if !targ_map.contains_key(&p.key) {
                pinned_exited_processes.push(p.clone());
            }
        }

        SnapshotDiff {
            baseline_generation: baseline.generation,
            target_generation: target.generation,
            cpu_percent_delta,
            mem_used_bytes_delta,
            spawned_processes,
            terminated_processes,
            exited_retained_processes: target.exited_processes.clone(),
            pinned_exited_processes,
            modified_processes,
        }
    }
}

fn metric_delta(a: &Metric<f64>, b: &Metric<f64>) -> Option<f64> {
    match (a.reading, b.reading) {
        (MetricReading::Measured { value: va, .. }, MetricReading::Measured { value: vb, .. }) => {
            Some(vb - va)
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::{MetricProvenance, MetricQuality, SourceId, UnavailableReason, UnitTag};

    #[test]
    fn disk_category_classifier_matches_extension_rules() {
        assert_eq!(
            classify_disk_extension("app", true),
            Some(DiskCategory::Applications)
        );
        assert_eq!(classify_disk_extension("folder", true), None);
        assert_eq!(
            classify_disk_extension("mp4", false),
            Some(DiskCategory::Media)
        );
        assert_eq!(
            classify_disk_extension("pdf", false),
            Some(DiskCategory::Documents)
        );
        assert_eq!(
            classify_disk_extension("rs", false),
            Some(DiskCategory::Code)
        );
        assert_eq!(
            classify_disk_extension("dmg", false),
            Some(DiskCategory::Archives)
        );
        assert_eq!(
            classify_disk_extension("dylib", false),
            Some(DiskCategory::SystemAndTemp)
        );
        assert_eq!(
            classify_disk_extension("", false),
            Some(DiskCategory::Other)
        );
    }

    #[test]
    fn snapshot_database_preserves_nulls_and_diffs_by_process_key_and_pinned() {
        let db = SnapshotDatabase::open_in_memory().expect("in-memory sqlite");
        let mut s1 = SystemSnapshot::new_empty(1, 1_000_000_000);
        let mut s2 = SystemSnapshot::new_empty(2, 2_000_000_000);
        let prov = MetricProvenance {
            source: SourceId(1),
            unit_tag: Some(UnitTag::BYTES),
            sampled_at_ns: 1_000_000_000,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };

        let k1 = ProcessKey {
            pid: 100,
            creation_token_us: 5000,
            uid: Some(501),
        };
        let k2 = ProcessKey {
            pid: 200,
            creation_token_us: 6000,
            uid: Some(501),
        };
        s1.processes.push(ProcessSnapshot {
            key: k1,
            ppid: 1,
            name: CompactString::const_new("proc_a"),
            cpu_percent: Metric::unavailable(UnavailableReason::NotYetAvailable, prov),
            phys_footprint_bytes: Metric::measured(1000.0, prov),
            resident_bytes: Metric::measured(1000.0, prov),
            disk_read_bytes: Metric::measured(0.0, prov),
            disk_write_bytes: Metric::measured(0.0, prov),
            exited_at_ns: None,
        });

        s2.processes.push(ProcessSnapshot {
            key: k1,
            ppid: 1,
            name: CompactString::const_new("proc_a"),
            cpu_percent: Metric::measured(15.0, prov),
            phys_footprint_bytes: Metric::measured(2500.0, prov),
            resident_bytes: Metric::measured(2500.0, prov),
            disk_read_bytes: Metric::measured(0.0, prov),
            disk_write_bytes: Metric::measured(0.0, prov),
            exited_at_ns: None,
        });
        let pinned_proc = ProcessSnapshot {
            key: k2,
            ppid: 1,
            name: CompactString::const_new("proc_b"),
            cpu_percent: Metric::measured(5.0, prov),
            phys_footprint_bytes: Metric::measured(500.0, prov),
            resident_bytes: Metric::measured(500.0, prov),
            disk_read_bytes: Metric::measured(0.0, prov),
            disk_write_bytes: Metric::measured(0.0, prov),
            exited_at_ns: Some(2_000_000_000),
        };

        let id1 = db.save_snapshot("base", &s1).unwrap();
        let (_, loaded1) = db.load_snapshot(id1).unwrap();
        assert_eq!(loaded1.generation, 1);

        db.pin_exited_process(&pinned_proc).unwrap();
        let diff = db.diff_with_pinned(&s1, &s2).unwrap();
        assert_eq!(diff.pinned_exited_processes.len(), 1);
        assert_eq!(diff.pinned_exited_processes[0].key, k2);
        assert_eq!(diff.modified_processes.len(), 1);
        assert_eq!(
            diff.modified_processes[0].footprint_delta_bytes,
            Some(1500.0)
        );
        // Because s1.cpu_percent was NotYetAvailable, cpu_delta_percent is honestly None
        assert_eq!(diff.modified_processes[0].cpu_delta_percent, None);
    }

    #[test]
    fn treemap_duplicates_disk_snapshot_store_and_metrics_history_work_end_to_end() {
        let tmp_root = std::env::temp_dir().join(format!("nexus_disk_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&tmp_root);
        let sub_dir = tmp_root.join("assets");
        std::fs::create_dir_all(&sub_dir).unwrap();
        std::fs::write(tmp_root.join("dup1.bin"), vec![42u8; 4096]).unwrap();
        std::fs::write(sub_dir.join("dup2.bin"), vec![42u8; 4096]).unwrap();
        std::fs::write(sub_dir.join("unique.rs"), b"fn main() {}\n").unwrap();

        let report1 = scan_directory_parallel(&tmp_root, 4);
        assert_eq!(report1.total_files, 3);
        assert_eq!(report1.total_bytes, 8192 + 13);

        // Squarified treemap
        let rects = compute_squarified_treemap(
            &report1.root_node.children,
            RectBounds {
                x: 0.0,
                y: 0.0,
                width: 800.0,
                height: 600.0,
            },
        );
        assert!(!rects.is_empty());

        // 2-pass SHA-256 duplicate finder
        let dups = find_duplicates(&report1.root_node);
        assert_eq!(dups.len(), 1);
        assert_eq!(dups[0].file_size, 4096);
        assert_eq!(dups[0].wasted_bytes, 4096);
        assert_eq!(dups[0].paths.len(), 2);

        // DiskSnapshotStore save, list, diff, delete
        let mut store = DiskSnapshotStore::open_in_memory().unwrap();
        let snap1_id = store.save_disk_snapshot(&report1, Some("before")).unwrap();
        std::fs::write(tmp_root.join("added.dat"), vec![7u8; 2048]).unwrap();
        let report2 = scan_directory_parallel(&tmp_root, 4);
        let snap2_id = store.save_disk_snapshot(&report2, Some("after")).unwrap();

        let list = store.list_disk_snapshots(None).unwrap();
        assert_eq!(list.len(), 2);
        let diff = store.diff_disk_snapshots(snap1_id, snap2_id).unwrap();
        assert_eq!(diff.total_delta_bytes, 2048);
        assert_eq!(diff.total_delta_files, 1);
        assert!(store.delete_disk_snapshot(snap1_id).unwrap());
        assert_eq!(store.list_disk_snapshots(None).unwrap().len(), 1);

        // MetricsHistoryStore preserves NULL for Unavailable metrics
        let hist = MetricsHistoryStore::open_in_memory().unwrap();
        let s_empty = SystemSnapshot::new_empty(1, 1_000_000_000);
        hist.insert_snapshot(&s_empty, 1_700_000_000_000).unwrap();
        let rows = hist.query_recent(10).unwrap();
        assert_eq!(rows.len(), 1);
        assert!(rows[0].cpu_percent.is_none());
        assert!(rows[0].gpu_percent.is_none());

        let _ = std::fs::remove_dir_all(&tmp_root);
    }

    #[test]
    fn streaming_scanner_accounts_for_omissions_and_hit_testing_matches_wgsl() {
        use std::sync::atomic::AtomicBool;

        let tmp_root =
            std::env::temp_dir().join(format!("nexus_stream_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&tmp_root);
        std::fs::create_dir_all(&tmp_root).unwrap();
        std::fs::write(tmp_root.join("sample.rs"), b"fn ok() {}").unwrap();

        #[cfg(unix)]
        let locked = {
            use std::os::unix::fs::PermissionsExt;
            let locked = tmp_root.join("locked_dir");
            std::fs::create_dir_all(&locked).unwrap();
            std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o000)).unwrap();
            locked
        };

        let (tx, rx) = std::sync::mpsc::channel();
        let cancel = AtomicBool::new(false);
        let report = scan_directory_streaming(&tmp_root, 3, 100, 1000, tx, &cancel);
        assert_eq!(report.total_files, 1);
        let updates: Vec<_> = rx.try_iter().collect();
        let last = updates.last().expect("final streaming update");
        assert!(last.is_complete);

        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            // On non-root user runs, reading 0000 dir increments permission_denied_count.
            if unsafe_free_is_non_root() {
                assert!(last.omissions.permission_denied_count >= 1);
                assert!(
                    last.omissions
                        .sample_omitted_paths
                        .iter()
                        .any(|p| p == &locked)
                );
            }
            // Restore permissions so cleanup succeeds.
            let _ = std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o755));
        }

        let _ = std::fs::remove_dir_all(&tmp_root);

        // Hit-testing Mode 2 (48x26 Defrag Blocks)
        let fracs = [0.5, 0.25, 0.25, 0.0, 0.0, 0.0, 0.0];
        assert_eq!(
            entry_at_normalized((0.02, 0.99), 2.0, 2, 0.0, &fracs, 1.0),
            Some(DiskCategory::Media)
        );
        assert_eq!(
            entry_at_normalized((0.95, 0.01), 2.0, 2, 0.0, &fracs, 0.5),
            None
        );

        // Hit-testing Mode 1 (Ocean Metaball #0 center at angle 0 -> (0.74, 0.5))
        assert_eq!(
            entry_at_normalized((0.74, 0.5), 1.0, 1, 0.0, &fracs, 1.0),
            Some(DiskCategory::Media)
        );
        // Mode 3 (Vector Tunnel) disables category hit-testing
        assert_eq!(
            entry_at_normalized((0.5, 0.5), 1.0, 3, 0.0, &fracs, 1.0),
            None
        );
    }

    #[cfg(unix)]
    fn unsafe_free_is_non_root() -> bool {
        // Check if HOME exists and is not /var/root
        std::env::var("USER").map(|u| u != "root").unwrap_or(true)
    }
}
