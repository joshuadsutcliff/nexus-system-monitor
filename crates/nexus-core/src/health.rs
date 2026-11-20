use crate::{MetricReading, ProcessKey, SystemSnapshot};
use compact_str::CompactString;
use serde::{Deserialize, Serialize};
use std::collections::VecDeque;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TrendDirection {
    Improving,
    Stable,
    Degrading,
}

impl TrendDirection {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Improving => "Improving",
            Self::Stable => "Stable",
            Self::Degrading => "Degrading",
        }
    }
}

/// Rolling in-memory tracker of overall composite health scores across snapshots.
#[derive(Debug, Clone)]
pub struct SystemHealthTracker {
    capacity: usize,
    history: VecDeque<(u64, f64)>,
}

impl Default for SystemHealthTracker {
    fn default() -> Self {
        Self::new(180)
    }
}

impl SystemHealthTracker {
    pub fn new(capacity: usize) -> Self {
        let cap = capacity.max(4);
        Self {
            capacity: cap,
            history: VecDeque::with_capacity(cap),
        }
    }

    pub fn record(&mut self, sampled_at_ns: u64, score: f64) {
        if !score.is_finite() {
            return;
        }
        if self.history.len() >= self.capacity {
            self.history.pop_front();
        }
        self.history
            .push_back((sampled_at_ns, score.clamp(0.0, 100.0)));
    }

    pub fn points(&self) -> &VecDeque<(u64, f64)> {
        &self.history
    }

    pub fn latest_score(&self) -> Option<f64> {
        self.history.back().map(|&(_, s)| s)
    }

    /// Compares the newer half of the rolling window against the older half:
    /// `delta > 3.0` -> `Improving`, `delta < -3.0` -> `Degrading`, else `Stable`.
    pub fn trend(&self) -> TrendDirection {
        let len = self.history.len();
        if len < 4 {
            return TrendDirection::Stable;
        }
        let mid = len / 2;
        let mut old_sum = 0.0_f64;
        for &(_, val) in self.history.iter().take(mid) {
            old_sum += val;
        }
        let mut new_sum = 0.0_f64;
        for &(_, val) in self.history.iter().take(len).skip(mid) {
            new_sum += val;
        }
        let old_avg = old_sum / (mid as f64);
        let new_avg = new_sum / ((len - mid) as f64);
        let delta = new_avg - old_avg;
        if delta > 3.0 {
            TrendDirection::Improving
        } else if delta < -3.0 {
            TrendDirection::Degrading
        } else {
            TrendDirection::Stable
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum HealthLevel {
    Excellent,
    Good,
    Fair,
    Poor,
    Critical,
}

impl HealthLevel {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Excellent => "Excellent",
            Self::Good => "Good",
            Self::Fair => "Fair",
            Self::Poor => "Poor",
            Self::Critical => "Critical",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BottleneckType {
    Idle,
    Balanced,
    ThermalThrottle,
    VramBound,
    GpuBound,
    CpuBound,
    MemoryBound,
    StorageBound,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BottleneckSeverity {
    None,
    Mild,
    Moderate,
    Severe,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SubsystemHealthScore {
    pub name: CompactString,
    pub score: Option<f64>,
    pub current_value: Option<f64>,
    pub unit: CompactString,
    pub level: Option<HealthLevel>,
    pub summary: CompactString,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BottleneckReport {
    pub bottleneck: BottleneckType,
    pub severity: BottleneckSeverity,
    pub headline: CompactString,
    pub explanation: CompactString,
    pub upgrade_advice: CompactString,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TopConsumerEntry {
    pub key: ProcessKey,
    pub name: CompactString,
    pub cpu_percent: Option<f64>,
    pub memory_bytes: Option<f64>,
    pub impact_score: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SystemHealthReport {
    pub overall_score: f64,
    pub overall_level: HealthLevel,
    pub cpu: SubsystemHealthScore,
    pub memory: SubsystemHealthScore,
    pub disk: SubsystemHealthScore,
    pub gpu: SubsystemHealthScore,
    pub thermal: SubsystemHealthScore,
    pub bottleneck: BottleneckReport,
    pub top_consumers: Vec<TopConsumerEntry>,
}

/// Stateless health scoring functions preserving Nexus weightings and honest missing-GPU exclusion.
/// Composite weights: CPU 30% + Memory 25% + Disk 20% + GPU 15% + Thermal 10%
/// (renormalized over available subsystems when GPU or thermal telemetry is unavailable).
pub struct HealthScoring;

impl HealthScoring {
    pub const CPU_WEIGHT: f64 = 0.30;
    pub const MEM_WEIGHT: f64 = 0.25;
    pub const DISK_WEIGHT: f64 = 0.20;
    pub const GPU_WEIGHT: f64 = 0.15;
    pub const THERMAL_WEIGHT: f64 = 0.10;

    pub fn score_cpu(cpu_percent: f64) -> f64 {
        if cpu_percent <= 50.0 {
            100.0 - cpu_percent * 0.4
        } else if cpu_percent <= 80.0 {
            80.0 - (cpu_percent - 50.0) * (40.0 / 30.0)
        } else if cpu_percent <= 95.0 {
            40.0 - (cpu_percent - 80.0) * (30.0 / 15.0)
        } else {
            (10.0 - (cpu_percent - 95.0) * 2.0).max(0.0)
        }
    }

    pub fn score_memory(mem_percent: f64) -> f64 {
        if mem_percent <= 60.0 {
            100.0 - mem_percent * (20.0 / 60.0)
        } else if mem_percent <= 80.0 {
            80.0 - (mem_percent - 60.0) * (40.0 / 20.0)
        } else if mem_percent <= 90.0 {
            40.0 - (mem_percent - 80.0) * (30.0 / 10.0)
        } else {
            (10.0 - (mem_percent - 90.0)).max(0.0)
        }
    }

    pub fn score_disk(disk_used_percent: f64) -> f64 {
        if disk_used_percent <= 70.0 {
            100.0
        } else if disk_used_percent <= 85.0 {
            100.0 - (disk_used_percent - 70.0) * (30.0 / 15.0)
        } else if disk_used_percent <= 95.0 {
            70.0 - (disk_used_percent - 85.0) * (50.0 / 10.0)
        } else {
            (20.0 - (disk_used_percent - 95.0) * 4.0).max(0.0)
        }
    }

    pub fn score_gpu(gpu_percent: f64) -> f64 {
        if gpu_percent <= 70.0 {
            100.0 - gpu_percent * 0.2
        } else if gpu_percent <= 90.0 {
            86.0 - (gpu_percent - 70.0) * (36.0 / 20.0)
        } else {
            (50.0 - (gpu_percent - 90.0) * 3.0).max(20.0)
        }
    }

    pub fn score_thermal(max_temp_c: f64) -> f64 {
        let warn_at = 80.0;
        let crit_at = 95.0;
        if max_temp_c <= warn_at {
            100.0 - (max_temp_c / warn_at) * 20.0
        } else if max_temp_c <= crit_at {
            80.0 - (max_temp_c - warn_at) / (crit_at - warn_at) * 60.0
        } else {
            (20.0 - (max_temp_c - crit_at) * 2.0).max(0.0)
        }
    }

    pub const fn level_for_score(score: f64) -> HealthLevel {
        if score >= 80.0 {
            HealthLevel::Excellent
        } else if score >= 60.0 {
            HealthLevel::Good
        } else if score >= 40.0 {
            HealthLevel::Fair
        } else if score >= 20.0 {
            HealthLevel::Poor
        } else {
            HealthLevel::Critical
        }
    }

    pub fn evaluate_snapshot(snapshot: &SystemSnapshot) -> SystemHealthReport {
        let cpu_val = measured_val(&snapshot.system.cpu_percent);
        let mem_val = measured_val(&snapshot.system.mem_used_percent);
        let gpu_val = measured_val(&snapshot.system.gpu_utilization_percent);
        let disk_used = snapshot
            .disks
            .first()
            .and_then(|d| measured_val(&d.root_used_percent));
        let temp_val = measured_val(&snapshot.hardware_telemetry.thermals.max_die_celsius);

        let mut weighted_sum = 0.0;
        let mut active_weight = 0.0;

        let cpu_score = cpu_val.map(Self::score_cpu);
        if let Some(s) = cpu_score {
            weighted_sum += s * Self::CPU_WEIGHT;
            active_weight += Self::CPU_WEIGHT;
        }

        let mem_score = mem_val.map(Self::score_memory);
        if let Some(s) = mem_score {
            weighted_sum += s * Self::MEM_WEIGHT;
            active_weight += Self::MEM_WEIGHT;
        }

        let disk_score = disk_used.map(Self::score_disk);
        if let Some(s) = disk_score {
            weighted_sum += s * Self::DISK_WEIGHT;
            active_weight += Self::DISK_WEIGHT;
        }

        let gpu_score = gpu_val.map(Self::score_gpu);
        if let Some(s) = gpu_score {
            weighted_sum += s * Self::GPU_WEIGHT;
            active_weight += Self::GPU_WEIGHT;
        }

        let thermal_score = temp_val.map(Self::score_thermal);
        if let Some(s) = thermal_score {
            weighted_sum += s * Self::THERMAL_WEIGHT;
            active_weight += Self::THERMAL_WEIGHT;
        }

        let overall_score = if active_weight > 0.0 {
            (weighted_sum / active_weight).clamp(0.0, 100.0)
        } else {
            100.0
        };
        let overall_level = Self::level_for_score(overall_score);

        let bottleneck = BottleneckDetector::analyse(snapshot);

        let mut consumers: Vec<TopConsumerEntry> = snapshot
            .processes
            .iter()
            .map(|p| {
                let c = measured_val(&p.cpu_percent);
                let m = measured_val(&p.phys_footprint_bytes);
                let m_gib = m.unwrap_or(0.0) / (1024.0 * 1024.0 * 1024.0);
                let impact = c.unwrap_or(0.0) * 1.5 + m_gib * 10.0;
                TopConsumerEntry {
                    key: p.key,
                    name: p.name.clone(),
                    cpu_percent: c,
                    memory_bytes: m,
                    impact_score: impact,
                }
            })
            .collect();
        consumers.sort_by(|a, b| {
            b.impact_score
                .partial_cmp(&a.impact_score)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        consumers.truncate(5);

        SystemHealthReport {
            overall_score,
            overall_level,
            cpu: SubsystemHealthScore {
                name: CompactString::const_new("CPU"),
                score: cpu_score,
                current_value: cpu_val,
                unit: CompactString::const_new("%"),
                level: cpu_score.map(Self::level_for_score),
                summary: match cpu_val {
                    Some(v) => CompactString::from(format!("{v:.1}% total CPU load")),
                    None => CompactString::const_new("CPU load pending warm-up"),
                },
            },
            memory: SubsystemHealthScore {
                name: CompactString::const_new("Memory"),
                score: mem_score,
                current_value: mem_val,
                unit: CompactString::const_new("%"),
                level: mem_score.map(Self::level_for_score),
                summary: match mem_val {
                    Some(v) => CompactString::from(format!("{v:.1}% RAM allocated")),
                    None => CompactString::const_new("Memory telemetry unavailable"),
                },
            },
            disk: SubsystemHealthScore {
                name: CompactString::const_new("Disk"),
                score: disk_score,
                current_value: disk_used,
                unit: CompactString::const_new("%"),
                level: disk_score.map(Self::level_for_score),
                summary: match disk_used {
                    Some(v) => CompactString::from(format!("{v:.1}% root volume used")),
                    None => CompactString::const_new("Disk capacity unavailable"),
                },
            },
            gpu: SubsystemHealthScore {
                name: CompactString::const_new("GPU"),
                score: gpu_score,
                current_value: gpu_val,
                unit: CompactString::const_new("%"),
                level: gpu_score.map(Self::level_for_score),
                summary: match gpu_val {
                    Some(v) => CompactString::from(format!("{v:.1}% GPU utilization")),
                    None => CompactString::const_new("Excluded from composite (unavailable)"),
                },
            },
            thermal: SubsystemHealthScore {
                name: CompactString::const_new("Thermal"),
                score: thermal_score,
                current_value: temp_val,
                unit: CompactString::const_new("C"),
                level: thermal_score.map(Self::level_for_score),
                summary: match temp_val {
                    Some(v) => CompactString::from(format!("{v:.1} C max die")),
                    None => CompactString::const_new("Thermal sensors unavailable"),
                },
            },
            bottleneck,
            top_consumers: consumers,
        }
    }
}

pub struct BottleneckDetector;

impl BottleneckDetector {
    const MAXED_HIGH: f64 = 92.0;
    const MAXED_MODERATE: f64 = 80.0;
    const IDLE_THRESHOLD: f64 = 30.0;
    const MIN_WORKLOAD_LOAD: f64 = 35.0;

    pub fn analyse(snapshot: &SystemSnapshot) -> BottleneckReport {
        let cpu = measured_val(&snapshot.system.cpu_percent).unwrap_or(0.0);
        let gpu = measured_val(&snapshot.system.gpu_utilization_percent).unwrap_or(0.0);
        let mem = measured_val(&snapshot.system.mem_used_percent).unwrap_or(0.0);
        let temp = measured_val(&snapshot.hardware_telemetry.thermals.max_die_celsius);

        if temp.is_some_and(|t| t > 95.0) {
            return BottleneckReport {
                bottleneck: BottleneckType::ThermalThrottle,
                severity: BottleneckSeverity::Severe,
                headline: CompactString::const_new("Thermal throttling detected"),
                explanation: CompactString::from(format!(
                    "Die temperature reached {:.1} C, exceeding the 95 C thermal envelope.",
                    temp.unwrap_or(0.0)
                )),
                upgrade_advice: CompactString::const_new(
                    "Improve airflow, clean vents, or reduce sustained power limits.",
                ),
            };
        }

        if cpu.max(gpu) < Self::MIN_WORKLOAD_LOAD && mem <= Self::MAXED_HIGH {
            return BottleneckReport {
                bottleneck: BottleneckType::Idle,
                severity: BottleneckSeverity::None,
                headline: CompactString::const_new("No significant workload detected"),
                explanation: CompactString::const_new(
                    "System CPU and GPU loads are within idle headroom.",
                ),
                upgrade_advice: CompactString::const_new(""),
            };
        }

        if mem > Self::MAXED_HIGH {
            let sev = if mem > 97.0 {
                BottleneckSeverity::Severe
            } else {
                BottleneckSeverity::Moderate
            };
            return BottleneckReport {
                bottleneck: BottleneckType::MemoryBound,
                severity: sev,
                headline: CompactString::from(format!("Memory bottleneck - RAM is {mem:.0}% full")),
                explanation: CompactString::const_new(
                    "Physical memory is nearly exhausted; compression or swap thrashing is likely.",
                ),
                upgrade_advice: CompactString::const_new(
                    "Close memory-heavy background apps or add more unified/system RAM.",
                ),
            };
        }

        if gpu >= Self::MAXED_HIGH && cpu < Self::MAXED_MODERATE - 10.0 {
            let sev = if cpu < Self::IDLE_THRESHOLD {
                BottleneckSeverity::Severe
            } else {
                BottleneckSeverity::Moderate
            };
            return BottleneckReport {
                bottleneck: BottleneckType::GpuBound,
                severity: sev,
                headline: CompactString::from(format!(
                    "GPU bottleneck ({gpu:.0}% GPU, {cpu:.0}% CPU)"
                )),
                explanation: CompactString::const_new(
                    "GPU compute is saturated while CPU retains scheduling headroom.",
                ),
                upgrade_advice: CompactString::const_new(
                    "Lower shader/render resolution settings or step up GPU tier.",
                ),
            };
        }

        if cpu >= Self::MAXED_HIGH && gpu < Self::MAXED_MODERATE - 10.0 {
            let sev = if gpu < Self::IDLE_THRESHOLD {
                BottleneckSeverity::Severe
            } else {
                BottleneckSeverity::Moderate
            };
            return BottleneckReport {
                bottleneck: BottleneckType::CpuBound,
                severity: sev,
                headline: CompactString::from(format!(
                    "CPU bottleneck ({cpu:.0}% CPU, {gpu:.0}% GPU)"
                )),
                explanation: CompactString::const_new(
                    "CPU cores are saturated while GPU waits for command submission.",
                ),
                upgrade_advice: CompactString::const_new(
                    "Enable Auto-Balance or throttle noisy background processes.",
                ),
            };
        }

        BottleneckReport {
            bottleneck: BottleneckType::Balanced,
            severity: BottleneckSeverity::Mild,
            headline: CompactString::const_new("System is well-balanced - no clear bottleneck"),
            explanation: CompactString::from(format!(
                "Active workload is balanced across CPU ({cpu:.0}%), GPU ({gpu:.0}%), and Memory ({mem:.0}%)."
            )),
            upgrade_advice: CompactString::const_new(""),
        }
    }
}

fn measured_val(m: &crate::Metric<f64>) -> Option<f64> {
    match m.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn system_health_tracker_detects_improving_stable_and_degrading_trends() {
        let mut tracker = SystemHealthTracker::new(16);
        assert_eq!(tracker.trend(), TrendDirection::Stable);

        for (i, score) in [60.0, 62.0, 75.0, 82.0].into_iter().enumerate() {
            tracker.record((i as u64 + 1) * 1_000_000_000, score);
        }
        assert_eq!(tracker.trend(), TrendDirection::Improving);

        let mut stable = SystemHealthTracker::new(16);
        for (i, score) in [80.0, 81.0, 80.5, 81.5].into_iter().enumerate() {
            stable.record((i as u64 + 1) * 1_000_000_000, score);
        }
        assert_eq!(stable.trend(), TrendDirection::Stable);

        let mut degrading = SystemHealthTracker::new(16);
        for (i, score) in [88.0, 86.0, 70.0, 62.0].into_iter().enumerate() {
            degrading.record((i as u64 + 1) * 1_000_000_000, score);
        }
        assert_eq!(degrading.trend(), TrendDirection::Degrading);
    }
}
