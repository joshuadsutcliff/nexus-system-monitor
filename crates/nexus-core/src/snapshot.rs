use crate::{
    CoherenceRepairs, Metric, MetricProvenance, MetricQuality, MetricReading, ProcessKey,
    ProviderHealthState, SourceId, UnavailableReason,
};
use compact_str::CompactString;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

/// Logical CPU cluster classification on Apple Silicon (`0x100583cc4`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum LogicalProcessorClass {
    Performance,
    Efficiency,
    Unknown,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CoreTelemetry {
    pub core_index: u32,
    pub class: LogicalProcessorClass,
    pub total_percent: Metric<f64>,
    pub user_percent: Metric<f64>,
    pub system_percent: Metric<f64>,
    pub idle_percent: Metric<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SystemMetrics {
    pub cpu_percent: Metric<f64>,
    pub cpu_user_percent: Metric<f64>,
    pub cpu_kernel_percent: Metric<f64>,
    pub performance_core_count: u32,
    pub efficiency_core_count: u32,
    pub cores: Vec<CoreTelemetry>,
    pub load_avg_1m: Metric<f64>,
    pub load_avg_5m: Metric<f64>,
    pub load_avg_15m: Metric<f64>,
    pub uptime_seconds: Metric<f64>,
    pub mem_total_bytes: Metric<f64>,
    pub mem_used_bytes: Metric<f64>,
    pub mem_active_bytes: Metric<f64>,
    pub mem_wired_bytes: Metric<f64>,
    pub mem_compressed_bytes: Metric<f64>,
    pub mem_cached_files_bytes: Metric<f64>,
    pub mem_free_bytes: Metric<f64>,
    pub mem_used_percent: Metric<f64>,
    pub swap_total_bytes: Metric<f64>,
    pub swap_used_bytes: Metric<f64>,
    pub swap_used_percent: Metric<f64>,
    pub system_pressure_percent: Metric<f64>,
    pub gpu_model: CompactString,
    pub gpu_core_count: Metric<f64>,
    pub gpu_utilization_percent: Metric<f64>,
    pub gpu_in_use_memory_bytes: Metric<f64>,
    pub gpu_alloc_memory_bytes: Metric<f64>,
}

impl SystemMetrics {
    pub fn empty_unavailable(sampled_at_ns: u64) -> Self {
        let prov = MetricProvenance {
            source: SourceId(0),
            unit_tag: None,
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Unknown,
            contributed_by_stale: false,
        };
        let unavail = Metric {
            reading: MetricReading::Unavailable(UnavailableReason::NotYetAvailable),
            provenance: prov,
        };
        Self {
            cpu_percent: unavail,
            cpu_user_percent: unavail,
            cpu_kernel_percent: unavail,
            performance_core_count: 0,
            efficiency_core_count: 0,
            cores: Vec::new(),
            load_avg_1m: unavail,
            load_avg_5m: unavail,
            load_avg_15m: unavail,
            uptime_seconds: unavail,
            mem_total_bytes: unavail,
            mem_used_bytes: unavail,
            mem_active_bytes: unavail,
            mem_wired_bytes: unavail,
            mem_compressed_bytes: unavail,
            mem_cached_files_bytes: unavail,
            mem_free_bytes: unavail,
            mem_used_percent: unavail,
            swap_total_bytes: unavail,
            swap_used_bytes: unavail,
            swap_used_percent: unavail,
            system_pressure_percent: unavail,
            gpu_model: CompactString::const_new("Unknown GPU"),
            gpu_core_count: unavail,
            gpu_utilization_percent: unavail,
            gpu_in_use_memory_bytes: unavail,
            gpu_alloc_memory_bytes: unavail,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct MediaEngineBlockMetric {
    pub id: CompactString,
    pub label: CompactString,
    pub active_residency_percent: Metric<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ClusterClockMetric {
    pub cluster_name: CompactString,
    pub is_performance: bool,
    pub effective_mhz: Metric<f64>,
    pub active_residency_percent: Metric<f64>,
}

/// Apple Silicon `libIOReport` and battery/adapter power breakdown (`0x1005e477c`, `0x1005952a8`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SocPowerMetrics {
    pub cpu_watts: Metric<f64>,
    pub gpu_watts: Metric<f64>,
    pub ane_watts: Metric<f64>,
    pub dram_watts: Metric<f64>,
    pub package_watts: Metric<f64>,
    pub system_power_in_watts: Metric<f64>,
    pub p_cluster_active_residency_percent: Metric<f64>,
    pub e_cluster_active_residency_percent: Metric<f64>,
    pub ane_active_residency_percent: Metric<f64>,
    pub e_cluster_mhz: Metric<f64>,
    pub p_cluster_mhz: Metric<f64>,
    pub gpu_clock_mhz: Metric<f64>,
    pub media_blocks: Vec<MediaEngineBlockMetric>,
    pub cluster_clocks: Vec<ClusterClockMetric>,
    pub soc_floor_residency_percent: Metric<f64>,
    pub pmp_power_watts: Metric<f64>,
}

impl SocPowerMetrics {
    pub fn empty_unavailable(sampled_at_ns: u64) -> Self {
        let prov = MetricProvenance {
            source: SourceId(0),
            unit_tag: None,
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Unknown,
            contributed_by_stale: false,
        };
        let unavail = Metric {
            reading: MetricReading::Unavailable(UnavailableReason::NotYetAvailable),
            provenance: prov,
        };
        Self {
            cpu_watts: unavail,
            gpu_watts: unavail,
            ane_watts: unavail,
            dram_watts: unavail,
            package_watts: unavail,
            system_power_in_watts: unavail,
            p_cluster_active_residency_percent: unavail,
            e_cluster_active_residency_percent: unavail,
            ane_active_residency_percent: unavail,
            e_cluster_mhz: unavail,
            p_cluster_mhz: unavail,
            gpu_clock_mhz: unavail,
            media_blocks: Vec::new(),
            cluster_clocks: Vec::new(),
            soc_floor_residency_percent: unavail,
            pmp_power_watts: unavail,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ThermalSensorReading {
    pub name: CompactString,
    pub celsius: Metric<f64>,
}

/// Thermal warning level and sensor readings (`0x1005952a8`, `0x1005daf38`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ThermalMetrics {
    pub warning_level: Metric<f64>,
    pub max_die_celsius: Metric<f64>,
    pub sensors: Vec<ThermalSensorReading>,
}

impl ThermalMetrics {
    pub fn empty_unavailable(sampled_at_ns: u64) -> Self {
        let prov = MetricProvenance {
            source: SourceId(0),
            unit_tag: None,
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Unknown,
            contributed_by_stale: false,
        };
        let unavail = Metric {
            reading: MetricReading::Unavailable(UnavailableReason::NotYetAvailable),
            provenance: prov,
        };
        Self {
            warning_level: unavail,
            max_die_celsius: unavail,
            sensors: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DiskSnapshot {
    pub bsd_name: CompactString,
    pub read_bytes_total: Metric<f64>,
    pub write_bytes_total: Metric<f64>,
    pub read_ops_total: Metric<f64>,
    pub write_ops_total: Metric<f64>,
    pub read_bytes_per_sec: Metric<f64>,
    pub write_bytes_per_sec: Metric<f64>,
    pub root_total_bytes: Metric<f64>,
    pub root_available_bytes: Metric<f64>,
    pub root_used_percent: Metric<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct NetworkSnapshot {
    pub interface: CompactString,
    pub rx_bytes_total: Metric<f64>,
    pub tx_bytes_total: Metric<f64>,
    pub rx_packets_total: Metric<f64>,
    pub tx_packets_total: Metric<f64>,
    pub rx_bytes_per_sec: Metric<f64>,
    pub tx_bytes_per_sec: Metric<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProcessSnapshot {
    pub key: ProcessKey,
    pub ppid: i32,
    pub name: CompactString,
    pub cpu_percent: Metric<f64>,
    pub phys_footprint_bytes: Metric<f64>,
    pub resident_bytes: Metric<f64>,
    pub disk_read_bytes: Metric<f64>,
    pub disk_write_bytes: Metric<f64>,
    pub exited_at_ns: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderHealth {
    pub id: CompactString,
    pub state: ProviderHealthState,
    pub consecutive_failures: u32,
    pub last_success_at_ns: u64,
    pub last_error: Option<CompactString>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct HardwareTelemetryFrame {
    pub generation: u64,
    pub sampled_at_ns: u64,
    pub soc_power: SocPowerMetrics,
    pub thermals: ThermalMetrics,
}

/// Complete coherent telemetry snapshot (`SystemSnapshot`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SystemSnapshot {
    pub schema_version: u32,
    pub generation: u64,
    pub sampled_at_ns: u64,
    pub sources: Vec<CompactString>,
    pub system: SystemMetrics,
    pub hardware_telemetry: HardwareTelemetryFrame,
    pub disks: Vec<DiskSnapshot>,
    pub networks: Vec<NetworkSnapshot>,
    pub processes: Vec<ProcessSnapshot>,
    pub exited_processes: Vec<ProcessSnapshot>,
    pub provider_health: Vec<ProviderHealth>,
    pub coherence_repairs: CoherenceRepairs,
}

impl SystemSnapshot {
    pub fn new_empty(generation: u64, sampled_at_ns: u64) -> Self {
        Self {
            schema_version: 1,
            generation,
            sampled_at_ns,
            sources: vec![CompactString::const_new("core.init")],
            system: SystemMetrics::empty_unavailable(sampled_at_ns),
            hardware_telemetry: HardwareTelemetryFrame {
                generation,
                sampled_at_ns,
                soc_power: SocPowerMetrics::empty_unavailable(sampled_at_ns),
                thermals: ThermalMetrics::empty_unavailable(sampled_at_ns),
            },
            disks: Vec::new(),
            networks: Vec::new(),
            processes: Vec::new(),
            exited_processes: Vec::new(),
            provider_health: Vec::new(),
            coherence_repairs: CoherenceRepairs::default(),
        }
    }

    /// Interns a provider source string into `self.sources` and returns its `SourceId`.
    pub fn intern_source(&mut self, name: &str) -> SourceId {
        if let Some(pos) = self.sources.iter().position(|s| s.as_str() == name) {
            return SourceId(pos as u32);
        }
        let id = SourceId(self.sources.len() as u32);
        self.sources.push(CompactString::from(name));
        id
    }

    /// Resolves a `SourceId` back to its string slice.
    pub fn source_name(&self, id: SourceId) -> &str {
        self.sources
            .get(id.0 as usize)
            .map(CompactString::as_str)
            .unwrap_or("unknown")
    }

    /// Implements `SystemSnapshot::FirstCoherenceFault()` (`0x10060d2d8`).
    pub fn first_coherence_fault(&self) -> Option<&'static str> {
        if self.schema_version != 1 {
            return Some("snapshot.schemaVersion");
        }
        if self.generation == 0 {
            return Some("snapshot.generation");
        }
        if self.hardware_telemetry.generation != self.generation {
            return Some("hardwareTelemetry.generationMismatch");
        }
        if self.hardware_telemetry.sampled_at_ns != self.sampled_at_ns {
            return Some("hardwareTelemetry.sampledAtMismatch");
        }

        if !percent_is_valid(&self.system.cpu_percent) {
            return Some("system.cpuPercent");
        }
        if !percent_is_valid(&self.system.cpu_user_percent) {
            return Some("system.cpuUserPercent");
        }
        if !percent_is_valid(&self.system.cpu_kernel_percent) {
            return Some("system.cpuKernelPercent");
        }
        if !percent_is_valid(&self.system.mem_used_percent) {
            return Some("system.memUsedPercent");
        }
        if !percent_is_valid(&self.system.swap_used_percent) {
            return Some("system.swapUsedPercent");
        }
        if !percent_is_valid(&self.system.system_pressure_percent) {
            return Some("system.systemPressurePercent");
        }
        if !percent_is_valid(&self.system.gpu_utilization_percent) {
            return Some("system.gpuUtilizationPercent");
        }

        let mut proc_keys = HashSet::with_capacity(self.processes.len());
        for p in &self.processes {
            if !proc_keys.insert(p.key) {
                return Some("processes.duplicateKey");
            }
        }

        let mut disk_keys = HashSet::with_capacity(self.disks.len());
        for d in &self.disks {
            if !disk_keys.insert(d.bsd_name.as_str()) {
                return Some("devices.duplicateKey");
            }
        }

        let mut health_keys = HashSet::with_capacity(self.provider_health.len());
        for h in &self.provider_health {
            if h.id.is_empty() {
                return Some("providerHealth.emptyId");
            }
            if !health_keys.insert(h.id.as_str()) {
                return Some("providerHealth.duplicateKey");
            }
            if h.last_success_at_ns > self.sampled_at_ns {
                return Some("providerHealth.successInFuture");
            }
        }

        None
    }
}

#[inline]
fn percent_is_valid(metric: &Metric<f64>) -> bool {
    if !metric.is_consistent() {
        return false;
    }
    match metric.reading {
        MetricReading::Measured { value, .. } => (0.0..=100.0).contains(&value),
        MetricReading::Unavailable(_) => true,
    }
}

/// Implements `RepairSnapshotCoherence` (`0x10060b0dc`) and `RepairPercent` (`0x10060d098`).
pub fn repair_snapshot_coherence(snapshot: &mut SystemSnapshot) -> CoherenceRepairs {
    let mut repairs = CoherenceRepairs::default();

    snapshot.system.cpu_percent.repair_percent(&mut repairs);
    snapshot
        .system
        .cpu_user_percent
        .repair_percent(&mut repairs);
    snapshot
        .system
        .cpu_kernel_percent
        .repair_percent(&mut repairs);
    snapshot
        .system
        .mem_used_percent
        .repair_percent(&mut repairs);
    snapshot
        .system
        .swap_used_percent
        .repair_percent(&mut repairs);
    snapshot
        .system
        .system_pressure_percent
        .repair_percent(&mut repairs);
    snapshot
        .system
        .gpu_utilization_percent
        .repair_percent(&mut repairs);

    for core in &mut snapshot.system.cores {
        core.total_percent.repair_percent(&mut repairs);
        core.user_percent.repair_percent(&mut repairs);
        core.system_percent.repair_percent(&mut repairs);
        core.idle_percent.repair_percent(&mut repairs);
    }

    snapshot
        .hardware_telemetry
        .soc_power
        .p_cluster_active_residency_percent
        .repair_percent(&mut repairs);
    snapshot
        .hardware_telemetry
        .soc_power
        .e_cluster_active_residency_percent
        .repair_percent(&mut repairs);
    snapshot
        .hardware_telemetry
        .soc_power
        .ane_active_residency_percent
        .repair_percent(&mut repairs);
    snapshot
        .hardware_telemetry
        .soc_power
        .soc_floor_residency_percent
        .repair_percent(&mut repairs);
    for block in &mut snapshot.hardware_telemetry.soc_power.media_blocks {
        block.active_residency_percent.repair_percent(&mut repairs);
    }
    for cluster in &mut snapshot.hardware_telemetry.soc_power.cluster_clocks {
        cluster
            .active_residency_percent
            .repair_percent(&mut repairs);
    }

    for disk in &mut snapshot.disks {
        disk.root_used_percent.repair_percent(&mut repairs);
    }

    let mut seen_procs = HashSet::with_capacity(snapshot.processes.len());
    snapshot.processes.retain(|p| {
        if seen_procs.insert(p.key) {
            true
        } else {
            repairs.duplicate_rows_dropped += 1;
            false
        }
    });

    for proc in &mut snapshot.processes {
        if let MetricReading::Measured { value, stale } = proc.cpu_percent.reading {
            if !value.is_finite() {
                proc.cpu_percent.reading =
                    MetricReading::Unavailable(UnavailableReason::TransientError);
                repairs.marked_unavailable += 1;
            } else if value < 0.0 {
                proc.cpu_percent.reading = MetricReading::Measured { value: 0.0, stale };
                repairs.percentages_clamped += 1;
            }
        }
    }

    snapshot.coherence_repairs = repairs;
    repairs
}
