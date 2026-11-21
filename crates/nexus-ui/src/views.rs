use compact_str::CompactString;
use nexus_core::{
    HostPlatform, MetricReading, ProcessKey, ProcessSnapshot, SystemSnapshot, constants,
};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

/// Complete 16-case `Destination` navigation enum.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Destination {
    Summary = 0,
    Performance = 1,
    Processes = 2,
    SystemInfo = 3,
    Startup = 4,
    Users = 5,
    Services = 6,
    PowerAndFreq = 7,
    AppleSilicon = 8,
    FlightRecorder = 9,
    Connections = 10,
    InstalledApps = 11,
    Drivers = 12,
    DiskSpace = 13,
    Benchmarks = 14,
    Settings = 15,
}

impl Destination {
    pub const ALL: [Self; 16] = [
        Self::Summary,
        Self::Performance,
        Self::Processes,
        Self::SystemInfo,
        Self::Startup,
        Self::Users,
        Self::Services,
        Self::PowerAndFreq,
        Self::AppleSilicon,
        Self::FlightRecorder,
        Self::Connections,
        Self::InstalledApps,
        Self::Drivers,
        Self::DiskSpace,
        Self::Benchmarks,
        Self::Settings,
    ];

    /// Matches `Destination.slug` (`0x1000299ac`).
    #[inline]
    pub const fn slug(self) -> &'static str {
        match self {
            Self::Summary => "summary",
            Self::Performance => "performance",
            Self::Processes => "processes",
            Self::SystemInfo => "system-info",
            Self::Startup => "startup",
            Self::Users => "users",
            Self::Services => "services",
            Self::PowerAndFreq => "power-and-freq",
            Self::AppleSilicon => "apple-silicon",
            Self::FlightRecorder => "flight-recorder",
            Self::Connections => "connections",
            Self::InstalledApps => "installed-apps",
            Self::Drivers => "drivers",
            Self::DiskSpace => "disk-space",
            Self::Benchmarks => "benchmarks",
            Self::Settings => "settings",
        }
    }

    #[inline]
    pub const fn title(self) -> &'static str {
        self.title_for(HostPlatform::current())
    }

    #[inline]
    pub const fn title_for(self, platform: HostPlatform) -> &'static str {
        match self {
            Self::Summary => "Summary",
            Self::Performance => "Performance",
            Self::Processes => "Processes",
            Self::SystemInfo => "System Info",
            Self::Startup => "Startup Apps",
            Self::Users => "Users",
            Self::Services => "Services",
            Self::PowerAndFreq => "Power & Freq",
            Self::AppleSilicon => match platform {
                HostPlatform::MacOs => "Apple Silicon",
                HostPlatform::Windows | HostPlatform::Linux => "SoC & Thermals",
            },
            Self::FlightRecorder => "Flight Recorder",
            Self::Connections => "Connections",
            Self::InstalledApps => "Installed Apps",
            Self::Drivers => "Drivers",
            Self::DiskSpace => "Disk Space",
            Self::Benchmarks => "Benchmarks",
            Self::Settings => "Settings",
        }
    }
}

/// Heat ramp RGBA mapping (`v` in `[0.0, 1.0]`):
/// - Clamps `v` to `[0.0, 1.0]` (NaN -> `0.0`)
/// - Alpha `A = 0.28 * v + 0.10` (`[0.10, 0.38]`)
/// - For `v < 0.75`: green-yellow ramp; for `v >= 0.75`: orange-red ramp.
pub fn heat_ramp_rgba(value: f64) -> [f32; 4] {
    let v = if value.is_finite() {
        value.clamp(0.0, 1.0)
    } else {
        0.0
    };
    let alpha = (v * 0.28 + 0.10) as f32;
    if v >= 0.75 {
        let g_raw = ((v - 0.75) * -660.0 + 210.0).max(45.0);
        let g_byte = (g_raw as i32) & 0xff;
        [1.0, (g_byte as f32) / 255.0, 20.0 / 255.0, alpha]
    } else {
        let r_byte = ((v * 255.0) / 0.75) as u32;
        [(r_byte as f32) / 255.0, 210.0 / 255.0, 30.0 / 255.0, alpha]
    }
}

/// Computes rank-weighted process excitation intensity.
#[inline]
pub fn process_update_excitation(delta: f64, highlight_enabled: bool, rank: usize) -> f64 {
    if !highlight_enabled || !delta.is_finite() || delta <= 0.0 {
        return 0.0;
    }
    let clamped_delta = delta.min(1.0);
    let rank_weight = ((rank as f64) * -0.1 + 1.0).max(0.1);
    clamped_delta * rank_weight
}

/// Top-10 process rank excitation pulse state (`RankGlowLayerView` at `0x1001788fc`).
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct RankGlowEntry {
    pub excitation: f64,
    pub triggered_at_ns: u64,
}

impl RankGlowEntry {
    /// Computes the current RGBA tint (`#57FF73`, peak alpha `0.46`, `1.30s` linear fade-out).
    pub fn current_rgba(&self, now_ns: u64) -> Option<[f32; 4]> {
        let age_sec = (now_ns.saturating_sub(self.triggered_at_ns) as f64) / 1_000_000_000.0;
        if age_sec >= constants::RANK_GLOW_DURATION_S {
            return None;
        }
        let fade = 1.0 - (age_sec / constants::RANK_GLOW_DURATION_S);
        let alpha =
            (f64::from(constants::RANK_GLOW_PEAK_ALPHA) * self.excitation * fade).clamp(0.0, 1.0);
        if alpha <= 1e-4 {
            return None;
        }
        Some([
            constants::RANK_GLOW_TINT_SRGB[0],
            constants::RANK_GLOW_TINT_SRGB[1],
            constants::RANK_GLOW_TINT_SRGB[2],
            alpha as f32,
        ])
    }
}

/// Exited process crimson wash state (`ExitedProcessWashLayerView` at `0x100179960`, `ExitedRowWash` at `0x10017a4b4`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExitedRowWashStyle {
    pub wash_rgba: [f32; 4],
    pub text_alpha_multiplier: f32,
    pub status_text: CompactString,
}

/// Computes the 5.0-second linear crimson wash (`#FF575F`, peak alpha `0.30`, text dim `0.55`, status `"Exited"`).
pub fn compute_exited_row_wash(exited_at_ns: u64, now_ns: u64) -> Option<ExitedRowWashStyle> {
    let age_sec = (now_ns.saturating_sub(exited_at_ns) as f64) / 1_000_000_000.0;
    if age_sec >= constants::EXITED_RETENTION_S {
        return None;
    }
    let remaining = 1.0 - (age_sec / constants::EXITED_RETENTION_S);
    let alpha = (f64::from(constants::EXITED_PEAK_ALPHA) * remaining).clamp(0.0, 1.0) as f32;
    Some(ExitedRowWashStyle {
        wash_rgba: [
            constants::EXITED_WASH_SRGB[0],
            constants::EXITED_WASH_SRGB[1],
            constants::EXITED_WASH_SRGB[2],
            alpha,
        ],
        text_alpha_multiplier: constants::EXITED_TEXT_DIM,
        status_text: CompactString::const_new("Exited"),
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProcessGroupingMode {
    Flat,
    ParentChildHierarchy,
    CoalitionAppGroup,
}

/// Sortable columns in the `Processes & Applications` table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub enum ProcessSortColumn {
    Name,
    Impact,
    Cpu,
    #[default]
    Memory,
    Group,
    Pid,
    Token,
}

impl ProcessSortColumn {
    pub fn from_column_id(id: &str) -> Option<Self> {
        match id {
            "name" => Some(Self::Name),
            "impact" => Some(Self::Impact),
            "cpu" => Some(Self::Cpu),
            "mem" => Some(Self::Memory),
            "group" => Some(Self::Group),
            "pid" => Some(Self::Pid),
            "token" => Some(Self::Token),
            _ => None,
        }
    }

    pub const fn default_descending(self) -> bool {
        match self {
            Self::Cpu | Self::Memory | Self::Impact | Self::Pid | Self::Token => true,
            Self::Name | Self::Group => false,
        }
    }
}

fn default_sort_descending() -> bool {
    true
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProcessInspectorDossier {
    pub key: ProcessKey,
    pub ppid: i32,
    pub name: CompactString,
    pub group_key: CompactString,
    pub cpu_formatted: String,
    pub phys_footprint_formatted: String,
    pub resident_formatted: String,
    pub status_label: CompactString,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProcessTableRowView {
    pub snapshot: ProcessSnapshot,
    pub depth: u32,
    pub group_name: CompactString,
    pub cpu_heat_rgba: [f32; 4],
    pub mem_heat_rgba: [f32; 4],
    pub rank_glow_rgba: Option<[f32; 4]>,
    pub exited_wash: Option<ExitedRowWashStyle>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct ProcessTableCacheKey {
    generation: u64,
    sampled_at_ns: u64,
    process_count: usize,
    exited_count: usize,
    sort_column: ProcessSortColumn,
    sort_descending: bool,
    grouping_mode: Option<ProcessGroupingMode>,
    search_query: String,
    show_exited: bool,
    highlight_updates: bool,
}

/// Interactive state machine for `ProcessesView` (`0x1003706a0`), supporting flat, hierarchical (`ppid`),
/// and coalition/app-bundle grouping, live search filtering, `ProcessInspectorView` (`0x100377f00`),
/// automatic dead-PID pruning in `collapsed_process_ids`, `RankGlowLayerView`, and `ExitedRowWash`.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ProcessTableState {
    pub grouping_mode: Option<ProcessGroupingMode>,
    pub search_query: String,
    #[serde(default)]
    pub sort_column: ProcessSortColumn,
    #[serde(default = "default_sort_descending")]
    pub sort_descending: bool,
    pub selected_key: Option<ProcessKey>,
    pub inspector_open: bool,
    pub highlight_updates: bool,
    pub show_exited: bool,
    pub collapsed_process_ids: HashSet<String>,
    pub rank_glows: HashMap<ProcessKey, RankGlowEntry>,
    pub prev_cpu_by_key: HashMap<ProcessKey, f64>,
    #[serde(skip)]
    cached_key: Option<ProcessTableCacheKey>,
    #[serde(skip)]
    cached_rows: Vec<ProcessTableRowView>,
}

impl ProcessTableState {
    pub fn new() -> Self {
        Self {
            grouping_mode: Some(ProcessGroupingMode::Flat),
            search_query: String::new(),
            sort_column: ProcessSortColumn::Memory,
            sort_descending: true,
            selected_key: None,
            inspector_open: false,
            highlight_updates: true,
            show_exited: true,
            collapsed_process_ids: HashSet::new(),
            rank_glows: HashMap::new(),
            prev_cpu_by_key: HashMap::new(),
            cached_key: None,
            cached_rows: Vec::new(),
        }
    }

    /// Toggles sort order when clicking an active column, or selects a new column with its natural sort direction.
    pub fn toggle_sort(&mut self, column: ProcessSortColumn) {
        if self.sort_column == column {
            self.sort_descending = !self.sort_descending;
        } else {
            self.sort_column = column;
            self.sort_descending = column.default_descending();
        }
        self.cached_key = None;
    }

    /// Prunes dead `ProcessKey` strings from `self.collapsed_process_ids`.
    pub fn prune_dead_collapsed_ids(&mut self, live_processes: &[ProcessSnapshot]) {
        let live_ids: HashSet<String> = live_processes
            .iter()
            .map(|p| p.key.to_collapsed_id_string())
            .collect();
        self.collapsed_process_ids
            .retain(|id| live_ids.contains(id));
    }

    /// Updates the table from a `SystemSnapshot` and produces styled `ProcessTableRowView` rows.
    pub fn build_rows(&mut self, snap: &SystemSnapshot) -> Vec<ProcessTableRowView> {
        let cache_key = ProcessTableCacheKey {
            generation: snap.generation,
            sampled_at_ns: snap.sampled_at_ns,
            process_count: snap.processes.len(),
            exited_count: snap.exited_processes.len(),
            sort_column: self.sort_column,
            sort_descending: self.sort_descending,
            grouping_mode: self.grouping_mode,
            search_query: self.search_query.clone(),
            show_exited: self.show_exited,
            highlight_updates: self.highlight_updates,
        };
        if self.cached_key.as_ref() == Some(&cache_key) {
            return self.cached_rows.clone();
        }

        self.prune_dead_collapsed_ids(&snap.processes);

        let total_mem = match snap.system.mem_total_bytes.reading {
            MetricReading::Measured { value, .. } if value > 0.0 => value,
            _ => 16.0 * 1024.0 * 1024.0 * 1024.0,
        };

        let query_lower = self.search_query.trim().to_ascii_lowercase();
        let mut filtered: Vec<ProcessSnapshot> = snap
            .processes
            .iter()
            .filter(|p| {
                query_lower.is_empty()
                    || p.name.to_ascii_lowercase().contains(&query_lower)
                    || p.key.pid.to_string().contains(&query_lower)
            })
            .cloned()
            .collect();

        let sort_col = self.sort_column;
        let desc = self.sort_descending;
        let metric_f64 = |m: &nexus_core::Metric<f64>| match m.reading {
            MetricReading::Measured { value, .. } if value.is_finite() => value,
            _ => -1.0,
        };
        let impact_score = |p: &ProcessSnapshot| -> (u8, f64) {
            let cpu = metric_f64(&p.cpu_percent).max(0.0);
            let mem_mib = (metric_f64(&p.phys_footprint_bytes).max(0.0)) / (1024.0 * 1024.0);
            let tier = if cpu >= 35.0 {
                3
            } else if mem_mib >= 1536.0 {
                2
            } else if cpu >= 10.0 || mem_mib >= 512.0 {
                1
            } else {
                0
            };
            (tier, cpu * 16.0 + mem_mib * 0.05)
        };

        filtered.sort_by(|a, b| {
            let ord = match sort_col {
                ProcessSortColumn::Memory => metric_f64(&a.phys_footprint_bytes)
                    .partial_cmp(&metric_f64(&b.phys_footprint_bytes))
                    .unwrap_or(std::cmp::Ordering::Equal),
                ProcessSortColumn::Cpu => metric_f64(&a.cpu_percent)
                    .partial_cmp(&metric_f64(&b.cpu_percent))
                    .unwrap_or(std::cmp::Ordering::Equal)
                    .then_with(|| {
                        metric_f64(&a.phys_footprint_bytes)
                            .partial_cmp(&metric_f64(&b.phys_footprint_bytes))
                            .unwrap_or(std::cmp::Ordering::Equal)
                    }),
                ProcessSortColumn::Impact => {
                    let (ta, sa) = impact_score(a);
                    let (tb, sb) = impact_score(b);
                    ta.cmp(&tb)
                        .then_with(|| sa.partial_cmp(&sb).unwrap_or(std::cmp::Ordering::Equal))
                }
                ProcessSortColumn::Name => a
                    .name
                    .as_str()
                    .to_ascii_lowercase()
                    .cmp(&b.name.as_str().to_ascii_lowercase())
                    .then_with(|| a.name.cmp(&b.name)),
                ProcessSortColumn::Group => {
                    let ga = derive_coalition_group_name(&a.name);
                    let gb = derive_coalition_group_name(&b.name);
                    ga.as_str()
                        .to_ascii_lowercase()
                        .cmp(&gb.as_str().to_ascii_lowercase())
                        .then_with(|| {
                            metric_f64(&a.phys_footprint_bytes)
                                .partial_cmp(&metric_f64(&b.phys_footprint_bytes))
                                .unwrap_or(std::cmp::Ordering::Equal)
                        })
                }
                ProcessSortColumn::Pid => a.key.pid.cmp(&b.key.pid),
                ProcessSortColumn::Token => a.key.creation_token_us.cmp(&b.key.creation_token_us),
            };
            let directed = if desc { ord.reverse() } else { ord };
            directed.then_with(|| a.key.pid.cmp(&b.key.pid))
        });

        // Update top-10 RankGlow excitations (`0x10054525c`)
        for (rank, p) in filtered
            .iter()
            .take(constants::RANK_GLOW_HEAD_COUNT)
            .enumerate()
        {
            let cur_cpu = match p.cpu_percent.reading {
                MetricReading::Measured { value, .. } => value / 100.0,
                _ => 0.0,
            };
            let prev_cpu = self.prev_cpu_by_key.get(&p.key).copied().unwrap_or(0.0);
            let delta = (cur_cpu - prev_cpu).max(0.0);
            let exc = process_update_excitation(delta, self.highlight_updates, rank);
            if exc > 0.02 {
                self.rank_glows.insert(
                    p.key,
                    RankGlowEntry {
                        excitation: exc,
                        triggered_at_ns: snap.sampled_at_ns,
                    },
                );
            }
        }

        self.prev_cpu_by_key.clear();
        for p in &snap.processes {
            if let MetricReading::Measured { value, .. } = p.cpu_percent.reading {
                self.prev_cpu_by_key.insert(p.key, value / 100.0);
            }
        }

        let pid_set: HashSet<i32> = filtered.iter().map(|p| p.key.pid).collect();
        let mut rows = Vec::with_capacity(filtered.len() + snap.exited_processes.len());

        let mode = self.grouping_mode.unwrap_or(ProcessGroupingMode::Flat);
        for p in filtered {
            let depth = match mode {
                ProcessGroupingMode::Flat | ProcessGroupingMode::CoalitionAppGroup => 0,
                ProcessGroupingMode::ParentChildHierarchy => {
                    if p.ppid > 1 && pid_set.contains(&p.ppid) {
                        1
                    } else {
                        0
                    }
                }
            };
            let group_name = derive_coalition_group_name(&p.name);
            let cpu_norm = match p.cpu_percent.reading {
                MetricReading::Measured { value, .. } => (value / 100.0).clamp(0.0, 1.0),
                _ => 0.0,
            };
            let mem_norm = match p.phys_footprint_bytes.reading {
                MetricReading::Measured { value, .. } => (value / total_mem).clamp(0.0, 1.0),
                _ => 0.0,
            };
            let rank_glow_rgba = self
                .rank_glows
                .get(&p.key)
                .and_then(|g| g.current_rgba(snap.sampled_at_ns));

            rows.push(ProcessTableRowView {
                snapshot: p,
                depth,
                group_name,
                cpu_heat_rgba: heat_ramp_rgba(cpu_norm),
                mem_heat_rgba: heat_ramp_rgba(mem_norm),
                rank_glow_rgba,
                exited_wash: None,
            });
        }

        if self.show_exited {
            for ex in &snap.exited_processes {
                if let Some(exited_at) = ex.exited_at_ns
                    && let Some(wash) = compute_exited_row_wash(exited_at, snap.sampled_at_ns)
                {
                    let group_name = derive_coalition_group_name(&ex.name);
                    rows.push(ProcessTableRowView {
                        snapshot: ex.clone(),
                        depth: 0,
                        group_name,
                        cpu_heat_rgba: [0.0; 4],
                        mem_heat_rgba: [0.0; 4],
                        rank_glow_rgba: None,
                        exited_wash: Some(wash),
                    });
                }
            }
        }

        self.cached_key = Some(cache_key);
        self.cached_rows = rows.clone();
        rows
    }

    /// Builds a structured `ProcessInspectorDossier` (`ProcessInspectorView` at `0x100377f00`) for `target`.
    pub fn inspect_process(
        &self,
        snap: &SystemSnapshot,
        target: ProcessKey,
    ) -> Option<ProcessInspectorDossier> {
        let p = snap
            .processes
            .iter()
            .find(|p| p.key == target)
            .or_else(|| snap.exited_processes.iter().find(|p| p.key == target))?;
        let status_label = if p.exited_at_ns.is_some() {
            CompactString::const_new("Exited")
        } else {
            CompactString::const_new("Running")
        };
        Some(ProcessInspectorDossier {
            key: p.key,
            ppid: p.ppid,
            name: p.name.clone(),
            group_key: derive_coalition_group_name(&p.name),
            cpu_formatted: p.cpu_percent.format_with_unit(1, "%"),
            phys_footprint_formatted: format_process_bytes_metric(&p.phys_footprint_bytes),
            resident_formatted: format_process_bytes_metric(&p.resident_bytes),
            status_label,
        })
    }
}

fn format_process_bytes_metric(metric: &nexus_core::Metric<f64>) -> String {
    match metric.reading {
        MetricReading::Measured { value, .. } if value.is_finite() && value >= 0.0 => {
            const KIB: f64 = 1024.0;
            const MIB: f64 = 1024.0 * 1024.0;
            const GIB: f64 = 1024.0 * 1024.0 * 1024.0;
            if value >= GIB {
                format!("{:.2} GiB ({:.0} B)", value / GIB, value)
            } else if value >= MIB {
                format!("{:.1} MiB ({:.0} B)", value / MIB, value)
            } else if value >= KIB {
                format!("{:.1} KiB ({:.0} B)", value / KIB, value)
            } else {
                format!("{:.0} B", value)
            }
        }
        _ => metric.format_with_unit(0, "B"),
    }
}

fn derive_coalition_group_name(name: &str) -> CompactString {
    if let Some((prefix, _)) = name.split_once(" Helper") {
        CompactString::from(prefix.trim())
    } else {
        CompactString::from(name)
    }
}
