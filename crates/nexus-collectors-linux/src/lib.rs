use compact_str::CompactString;
use nexus_core::{
    ConnectionProtocol, Metric, MetricProvenance, MetricQuality, NetworkConnectionEntry,
    ProcessKey, ProcessSnapshot, ProviderHealth, ProviderHealthState, ServiceEntry,
    ServiceStartType, ServiceState, SourceId, StartupItem, StartupItemType, SystemSnapshot,
    TcpState, UnavailableReason, UnitTag, WifiBand, WifiNetworkEntry, WirelessScanReport,
    repair_snapshot_coherence,
};
#[cfg(target_os = "linux")]
use nexus_core::{
    CoreTelemetry, DiskSnapshot, LogicalProcessorClass, MetricReading, NetworkSnapshot,
    ThermalSensorReading,
};
use parking_lot::Mutex;
use std::collections::HashMap;
#[cfg(target_os = "linux")]
use std::collections::HashSet;
use std::io::ErrorKind;
use std::net::{Ipv4Addr, Ipv6Addr};
use std::time::Instant;

/// Retains exited processes for `5,000,000,000` ns (5.0 s).
pub const EXITED_PROCESS_RETENTION_NS: u64 = 5_000_000_000;

#[derive(Debug, Clone, Copy)]
struct LinuxCpuTicks {
    user: u64,
    nice: u64,
    system: u64,
    idle: u64,
    iowait: u64,
    irq: u64,
    softirq: u64,
    steal: u64,
}

impl LinuxCpuTicks {
    const fn total(self) -> u64 {
        self.user
            + self.nice
            + self.system
            + self.idle
            + self.iowait
            + self.irq
            + self.softirq
            + self.steal
    }

    const fn busy(self) -> u64 {
        self.user + self.nice + self.system + self.irq + self.softirq + self.steal
    }
}

/// Parsed fields from `/proc/[pid]/stat`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinuxPidStat {
    pub pid: i32,
    pub comm: CompactString,
    pub state: char,
    pub ppid: i32,
    pub utime_ticks: u64,
    pub stime_ticks: u64,
    pub num_threads: u32,
    pub starttime_ticks: u64,
    pub vsize_bytes: u64,
    pub rss_bytes: u64,
}

#[derive(Debug, Clone, Copy)]
#[allow(dead_code)]
struct PrevIoCounter {
    rx_or_read: u64,
    tx_or_write: u64,
    sampled_at_ns: u64,
}

#[allow(dead_code)]
struct LinuxCollectorInner {
    epoch: Instant,
    generation: u64,
    prev_total_cpu: Option<(LinuxCpuTicks, u64)>,
    prev_core_cpus: HashMap<u32, LinuxCpuTicks>,
    prev_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)>,
    exited_procs: HashMap<ProcessKey, ProcessSnapshot>,
    prev_disks: HashMap<CompactString, PrevIoCounter>,
    prev_nets: HashMap<CompactString, PrevIoCounter>,
}

/// Cross-platform Linux collector (`/proc/stat`, `/proc/meminfo`, `/proc/[pid]/stat`, `/proc/net/*`, `systemctl`, `nmcli`/`iw`).
pub struct LinuxCollector {
    inner: Mutex<LinuxCollectorInner>,
}

impl Default for LinuxCollector {
    fn default() -> Self {
        Self::new()
    }
}

impl LinuxCollector {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(LinuxCollectorInner {
                epoch: Instant::now(),
                generation: 0,
                prev_total_cpu: None,
                prev_core_cpus: HashMap::new(),
                prev_procs: HashMap::new(),
                exited_procs: HashMap::new(),
                prev_disks: HashMap::new(),
                prev_nets: HashMap::new(),
            }),
        }
    }

    /// Reads a PID-reuse-safe `ProcessKey(pid, creation_token_us, uid)`, `ppid`, and `name` from `/proc/[pid]`.
    pub fn read_process_key(pid: i32) -> Option<(ProcessKey, i32, CompactString)> {
        read_process_key(pid)
    }

    /// Collects a snapshot on Linux hosts (or returns an `Unsupported` snapshot on non-Linux hosts).
    pub fn collect(&self) -> SystemSnapshot {
        let mut guard = self.inner.lock();
        guard.generation += 1;
        let generation = guard.generation;
        let sampled_at_ns = (guard.epoch.elapsed().as_nanos() as u64).max(1);
        let mut snap = SystemSnapshot::new_empty(generation, sampled_at_ns);

        #[cfg(target_os = "linux")]
        {
            Self::collect_linux_host(&mut guard, &mut snap, sampled_at_ns);
        }

        #[cfg(not(target_os = "linux"))]
        {
            let src = snap.intern_source("linux.procfs.unavailable_on_host");
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
                id: CompactString::const_new("linux.procfs"),
                state: ProviderHealthState::Unavailable,
                consecutive_failures: 0,
                last_success_at_ns: 0,
                last_error: Some(CompactString::const_new("non-linux host")),
            });
            let _ = &mut guard.prev_total_cpu;
        }

        let _ = repair_snapshot_coherence(&mut snap);
        snap
    }

    /// Parses `/proc/stat` first line (`cpu  user nice system idle iowait irq softirq steal`)
    /// and computes `(total_pct, user_pct, kernel_pct)` against a prior sample.
    pub fn parse_proc_stat_delta(
        prev_line: &str,
        cur_line: &str,
        sampled_at_ns: u64,
        window_ns: u64,
        source: SourceId,
    ) -> (Metric<f64>, Metric<f64>, Metric<f64>) {
        let prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns,
            window_ns,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let (Some((_, p)), Some((_, c))) = (
            Self::parse_cpu_line(prev_line),
            Self::parse_cpu_line(cur_line),
        ) else {
            let err = Metric::unavailable(UnavailableReason::TransientError, prov);
            return (err, err, err);
        };
        if c.total() <= p.total() || c.busy() < p.busy() {
            let pending = Metric::unavailable(UnavailableReason::NotYetAvailable, prov);
            return (pending, pending, pending);
        }
        let d_tot = (c.total() - p.total()) as f64;
        let d_busy = (c.busy() - p.busy()) as f64;
        let d_user = ((c.user + c.nice).saturating_sub(p.user + p.nice)) as f64;
        let d_sys =
            ((c.system + c.irq + c.softirq).saturating_sub(p.system + p.irq + p.softirq)) as f64;
        (
            Metric::measured((d_busy / d_tot * 100.0).clamp(0.0, 100.0), prov),
            Metric::measured((d_user / d_tot * 100.0).clamp(0.0, 100.0), prov),
            Metric::measured((d_sys / d_tot * 100.0).clamp(0.0, 100.0), prov),
        )
    }

    /// Parses `/proc/meminfo` text into `(total_bytes, used_bytes, available_bytes, used_percent)`.
    pub fn parse_proc_meminfo(
        text: &str,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> (Metric<f64>, Metric<f64>, Metric<f64>, Metric<f64>) {
        let b_prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::BYTES),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let p_prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };

        let mut mem_total_kib: Option<u64> = None;
        let mut mem_avail_kib: Option<u64> = None;
        for line in text.lines() {
            if let Some(rest) = line.strip_prefix("MemTotal:") {
                mem_total_kib = rest
                    .split_whitespace()
                    .next()
                    .and_then(|v| v.parse::<u64>().ok());
            } else if let Some(rest) = line.strip_prefix("MemAvailable:") {
                mem_avail_kib = rest
                    .split_whitespace()
                    .next()
                    .and_then(|v| v.parse::<u64>().ok());
            }
        }

        match (mem_total_kib, mem_avail_kib) {
            (Some(tot_kib), Some(avail_kib)) if tot_kib > 0 => {
                let tot = (tot_kib * 1024) as f64;
                let avail = (avail_kib.min(tot_kib) * 1024) as f64;
                let used = (tot - avail).max(0.0);
                let pct = (used / tot * 100.0).clamp(0.0, 100.0);
                (
                    Metric::measured(tot, b_prov),
                    Metric::measured(used, b_prov),
                    Metric::measured(avail, b_prov),
                    Metric::measured(pct, p_prov),
                )
            }
            _ => (
                Metric::unavailable(UnavailableReason::TransientError, b_prov),
                Metric::unavailable(UnavailableReason::TransientError, b_prov),
                Metric::unavailable(UnavailableReason::TransientError, b_prov),
                Metric::unavailable(UnavailableReason::TransientError, p_prov),
            ),
        }
    }

    /// Honestly encodes `EACCES`/`EPERM` (`ErrorKind::PermissionDenied`) on `/proc/[pid]/io` or
    /// `/proc/[pid]/stat` as `UnavailableReason::AccessDenied` rather than dropping or fabricating `0.0`.
    pub fn build_process_io_metrics(
        io_read: Result<&str, ErrorKind>,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> (Metric<f64>, Metric<f64>) {
        let prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::BYTES),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        match io_read {
            Ok(txt) => {
                let mut r_bytes: Option<f64> = None;
                let mut w_bytes: Option<f64> = None;
                for line in txt.lines() {
                    if let Some(rest) = line.strip_prefix("read_bytes:") {
                        r_bytes = rest.trim().parse::<f64>().ok();
                    } else if let Some(rest) = line.strip_prefix("write_bytes:") {
                        w_bytes = rest.trim().parse::<f64>().ok();
                    }
                }
                let rm = r_bytes
                    .map(|v| Metric::measured(v, prov))
                    .unwrap_or_else(|| {
                        Metric::unavailable(UnavailableReason::TransientError, prov)
                    });
                let wm = w_bytes
                    .map(|v| Metric::measured(v, prov))
                    .unwrap_or_else(|| {
                        Metric::unavailable(UnavailableReason::TransientError, prov)
                    });
                (rm, wm)
            }
            Err(ErrorKind::PermissionDenied) => (
                Metric::unavailable(UnavailableReason::AccessDenied, prov),
                Metric::unavailable(UnavailableReason::AccessDenied, prov),
            ),
            Err(_) => (
                Metric::unavailable(UnavailableReason::TransientError, prov),
                Metric::unavailable(UnavailableReason::TransientError, prov),
            ),
        }
    }

    /// Builds a `ProcessSnapshot` preserving `AccessDenied` when `/proc/[pid]/io` raises `EACCES`/`EPERM`.
    pub fn make_restricted_process_snapshot(
        key: ProcessKey,
        ppid: i32,
        name: CompactString,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> ProcessSnapshot {
        let b_prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::BYTES),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let p_prov = MetricProvenance {
            source,
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        ProcessSnapshot {
            key,
            ppid,
            name,
            cpu_percent: Metric::unavailable(UnavailableReason::AccessDenied, p_prov),
            phys_footprint_bytes: Metric::unavailable(UnavailableReason::AccessDenied, b_prov),
            resident_bytes: Metric::unavailable(UnavailableReason::AccessDenied, b_prov),
            disk_read_bytes: Metric::unavailable(UnavailableReason::AccessDenied, b_prov),
            disk_write_bytes: Metric::unavailable(UnavailableReason::AccessDenied, b_prov),
            exited_at_ns: None,
        }
    }

    /// Parses `/proc/net/{tcp,tcp6,udp,udp6}` socket tables into `(NetworkConnectionEntry, inode)` tuples.
    pub fn parse_proc_net_table(
        content: &str,
        protocol: ConnectionProtocol,
        inode_map: &HashMap<u64, (i32, CompactString)>,
    ) -> Vec<NetworkConnectionEntry> {
        let is_ipv6 = matches!(
            protocol,
            ConnectionProtocol::Tcp6 | ConnectionProtocol::Udp6
        );
        let is_udp = matches!(
            protocol,
            ConnectionProtocol::Udp4 | ConnectionProtocol::Udp6
        );
        let mut out = Vec::new();
        for (line_idx, raw_line) in content.lines().enumerate() {
            if line_idx == 0 {
                continue;
            }
            let parts: Vec<&str> = raw_line.split_whitespace().collect();
            if parts.len() < 10 {
                continue;
            }
            let Some((laddr, lport)) = parse_proc_hex_addr_port(parts[1], is_ipv6) else {
                continue;
            };
            let Some((raddr, rport)) = parse_proc_hex_addr_port(parts[2], is_ipv6) else {
                continue;
            };
            let state = if is_udp {
                TcpState::Unknown
            } else {
                parse_proc_tcp_hex_state(parts[3])
            };
            let inode = parts[9].parse::<u64>().unwrap_or(0);
            let (pid, process_name) = inode_map
                .get(&inode)
                .map(|(p, n)| (Some(*p), n.clone()))
                .unwrap_or((None, CompactString::const_new("")));
            out.push(NetworkConnectionEntry {
                protocol,
                local_address: CompactString::from(laddr),
                local_port: lport,
                remote_address: CompactString::from(raddr),
                remote_port: rport,
                state,
                pid,
                process_key: None,
                process_name,
                rx_bytes: None,
                tx_bytes: None,
            });
        }
        out
    }

    /// Parses `systemctl list-units --type=service --all --no-pager --no-legend` output.
    pub fn parse_systemd_units(output: &str) -> Vec<ServiceEntry> {
        let mut out = Vec::new();
        for raw_line in output.lines() {
            let line = raw_line.trim().trim_start_matches('●').trim();
            if line.is_empty() {
                continue;
            }
            let parts: Vec<&str> = line.split_whitespace().collect();
            if parts.len() < 4 {
                continue;
            }
            let unit = parts[0];
            let active = parts[2];
            let sub = parts[3];
            let desc = if parts.len() > 4 {
                parts[4..].join(" ")
            } else {
                String::new()
            };
            let state = match (active, sub) {
                ("active", "running") | ("active", "exited") => ServiceState::Running,
                ("activating", _) => ServiceState::StartPending,
                ("deactivating", _) => ServiceState::StopPending,
                ("inactive", _) | ("failed", _) => ServiceState::Stopped,
                _ => ServiceState::Unknown,
            };
            out.push(ServiceEntry {
                name: CompactString::from(unit),
                display_name: CompactString::from(unit.trim_end_matches(".service")),
                description: CompactString::from(desc),
                state,
                start_type: ServiceStartType::Unknown,
                pid: None,
                binary_path: CompactString::const_new(""),
                user_account: CompactString::const_new(""),
            });
        }
        out
    }

    /// Parses an XDG `.desktop` autostart file (`~/.config/autostart/*.desktop`, `/etc/xdg/autostart/*.desktop`).
    pub fn parse_xdg_autostart_desktop(
        filename: &str,
        location: &str,
        content: &str,
        user_writable: bool,
    ) -> StartupItem {
        let mut name = filename.trim_end_matches(".desktop").to_string();
        let mut exec = String::new();
        let mut hidden = false;
        let mut enabled = true;
        for line in content.lines() {
            let trimmed = line.trim();
            if let Some(v) = trimmed.strip_prefix("Name=") {
                name = v.trim().to_string();
            } else if let Some(v) = trimmed.strip_prefix("Exec=") {
                exec = v.trim().to_string();
            } else if let Some(v) = trimmed.strip_prefix("Hidden=") {
                hidden = v.trim().eq_ignore_ascii_case("true");
            } else if let Some(v) = trimmed.strip_prefix("X-GNOME-Autostart-enabled=") {
                enabled = !v.trim().eq_ignore_ascii_case("false");
            }
        }
        StartupItem {
            name: CompactString::from(name),
            command: CompactString::from(exec),
            publisher: CompactString::const_new(""),
            location: CompactString::from(location),
            is_enabled: !hidden && enabled,
            run_at_load: !hidden && enabled,
            item_type: StartupItemType::XdgAutostart,
            user_writable,
        }
    }

    /// Parses `nmcli -t -f IN-USE,SSID,BSSID,CHAN,SIGNAL,SECURITY dev wifi` output.
    pub fn parse_linux_nmcli_wifi(
        output: &str,
        sampled_at_ns: u64,
        source: SourceId,
    ) -> WirelessScanReport {
        let mut networks = Vec::new();
        let mut connected_ssid = None;
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

        for line in output.lines() {
            if line.trim().is_empty() {
                continue;
            }
            // nmcli escapes `:` in BSSIDs as `\:`
            let normalized = line.replace("\\:", "__COLON__");
            let cols: Vec<&str> = normalized.split(':').collect();
            if cols.len() < 6 {
                continue;
            }
            let is_connected = cols[0].trim() == "*";
            let ssid_raw = cols[1].replace("__COLON__", ":");
            let ssid = if ssid_raw.is_empty() {
                "<hidden>".to_string()
            } else {
                ssid_raw
            };
            let bssid = cols[2].replace("__COLON__", ":");
            let channel = cols[3].trim().parse::<u32>().unwrap_or(0);
            let signal_pct = cols[4].trim().parse::<f64>().ok();
            let security = cols[5].replace("__COLON__", ":");

            // Approximate dBm from nmcli 0..100% quality scale (`-100 + pct * 0.5`)
            let rssi_dbm = match signal_pct {
                Some(pct) => Metric::measured(-100.0 + pct.clamp(0.0, 100.0) * 0.5, dbm_prov),
                None => Metric::unavailable(UnavailableReason::NotApplicable, dbm_prov),
            };
            let band = if channel > 14 {
                WifiBand::Band5GHz
            } else if channel > 0 {
                WifiBand::Band2_4GHz
            } else {
                WifiBand::Unknown
            };
            if is_connected {
                connected_ssid = Some(CompactString::from(ssid.as_str()));
            }
            networks.push(WifiNetworkEntry {
                ssid: CompactString::from(ssid),
                bssid: if bssid.is_empty() {
                    None
                } else {
                    Some(CompactString::from(bssid))
                },
                rssi_dbm,
                noise_dbm: Metric::unavailable(UnavailableReason::Unsupported, dbm_prov),
                snr_db: Metric::unavailable(UnavailableReason::Unsupported, snr_prov),
                channel,
                channel_width_mhz: None,
                band,
                phy_mode: CompactString::const_new("802.11"),
                security: CompactString::from(if security.is_empty() {
                    "Open"
                } else {
                    &security
                }),
                is_connected,
                redacted: false,
            });
        }
        WirelessScanReport {
            interface: CompactString::const_new("wlan0"),
            connected_ssid,
            networks,
            notes: CompactString::const_new("Parsed from nmcli dev wifi"),
        }
    }

    /// Parses `iw dev wlan0 scan` blocks into `WirelessScanReport`.
    pub fn parse_linux_iw_scan(
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
            quality: MetricQuality::Authoritative,
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

        let mut cur_bssid: Option<String> = None;
        let mut cur_ssid: Option<String> = None;
        let mut cur_rssi: Option<f64> = None;
        let mut cur_freq: Option<u32> = None;

        let flush = |networks: &mut Vec<WifiNetworkEntry>,
                     bssid: &mut Option<String>,
                     ssid: &mut Option<String>,
                     rssi: &mut Option<f64>,
                     freq: &mut Option<u32>| {
            if let Some(b) = bssid.take() {
                let f = freq.take().unwrap_or(0);
                let (band, channel) = freq_mhz_to_band_channel(f);
                let rssi_m = rssi
                    .take()
                    .map(|v| Metric::measured(v, dbm_prov))
                    .unwrap_or_else(|| {
                        Metric::unavailable(UnavailableReason::NotApplicable, dbm_prov)
                    });
                networks.push(WifiNetworkEntry {
                    ssid: CompactString::from(ssid.take().unwrap_or_else(|| "<hidden>".into())),
                    bssid: Some(CompactString::from(b)),
                    rssi_dbm: rssi_m,
                    noise_dbm: Metric::unavailable(UnavailableReason::Unsupported, dbm_prov),
                    snr_db: Metric::unavailable(UnavailableReason::Unsupported, snr_prov),
                    channel,
                    channel_width_mhz: None,
                    band,
                    phy_mode: CompactString::const_new("802.11"),
                    security: CompactString::const_new("Unknown"),
                    is_connected: false,
                    redacted: false,
                });
            }
        };

        for raw_line in output.lines() {
            let line = raw_line.trim();
            if let Some(rest) = line.strip_prefix("BSS ") {
                flush(
                    &mut networks,
                    &mut cur_bssid,
                    &mut cur_ssid,
                    &mut cur_rssi,
                    &mut cur_freq,
                );
                let b = rest
                    .split(['(', ' '])
                    .next()
                    .unwrap_or("")
                    .trim()
                    .to_string();
                if !b.is_empty() {
                    cur_bssid = Some(b);
                }
            } else if let Some(rest) = line.strip_prefix("SSID:") {
                cur_ssid = Some(rest.trim().to_string());
            } else if let Some(rest) = line.strip_prefix("signal:") {
                cur_rssi = rest
                    .split_whitespace()
                    .next()
                    .and_then(|v| v.parse::<f64>().ok());
            } else if let Some(rest) = line.strip_prefix("freq:") {
                cur_freq = rest
                    .split_whitespace()
                    .next()
                    .and_then(|v| v.parse::<u32>().ok());
            }
        }
        flush(
            &mut networks,
            &mut cur_bssid,
            &mut cur_ssid,
            &mut cur_rssi,
            &mut cur_freq,
        );

        WirelessScanReport {
            interface: CompactString::const_new("wlan0"),
            connected_ssid: None,
            networks,
            notes: CompactString::const_new("Parsed from iw scan"),
        }
    }

    fn parse_cpu_line(line: &str) -> Option<(Option<u32>, LinuxCpuTicks)> {
        let mut parts = line.split_whitespace();
        let prefix = parts.next()?;
        if !prefix.starts_with("cpu") {
            return None;
        }
        let core_idx = if prefix == "cpu" {
            None
        } else {
            let idx = prefix.strip_prefix("cpu")?.parse::<u32>().ok()?;
            Some(idx)
        };
        let user = parts.next()?.parse().ok()?;
        let nice = parts.next()?.parse().ok()?;
        let system = parts.next()?.parse().ok()?;
        let idle = parts.next()?.parse().ok()?;
        let iowait = parts.next().and_then(|s| s.parse().ok()).unwrap_or(0);
        let irq = parts.next().and_then(|s| s.parse().ok()).unwrap_or(0);
        let softirq = parts.next().and_then(|s| s.parse().ok()).unwrap_or(0);
        let steal = parts.next().and_then(|s| s.parse().ok()).unwrap_or(0);
        Some((
            core_idx,
            LinuxCpuTicks {
                user,
                nice,
                system,
                idle,
                iowait,
                irq,
                softirq,
                steal,
            },
        ))
    }

    #[cfg(target_os = "linux")]
    fn collect_linux_host(
        state: &mut LinuxCollectorInner,
        snap: &mut SystemSnapshot,
        sampled_at_ns: u64,
    ) {
        let src_stat = snap.intern_source("linux.proc.stat");
        let src_mem = snap.intern_source("linux.proc.meminfo");
        let src_load = snap.intern_source("linux.proc.loadavg");
        let src_up = snap.intern_source("linux.proc.uptime");
        let src_disk = snap.intern_source("linux.proc.diskstats");
        let src_statvfs = snap.intern_source("linux.disk.statvfs");
        let src_net = snap.intern_source("linux.proc.net_dev");
        let src_proc = snap.intern_source("linux.proc.pid_stat");
        let src_proc_io = snap.intern_source("linux.proc.pid_io");
        let src_therm = snap.intern_source("linux.sysfs.thermal");
        let src_power = snap.intern_source("linux.sysfs.power_supply");

        let mut procfs_ok = false;

        // 1. CPU (aggregate + per-core from /proc/stat)
        let prev_cpu_ns = state.prev_total_cpu.map(|(_, ns)| ns);
        let cpu_win_ns = prev_cpu_ns
            .map(|p| sampled_at_ns.saturating_sub(p))
            .unwrap_or(0);
        let pct_prov = MetricProvenance {
            source: src_stat,
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns,
            window_ns: cpu_win_ns,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };

        if let Ok(stat_txt) = std::fs::read_to_string("/proc/stat") {
            procfs_ok = true;
            let mut total_cur: Option<LinuxCpuTicks> = None;
            let mut cur_cores: Vec<(u32, LinuxCpuTicks)> = Vec::new();
            for line in stat_txt.lines() {
                if !line.starts_with("cpu") {
                    break;
                }
                if let Some((core_idx, ticks)) = Self::parse_cpu_line(line) {
                    match core_idx {
                        None => total_cur = Some(ticks),
                        Some(idx) => cur_cores.push((idx, ticks)),
                    }
                }
            }

            if let Some(cur_ticks) = total_cur {
                if let Some((prev_ticks, _)) =
                    state.prev_total_cpu.replace((cur_ticks, sampled_at_ns))
                {
                    let (tot, usr, sys) =
                        compute_linux_cpu_delta_metrics(prev_ticks, cur_ticks, pct_prov);
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

            cur_cores.sort_by_key(|(idx, _)| *idx);
            snap.system.performance_core_count = cur_cores.len() as u32;
            snap.system.efficiency_core_count = 0;

            let mut next_cores = HashMap::with_capacity(cur_cores.len());
            for (idx, cur) in cur_cores {
                let (tot_m, usr_m, sys_m, idle_m) = if let Some(&prev) =
                    state.prev_core_cpus.get(&idx)
                {
                    if cur.total() > prev.total() && cur.busy() >= prev.busy() {
                        let d_tot = (cur.total() - prev.total()) as f64;
                        let d_busy = (cur.busy() - prev.busy()) as f64;
                        let d_user =
                            ((cur.user + cur.nice).saturating_sub(prev.user + prev.nice)) as f64;
                        let d_sys = ((cur.system + cur.irq + cur.softirq)
                            .saturating_sub(prev.system + prev.irq + prev.softirq))
                            as f64;
                        let d_idle = ((cur.idle + cur.iowait)
                            .saturating_sub(prev.idle + prev.iowait))
                            as f64;
                        (
                            Metric::measured((d_busy / d_tot * 100.0).clamp(0.0, 100.0), pct_prov),
                            Metric::measured((d_user / d_tot * 100.0).clamp(0.0, 100.0), pct_prov),
                            Metric::measured((d_sys / d_tot * 100.0).clamp(0.0, 100.0), pct_prov),
                            Metric::measured((d_idle / d_tot * 100.0).clamp(0.0, 100.0), pct_prov),
                        )
                    } else {
                        let p = Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                        (p, p, p, p)
                    }
                } else {
                    let p = Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                    (p, p, p, p)
                };
                snap.system.cores.push(CoreTelemetry {
                    core_index: idx,
                    class: LogicalProcessorClass::Performance,
                    total_percent: tot_m,
                    user_percent: usr_m,
                    system_percent: sys_m,
                    idle_percent: idle_m,
                });
                next_cores.insert(idx, cur);
            }
            state.prev_core_cpus = next_cores;
        }

        // Load averages
        if let Ok(load_txt) = std::fs::read_to_string("/proc/loadavg") {
            let mut vals = load_txt
                .split_whitespace()
                .filter_map(|s| s.parse::<f64>().ok());
            let load_prov = MetricProvenance {
                source: src_load,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            if let (Some(l1), Some(l5), Some(l15)) = (vals.next(), vals.next(), vals.next()) {
                snap.system.load_avg_1m = Metric::measured(l1, load_prov);
                snap.system.load_avg_5m = Metric::measured(l5, load_prov);
                snap.system.load_avg_15m = Metric::measured(l15, load_prov);
            }
        }

        // Uptime
        if let Ok(up_txt) = std::fs::read_to_string("/proc/uptime")
            && let Some(secs) = up_txt
                .split_whitespace()
                .next()
                .and_then(|s| s.parse::<f64>().ok())
        {
            snap.system.uptime_seconds = Metric::measured(
                secs.max(0.0),
                MetricProvenance {
                    source: src_up,
                    unit_tag: Some(UnitTag::SECONDS),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                },
            );
        }

        // CPU clock frequency (sysfs cpufreq or /proc/cpuinfo)
        if let Some(mhz) = read_linux_cpu_mhz() {
            snap.hardware_telemetry.soc_power.p_cluster_mhz = Metric::measured(
                mhz,
                MetricProvenance {
                    source: snap.intern_source("linux.sysfs.cpufreq"),
                    unit_tag: Some(UnitTag::MEGAHERTZ),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                },
            );
        }

        // 2. Memory & Swap (/proc/meminfo + /proc/pressure/memory)
        if let Ok(mem_txt) = std::fs::read_to_string("/proc/meminfo") {
            procfs_ok = true;
            let (tot, used, free, pct) = Self::parse_proc_meminfo(&mem_txt, sampled_at_ns, src_mem);
            snap.system.mem_total_bytes = tot;
            snap.system.mem_used_bytes = used;
            snap.system.mem_free_bytes = free;
            snap.system.mem_used_percent = pct;

            let parsed_mem = parse_meminfo_details(&mem_txt);
            let b_prov = MetricProvenance {
                source: src_mem,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let p_prov = MetricProvenance {
                source: src_mem,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            if let Some(active_kib) = parsed_mem.active_kib {
                snap.system.mem_active_bytes = Metric::measured((active_kib * 1024) as f64, b_prov);
            }
            if let Some(wired_kib) = parsed_mem.mlocked_kib.or(parsed_mem.unevictable_kib) {
                snap.system.mem_wired_bytes = Metric::measured((wired_kib * 1024) as f64, b_prov);
            }
            let cached_kib = parsed_mem
                .cached_kib
                .unwrap_or(0)
                .saturating_add(parsed_mem.buffers_kib.unwrap_or(0))
                .saturating_add(parsed_mem.sreclaimable_kib.unwrap_or(0));
            snap.system.mem_cached_files_bytes =
                Metric::measured((cached_kib * 1024) as f64, b_prov);
            snap.system.mem_compressed_bytes =
                Metric::measured((parsed_mem.zswap_kib.unwrap_or(0) * 1024) as f64, b_prov);

            if let (Some(sw_tot_kib), Some(sw_free_kib)) =
                (parsed_mem.swap_total_kib, parsed_mem.swap_free_kib)
            {
                let sw_tot = (sw_tot_kib * 1024) as f64;
                let sw_used = (sw_tot_kib.saturating_sub(sw_free_kib) * 1024) as f64;
                let sw_pct = if sw_tot_kib > 0 {
                    (sw_used / sw_tot * 100.0).clamp(0.0, 100.0)
                } else {
                    0.0
                };
                snap.system.swap_total_bytes = Metric::measured(sw_tot, b_prov);
                snap.system.swap_used_bytes = Metric::measured(sw_used, b_prov);
                snap.system.swap_used_percent = Metric::measured(sw_pct, p_prov);
            }

            if let Some(psi) = read_linux_mem_psi_avg10() {
                snap.system.system_pressure_percent = Metric::measured(
                    psi.clamp(0.0, 100.0),
                    MetricProvenance {
                        source: snap.intern_source("linux.proc.pressure_memory"),
                        unit_tag: Some(UnitTag::PERCENT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Authoritative,
                        contributed_by_stale: false,
                    },
                );
            } else if let MetricReading::Measured { value: mem_pct, .. } = pct.reading {
                let est = if mem_pct >= 90.0 {
                    90.0
                } else if mem_pct >= 75.0 {
                    60.0
                } else {
                    10.0
                };
                snap.system.system_pressure_percent = Metric::measured(
                    est,
                    MetricProvenance {
                        source: src_mem,
                        unit_tag: Some(UnitTag::PERCENT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Estimated,
                        contributed_by_stale: false,
                    },
                );
            }
        }

        // 3. Disks (statvfs("/") + /proc/diskstats)
        let (root_total, root_avail, root_pct) =
            linux_root_statvfs_metrics(sampled_at_ns, src_statvfs);
        let disk_bytes_prov = MetricProvenance {
            source: src_disk,
            unit_tag: Some(UnitTag::BYTES),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let disk_cnt_prov = MetricProvenance {
            source: src_disk,
            unit_tag: Some(UnitTag::COUNT),
            sampled_at_ns,
            window_ns: 0,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };

        let mut parsed_disks = std::fs::read_to_string("/proc/diskstats")
            .map(|txt| parse_proc_diskstats(&txt))
            .unwrap_or_default();
        let whole_disks: Vec<_> = parsed_disks
            .iter()
            .filter(|(name, ..)| is_linux_whole_block_device(name))
            .cloned()
            .collect();
        if !whole_disks.is_empty() {
            parsed_disks = whole_disks;
        }

        let mut seen_disks = HashSet::new();
        for (dev_name, r_ops, r_bytes, w_ops, w_bytes) in parsed_disks {
            if !seen_disks.insert(dev_name.clone()) {
                continue;
            }
            let prev = state.prev_disks.insert(
                dev_name.clone(),
                PrevIoCounter {
                    rx_or_read: r_bytes,
                    tx_or_write: w_bytes,
                    sampled_at_ns,
                },
            );
            let (read_rate, write_rate) =
                compute_io_rate_metrics(prev, r_bytes, w_bytes, sampled_at_ns, src_disk);
            snap.disks.push(DiskSnapshot {
                bsd_name: dev_name,
                read_bytes_total: Metric::measured(r_bytes as f64, disk_bytes_prov),
                write_bytes_total: Metric::measured(w_bytes as f64, disk_bytes_prov),
                read_ops_total: Metric::measured(r_ops as f64, disk_cnt_prov),
                write_ops_total: Metric::measured(w_ops as f64, disk_cnt_prov),
                read_bytes_per_sec: read_rate,
                write_bytes_per_sec: write_rate,
                root_total_bytes: root_total,
                root_available_bytes: root_avail,
                root_used_percent: root_pct,
            });
        }
        if snap.disks.is_empty() {
            let unavail_bytes =
                Metric::unavailable(UnavailableReason::Unsupported, disk_bytes_prov);
            let unavail_cnt = Metric::unavailable(UnavailableReason::Unsupported, disk_cnt_prov);
            let unavail_rate = Metric::unavailable(
                UnavailableReason::Unsupported,
                MetricProvenance {
                    source: src_disk,
                    unit_tag: Some(UnitTag::BYTES_PER_SEC),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Unknown,
                    contributed_by_stale: false,
                },
            );
            snap.disks.push(DiskSnapshot {
                bsd_name: CompactString::const_new("root"),
                read_bytes_total: unavail_bytes,
                write_bytes_total: unavail_bytes,
                read_ops_total: unavail_cnt,
                write_ops_total: unavail_cnt,
                read_bytes_per_sec: unavail_rate,
                write_bytes_per_sec: unavail_rate,
                root_total_bytes: root_total,
                root_available_bytes: root_avail,
                root_used_percent: root_pct,
            });
        }

        // 4. Networks (/proc/net/dev)
        if let Ok(net_txt) = std::fs::read_to_string("/proc/net/dev") {
            let net_bytes_prov = MetricProvenance {
                source: src_net,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let net_pkt_prov = MetricProvenance {
                source: src_net,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            for (iface, rx_bytes, rx_pkts, tx_bytes, tx_pkts) in parse_proc_net_dev(&net_txt) {
                let prev = state.prev_nets.insert(
                    iface.clone(),
                    PrevIoCounter {
                        rx_or_read: rx_bytes,
                        tx_or_write: tx_bytes,
                        sampled_at_ns,
                    },
                );
                let (rx_rate, tx_rate) =
                    compute_io_rate_metrics(prev, rx_bytes, tx_bytes, sampled_at_ns, src_net);
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

        // 5. Processes (/proc/[pid]/stat + /proc/[pid]/io + 5s exited tombstones)
        let (page_size, clk_tck) = linux_page_and_clk_tck();
        let mut next_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)> = HashMap::new();
        if let Ok(entries) = std::fs::read_dir("/proc") {
            let b_prov = MetricProvenance {
                source: src_proc,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            for entry in entries.flatten() {
                let fname = entry.file_name();
                let Some(pid_str) = fname.to_str() else {
                    continue;
                };
                let Ok(pid) = pid_str.parse::<i32>() else {
                    continue;
                };
                if pid <= 0 {
                    continue;
                }
                let proc_dir = entry.path();
                let Ok(stat_raw) = std::fs::read_to_string(proc_dir.join("stat")) else {
                    continue;
                };
                let Some(st) = parse_proc_pid_stat(&stat_raw, page_size) else {
                    continue;
                };
                let uid = linux_proc_uid(&proc_dir);
                let creation_token_us =
                    ((u128::from(st.starttime_ticks) * 1_000_000) / u128::from(clk_tck)) as u64;
                let key = ProcessKey {
                    pid,
                    creation_token_us,
                    uid,
                };
                let total_cpu_ticks = st.utime_ticks.saturating_add(st.stime_ticks);
                let total_cpu_ns =
                    ((u128::from(total_cpu_ticks) * 1_000_000_000) / u128::from(clk_tck)) as u64;

                let cpu_percent =
                    if let Some((prev_cpu_ns, prev_at_ns, _)) = state.prev_procs.get(&key) {
                        let dt_ns = sampled_at_ns.saturating_sub(*prev_at_ns);
                        let pct_prov_proc = MetricProvenance {
                            source: src_proc,
                            unit_tag: Some(UnitTag::PERCENT),
                            sampled_at_ns,
                            window_ns: dt_ns,
                            quality: MetricQuality::Authoritative,
                            contributed_by_stale: false,
                        };
                        if dt_ns > 0 && total_cpu_ns >= *prev_cpu_ns {
                            let d_cpu = total_cpu_ns - *prev_cpu_ns;
                            let pct = ((d_cpu as f64) / (dt_ns as f64) * 100.0).max(0.0);
                            Metric::measured(pct, pct_prov_proc)
                        } else {
                            Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov_proc)
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

                let (disk_read_bytes, disk_write_bytes) =
                    match std::fs::read_to_string(proc_dir.join("io")) {
                        Ok(io_txt) => {
                            Self::build_process_io_metrics(Ok(&io_txt), sampled_at_ns, src_proc_io)
                        }
                        Err(e) => Self::build_process_io_metrics(
                            Err(e.kind()),
                            sampled_at_ns,
                            src_proc_io,
                        ),
                    };

                let rss = Metric::measured(st.rss_bytes as f64, b_prov);
                let proc_snap = ProcessSnapshot {
                    key,
                    ppid: st.ppid,
                    name: st.comm,
                    cpu_percent,
                    phys_footprint_bytes: rss,
                    resident_bytes: rss,
                    disk_read_bytes,
                    disk_write_bytes,
                    exited_at_ns: None,
                };
                snap.processes.push(proc_snap.clone());
                next_procs.insert(key, (total_cpu_ns, sampled_at_ns, proc_snap));
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

        // 6. Thermals (/sys/class/thermal/thermal_zone* + /sys/class/hwmon) & Power (/sys/class/power_supply/BAT*)
        collect_linux_thermals(snap, sampled_at_ns, src_therm);
        if let Some(watts) = read_linux_battery_watts() {
            snap.hardware_telemetry.soc_power.system_power_in_watts = Metric::measured(
                watts,
                MetricProvenance {
                    source: src_power,
                    unit_tag: Some(UnitTag::WATTS),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                },
            );
        }

        snap.provider_health.push(ProviderHealth {
            id: CompactString::const_new("linux.procfs"),
            state: if procfs_ok {
                ProviderHealthState::Healthy
            } else {
                ProviderHealthState::Unavailable
            },
            consecutive_failures: u32::from(!procfs_ok),
            last_success_at_ns: if procfs_ok { sampled_at_ns } else { 0 },
            last_error: if procfs_ok {
                None
            } else {
                Some(CompactString::const_new("/proc/stat unavailable"))
            },
        });
    }
}

/// Reads a PID-reuse-safe `ProcessKey(pid, creation_token_us, uid)`, `ppid`, and `name` on Linux.
pub fn read_process_key(pid: i32) -> Option<(ProcessKey, i32, CompactString)> {
    #[cfg(target_os = "linux")]
    {
        if pid <= 0 {
            return None;
        }
        let proc_dir = std::path::PathBuf::from(format!("/proc/{pid}"));
        let stat_raw = std::fs::read_to_string(proc_dir.join("stat")).ok()?;
        let (page_size, clk_tck) = linux_page_and_clk_tck();
        let st = parse_proc_pid_stat(&stat_raw, page_size)?;
        let creation_token_us =
            ((u128::from(st.starttime_ticks) * 1_000_000) / u128::from(clk_tck)) as u64;
        let uid = linux_proc_uid(&proc_dir);
        Some((
            ProcessKey {
                pid,
                creation_token_us,
                uid,
            },
            st.ppid,
            st.comm,
        ))
    }
    #[cfg(not(target_os = "linux"))]
    {
        let _ = pid;
        None
    }
}

/// Parses a single `/proc/[pid]/stat` line, safely handling spaces and parentheses inside `(comm)`.
pub fn parse_proc_pid_stat(content: &str, page_size: u64) -> Option<LinuxPidStat> {
    let trimmed = content.trim();
    let lp = trimmed.find('(')?;
    let rp = trimmed.rfind(')')?;
    if rp <= lp {
        return None;
    }
    let pid = trimmed[..lp].trim().parse::<i32>().ok()?;
    let comm = CompactString::from(trimmed[lp + 1..rp].trim());
    let fields: Vec<&str> = trimmed[rp + 1..].split_whitespace().collect();
    if fields.len() < 22 {
        return None;
    }
    let state = fields[0].chars().next().unwrap_or('?');
    let ppid = fields[1].parse::<i32>().ok()?;
    let utime_ticks = fields[11].parse::<u64>().ok()?;
    let stime_ticks = fields[12].parse::<u64>().ok()?;
    let num_threads = fields[17].parse::<u32>().unwrap_or(1);
    let starttime_ticks = fields[19].parse::<u64>().ok()?;
    let vsize_bytes = fields[20].parse::<u64>().unwrap_or(0);
    let rss_pages = fields[21].parse::<u64>().unwrap_or(0);
    let rss_bytes = rss_pages.saturating_mul(page_size.max(1));
    Some(LinuxPidStat {
        pid,
        comm,
        state,
        ppid,
        utime_ticks,
        stime_ticks,
        num_threads,
        starttime_ticks,
        vsize_bytes,
        rss_bytes,
    })
}

/// Parses `/proc/net/dev` into `(interface, rx_bytes, rx_packets, tx_bytes, tx_packets)`, skipping `lo`.
pub fn parse_proc_net_dev(content: &str) -> Vec<(CompactString, u64, u64, u64, u64)> {
    let mut out = Vec::new();
    for (idx, line) in content.lines().enumerate() {
        if idx < 2 {
            continue;
        }
        let Some((iface_raw, rest)) = line.split_once(':') else {
            continue;
        };
        let iface = iface_raw.trim();
        if iface.is_empty() || iface == "lo" {
            continue;
        }
        let cols: Vec<&str> = rest.split_whitespace().collect();
        if cols.len() < 10 {
            continue;
        }
        let Ok(rx_bytes) = cols[0].parse::<u64>() else {
            continue;
        };
        let Ok(rx_packets) = cols[1].parse::<u64>() else {
            continue;
        };
        let Ok(tx_bytes) = cols[8].parse::<u64>() else {
            continue;
        };
        let Ok(tx_packets) = cols[9].parse::<u64>() else {
            continue;
        };
        out.push((
            CompactString::from(iface),
            rx_bytes,
            rx_packets,
            tx_bytes,
            tx_packets,
        ));
    }
    out
}

/// Parses `/proc/diskstats` into `(device_name, read_ops, read_bytes, write_ops, write_bytes)`, skipping `loop*`, `ram*`, `zram*`.
pub fn parse_proc_diskstats(content: &str) -> Vec<(CompactString, u64, u64, u64, u64)> {
    let mut out = Vec::new();
    for line in content.lines() {
        let cols: Vec<&str> = line.split_whitespace().collect();
        if cols.len() < 10 {
            continue;
        }
        let name = cols[2];
        if name.starts_with("loop") || name.starts_with("ram") || name.starts_with("zram") {
            continue;
        }
        let Ok(read_ops) = cols[3].parse::<u64>() else {
            continue;
        };
        let Ok(sectors_read) = cols[5].parse::<u64>() else {
            continue;
        };
        let Ok(write_ops) = cols[7].parse::<u64>() else {
            continue;
        };
        let Ok(sectors_written) = cols[9].parse::<u64>() else {
            continue;
        };
        out.push((
            CompactString::from(name),
            read_ops,
            sectors_read.saturating_mul(512),
            write_ops,
            sectors_written.saturating_mul(512),
        ));
    }
    out
}

/// Parses a Linux sysfs thermal sensor reading in millidegrees Celsius (`42500` -> `42.5`).
pub fn parse_thermal_millidegrees(content: &str) -> Option<f64> {
    let milli = content.trim().parse::<i64>().ok()?;
    let celsius = (milli as f64) / 1000.0;
    if celsius.is_finite() && (-50.0..=200.0).contains(&celsius) {
        Some(celsius)
    } else {
        None
    }
}

#[cfg(target_os = "linux")]
fn linux_page_and_clk_tck() -> (u64, u64) {
    // SAFETY: `sysconf(_SC_PAGESIZE)` and `sysconf(_SC_CLK_TCK)` are thread-safe POSIX queries.
    unsafe {
        let page = libc::sysconf(libc::_SC_PAGESIZE).max(4096) as u64;
        let clk = libc::sysconf(libc::_SC_CLK_TCK).max(1) as u64;
        (page, clk)
    }
}

#[cfg(target_os = "linux")]
fn linux_proc_uid(proc_dir: &std::path::Path) -> Option<u32> {
    use std::os::unix::fs::MetadataExt;
    std::fs::metadata(proc_dir).ok().map(|m| m.uid())
}

#[cfg(target_os = "linux")]
fn compute_linux_cpu_delta_metrics(
    p: LinuxCpuTicks,
    c: LinuxCpuTicks,
    prov: MetricProvenance,
) -> (Metric<f64>, Metric<f64>, Metric<f64>) {
    if c.total() <= p.total() || c.busy() < p.busy() {
        let pending = Metric::unavailable(UnavailableReason::NotYetAvailable, prov);
        return (pending, pending, pending);
    }
    let d_tot = (c.total() - p.total()) as f64;
    let d_busy = (c.busy() - p.busy()) as f64;
    let d_user = ((c.user + c.nice).saturating_sub(p.user + p.nice)) as f64;
    let d_sys =
        ((c.system + c.irq + c.softirq).saturating_sub(p.system + p.irq + p.softirq)) as f64;
    (
        Metric::measured((d_busy / d_tot * 100.0).clamp(0.0, 100.0), prov),
        Metric::measured((d_user / d_tot * 100.0).clamp(0.0, 100.0), prov),
        Metric::measured((d_sys / d_tot * 100.0).clamp(0.0, 100.0), prov),
    )
}

#[cfg(target_os = "linux")]
fn compute_io_rate_metrics(
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

#[cfg(target_os = "linux")]
fn linux_root_statvfs_metrics(
    sampled_at_ns: u64,
    src_statvfs: SourceId,
) -> (Metric<f64>, Metric<f64>, Metric<f64>) {
    let b_prov = MetricProvenance {
        source: src_statvfs,
        unit_tag: Some(UnitTag::BYTES),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };
    let p_prov = MetricProvenance {
        source: src_statvfs,
        unit_tag: Some(UnitTag::PERCENT),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };
    // SAFETY: `c"/"` is a valid NUL-terminated path and `st` is zero-initialized `libc::statvfs`.
    unsafe {
        let mut st: libc::statvfs = std::mem::zeroed();
        if libc::statvfs(c"/".as_ptr(), &raw mut st) == 0 && st.f_blocks > 0 {
            let bsize = if st.f_frsize > 0 {
                st.f_frsize as u64
            } else {
                (st.f_bsize as u64).max(1)
            };
            let total = (st.f_blocks as u64).saturating_mul(bsize);
            let avail = (st.f_bavail as u64).saturating_mul(bsize);
            let free = (st.f_bfree as u64).saturating_mul(bsize);
            let used = total.saturating_sub(free);
            let pct = ((used as f64) / (total as f64) * 100.0).clamp(0.0, 100.0);
            return (
                Metric::measured(total as f64, b_prov),
                Metric::measured(avail as f64, b_prov),
                Metric::measured(pct, p_prov),
            );
        }
    }
    (
        Metric::unavailable(UnavailableReason::TransientError, b_prov),
        Metric::unavailable(UnavailableReason::TransientError, b_prov),
        Metric::unavailable(UnavailableReason::TransientError, p_prov),
    )
}

#[cfg(target_os = "linux")]
#[derive(Default)]
struct LinuxMemInfoDetails {
    active_kib: Option<u64>,
    buffers_kib: Option<u64>,
    cached_kib: Option<u64>,
    sreclaimable_kib: Option<u64>,
    mlocked_kib: Option<u64>,
    unevictable_kib: Option<u64>,
    swap_total_kib: Option<u64>,
    swap_free_kib: Option<u64>,
    zswap_kib: Option<u64>,
}

#[cfg(target_os = "linux")]
fn parse_meminfo_details(text: &str) -> LinuxMemInfoDetails {
    let mut out = LinuxMemInfoDetails::default();
    for line in text.lines() {
        let Some((key, rest)) = line.split_once(':') else {
            continue;
        };
        let Some(val) = rest
            .split_whitespace()
            .next()
            .and_then(|v| v.parse::<u64>().ok())
        else {
            continue;
        };
        match key.trim() {
            "Active" => out.active_kib = Some(val),
            "Buffers" => out.buffers_kib = Some(val),
            "Cached" => out.cached_kib = Some(val),
            "SReclaimable" => out.sreclaimable_kib = Some(val),
            "Mlocked" => out.mlocked_kib = Some(val),
            "Unevictable" => out.unevictable_kib = Some(val),
            "SwapTotal" => out.swap_total_kib = Some(val),
            "SwapFree" => out.swap_free_kib = Some(val),
            "Zswap" => out.zswap_kib = Some(val),
            _ => {}
        }
    }
    out
}

#[cfg(target_os = "linux")]
fn read_linux_mem_psi_avg10() -> Option<f64> {
    let txt = std::fs::read_to_string("/proc/pressure/memory").ok()?;
    let line = txt.lines().find(|l| l.starts_with("some "))?;
    for tok in line.split_whitespace() {
        if let Some(val) = tok.strip_prefix("avg10=") {
            return val.parse::<f64>().ok().filter(|v| v.is_finite());
        }
    }
    None
}

#[cfg(target_os = "linux")]
fn read_linux_cpu_mhz() -> Option<f64> {
    if let Ok(khz_txt) =
        std::fs::read_to_string("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq")
        && let Ok(khz) = khz_txt.trim().parse::<f64>()
        && khz > 0.0
    {
        return Some(khz / 1000.0);
    }
    if let Ok(cpuinfo) = std::fs::read_to_string("/proc/cpuinfo") {
        for line in cpuinfo.lines() {
            if let Some((k, v)) = line.split_once(':')
                && k.trim() == "cpu MHz"
                && let Ok(mhz) = v.trim().parse::<f64>()
                && mhz > 0.0
            {
                return Some(mhz);
            }
        }
    }
    None
}

#[cfg(target_os = "linux")]
fn is_linux_whole_block_device(name: &str) -> bool {
    if name.starts_with("nvme") || name.starts_with("mmcblk") {
        if let Some(n_pos) = name.rfind('n') {
            return !name[n_pos..].contains('p');
        }
        return !name.contains('p');
    }
    if name.starts_with("sd")
        || name.starts_with("vd")
        || name.starts_with("hd")
        || name.starts_with("xvd")
    {
        return name.chars().last().is_some_and(|c| !c.is_ascii_digit());
    }
    !name.chars().last().is_some_and(|c| c.is_ascii_digit())
}

#[cfg(target_os = "linux")]
fn collect_linux_thermals(snap: &mut SystemSnapshot, sampled_at_ns: u64, src_therm: SourceId) {
    let prov = MetricProvenance {
        source: src_therm,
        unit_tag: Some(UnitTag::CELSIUS),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };
    let mut sensors = Vec::new();
    if let Ok(entries) = std::fs::read_dir("/sys/class/thermal") {
        for entry in entries.flatten() {
            let fname = entry.file_name();
            let Some(name_str) = fname.to_str() else {
                continue;
            };
            if !name_str.starts_with("thermal_zone") {
                continue;
            }
            let zone_dir = entry.path();
            let Ok(temp_txt) = std::fs::read_to_string(zone_dir.join("temp")) else {
                continue;
            };
            let Some(celsius) = parse_thermal_millidegrees(&temp_txt) else {
                continue;
            };
            let label = std::fs::read_to_string(zone_dir.join("type"))
                .ok()
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .unwrap_or_else(|| name_str.to_string());
            sensors.push(ThermalSensorReading {
                name: CompactString::from(label),
                celsius: Metric::measured(celsius, prov),
            });
        }
    }
    if sensors.is_empty()
        && let Ok(hwmons) = std::fs::read_dir("/sys/class/hwmon")
    {
        for hw in hwmons.flatten() {
            let hw_dir = hw.path();
            let chip_name = std::fs::read_to_string(hw_dir.join("name"))
                .ok()
                .map(|s| s.trim().to_string())
                .unwrap_or_else(|| "hwmon".to_string());
            let Ok(files) = std::fs::read_dir(&hw_dir) else {
                continue;
            };
            for f in files.flatten() {
                let fname = f.file_name();
                let Some(s) = fname.to_str() else {
                    continue;
                };
                if s.starts_with("temp")
                    && s.ends_with("_input")
                    && let Ok(temp_txt) = std::fs::read_to_string(f.path())
                    && let Some(celsius) = parse_thermal_millidegrees(&temp_txt)
                {
                    let prefix = s.trim_end_matches("_input");
                    let label = std::fs::read_to_string(hw_dir.join(format!("{prefix}_label")))
                        .ok()
                        .map(|l| l.trim().to_string())
                        .filter(|l| !l.is_empty())
                        .unwrap_or_else(|| format!("{chip_name}/{prefix}"));
                    sensors.push(ThermalSensorReading {
                        name: CompactString::from(label),
                        celsius: Metric::measured(celsius, prov),
                    });
                }
            }
        }
    }

    let max_temp = sensors.iter().fold(None::<f64>, |acc, s| {
        if let MetricReading::Measured { value, .. } = s.celsius.reading {
            Some(acc.map_or(value, |cur| cur.max(value)))
        } else {
            acc
        }
    });
    snap.hardware_telemetry.thermals.sensors = sensors;
    snap.hardware_telemetry.thermals.max_die_celsius = match max_temp {
        Some(t) => Metric::measured(t, prov),
        None => Metric::unavailable(UnavailableReason::Unsupported, prov),
    };
}

#[cfg(target_os = "linux")]
fn read_linux_battery_watts() -> Option<f64> {
    let entries = std::fs::read_dir("/sys/class/power_supply").ok()?;
    for entry in entries.flatten() {
        let fname = entry.file_name();
        let Some(name) = fname.to_str() else {
            continue;
        };
        if !name.starts_with("BAT") {
            continue;
        }
        let dir = entry.path();
        if let Ok(pw_txt) = std::fs::read_to_string(dir.join("power_now"))
            && let Ok(uw) = pw_txt.trim().parse::<f64>()
            && uw > 0.0
        {
            return Some(uw / 1_000_000.0);
        }
        if let (Ok(cur_txt), Ok(volt_txt)) = (
            std::fs::read_to_string(dir.join("current_now")),
            std::fs::read_to_string(dir.join("voltage_now")),
        ) && let (Ok(ua), Ok(uv)) = (
            cur_txt.trim().parse::<f64>(),
            volt_txt.trim().parse::<f64>(),
        ) {
            let w = (ua * uv) / 1e12;
            if w > 0.0 {
                return Some(w);
            }
        }
    }
    None
}

fn parse_proc_hex_addr_port(hex_addr_port: &str, is_ipv6: bool) -> Option<(String, u16)> {
    let (addr_hex, port_hex) = hex_addr_port.split_once(':')?;
    let port = u16::from_str_radix(port_hex, 16).ok()?;
    if is_ipv6 {
        if addr_hex.len() != 32 || !addr_hex.is_ascii() {
            return None;
        }
        let mut bytes = [0u8; 16];
        for g in 0..4 {
            for b in 0..4 {
                let start = g * 8 + (3 - b) * 2;
                bytes[g * 4 + b] = u8::from_str_radix(&addr_hex[start..start + 2], 16).ok()?;
            }
        }
        Some((Ipv6Addr::from(bytes).to_string(), port))
    } else {
        if addr_hex.len() != 8 {
            return None;
        }
        let raw = u32::from_str_radix(addr_hex, 16).ok()?;
        let ip = Ipv4Addr::new(
            (raw & 0xFF) as u8,
            ((raw >> 8) & 0xFF) as u8,
            ((raw >> 16) & 0xFF) as u8,
            ((raw >> 24) & 0xFF) as u8,
        );
        Some((ip.to_string(), port))
    }
}

fn parse_proc_tcp_hex_state(hex: &str) -> TcpState {
    match u8::from_str_radix(hex, 16).unwrap_or(0) {
        0x01 => TcpState::Established,
        0x02 => TcpState::SynSent,
        0x03 => TcpState::SynReceived,
        0x04 => TcpState::FinWait1,
        0x05 => TcpState::FinWait2,
        0x06 => TcpState::TimeWait,
        0x07 => TcpState::Closed,
        0x08 => TcpState::CloseWait,
        0x09 => TcpState::LastAck,
        0x0A => TcpState::Listen,
        0x0B => TcpState::Closing,
        _ => TcpState::Unknown,
    }
}

fn freq_mhz_to_band_channel(freq: u32) -> (WifiBand, u32) {
    if (2412..=2484).contains(&freq) {
        let ch = if freq == 2484 { 14 } else { (freq - 2407) / 5 };
        (WifiBand::Band2_4GHz, ch)
    } else if (5160..=5885).contains(&freq) {
        (WifiBand::Band5GHz, (freq - 5000) / 5)
    } else if (5955..=7115).contains(&freq) {
        (WifiBand::Band6GHz, (freq - 5950) / 5)
    } else {
        (WifiBand::Unknown, 0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::ValueState;

    #[test]
    fn linux_proc_stat_and_meminfo_parsers_preserve_provenance() {
        let l1 = "cpu  1000 100 300 8600 0 0 0 0 0 0";
        let l2 = "cpu  1250 100 400 8850 0 0 0 0 0 0";
        let (tot, usr, sys) = LinuxCollector::parse_proc_stat_delta(
            l1,
            l2,
            2_000_000_000,
            1_000_000_000,
            SourceId(1),
        );
        assert_eq!(tot.state(), ValueState::Valid);
        assert_eq!(usr.state(), ValueState::Valid);
        assert_eq!(sys.state(), ValueState::Valid);

        let meminfo = "MemTotal:       32768000 kB\nMemFree:         8000000 kB\nMemAvailable:   16384000 kB\n";
        let (mt, mu, ma, mp) =
            LinuxCollector::parse_proc_meminfo(meminfo, 2_000_000_000, SourceId(2));
        assert_eq!(mt.state(), ValueState::Valid);
        assert_eq!(mu.state(), ValueState::Valid);
        assert_eq!(ma.state(), ValueState::Valid);
        assert_eq!(mp.format_with_unit(1, "%"), "50.0%");
    }

    #[test]
    fn linux_access_denied_proc_net_systemd_xdg_and_wifi_parsers_work() {
        let (r, w) = LinuxCollector::build_process_io_metrics(
            Err(ErrorKind::PermissionDenied),
            1_000_000,
            SourceId(1),
        );
        assert_eq!(r.state(), ValueState::AccessDenied);
        assert_eq!(w.state(), ValueState::AccessDenied);

        let restricted = LinuxCollector::make_restricted_process_snapshot(
            ProcessKey {
                pid: 1,
                creation_token_us: 10,
                uid: Some(0),
            },
            0,
            CompactString::const_new("systemd"),
            1_000_000,
            SourceId(1),
        );
        assert_eq!(restricted.cpu_percent.state(), ValueState::AccessDenied);

        let proc_tcp = "\
  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
   0: 0100007F:23DE 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 55123 1 00000000 100 0 0 10 0
";
        let mut inodes = HashMap::new();
        inodes.insert(55123u64, (4242, CompactString::const_new("nexus")));
        let conns =
            LinuxCollector::parse_proc_net_table(proc_tcp, ConnectionProtocol::Tcp4, &inodes);
        assert_eq!(conns.len(), 1);
        assert_eq!(conns[0].local_address.as_str(), "127.0.0.1");
        assert_eq!(conns[0].local_port, 9182);
        assert_eq!(conns[0].state, TcpState::Listen);
        assert_eq!(conns[0].pid, Some(4242));

        let systemd = "docker.service loaded active running Docker Application Container Engine\n";
        let svcs = LinuxCollector::parse_systemd_units(systemd);
        assert_eq!(svcs.len(), 1);
        assert_eq!(svcs[0].state, ServiceState::Running);

        let xdg = LinuxCollector::parse_xdg_autostart_desktop(
            "nexus.desktop",
            "/home/user/.config/autostart/nexus.desktop",
            "[Desktop Entry]\nName=Nexus\nExec=/usr/bin/nexus\nHidden=false\n",
            true,
        );
        assert!(xdg.is_enabled);
        assert_eq!(xdg.command.as_str(), "/usr/bin/nexus");

        let nmcli = "*:HomeLabNet:AA\\:BB\\:CC\\:DD\\:EE\\:FF:36:84:WPA2\n";
        let wifi = LinuxCollector::parse_linux_nmcli_wifi(nmcli, 1_000, SourceId(1));
        assert_eq!(wifi.networks.len(), 1);
        assert!(wifi.networks[0].is_connected);
        assert_eq!(wifi.networks[0].band, WifiBand::Band5GHz);

        let iw = "BSS aa:bb:cc:dd:ee:ff(on wlan0)\n\tfreq: 5180\n\tsignal: -48.00 dBm\n\tSSID: Studio5G\n";
        let iw_rep = LinuxCollector::parse_linux_iw_scan(iw, 1_000, SourceId(1));
        assert_eq!(iw_rep.networks.len(), 1);
        assert_eq!(iw_rep.networks[0].channel, 36);
        assert_eq!(
            iw_rep.networks[0].rssi_dbm.format_with_unit(0, "dBm"),
            "-48 dBm"
        );
    }

    #[test]
    fn linux_pid_stat_diskstats_net_dev_and_thermal_parsers_work() {
        let stat_line = "1234 (Web Content (1)) S 1000 1234 1234 0 -1 4194304 100 0 0 0 50 25 0 0 20 0 4 0 987654 104857600 2560";
        let pid_stat = parse_proc_pid_stat(stat_line, 4096).expect("parse pid stat");
        assert_eq!(pid_stat.pid, 1234);
        assert_eq!(pid_stat.comm.as_str(), "Web Content (1)");
        assert_eq!(pid_stat.state, 'S');
        assert_eq!(pid_stat.ppid, 1000);
        assert_eq!(pid_stat.utime_ticks, 50);
        assert_eq!(pid_stat.stime_ticks, 25);
        assert_eq!(pid_stat.num_threads, 4);
        assert_eq!(pid_stat.starttime_ticks, 987654);
        assert_eq!(pid_stat.rss_bytes, 2560 * 4096);

        let diskstats = "\
   7       0 loop0 10 0 20 0 0 0 0 0 0 0 0
 259       0 nvme0n1 100 0 2000 10 50 0 1000 5 0 10 15
";
        let disks = parse_proc_diskstats(diskstats);
        assert_eq!(disks.len(), 1);
        assert_eq!(disks[0].0.as_str(), "nvme0n1");
        assert_eq!(disks[0].2, 2000 * 512);
        assert_eq!(disks[0].4, 1000 * 512);

        let net_dev = "\
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:    1000      10    0    0    0     0          0         0     1000      10    0    0    0     0       0          0
  eth0: 1048576     500    0    0    0     0          0         0   524288     250    0    0    0     0       0          0
";
        let nets = parse_proc_net_dev(net_dev);
        assert_eq!(nets.len(), 1);
        assert_eq!(nets[0].0.as_str(), "eth0");
        assert_eq!(nets[0].1, 1_048_576);
        assert_eq!(nets[0].2, 500);
        assert_eq!(nets[0].3, 524_288);
        assert_eq!(nets[0].4, 250);

        assert_eq!(parse_thermal_millidegrees("45250\n"), Some(45.25));
        assert_eq!(parse_thermal_millidegrees("not-a-num"), None);
    }
}
