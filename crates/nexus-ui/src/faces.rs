use crate::shaders::{GpuDiskBlob, GpuTunnelSegment};
use compact_str::CompactString;
use nexus_core::{
    LogicalProcessorClass, Metric, MetricProvenance, MetricQuality, MetricReading, SourceId,
    SystemHardwareSummary, SystemSnapshot, UnavailableReason, UnitTag, constants,
};
use nexus_disk::DiskScanReport;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::Path;

fn default_span() -> u8 {
    1
}

fn default_true_bool() -> bool {
    true
}

/// Non-numeric and numeric values returned by `SensorPath::resolve_value`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub enum SensorValue {
    Numeric(Metric<f64>),
    Text(CompactString),
    DurationSeconds(f64),
    Flag(bool),
    DiskCategories,
}

impl SensorValue {
    pub fn format_display(&self, unit: &str) -> String {
        match self {
            Self::Numeric(m) => m.format_with_unit(1, unit),
            Self::Text(s) => s.to_string(),
            Self::DurationSeconds(sec) => {
                let s = sec.max(0.0) as u64;
                let days = s / 86_400;
                let hours = (s % 86_400) / 3600;
                let mins = (s % 3600) / 60;
                if days > 0 {
                    format!("{days}d {hours}h {mins}m")
                } else {
                    format!("{hours}h {mins}m")
                }
            }
            Self::Flag(v) => if *v { "ACTIVE" } else { "INACTIVE" }.to_string(),
            Self::DiskCategories => "7 Categories".to_string(),
        }
    }

    pub fn normalized_fraction(&self, scale_max: f64) -> Option<f64> {
        match self {
            Self::Numeric(m) => match m.reading {
                MetricReading::Measured { value, .. } if value.is_finite() => {
                    Some((value / scale_max.max(1e-5)).clamp(0.0, 1.0))
                }
                _ => None,
            },
            Self::Flag(b) => Some(if *b { 1.0 } else { 0.0 }),
            _ => None,
        }
    }
}

/// Discoverable sensor descriptor exposed by `SensorRegistry`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SensorDescriptor {
    pub path: SensorPath,
    pub label: CompactString,
    pub unit: CompactString,
    pub default_max: u32,
    #[serde(default = "default_true_bool")]
    pub is_numeric: bool,
}

fn desc(
    path: SensorPath,
    label: impl Into<CompactString>,
    unit: &str,
    default_max: u32,
    is_numeric: bool,
) -> SensorDescriptor {
    SensorDescriptor {
        path,
        label: label.into(),
        unit: CompactString::from(unit),
        default_max,
        is_numeric,
    }
}

/// 2-axis composable sensor path (`category/instance/metric`), supporting dynamic core indices,
/// `[Group]` P-Cluster / E-Cluster aggregates, SoC watts, thermals, disks, networks, and non-numeric sensors.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct SensorPath {
    pub category: CompactString,
    pub instance: CompactString,
    pub metric: CompactString,
}

impl SensorPath {
    pub fn new(category: &str, instance: &str, metric: &str) -> Self {
        Self {
            category: CompactString::from(category),
            instance: CompactString::from(instance),
            metric: CompactString::from(metric),
        }
    }

    pub fn parse_str(path: &str) -> Option<Self> {
        let mut it = path.splitn(3, '/');
        let cat = it.next()?;
        let inst = it.next()?;
        let met = it.next()?;
        Some(Self::new(cat, inst, met))
    }

    pub fn as_path_string(&self) -> String {
        format!("{}/{}/{}", self.category, self.instance, self.metric)
    }

    pub fn is_group(&self) -> bool {
        self.instance.starts_with('[') || self.instance.as_str() == "*"
    }

    /// Expands `[Group]` sensors into concrete `(SensorPath, label)` pairs using the live `SystemSnapshot`.
    pub fn expand(&self, snap: &SystemSnapshot) -> Vec<(SensorPath, CompactString)> {
        let cat = self.category.as_str();
        let inst = self.instance.as_str();
        let met = self.metric.as_str();

        if cat == "cpu" && (inst == "[Group:AllCores]" || inst == "*") {
            let mut out = Vec::with_capacity(snap.system.cores.len());
            for c in &snap.system.cores {
                let tag = match c.class {
                    LogicalProcessorClass::Performance => "P",
                    LogicalProcessorClass::Efficiency => "E",
                    _ => "C",
                };
                out.push((
                    SensorPath::new("cpu", &format!("core_{}", c.core_index), met),
                    CompactString::from(format!("{tag}{}", c.core_index)),
                ));
            }
            if !out.is_empty() {
                return out;
            }
        }
        if cat == "cpu" && (inst == "[Group:P]" || inst == "[P-Cluster]") {
            let mut out = Vec::new();
            for c in snap
                .system
                .cores
                .iter()
                .filter(|c| c.class == LogicalProcessorClass::Performance)
            {
                out.push((
                    SensorPath::new("cpu", &format!("core_{}", c.core_index), met),
                    CompactString::from(format!("P{}", c.core_index)),
                ));
            }
            if !out.is_empty() {
                return out;
            }
        }
        if cat == "cpu" && (inst == "[Group:E]" || inst == "[E-Cluster]") {
            let mut out = Vec::new();
            for c in snap
                .system
                .cores
                .iter()
                .filter(|c| c.class == LogicalProcessorClass::Efficiency)
            {
                out.push((
                    SensorPath::new("cpu", &format!("core_{}", c.core_index), met),
                    CompactString::from(format!("E{}", c.core_index)),
                ));
            }
            if !out.is_empty() {
                return out;
            }
        }
        if cat == "disk" && (inst == "[Group:AllDisks]" || inst == "*") {
            let out: Vec<_> = snap
                .disks
                .iter()
                .map(|d| {
                    (
                        SensorPath::new("disk", &d.bsd_name, met),
                        d.bsd_name.clone(),
                    )
                })
                .collect();
            if !out.is_empty() {
                return out;
            }
        }
        if cat == "network" && (inst == "[Group:AllInterfaces]" || inst == "*") {
            let out: Vec<_> = snap
                .networks
                .iter()
                .map(|n| {
                    (
                        SensorPath::new("network", &n.interface, met),
                        n.interface.clone(),
                    )
                })
                .collect();
            if !out.is_empty() {
                return out;
            }
        }
        if cat == "thermal" && (inst == "[Group:AllSensors]" || inst == "*") {
            let out: Vec<_> = snap
                .hardware_telemetry
                .thermals
                .sensors
                .iter()
                .map(|t| {
                    (
                        SensorPath::new("thermal", &t.name, "celsius"),
                        t.name.clone(),
                    )
                })
                .collect();
            if !out.is_empty() {
                return out;
            }
        }
        if cat == "soc" && (inst == "[Group:MediaBlocks]" || inst == "*") {
            let out: Vec<_> = snap
                .hardware_telemetry
                .soc_power
                .media_blocks
                .iter()
                .map(|m| {
                    (
                        SensorPath::new("soc", &format!("media_{}", m.id), "residency"),
                        m.label.clone(),
                    )
                })
                .collect();
            if !out.is_empty() {
                return out;
            }
        }

        vec![(self.clone(), CompactString::from(self.as_path_string()))]
    }

    pub fn resolve_value(&self, snap: &SystemSnapshot) -> Option<SensorValue> {
        self.resolve_value_with_hw(snap, None)
    }

    pub fn resolve_value_with_hw(
        &self,
        snap: &SystemSnapshot,
        hw: Option<&SystemHardwareSummary>,
    ) -> Option<SensorValue> {
        match (
            self.category.as_str(),
            self.instance.as_str(),
            self.metric.as_str(),
        ) {
            ("gpu", "0", "model") => return Some(SensorValue::Text(snap.system.gpu_model.clone())),
            ("disk", "root", "categories") => return Some(SensorValue::DiskCategories),
            ("system", "uptime", "duration") | ("os", "system", "uptime") => {
                return match snap.system.uptime_seconds.reading {
                    MetricReading::Measured { value, .. } => {
                        Some(SensorValue::DurationSeconds(value))
                    }
                    _ => Some(SensorValue::Numeric(snap.system.uptime_seconds)),
                };
            }
            ("os", "system", "hostname") => {
                let name = hw.map_or_else(
                    || CompactString::const_new("localhost"),
                    |h| h.hostname.clone(),
                );
                return Some(SensorValue::Text(name));
            }
            ("os", "system", "os_version") => {
                let ver = hw.map_or_else(
                    || CompactString::from(std::env::consts::OS),
                    |h| CompactString::from(format!("{} {}", h.os_name, h.os_version)),
                );
                return Some(SensorValue::Text(ver));
            }
            ("os", "system", "kernel") => {
                let kern = hw.map_or_else(
                    || CompactString::from(std::env::consts::OS),
                    |h| h.firmware_version.clone(),
                );
                return Some(SensorValue::Text(kern));
            }
            ("os", "system", "arch") => {
                let arch = hw.map_or_else(
                    || CompactString::from(std::env::consts::ARCH),
                    |h| h.architecture.clone(),
                );
                return Some(SensorValue::Text(arch));
            }
            ("system", "snapshot", "generation") => {
                return Some(SensorValue::Text(CompactString::from(format!(
                    "#{}",
                    snap.generation
                ))));
            }
            _ => {}
        }
        self.resolve_metric(snap).map(SensorValue::Numeric)
    }

    /// Resolves a numeric `Metric<f64>` from a `SystemSnapshot`.
    pub fn resolve_metric(&self, snap: &SystemSnapshot) -> Option<Metric<f64>> {
        let cat = self.category.as_str();
        let inst = self.instance.as_str();
        let met = self.metric.as_str();

        if cat == "cpu" {
            if inst == "total" {
                return match met {
                    "utilization" => Some(snap.system.cpu_percent),
                    "user" => Some(snap.system.cpu_user_percent),
                    "kernel" => Some(snap.system.cpu_kernel_percent),
                    _ => None,
                };
            }
            if inst == "load" {
                return match met {
                    "1m" => Some(snap.system.load_avg_1m),
                    "5m" => Some(snap.system.load_avg_5m),
                    "15m" => Some(snap.system.load_avg_15m),
                    _ => None,
                };
            }
            if inst == "[P-Cluster]" || inst == "[Group:P]" {
                return Some(aggregate_core_cluster(
                    snap,
                    LogicalProcessorClass::Performance,
                ));
            }
            if inst == "[E-Cluster]" || inst == "[Group:E]" {
                return Some(aggregate_core_cluster(
                    snap,
                    LogicalProcessorClass::Efficiency,
                ));
            }
            if let Some(idx_str) = inst.strip_prefix("core_")
                && let Ok(idx) = idx_str.parse::<u32>()
                && let Some(core) = snap.system.cores.iter().find(|c| c.core_index == idx)
            {
                return match met {
                    "utilization" => Some(core.total_percent),
                    "user" => Some(core.user_percent),
                    "kernel" => Some(core.system_percent),
                    "idle" => Some(core.idle_percent),
                    _ => None,
                };
            }
        }

        if cat == "memory" {
            if inst == "system" {
                return match met {
                    "used_percent" => Some(snap.system.mem_used_percent),
                    "used_bytes" => Some(snap.system.mem_used_bytes),
                    "total_bytes" => Some(snap.system.mem_total_bytes),
                    "active_bytes" => Some(snap.system.mem_active_bytes),
                    "wired_bytes" => Some(snap.system.mem_wired_bytes),
                    "compressed_bytes" => Some(snap.system.mem_compressed_bytes),
                    "free_bytes" => Some(snap.system.mem_free_bytes),
                    "pressure" => Some(snap.system.system_pressure_percent),
                    _ => None,
                };
            }
            if inst == "swap" {
                return match met {
                    "used_bytes" => Some(snap.system.swap_used_bytes),
                    "total_bytes" => Some(snap.system.swap_total_bytes),
                    "used_percent" => Some(snap.system.swap_used_percent),
                    _ => None,
                };
            }
        }

        if cat == "gpu" && inst == "0" {
            return match met {
                "utilization" => Some(snap.system.gpu_utilization_percent),
                "core_count" => Some(snap.system.gpu_core_count),
                "memory_used" => Some(snap.system.gpu_in_use_memory_bytes),
                "memory_alloc" => Some(snap.system.gpu_alloc_memory_bytes),
                _ => None,
            };
        }

        if cat == "soc" {
            let sp = &snap.hardware_telemetry.soc_power;
            return match (inst, met) {
                ("package", "watts") => Some(sp.package_watts),
                ("cpu", "watts") => Some(sp.cpu_watts),
                ("gpu", "watts") => Some(sp.gpu_watts),
                ("ane", "watts") => Some(sp.ane_watts),
                ("dram", "watts") => Some(sp.dram_watts),
                ("pmp", "watts") => Some(sp.pmp_power_watts),
                ("system_in", "watts") => Some(sp.system_power_in_watts),
                ("p_cluster", "residency") => Some(sp.p_cluster_active_residency_percent),
                ("e_cluster", "residency") => Some(sp.e_cluster_active_residency_percent),
                ("ane", "residency") => Some(sp.ane_active_residency_percent),
                ("floor", "residency") => Some(sp.soc_floor_residency_percent),
                ("e_cluster", "mhz") => Some(sp.e_cluster_mhz),
                ("p_cluster", "mhz") => Some(sp.p_cluster_mhz),
                ("gpu", "mhz") => Some(sp.gpu_clock_mhz),
                (media_inst, "residency") if media_inst.starts_with("media_") => {
                    let target = media_inst.trim_start_matches("media_");
                    sp.media_blocks
                        .iter()
                        .find(|b| b.id.as_str() == target)
                        .map(|b| b.active_residency_percent)
                }
                (cluster_inst, "mhz") if cluster_inst.starts_with("cluster_") => {
                    let target = cluster_inst.trim_start_matches("cluster_");
                    sp.cluster_clocks
                        .iter()
                        .find(|c| c.cluster_name.as_str() == target)
                        .map(|c| c.effective_mhz)
                }
                _ => None,
            };
        }

        if cat == "thermal" {
            let th = &snap.hardware_telemetry.thermals;
            if inst == "soc" {
                return match met {
                    "max_celsius" => Some(th.max_die_celsius),
                    "warning_level" => Some(th.warning_level),
                    _ => None,
                };
            }
            if let Some(sensor) = th.sensors.iter().find(|s| s.name.as_str() == inst) {
                return Some(sensor.celsius);
            }
        }

        if cat == "disk" {
            let target = snap
                .disks
                .iter()
                .find(|d| d.bsd_name.as_str() == inst)
                .or_else(|| snap.disks.first());
            if let Some(d) = target {
                return match met {
                    "used_percent" => Some(d.root_used_percent),
                    "available_bytes" => Some(d.root_available_bytes),
                    "read_bps" => Some(d.read_bytes_per_sec),
                    "write_bps" => Some(d.write_bytes_per_sec),
                    _ => None,
                };
            }
        }

        if cat == "network" {
            let target = snap
                .networks
                .iter()
                .find(|n| n.interface.as_str() == inst)
                .or_else(|| snap.networks.first());
            if let Some(n) = target {
                return match met {
                    "rx_bps" => Some(n.rx_bytes_per_sec),
                    "tx_bps" => Some(n.tx_bytes_per_sec),
                    "rx_total" => Some(n.rx_bytes_total),
                    "tx_total" => Some(n.tx_bytes_total),
                    _ => None,
                };
            }
        }

        None
    }
}

fn aggregate_core_cluster(snap: &SystemSnapshot, class: LogicalProcessorClass) -> Metric<f64> {
    let matching: Vec<f64> = snap
        .system
        .cores
        .iter()
        .filter(|c| c.class == class)
        .filter_map(|c| match c.total_percent.reading {
            MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
            _ => None,
        })
        .collect();
    if matching.is_empty() {
        return Metric::unavailable(
            UnavailableReason::Unsupported,
            MetricProvenance {
                source: SourceId(0),
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns: snap.sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Unknown,
                contributed_by_stale: false,
            },
        );
    }
    let avg = matching.iter().sum::<f64>() / (matching.len() as f64);
    Metric::measured(
        avg,
        MetricProvenance {
            source: SourceId(0),
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns: snap.sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Fallback,
            contributed_by_stale: false,
        },
    )
}

/// Discovers all live and structural sensors from a `SystemSnapshot` for picker UIs and CLI queries.
pub struct SensorRegistry;

impl SensorRegistry {
    pub fn discover(snap: &SystemSnapshot) -> Vec<SensorDescriptor> {
        let mut out = vec![
            desc(
                SensorPath::new("cpu", "total", "utilization"),
                "CPU Total Utilization",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "total", "user"),
                "CPU User Space",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "total", "kernel"),
                "CPU Kernel Space",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "[P-Cluster]", "utilization"),
                "P-Cluster Group Avg",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "[E-Cluster]", "utilization"),
                "E-Cluster Group Avg",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "load", "1m"),
                "Load Average (1m)",
                "",
                16,
                true,
            ),
            desc(
                SensorPath::new("memory", "system", "used_percent"),
                "Memory Used %",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("memory", "system", "pressure"),
                "Memory Pressure %",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("memory", "swap", "used_percent"),
                "Swap Used %",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("gpu", "0", "utilization"),
                "GPU Active %",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("gpu", "0", "model"),
                "GPU Device Name",
                "",
                1,
                false,
            ),
            desc(
                SensorPath::new("soc", "package", "watts"),
                "SoC Package Power",
                "W",
                60,
                true,
            ),
            desc(
                SensorPath::new("soc", "cpu", "watts"),
                "CPU Rail Power",
                "W",
                45,
                true,
            ),
            desc(
                SensorPath::new("soc", "gpu", "watts"),
                "GPU Rail Power",
                "W",
                45,
                true,
            ),
            desc(
                SensorPath::new("soc", "ane", "watts"),
                if nexus_core::HostPlatform::current().is_macos() {
                    "ANE Power"
                } else {
                    "Accelerator Power"
                },
                "W",
                15,
                true,
            ),
            desc(
                SensorPath::new("soc", "dram", "watts"),
                "DRAM Power",
                "W",
                20,
                true,
            ),
            desc(
                SensorPath::new("soc", "system_in", "watts"),
                "System Input Power",
                "W",
                100,
                true,
            ),
            desc(
                SensorPath::new("soc", "p_cluster", "residency"),
                "P-Cluster Residency",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("soc", "e_cluster", "residency"),
                "E-Cluster Residency",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("soc", "ane", "residency"),
                if nexus_core::HostPlatform::current().is_macos() {
                    "ANE Active Residency"
                } else {
                    "Accelerator Active Residency"
                },
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("soc", "floor", "residency"),
                "SoC Floor Residency",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("soc", "pmp", "watts"),
                "PMP Power",
                "W",
                15,
                true,
            ),
            desc(
                SensorPath::new("soc", "e_cluster", "mhz"),
                "E-Cluster Clock",
                "MHz",
                3500,
                true,
            ),
            desc(
                SensorPath::new("soc", "p_cluster", "mhz"),
                "P-Cluster Clock",
                "MHz",
                4500,
                true,
            ),
            desc(
                SensorPath::new("soc", "gpu", "mhz"),
                "GPU Clock",
                "MHz",
                1600,
                true,
            ),
            desc(
                SensorPath::new("thermal", "soc", "max_celsius"),
                "Max Die Temp",
                "C",
                110,
                true,
            ),
            desc(
                SensorPath::new("disk", "root", "used_percent"),
                "Root Volume Used %",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("disk", "root", "categories"),
                "Disk Category Breakdown",
                "",
                100,
                false,
            ),
            desc(
                SensorPath::new("system", "uptime", "duration"),
                "System Uptime",
                "",
                1,
                true,
            ),
            desc(
                SensorPath::new("os", "system", "hostname"),
                "OS Hostname",
                "",
                1,
                false,
            ),
            desc(
                SensorPath::new("os", "system", "os_version"),
                "OS Version",
                "",
                1,
                false,
            ),
            desc(
                SensorPath::new("os", "system", "kernel"),
                "OS Kernel / Firmware",
                "",
                1,
                false,
            ),
            desc(
                SensorPath::new("os", "system", "arch"),
                "CPU Architecture",
                "",
                1,
                false,
            ),
            desc(
                SensorPath::new("os", "system", "uptime"),
                "OS Uptime",
                "",
                1,
                true,
            ),
            desc(
                SensorPath::new("cpu", "[Group:AllCores]", "utilization"),
                "[Group] All Logical Cores",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "[Group:P]", "utilization"),
                "[Group] Performance Cores",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("cpu", "[Group:E]", "utilization"),
                "[Group] Efficiency Cores",
                "%",
                100,
                true,
            ),
            desc(
                SensorPath::new("disk", "[Group:AllDisks]", "read_bps"),
                "[Group] All Disks Read/s",
                "B/s",
                500_000_000,
                true,
            ),
            desc(
                SensorPath::new("network", "[Group:AllInterfaces]", "rx_bps"),
                "[Group] All Network RX/s",
                "B/s",
                125_000_000,
                true,
            ),
            desc(
                SensorPath::new("thermal", "[Group:AllSensors]", "celsius"),
                "[Group] All Thermal Sensors",
                "C",
                110,
                true,
            ),
            desc(
                SensorPath::new("soc", "[Group:MediaBlocks]", "residency"),
                "[Group] All Media Blocks",
                "%",
                100,
                true,
            ),
        ];

        for m in &snap.hardware_telemetry.soc_power.media_blocks {
            out.push(desc(
                SensorPath::new("soc", &format!("media_{}", m.id), "residency"),
                format!("Media {}", m.label),
                "%",
                100,
                true,
            ));
        }
        for c in &snap.hardware_telemetry.soc_power.cluster_clocks {
            out.push(desc(
                SensorPath::new("soc", &format!("cluster_{}", c.cluster_name), "mhz"),
                format!("Cluster {} Clock", c.cluster_name),
                "MHz",
                4500,
                true,
            ));
        }
        for c in &snap.system.cores {
            out.push(desc(
                SensorPath::new("cpu", &format!("core_{}", c.core_index), "utilization"),
                format!("Core #{} ({:?})", c.core_index, c.class),
                "%",
                100,
                true,
            ));
        }
        for t in &snap.hardware_telemetry.thermals.sensors {
            out.push(desc(
                SensorPath::new("thermal", &t.name, "celsius"),
                format!("Thermal {}", t.name),
                "C",
                110,
                true,
            ));
        }
        for d in &snap.disks {
            out.push(desc(
                SensorPath::new("disk", &d.bsd_name, "read_bps"),
                format!("Disk {} Read/s", d.bsd_name),
                "B/s",
                500_000_000,
                true,
            ));
            out.push(desc(
                SensorPath::new("disk", &d.bsd_name, "write_bps"),
                format!("Disk {} Write/s", d.bsd_name),
                "B/s",
                500_000_000,
                true,
            ));
        }
        for n in &snap.networks {
            out.push(desc(
                SensorPath::new("network", &n.interface, "rx_bps"),
                format!("Net {} RX/s", n.interface),
                "B/s",
                125_000_000,
                true,
            ));
            out.push(desc(
                SensorPath::new("network", &n.interface, "tx_bps"),
                format!("Net {} TX/s", n.interface),
                "B/s",
                125_000_000,
                true,
            ));
        }

        out
    }
}

/// Capability flags declared by each `FaceKind` (KDE Plasma `FaceConfiguration` contract).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FaceProperties {
    pub supports_sensors: bool,
    pub supports_sensors_colors: bool,
    pub supports_total_sensors: bool,
    pub max_total_sensors: usize,
    pub supports_low_priority_sensors: bool,
    pub allows_in_face_grid: bool,
}

/// First-class `FaceKind` variants combining KDE Plasma System Monitor faces with Nexus instrument faces.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum FaceKind {
    VfdSegmentedMeter,
    CompressedHistoryGraph,
    BarChart,
    PieChart,
    HorizontalCompositionBar,
    ColorGrid,
    FaceGrid,
    KpiTextReadout,
    ProcessorClusterGrid,
    DiskOceanMetaballs,
    DiskDefragBlocks,
    VectorTunnelTempest,
    ProcessTable,
    ApplicationsTable,
}

impl FaceKind {
    pub const ALL: [Self; 14] = [
        Self::VfdSegmentedMeter,
        Self::CompressedHistoryGraph,
        Self::BarChart,
        Self::PieChart,
        Self::HorizontalCompositionBar,
        Self::ColorGrid,
        Self::FaceGrid,
        Self::KpiTextReadout,
        Self::ProcessorClusterGrid,
        Self::DiskOceanMetaballs,
        Self::DiskDefragBlocks,
        Self::VectorTunnelTempest,
        Self::ProcessTable,
        Self::ApplicationsTable,
    ];

    pub const fn label(self) -> &'static str {
        match self {
            Self::VfdSegmentedMeter => "VFD Segmented Meter",
            Self::CompressedHistoryGraph => "Line Chart (History)",
            Self::BarChart => "Bar Chart",
            Self::PieChart => "Pie / Ring Chart",
            Self::HorizontalCompositionBar => "Horizontal Bars",
            Self::ColorGrid => "Color Grid",
            Self::FaceGrid => "Face Grid",
            Self::KpiTextReadout => "Text Only (KPI)",
            Self::ProcessorClusterGrid => "Processor Cluster Grid",
            Self::DiskOceanMetaballs => "Disk Ocean Metaballs",
            Self::DiskDefragBlocks => "Disk 48x26 Defrag",
            Self::VectorTunnelTempest => "Vector Tunnel Tempest",
            Self::ProcessTable => "Process Table",
            Self::ApplicationsTable => "Applications Table",
        }
    }

    pub const fn properties(self) -> FaceProperties {
        match self {
            Self::VfdSegmentedMeter
            | Self::BarChart
            | Self::ColorGrid
            | Self::KpiTextReadout
            | Self::ProcessorClusterGrid => FaceProperties {
                supports_sensors: true,
                supports_sensors_colors: true,
                supports_total_sensors: false,
                max_total_sensors: 0,
                supports_low_priority_sensors: false,
                allows_in_face_grid: true,
            },
            Self::CompressedHistoryGraph => FaceProperties {
                supports_sensors: true,
                supports_sensors_colors: true,
                supports_total_sensors: false,
                max_total_sensors: 0,
                supports_low_priority_sensors: true,
                allows_in_face_grid: true,
            },
            Self::PieChart | Self::HorizontalCompositionBar => FaceProperties {
                supports_sensors: true,
                supports_sensors_colors: true,
                supports_total_sensors: true,
                max_total_sensors: 1,
                supports_low_priority_sensors: false,
                allows_in_face_grid: true,
            },
            Self::FaceGrid => FaceProperties {
                supports_sensors: true,
                supports_sensors_colors: true,
                supports_total_sensors: false,
                max_total_sensors: 0,
                supports_low_priority_sensors: false,
                allows_in_face_grid: false,
            },
            Self::DiskOceanMetaballs
            | Self::DiskDefragBlocks
            | Self::VectorTunnelTempest
            | Self::ProcessTable
            | Self::ApplicationsTable => FaceProperties {
                supports_sensors: false,
                supports_sensors_colors: false,
                supports_total_sensors: false,
                max_total_sensors: 0,
                supports_low_priority_sensors: false,
                allows_in_face_grid: false,
            },
        }
    }
}

/// KDE Plasma per-face configuration attached to each `DashboardTile`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TileFaceConfig {
    pub show_title: bool,
    pub show_legend: bool,
    pub stacked: bool,
    pub smooth: bool,
    pub fill_opacity: f32,
    pub grid_lines: bool,
    pub range_auto_y: bool,
    pub range_from: f64,
    pub range_to: f64,
    pub pie_start_angle: f32,
    pub pie_end_angle: f32,
    pub grid_columns: u8,
    pub nested_face: FaceKind,
    pub min_update_ms: u64,
    pub sensor_colors: HashMap<CompactString, [u8; 3]>,
    pub low_priority_sensors: Vec<SensorPath>,
    pub total_sensors: Vec<SensorPath>,
}

impl Default for TileFaceConfig {
    fn default() -> Self {
        Self {
            show_title: true,
            show_legend: true,
            stacked: false,
            smooth: true,
            fill_opacity: 0.35,
            grid_lines: true,
            range_auto_y: true,
            range_from: 0.0,
            range_to: 100.0,
            pie_start_angle: -180.0,
            pie_end_angle: 180.0,
            grid_columns: 4,
            nested_face: FaceKind::VfdSegmentedMeter,
            min_update_ms: 0,
            sensor_colors: HashMap::new(),
            low_priority_sensors: Vec::new(),
            total_sensors: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DashboardTile {
    pub id: CompactString,
    pub title: CompactString,
    pub face: FaceKind,
    pub sensors: Vec<SensorPath>,
    #[serde(default = "default_span")]
    pub col_span: u8,
    #[serde(default = "default_span")]
    pub row_span: u8,
    #[serde(default)]
    pub config: TileFaceConfig,
}

impl DashboardTile {
    /// Non-destructively swaps `self.face` while preserving all bound `SensorPath`s and `config`.
    pub fn swap_face(&mut self, new_face: FaceKind) {
        self.face = new_face;
    }
}

/// Composable 2-axis `PageEngine` (`FaceKind x SensorPath`).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
pub struct PageEngine {
    pub tiles: Vec<DashboardTile>,
}

impl PageEngine {
    pub fn new_default_summary() -> Self {
        Self {
            tiles: vec![
                DashboardTile {
                    id: CompactString::const_new("tile.cpu"),
                    title: CompactString::const_new("CPU Load"),
                    face: FaceKind::VfdSegmentedMeter,
                    sensors: vec![SensorPath::new("cpu", "total", "utilization")],
                    col_span: 1,
                    row_span: 1,
                    config: TileFaceConfig::default(),
                },
                DashboardTile {
                    id: CompactString::const_new("tile.cpu_history"),
                    title: CompactString::const_new("CPU History"),
                    face: FaceKind::CompressedHistoryGraph,
                    sensors: vec![SensorPath::new("cpu", "total", "utilization")],
                    col_span: 1,
                    row_span: 1,
                    config: TileFaceConfig::default(),
                },
                DashboardTile {
                    id: CompactString::const_new("tile.soc_clusters"),
                    title: CompactString::const_new(
                        if nexus_core::HostPlatform::current().is_macos() {
                            "Apple Silicon Clusters"
                        } else {
                            "Processor & GPU Clusters"
                        },
                    ),
                    face: FaceKind::ProcessorClusterGrid,
                    sensors: vec![
                        SensorPath::new("soc", "p_cluster", "residency"),
                        SensorPath::new("soc", "e_cluster", "residency"),
                    ],
                    col_span: 1,
                    row_span: 1,
                    config: TileFaceConfig::default(),
                },
                DashboardTile {
                    id: CompactString::const_new("tile.memory_bar"),
                    title: CompactString::const_new("Memory Composition"),
                    face: FaceKind::HorizontalCompositionBar,
                    sensors: vec![
                        SensorPath::new("memory", "system", "used_percent"),
                        SensorPath::new("memory", "system", "pressure"),
                    ],
                    col_span: 1,
                    row_span: 1,
                    config: TileFaceConfig::default(),
                },
                DashboardTile {
                    id: CompactString::const_new("tile.soc_power"),
                    title: CompactString::const_new("SoC Package Watts"),
                    face: FaceKind::KpiTextReadout,
                    sensors: vec![SensorPath::new("soc", "package", "watts")],
                    col_span: 1,
                    row_span: 1,
                    config: TileFaceConfig::default(),
                },
                DashboardTile {
                    id: CompactString::const_new("tile.disk_ocean"),
                    title: CompactString::const_new("Disk Space Ocean"),
                    face: FaceKind::DiskOceanMetaballs,
                    sensors: vec![SensorPath::new("disk", "root", "categories")],
                    col_span: 1,
                    row_span: 1,
                    config: TileFaceConfig::default(),
                },
            ],
        }
    }

    pub fn add_tile(&mut self, tile: DashboardTile) {
        self.tiles.push(tile);
    }

    pub fn remove_tile(&mut self, tile_id: &str) -> bool {
        let before = self.tiles.len();
        self.tiles.retain(|t| t.id.as_str() != tile_id);
        self.tiles.len() != before
    }

    pub fn swap_tile_face(&mut self, tile_id: &str, new_face: FaceKind) -> bool {
        if let Some(t) = self.tiles.iter_mut().find(|t| t.id.as_str() == tile_id) {
            t.swap_face(new_face);
            true
        } else {
            false
        }
    }

    pub fn bind_sensor(&mut self, tile_id: &str, sensor: SensorPath) -> bool {
        if let Some(t) = self.tiles.iter_mut().find(|t| t.id.as_str() == tile_id) {
            if !t.sensors.contains(&sensor) {
                t.sensors.push(sensor);
            }
            true
        } else {
            false
        }
    }

    pub fn unbind_sensor(&mut self, tile_id: &str, sensor_path_str: &str) -> bool {
        if let Some(t) = self.tiles.iter_mut().find(|t| t.id.as_str() == tile_id) {
            let before = t.sensors.len();
            t.sensors
                .retain(|s| s.as_path_string().as_str() != sensor_path_str);
            t.sensors.len() != before
        } else {
            false
        }
    }
}

/// Interactive undo/redo editing session over a `PageEngine`.
#[derive(Debug, Clone)]
pub struct PageEditSession {
    pub active: bool,
    original: PageEngine,
    pub current: PageEngine,
    undo_stack: Vec<PageEngine>,
    redo_stack: Vec<PageEngine>,
    next_id_seq: usize,
}

impl PageEditSession {
    pub fn new(initial: PageEngine) -> Self {
        let seq = initial.tiles.len() + 1;
        Self {
            active: false,
            original: initial.clone(),
            current: initial,
            undo_stack: Vec::new(),
            redo_stack: Vec::new(),
            next_id_seq: seq,
        }
    }

    pub fn begin(&mut self, live: PageEngine) {
        self.active = true;
        self.original = live.clone();
        self.current = live;
        self.undo_stack.clear();
        self.redo_stack.clear();
    }

    pub fn can_undo(&self) -> bool {
        !self.undo_stack.is_empty()
    }

    pub fn can_redo(&self) -> bool {
        !self.redo_stack.is_empty()
    }

    pub fn is_dirty(&self) -> bool {
        self.current != self.original
    }

    fn record_mutation(&mut self, mutated: bool) {
        if !mutated {
            self.undo_stack.pop();
        } else {
            self.redo_stack.clear();
        }
    }

    pub fn add_tile(&mut self, title: &str, face: FaceKind, sensor: SensorPath) -> CompactString {
        self.undo_stack.push(self.current.clone());
        self.redo_stack.clear();
        let id = CompactString::from(format!("tile.custom_{}", self.next_id_seq));
        self.next_id_seq += 1;
        self.current.add_tile(DashboardTile {
            id: id.clone(),
            title: CompactString::from(title),
            face,
            sensors: vec![sensor],
            col_span: 1,
            row_span: 1,
            config: TileFaceConfig::default(),
        });
        id
    }

    pub fn update_tile(&mut self, tile_id: &str, f: impl FnOnce(&mut DashboardTile)) -> bool {
        self.undo_stack.push(self.current.clone());
        let ok = if let Some(t) = self
            .current
            .tiles
            .iter_mut()
            .find(|t| t.id.as_str() == tile_id)
        {
            f(t);
            true
        } else {
            false
        };
        self.record_mutation(ok);
        ok
    }

    pub fn move_tile_up(&mut self, tile_id: &str) -> bool {
        let Some(idx) = self
            .current
            .tiles
            .iter()
            .position(|t| t.id.as_str() == tile_id)
        else {
            return false;
        };
        if idx == 0 {
            return false;
        }
        self.undo_stack.push(self.current.clone());
        self.current.tiles.swap(idx, idx - 1);
        self.redo_stack.clear();
        true
    }

    pub fn move_tile_down(&mut self, tile_id: &str) -> bool {
        let Some(idx) = self
            .current
            .tiles
            .iter()
            .position(|t| t.id.as_str() == tile_id)
        else {
            return false;
        };
        if idx + 1 >= self.current.tiles.len() {
            return false;
        }
        self.undo_stack.push(self.current.clone());
        self.current.tiles.swap(idx, idx + 1);
        self.redo_stack.clear();
        true
    }

    pub fn remove_tile(&mut self, tile_id: &str) -> bool {
        self.undo_stack.push(self.current.clone());
        let ok = self.current.remove_tile(tile_id);
        self.record_mutation(ok);
        ok
    }

    pub fn swap_face(&mut self, tile_id: &str, new_face: FaceKind) -> bool {
        self.undo_stack.push(self.current.clone());
        let ok = self.current.swap_tile_face(tile_id, new_face);
        self.record_mutation(ok);
        ok
    }

    pub fn bind_sensor(&mut self, tile_id: &str, sensor: SensorPath) -> bool {
        self.undo_stack.push(self.current.clone());
        let ok = self.current.bind_sensor(tile_id, sensor);
        self.record_mutation(ok);
        ok
    }

    pub fn unbind_sensor(&mut self, tile_id: &str, sensor_path_str: &str) -> bool {
        self.undo_stack.push(self.current.clone());
        let ok = self.current.unbind_sensor(tile_id, sensor_path_str);
        self.record_mutation(ok);
        ok
    }

    pub fn reset_to_default(&mut self) {
        self.undo_stack.push(self.current.clone());
        self.redo_stack.clear();
        self.current = PageEngine::new_default_summary();
    }

    pub fn undo(&mut self) -> bool {
        if let Some(prev) = self.undo_stack.pop() {
            self.redo_stack.push(self.current.clone());
            self.current = prev;
            true
        } else {
            false
        }
    }

    pub fn redo(&mut self) -> bool {
        if let Some(next) = self.redo_stack.pop() {
            self.undo_stack.push(self.current.clone());
            self.current = next;
            true
        } else {
            false
        }
    }

    pub fn commit(&mut self) -> PageEngine {
        self.active = false;
        self.original = self.current.clone();
        self.undo_stack.clear();
        self.redo_stack.clear();
        self.current.clone()
    }

    pub fn cancel(&mut self) -> PageEngine {
        self.active = false;
        self.current = self.original.clone();
        self.undo_stack.clear();
        self.redo_stack.clear();
        self.current.clone()
    }
}

/// JSON-serializable Dashboard Workspace Profile.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WorkspaceProfile {
    pub id: CompactString,
    pub name: CompactString,
    pub page: PageEngine,
}

/// KDE Plasma Table `ColumnDisplayStyle` controlling inline cell rendering.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ColumnDisplayStyle {
    Hidden,
    TextOnly,
    LineChart,
    TextOnlyScaled100,
    LineChartScaled100,
    HorizontalBar,
}

impl ColumnDisplayStyle {
    pub const ALL_NON_PERCENT: [Self; 4] = [
        Self::Hidden,
        Self::TextOnly,
        Self::LineChart,
        Self::HorizontalBar,
    ];

    pub const ALL_PERCENT: [Self; 6] = [
        Self::Hidden,
        Self::TextOnly,
        Self::LineChart,
        Self::TextOnlyScaled100,
        Self::LineChartScaled100,
        Self::HorizontalBar,
    ];

    pub const fn allowed_for(is_percentage: bool) -> &'static [Self] {
        if is_percentage {
            &Self::ALL_PERCENT
        } else {
            &Self::ALL_NON_PERCENT
        }
    }

    pub const fn label(self) -> &'static str {
        match self {
            Self::Hidden => "Hidden",
            Self::TextOnly => "Text Only",
            Self::LineChart => "Line Chart",
            Self::TextOnlyScaled100 => "Text Only (Scaled 100%)",
            Self::LineChartScaled100 => "Line Chart (Scaled 100%)",
            Self::HorizontalBar => "Horizontal Bar",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TableColumnConfig {
    pub id: CompactString,
    pub label: CompactString,
    pub is_percentage: bool,
    pub style: ColumnDisplayStyle,
}

pub fn default_process_columns() -> Vec<TableColumnConfig> {
    vec![
        TableColumnConfig {
            id: CompactString::const_new("name"),
            label: CompactString::const_new("Application / Process"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("impact"),
            label: CompactString::const_new("Impact"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("cpu"),
            label: CompactString::const_new("CPU %"),
            is_percentage: true,
            style: ColumnDisplayStyle::LineChart,
        },
        TableColumnConfig {
            id: CompactString::const_new("mem"),
            label: CompactString::const_new("Memory"),
            is_percentage: false,
            style: ColumnDisplayStyle::HorizontalBar,
        },
        TableColumnConfig {
            id: CompactString::const_new("group"),
            label: CompactString::const_new("Group"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("pid"),
            label: CompactString::const_new("PID"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("token"),
            label: CompactString::const_new("CreationToken"),
            is_percentage: false,
            style: ColumnDisplayStyle::Hidden,
        },
        TableColumnConfig {
            id: CompactString::const_new("actions"),
            label: CompactString::const_new("Actions"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
    ]
}

pub fn default_app_columns() -> Vec<TableColumnConfig> {
    vec![
        TableColumnConfig {
            id: CompactString::const_new("name"),
            label: CompactString::const_new("App Name"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("version"),
            label: CompactString::const_new("Version"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("size"),
            label: CompactString::const_new("Size"),
            is_percentage: false,
            style: ColumnDisplayStyle::HorizontalBar,
        },
        TableColumnConfig {
            id: CompactString::const_new("cpu"),
            label: CompactString::const_new("CPU %"),
            is_percentage: true,
            style: ColumnDisplayStyle::LineChartScaled100,
        },
        TableColumnConfig {
            id: CompactString::const_new("mem"),
            label: CompactString::const_new("Memory"),
            is_percentage: false,
            style: ColumnDisplayStyle::HorizontalBar,
        },
        TableColumnConfig {
            id: CompactString::const_new("bundle_id"),
            label: CompactString::const_new("Bundle ID"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
        TableColumnConfig {
            id: CompactString::const_new("inspect"),
            label: CompactString::const_new("Inspect"),
            is_percentage: false,
            style: ColumnDisplayStyle::TextOnly,
        },
    ]
}

impl WorkspaceProfile {
    pub fn save_to(&self, path: &Path) -> std::io::Result<()> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(self).map_err(std::io::Error::other)?;
        fs::write(path, json)
    }

    pub fn load_from(path: &Path) -> std::io::Result<Self> {
        let raw = fs::read_to_string(path)?;
        serde_json::from_str(&raw)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    }
}

/// Face 1: `VfdSegmentedMeter`.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct VfdSegmentGeometry {
    pub segment_count: u32,
    pub segment_thickness: f64,
    pub centering_inset: f64,
    pub glow_inset: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct VfdSegmentedMeterState {
    pub target_level: f64,
    pub displayed_level: f64,
}

impl Default for VfdSegmentedMeterState {
    fn default() -> Self {
        Self::new()
    }
}

impl VfdSegmentedMeterState {
    pub const fn new() -> Self {
        Self {
            target_level: 0.0,
            displayed_level: 0.0,
        }
    }

    pub fn compute_geometry(
        total_length: f64,
        max_segment_thickness: f64,
        gap: f64,
        cross_thickness: f64,
    ) -> VfdSegmentGeometry {
        let len = if total_length.is_finite() && total_length > 1.0 {
            total_length
        } else {
            1.0
        };
        let max_thick = if !max_segment_thickness.is_finite() {
            8.0
        } else {
            max_segment_thickness.max(1.0)
        };
        let g = if !gap.is_finite() { 2.0 } else { gap.max(0.0) };

        let raw_count = ((len + g) / (max_thick + g)) as u32;
        let segment_count = raw_count.max(1);
        let segment_thickness = len.min(max_thick);
        let used = g * f64::from(segment_count - 1) + segment_thickness * f64::from(segment_count);
        let centering_inset = ((len - used) * 0.5).max(0.0);

        let min_dim = segment_thickness.min(cross_thickness.max(1.0));
        let glow_inset = (min_dim * 2.2).clamp(8.0, 22.0);

        VfdSegmentGeometry {
            segment_count,
            segment_thickness,
            centering_inset,
            glow_inset,
        }
    }

    pub fn step(&mut self, target: f64, dt_seconds: f64, snap_immediately: bool) -> f64 {
        let clamped_target = if target.is_finite() {
            target.clamp(0.0, 1.0)
        } else {
            0.0
        };
        self.target_level = clamped_target;

        if snap_immediately || !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            self.displayed_level = clamped_target;
            return self.displayed_level;
        }

        let tau = if clamped_target >= self.displayed_level {
            constants::VFD_ATTACK_SECONDS
        } else {
            constants::VFD_DECAY_SECONDS
        };
        let factor = 1.0 - (-dt_seconds / tau).exp();
        self.displayed_level += (clamped_target - self.displayed_level) * factor;
        if (self.displayed_level - clamped_target).abs() < 1e-4 {
            self.displayed_level = clamped_target;
        }
        self.displayed_level
    }
}

/// Sample point stored inside `CompressedHistoryGraph`.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct TimedHistorySample {
    pub sampled_at_sec: f64,
    pub value: Option<f64>,
    pub break_before: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct BezierSegment {
    pub p0: [f64; 2],
    pub c1: [f64; 2],
    pub c2: [f64; 2],
    pub p1: [f64; 2],
    pub is_straight: bool,
}

/// Face 2: `CompressedHistoryGraph` with 4-band non-linear time compression and EMA gap subpaths.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompressedHistoryGraph {
    pub samples: Vec<TimedHistorySample>,
    pub capacity: usize,
    pub interval_ema_sec: f64,
    pub missing_observation_pending: bool,
    pub forced_break_pending: bool,
}

impl Default for CompressedHistoryGraph {
    fn default() -> Self {
        Self::new(512)
    }
}

impl CompressedHistoryGraph {
    pub fn new(capacity: usize) -> Self {
        Self {
            samples: Vec::with_capacity(capacity.max(16)),
            capacity: capacity.max(16),
            interval_ema_sec: 1.0,
            missing_observation_pending: false,
            forced_break_pending: false,
        }
    }

    pub fn push_metric(&mut self, sampled_at_sec: f64, metric: &Metric<f64>) {
        let value = match metric.reading {
            MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
            _ => None,
        };
        self.push_raw(sampled_at_sec, value);
    }

    pub fn push_raw(&mut self, sampled_at_sec: f64, value: Option<f64>) {
        let mut break_before = self.forced_break_pending || self.missing_observation_pending;
        if let Some(last) = self.samples.last() {
            let dt = (sampled_at_sec - last.sampled_at_sec).max(0.0);
            if dt > 4.25 * self.interval_ema_sec {
                break_before = true;
            } else if dt <= 1.5 * self.interval_ema_sec {
                let clamped_dt = dt.max(0.03);
                self.interval_ema_sec = (0.2 * clamped_dt + 0.8 * self.interval_ema_sec).max(0.03);
            }
        }

        if value.is_none() {
            self.missing_observation_pending = true;
        } else {
            self.missing_observation_pending = false;
            self.forced_break_pending = false;
        }

        if self.samples.len() >= self.capacity {
            self.samples.remove(0);
        }
        self.samples.push(TimedHistorySample {
            sampled_at_sec,
            value,
            break_before,
        });
    }

    /// Solves $1 + r + r^2 + r^3 = M$ via 64-step binary search.
    pub fn history_band_ratio(multiplier: f64) -> f64 {
        if !multiplier.is_finite() || multiplier <= 1.0 {
            return 0.0;
        }
        let mut lo = 0.0_f64;
        let mut hi = multiplier.max(1.0);
        for _ in 0..64 {
            let mid = (lo + hi) * 0.5;
            let poly = 1.0 + mid + mid * mid + mid * mid * mid;
            if multiplier <= poly {
                hi = mid;
            } else {
                lo = mid;
            }
        }
        (lo + hi) * 0.5
    }

    /// Computes non-linearly compressed normalized age across 4 bands.
    pub fn compressed_age_at_distance(normalized_distance: f64, multiplier: f64) -> f64 {
        if !normalized_distance.is_finite()
            || normalized_distance <= 0.0
            || !multiplier.is_finite()
            || multiplier <= 1.0
        {
            return normalized_distance;
        }
        let r = Self::history_band_ratio(multiplier);
        if r <= 0.0 {
            return normalized_distance;
        }
        let r2 = r * r;
        let r3 = r2 * r;
        let (band_start, base_age, s0, s1) = if normalized_distance <= 0.25 {
            (0.0, 0.0, 1.0, 1.0)
        } else if normalized_distance <= 0.50 {
            (0.25, 0.25, 1.0, r)
        } else if normalized_distance <= 0.75 {
            (0.50, 0.25 + 0.25 * r, r, r2)
        } else if normalized_distance <= 1.00 {
            (0.75, 0.25 + 0.25 * r + 0.25 * r2, r2, r3)
        } else {
            return 0.25 * (1.0 + r + r2 + r3) + r3 * (normalized_distance - 1.0);
        };

        let u = ((normalized_distance - band_start) * 4.0).clamp(0.0, 1.0);
        let u2 = u * u;
        let u3 = u2 * u;
        let u4 = u3 * u;
        let mid_s = (s0 + s1) * 0.5;
        let blend = (-0.5 * u4 + u3) * (s1 - s0) + u * s0 + (-2.0 * u3 + 3.0 * u2) * (s1 - mid_s);
        base_age + blend * 0.25
    }

    /// Maps an age in seconds (with newest sample at `x = 1.0`) onto `[0.0, 1.0]` using `compressed_age_at_distance`
    /// binary inversion so recent 25% of graph width holds highest-resolution recent samples!
    pub fn map_age_to_normalized_x(age_sec: f64, max_window_sec: f64, multiplier: f64) -> f64 {
        if !age_sec.is_finite() || age_sec <= 0.0 || max_window_sec <= 0.0 {
            return 1.0;
        }
        let max_age_ratio = Self::compressed_age_at_distance(1.0, multiplier).max(1e-5);
        let target_ratio = (age_sec / max_window_sec) * max_age_ratio;
        let mut lo = 0.0_f64;
        let mut hi = 1.0_f64;
        for _ in 0..24 {
            let mid = (lo + hi) * 0.5;
            if Self::compressed_age_at_distance(mid, multiplier) < target_ratio {
                lo = mid;
            } else {
                hi = mid;
            }
        }
        (1.0 - (lo + hi) * 0.5).clamp(0.0, 1.0)
    }

    /// Builds disconnected Catmull-Rom / cubic Bezier subpaths broken cleanly at unavailable samples or gaps.
    pub fn build_curve_subpaths(&self) -> Vec<Vec<BezierSegment>> {
        let mut subpaths: Vec<Vec<[f64; 2]>> = Vec::new();
        for s in &self.samples {
            let Some(val) = s.value else {
                continue;
            };
            if s.break_before || subpaths.is_empty() {
                subpaths.push(vec![[s.sampled_at_sec, val]]);
            } else if let Some(cur) = subpaths.last_mut() {
                cur.push([s.sampled_at_sec, val]);
            }
        }

        let mut out = Vec::with_capacity(subpaths.len());
        for pts in subpaths {
            if pts.len() < 2 {
                continue;
            }
            let mut segs = Vec::with_capacity(pts.len() - 1);
            for i in 0..(pts.len() - 1) {
                let p0 = pts[i];
                let p1 = pts[i + 1];
                let is_straight = (p1[1] - p0[1]).abs() <= 0.001;
                if is_straight {
                    let dx = (p1[0] - p0[0]) / 3.0;
                    segs.push(BezierSegment {
                        p0,
                        c1: [p0[0] + dx, p0[1]],
                        c2: [p1[0] - dx, p1[1]],
                        p1,
                        is_straight: true,
                    });
                } else {
                    let prev = if i > 0 { pts[i - 1] } else { p0 };
                    let next = if i + 2 < pts.len() { pts[i + 2] } else { p1 };
                    let min_y = p0[1].min(p1[1]);
                    let max_y = p0[1].max(p1[1]);
                    let c1x = p0[0] + (p1[0] - p0[0]) / 3.0;
                    let c2x = p1[0] - (p1[0] - p0[0]) / 3.0;
                    let c1y = (p0[1] + (p1[1] - prev[1]) / 6.0).clamp(min_y, max_y);
                    let c2y = (p1[1] - (next[1] - p0[1]) / 6.0).clamp(min_y, max_y);
                    segs.push(BezierSegment {
                        p0,
                        c1: [c1x, c1y],
                        c2: [c2x, c2y],
                        p1,
                        is_straight: false,
                    });
                }
            }
            out.push(segs);
        }
        out
    }
}

/// Face 3: `ProcessorClusterGrid`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProcessorClusterGridState {
    pub p_core_count: u32,
    pub e_core_count: u32,
    pub p_cluster_residency_pct: Option<f64>,
    pub e_cluster_residency_pct: Option<f64>,
    pub gpu_utilization_pct: Option<f64>,
    pub ane_residency_pct: Option<f64>,
    pub package_watts: Option<f64>,
}

impl ProcessorClusterGridState {
    pub fn from_snapshot(snap: &SystemSnapshot) -> Self {
        let val = |m: &Metric<f64>| match m.reading {
            MetricReading::Measured { value, .. } => Some(value),
            _ => None,
        };
        let sp = &snap.hardware_telemetry.soc_power;
        Self {
            p_core_count: snap.system.performance_core_count,
            e_core_count: snap.system.efficiency_core_count,
            p_cluster_residency_pct: val(&sp.p_cluster_active_residency_percent),
            e_cluster_residency_pct: val(&sp.e_cluster_active_residency_percent),
            gpu_utilization_pct: val(&snap.system.gpu_utilization_percent),
            ane_residency_pct: val(&sp.ane_active_residency_percent),
            package_watts: val(&sp.package_watts),
        }
    }
}

/// Faces 4 & 5 (`DiskOceanMetaballs` Mode 1 & `DiskDefragBlocks` Mode 2): builds `GpuDiskBlob` storage buffers.
pub fn build_disk_shader_blobs(report: &DiskScanReport) -> Vec<GpuDiskBlob> {
    let mut blobs = Vec::with_capacity(report.categories.len());
    for (idx, cat_tot) in report.categories.iter().enumerate() {
        let angle = (idx as f32) * std::f32::consts::TAU / 7.0;
        let cx = 0.5 + 0.24 * angle.cos();
        let cy = 0.5 + 0.24 * angle.sin();
        let radius = (0.06 + cat_tot.share_fraction.sqrt() * 0.22).clamp(0.04, 0.35);
        let rgb = cat_tot.category.default_color_srgb();
        let pattern = ((idx % 6) + 1) as f32;
        blobs.push(GpuDiskBlob {
            pos_rad_seed: [cx, cy, radius, (idx as f32) * 1.73],
            colour_share: [rgb[0], rgb[1], rgb[2], cat_tot.share_fraction],
            frac_pat_pad: [cat_tot.share_fraction, pattern, 0.0, 0.0],
        });
    }
    blobs
}

/// Face 6 (`VectorTunnelTempest`): builds instanced `GpuTunnelSegment` capsules for `shaders/vector_tunnel.wgsl`.
pub fn build_vector_tunnel_segments(
    viewport_width: f32,
    viewport_height: f32,
    time_sec: f32,
    ring_count: usize,
) -> Vec<GpuTunnelSegment> {
    let spokes = 12usize;
    let cx = viewport_width * 0.5;
    let cy = viewport_height * 0.5;
    let max_r = viewport_width.min(viewport_height) * 0.45;
    let mut segments = Vec::with_capacity(ring_count * spokes);

    for ring in 0..ring_count {
        let depth = ((ring as f32 / ring_count.max(1) as f32) + (time_sec * 0.25).fract()).fract();
        let r = (depth * depth) * max_r.max(10.0);
        let alpha = depth.clamp(0.1, 1.0);
        for s in 0..spokes {
            let a0 = (s as f32) * std::f32::consts::TAU / (spokes as f32);
            let a1 = ((s + 1) as f32) * std::f32::consts::TAU / (spokes as f32);
            segments.push(GpuTunnelSegment {
                endpoints: [
                    cx + r * a0.cos(),
                    cy + r * a0.sin(),
                    cx + r * a1.cos(),
                    cy + r * a1.sin(),
                ],
                color: [0.24, 1.0, 0.55, alpha],
                width_pad: [1.8, 0.0, 0.0, 0.0],
            });
        }
    }
    segments
}
