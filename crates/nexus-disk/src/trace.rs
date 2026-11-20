use nexus_core::{LogicalProcessorClass, Metric, MetricReading, SystemSnapshot};
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Instant, SystemTime, UNIX_EPOCH};
use thiserror::Error;

/// Strict 32 MiB size ceiling for `.nexustrace` files (`0x2000000` bytes).
pub const MAX_TRACE_BYTES: usize = 33_554_432;

/// Canonical format identifier stored inside `.nexustrace` headers.
pub const NEXUS_TRACE_FORMAT: &str = "com.joshsutcliff.nexusmonitor.trace";

static TMP_COUNTER: AtomicU64 = AtomicU64::new(1);

#[derive(Debug, Error)]
pub enum TraceError {
    #[error("The recording is too large to save safely ({size} > {MAX_TRACE_BYTES} bytes).")]
    TooLarge { size: usize },
    #[error("The trace is not valid JSON: {0}")]
    InvalidJson(String),
    #[error("Failed to decode trace JSON: {0}")]
    Serde(#[from] serde_json::Error),
    #[error("Trace I/O error: {0}")]
    Io(#[from] std::io::Error),
}

/// 17-key `.nexustrace` root document schema.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusTrace {
    pub version: u32,
    pub format: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub recorded_at: Option<String>,
    pub machine_name: String,
    pub e_core_count: usize,
    pub p_core_count: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub gpu_core_count: Option<usize>,
    pub e_core_labels: Vec<String>,
    pub p_core_labels: Vec<String>,
    pub npu_labels: Vec<String>,
    pub media_labels: Vec<String>,
    pub power_labels: Vec<String>,
    pub thermal_labels: Vec<String>,
    pub os_version: String,
    pub is_apple_silicon: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub memory_bytes: Option<f64>,
    pub samples: Vec<NexusTraceSample>,
}

/// 11-key `.nexustrace` per-tick sample schema.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct NexusTraceSample {
    #[serde(rename = "t")]
    pub elapsed_seconds: f64,
    #[serde(rename = "eCores")]
    pub e_core_loads: Vec<Option<f64>>,
    #[serde(rename = "pCores")]
    pub p_core_loads: Vec<Option<f64>>,
    #[serde(rename = "gpu", skip_serializing_if = "Option::is_none")]
    pub gpu_load_percent: Option<f64>,
    #[serde(rename = "npu")]
    pub npu_loads: Vec<Option<f64>>,
    #[serde(rename = "npuWatts", skip_serializing_if = "Option::is_none")]
    pub npu_power_watts: Option<f64>,
    #[serde(rename = "media")]
    pub media_loads: Vec<Option<f64>>,
    #[serde(rename = "power")]
    pub power_watts: Vec<Option<f64>>,
    #[serde(rename = "pkgWatts", skip_serializing_if = "Option::is_none")]
    pub package_watts: Option<f64>,
    #[serde(rename = "maxTempC", skip_serializing_if = "Option::is_none")]
    pub max_thermal_celsius: Option<f64>,
    #[serde(rename = "thermals")]
    pub thermal_sensors: Vec<Option<f64>>,
}

fn metric_opt(m: &Metric<f64>) -> Option<f64> {
    match m.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
        _ => None,
    }
}

impl NexusTrace {
    /// Initializes a `.nexustrace` header from a live `SystemSnapshot`.
    pub fn from_snapshot(
        snap: &SystemSnapshot,
        machine_name: impl Into<String>,
        os_version: impl Into<String>,
        is_apple_silicon: bool,
        recorded_at: Option<String>,
    ) -> Self {
        let mut e_core_labels = Vec::new();
        let mut p_core_labels = Vec::new();
        for core in &snap.system.cores {
            match core.class {
                LogicalProcessorClass::Efficiency => {
                    e_core_labels.push(format!("E{}", e_core_labels.len()));
                }
                LogicalProcessorClass::Performance | LogicalProcessorClass::Unknown => {
                    p_core_labels.push(format!("P{}", p_core_labels.len()));
                }
            }
        }
        let e_core_count = if e_core_labels.is_empty() {
            snap.system.efficiency_core_count as usize
        } else {
            e_core_labels.len()
        };
        let p_core_count = if p_core_labels.is_empty() {
            snap.system.performance_core_count as usize
        } else {
            p_core_labels.len()
        };

        let gpu_core_count = metric_opt(&snap.system.gpu_core_count).map(|v| v.max(0.0) as usize);
        let memory_bytes = metric_opt(&snap.system.mem_total_bytes);

        let media_labels = snap
            .hardware_telemetry
            .soc_power
            .media_blocks
            .iter()
            .map(|b| b.label.to_string())
            .collect();

        let thermal_labels = snap
            .hardware_telemetry
            .thermals
            .sensors
            .iter()
            .map(|s| s.name.to_string())
            .collect();

        Self {
            version: 1,
            format: NEXUS_TRACE_FORMAT.to_string(),
            recorded_at,
            machine_name: machine_name.into(),
            e_core_count,
            p_core_count,
            gpu_core_count,
            e_core_labels,
            p_core_labels,
            npu_labels: vec!["ANE0".to_string()],
            media_labels,
            power_labels: vec![
                "CPU".to_string(),
                "GPU".to_string(),
                "ANE".to_string(),
                "DRAM".to_string(),
                "Package".to_string(),
            ],
            thermal_labels,
            os_version: os_version.into(),
            is_apple_silicon,
            memory_bytes,
            samples: Vec::new(),
        }
    }
}

impl NexusTraceSample {
    /// Captures one honest sample tick from a `SystemSnapshot`.
    /// Unavailable per-element array slots become `None` (`null` in JSON);
    /// unavailable scalar metrics omit their JSON keys completely.
    pub fn from_snapshot(snap: &SystemSnapshot, elapsed_seconds: f64) -> Self {
        let mut e_core_loads = Vec::new();
        let mut p_core_loads = Vec::new();
        for core in &snap.system.cores {
            let val = metric_opt(&core.total_percent);
            match core.class {
                LogicalProcessorClass::Efficiency => e_core_loads.push(val),
                LogicalProcessorClass::Performance | LogicalProcessorClass::Unknown => {
                    p_core_loads.push(val);
                }
            }
        }

        let sp = &snap.hardware_telemetry.soc_power;
        let th = &snap.hardware_telemetry.thermals;

        Self {
            elapsed_seconds: if elapsed_seconds.is_finite() {
                elapsed_seconds.max(0.0)
            } else {
                0.0
            },
            e_core_loads,
            p_core_loads,
            gpu_load_percent: metric_opt(&snap.system.gpu_utilization_percent),
            npu_loads: vec![metric_opt(&sp.ane_active_residency_percent)],
            npu_power_watts: metric_opt(&sp.ane_watts),
            media_loads: sp
                .media_blocks
                .iter()
                .map(|b| metric_opt(&b.active_residency_percent))
                .collect(),
            power_watts: vec![
                metric_opt(&sp.cpu_watts),
                metric_opt(&sp.gpu_watts),
                metric_opt(&sp.ane_watts),
                metric_opt(&sp.dram_watts),
                metric_opt(&sp.package_watts),
            ],
            package_watts: metric_opt(&sp.package_watts),
            max_thermal_celsius: metric_opt(&th.max_die_celsius),
            thermal_sensors: th.sensors.iter().map(|s| metric_opt(&s.celsius)).collect(),
        }
    }
}

/// Converts a UNIX timestamp in seconds to `(year, month, day, hour, minute, second)` in UTC.
fn utc_components(unix_secs: u64) -> (i64, u32, u32, u32, u32, u32) {
    let secs_of_day = (unix_secs % 86_400) as u32;
    let days = (unix_secs / 86_400) as i64;

    // Howard Hinnant's civil-from-days algorithm (epoch 1970-01-01).
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = y + if m <= 2 { 1 } else { 0 };

    let hour = secs_of_day / 3600;
    let minute = (secs_of_day % 3600) / 60;
    let second = secs_of_day % 60;
    (year, m as u32, d as u32, hour, minute, second)
}

/// Formats `SystemTime` as pure-Rust UTC ISO-8601 (`YYYY-MM-DDTHH:MM:SSZ`).
pub fn format_utc_iso8601(time: SystemTime) -> String {
    let unix_secs = time
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let (y, m, d, hh, mm, ss) = utc_components(unix_secs);
    format!("{y:04}-{m:02}-{d:02}T{hh:02}:{mm:02}:{ss:02}Z")
}

/// Formats `SystemTime` as pure-Rust UTC filename stamp (`YYYYMMDD_HHMM`).
pub fn format_utc_filename_stamp(time: SystemTime) -> String {
    let unix_secs = time
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let (y, m, d, hh, mm, _) = utc_components(unix_secs);
    format!("{y:04}{m:02}{d:02}_{hh:02}{mm:02}")
}

/// Incremental byte-size accountant enforcing the 32 MiB ceiling before serialization.
#[derive(Debug, Clone)]
pub struct NexusTraceEncodedSize {
    current_bytes: usize,
    sample_count: usize,
}

impl NexusTraceEncodedSize {
    /// Initializes exact encoded byte length from a `NexusTrace` header (with `samples` empty or populated).
    pub fn from_trace(trace: &NexusTrace) -> Result<Self, TraceError> {
        let encoded = serde_json::to_vec(trace)?;
        Ok(Self {
            current_bytes: encoded.len(),
            sample_count: trace.samples.len(),
        })
    }

    pub fn current_bytes(&self) -> usize {
        self.current_bytes
    }

    pub fn sample_count(&self) -> usize {
        self.sample_count
    }

    /// Computes exact byte delta if `sample` were appended to the trace JSON array.
    pub fn delta_for_sample(&self, sample: &NexusTraceSample) -> Result<usize, TraceError> {
        let sample_bytes = serde_json::to_vec(sample)?.len();
        let comma = usize::from(self.sample_count > 0);
        Ok(sample_bytes + comma)
    }

    /// Returns true if appending `sample` keeps total encoded JSON size `<= MAX_TRACE_BYTES`.
    pub fn can_append(&self, sample: &NexusTraceSample) -> Result<bool, TraceError> {
        let delta = self.delta_for_sample(sample)?;
        Ok(self.current_bytes.saturating_add(delta) <= MAX_TRACE_BYTES)
    }

    /// Accounts for an appended sample, rejecting if `MAX_TRACE_BYTES` would be exceeded.
    pub fn try_add_sample(&mut self, sample: &NexusTraceSample) -> Result<usize, TraceError> {
        let delta = self.delta_for_sample(sample)?;
        let next = self.current_bytes.saturating_add(delta);
        if next > MAX_TRACE_BYTES {
            return Err(TraceError::TooLarge { size: next });
        }
        self.current_bytes = next;
        self.sample_count += 1;
        Ok(self.current_bytes)
    }
}

/// Streaming JSON preflight validator (`NexusTraceJsonPreflight`).
/// Verifies size ceiling, UTF-8 validity, string escapes, bracket balance,
/// nesting depth (`<= 16`), and required top-level keys before `serde_json` decoding.
pub struct NexusTraceJsonPreflight;

impl NexusTraceJsonPreflight {
    pub const MAX_NESTING_DEPTH: usize = 16;

    pub fn validate(bytes: &[u8]) -> Result<(), TraceError> {
        if bytes.len() > MAX_TRACE_BYTES {
            return Err(TraceError::TooLarge { size: bytes.len() });
        }
        let text = std::str::from_utf8(bytes)
            .map_err(|_| TraceError::InvalidJson("input is not valid UTF-8".to_string()))?;

        let raw = text.as_bytes();
        let mut stack: Vec<u8> = Vec::with_capacity(Self::MAX_NESTING_DEPTH);
        let mut in_string = false;
        let mut escape = false;
        let mut started = false;
        let mut finished = false;
        let mut idx = 0;

        while idx < raw.len() {
            let b = raw[idx];
            if in_string {
                if escape {
                    escape = false;
                } else if b == b'\\' {
                    escape = true;
                } else if b == b'"' {
                    in_string = false;
                } else if b < 0x20 {
                    return Err(TraceError::InvalidJson(
                        "unescaped control character inside string".to_string(),
                    ));
                }
                idx += 1;
                continue;
            }

            if b.is_ascii_whitespace() {
                idx += 1;
                continue;
            }

            if finished {
                return Err(TraceError::InvalidJson(
                    "trailing characters after root JSON object".to_string(),
                ));
            }

            if !started {
                if b != b'{' {
                    return Err(TraceError::InvalidJson(
                        "root JSON value must be an object".to_string(),
                    ));
                }
                started = true;
                stack.push(b'}');
                idx += 1;
                continue;
            }

            match b {
                b'"' => {
                    in_string = true;
                }
                b'{' => {
                    if stack.len() >= Self::MAX_NESTING_DEPTH {
                        return Err(TraceError::InvalidJson(
                            "maximum JSON nesting depth exceeded".to_string(),
                        ));
                    }
                    stack.push(b'}');
                }
                b'[' => {
                    if stack.len() >= Self::MAX_NESTING_DEPTH {
                        return Err(TraceError::InvalidJson(
                            "maximum JSON nesting depth exceeded".to_string(),
                        ));
                    }
                    stack.push(b']');
                }
                b'}' | b']' => {
                    let expected = stack.pop();
                    if expected != Some(b) {
                        return Err(TraceError::InvalidJson(
                            "mismatched JSON closing delimiter".to_string(),
                        ));
                    }
                    if stack.is_empty() {
                        finished = true;
                    }
                }
                _ => {}
            }
            idx += 1;
        }

        if in_string || !started || !finished || !stack.is_empty() {
            return Err(TraceError::InvalidJson(
                "unterminated JSON structure".to_string(),
            ));
        }

        for required_key in ["\"version\"", "\"format\"", "\"samples\""] {
            if !text.contains(required_key) {
                return Err(TraceError::InvalidJson(format!(
                    "missing required header key {required_key}"
                )));
            }
        }

        Ok(())
    }
}

/// Returns the default directory for saved `.nexustrace` files:
/// `~/Library/Application Support/NexusSystemMonitor/Traces`.
pub fn default_traces_dir() -> PathBuf {
    crate::default_app_support_dir().join("Traces")
}

/// Computes the next collision-free trace file path in `dir` for `time`:
/// `NEXUS_TRACE_<YYYYMMDD_HHMM>.nexustrace`, appending `-2`, `-3`, ... on collision.
pub fn next_trace_file_path(dir: &Path, time: SystemTime) -> PathBuf {
    let stamp = format_utc_filename_stamp(time);
    let base = format!("NEXUS_TRACE_{stamp}");
    let first = dir.join(format!("{base}.nexustrace"));
    if !first.exists() {
        return first;
    }
    let mut suffix = 2u32;
    loop {
        let candidate = dir.join(format!("{base}-{suffix}.nexustrace"));
        if !candidate.exists() {
            return candidate;
        }
        suffix += 1;
    }
}

/// Writes `trace` atomically without ever overwriting an existing destination file.
/// Enforces the strict `<= MAX_TRACE_BYTES` (32 MiB) limit.
pub fn write_trace_atomic_no_overwrite(
    trace: &NexusTrace,
    path: &Path,
) -> Result<usize, TraceError> {
    let encoded = serde_json::to_vec(trace)?;
    if encoded.len() > MAX_TRACE_BYTES {
        return Err(TraceError::TooLarge {
            size: encoded.len(),
        });
    }

    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }

    let tmp_id = TMP_COUNTER.fetch_add(1, Ordering::Relaxed);
    let file_name = path
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("trace.nexustrace");
    let tmp_path = path.with_file_name(format!(".{file_name}.tmp.{}.{tmp_id}", std::process::id()));

    {
        let mut f = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&tmp_path)?;
        if let Err(e) = f.write_all(&encoded).and_then(|()| f.sync_all()) {
            let _ = fs::remove_file(&tmp_path);
            return Err(TraceError::Io(e));
        }
    }

    let link_res = fs::hard_link(&tmp_path, path);
    let _ = fs::remove_file(&tmp_path);
    link_res?;
    Ok(encoded.len())
}

/// Saves `trace` into `dir` with automatic `NEXUS_TRACE_<YYYYMMDD_HHMM>(-N).nexustrace` collision handling.
pub fn save_trace_unique(
    trace: &NexusTrace,
    dir: &Path,
    time: SystemTime,
) -> Result<(PathBuf, usize), TraceError> {
    fs::create_dir_all(dir)?;
    let stamp = format_utc_filename_stamp(time);
    let base = format!("NEXUS_TRACE_{stamp}");
    let mut attempt = 1u32;
    loop {
        let candidate = if attempt == 1 {
            dir.join(format!("{base}.nexustrace"))
        } else {
            dir.join(format!("{base}-{attempt}.nexustrace"))
        };
        match write_trace_atomic_no_overwrite(trace, &candidate) {
            Ok(bytes) => return Ok((candidate, bytes)),
            Err(TraceError::Io(err)) if err.kind() == std::io::ErrorKind::AlreadyExists => {
                attempt += 1;
            }
            Err(err) => return Err(err),
        }
    }
}

/// Reads, preflight-validates, and decodes a `.nexustrace` file from disk.
pub fn read_trace_file(path: &Path) -> Result<NexusTrace, TraceError> {
    let file = File::open(path)?;
    let mut buf = Vec::new();
    let mut limited = file.take((MAX_TRACE_BYTES + 1) as u64);
    limited.read_to_end(&mut buf)?;
    if buf.len() > MAX_TRACE_BYTES {
        return Err(TraceError::TooLarge { size: buf.len() });
    }
    NexusTraceJsonPreflight::validate(&buf)?;
    let trace: NexusTrace = serde_json::from_slice(&buf)?;
    Ok(trace)
}

/// Stateful `.nexustrace` recorder enforcing the 32 MiB size budget.
#[derive(Debug, Clone)]
pub struct NexusTraceRecorder {
    trace: Option<NexusTrace>,
    size_accountant: Option<NexusTraceEncodedSize>,
    started_at: Option<Instant>,
    pub last_saved_path: Option<PathBuf>,
    pub last_status: String,
}

impl Default for NexusTraceRecorder {
    fn default() -> Self {
        Self::new()
    }
}

impl NexusTraceRecorder {
    pub fn new() -> Self {
        Self {
            trace: None,
            size_accountant: None,
            started_at: None,
            last_saved_path: None,
            last_status: String::from("Idle"),
        }
    }

    pub fn is_recording(&self) -> bool {
        self.trace.is_some()
    }

    pub fn sample_count(&self) -> usize {
        self.trace.as_ref().map(|t| t.samples.len()).unwrap_or(0)
    }

    pub fn encoded_bytes(&self) -> usize {
        self.size_accountant
            .as_ref()
            .map(NexusTraceEncodedSize::current_bytes)
            .unwrap_or(0)
    }

    pub fn start(
        &mut self,
        snap: &SystemSnapshot,
        machine_name: impl Into<String>,
        os_version: impl Into<String>,
        is_apple_silicon: bool,
    ) -> Result<(), TraceError> {
        let now = SystemTime::now();
        let mut trace = NexusTrace::from_snapshot(
            snap,
            machine_name,
            os_version,
            is_apple_silicon,
            Some(format_utc_iso8601(now)),
        );
        let first_sample = NexusTraceSample::from_snapshot(snap, 0.0);
        trace.samples.push(first_sample);
        let accountant = NexusTraceEncodedSize::from_trace(&trace)?;
        if accountant.current_bytes() > MAX_TRACE_BYTES {
            return Err(TraceError::TooLarge {
                size: accountant.current_bytes(),
            });
        }
        self.trace = Some(trace);
        self.size_accountant = Some(accountant);
        self.started_at = Some(Instant::now());
        self.last_status = String::from("Recording .nexustrace...");
        Ok(())
    }

    /// Appends a snapshot tick if active; returns `false` if the 32 MiB ceiling was reached.
    pub fn record_snapshot(&mut self, snap: &SystemSnapshot) -> bool {
        let (Some(trace), Some(accountant), Some(started)) =
            (&mut self.trace, &mut self.size_accountant, self.started_at)
        else {
            return false;
        };
        let sample = NexusTraceSample::from_snapshot(snap, started.elapsed().as_secs_f64());
        match accountant.try_add_sample(&sample) {
            Ok(_) => {
                trace.samples.push(sample);
                true
            }
            Err(_) => {
                self.last_status = String::from("Trace paused: 32 MiB limit reached");
                false
            }
        }
    }

    /// Stops recording and atomically writes the `.nexustrace` file without overwriting existing files.
    pub fn stop_and_save(&mut self, dir: &Path) -> Result<Option<PathBuf>, TraceError> {
        let Some(trace) = self.trace.take() else {
            return Ok(None);
        };
        self.size_accountant = None;
        self.started_at = None;
        let (path, bytes) = save_trace_unique(&trace, dir, SystemTime::now())?;
        self.last_saved_path = Some(path.clone());
        self.last_status = format!(
            "Saved {} samples ({} bytes) to {}",
            trace.samples.len(),
            bytes,
            path.display()
        );
        Ok(Some(path))
    }

    /// Discards the active trace without writing to disk.
    pub fn discard(&mut self) {
        self.trace = None;
        self.size_accountant = None;
        self.started_at = None;
        self.last_status = String::from("Recording discarded");
    }
}

/// Standalone `.nexustrace` replay timeline (`load`, `seek`, `play`, `pause`, `restart`).
#[derive(Debug, Clone)]
pub struct NexusTracePlayer {
    pub trace: NexusTrace,
    pub source_path: Option<PathBuf>,
    pub current_index: usize,
    pub playing: bool,
    playback_offset_secs: f64,
    playback_anchor: Option<Instant>,
}

impl NexusTracePlayer {
    pub fn load(path: &Path) -> Result<Self, TraceError> {
        let trace = read_trace_file(path)?;
        Ok(Self {
            trace,
            source_path: Some(path.to_path_buf()),
            current_index: 0,
            playing: false,
            playback_offset_secs: 0.0,
            playback_anchor: None,
        })
    }

    pub fn from_trace(trace: NexusTrace) -> Self {
        Self {
            trace,
            source_path: None,
            current_index: 0,
            playing: false,
            playback_offset_secs: 0.0,
            playback_anchor: None,
        }
    }

    pub fn duration_seconds(&self) -> f64 {
        self.trace
            .samples
            .last()
            .map(|s| s.elapsed_seconds.max(0.0))
            .unwrap_or(0.0)
    }

    pub fn playback_elapsed(&self) -> f64 {
        if self.playing
            && let Some(anchor) = self.playback_anchor
        {
            (self.playback_offset_secs + anchor.elapsed().as_secs_f64())
                .min(self.duration_seconds())
        } else {
            self.playback_offset_secs.min(self.duration_seconds())
        }
    }

    /// Binary-searches `samples` by `elapsed_seconds` (`t`) and updates the playback cursor.
    pub fn seek(&mut self, target_seconds: f64) -> Option<usize> {
        if self.trace.samples.is_empty() {
            self.current_index = 0;
            self.playback_offset_secs = 0.0;
            return None;
        }
        let clamped = target_seconds.clamp(0.0, self.duration_seconds());
        let pos = self
            .trace
            .samples
            .partition_point(|s| s.elapsed_seconds < clamped);
        let idx = pos.min(self.trace.samples.len().saturating_sub(1));
        self.current_index = idx;
        self.playback_offset_secs = clamped;
        if self.playing {
            self.playback_anchor = Some(Instant::now());
        }
        Some(idx)
    }

    pub fn play(&mut self) {
        if self.trace.samples.is_empty() {
            return;
        }
        if self.playback_offset_secs >= self.duration_seconds() {
            self.playback_offset_secs = 0.0;
            self.current_index = 0;
        }
        self.playing = true;
        self.playback_anchor = Some(Instant::now());
    }

    pub fn pause(&mut self) {
        if self.playing {
            self.playback_offset_secs = self.playback_elapsed();
            self.playing = false;
            self.playback_anchor = None;
        }
    }

    pub fn restart(&mut self) {
        self.current_index = 0;
        self.playback_offset_secs = 0.0;
        self.playing = !self.trace.samples.is_empty();
        self.playback_anchor = if self.playing {
            Some(Instant::now())
        } else {
            None
        };
    }

    /// Advances playback against monotonic wall-clock time and returns the active sample.
    pub fn tick(&mut self) -> Option<&NexusTraceSample> {
        if self.playing
            && let Some(anchor) = self.playback_anchor
        {
            let target = self.playback_offset_secs + anchor.elapsed().as_secs_f64();
            let dur = self.duration_seconds();
            if target >= dur {
                self.current_index = self.trace.samples.len().saturating_sub(1);
                self.playback_offset_secs = dur;
                self.playing = false;
                self.playback_anchor = None;
            } else {
                let pos = self
                    .trace
                    .samples
                    .partition_point(|s| s.elapsed_seconds <= target);
                self.current_index = pos.saturating_sub(1);
            }
        }
        self.current_sample()
    }

    pub fn current_sample(&self) -> Option<&NexusTraceSample> {
        self.trace.samples.get(self.current_index)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::{
        CoreTelemetry, MetricProvenance, MetricQuality, SourceId, UnavailableReason, UnitTag,
    };

    fn test_prov() -> MetricProvenance {
        MetricProvenance {
            source: SourceId(0),
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns: 1_000,
            window_ns: 100,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        }
    }

    #[test]
    fn trace_schema_honesty_serializes_null_slots_and_omits_missing_scalars() {
        let mut snap = SystemSnapshot::new_empty(1, 1_000);
        snap.system.cores = vec![
            CoreTelemetry {
                core_index: 0,
                class: LogicalProcessorClass::Efficiency,
                total_percent: Metric::measured(14.5, test_prov()),
                user_percent: Metric::measured(10.0, test_prov()),
                system_percent: Metric::measured(4.5, test_prov()),
                idle_percent: Metric::measured(85.5, test_prov()),
            },
            CoreTelemetry {
                core_index: 1,
                class: LogicalProcessorClass::Efficiency,
                total_percent: Metric::unavailable(UnavailableReason::TransientError, test_prov()),
                user_percent: Metric::unavailable(UnavailableReason::TransientError, test_prov()),
                system_percent: Metric::unavailable(UnavailableReason::TransientError, test_prov()),
                idle_percent: Metric::unavailable(UnavailableReason::TransientError, test_prov()),
            },
        ];

        let mut trace = NexusTrace::from_snapshot(
            &snap,
            "Apple M4 Pro",
            "macOS 15.6",
            true,
            Some("2026-11-19T22:15:00Z".to_string()),
        );
        let sample = NexusTraceSample::from_snapshot(&snap, 0.25);
        trace.samples.push(sample);

        let json = serde_json::to_string(&trace).expect("serialize");
        let v: serde_json::Value = serde_json::from_str(&json).expect("parse value");
        let obj = v.as_object().expect("root object");
        assert_eq!(obj["format"], NEXUS_TRACE_FORMAT);
        assert_eq!(obj["version"], 1);
        assert!(obj.get("gpuCoreCount").is_none());

        let s0 = v["samples"][0].as_object().expect("sample object");
        assert_eq!(s0["eCores"][0], 14.5);
        assert!(s0["eCores"][1].is_null());
        assert!(s0.get("gpu").is_none());
        assert!(s0.get("npuWatts").is_none());
        assert!(s0.get("pkgWatts").is_none());
        assert!(s0.get("maxTempC").is_none());
    }

    #[test]
    fn encoded_size_accountant_matches_exact_bytes_and_enforces_32_mib_cap() {
        let snap = SystemSnapshot::new_empty(1, 1_000);
        let mut trace = NexusTrace::from_snapshot(
            &snap,
            "Mac",
            "macOS",
            true,
            Some("2026-11-19T22:15:00Z".to_string()),
        );
        let mut acct = NexusTraceEncodedSize::from_trace(&trace).expect("acct");
        for i in 0..5 {
            let s = NexusTraceSample::from_snapshot(&snap, i as f64 * 0.5);
            acct.try_add_sample(&s).expect("add sample");
            trace.samples.push(s);
            let exact = serde_json::to_vec(&trace).expect("vec").len();
            assert_eq!(acct.current_bytes(), exact);
        }

        // Oversized payload > 32 MiB must fail write_trace_atomic_no_overwrite.
        let mut huge_trace = trace.clone();
        huge_trace.samples.push(NexusTraceSample {
            elapsed_seconds: 1.0,
            e_core_loads: vec![Some(1.0); 8_500_000],
            p_core_loads: Vec::new(),
            gpu_load_percent: None,
            npu_loads: Vec::new(),
            npu_power_watts: None,
            media_loads: Vec::new(),
            power_watts: Vec::new(),
            package_watts: None,
            max_thermal_celsius: None,
            thermal_sensors: Vec::new(),
        });
        let tmp_dir = std::env::temp_dir().join(format!("nexus_trace_big_{}", std::process::id()));
        let err = write_trace_atomic_no_overwrite(&huge_trace, &tmp_dir.join("big.nexustrace"))
            .unwrap_err();
        assert!(matches!(err, TraceError::TooLarge { .. }));
        let _ = fs::remove_dir_all(&tmp_dir);
    }

    #[test]
    fn preflight_validates_and_rejects_malformed_or_deep_json() {
        let snap = SystemSnapshot::new_empty(1, 1_000);
        let trace = NexusTrace::from_snapshot(&snap, "Mac", "macOS", true, None);
        let bytes = serde_json::to_vec(&trace).expect("serialize");
        NexusTraceJsonPreflight::validate(&bytes).expect("valid trace passes");

        assert!(NexusTraceJsonPreflight::validate(b"[]").is_err());
        assert!(NexusTraceJsonPreflight::validate(br#"{"version":1}"#).is_err());
        assert!(
            NexusTraceJsonPreflight::validate(br#"{"version":1,"format":"x","samples":[]"#)
                .is_err()
        );
        let too_deep = format!("{}{}", "[".repeat(20), "]".repeat(20));
        let wrapped = format!(r#"{{"version":1,"format":"x","samples":{too_deep}}}"#);
        assert!(NexusTraceJsonPreflight::validate(wrapped.as_bytes()).is_err());
    }

    #[test]
    fn atomic_save_never_overwrites_and_player_seeks_binary() {
        let tmp_dir = std::env::temp_dir().join(format!(
            "nexus_trace_save_{}_{}",
            std::process::id(),
            TMP_COUNTER.fetch_add(1, Ordering::Relaxed)
        ));
        let snap = SystemSnapshot::new_empty(1, 1_000);
        let mut trace = NexusTrace::from_snapshot(&snap, "Mac", "macOS", true, None);
        for i in 0..10 {
            trace
                .samples
                .push(NexusTraceSample::from_snapshot(&snap, i as f64 * 0.5));
        }

        let t = UNIX_EPOCH + std::time::Duration::from_secs(1_795_000_000);
        let (p1, _) = save_trace_unique(&trace, &tmp_dir, t).expect("save 1");
        let (p2, _) = save_trace_unique(&trace, &tmp_dir, t).expect("save 2");
        assert_ne!(p1, p2);
        assert!(p2.to_string_lossy().ends_with("-2.nexustrace"));

        let mut player = NexusTracePlayer::load(&p1).expect("load");
        assert_eq!(player.seek(1.8), Some(4)); // 2.0s is first >= 1.8s
        assert_eq!(player.current_sample().unwrap().elapsed_seconds, 2.0);
        player.restart();
        assert_eq!(player.current_index, 0);
        assert!(player.playing);
        player.pause();
        assert!(!player.playing);

        let _ = fs::remove_dir_all(&tmp_dir);
    }

    #[test]
    fn corrupt_and_oversize_trace_files_return_clean_errors_without_panic() {
        let bad_path =
            std::env::temp_dir().join(format!("nexus_bad_trace_{}.nexustrace", std::process::id()));
        fs::write(&bad_path, b"{\"format\":\"bad\",").expect("write malformed trace");
        assert!(NexusTracePlayer::load(&bad_path).is_err());
        let _ = fs::remove_file(&bad_path);

        let missing = std::env::temp_dir().join("nexus_nonexistent_trace_file_12345.nexustrace");
        assert!(NexusTracePlayer::load(&missing).is_err());

        let deep = vec![b'['; 128];
        assert!(NexusTraceJsonPreflight::validate(&deep).is_err());
    }
}
