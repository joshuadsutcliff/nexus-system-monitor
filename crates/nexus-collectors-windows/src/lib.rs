use compact_str::CompactString;
#[cfg(target_os = "windows")]
use nexus_core::{
    ClusterClockMetric, CoreTelemetry, DiskSnapshot, LogicalProcessorClass, MediaEngineBlockMetric,
    MetricReading, NetworkSnapshot, ThermalSensorReading,
};
use nexus_core::{
    ConnectionProtocol, Metric, MetricProvenance, MetricQuality, NetworkConnectionEntry,
    ProcessKey, ProcessSnapshot, ProviderHealth, ProviderHealthState, ServiceEntry,
    ServiceStartType, ServiceState, SourceId, StartupItem, StartupItemType, SystemSnapshot,
    TcpState, UnavailableReason, UnitTag, WifiBand, WifiNetworkEntry, WirelessScanReport,
    repair_snapshot_coherence,
};
use parking_lot::Mutex;
#[cfg(not(target_os = "windows"))]
use std::collections::HashMap;
#[cfg(target_os = "windows")]
use std::collections::{HashMap, HashSet};
use std::net::Ipv4Addr;
use std::time::Instant;

/// Retains exited processes for `5,000,000,000` ns (5.0 s).
pub const EXITED_PROCESS_RETENTION_NS: u64 = 5_000_000_000;

/// Previous cumulative I/O counter sample for monotonic byte and operation rate calculation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrevIoCounter {
    pub rx_or_read: u64,
    pub tx_or_write: u64,
    pub read_ops: u64,
    pub write_ops: u64,
    pub sampled_at_ns: u64,
}

/// NVML `nvmlUtilization_t` C struct layout (8 bytes: GPU % and memory controller %).
#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct NvmlUtilization {
    pub gpu: u32,
    pub memory: u32,
}

/// NVML `nvmlMemory_t` C struct layout (24 bytes: total, free, and used VRAM bytes).
#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct NvmlMemory {
    pub total: u64,
    pub free: u64,
    pub used: u64,
}

/// Parsed Windows GPU engine utilization breakdown from PDH `\GPU Engine(*)\Utilization Percentage` or NVML.
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct GpuEngineBreakdown {
    pub engine_3d_percent: f64,
    pub engine_compute_percent: f64,
    pub engine_copy_percent: f64,
    pub engine_video_decode_percent: f64,
    pub engine_video_encode_percent: f64,
    pub overall_percent: f64,
}

#[allow(dead_code)]
struct WindowsCollectorInner {
    epoch: Instant,
    generation: u64,
    prev_idle_ticks: Option<(u64, u64, u64)>,
    prev_cpu_sampled_at_ns: Option<u64>,
    prev_core_times: Vec<(u64, u64, u64)>,
    prev_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)>,
    exited_procs: HashMap<ProcessKey, ProcessSnapshot>,
    prev_disks: HashMap<CompactString, PrevIoCounter>,
    prev_nets: HashMap<CompactString, PrevIoCounter>,
    #[cfg(target_os = "windows")]
    nvml: Option<win32::NvmlSession>,
    #[cfg(target_os = "windows")]
    nvml_probed: bool,
    #[cfg(target_os = "windows")]
    dxgi_cached: Option<win32::DxgiAdapterSummary>,
    #[cfg(target_os = "windows")]
    dxgi_probed: bool,
    #[cfg(target_os = "windows")]
    pdh: Option<win32::PdhSession>,
    #[cfg(target_os = "windows")]
    pdh_probed: bool,
}

/// Cross-platform Windows collector (`GetSystemTimes`, `GlobalMemoryStatusEx`, `NtQuerySystemInformation`, `CreateToolhelp32Snapshot`, NVML/DXGI/PDH, `GetIfTable2`, `IOCTL_DISK_PERFORMANCE`, SCM, `netsh wlan`).
pub struct WindowsCollector {
    inner: Mutex<WindowsCollectorInner>,
}

impl Default for WindowsCollector {
    fn default() -> Self {
        Self::new()
    }
}

impl WindowsCollector {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(WindowsCollectorInner {
                epoch: Instant::now(),
                generation: 0,
                prev_idle_ticks: None,
                prev_cpu_sampled_at_ns: None,
                prev_core_times: Vec::new(),
                prev_procs: HashMap::new(),
                exited_procs: HashMap::new(),
                prev_disks: HashMap::new(),
                prev_nets: HashMap::new(),
                #[cfg(target_os = "windows")]
                nvml: None,
                #[cfg(target_os = "windows")]
                nvml_probed: false,
                #[cfg(target_os = "windows")]
                dxgi_cached: None,
                #[cfg(target_os = "windows")]
                dxgi_probed: false,
                #[cfg(target_os = "windows")]
                pdh: None,
                #[cfg(target_os = "windows")]
                pdh_probed: false,
            }),
        }
    }

    /// Reads a PID-reuse-safe `ProcessKey(pid, creation_token_us, None)`, `ppid`, and `name` on Windows.
    pub fn read_process_key(pid: i32) -> Option<(ProcessKey, i32, CompactString)> {
        read_process_key(pid)
    }

    /// Collects a snapshot on Windows hosts (or returns an `Unsupported` snapshot on non-Windows hosts).
    pub fn collect(&self) -> SystemSnapshot {
        let mut guard = self.inner.lock();
        guard.generation += 1;
        let generation = guard.generation;
        let sampled_at_ns = (guard.epoch.elapsed().as_nanos() as u64).max(1);
        let mut snap = SystemSnapshot::new_empty(generation, sampled_at_ns);

        #[cfg(target_os = "windows")]
        {
            Self::collect_windows_host(&mut guard, &mut snap, sampled_at_ns);
        }

        #[cfg(not(target_os = "windows"))]
        {
            let src = snap.intern_source("windows.win32.system_times");
            let prov = MetricProvenance {
                source: src,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Unknown,
                contributed_by_stale: false,
            };
            snap.system.cpu_percent = Metric::unavailable(UnavailableReason::Unsupported, prov);
            snap.provider_health.push(ProviderHealth {
                id: CompactString::const_new("windows.pdh"),
                state: ProviderHealthState::Unavailable,
                consecutive_failures: 0,
                last_success_at_ns: 0,
                last_error: Some(CompactString::const_new("non-windows host")),
            });
            let _ = &mut guard.prev_idle_ticks;
        }

        let _ = repair_snapshot_coherence(&mut snap);
        snap
    }

    /// Computes CPU total, user, and kernel percentages from Win32 `GetSystemTimes` 100-ns `FILETIME` deltas
    /// (where `kernel_time` on Windows includes `idle_time`).
    pub fn compute_system_times_delta(
        prev: (u64, u64, u64),
        cur: (u64, u64, u64),
        sampled_at_ns: u64,
        window_ns: u64,
        source: SourceId,
    ) -> (Metric<f64>, Metric<f64>, Metric<f64>) {
        let (prev_idle, prev_kernel, prev_user) = prev;
        let (cur_idle, cur_kernel, cur_user) = cur;
        let prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns,
            window_ns,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        if cur_idle < prev_idle || cur_kernel < prev_kernel || cur_user < prev_user {
            let pending = Metric::unavailable(UnavailableReason::NotYetAvailable, prov);
            return (pending, pending, pending);
        }
        let d_idle = cur_idle - prev_idle;
        let d_kernel_total = cur_kernel - prev_kernel;
        let d_user = cur_user - prev_user;
        let d_total = d_kernel_total + d_user;
        if d_total == 0 || d_kernel_total < d_idle {
            let pending = Metric::unavailable(UnavailableReason::NotYetAvailable, prov);
            return (pending, pending, pending);
        }
        let d_sys = d_kernel_total - d_idle;
        let d_busy = d_sys + d_user;
        let denom = d_total as f64;
        (
            Metric::measured(((d_busy as f64) / denom * 100.0).clamp(0.0, 100.0), prov),
            Metric::measured(((d_user as f64) / denom * 100.0).clamp(0.0, 100.0), prov),
            Metric::measured(((d_sys as f64) / denom * 100.0).clamp(0.0, 100.0), prov),
        )
    }

    /// Constructs a Windows `ProcessKey` from a Win32 PID and `GetProcessTimes` `CreationTime` (100-ns intervals since 1601).
    pub const fn make_process_key(pid: u32, filetime_100ns: u64) -> ProcessKey {
        ProcessKey {
            pid: pid as i32,
            creation_token_us: filetime_100ns / 10,
            uid: None,
        }
    }

    /// Normalizes a per-process CPU time delta (`d_cpu_ns`) over elapsed wall time (`dt_ns`) and
    /// logical processor count (`logical_cores`) into `[0.0, 100.0]` of total system CPU capacity.
    pub fn compute_normalized_process_cpu_percent(
        d_cpu_ns: u64,
        dt_ns: u64,
        logical_cores: u32,
    ) -> f64 {
        if dt_ns == 0 {
            return 0.0;
        }
        let denom = (dt_ns as f64) * f64::from(logical_cores.max(1));
        ((d_cpu_ns as f64) / denom * 100.0).clamp(0.0, 100.0)
    }

    /// Converts a Win32 `GetExtendedTcpTable` (`MIB_TCPROW_OWNER_PID`) record (network byte order ports/IPs)
    /// into a unified `NetworkConnectionEntry`.
    pub fn from_mib_tcp_row(
        local_addr_le: u32,
        local_port_nbo: u16,
        remote_addr_le: u32,
        remote_port_nbo: u16,
        mib_state: u32,
        owning_pid: u32,
        process_name: &str,
    ) -> NetworkConnectionEntry {
        let local_ip = Ipv4Addr::from(local_addr_le.to_be()).to_string();
        let remote_ip = Ipv4Addr::from(remote_addr_le.to_be()).to_string();
        let state = match mib_state {
            1 => TcpState::Closed,
            2 => TcpState::Listen,
            3 => TcpState::SynSent,
            4 => TcpState::SynReceived,
            5 => TcpState::Established,
            6 => TcpState::FinWait1,
            7 => TcpState::FinWait2,
            8 => TcpState::CloseWait,
            9 => TcpState::Closing,
            10 => TcpState::LastAck,
            11 => TcpState::TimeWait,
            _ => TcpState::Unknown,
        };
        NetworkConnectionEntry {
            protocol: ConnectionProtocol::Tcp4,
            local_address: CompactString::from(local_ip),
            local_port: u16::from_be(local_port_nbo),
            remote_address: CompactString::from(remote_ip),
            remote_port: u16::from_be(remote_port_nbo),
            state,
            pid: Some(owning_pid as i32),
            process_key: None,
            process_name: CompactString::from(process_name),
            rx_bytes: None,
            tx_bytes: None,
        }
    }

    /// Parses `sc.exe queryex type= service state= all` output blocks.
    pub fn parse_windows_sc_query(output: &str) -> Vec<ServiceEntry> {
        let mut services = Vec::new();
        let mut name = String::new();
        let mut display = String::new();
        let mut state = ServiceState::Unknown;
        let mut pid = None;

        let flush = |out: &mut Vec<ServiceEntry>,
                     name: &mut String,
                     display: &mut String,
                     state: &mut ServiceState,
                     pid: &mut Option<i32>| {
            if !name.is_empty() {
                out.push(ServiceEntry {
                    name: CompactString::from(name.as_str()),
                    display_name: CompactString::from(if display.is_empty() {
                        name.as_str()
                    } else {
                        display.as_str()
                    }),
                    description: CompactString::const_new(""),
                    state: *state,
                    start_type: ServiceStartType::Unknown,
                    pid: *pid,
                    binary_path: CompactString::const_new(""),
                    user_account: CompactString::const_new(""),
                });
                name.clear();
                display.clear();
                *state = ServiceState::Unknown;
                *pid = None;
            }
        };

        for raw_line in output.lines() {
            let line = raw_line.trim();
            if let Some(rest) = line.strip_prefix("SERVICE_NAME:") {
                flush(&mut services, &mut name, &mut display, &mut state, &mut pid);
                name = rest.trim().to_string();
            } else if let Some(rest) = line.strip_prefix("DISPLAY_NAME:") {
                display = rest.trim().to_string();
            } else if line.starts_with("STATE") {
                if line.contains("RUNNING") {
                    state = ServiceState::Running;
                } else if line.contains("STOPPED") {
                    state = ServiceState::Stopped;
                } else if line.contains("PAUSED") {
                    state = ServiceState::Paused;
                }
            } else if line.starts_with("PID")
                && let Some((_, val)) = line.split_once(':')
            {
                pid = val.trim().parse::<i32>().ok().filter(|&p| p > 0);
            }
        }
        flush(&mut services, &mut name, &mut display, &mut state, &mut pid);
        services
    }

    /// Maps Windows registry `HKLM/HKCU\...\Run` + `StartupApproved` byte status (`0x02` = enabled, `0x03` = disabled).
    pub fn build_windows_startup_item(
        name: &str,
        command: &str,
        registry_path: &str,
        approved_status_byte: Option<u8>,
    ) -> StartupItem {
        let enabled = approved_status_byte.is_none_or(|b| (b & 0x01) == 0);
        StartupItem {
            name: CompactString::from(name),
            command: CompactString::from(command),
            publisher: CompactString::const_new(""),
            location: CompactString::from(registry_path),
            is_enabled: enabled,
            run_at_load: enabled,
            item_type: StartupItemType::WindowsRegistryRun,
            user_writable: registry_path.starts_with("HKCU"),
        }
    }

    /// Parses `netsh wlan show networks mode=bssid` output into `WirelessScanReport`.
    pub fn parse_windows_netsh_wlan(
        output: &str,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> WirelessScanReport {
        let mut networks = Vec::new();
        let dbm_prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::DECIBEL_MILLIWATT),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Fallback,
            contributed_by_stale: false,
        };
        let snr_prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::DECIBEL),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Unknown,
            contributed_by_stale: false,
        };

        let mut cur_ssid = String::new();
        let mut cur_auth = String::from("Unknown");
        let mut cur_bssid: Option<String> = None;
        let mut cur_signal: Option<f64> = None;
        let mut cur_channel: u32 = 0;

        let flush_bssid = |nets: &mut Vec<WifiNetworkEntry>,
                           ssid: &str,
                           auth: &str,
                           bssid: &mut Option<String>,
                           signal: &mut Option<f64>,
                           channel: &mut u32| {
            if let Some(b) = bssid.take() {
                let rssi = signal
                    .take()
                    .map(|pct| Metric::measured(-100.0 + pct.clamp(0.0, 100.0) * 0.5, dbm_prov))
                    .unwrap_or_else(|| {
                        Metric::unavailable(UnavailableReason::NotApplicable, dbm_prov)
                    });
                let ch = *channel;
                *channel = 0;
                let band = if ch > 14 {
                    WifiBand::Band5GHz
                } else if ch > 0 {
                    WifiBand::Band2_4GHz
                } else {
                    WifiBand::Unknown
                };
                nets.push(WifiNetworkEntry {
                    ssid: CompactString::from(if ssid.is_empty() { "<hidden>" } else { ssid }),
                    bssid: Some(CompactString::from(b)),
                    rssi_dbm: rssi,
                    noise_dbm: Metric::unavailable(UnavailableReason::Unsupported, dbm_prov),
                    snr_db: Metric::unavailable(UnavailableReason::Unsupported, snr_prov),
                    channel: ch,
                    channel_width_mhz: None,
                    band,
                    phy_mode: CompactString::const_new("802.11"),
                    security: CompactString::from(auth),
                    is_connected: false,
                    redacted: false,
                });
            }
        };

        for raw_line in output.lines() {
            let line = raw_line.trim();
            if line.starts_with("SSID ")
                && let Some((_, val)) = line.split_once(':')
            {
                flush_bssid(
                    &mut networks,
                    &cur_ssid,
                    &cur_auth,
                    &mut cur_bssid,
                    &mut cur_signal,
                    &mut cur_channel,
                );
                cur_ssid = val.trim().to_string();
                cur_auth = "Unknown".to_string();
            } else if line.starts_with("Authentication")
                && let Some((_, val)) = line.split_once(':')
            {
                cur_auth = val.trim().to_string();
            } else if line.starts_with("BSSID ")
                && let Some((_, val)) = line.split_once(':')
            {
                flush_bssid(
                    &mut networks,
                    &cur_ssid,
                    &cur_auth,
                    &mut cur_bssid,
                    &mut cur_signal,
                    &mut cur_channel,
                );
                cur_bssid = Some(val.trim().to_string());
            } else if line.starts_with("Signal")
                && let Some((_, val)) = line.split_once(':')
            {
                cur_signal = val.trim().trim_end_matches('%').parse::<f64>().ok();
            } else if line.starts_with("Channel")
                && let Some((_, val)) = line.split_once(':')
            {
                cur_channel = val.trim().parse::<u32>().unwrap_or(0);
            }
        }
        flush_bssid(
            &mut networks,
            &cur_ssid,
            &cur_auth,
            &mut cur_bssid,
            &mut cur_signal,
            &mut cur_channel,
        );

        WirelessScanReport {
            interface: CompactString::const_new("Wi-Fi"),
            connected_ssid: None,
            networks,
            notes: CompactString::const_new("Parsed from netsh wlan show networks mode=bssid"),
        }
    }

    /// Parses `netsh wlan show interfaces` output combined with `netsh wlan show networks mode=bssid`
    /// so the active connected Wi-Fi SSID and `is_connected` flag are populated on Windows.
    pub fn parse_windows_netsh_wlan_with_interfaces(
        networks_output: &str,
        interfaces_output: &str,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> WirelessScanReport {
        let mut rep = Self::parse_windows_netsh_wlan(networks_output, sampled_at_ns, source);
        let mut connected_ssid: Option<String> = None;
        let mut state_connected = false;
        let mut cur_iface_ssid: Option<String> = None;
        for raw_line in interfaces_output.lines() {
            let line = raw_line.trim();
            if line.starts_with("State")
                && let Some((_, val)) = line.split_once(':')
            {
                let v = val.trim().to_ascii_lowercase();
                state_connected = v == "connected";
            } else if line.starts_with("SSID")
                && !line.starts_with("BSSID")
                && let Some((_, val)) = line.split_once(':')
            {
                let s = val.trim();
                if !s.is_empty() {
                    cur_iface_ssid = Some(s.to_string());
                }
            }
        }
        if state_connected {
            connected_ssid = cur_iface_ssid;
        }
        if let Some(ssid) = connected_ssid {
            for net in &mut rep.networks {
                if net.ssid.as_str() == ssid {
                    net.is_connected = true;
                }
            }
            rep.connected_ssid = Some(CompactString::from(ssid));
        }
        rep
    }

    /// Computes monotonic byte/sec throughput metrics from cumulative `(rx_or_read, tx_or_write)` counters.
    pub fn compute_io_rate_metrics(
        prev: Option<PrevIoCounter>,
        cur_read_or_rx: u64,
        cur_write_or_tx: u64,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> (Metric<f64>, Metric<f64>) {
        if let Some(p) = prev {
            let dt_ns = sampled_at_ns.saturating_sub(p.sampled_at_ns);
            let rate_prov = MetricProvenance {
                source,
                unit_tag: Some(UnitTag::BYTES_PER_SEC),
                sampled_at_ns,
                window_ns: dt_ns,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            if dt_ns > 0 && cur_read_or_rx >= p.rx_or_read && cur_write_or_tx >= p.tx_or_write {
                let dt_s = (dt_ns as f64) / 1_000_000_000.0;
                (
                    Metric::measured(((cur_read_or_rx - p.rx_or_read) as f64) / dt_s, rate_prov),
                    Metric::measured(((cur_write_or_tx - p.tx_or_write) as f64) / dt_s, rate_prov),
                )
            } else {
                (
                    Metric::unavailable(UnavailableReason::NotYetAvailable, rate_prov),
                    Metric::unavailable(UnavailableReason::NotYetAvailable, rate_prov),
                )
            }
        } else {
            let rate_prov = MetricProvenance {
                source,
                unit_tag: Some(UnitTag::BYTES_PER_SEC),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            (
                Metric::unavailable(UnavailableReason::NotYetAvailable, rate_prov),
                Metric::unavailable(UnavailableReason::NotYetAvailable, rate_prov),
            )
        }
    }

    /// Computes monotonic operations/sec (IOPS) metrics from cumulative `(read_ops, write_ops)` counters.
    pub fn compute_iops_metrics(
        prev: Option<PrevIoCounter>,
        cur_read_ops: u64,
        cur_write_ops: u64,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> (Metric<f64>, Metric<f64>) {
        if let Some(p) = prev {
            let dt_ns = sampled_at_ns.saturating_sub(p.sampled_at_ns);
            let prov = MetricProvenance {
                source,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: dt_ns,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            if dt_ns > 0 && cur_read_ops >= p.read_ops && cur_write_ops >= p.write_ops {
                let dt_s = (dt_ns as f64) / 1_000_000_000.0;
                (
                    Metric::measured(((cur_read_ops - p.read_ops) as f64) / dt_s, prov),
                    Metric::measured(((cur_write_ops - p.write_ops) as f64) / dt_s, prov),
                )
            } else {
                (
                    Metric::unavailable(UnavailableReason::NotYetAvailable, prov),
                    Metric::unavailable(UnavailableReason::NotYetAvailable, prov),
                )
            }
        } else {
            let prov = MetricProvenance {
                source,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            (
                Metric::unavailable(UnavailableReason::NotYetAvailable, prov),
                Metric::unavailable(UnavailableReason::NotYetAvailable, prov),
            )
        }
    }

    /// Filters a Windows `GetIfTable2` (`MIB_IF_ROW2`) interface entry so loopback (`24`), tunnel (`131`),
    /// down interfaces (`OperStatus != 1`), non-hardware adapters (`bit 0x01 == 0`), and NDIS lightweight
    /// filter rows (`bit 0x02 != 0`) are excluded to prevent double-counting network traffic.
    pub const fn should_include_windows_if_row(
        if_type: u32,
        oper_status: i32,
        status_flags_bitfield: u8,
        _in_octets: u64,
        _out_octets: u64,
    ) -> bool {
        // IfOperStatusUp = 1, MIB_IF_TYPE_LOOPBACK = 24, IF_TYPE_TUNNEL = 131
        if oper_status != 1 || if_type == 24 || if_type == 131 {
            return false;
        }
        // InterfaceAndOperStatusFlags in netioapi.h:
        // bit 0 (0x01) = HardwareInterface
        // bit 1 (0x02) = FilterInterface
        // bit 2 (0x04) = ConnectorPresent
        let is_filter = (status_flags_bitfield & 0x02) != 0;
        if is_filter {
            return false;
        }
        (status_flags_bitfield & 0x01) != 0
    }

    /// Aggregates Windows PDH `\GPU Engine(*)\Utilization Percentage` instance values into 3D, Compute,
    /// Copy, Video Decode, Video Encode, and clamped overall GPU utilization `[0.0, 100.0]`.
    pub fn aggregate_pdh_gpu_engines<'a, I>(instances: I) -> GpuEngineBreakdown
    where
        I: IntoIterator<Item = (&'a str, f64)>,
    {
        let mut e3d = 0.0f64;
        let mut ecompute = 0.0f64;
        let mut ecopy = 0.0f64;
        let mut edec = 0.0f64;
        let mut eenc = 0.0f64;
        let mut eother = 0.0f64;

        for (name, val) in instances {
            if !val.is_finite() || val <= 0.0 {
                continue;
            }
            let lower = name.to_ascii_lowercase();
            if lower.contains("engtype_3d") || lower.contains("engtype_graphics") {
                e3d += val;
            } else if lower.contains("engtype_compute") || lower.contains("engtype_cuda") {
                ecompute += val;
            } else if lower.contains("engtype_copy") {
                ecopy += val;
            } else if lower.contains("engtype_videodecode") || lower.contains("engtype_dec") {
                edec += val;
            } else if lower.contains("engtype_videoencode") || lower.contains("engtype_enc") {
                eenc += val;
            } else if lower.contains("engtype_videoprocessing") {
                edec += val * 0.5;
            } else {
                eother += val;
            }
        }

        let e3d_c = e3d.clamp(0.0, 100.0);
        let ecompute_c = ecompute.clamp(0.0, 100.0);
        let ecopy_c = ecopy.clamp(0.0, 100.0);
        let edec_c = edec.clamp(0.0, 100.0);
        let eenc_c = eenc.clamp(0.0, 100.0);
        let overall = e3d_c
            .max(ecompute_c)
            .max(ecopy_c)
            .max(edec_c)
            .max(eenc_c)
            .max(eother.clamp(0.0, 100.0));

        GpuEngineBreakdown {
            engine_3d_percent: e3d_c,
            engine_compute_percent: ecompute_c,
            engine_copy_percent: ecopy_c,
            engine_video_decode_percent: edec_c,
            engine_video_encode_percent: eenc_c,
            overall_percent: overall,
        }
    }

    /// Converts an ACPI / PDH `\Thermal Zone Information(*)\Temperature` reading in Kelvin (or tenths of Kelvin)
    /// to Celsius, rejecting uninitialized stubs (`<= 0 C`, `>= 150 C`, or the static `273.15 K` / `300.95 K` Windows stub when flagged).
    pub fn acpi_kelvin_to_valid_celsius(raw_kelvin: f64) -> Option<f64> {
        if !raw_kelvin.is_finite() || raw_kelvin <= 0.0 {
            return None;
        }
        let kelvin = if raw_kelvin > 1000.0 {
            raw_kelvin / 10.0
        } else {
            raw_kelvin
        };
        let celsius = kelvin - 273.15;
        // Reject 0.0 C (273.15 K) and the classic Windows static 27.8 C (300.95 K / 301.0 K) ACPI stub
        if !(5.0..=130.0).contains(&celsius) || (celsius - 27.85).abs() < 0.25 {
            return None;
        }
        Some(celsius)
    }
}

/// Reads a PID-reuse-safe `ProcessKey(pid, creation_token_us, None)`, `ppid`, and `name` on Windows.
pub fn read_process_key(pid: i32) -> Option<(ProcessKey, i32, CompactString)> {
    #[cfg(target_os = "windows")]
    {
        win32::read_process_key_win32(pid)
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = pid;
        None
    }
}

#[cfg(target_os = "windows")]
#[allow(clippy::upper_case_acronyms)]
mod win32 {
    use super::*;
    use std::ffi::{c_char, c_int, c_uint, c_void};
    use std::mem::size_of;
    use std::path::PathBuf;
    use std::ptr;
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        FreeMibTable, GetIfTable2, MIB_IF_TABLE2,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
    };
    use windows_sys::Win32::System::IO::DeviceIoControl;
    use windows_sys::Win32::System::Ioctl::{DISK_PERFORMANCE, IOCTL_DISK_PERFORMANCE};
    use windows_sys::Win32::System::Performance::{
        PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_DOUBLE, PDH_HCOUNTER, PDH_HQUERY, PDH_MORE_DATA,
        PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterArrayW,
        PdhOpenQueryW,
    };
    use windows_sys::Win32::System::Power::{CallNtPowerInformation, ProcessorInformation};
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX,
    };
    use windows_sys::Win32::System::Threading::{GetProcessIoCounters, IO_COUNTERS};

    const TH32CS_SNAPPROCESS: u32 = 0x0000_0002;
    const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
    const ERROR_ACCESS_DENIED: u32 = 5;
    const DRIVE_REMOVABLE: u32 = 2;
    const DRIVE_FIXED: u32 = 3;
    const DRIVE_RAMDISK: u32 = 6;
    const SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION_CLASS: u32 = 8;

    #[repr(C)]
    #[derive(Default, Clone, Copy)]
    struct FILETIME {
        dw_low_date_time: u32,
        dw_high_date_time: u32,
    }

    #[repr(C)]
    #[derive(Default, Clone, Copy)]
    struct MEMORYSTATUSEX {
        dw_length: u32,
        dw_memory_load: u32,
        ull_total_phys: u64,
        ull_avail_phys: u64,
        ull_total_page_file: u64,
        ull_avail_page_file: u64,
        ull_total_virtual: u64,
        ull_avail_virtual: u64,
        ull_avail_extended_virtual: u64,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct SYSTEM_INFO {
        dw_oem_id: u32,
        dw_page_size: u32,
        lp_minimum_application_address: *mut c_void,
        lp_maximum_application_address: *mut c_void,
        dw_active_processor_mask: usize,
        dw_number_of_processors: u32,
        dw_processor_type: u32,
        dw_allocation_granularity: u32,
        w_processor_level: u16,
        w_processor_revision: u16,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct PROCESSENTRY32W {
        dw_size: u32,
        cnt_usage: u32,
        th32_process_id: u32,
        th32_default_heap_id: usize,
        th32_module_id: u32,
        cnt_threads: u32,
        th32_parent_process_id: u32,
        pc_pri_class_base: i32,
        dw_flags: u32,
        sz_exe_file: [u16; 260],
    }

    #[repr(C)]
    #[derive(Default, Clone, Copy)]
    struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION {
        idle_time: i64,
        kernel_time: i64,
        user_time: i64,
        reserved1: [i64; 2],
        reserved2: u32,
    }

    #[repr(C)]
    #[derive(Default, Clone, Copy)]
    struct PROCESSOR_POWER_INFORMATION {
        number: u32,
        max_mhz: u32,
        current_mhz: u32,
        mhz_limit: u32,
        max_idle_state: u32,
        current_idle_state: u32,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetSystemTimes(
            lp_idle_time: *mut FILETIME,
            lp_kernel_time: *mut FILETIME,
            lp_user_time: *mut FILETIME,
        ) -> i32;
        fn GetSystemInfo(lp_system_info: *mut SYSTEM_INFO);
        fn GlobalMemoryStatusEx(lp_buffer: *mut MEMORYSTATUSEX) -> i32;
        fn GetTickCount64() -> u64;
        fn GetLogicalDriveStringsW(n_buffer_length: u32, lp_buffer: *mut u16) -> u32;
        fn GetDriveTypeW(lp_root_path_name: *const u16) -> u32;
        fn GetDiskFreeSpaceExW(
            lp_directory_name: *const u16,
            lp_free_bytes_available_to_caller: *mut u64,
            lp_total_number_of_bytes: *mut u64,
            lp_total_number_of_free_bytes: *mut u64,
        ) -> i32;
        fn CreateToolhelp32Snapshot(dw_flags: u32, th32_process_id: u32) -> *mut c_void;
        fn Process32FirstW(h_snapshot: *mut c_void, lppe: *mut PROCESSENTRY32W) -> i32;
        fn Process32NextW(h_snapshot: *mut c_void, lppe: *mut PROCESSENTRY32W) -> i32;
        fn OpenProcess(
            dw_desired_access: u32,
            b_inherit_handle: i32,
            dw_process_id: u32,
        ) -> *mut c_void;
        fn GetProcessTimes(
            h_process: *mut c_void,
            lp_creation_time: *mut FILETIME,
            lp_exit_time: *mut FILETIME,
            lp_kernel_time: *mut FILETIME,
            lp_user_time: *mut FILETIME,
        ) -> i32;
        fn QueryFullProcessImageNameW(
            h_process: *mut c_void,
            dw_flags: u32,
            lp_exe_name: *mut u16,
            lpdw_size: *mut u32,
        ) -> i32;
        fn ProcessIdToSessionId(dw_process_id: u32, p_session_id: *mut u32) -> i32;
        fn GetCurrentProcess() -> *mut c_void;
        fn K32EmptyWorkingSet(h_process: *mut c_void) -> i32;
        fn GetLastError() -> u32;
        fn CloseHandle(h_object: *mut c_void) -> i32;
    }

    #[link(name = "ntdll")]
    unsafe extern "system" {
        fn NtQuerySystemInformation(
            system_information_class: u32,
            system_information: *mut c_void,
            system_information_length: u32,
            return_length: *mut u32,
        ) -> i32;
    }

    struct HandleGuard(*mut c_void);
    impl Drop for HandleGuard {
        fn drop(&mut self) {
            if !self.0.is_null() && self.0 != (-1isize as *mut c_void) {
                // SAFETY: `self.0` is a valid non-null Win32 handle.
                unsafe {
                    let _ = CloseHandle(self.0);
                }
            }
        }
    }

    #[inline]
    fn filetime_to_u64(ft: FILETIME) -> u64 {
        (u64::from(ft.dw_high_date_time) << 32) | u64::from(ft.dw_low_date_time)
    }

    #[inline]
    fn make_win32_process_key(pid_u32: u32, filetime_100ns: u64) -> ProcessKey {
        let mut session_id: u32 = 0;
        // SAFETY: `session_id` is a valid stack `u32`. Session 0 = system/service daemons (uid 0);
        // Session >= 1 = interactive desktop user applications (uid 1000).
        let uid = if pid_u32 > 4
            && unsafe { ProcessIdToSessionId(pid_u32, &raw mut session_id) } != 0
            && session_id >= 1
        {
            Some(1000)
        } else {
            Some(0)
        };
        ProcessKey {
            pid: pid_u32 as i32,
            creation_token_us: filetime_100ns / 10,
            uid,
        }
    }

    fn utf16_z_to_compact(buf: &[u16]) -> CompactString {
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        CompactString::from(String::from_utf16_lossy(&buf[..len]))
    }

    fn to_wide_z(s: &str) -> Vec<u16> {
        let mut v: Vec<u16> = s.encode_utf16().collect();
        v.push(0);
        v
    }

    unsafe fn pwstr_to_string(ptr: *const u16) -> String {
        if ptr.is_null() {
            return String::new();
        }
        let mut len = 0usize;
        while len < 512 && unsafe { *ptr.add(len) } != 0 {
            len += 1;
        }
        let slice = unsafe { std::slice::from_raw_parts(ptr, len) };
        String::from_utf16_lossy(slice)
    }

    // -------------------------------------------------------------------------
    // Dynamic NVML FFI (`nvml.dll`)
    // -------------------------------------------------------------------------
    type NvmlDevice = *mut c_void;
    type FnNvmlInit = unsafe extern "C" fn() -> c_int;
    type FnNvmlShutdown = unsafe extern "C" fn() -> c_int;
    type FnNvmlDeviceGetCount = unsafe extern "C" fn(*mut c_uint) -> c_int;
    type FnNvmlDeviceGetHandleByIndex = unsafe extern "C" fn(c_uint, *mut NvmlDevice) -> c_int;
    type FnNvmlDeviceGetName = unsafe extern "C" fn(NvmlDevice, *mut c_char, c_uint) -> c_int;
    type FnNvmlDeviceGetUtilizationRates =
        unsafe extern "C" fn(NvmlDevice, *mut NvmlUtilization) -> c_int;
    type FnNvmlDeviceGetMemoryInfo = unsafe extern "C" fn(NvmlDevice, *mut NvmlMemory) -> c_int;
    type FnNvmlDeviceGetTemperature =
        unsafe extern "C" fn(NvmlDevice, c_uint, *mut c_uint) -> c_int;
    type FnNvmlDeviceGetPowerUsage = unsafe extern "C" fn(NvmlDevice, *mut c_uint) -> c_int;
    type FnNvmlDeviceGetClockInfo = unsafe extern "C" fn(NvmlDevice, c_uint, *mut c_uint) -> c_int;
    type FnNvmlDeviceGetFanSpeed = unsafe extern "C" fn(NvmlDevice, *mut c_uint) -> c_int;
    type FnNvmlDeviceGetEncoderUtil =
        unsafe extern "C" fn(NvmlDevice, *mut c_uint, *mut c_uint) -> c_int;
    type FnNvmlDeviceGetDecoderUtil =
        unsafe extern "C" fn(NvmlDevice, *mut c_uint, *mut c_uint) -> c_int;

    pub(super) struct NvmlSession {
        _lib: libloading::Library,
        device: NvmlDevice,
        pub adapter_name: CompactString,
        fn_shutdown: Option<FnNvmlShutdown>,
        fn_util: FnNvmlDeviceGetUtilizationRates,
        fn_mem: FnNvmlDeviceGetMemoryInfo,
        fn_temp: Option<FnNvmlDeviceGetTemperature>,
        fn_power: Option<FnNvmlDeviceGetPowerUsage>,
        fn_clock: Option<FnNvmlDeviceGetClockInfo>,
        fn_fan: Option<FnNvmlDeviceGetFanSpeed>,
        fn_enc: Option<FnNvmlDeviceGetEncoderUtil>,
        fn_dec: Option<FnNvmlDeviceGetDecoderUtil>,
    }

    // SAFETY: `NvmlSession` holds function pointers and an opaque NVML device handle protected by `WindowsCollector::inner` Mutex.
    unsafe impl Send for NvmlSession {}

    impl Drop for NvmlSession {
        fn drop(&mut self) {
            if let Some(shutdown) = self.fn_shutdown {
                // SAFETY: `nvmlInit_v2` succeeded during `NvmlSession::try_load`.
                unsafe {
                    let _ = shutdown();
                }
            }
        }
    }

    pub(super) struct NvmlSample {
        pub adapter_name: CompactString,
        pub gpu_util_percent: Option<f64>,
        pub mem_ctrl_util_percent: Option<f64>,
        pub vram_used_bytes: Option<f64>,
        pub vram_total_bytes: Option<f64>,
        pub temp_celsius: Option<f64>,
        pub power_watts: Option<f64>,
        pub graphics_clock_mhz: Option<f64>,
        pub mem_clock_mhz: Option<f64>,
        pub fan_percent: Option<f64>,
        pub encoder_util_percent: Option<f64>,
        pub decoder_util_percent: Option<f64>,
    }

    impl NvmlSession {
        pub fn try_load() -> Option<Self> {
            let sys_root =
                std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".to_string());
            let prog_files =
                std::env::var("ProgramW6432").unwrap_or_else(|_| "C:\\Program Files".to_string());
            let candidates = [
                PathBuf::from(&sys_root).join("System32\\nvml.dll"),
                PathBuf::from(&prog_files).join("NVIDIA Corporation\\NVSMI\\nvml.dll"),
            ];
            let lib = candidates
                .iter()
                .filter(|p| p.exists())
                .find_map(|p| unsafe { libloading::Library::new(p).ok() })?;

            unsafe {
                let fn_init: FnNvmlInit = *lib
                    .get(b"nvmlInit_v2\0")
                    .or_else(|_| lib.get(b"nvmlInit\0"))
                    .ok()?;
                if fn_init() != 0 {
                    return None;
                }
                let fn_shutdown: Option<FnNvmlShutdown> =
                    lib.get(b"nvmlShutdown\0").ok().map(|s| *s);
                let fn_count: FnNvmlDeviceGetCount = *lib
                    .get(b"nvmlDeviceGetCount_v2\0")
                    .or_else(|_| lib.get(b"nvmlDeviceGetCount\0"))
                    .ok()?;
                let mut count: c_uint = 0;
                if fn_count(&raw mut count) != 0 || count == 0 {
                    if let Some(sh) = fn_shutdown {
                        let _ = sh();
                    }
                    return None;
                }
                let fn_handle: FnNvmlDeviceGetHandleByIndex = *lib
                    .get(b"nvmlDeviceGetHandleByIndex_v2\0")
                    .or_else(|_| lib.get(b"nvmlDeviceGetHandleByIndex\0"))
                    .ok()?;
                let mut device: NvmlDevice = ptr::null_mut();
                if fn_handle(0, &raw mut device) != 0 || device.is_null() {
                    if let Some(sh) = fn_shutdown {
                        let _ = sh();
                    }
                    return None;
                }
                let adapter_name = if let Ok(fn_name) =
                    lib.get::<FnNvmlDeviceGetName>(b"nvmlDeviceGetName\0")
                {
                    let mut name_buf = [0 as c_char; 96];
                    if (*fn_name)(device, name_buf.as_mut_ptr(), name_buf.len() as c_uint) == 0 {
                        let bytes: Vec<u8> = name_buf
                            .iter()
                            .take_while(|&&c| c != 0)
                            .map(|&c| c as u8)
                            .collect();
                        let s = String::from_utf8_lossy(&bytes).trim().to_string();
                        if s.is_empty() {
                            CompactString::const_new("NVIDIA GeForce GPU")
                        } else {
                            CompactString::from(s)
                        }
                    } else {
                        CompactString::const_new("NVIDIA GeForce GPU")
                    }
                } else {
                    CompactString::const_new("NVIDIA GeForce GPU")
                };

                let fn_util: FnNvmlDeviceGetUtilizationRates =
                    match lib.get(b"nvmlDeviceGetUtilizationRates\0") {
                        Ok(s) => *s,
                        Err(_) => {
                            if let Some(sh) = fn_shutdown {
                                let _ = sh();
                            }
                            return None;
                        }
                    };
                let fn_mem: FnNvmlDeviceGetMemoryInfo = match lib.get(b"nvmlDeviceGetMemoryInfo\0")
                {
                    Ok(s) => *s,
                    Err(_) => {
                        if let Some(sh) = fn_shutdown {
                            let _ = sh();
                        }
                        return None;
                    }
                };
                let fn_temp = lib
                    .get::<FnNvmlDeviceGetTemperature>(b"nvmlDeviceGetTemperature\0")
                    .ok()
                    .map(|s| *s);
                let fn_power = lib
                    .get::<FnNvmlDeviceGetPowerUsage>(b"nvmlDeviceGetPowerUsage\0")
                    .ok()
                    .map(|s| *s);
                let fn_clock = lib
                    .get::<FnNvmlDeviceGetClockInfo>(b"nvmlDeviceGetClockInfo\0")
                    .ok()
                    .map(|s| *s);
                let fn_fan = lib
                    .get::<FnNvmlDeviceGetFanSpeed>(b"nvmlDeviceGetFanSpeed\0")
                    .ok()
                    .map(|s| *s);
                let fn_enc = lib
                    .get::<FnNvmlDeviceGetEncoderUtil>(b"nvmlDeviceGetEncoderUtilization\0")
                    .ok()
                    .map(|s| *s);
                let fn_dec = lib
                    .get::<FnNvmlDeviceGetDecoderUtil>(b"nvmlDeviceGetDecoderUtilization\0")
                    .ok()
                    .map(|s| *s);

                Some(Self {
                    _lib: lib,
                    device,
                    adapter_name,
                    fn_shutdown,
                    fn_util,
                    fn_mem,
                    fn_temp,
                    fn_power,
                    fn_clock,
                    fn_fan,
                    fn_enc,
                    fn_dec,
                })
            }
        }

        pub fn sample(&self) -> NvmlSample {
            unsafe {
                let mut util = NvmlUtilization::default();
                let (gpu_util_percent, mem_ctrl_util_percent) =
                    if (self.fn_util)(self.device, &raw mut util) == 0 {
                        (
                            Some(f64::from(util.gpu).clamp(0.0, 100.0)),
                            Some(f64::from(util.memory).clamp(0.0, 100.0)),
                        )
                    } else {
                        (None, None)
                    };

                let mut mem = NvmlMemory::default();
                let (vram_used_bytes, vram_total_bytes) =
                    if (self.fn_mem)(self.device, &raw mut mem) == 0 && mem.total > 0 {
                        (Some(mem.used as f64), Some(mem.total as f64))
                    } else {
                        (None, None)
                    };

                // NVML_TEMPERATURE_GPU = 0
                let temp_celsius = self.fn_temp.and_then(|f| {
                    let mut t: c_uint = 0;
                    if f(self.device, 0, &raw mut t) == 0 && (1..=130).contains(&t) {
                        Some(f64::from(t))
                    } else {
                        None
                    }
                });

                let power_watts = self.fn_power.and_then(|f| {
                    let mut mw: c_uint = 0;
                    if f(self.device, &raw mut mw) == 0 && mw > 0 {
                        Some(f64::from(mw) / 1000.0)
                    } else {
                        None
                    }
                });

                // NVML_CLOCK_GRAPHICS = 0, NVML_CLOCK_MEM = 2
                let graphics_clock_mhz = self.fn_clock.and_then(|f| {
                    let mut mhz: c_uint = 0;
                    if f(self.device, 0, &raw mut mhz) == 0 && mhz > 0 {
                        Some(f64::from(mhz))
                    } else {
                        None
                    }
                });
                let mem_clock_mhz = self.fn_clock.and_then(|f| {
                    let mut mhz: c_uint = 0;
                    if f(self.device, 2, &raw mut mhz) == 0 && mhz > 0 {
                        Some(f64::from(mhz))
                    } else {
                        None
                    }
                });

                let fan_percent = self.fn_fan.and_then(|f| {
                    let mut pct: c_uint = 0;
                    if f(self.device, &raw mut pct) == 0 {
                        Some(f64::from(pct).clamp(0.0, 100.0))
                    } else {
                        None
                    }
                });

                let encoder_util_percent = self.fn_enc.and_then(|f| {
                    let mut u: c_uint = 0;
                    let mut period: c_uint = 0;
                    if f(self.device, &raw mut u, &raw mut period) == 0 {
                        Some(f64::from(u).clamp(0.0, 100.0))
                    } else {
                        None
                    }
                });
                let decoder_util_percent = self.fn_dec.and_then(|f| {
                    let mut u: c_uint = 0;
                    let mut period: c_uint = 0;
                    if f(self.device, &raw mut u, &raw mut period) == 0 {
                        Some(f64::from(u).clamp(0.0, 100.0))
                    } else {
                        None
                    }
                });

                NvmlSample {
                    adapter_name: self.adapter_name.clone(),
                    gpu_util_percent,
                    mem_ctrl_util_percent,
                    vram_used_bytes,
                    vram_total_bytes,
                    temp_celsius,
                    power_watts,
                    graphics_clock_mhz,
                    mem_clock_mhz,
                    fan_percent,
                    encoder_util_percent,
                    decoder_util_percent,
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // DXGI Adapter Fallback (`dxgi.dll` -> `CreateDXGIFactory1` -> `IDXGIAdapter1::GetDesc1`)
    // -------------------------------------------------------------------------
    #[derive(Debug, Clone)]
    pub(super) struct DxgiAdapterSummary {
        pub adapter_name: CompactString,
        pub dedicated_vram_bytes: u64,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct Guid {
        data1: u32,
        data2: u16,
        data3: u16,
        data4: [u8; 8],
    }

    const IID_IDXGIFACTORY1: Guid = Guid {
        data1: 0x770a_ae78,
        data2: 0xf26f,
        data3: 0x4dba,
        data4: [0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87],
    };

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct Luid {
        low_part: u32,
        high_part: i32,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct DxgiAdapterDesc1 {
        description: [u16; 128],
        vendor_id: u32,
        device_id: u32,
        sub_sys_id: u32,
        revision: u32,
        dedicated_video_memory: usize,
        dedicated_system_memory: usize,
        shared_system_memory: usize,
        adapter_luid: Luid,
        flags: u32,
    }

    #[repr(C)]
    struct IDXGIFactory1Vtbl {
        // IUnknown (3)
        query_interface: usize,
        add_ref: usize,
        release: unsafe extern "system" fn(*mut c_void) -> u32,
        // IDXGIObject (4)
        set_private_data: usize,
        set_private_data_interface: usize,
        get_private_data: usize,
        get_parent: usize,
        // IDXGIFactory (5)
        enum_adapters: usize,
        make_window_association: usize,
        get_window_association: usize,
        create_swap_chain: usize,
        create_software_adapter: usize,
        // IDXGIFactory1 (2)
        enum_adapters1: unsafe extern "system" fn(*mut c_void, u32, *mut *mut c_void) -> i32,
        is_current: usize,
    }

    #[repr(C)]
    struct IDXGIAdapter1Vtbl {
        // IUnknown (3)
        query_interface: usize,
        add_ref: usize,
        release: unsafe extern "system" fn(*mut c_void) -> u32,
        // IDXGIObject (4)
        set_private_data: usize,
        set_private_data_interface: usize,
        get_private_data: usize,
        get_parent: usize,
        // IDXGIAdapter (3)
        enum_outputs: usize,
        get_desc: usize,
        check_interface_support: usize,
        // IDXGIAdapter1 (1)
        get_desc1: unsafe extern "system" fn(*mut c_void, *mut DxgiAdapterDesc1) -> i32,
    }

    #[link(name = "dxgi")]
    unsafe extern "system" {
        fn CreateDXGIFactory1(riid: *const Guid, pp_factory: *mut *mut c_void) -> i32;
    }

    pub(super) fn probe_best_dxgi_adapter() -> Option<DxgiAdapterSummary> {
        const DXGI_ADAPTER_FLAG_SOFTWARE: u32 = 2;
        unsafe {
            let iid = IID_IDXGIFACTORY1;
            let mut factory: *mut c_void = ptr::null_mut();
            if CreateDXGIFactory1(&raw const iid, &raw mut factory) < 0 || factory.is_null() {
                return None;
            }
            let factory_vtbl = *(factory as *const *const IDXGIFactory1Vtbl);
            let mut best: Option<DxgiAdapterSummary> = None;

            for idx in 0..8u32 {
                let mut adapter: *mut c_void = ptr::null_mut();
                let hr = ((*factory_vtbl).enum_adapters1)(factory, idx, &raw mut adapter);
                if hr < 0 || adapter.is_null() {
                    break;
                }
                let adapter_vtbl = *(adapter as *const *const IDXGIAdapter1Vtbl);
                let mut desc: DxgiAdapterDesc1 = std::mem::zeroed();
                if ((*adapter_vtbl).get_desc1)(adapter, &raw mut desc) >= 0 {
                    let is_software = (desc.flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                    let name = utf16_z_to_compact(&desc.description);
                    if !is_software
                        && !name.is_empty()
                        && !name.eq_ignore_ascii_case("Microsoft Basic Render Driver")
                    {
                        let vram = desc.dedicated_video_memory as u64;
                        if best.as_ref().is_none_or(|b| vram > b.dedicated_vram_bytes) {
                            best = Some(DxgiAdapterSummary {
                                adapter_name: name,
                                dedicated_vram_bytes: vram,
                            });
                        }
                    }
                }
                let _ = ((*adapter_vtbl).release)(adapter);
            }
            let _ = ((*factory_vtbl).release)(factory);
            best
        }
    }

    // -------------------------------------------------------------------------
    // Persistent PDH Session (`pdh.dll`) for Disk Rates, GPU Engine/VRAM Fallback & Thermal Zones
    // -------------------------------------------------------------------------
    pub(super) struct PdhSession {
        query: PDH_HQUERY,
        c_disk_read_bytes: Option<PDH_HCOUNTER>,
        c_disk_write_bytes: Option<PDH_HCOUNTER>,
        c_disk_read_ops: Option<PDH_HCOUNTER>,
        c_disk_write_ops: Option<PDH_HCOUNTER>,
        c_gpu_engine: Option<PDH_HCOUNTER>,
        c_gpu_vram_used: Option<PDH_HCOUNTER>,
        c_thermal_temp: Option<PDH_HCOUNTER>,
        primed: bool,
        scratch_buf: Vec<u64>,
    }

    // SAFETY: `PdhSession` owns a Win32 `PDH_HQUERY` accessed exclusively under `WindowsCollector::inner` Mutex.
    unsafe impl Send for PdhSession {}

    impl Drop for PdhSession {
        fn drop(&mut self) {
            if !self.query.is_null() {
                unsafe {
                    let _ = PdhCloseQuery(self.query);
                }
            }
        }
    }

    #[derive(Default)]
    pub(super) struct PdhTickSample {
        pub disk_rates: HashMap<CompactString, (f64, f64, f64, f64)>,
        pub gpu_engines: Option<GpuEngineBreakdown>,
        pub gpu_dedicated_vram_used_bytes: Option<f64>,
        pub thermal_zones: Vec<(CompactString, f64)>,
    }

    impl PdhSession {
        pub fn try_open(include_gpu_counters: bool) -> Option<Self> {
            unsafe {
                let mut query: PDH_HQUERY = ptr::null_mut();
                if PdhOpenQueryW(ptr::null(), 0, &raw mut query) != 0 || query.is_null() {
                    return None;
                }
                let add_counter = |path: &str| -> Option<PDH_HCOUNTER> {
                    let wide = to_wide_z(path);
                    let mut hc: PDH_HCOUNTER = ptr::null_mut();
                    if PdhAddEnglishCounterW(query, wide.as_ptr(), 0, &raw mut hc) == 0
                        && !hc.is_null()
                    {
                        Some(hc)
                    } else {
                        None
                    }
                };

                let c_disk_read_bytes = add_counter("\\LogicalDisk(*)\\Disk Read Bytes/sec");
                let c_disk_write_bytes = add_counter("\\LogicalDisk(*)\\Disk Write Bytes/sec");
                let c_disk_read_ops = add_counter("\\LogicalDisk(*)\\Disk Reads/sec");
                let c_disk_write_ops = add_counter("\\LogicalDisk(*)\\Disk Writes/sec");
                let c_gpu_engine = if include_gpu_counters {
                    add_counter("\\GPU Engine(*)\\Utilization Percentage")
                } else {
                    None
                };
                let c_gpu_vram_used = if include_gpu_counters {
                    add_counter("\\GPU Adapter Memory(*)\\Dedicated Usage")
                } else {
                    None
                };
                let c_thermal_temp = add_counter("\\Thermal Zone Information(*)\\Temperature");

                // Prime baseline sample for rate counters
                let _ = PdhCollectQueryData(query);

                Some(Self {
                    query,
                    c_disk_read_bytes,
                    c_disk_write_bytes,
                    c_disk_read_ops,
                    c_disk_write_ops,
                    c_gpu_engine,
                    c_gpu_vram_used,
                    c_thermal_temp,
                    primed: false,
                    scratch_buf: Vec::with_capacity(2048),
                })
            }
        }

        fn read_counter_array(&mut self, counter: PDH_HCOUNTER) -> Vec<(String, f64)> {
            let mut out = Vec::new();
            unsafe {
                let mut buf_size: u32 = 0;
                let mut item_count: u32 = 0;
                let rc = PdhGetFormattedCounterArrayW(
                    counter,
                    PDH_FMT_DOUBLE,
                    &raw mut buf_size,
                    &raw mut item_count,
                    ptr::null_mut(),
                );
                if (rc != PDH_MORE_DATA && rc != 0) || buf_size == 0 || item_count == 0 {
                    return out;
                }
                let needed_bytes =
                    (buf_size as usize).max(size_of::<PDH_FMT_COUNTERVALUE_ITEM_W>());
                let needed_u64 = needed_bytes.div_ceil(size_of::<u64>());
                if self.scratch_buf.len() < needed_u64 {
                    self.scratch_buf.resize(needed_u64, 0);
                }
                let items_ptr = self
                    .scratch_buf
                    .as_mut_ptr()
                    .cast::<PDH_FMT_COUNTERVALUE_ITEM_W>();
                if PdhGetFormattedCounterArrayW(
                    counter,
                    PDH_FMT_DOUBLE,
                    &raw mut buf_size,
                    &raw mut item_count,
                    items_ptr,
                ) == 0
                {
                    let slice = std::slice::from_raw_parts(items_ptr, item_count as usize);
                    for item in slice {
                        if item.FmtValue.CStatus == 0 {
                            let name = pwstr_to_string(item.szName);
                            let val = item.FmtValue.Anonymous.doubleValue;
                            if val.is_finite() {
                                out.push((name, val));
                            }
                        }
                    }
                }
            }
            out
        }

        pub fn sample(&mut self, poll_gpu: bool) -> PdhTickSample {
            let mut res = PdhTickSample::default();
            unsafe {
                if PdhCollectQueryData(self.query) != 0 {
                    return res;
                }
            }
            let was_primed = self.primed;
            self.primed = true;

            if was_primed {
                if let Some(hc) = self.c_disk_read_bytes {
                    for (inst, val) in self.read_counter_array(hc) {
                        if inst != "_Total" && !inst.starts_with("HarddiskVolume") {
                            let entry = res
                                .disk_rates
                                .entry(CompactString::from(inst))
                                .or_insert((0.0, 0.0, 0.0, 0.0));
                            entry.0 = val.max(0.0);
                        }
                    }
                }
                if let Some(hc) = self.c_disk_write_bytes {
                    for (inst, val) in self.read_counter_array(hc) {
                        if inst != "_Total" && !inst.starts_with("HarddiskVolume") {
                            let entry = res
                                .disk_rates
                                .entry(CompactString::from(inst))
                                .or_insert((0.0, 0.0, 0.0, 0.0));
                            entry.1 = val.max(0.0);
                        }
                    }
                }
                if let Some(hc) = self.c_disk_read_ops {
                    for (inst, val) in self.read_counter_array(hc) {
                        if inst != "_Total" && !inst.starts_with("HarddiskVolume") {
                            let entry = res
                                .disk_rates
                                .entry(CompactString::from(inst))
                                .or_insert((0.0, 0.0, 0.0, 0.0));
                            entry.2 = val.max(0.0);
                        }
                    }
                }
                if let Some(hc) = self.c_disk_write_ops {
                    for (inst, val) in self.read_counter_array(hc) {
                        if inst != "_Total" && !inst.starts_with("HarddiskVolume") {
                            let entry = res
                                .disk_rates
                                .entry(CompactString::from(inst))
                                .or_insert((0.0, 0.0, 0.0, 0.0));
                            entry.3 = val.max(0.0);
                        }
                    }
                }
                if poll_gpu && let Some(hc) = self.c_gpu_engine {
                    let arr = self.read_counter_array(hc);
                    if !arr.is_empty() {
                        let pairs: Vec<(&str, f64)> =
                            arr.iter().map(|(s, v)| (s.as_str(), *v)).collect();
                        res.gpu_engines = Some(WindowsCollector::aggregate_pdh_gpu_engines(pairs));
                    }
                }
            }

            if poll_gpu && let Some(hc) = self.c_gpu_vram_used {
                let mut max_vram = 0.0f64;
                for (_, val) in self.read_counter_array(hc) {
                    if val > max_vram {
                        max_vram = val;
                    }
                }
                if max_vram > 0.0 {
                    res.gpu_dedicated_vram_used_bytes = Some(max_vram);
                }
            }

            if let Some(hc) = self.c_thermal_temp {
                for (inst, val) in self.read_counter_array(hc) {
                    if let Some(c) = WindowsCollector::acpi_kelvin_to_valid_celsius(val) {
                        let clean = inst
                            .trim_start_matches("\\_TZ.")
                            .trim_start_matches("\\_SB.")
                            .trim();
                        let label = if clean.is_empty() {
                            CompactString::const_new("ACPI Thermal Zone")
                        } else {
                            CompactString::from(format!("ACPI Thermal ({clean})"))
                        };
                        res.thermal_zones.push((label, c));
                    }
                }
            }

            res
        }
    }

    fn query_volume_disk_performance(bsd_name: &str) -> Option<(u64, u64, u64, u64)> {
        let dev_path = format!("\\\\.\\{bsd_name}");
        let wide = to_wide_z(&dev_path);
        unsafe {
            let h = CreateFileW(
                wide.as_ptr(),
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                ptr::null(),
                OPEN_EXISTING,
                0,
                ptr::null_mut(),
            );
            if h.is_null() || h == (-1isize as *mut c_void) {
                return None;
            }
            let _guard = HandleGuard(h);
            let mut dp = DISK_PERFORMANCE::default();
            let mut bytes_ret: u32 = 0;
            let ok = DeviceIoControl(
                h,
                IOCTL_DISK_PERFORMANCE,
                ptr::null(),
                0,
                (&raw mut dp).cast::<c_void>(),
                size_of::<DISK_PERFORMANCE>() as u32,
                &raw mut bytes_ret,
                ptr::null_mut(),
            );
            if ok != 0 && (dp.BytesRead >= 0 && dp.BytesWritten >= 0) {
                let r_bytes = dp.BytesRead as u64;
                let w_bytes = dp.BytesWritten as u64;
                let r_ops = u64::from(dp.ReadCount);
                let w_ops = u64::from(dp.WriteCount);
                if r_bytes > 0 || w_bytes > 0 || r_ops > 0 || w_ops > 0 {
                    return Some((r_bytes, w_bytes, r_ops, w_ops));
                }
            }
        }
        None
    }

    fn query_cpu_clock_mhz(logical_cores: u32) -> Option<f64> {
        let count = (logical_cores as usize).clamp(1, 256);
        let mut infos = vec![PROCESSOR_POWER_INFORMATION::default(); count];
        let out_bytes = (infos.len() * size_of::<PROCESSOR_POWER_INFORMATION>()) as u32;
        unsafe {
            if CallNtPowerInformation(
                ProcessorInformation,
                ptr::null(),
                0,
                infos.as_mut_ptr().cast::<c_void>(),
                out_bytes,
            ) == 0
            {
                let mut sum = 0u64;
                let mut n = 0u64;
                for info in &infos {
                    let mhz = if info.current_mhz > 0 {
                        info.current_mhz
                    } else {
                        info.max_mhz
                    };
                    if mhz > 0 {
                        sum += u64::from(mhz);
                        n += 1;
                    }
                }
                if n > 0 {
                    return Some((sum as f64) / (n as f64));
                }
            }
        }
        None
    }

    pub(super) fn read_process_key_win32(pid: i32) -> Option<(ProcessKey, i32, CompactString)> {
        if pid <= 0 {
            return None;
        }
        let target_pid = pid as u32;
        // SAFETY: Opens target process with query-limited access for creation time & image name.
        let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, target_pid) };
        if handle.is_null() {
            return None;
        }
        let _guard = HandleGuard(handle);

        let mut ct = FILETIME::default();
        let mut et = FILETIME::default();
        let mut kt = FILETIME::default();
        let mut ut = FILETIME::default();
        // SAFETY: `handle` is valid and pointers point to writable stack FILETIME structs.
        if unsafe { GetProcessTimes(handle, &raw mut ct, &raw mut et, &raw mut kt, &raw mut ut) }
            == 0
        {
            return None;
        }
        let key = make_win32_process_key(target_pid, filetime_to_u64(ct));

        let mut ppid = 0i32;
        let mut exe_name = CompactString::const_new("");
        // SAFETY: Snapshot handle is closed by `HandleGuard`.
        let snap_h = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
        if !snap_h.is_null() && snap_h != (-1isize as *mut c_void) {
            let _snap_guard = HandleGuard(snap_h);
            let mut pe = PROCESSENTRY32W {
                dw_size: size_of::<PROCESSENTRY32W>() as u32,
                cnt_usage: 0,
                th32_process_id: 0,
                th32_default_heap_id: 0,
                th32_module_id: 0,
                cnt_threads: 0,
                th32_parent_process_id: 0,
                pc_pri_class_base: 0,
                dw_flags: 0,
                sz_exe_file: [0; 260],
            };
            // SAFETY: `pe.dw_size` is initialized before `Process32FirstW`.
            if unsafe { Process32FirstW(snap_h, &raw mut pe) } != 0 {
                loop {
                    if pe.th32_process_id == target_pid {
                        ppid = pe.th32_parent_process_id as i32;
                        exe_name = utf16_z_to_compact(&pe.sz_exe_file);
                        break;
                    }
                    // SAFETY: `snap_h` and `pe` remain valid.
                    if unsafe { Process32NextW(snap_h, &raw mut pe) } == 0 {
                        break;
                    }
                }
            }
        }

        if exe_name.is_empty() {
            let mut path_buf = [0u16; 512];
            let mut len = path_buf.len() as u32;
            // SAFETY: `path_buf` has `len` writable UTF-16 code units.
            if unsafe { QueryFullProcessImageNameW(handle, 0, path_buf.as_mut_ptr(), &raw mut len) }
                != 0
                && len > 0
            {
                let full = String::from_utf16_lossy(&path_buf[..len as usize]);
                let base = std::path::Path::new(&full)
                    .file_name()
                    .and_then(|s| s.to_str())
                    .unwrap_or(&full);
                exe_name = CompactString::from(base);
            }
        }

        Some((key, ppid, exe_name))
    }

    impl WindowsCollector {
        pub(super) fn collect_windows_host(
            state: &mut WindowsCollectorInner,
            snap: &mut SystemSnapshot,
            sampled_at_ns: u64,
        ) {
            let src_cpu = snap.intern_source("windows.win32.system_times");
            let src_mem = snap.intern_source("windows.win32.global_memory_status");
            let src_up = snap.intern_source("windows.win32.get_tick_count64");
            let src_disk = snap.intern_source("windows.win32.disk_free_space");
            let src_disk_io = snap.intern_source("windows.ioctl.disk_performance");
            let src_proc = snap.intern_source("windows.win32.toolhelp_psapi");
            let src_nvml = snap.intern_source("windows.nvml.gpu");
            let src_d3dkmt = snap.intern_source("windows.d3dkmt.gpu");
            let src_if_table2 = snap.intern_source("windows.iphlpapi.if_table2");
            let src_thermal = snap.intern_source("windows.wmi.thermal");
            let src_power = snap.intern_source("windows.win32.power_information");

            // Lazy one-time probes for NVML, DXGI, and PDH sessions
            if !state.nvml_probed {
                state.nvml_probed = true;
                state.nvml = NvmlSession::try_load();
            }
            if !state.dxgi_probed {
                state.dxgi_probed = true;
                state.dxgi_cached = probe_best_dxgi_adapter();
            }
            if !state.pdh_probed {
                state.pdh_probed = true;
                let need_pdh_gpu = state.nvml.is_none();
                state.pdh = PdhSession::try_open(need_pdh_gpu);
            }

            // Trim one-time DirectX shader compiler working-set pages after initial window creation
            if snap.generation == 3 {
                // SAFETY: `GetCurrentProcess()` returns the pseudo-handle for the current process.
                unsafe {
                    let _ = K32EmptyWorkingSet(GetCurrentProcess());
                }
            }

            let poll_pdh_gpu = state.nvml.is_none();
            let should_poll_pdh = poll_pdh_gpu || snap.generation <= 2 || snap.generation % 8 == 1;
            let pdh_sample = if should_poll_pdh {
                state
                    .pdh
                    .as_mut()
                    .map(|p| p.sample(poll_pdh_gpu))
                    .unwrap_or_default()
            } else {
                PdhTickSample::default()
            };

            let mut cpu_ok = false;
            let mut mem_ok = false;

            // 1. CPU & logical cores
            let mut sys_info = SYSTEM_INFO {
                dw_oem_id: 0,
                dw_page_size: 0,
                lp_minimum_application_address: ptr::null_mut(),
                lp_maximum_application_address: ptr::null_mut(),
                dw_active_processor_mask: 0,
                dw_number_of_processors: 0,
                dw_processor_type: 0,
                dw_allocation_granularity: 0,
                w_processor_level: 0,
                w_processor_revision: 0,
            };
            // SAFETY: `sys_info` is a valid stack `SYSTEM_INFO`.
            unsafe { GetSystemInfo(&raw mut sys_info) };
            let logical_cores = sys_info.dw_number_of_processors.max(1);
            snap.system.performance_core_count = logical_cores;
            snap.system.efficiency_core_count = 0;
            snap.system.gpu_model = CompactString::const_new("Windows Display Adapter");

            let window_ns = state
                .prev_cpu_sampled_at_ns
                .map(|p| sampled_at_ns.saturating_sub(p))
                .unwrap_or(0);
            let pct_prov = MetricProvenance {
                source: src_cpu,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            let mut idle_ft = FILETIME::default();
            let mut kernel_ft = FILETIME::default();
            let mut user_ft = FILETIME::default();
            // SAFETY: All three FILETIME pointers point to stack variables.
            if unsafe { GetSystemTimes(&raw mut idle_ft, &raw mut kernel_ft, &raw mut user_ft) }
                != 0
            {
                cpu_ok = true;
                let cur = (
                    filetime_to_u64(idle_ft),
                    filetime_to_u64(kernel_ft),
                    filetime_to_u64(user_ft),
                );
                state.prev_cpu_sampled_at_ns = Some(sampled_at_ns);
                if let Some(prev) = state.prev_idle_ticks.replace(cur) {
                    let (tot, usr, sys) = Self::compute_system_times_delta(
                        prev,
                        cur,
                        sampled_at_ns,
                        window_ns,
                        src_cpu,
                    );
                    snap.system.cpu_percent = tot;
                    snap.system.cpu_user_percent = usr;
                    snap.system.cpu_kernel_percent = sys;
                } else {
                    snap.system.cpu_percent =
                        Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                    snap.system.cpu_user_percent =
                        Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                    snap.system.cpu_kernel_percent =
                        Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                }
            }

            if let MetricReading::Measured { value: cpu_pct, .. } = snap.system.cpu_percent.reading
            {
                snap.hardware_telemetry
                    .soc_power
                    .p_cluster_active_residency_percent = Metric::measured(cpu_pct, pct_prov);
            }

            if let Some(cpu_mhz) = query_cpu_clock_mhz(logical_cores) {
                let mhz_prov = MetricProvenance {
                    source: src_power,
                    unit_tag: Some(UnitTag::MEGAHERTZ),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                snap.hardware_telemetry.soc_power.p_cluster_mhz =
                    Metric::measured(cpu_mhz, mhz_prov);
                snap.hardware_telemetry
                    .soc_power
                    .cluster_clocks
                    .push(ClusterClockMetric {
                        cluster_name: CompactString::const_new("CPU Package Clock"),
                        is_performance: true,
                        effective_mhz: Metric::measured(cpu_mhz, mhz_prov),
                        active_residency_percent: snap
                            .hardware_telemetry
                            .soc_power
                            .p_cluster_active_residency_percent,
                    });
            }

            // Per-core CPU via NtQuerySystemInformation(SystemProcessorPerformanceInformation = 8)
            let mut perf_buf =
                vec![SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION::default(); logical_cores as usize];
            let buf_bytes =
                (perf_buf.len() * size_of::<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>()) as u32;
            let mut ret_len: u32 = 0;
            // SAFETY: `perf_buf` is sized for `logical_cores` entries.
            let nt_status = unsafe {
                NtQuerySystemInformation(
                    SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION_CLASS,
                    perf_buf.as_mut_ptr().cast::<c_void>(),
                    buf_bytes,
                    &raw mut ret_len,
                )
            };
            let core_entry_sz = size_of::<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
            let core_count = if nt_status >= 0 && (ret_len as usize) >= core_entry_sz {
                ((ret_len as usize) / core_entry_sz).min(perf_buf.len())
            } else {
                0
            };

            if core_count > 0 {
                let mut cur_cores = Vec::with_capacity(core_count);
                for (idx, item) in perf_buf[..core_count].iter().enumerate() {
                    let cur = (
                        item.idle_time.max(0) as u64,
                        item.kernel_time.max(0) as u64,
                        item.user_time.max(0) as u64,
                    );
                    let (tot_m, usr_m, sys_m, idle_m) = if state.prev_core_times.len() == core_count
                    {
                        let prev = state.prev_core_times[idx];
                        let (tot, usr, sys) = Self::compute_system_times_delta(
                            prev,
                            cur,
                            sampled_at_ns,
                            window_ns,
                            src_cpu,
                        );
                        let idle = if cur.0 >= prev.0 && cur.1 >= prev.1 && cur.2 >= prev.2 {
                            let d_idle = cur.0 - prev.0;
                            let d_total = (cur.1 - prev.1) + (cur.2 - prev.2);
                            if d_total > 0 {
                                Metric::measured(
                                    ((d_idle as f64) / (d_total as f64) * 100.0).clamp(0.0, 100.0),
                                    pct_prov,
                                )
                            } else {
                                Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov)
                            }
                        } else {
                            Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov)
                        };
                        (tot, usr, sys, idle)
                    } else {
                        let p = Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                        (p, p, p, p)
                    };
                    snap.system.cores.push(CoreTelemetry {
                        core_index: idx as u32,
                        class: LogicalProcessorClass::Performance,
                        total_percent: tot_m,
                        user_percent: usr_m,
                        system_percent: sys_m,
                        idle_percent: idle_m,
                    });
                    cur_cores.push(cur);
                }
                state.prev_core_times = cur_cores;
            } else {
                for idx in 0..logical_cores {
                    let p = Metric::unavailable(UnavailableReason::Unsupported, pct_prov);
                    snap.system.cores.push(CoreTelemetry {
                        core_index: idx,
                        class: LogicalProcessorClass::Performance,
                        total_percent: p,
                        user_percent: p,
                        system_percent: p,
                        idle_percent: p,
                    });
                }
            }

            // Uptime
            // SAFETY: `GetTickCount64` takes no parameters and cannot fail.
            let ticks_ms = unsafe { GetTickCount64() };
            snap.system.uptime_seconds = Metric::measured(
                (ticks_ms as f64) / 1000.0,
                MetricProvenance {
                    source: src_up,
                    unit_tag: Some(UnitTag::SECONDS),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                },
            );

            // 1b. Dedicated & Integrated GPU Telemetry (NVML primary, DXGI + PDH fallback)
            let mut thermal_sensors: Vec<ThermalSensorReading> = Vec::new();
            let mut nvml_healthy = false;
            if let Some(nvml) = state.nvml.as_ref() {
                let s = nvml.sample();
                nvml_healthy = true;
                snap.system.gpu_model = s.adapter_name;
                let nvml_pct_prov = MetricProvenance {
                    source: src_nvml,
                    unit_tag: Some(UnitTag::PERCENT),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let nvml_bytes_prov = MetricProvenance {
                    source: src_nvml,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let nvml_watts_prov = MetricProvenance {
                    source: src_nvml,
                    unit_tag: Some(UnitTag::WATTS),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let nvml_mhz_prov = MetricProvenance {
                    source: src_nvml,
                    unit_tag: Some(UnitTag::MEGAHERTZ),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let nvml_temp_prov = MetricProvenance {
                    source: src_nvml,
                    unit_tag: Some(UnitTag::CELSIUS),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };

                if let Some(u) = s.gpu_util_percent {
                    let m = Metric::measured(u, nvml_pct_prov);
                    snap.system.gpu_utilization_percent = m;
                    snap.hardware_telemetry
                        .soc_power
                        .media_blocks
                        .push(MediaEngineBlockMetric {
                            id: CompactString::const_new("gpu_3d"),
                            label: CompactString::const_new("3D / Compute Core"),
                            active_residency_percent: m,
                        });
                }
                if let Some(mu) = s.mem_ctrl_util_percent {
                    snap.hardware_telemetry
                        .soc_power
                        .media_blocks
                        .push(MediaEngineBlockMetric {
                            id: CompactString::const_new("gpu_mem_ctrl"),
                            label: CompactString::const_new("VRAM Memory Controller"),
                            active_residency_percent: Metric::measured(mu, nvml_pct_prov),
                        });
                }
                if let Some(dec) = s.decoder_util_percent {
                    snap.hardware_telemetry
                        .soc_power
                        .media_blocks
                        .push(MediaEngineBlockMetric {
                            id: CompactString::const_new("nvdec"),
                            label: CompactString::const_new("Video Decode (NVDEC)"),
                            active_residency_percent: Metric::measured(dec, nvml_pct_prov),
                        });
                }
                if let Some(enc) = s.encoder_util_percent {
                    snap.hardware_telemetry
                        .soc_power
                        .media_blocks
                        .push(MediaEngineBlockMetric {
                            id: CompactString::const_new("nvenc"),
                            label: CompactString::const_new("Video Encode (NVENC)"),
                            active_residency_percent: Metric::measured(enc, nvml_pct_prov),
                        });
                }
                if let Some(fan) = s.fan_percent {
                    snap.hardware_telemetry
                        .soc_power
                        .media_blocks
                        .push(MediaEngineBlockMetric {
                            id: CompactString::const_new("gpu_fan"),
                            label: CompactString::const_new("GPU Cooling Fan"),
                            active_residency_percent: Metric::measured(fan, nvml_pct_prov),
                        });
                }
                if let Some(used) = s.vram_used_bytes {
                    snap.system.gpu_in_use_memory_bytes = Metric::measured(used, nvml_bytes_prov);
                }
                if let Some(tot) = s.vram_total_bytes {
                    snap.system.gpu_alloc_memory_bytes = Metric::measured(tot, nvml_bytes_prov);
                }
                if let Some(w) = s.power_watts {
                    let wm = Metric::measured(w, nvml_watts_prov);
                    snap.hardware_telemetry.soc_power.gpu_watts = wm;
                    let est_prov = MetricProvenance {
                        quality: MetricQuality::Estimated,
                        ..nvml_watts_prov
                    };
                    snap.hardware_telemetry.soc_power.package_watts = Metric::measured(w, est_prov);
                }
                if let Some(clk) = s.graphics_clock_mhz {
                    let cm = Metric::measured(clk, nvml_mhz_prov);
                    snap.hardware_telemetry.soc_power.gpu_clock_mhz = cm;
                    snap.hardware_telemetry
                        .soc_power
                        .cluster_clocks
                        .push(ClusterClockMetric {
                            cluster_name: CompactString::const_new("GPU Core Clock"),
                            is_performance: true,
                            effective_mhz: cm,
                            active_residency_percent: snap.system.gpu_utilization_percent,
                        });
                }
                if let Some(mclk) = s.mem_clock_mhz {
                    snap.hardware_telemetry
                        .soc_power
                        .cluster_clocks
                        .push(ClusterClockMetric {
                            cluster_name: CompactString::const_new("GPU Memory Clock"),
                            is_performance: true,
                            effective_mhz: Metric::measured(mclk, nvml_mhz_prov),
                            active_residency_percent: s
                                .mem_ctrl_util_percent
                                .map(|v| Metric::measured(v, nvml_pct_prov))
                                .unwrap_or(snap.system.gpu_utilization_percent),
                        });
                }
                if let Some(temp) = s.temp_celsius {
                    thermal_sensors.push(ThermalSensorReading {
                        name: CompactString::const_new("GPU Core (NVML)"),
                        celsius: Metric::measured(temp, nvml_temp_prov),
                    });
                }
            } else {
                // Universal DXGI + PDH fallback path (AMD / Intel / NVIDIA without NVML)
                let d3d_pct_prov = MetricProvenance {
                    source: src_d3dkmt,
                    unit_tag: Some(UnitTag::PERCENT),
                    sampled_at_ns,
                    window_ns,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let d3d_bytes_prov = MetricProvenance {
                    source: src_d3dkmt,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                if let Some(dxgi) = state.dxgi_cached.as_ref() {
                    snap.system.gpu_model = dxgi.adapter_name.clone();
                    if dxgi.dedicated_vram_bytes > 0 {
                        snap.system.gpu_alloc_memory_bytes =
                            Metric::measured(dxgi.dedicated_vram_bytes as f64, d3d_bytes_prov);
                    }
                }
                if let Some(used_vram) = pdh_sample.gpu_dedicated_vram_used_bytes {
                    snap.system.gpu_in_use_memory_bytes =
                        Metric::measured(used_vram, d3d_bytes_prov);
                }
                if let Some(eng) = pdh_sample.gpu_engines {
                    snap.system.gpu_utilization_percent =
                        Metric::measured(eng.overall_percent, d3d_pct_prov);
                    for (id, label, val) in [
                        ("gpu_3d", "3D Graphics Engine", eng.engine_3d_percent),
                        (
                            "gpu_compute",
                            "Compute Shader Engine",
                            eng.engine_compute_percent,
                        ),
                        ("gpu_copy", "DMA Copy Engine", eng.engine_copy_percent),
                        (
                            "gpu_vdec",
                            "Video Decode Engine",
                            eng.engine_video_decode_percent,
                        ),
                        (
                            "gpu_venc",
                            "Video Encode Engine",
                            eng.engine_video_encode_percent,
                        ),
                    ] {
                        snap.hardware_telemetry.soc_power.media_blocks.push(
                            MediaEngineBlockMetric {
                                id: CompactString::const_new(id),
                                label: CompactString::const_new(label),
                                active_residency_percent: Metric::measured(val, d3d_pct_prov),
                            },
                        );
                    }
                }
            }

            // Merge any valid Windows thermal zones from PDH
            let therm_prov = MetricProvenance {
                source: if nvml_healthy { src_nvml } else { src_thermal },
                unit_tag: Some(UnitTag::CELSIUS),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let wmi_therm_prov = MetricProvenance {
                source: src_thermal,
                unit_tag: Some(UnitTag::CELSIUS),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            for (zone_name, c) in pdh_sample.thermal_zones {
                thermal_sensors.push(ThermalSensorReading {
                    name: zone_name,
                    celsius: Metric::measured(c, wmi_therm_prov),
                });
            }
            let max_temp = thermal_sensors.iter().fold(None::<f64>, |acc, s| {
                if let MetricReading::Measured { value, .. } = s.celsius.reading {
                    Some(acc.map_or(value, |cur| cur.max(value)))
                } else {
                    acc
                }
            });
            snap.hardware_telemetry.thermals.sensors = thermal_sensors;
            if let Some(mt) = max_temp {
                snap.hardware_telemetry.thermals.max_die_celsius = Metric::measured(mt, therm_prov);
                let warn_lvl = if mt >= 95.0 {
                    2.0
                } else if mt >= 85.0 {
                    1.0
                } else {
                    0.0
                };
                snap.hardware_telemetry.thermals.warning_level = Metric::measured(
                    warn_lvl,
                    MetricProvenance {
                        source: therm_prov.source,
                        unit_tag: Some(UnitTag::COUNT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Authoritative,
                        contributed_by_stale: false,
                    },
                );
            }

            // 2. Memory (GlobalMemoryStatusEx)
            let mut ms = MEMORYSTATUSEX {
                dw_length: size_of::<MEMORYSTATUSEX>() as u32,
                ..Default::default()
            };
            // SAFETY: `ms.dw_length` is initialized to `size_of::<MEMORYSTATUSEX>()`.
            if unsafe { GlobalMemoryStatusEx(&raw mut ms) } != 0 && ms.ull_total_phys > 0 {
                mem_ok = true;
                let bp = MetricProvenance {
                    source: src_mem,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let pp = MetricProvenance {
                    source: src_mem,
                    unit_tag: Some(UnitTag::PERCENT),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let total = ms.ull_total_phys;
                let avail = ms.ull_avail_phys.min(total);
                let used = total.saturating_sub(avail);
                let used_pct = ((used as f64) / (total as f64) * 100.0).clamp(0.0, 100.0);

                snap.system.mem_total_bytes = Metric::measured(total as f64, bp);
                snap.system.mem_used_bytes = Metric::measured(used as f64, bp);
                snap.system.mem_free_bytes = Metric::measured(avail as f64, bp);
                snap.system.mem_used_percent = Metric::measured(used_pct, pp);
                snap.system.system_pressure_percent =
                    Metric::measured(f64::from(ms.dw_memory_load).clamp(0.0, 100.0), pp);

                let swap_total = ms.ull_total_page_file.saturating_sub(total);
                let commit_used = ms
                    .ull_total_page_file
                    .saturating_sub(ms.ull_avail_page_file);
                let swap_used = commit_used.saturating_sub(used).min(swap_total);
                let swap_pct = if swap_total > 0 {
                    ((swap_used as f64) / (swap_total as f64) * 100.0).clamp(0.0, 100.0)
                } else {
                    0.0
                };
                snap.system.swap_total_bytes = Metric::measured(swap_total as f64, bp);
                snap.system.swap_used_bytes = Metric::measured(swap_used as f64, bp);
                snap.system.swap_used_percent = Metric::measured(swap_pct, pp);
            }

            // 3. Disks (GetLogicalDriveStringsW + GetDiskFreeSpaceExW + IOCTL_DISK_PERFORMANCE / PDH LogicalDisk)
            let mut drive_roots: Vec<Vec<u16>> = Vec::new();
            let mut raw_buf = [0u16; 256];
            // SAFETY: `raw_buf` has 256 UTF-16 code units.
            let n_chars =
                unsafe { GetLogicalDriveStringsW(raw_buf.len() as u32, raw_buf.as_mut_ptr()) };
            if n_chars > 0 && (n_chars as usize) < raw_buf.len() {
                let mut start = 0usize;
                for i in 0..n_chars as usize {
                    if raw_buf[i] == 0 {
                        if i > start {
                            let mut root = raw_buf[start..i].to_vec();
                            root.push(0);
                            drive_roots.push(root);
                        }
                        start = i + 1;
                    }
                }
            } else {
                let mut c_root: Vec<u16> = "C:\\".encode_utf16().collect();
                c_root.push(0);
                drive_roots.push(c_root);
            }

            let d_bytes_prov = MetricProvenance {
                source: src_disk,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let d_io_bytes_prov = MetricProvenance {
                source: src_disk_io,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let d_io_cnt_prov = MetricProvenance {
                source: src_disk_io,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let d_io_rate_prov = MetricProvenance {
                source: src_disk_io,
                unit_tag: Some(UnitTag::BYTES_PER_SEC),
                sampled_at_ns,
                window_ns,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let d_pct_prov = MetricProvenance {
                source: src_disk,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            let mut seen_drives = HashSet::new();
            for root_w in drive_roots {
                // SAFETY: `root_w` is NUL-terminated UTF-16.
                let dtype = unsafe { GetDriveTypeW(root_w.as_ptr()) };
                if !matches!(dtype, DRIVE_FIXED | DRIVE_REMOVABLE | DRIVE_RAMDISK) {
                    continue;
                }
                let mut avail_caller = 0u64;
                let mut total_bytes = 0u64;
                let mut total_free = 0u64;
                // SAFETY: `root_w` is NUL-terminated UTF-16 and pointers are stack u64s.
                if unsafe {
                    GetDiskFreeSpaceExW(
                        root_w.as_ptr(),
                        &raw mut avail_caller,
                        &raw mut total_bytes,
                        &raw mut total_free,
                    )
                } == 0
                {
                    continue;
                }
                let label_full =
                    String::from_utf16_lossy(&root_w[..root_w.len().saturating_sub(1)]);
                let bsd_name = CompactString::from(label_full.trim_end_matches('\\'));
                if bsd_name.is_empty() || !seen_drives.insert(bsd_name.clone()) {
                    continue;
                }
                let used = total_bytes.saturating_sub(avail_caller);
                let pct = if total_bytes > 0 {
                    ((used as f64) / (total_bytes as f64) * 100.0).clamp(0.0, 100.0)
                } else {
                    0.0
                };

                let (
                    read_bytes_total,
                    write_bytes_total,
                    read_ops_total,
                    write_ops_total,
                    read_bytes_per_sec,
                    write_bytes_per_sec,
                ) = if let Some((r_bytes, w_bytes, r_ops, w_ops)) =
                    query_volume_disk_performance(bsd_name.as_str())
                {
                    let prev = state.prev_disks.insert(
                        bsd_name.clone(),
                        PrevIoCounter {
                            rx_or_read: r_bytes,
                            tx_or_write: w_bytes,
                            read_ops: r_ops,
                            write_ops: w_ops,
                            sampled_at_ns,
                        },
                    );
                    let (mut r_rate, mut w_rate) = Self::compute_io_rate_metrics(
                        prev,
                        r_bytes,
                        w_bytes,
                        sampled_at_ns,
                        src_disk_io,
                    );
                    if !matches!(r_rate.reading, MetricReading::Measured { .. })
                        && let Some(&(pdh_rb, pdh_wb, _, _)) = pdh_sample.disk_rates.get(&bsd_name)
                    {
                        r_rate = Metric::measured(pdh_rb, d_io_rate_prov);
                        w_rate = Metric::measured(pdh_wb, d_io_rate_prov);
                    }
                    (
                        Metric::measured(r_bytes as f64, d_io_bytes_prov),
                        Metric::measured(w_bytes as f64, d_io_bytes_prov),
                        Metric::measured(r_ops as f64, d_io_cnt_prov),
                        Metric::measured(w_ops as f64, d_io_cnt_prov),
                        r_rate,
                        w_rate,
                    )
                } else if let Some(&(pdh_rb, pdh_wb, pdh_ro, pdh_wo)) =
                    pdh_sample.disk_rates.get(&bsd_name)
                {
                    let dt_s = (window_ns as f64) / 1_000_000_000.0;
                    let prev = state.prev_disks.get(&bsd_name).copied();
                    let r_bytes = prev
                        .map(|p| p.rx_or_read.saturating_add((pdh_rb * dt_s) as u64))
                        .unwrap_or(0);
                    let w_bytes = prev
                        .map(|p| p.tx_or_write.saturating_add((pdh_wb * dt_s) as u64))
                        .unwrap_or(0);
                    let r_ops = prev
                        .map(|p| p.read_ops.saturating_add((pdh_ro * dt_s) as u64))
                        .unwrap_or(0);
                    let w_ops = prev
                        .map(|p| p.write_ops.saturating_add((pdh_wo * dt_s) as u64))
                        .unwrap_or(0);
                    state.prev_disks.insert(
                        bsd_name.clone(),
                        PrevIoCounter {
                            rx_or_read: r_bytes,
                            tx_or_write: w_bytes,
                            read_ops: r_ops,
                            write_ops: w_ops,
                            sampled_at_ns,
                        },
                    );
                    (
                        Metric::measured(r_bytes as f64, d_io_bytes_prov),
                        Metric::measured(w_bytes as f64, d_io_bytes_prov),
                        Metric::measured(r_ops as f64, d_io_cnt_prov),
                        Metric::measured(w_ops as f64, d_io_cnt_prov),
                        Metric::measured(pdh_rb, d_io_rate_prov),
                        Metric::measured(pdh_wb, d_io_rate_prov),
                    )
                } else {
                    (
                        Metric::unavailable(UnavailableReason::NotYetAvailable, d_io_bytes_prov),
                        Metric::unavailable(UnavailableReason::NotYetAvailable, d_io_bytes_prov),
                        Metric::unavailable(UnavailableReason::NotYetAvailable, d_io_cnt_prov),
                        Metric::unavailable(UnavailableReason::NotYetAvailable, d_io_cnt_prov),
                        Metric::unavailable(UnavailableReason::NotYetAvailable, d_io_rate_prov),
                        Metric::unavailable(UnavailableReason::NotYetAvailable, d_io_rate_prov),
                    )
                };

                snap.disks.push(DiskSnapshot {
                    bsd_name,
                    read_bytes_total,
                    write_bytes_total,
                    read_ops_total,
                    write_ops_total,
                    read_bytes_per_sec,
                    write_bytes_per_sec,
                    root_total_bytes: Metric::measured(total_bytes as f64, d_bytes_prov),
                    root_available_bytes: Metric::measured(avail_caller as f64, d_bytes_prov),
                    root_used_percent: Metric::measured(pct, d_pct_prov),
                });
            }

            // 3b. Networks (`iphlpapi::GetIfTable2` + `FreeMibTable`)
            unsafe {
                let mut table_ptr: *mut MIB_IF_TABLE2 = ptr::null_mut();
                if GetIfTable2(&raw mut table_ptr) == 0 && !table_ptr.is_null() {
                    let num_entries = (*table_ptr).NumEntries as usize;
                    let rows = std::slice::from_raw_parts(
                        (*table_ptr).Table.as_ptr(),
                        num_entries.min(256),
                    );
                    let net_bytes_prov = MetricProvenance {
                        source: src_if_table2,
                        unit_tag: Some(UnitTag::BYTES),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Authoritative,
                        contributed_by_stale: false,
                    };
                    let net_pkt_prov = MetricProvenance {
                        source: src_if_table2,
                        unit_tag: Some(UnitTag::COUNT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Authoritative,
                        contributed_by_stale: false,
                    };

                    let mut candidates: Vec<(u64, CompactString, u64, u64, u64, u64)> = Vec::new();
                    let mut seen_ifaces = HashSet::new();
                    for row in rows {
                        let flags = row.InterfaceAndOperStatusFlags._bitfield;
                        if !Self::should_include_windows_if_row(
                            row.Type,
                            row.OperStatus,
                            flags,
                            row.InOctets,
                            row.OutOctets,
                        ) {
                            continue;
                        }
                        let mut iface = utf16_z_to_compact(&row.Alias);
                        if iface.is_empty() {
                            iface = utf16_z_to_compact(&row.Description);
                        }
                        if iface.is_empty() {
                            iface = CompactString::from(format!("eth{}", row.InterfaceIndex));
                        }
                        if !seen_ifaces.insert(iface.clone()) {
                            continue;
                        }
                        let rx_pkts = row.InUcastPkts.saturating_add(row.InNUcastPkts);
                        let tx_pkts = row.OutUcastPkts.saturating_add(row.OutNUcastPkts);
                        let traffic_weight = row.InOctets.saturating_add(row.OutOctets);
                        candidates.push((
                            traffic_weight,
                            iface,
                            row.InOctets,
                            row.OutOctets,
                            rx_pkts,
                            tx_pkts,
                        ));
                    }
                    FreeMibTable(table_ptr.cast::<c_void>());

                    // Sort primary active NIC with highest cumulative traffic first so index 0 is never a dormant virtual adapter
                    candidates.sort_by_key(|c| std::cmp::Reverse(c.0));

                    for (_, iface, rx_bytes, tx_bytes, rx_pkts, tx_pkts) in candidates {
                        let prev = state.prev_nets.insert(
                            iface.clone(),
                            PrevIoCounter {
                                rx_or_read: rx_bytes,
                                tx_or_write: tx_bytes,
                                read_ops: rx_pkts,
                                write_ops: tx_pkts,
                                sampled_at_ns,
                            },
                        );
                        let (rx_rate, tx_rate) = Self::compute_io_rate_metrics(
                            prev,
                            rx_bytes,
                            tx_bytes,
                            sampled_at_ns,
                            src_if_table2,
                        );
                        snap.networks.push(NetworkSnapshot {
                            interface: iface,
                            rx_bytes_total: Metric::measured(rx_bytes as f64, net_bytes_prov),
                            tx_bytes_total: Metric::measured(tx_bytes as f64, net_bytes_prov),
                            rx_packets_total: Metric::measured(rx_pkts as f64, net_pkt_prov),
                            tx_packets_total: Metric::measured(tx_pkts as f64, net_pkt_prov),
                            rx_bytes_per_sec: rx_rate,
                            tx_bytes_per_sec: tx_rate,
                        });
                    }
                }
            }

            // 4. Processes (CreateToolhelp32Snapshot + OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + GetProcessTimes + K32GetProcessMemoryInfo + GetProcessIoCounters)
            let mut next_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)> =
                HashMap::with_capacity(state.prev_procs.len().max(128));
            // SAFETY: Snapshot handle closed by `HandleGuard`.
            let snap_h = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
            if !snap_h.is_null() && snap_h != (-1isize as *mut c_void) {
                let _snap_guard = HandleGuard(snap_h);
                let mut pe = PROCESSENTRY32W {
                    dw_size: size_of::<PROCESSENTRY32W>() as u32,
                    cnt_usage: 0,
                    th32_process_id: 0,
                    th32_default_heap_id: 0,
                    th32_module_id: 0,
                    cnt_threads: 0,
                    th32_parent_process_id: 0,
                    pc_pri_class_base: 0,
                    dw_flags: 0,
                    sz_exe_file: [0; 260],
                };
                let pb_prov = MetricProvenance {
                    source: src_proc,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let pb_unavail_prov = MetricProvenance {
                    source: src_proc,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Unknown,
                    contributed_by_stale: false,
                };

                // SAFETY: `pe.dw_size` is set before calling `Process32FirstW`.
                if unsafe { Process32FirstW(snap_h, &raw mut pe) } != 0 {
                    loop {
                        let pid_u32 = pe.th32_process_id;
                        if pid_u32 > 0 {
                            let ppid = pe.th32_parent_process_id as i32;
                            let name = utf16_z_to_compact(&pe.sz_exe_file);

                            // SAFETY: Opens with PROCESS_QUERY_LIMITED_INFORMATION only (zero double-OpenProcess syscall overhead).
                            let proc_h = unsafe {
                                OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid_u32)
                            };

                            if !proc_h.is_null() {
                                let _pguard = HandleGuard(proc_h);
                                let mut ct = FILETIME::default();
                                let mut et = FILETIME::default();
                                let mut kt = FILETIME::default();
                                let mut ut = FILETIME::default();
                                // SAFETY: `proc_h` is valid.
                                let times_ok = unsafe {
                                    GetProcessTimes(
                                        proc_h,
                                        &raw mut ct,
                                        &raw mut et,
                                        &raw mut kt,
                                        &raw mut ut,
                                    )
                                } != 0;
                                let creation_100ns = if times_ok { filetime_to_u64(ct) } else { 0 };
                                let key = make_win32_process_key(pid_u32, creation_100ns);
                                let total_cpu_ns = if times_ok {
                                    filetime_to_u64(kt)
                                        .saturating_add(filetime_to_u64(ut))
                                        .saturating_mul(100)
                                } else {
                                    0
                                };

                                let cpu_percent = if !times_ok {
                                    Metric::unavailable(
                                        UnavailableReason::AccessDenied,
                                        MetricProvenance {
                                            source: src_proc,
                                            unit_tag: Some(UnitTag::PERCENT),
                                            sampled_at_ns,
                                            window_ns: 0,
                                            quality: MetricQuality::Unknown,
                                            contributed_by_stale: false,
                                        },
                                    )
                                } else if let Some((prev_cpu_ns, prev_at_ns, _)) =
                                    state.prev_procs.get(&key)
                                {
                                    let dt_ns = sampled_at_ns.saturating_sub(*prev_at_ns);
                                    let pprov = MetricProvenance {
                                        source: src_proc,
                                        unit_tag: Some(UnitTag::PERCENT),
                                        sampled_at_ns,
                                        window_ns: dt_ns,
                                        quality: MetricQuality::Authoritative,
                                        contributed_by_stale: false,
                                    };
                                    if dt_ns > 0 && total_cpu_ns >= *prev_cpu_ns {
                                        let d_cpu = total_cpu_ns - *prev_cpu_ns;
                                        Metric::measured(
                                            Self::compute_normalized_process_cpu_percent(
                                                d_cpu,
                                                dt_ns,
                                                logical_cores,
                                            ),
                                            pprov,
                                        )
                                    } else {
                                        Metric::unavailable(
                                            UnavailableReason::NotYetAvailable,
                                            pprov,
                                        )
                                    }
                                } else {
                                    Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        MetricProvenance {
                                            source: src_proc,
                                            unit_tag: Some(UnitTag::PERCENT),
                                            sampled_at_ns,
                                            window_ns: 0,
                                            quality: MetricQuality::Authoritative,
                                            contributed_by_stale: false,
                                        },
                                    )
                                };

                                let mut pmc = PROCESS_MEMORY_COUNTERS_EX {
                                    cb: size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
                                    ..Default::default()
                                };
                                // SAFETY: `pmc.cb` is set to `size_of::<PROCESS_MEMORY_COUNTERS_EX>()`.
                                let mem_res_ok = unsafe {
                                    K32GetProcessMemoryInfo(
                                        proc_h,
                                        (&raw mut pmc).cast(),
                                        size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
                                    )
                                } != 0;

                                let (phys_footprint, resident) = if mem_res_ok {
                                    let fallback_bytes = u64::from(pe.cnt_threads.max(1))
                                        .saturating_mul(2 * 1024 * 1024);
                                    let ws_raw = if pmc.WorkingSetSize > 0 {
                                        pmc.WorkingSetSize as u64
                                    } else {
                                        fallback_bytes
                                    };
                                    // Report Working Set (or PrivateUsage when smaller than WorkingSet) instead of
                                    // `PagefileUsage.max(WorkingSetSize)`, which inflated DX12/wgpu apps by counting
                                    // reserved GPU/virtual commit charge as physical RAM footprint.
                                    let footprint_raw = if pmc.PrivateUsage > 0
                                        && (pmc.PrivateUsage as u64) < ws_raw
                                    {
                                        pmc.PrivateUsage as u64
                                    } else {
                                        ws_raw
                                    };
                                    (
                                        Metric::measured(footprint_raw as f64, pb_prov),
                                        Metric::measured(ws_raw as f64, pb_prov),
                                    )
                                } else {
                                    let reason = if unsafe { GetLastError() } == ERROR_ACCESS_DENIED
                                    {
                                        UnavailableReason::AccessDenied
                                    } else {
                                        UnavailableReason::TransientError
                                    };
                                    (
                                        Metric::unavailable(reason, pb_unavail_prov),
                                        Metric::unavailable(reason, pb_unavail_prov),
                                    )
                                };

                                let mut io_cnt = IO_COUNTERS::default();
                                // SAFETY: `proc_h` is valid and `io_cnt` is stack-allocated.
                                let (disk_read_bytes, disk_write_bytes) = if unsafe {
                                    GetProcessIoCounters(proc_h, &raw mut io_cnt)
                                } != 0
                                {
                                    (
                                        Metric::measured(io_cnt.ReadTransferCount as f64, pb_prov),
                                        Metric::measured(io_cnt.WriteTransferCount as f64, pb_prov),
                                    )
                                } else {
                                    (
                                        Metric::unavailable(
                                            UnavailableReason::AccessDenied,
                                            pb_unavail_prov,
                                        ),
                                        Metric::unavailable(
                                            UnavailableReason::AccessDenied,
                                            pb_unavail_prov,
                                        ),
                                    )
                                };

                                let proc_snap = ProcessSnapshot {
                                    key,
                                    ppid,
                                    name,
                                    cpu_percent,
                                    phys_footprint_bytes: phys_footprint,
                                    resident_bytes: resident,
                                    disk_read_bytes,
                                    disk_write_bytes,
                                    exited_at_ns: None,
                                };
                                if let std::collections::hash_map::Entry::Vacant(e) =
                                    next_procs.entry(key)
                                {
                                    snap.processes.push(proc_snap.clone());
                                    e.insert((total_cpu_ns, sampled_at_ns, proc_snap));
                                }
                            } else {
                                let err = unsafe { GetLastError() };
                                let reason = if err == ERROR_ACCESS_DENIED {
                                    UnavailableReason::AccessDenied
                                } else {
                                    UnavailableReason::TransientError
                                };
                                let key = make_win32_process_key(pid_u32, 0);
                                let proc_snap = ProcessSnapshot {
                                    key,
                                    ppid,
                                    name,
                                    cpu_percent: Metric::unavailable(
                                        reason,
                                        MetricProvenance {
                                            source: src_proc,
                                            unit_tag: Some(UnitTag::PERCENT),
                                            sampled_at_ns,
                                            window_ns: 0,
                                            quality: MetricQuality::Unknown,
                                            contributed_by_stale: false,
                                        },
                                    ),
                                    phys_footprint_bytes: Metric::unavailable(
                                        reason,
                                        pb_unavail_prov,
                                    ),
                                    resident_bytes: Metric::unavailable(reason, pb_unavail_prov),
                                    disk_read_bytes: Metric::unavailable(reason, pb_unavail_prov),
                                    disk_write_bytes: Metric::unavailable(reason, pb_unavail_prov),
                                    exited_at_ns: None,
                                };
                                if let std::collections::hash_map::Entry::Vacant(e) =
                                    next_procs.entry(key)
                                {
                                    snap.processes.push(proc_snap.clone());
                                    e.insert((0, sampled_at_ns, proc_snap));
                                }
                            }
                        }

                        // SAFETY: `snap_h` and `pe` remain valid.
                        if unsafe { Process32NextW(snap_h, &raw mut pe) } == 0 {
                            break;
                        }
                    }
                }
            }

            for (old_key, (_, _, mut old_snap)) in state.prev_procs.drain() {
                if !next_procs.contains_key(&old_key) {
                    old_snap.exited_at_ns = Some(sampled_at_ns);
                    state.exited_procs.insert(old_key, old_snap);
                }
            }
            state.exited_procs.retain(|_, exited| {
                let exited_at = exited.exited_at_ns.unwrap_or(sampled_at_ns);
                sampled_at_ns.saturating_sub(exited_at) <= EXITED_PROCESS_RETENTION_NS
            });
            snap.exited_processes
                .extend(state.exited_procs.values().cloned());
            state.prev_procs = next_procs;

            let healthy = cpu_ok || mem_ok;
            snap.provider_health.push(ProviderHealth {
                id: CompactString::const_new("windows.win32"),
                state: if healthy {
                    ProviderHealthState::Healthy
                } else {
                    ProviderHealthState::Unavailable
                },
                consecutive_failures: u32::from(!healthy),
                last_success_at_ns: if healthy { sampled_at_ns } else { 0 },
                last_error: if healthy {
                    None
                } else {
                    Some(CompactString::const_new("Win32 telemetry APIs failed"))
                },
            });
            if nvml_healthy {
                snap.provider_health.push(ProviderHealth {
                    id: CompactString::const_new("windows.nvml.gpu"),
                    state: ProviderHealthState::Healthy,
                    consecutive_failures: 0,
                    last_success_at_ns: sampled_at_ns,
                    last_error: None,
                });
            } else if state.dxgi_cached.is_some() {
                snap.provider_health.push(ProviderHealth {
                    id: CompactString::const_new("windows.d3dkmt.gpu"),
                    state: ProviderHealthState::Healthy,
                    consecutive_failures: 0,
                    last_success_at_ns: sampled_at_ns,
                    last_error: None,
                });
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::ValueState;

    #[test]
    fn windows_system_times_delta_subtracts_idle_from_kernel() {
        let (tot, usr, sys) = WindowsCollector::compute_system_times_delta(
            (1000, 1500, 500),
            (1600, 2300, 700),
            1_000_000_000,
            500_000_000,
            SourceId(1),
        );
        // d_idle = 600, d_kernel_total = 800 (so d_sys = 200), d_user = 200, d_total = 1000
        assert_eq!(tot.state(), ValueState::Valid);
        assert_eq!(tot.format_with_unit(1, "%"), "40.0%");
        assert_eq!(usr.format_with_unit(1, "%"), "20.0%");
        assert_eq!(sys.format_with_unit(1, "%"), "20.0%");

        let key = WindowsCollector::make_process_key(4812, 133_500_000_000_000_000);
        assert_eq!(key.pid, 4812);
        assert_eq!(key.creation_token_us, 13_350_000_000_000_000);
    }

    #[test]
    fn windows_netsh_sc_startup_and_mib_tcp_parsers_work() {
        let row = WindowsCollector::from_mib_tcp_row(
            0x0100007f, // 127.0.0.1 in LE u32
            0xde23,     // 9182 in network byte order
            0,
            0,
            2, // MIB_TCP_STATE_LISTEN
            1234,
            "nexus.exe",
        );
        assert_eq!(row.local_address.as_str(), "127.0.0.1");
        assert_eq!(row.local_port, 9182);
        assert_eq!(row.state, TcpState::Listen);

        let sc = "\
SERVICE_NAME: wuauserv
DISPLAY_NAME: Windows Update
        STATE              : 4  RUNNING
        PID                : 1456
";
        let svcs = WindowsCollector::parse_windows_sc_query(sc);
        assert_eq!(svcs.len(), 1);
        assert_eq!(svcs[0].state, ServiceState::Running);
        assert_eq!(svcs[0].pid, Some(1456));

        let startup = WindowsCollector::build_windows_startup_item(
            "Nexus",
            "C:\\Program Files\\Nexus\\nexus.exe",
            "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            Some(0x03),
        );
        assert!(!startup.is_enabled);
        assert!(startup.user_writable);

        let netsh = "\
SSID 1 : StudioWiFi
    Authentication          : WPA2-Personal
    BSSID 1                 : aa:bb:cc:dd:ee:ff
         Signal             : 90%
         Channel            : 149
";
        let wifi = WindowsCollector::parse_windows_netsh_wlan(netsh, 1_000, SourceId(1));
        assert_eq!(wifi.networks.len(), 1);
        assert_eq!(wifi.networks[0].ssid.as_str(), "StudioWiFi");
        assert_eq!(wifi.networks[0].channel, 149);
        assert_eq!(wifi.networks[0].band, WifiBand::Band5GHz);
        assert_eq!(
            wifi.networks[0].rssi_dbm.format_with_unit(0, "dBm"),
            "-55 dBm"
        );

        let ifaces = "\
    Name                   : Wi-Fi
    State                  : connected
    SSID                   : StudioWiFi
";
        let wifi_conn = WindowsCollector::parse_windows_netsh_wlan_with_interfaces(
            netsh,
            ifaces,
            1_000,
            SourceId(1),
        );
        assert_eq!(wifi_conn.connected_ssid.as_deref(), Some("StudioWiFi"));
        assert!(wifi_conn.networks[0].is_connected);
    }

    #[test]
    fn windows_nvml_struct_layout_if_table2_filter_io_deltas_and_pdh_gpu_aggregation_work() {
        assert_eq!(std::mem::size_of::<NvmlUtilization>(), 8);
        assert_eq!(std::mem::size_of::<NvmlMemory>(), 24);

        // GetIfTable2 filter: excludes loopback (24), tunnel (131), down (OperStatus != 1), and NDIS filter rows (0x02),
        // while keeping HardwareInterface (0x01) and HardwareInterface | ConnectorPresent (0x05).
        assert!(WindowsCollector::should_include_windows_if_row(
            6, 1, 0x01, 1000, 1000
        ));
        assert!(WindowsCollector::should_include_windows_if_row(
            6, 1, 0x05, 1000, 1000
        ));
        assert!(!WindowsCollector::should_include_windows_if_row(
            24, 1, 0x01, 1000, 1000
        ));
        assert!(!WindowsCollector::should_include_windows_if_row(
            131, 1, 0x01, 1000, 1000
        ));
        assert!(!WindowsCollector::should_include_windows_if_row(
            6, 2, 0x01, 1000, 1000
        ));
        assert!(!WindowsCollector::should_include_windows_if_row(
            6, 1, 0x03, 1000, 1000
        ));
        assert!(!WindowsCollector::should_include_windows_if_row(
            6, 1, 0x00, 1000, 1000
        ));

        // Monotonic Disk & Network I/O rate and IOPS delta calculation
        let prev = PrevIoCounter {
            rx_or_read: 1_000_000,
            tx_or_write: 500_000,
            read_ops: 100,
            write_ops: 50,
            sampled_at_ns: 1_000_000_000,
        };
        let (r_rate, w_rate) = WindowsCollector::compute_io_rate_metrics(
            Some(prev),
            3_000_000,
            1_500_000,
            1_500_000_000,
            SourceId(2),
        );
        assert_eq!(r_rate.state(), ValueState::Valid);
        assert_eq!(w_rate.state(), ValueState::Valid);
        assert_eq!(r_rate.format_with_unit(0, "B/s"), "4000000 B/s");
        assert_eq!(w_rate.format_with_unit(0, "B/s"), "2000000 B/s");

        let (r_iops, w_iops) = WindowsCollector::compute_iops_metrics(
            Some(prev),
            250,
            100,
            1_500_000_000,
            SourceId(2),
        );
        assert_eq!(r_iops.format_with_unit(0, "IOPS"), "300 IOPS");
        assert_eq!(w_iops.format_with_unit(0, "IOPS"), "100 IOPS");

        // PDH GPU Engine aggregation and clamping
        let engines = WindowsCollector::aggregate_pdh_gpu_engines([
            ("pid_1234_luid_0x0_0x123_phys_0_eng_0_engtype_3D", 42.5),
            ("pid_5678_luid_0x0_0x123_phys_0_eng_0_engtype_3D", 17.5),
            ("pid_1234_luid_0x0_0x123_phys_0_eng_1_engtype_Copy", 12.0),
            (
                "pid_1234_luid_0x0_0x123_phys_0_eng_2_engtype_VideoDecode",
                25.0,
            ),
        ]);
        assert_eq!(engines.engine_3d_percent, 60.0);
        assert_eq!(engines.engine_copy_percent, 12.0);
        assert_eq!(engines.engine_video_decode_percent, 25.0);
        assert_eq!(engines.overall_percent, 60.0);

        // ACPI Kelvin thermal conversion rejects 273.15 K and 301.0 K static stub
        assert_eq!(WindowsCollector::acpi_kelvin_to_valid_celsius(273.15), None);
        assert_eq!(WindowsCollector::acpi_kelvin_to_valid_celsius(301.0), None);
        let valid_c =
            WindowsCollector::acpi_kelvin_to_valid_celsius(335.15).expect("valid 62C thermal");
        assert!((valid_c - 62.0).abs() < 0.01);

        // Per-process CPU % normalization by logical core count (1.137 cores on 16-thread CPU = 7.10625%)
        let norm_cpu = WindowsCollector::compute_normalized_process_cpu_percent(
            1_137_000_000,
            1_000_000_000,
            16,
        );
        assert!((norm_cpu - 7.10625).abs() < 1e-4);
    }
}
