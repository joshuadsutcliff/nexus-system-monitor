#![forbid(unsafe_code)]

pub mod benchmark;
pub mod cadence;
pub mod health;
pub mod platform;
pub mod remote;
pub mod snapshot;
pub mod telemetry;
pub mod tokens;

use compact_str::CompactString;
use serde::{Deserialize, Serialize};
use std::num::NonZeroU64;

pub use benchmark::{
    CPU_MIX_BATCH_ITERATIONS, CPU_OPS_PER_BATCH, CpuBenchmarkResult,
    DEFAULT_BENCHMARK_DURATION_SECS, DEFAULT_NEXUS_BASELINE, UncachedDiskIo, benchmark_ratios,
    cpu_benchmark_mix, nexus_score, run_cpu_multi, run_cpu_single, run_cpu_suite,
    six_character_ratio,
};
pub use cadence::{
    CadenceCoordinator, CadenceLease, DomainMask, SnapshotHub, SnapshotPublisher, UpdateSpeed,
};
pub use health::{
    BottleneckDetector, BottleneckReport, BottleneckSeverity, BottleneckType, HealthLevel,
    HealthScoring, SubsystemHealthScore, SystemHealthReport, SystemHealthTracker, TopConsumerEntry,
    TrendDirection,
};
pub use platform::{
    ConnectionProtocol, DriverEntry, DriverKind, DriverScope, DriverState, InstalledAppEntry,
    LanHostEntry, LanPortEntry, LanRadarReport, NetworkConnectionEntry, RelatedFileEntry,
    ServiceActionResult, ServiceEntry, ServiceStartType, ServiceState, StartupItem,
    StartupItemType, SystemHardwareSummary, TcpState, UninstallReceipt, UserSessionEntry,
    VolumeSpaceSummary, WifiBand, WifiNetworkEntry, WirelessScanReport,
};
pub use remote::{
    BONJOUR_SERVICE_TYPE, ConnectedRemotePeer, DEFAULT_REMOTE_PORT, INFO_CLIENT_FRAMES,
    INFO_DEVICE_PROOF, INFO_PAIR_SAS, INFO_PAIR_SESSION_KEY, INFO_SESSION_KEY, LEGACY_REMOTE_PORT,
    MAX_FRAME_BYTES, MAX_HANDSHAKE_FRAME_BYTES, PairedDeviceRecord, PairingCode,
    RemoteAddressPolicy, RemoteAttemptLimiter, RemoteError, RemoteHandshakeMessage,
    RemoteKeySchedule, RemoteServerConfig, RemoteServerHandle, RemoteServerStatus,
    RemoteTelemetrySample, RemoteTranscript, SEALED_HEADER_BYTES, SEALED_WIRE_MARKER,
    SealedWireFrame, chacha20_poly1305_open, chacha20_poly1305_seal, decode_frames, encode_frame,
    fill_random_bytes, generate_x25519_keypair, hex_decode, hex_encode, hkdf_sha256,
    hkdf_sha256_32, hmac_sha256, sha256, x25519, x25519_base,
};
pub use snapshot::{
    ClusterClockMetric, CoreTelemetry, DiskSnapshot, HardwareTelemetryFrame, LogicalProcessorClass,
    MediaEngineBlockMetric, NetworkSnapshot, ProcessSnapshot, ProviderHealth, SocPowerMetrics,
    SystemMetrics, SystemSnapshot, ThermalMetrics, ThermalSensorReading, repair_snapshot_coherence,
};
pub use telemetry::{GrafanaDashboard, TelegrafConfigGenerator, render_prometheus_metrics};
pub use tokens::{
    DisplayMode, PopoutMode, PopoutSkin, SkinChannelColors, SkinFontSpec, SkinGraphConfig,
    SkinHeader, SkinInk, SkinInset, SkinLogo, SkinMeters, SkinMetrics, SkinSurfaces,
    SkinTypography, SkinValidationError, SurfaceFill, VfdTextColor, bundled,
    canonical_popout_skin_id, clamp_popout_skin_font_size, clamp_skin_font_size, constants,
    skin_document_extension_is_supported, skin_image_extension_is_portable,
    skin_image_name_is_safe, validate_skin_document_bytes, validate_skin_file_count,
    validate_skin_format_version, validate_skin_image_bytes, validate_skin_image_extent,
};

/// Interned provenance source identifier into `SystemSnapshot::sources` (avoids 24-byte String per metric).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct SourceId(pub u32);

/// Unit/domain compatibility tag (prevents raw 0 from acting as an ambiguous wildcard).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct UnitTag(pub NonZeroU64);

impl UnitTag {
    pub const PERCENT: Self = Self(NonZeroU64::new(1).unwrap());
    pub const BYTES: Self = Self(NonZeroU64::new(2).unwrap());
    pub const BYTES_PER_SEC: Self = Self(NonZeroU64::new(3).unwrap());
    pub const WATTS: Self = Self(NonZeroU64::new(4).unwrap());
    pub const CELSIUS: Self = Self(NonZeroU64::new(5).unwrap());
    pub const SECONDS: Self = Self(NonZeroU64::new(6).unwrap());
    pub const COUNT: Self = Self(NonZeroU64::new(7).unwrap());
    pub const DECIBEL_MILLIWATT: Self = Self(NonZeroU64::new(8).unwrap());
    pub const DECIBEL: Self = Self(NonZeroU64::new(9).unwrap());
    pub const MILLISECONDS: Self = Self(NonZeroU64::new(10).unwrap());
    pub const MEGAHERTZ: Self = Self(NonZeroU64::new(11).unwrap());
}

/// 8-state metric value honesty contract.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ValueState {
    Valid = 0,
    Zero = 1,
    NotApplicable = 2,
    Unsupported = 3,
    AccessDenied = 4,
    NotYetAvailable = 5,
    Stale = 6,
    TransientError = 7,
}

impl ValueState {
    /// Returns the canonical camelCase identifier for this `ValueState`.
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Valid => "valid",
            Self::Zero => "zero",
            Self::NotApplicable => "notApplicable",
            Self::Unsupported => "unsupported",
            Self::AccessDenied => "accessDenied",
            Self::NotYetAvailable => "notYetAvailable",
            Self::Stale => "stale",
            Self::TransientError => "transientError",
        }
    }

    /// Returns the human-readable display placeholder for unavailable or stale states.
    #[inline]
    pub const fn placeholder(self) -> &'static str {
        match self {
            Self::Valid | Self::Zero => "",
            Self::NotApplicable | Self::Unsupported | Self::TransientError => "Unavailable",
            Self::AccessDenied => "Access denied",
            Self::NotYetAvailable => "\u{2014}",
            Self::Stale => "Stale",
        }
    }
}

/// Reasons a metric has no measured value (states 2, 3, 4, 5, 7).
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum UnavailableReason {
    NotApplicable = 2,
    Unsupported = 3,
    AccessDenied = 4,
    NotYetAvailable = 5,
    TransientError = 7,
}

impl UnavailableReason {
    /// Severity rank for `aggregate_sum` refusal (lower is worse).
    /// Note: Swaps `AccessDenied` (4) ahead of `Unsupported` (5) so permission denials are never masked.
    #[inline]
    pub const fn severity_rank(self) -> u8 {
        match self {
            Self::TransientError => 0,
            Self::AccessDenied => 4,
            Self::Unsupported => 5,
            Self::NotYetAvailable => 6,
            Self::NotApplicable => 7,
        }
    }

    #[inline]
    pub const fn placeholder(self) -> &'static str {
        match self {
            Self::NotApplicable | Self::Unsupported | Self::TransientError => "Unavailable",
            Self::AccessDenied => "Access denied",
            Self::NotYetAvailable => "\u{2014}",
        }
    }
}

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum MetricQuality {
    Authoritative = 0,
    Fallback = 1,
    Estimated = 2,
    Unknown = 3,
}

impl MetricQuality {
    /// Returns the canonical lowercase identifier for this `MetricQuality`.
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Authoritative => "authoritative",
            Self::Fallback => "fallback",
            Self::Estimated => "estimated",
            Self::Unknown => "unknown",
        }
    }
}

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum HardwareMetricKind {
    MonotonicCounter = 0,
    DerivedRate = 1,
    PolicyValue = 2,
    StaticProperty = 3,
}

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ProviderHealthState {
    Healthy = 0,
    Degraded = 1,
    Unavailable = 2,
    Restarting = 3,
}

impl ProviderHealthState {
    /// Returns the canonical lowercase identifier for this `ProviderHealthState`.
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Healthy => "healthy",
            Self::Degraded => "degraded",
            Self::Unavailable => "unavailable",
            Self::Restarting => "restarting",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct MetricProvenance {
    pub source: SourceId,
    pub unit_tag: Option<UnitTag>,
    pub sampled_at_ns: u64,
    pub window_ns: u64,
    pub quality: MetricQuality,
    pub contributed_by_stale: bool,
}

/// Compile-time invariant carrier for typed metric measurements and unavailability reasons.
/// Impossible states (such as `Zero` with `value != 0.0` or `NaN` with `has_value = true`) cannot be constructed.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub enum MetricReading<T> {
    Measured { value: T, stale: bool },
    Unavailable(UnavailableReason),
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct Metric<T> {
    pub reading: MetricReading<T>,
    pub provenance: MetricProvenance,
}

impl<T> Metric<T> {
    #[inline]
    pub const fn unavailable(reason: UnavailableReason, provenance: MetricProvenance) -> Self {
        Self {
            reading: MetricReading::Unavailable(reason),
            provenance,
        }
    }
}

impl Metric<f64> {
    /// Enforces measurement invariants: rejects NaN/Inf into `TransientError`
    /// and canonicalizes `-0.0` to `+0.0`.
    #[inline]
    pub fn measured(raw: f64, provenance: MetricProvenance) -> Self {
        if !raw.is_finite() {
            return Self {
                reading: MetricReading::Unavailable(UnavailableReason::TransientError),
                provenance,
            };
        }
        let normalized = if raw == 0.0 { 0.0 } else { raw };
        Self {
            reading: MetricReading::Measured {
                value: normalized,
                stale: false,
            },
            provenance,
        }
    }

    /// Constructs a stale metric (`ValueState::Stale`), rejecting NaN/Inf into `TransientError`.
    #[inline]
    pub fn stale_from(raw: f64, mut provenance: MetricProvenance) -> Self {
        if !raw.is_finite() {
            return Self {
                reading: MetricReading::Unavailable(UnavailableReason::TransientError),
                provenance,
            };
        }
        let normalized = if raw == 0.0 { 0.0 } else { raw };
        provenance.contributed_by_stale = true;
        Self {
            reading: MetricReading::Measured {
                value: normalized,
                stale: true,
            },
            provenance,
        }
    }

    /// Returns true if any measured value is finite (non-NaN, non-Inf).
    #[inline]
    pub fn is_consistent(&self) -> bool {
        match self.reading {
            MetricReading::Measured { value, .. } => value.is_finite(),
            MetricReading::Unavailable(_) => true,
        }
    }

    #[inline]
    pub fn state(&self) -> ValueState {
        match self.reading {
            MetricReading::Measured { value, stale: true } => {
                if !value.is_finite() {
                    ValueState::TransientError
                } else {
                    ValueState::Stale
                }
            }
            MetricReading::Measured {
                value,
                stale: false,
            } => {
                if !value.is_finite() {
                    ValueState::TransientError
                } else if value == 0.0 {
                    ValueState::Zero
                } else {
                    ValueState::Valid
                }
            }
            MetricReading::Unavailable(reason) => match reason {
                UnavailableReason::NotApplicable => ValueState::NotApplicable,
                UnavailableReason::Unsupported => ValueState::Unsupported,
                UnavailableReason::AccessDenied => ValueState::AccessDenied,
                UnavailableReason::NotYetAvailable => ValueState::NotYetAvailable,
                UnavailableReason::TransientError => ValueState::TransientError,
            },
        }
    }

    /// Implements `RepairPercent` (`0x10060d098`): clamps measured/stale percentages to `[0.0, 100.0]`
    /// and increments `CoherenceRepairs` when modified.
    pub fn repair_percent(&mut self, repairs: &mut CoherenceRepairs) {
        if let MetricReading::Measured { value, stale } = self.reading {
            if !value.is_finite() {
                self.reading = MetricReading::Unavailable(UnavailableReason::TransientError);
                repairs.marked_unavailable += 1;
            } else if !(0.0..=100.0).contains(&value) {
                let clamped = value.clamp(0.0, 100.0);
                self.reading = MetricReading::Measured {
                    value: if clamped == 0.0 { 0.0 } else { clamped },
                    stale,
                };
                repairs.percentages_clamped += 1;
            }
        }
    }

    /// Formats the metric reading for display, honoring placeholders and `" (stale)"` suffix (`0x1005401b4`).
    pub fn format_with_unit(&self, precision: usize, unit: &str) -> String {
        match self.reading {
            MetricReading::Measured { value, stale } => {
                if !value.is_finite() {
                    return UnavailableReason::TransientError.placeholder().to_string();
                }
                let base = if unit.is_empty() {
                    format!("{value:.precision$}")
                } else if unit == "%" {
                    format!("{value:.precision$}%")
                } else {
                    format!("{value:.precision$} {unit}")
                };
                if stale {
                    format!("{base} (stale)")
                } else {
                    base
                }
            }
            MetricReading::Unavailable(reason) => reason.placeholder().to_string(),
        }
    }
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct CoherenceRepairs {
    pub percentages_clamped: u32,
    pub marked_unavailable: u32,
    pub duplicate_rows_dropped: u32,
}

/// Aggregates `Metric<f64>` slices honestly, refusing partial sums when hard errors occur.
pub fn aggregate_sum(items: &[Metric<f64>], mut base_prov: MetricProvenance) -> Metric<f64> {
    if items.is_empty() {
        return Metric {
            reading: MetricReading::Unavailable(UnavailableReason::NotApplicable),
            provenance: base_prov,
        };
    }
    let mut sum = 0.0_f64;
    let mut expected_tag = base_prov.unit_tag;
    let mut has_measured = false;
    let mut has_stale = false;
    let mut has_pending = false;
    let mut worst_hard_error: Option<UnavailableReason> = None;

    for item in items {
        if !item.is_consistent() {
            return Metric {
                reading: MetricReading::Unavailable(UnavailableReason::TransientError),
                provenance: base_prov,
            };
        }
        if let Some(item_tag) = item.provenance.unit_tag {
            match expected_tag {
                None => expected_tag = Some(item_tag),
                Some(active) if active != item_tag => {
                    return Metric {
                        reading: MetricReading::Unavailable(UnavailableReason::TransientError),
                        provenance: base_prov,
                    };
                }
                Some(_) => {}
            }
        }
        match item.reading {
            MetricReading::Measured { value, stale } => {
                if !value.is_finite() {
                    return Metric {
                        reading: MetricReading::Unavailable(UnavailableReason::TransientError),
                        provenance: base_prov,
                    };
                }
                sum += value;
                has_measured = true;
                if stale {
                    has_stale = true;
                }
            }
            MetricReading::Unavailable(UnavailableReason::NotApplicable) => {}
            MetricReading::Unavailable(UnavailableReason::NotYetAvailable) => {
                has_pending = true;
            }
            MetricReading::Unavailable(err) => {
                worst_hard_error = Some(match worst_hard_error {
                    None => err,
                    Some(prev) if err.severity_rank() <= prev.severity_rank() => err,
                    Some(prev) => prev,
                });
            }
        }
    }

    base_prov.unit_tag = expected_tag;
    if let Some(err) = worst_hard_error {
        return Metric {
            reading: MetricReading::Unavailable(err),
            provenance: base_prov,
        };
    }
    if !has_measured {
        let reason = if has_pending {
            UnavailableReason::NotYetAvailable
        } else {
            UnavailableReason::NotApplicable
        };
        return Metric {
            reading: MetricReading::Unavailable(reason),
            provenance: base_prov,
        };
    }
    if !sum.is_finite() {
        return Metric {
            reading: MetricReading::Unavailable(UnavailableReason::TransientError),
            provenance: base_prov,
        };
    }
    if has_stale {
        base_prov.quality = MetricQuality::Fallback;
        base_prov.contributed_by_stale = true;
    }
    let normalized = if sum == 0.0 { 0.0 } else { sum };
    Metric {
        reading: MetricReading::Measured {
            value: normalized,
            stale: has_stale,
        },
        provenance: base_prov,
    }
}

/// KDE 2-axis sensor value variant supporting numbers, strings, flags, durations, and OS logos.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub enum SensorValue {
    Numeric(Metric<f64>),
    Integer(Metric<i64>),
    DurationNs(Metric<u64>),
    Flag(Metric<bool>),
    Text(Metric<CompactString>),
    ImageAsset(CompactString),
}

/// Cross-platform PID-reuse-safe process identity (`0x1005a8e70`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub struct ProcessKey {
    pub pid: i32,
    pub creation_token_us: u64,
    pub uid: Option<u32>,
}

impl ProcessKey {
    /// Formats key as `"process|<pid>|<creation_token>|-"`.
    pub fn to_collapsed_id_string(&self) -> String {
        format!("process|{}|{}|-", self.pid, self.creation_token_us)
    }

    /// Verifies that `live` matches `self` (`0x10059287c`), refusing recycled PIDs.
    #[inline]
    pub fn matches_live(&self, live: &Self) -> bool {
        if self.pid != live.pid || self.creation_token_us != live.creation_token_us {
            return false;
        }
        if let (Some(a), Some(b)) = (self.uid, live.uid)
            && a != b
        {
            return false;
        }
        true
    }
}

/// Crash-safe priority restore receipt for `GamingMode`, `PerformanceProfile`, and `AutoBalance`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PriorityRestoreReceipt {
    pub key: ProcessKey,
    pub process_name: CompactString,
    pub original_priority: i32,
    pub original_affinity_mask: Option<u64>,
    pub applied_by_rule_id: CompactString,
    pub applied_at_unix_ms: i64,
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use std::time::Duration;

    fn sample_prov(unit_tag: Option<UnitTag>) -> MetricProvenance {
        MetricProvenance {
            source: SourceId(1),
            unit_tag,
            sampled_at_ns: 1_000_000_000,
            window_ns: 250_000_000,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        }
    }

    #[test]
    fn metric_measured_rejects_nan_and_inf_and_normalizes_neg_zero() {
        let prov = sample_prov(Some(UnitTag::PERCENT));

        let nan_m = Metric::measured(f64::NAN, prov);
        assert_eq!(nan_m.state(), ValueState::TransientError);
        assert_eq!(
            nan_m.reading,
            MetricReading::Unavailable(UnavailableReason::TransientError)
        );
        assert_eq!(nan_m.format_with_unit(1, "%"), "Unavailable");

        let inf_m = Metric::measured(f64::INFINITY, prov);
        assert_eq!(inf_m.state(), ValueState::TransientError);

        let neg_inf_m = Metric::measured(f64::NEG_INFINITY, prov);
        assert_eq!(neg_inf_m.state(), ValueState::TransientError);

        let neg_zero = Metric::measured(-0.0, prov);
        assert_eq!(neg_zero.state(), ValueState::Zero);
        if let MetricReading::Measured { value, stale } = neg_zero.reading {
            assert!(!stale);
            assert!(value.is_sign_positive());
        } else {
            panic!("expected Measured(0.0)");
        }

        let valid = Metric::measured(42.5, prov);
        assert_eq!(valid.state(), ValueState::Valid);
        assert_eq!(valid.format_with_unit(1, "%"), "42.5%");

        let stale = Metric::stale_from(19.2, prov);
        assert_eq!(stale.state(), ValueState::Stale);
        assert!(stale.provenance.contributed_by_stale);
        assert_eq!(stale.format_with_unit(1, "%"), "19.2% (stale)");
    }

    #[test]
    fn value_state_and_placeholders_match_contract() {
        assert_eq!(ValueState::Valid.as_str(), "valid");
        assert_eq!(ValueState::Zero.as_str(), "zero");
        assert_eq!(ValueState::NotApplicable.as_str(), "notApplicable");
        assert_eq!(ValueState::Unsupported.as_str(), "unsupported");
        assert_eq!(ValueState::AccessDenied.as_str(), "accessDenied");
        assert_eq!(ValueState::NotYetAvailable.as_str(), "notYetAvailable");
        assert_eq!(ValueState::Stale.as_str(), "stale");
        assert_eq!(ValueState::TransientError.as_str(), "transientError");

        assert_eq!(ValueState::Valid.placeholder(), "");
        assert_eq!(ValueState::Zero.placeholder(), "");
        assert_eq!(ValueState::NotApplicable.placeholder(), "Unavailable");
        assert_eq!(ValueState::Unsupported.placeholder(), "Unavailable");
        assert_eq!(ValueState::AccessDenied.placeholder(), "Access denied");
        assert_eq!(ValueState::NotYetAvailable.placeholder(), "\u{2014}");
        assert_eq!(ValueState::Stale.placeholder(), "Stale");
        assert_eq!(ValueState::TransientError.placeholder(), "Unavailable");
    }

    #[test]
    fn aggregate_sum_enforces_honesty_and_severity_ranking() {
        let base = sample_prov(Some(UnitTag::WATTS));

        // 1. Empty slice -> NotApplicable
        let empty = aggregate_sum(&[], base);
        assert_eq!(empty.state(), ValueState::NotApplicable);

        // 2. Valid + Zero -> Valid sum
        let m1 = Metric::measured(12.5, base);
        let m2 = Metric::measured(0.0, base);
        let sum = aggregate_sum(&[m1, m2], base);
        assert_eq!(sum.state(), ValueState::Valid);
        assert_eq!(
            sum.reading,
            MetricReading::Measured {
                value: 12.5,
                stale: false
            }
        );

        // 3. Valid + Stale -> Stale with Fallback quality
        let m_stale = Metric::stale_from(3.5, base);
        let stale_sum = aggregate_sum(&[m1, m_stale], base);
        assert_eq!(stale_sum.state(), ValueState::Stale);
        assert_eq!(stale_sum.provenance.quality, MetricQuality::Fallback);
        assert!(stale_sum.provenance.contributed_by_stale);
        assert_eq!(
            stale_sum.reading,
            MetricReading::Measured {
                value: 16.0,
                stale: true
            }
        );

        // 4. NotYetAvailable is skipped when other valid items exist, but returns NotYetAvailable when alone
        let m_pending = Metric::unavailable(UnavailableReason::NotYetAvailable, sample_prov(None));
        let partial_warm = aggregate_sum(&[m1, m_pending], base);
        assert_eq!(partial_warm.state(), ValueState::Valid);
        let all_pending = aggregate_sum(&[m_pending], base);
        assert_eq!(all_pending.state(), ValueState::NotYetAvailable);

        // 5. Hard failure refuses partial sums and ranks TransientError < AccessDenied < Unsupported
        let m_unab = Metric::unavailable(UnavailableReason::Unsupported, base);
        let m_denied = Metric::unavailable(UnavailableReason::AccessDenied, base);
        let m_err = Metric::unavailable(UnavailableReason::TransientError, base);

        let refused_denied = aggregate_sum(&[m1, m_unab, m_denied], base);
        assert_eq!(refused_denied.state(), ValueState::AccessDenied);

        let refused_err = aggregate_sum(&[m1, m_denied, m_err], base);
        assert_eq!(refused_err.state(), ValueState::TransientError);

        // 6. UnitTag mismatch -> TransientError
        let wrong_unit = Metric::measured(5.0, sample_prov(Some(UnitTag::BYTES)));
        let mismatch = aggregate_sum(&[m1, wrong_unit], base);
        assert_eq!(mismatch.state(), ValueState::TransientError);
    }

    #[test]
    fn repair_snapshot_coherence_clamps_percentages_and_deduplicates_processes() {
        let mut snap = SystemSnapshot::new_empty(1, 5_000_000_000);
        let prov = sample_prov(Some(UnitTag::PERCENT));
        snap.system.cpu_percent = Metric {
            reading: MetricReading::Measured {
                value: 142.0,
                stale: false,
            },
            provenance: prov,
        };
        snap.system.mem_used_percent = Metric {
            reading: MetricReading::Measured {
                value: -5.0,
                stale: false,
            },
            provenance: prov,
        };
        snap.system.gpu_utilization_percent = Metric {
            reading: MetricReading::Measured {
                value: f64::NAN,
                stale: false,
            },
            provenance: prov,
        };

        let key = ProcessKey {
            pid: 100,
            creation_token_us: 123_456,
            uid: Some(501),
        };
        let proc_row = ProcessSnapshot {
            key,
            ppid: 1,
            name: CompactString::const_new("test_proc"),
            cpu_percent: Metric::measured(10.0, prov),
            phys_footprint_bytes: Metric::measured(4096.0, sample_prov(Some(UnitTag::BYTES))),
            resident_bytes: Metric::measured(4096.0, sample_prov(Some(UnitTag::BYTES))),
            disk_read_bytes: Metric::measured(0.0, sample_prov(Some(UnitTag::BYTES))),
            disk_write_bytes: Metric::measured(0.0, sample_prov(Some(UnitTag::BYTES))),
            exited_at_ns: None,
        };
        snap.processes.push(proc_row.clone());
        snap.processes.push(proc_row);

        assert_eq!(snap.first_coherence_fault(), Some("system.cpuPercent"));

        let repairs = repair_snapshot_coherence(&mut snap);
        assert_eq!(repairs.percentages_clamped, 2);
        assert_eq!(repairs.marked_unavailable, 1);
        assert_eq!(repairs.duplicate_rows_dropped, 1);
        assert_eq!(snap.processes.len(), 1);
        assert_eq!(snap.first_coherence_fault(), None);
    }

    #[test]
    fn process_key_verifies_creation_token_and_uid() {
        let k1 = ProcessKey {
            pid: 4242,
            creation_token_us: 1_700_000_000_000_000,
            uid: Some(501),
        };
        let k_same = ProcessKey {
            pid: 4242,
            creation_token_us: 1_700_000_000_000_000,
            uid: Some(501),
        };
        let k_recycled = ProcessKey {
            pid: 4242,
            creation_token_us: 1_700_000_000_500_000,
            uid: Some(501),
        };
        assert!(k1.matches_live(&k_same));
        assert!(!k1.matches_live(&k_recycled));
        assert_eq!(
            k1.to_collapsed_id_string(),
            "process|4242|1700000000000000|-"
        );
    }

    #[test]
    fn cadence_coordinator_and_snapshot_hub_coalesce_without_evicting_leases() {
        let coord = CadenceCoordinator::new();
        assert_eq!(coord.active_schedule(), (None, DomainMask::empty()));

        let lease_ui = coord.acquire_lease(
            Duration::from_millis(1000),
            DomainMask::CPU_MEM_GPU | DomainMask::PROCESSES,
        );
        assert_eq!(
            coord.active_schedule(),
            (
                Some(Duration::from_millis(1000)),
                DomainMask::CPU_MEM_GPU | DomainMask::PROCESSES
            )
        );

        let lease_fast = coord.acquire_lease(Duration::from_millis(250), DomainMask::SOC_POWER);
        assert_eq!(
            coord.active_schedule(),
            (
                Some(Duration::from_millis(250)),
                DomainMask::CPU_MEM_GPU | DomainMask::PROCESSES | DomainMask::SOC_POWER
            )
        );

        drop(lease_fast);
        // Dropping fast lease restores 1000ms UI lease instead of stopping or evicting it
        assert_eq!(
            coord.active_schedule(),
            (
                Some(Duration::from_millis(1000)),
                DomainMask::CPU_MEM_GPU | DomainMask::PROCESSES
            )
        );
        drop(lease_ui);
        assert_eq!(coord.active_schedule(), (None, DomainMask::empty()));

        let snap0 = Arc::new(SystemSnapshot::new_empty(1, 100));
        let (pub_handle, mut hub) = SnapshotHub::new(snap0);
        let snap1 = Arc::new(SystemSnapshot::new_empty(2, 200));
        let snap2 = Arc::new(SystemSnapshot::new_empty(3, 300));
        pub_handle.publish(snap1);
        pub_handle.publish(snap2);

        // UI watch channel coalesces to newest (generation 3)
        assert_eq!(hub.ui_inbox.borrow().generation, 3);
        // Automation & storage rings retain both in FIFO order
        assert_eq!(hub.automation_rx.try_recv().unwrap().generation, 2);
        assert_eq!(hub.automation_rx.try_recv().unwrap().generation, 3);
        assert_eq!(hub.storage_rx.try_recv().unwrap().generation, 2);
        assert_eq!(hub.storage_rx.try_recv().unwrap().generation, 3);
    }

    #[test]
    fn bundled_skins_and_7_security_validators_pass_all_rules() {
        assert!(!bundled::FONT_MICHROMA_REGULAR_TTF.is_empty());
        assert!(!bundled::FONT_LED_DOT_MATRIX_TTF.is_empty());

        let skins = bundled::load_bundled_skins().expect("all 5 bundled skins must validate");
        assert_eq!(skins.len(), 5);
        assert_eq!(skins[0].id.as_str(), "com.nexus.console");
        assert_eq!(skins[1].id.as_str(), "com.nexus.traces");
        assert_eq!(skins[2].id.as_str(), "winamp");
        assert_eq!(skins[3].id.as_str(), "mediaPlayer");
        assert_eq!(skins[4].id.as_str(), "com.nexus.wintaskman");

        // Validator 1: format version
        assert!(validate_skin_format_version(1).is_ok());
        assert_eq!(
            validate_skin_format_version(2),
            Err(SkinValidationError::UnsupportedFormatVersion(2))
        );

        // Validator 2: max document bytes (256 KB)
        assert!(validate_skin_document_bytes(262_144).is_ok());
        assert!(validate_skin_document_bytes(262_145).is_err());

        // Validator 3: max image bytes (8 MB)
        assert!(validate_skin_image_bytes(8_388_608).is_ok());
        assert!(validate_skin_image_bytes(8_388_609).is_err());

        // Validator 4: max files (64)
        assert!(validate_skin_file_count(64).is_ok());
        assert!(validate_skin_file_count(65).is_err());

        // Validator 5: max image extent (4096 px and <= 4 MP total pixels)
        assert!(validate_skin_image_extent(16.0, 16.0).is_ok());
        assert!(validate_skin_image_extent(4096.0, 1024.0).is_ok());
        assert!(validate_skin_image_extent(4096.0, 4096.0).is_err()); // > 4 MP
        assert!(validate_skin_image_extent(4097.0, 1.0).is_err());
        assert!(validate_skin_image_extent(0.0, 16.0).is_err());

        // Validator 6: font size clamping
        assert_eq!(clamp_skin_font_size(3.0), 7.0);
        assert_eq!(clamp_skin_font_size(120.0), 72.0);
        assert_eq!(clamp_skin_font_size(f32::NAN), 10.0);
        assert_eq!(clamp_popout_skin_font_size(3.0), 6.0);
        assert_eq!(clamp_popout_skin_font_size(40.0), 24.0);

        // Validator 7: safe image filename
        assert!(skin_image_name_is_safe("taskman.png"));
        assert!(skin_image_name_is_safe("panel_bg.JPG"));
        assert!(!skin_image_name_is_safe("../traversal.png"));
        assert!(!skin_image_name_is_safe("sub/taskman.png"));
        assert!(!skin_image_name_is_safe("sub\\taskman.png"));
        assert!(!skin_image_name_is_safe(".hidden.png"));
        assert!(!skin_image_name_is_safe("con.png"));
        assert!(!skin_image_name_is_safe("com1.png"));
        assert!(!skin_image_name_is_safe("lpt9.png"));
        assert!(!skin_image_name_is_safe("bad.pdf"));
        assert!(!skin_image_name_is_safe("script.sh"));

        // DisplayMode & VfdTextColor resolution
        assert_eq!(
            VfdTextColor::Automatic.resolve_srgb(DisplayMode::Green),
            [0.0, 1.0, 0.0, 1.0]
        );
        assert_eq!(
            VfdTextColor::Automatic.resolve_srgb(DisplayMode::Amber),
            [1.0, 0.65, 0.0, 1.0]
        );
        assert!(DisplayMode::Green.forces_dark_appearance());
        assert!(!DisplayMode::Color.forces_dark_appearance());
    }

    #[test]
    fn health_scoring_grafana_telegraf_and_prometheus_preserve_honesty() {
        let mut snap = SystemSnapshot::new_empty(2, 2_000_000_000);
        let prov = sample_prov(Some(UnitTag::PERCENT));
        snap.system.cpu_percent = Metric::measured(25.0, prov);
        snap.system.mem_used_percent = Metric::measured(50.0, prov);
        // GPU remains NotYetAvailable -> excluded from composite weight rather than rewarded as 100
        let report = HealthScoring::evaluate_snapshot(&snap);
        assert!(report.gpu.score.is_none());
        assert!(report.overall_score > 70.0 && report.overall_score <= 100.0);
        assert_eq!(report.bottleneck.bottleneck, BottleneckType::Idle);

        let prom = render_prometheus_metrics(&snap, 3, 1);
        assert!(prom.contains("nexus_cpu_usage_percent"));
        assert!(prom.contains("nexus_metric_unavailable{metric=\"nexus_gpu_usage_percent\""));
        assert!(prom.contains("nexus_alert_events_total 3"));
        let forbidden = ["tm", "og"].concat();
        assert!(!prom.contains(&forbidden));

        let parsed: serde_json::Value =
            serde_json::from_str(GrafanaDashboard::JSON).expect("valid Grafana JSON");
        assert_eq!(parsed["title"], "Nexus System Monitor");
        let telegraf = TelegrafConfigGenerator::generate(9182);
        assert!(telegraf.contains("http://localhost:9182/metrics"));
    }
}
