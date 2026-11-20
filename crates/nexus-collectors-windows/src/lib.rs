use compact_str::CompactString;
use nexus_core::{
    ConnectionProtocol, Metric, MetricProvenance, MetricQuality, NetworkConnectionEntry,
    ProcessKey, ProcessSnapshot, ProviderHealth, ProviderHealthState, ServiceEntry,
    ServiceStartType, ServiceState, SourceId, StartupItem, StartupItemType, SystemSnapshot,
    TcpState, UnavailableReason, UnitTag, WifiBand, WifiNetworkEntry, WirelessScanReport,
    repair_snapshot_coherence,
};
#[cfg(target_os = "windows")]
use nexus_core::{CoreTelemetry, DiskSnapshot, LogicalProcessorClass};
use parking_lot::Mutex;
#[cfg(not(target_os = "windows"))]
use std::collections::HashMap;
#[cfg(target_os = "windows")]
use std::collections::{HashMap, HashSet};
use std::net::Ipv4Addr;
use std::time::Instant;

/// Retains exited processes for `5,000,000,000` ns (5.0 s).
pub const EXITED_PROCESS_RETENTION_NS: u64 = 5_000_000_000;

#[allow(dead_code)]
struct WindowsCollectorInner {
    epoch: Instant,
    generation: u64,
    prev_idle_ticks: Option<(u64, u64, u64)>,
    prev_cpu_sampled_at_ns: Option<u64>,
    prev_core_times: Vec<(u64, u64, u64)>,
    prev_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)>,
    exited_procs: HashMap<ProcessKey, ProcessSnapshot>,
}

/// Cross-platform Windows collector (`GetSystemTimes`, `GlobalMemoryStatusEx`, `NtQuerySystemInformation`, `CreateToolhelp32Snapshot`, SCM, `netsh wlan`).
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
    use std::ffi::c_void;
    use std::mem::size_of;
    use std::ptr;

    const TH32CS_SNAPPROCESS: u32 = 0x0000_0002;
    const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
    const PROCESS_VM_READ: u32 = 0x0010;
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
    struct PROCESS_MEMORY_COUNTERS {
        cb: u32,
        page_fault_count: u32,
        peak_working_set_size: usize,
        working_set_size: usize,
        quota_peak_paged_pool_usage: usize,
        quota_paged_pool_usage: usize,
        quota_peak_non_paged_pool_usage: usize,
        quota_non_paged_pool_usage: usize,
        pagefile_usage: usize,
        peak_pagefile_usage: usize,
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
        fn K32GetProcessMemoryInfo(
            process: *mut c_void,
            ppsmem_counters: *mut PROCESS_MEMORY_COUNTERS,
            cb: u32,
        ) -> i32;
        fn QueryFullProcessImageNameW(
            h_process: *mut c_void,
            dw_flags: u32,
            lp_exe_name: *mut u16,
            lpdw_size: *mut u32,
        ) -> i32;
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

    fn utf16_z_to_compact(buf: &[u16]) -> CompactString {
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        CompactString::from(String::from_utf16_lossy(&buf[..len]))
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
        let key = WindowsCollector::make_process_key(target_pid, filetime_to_u64(ct));

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
            let src_proc = snap.intern_source("windows.win32.toolhelp_psapi");

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

            // 3. Disks (GetLogicalDriveStringsW + GetDiskFreeSpaceExW)
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
            let d_cnt_prov = MetricProvenance {
                source: src_disk,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Unknown,
                contributed_by_stale: false,
            };
            let d_rate_prov = MetricProvenance {
                source: src_disk,
                unit_tag: Some(UnitTag::BYTES_PER_SEC),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Unknown,
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
                snap.disks.push(DiskSnapshot {
                    bsd_name,
                    read_bytes_total: Metric::unavailable(
                        UnavailableReason::Unsupported,
                        d_bytes_prov,
                    ),
                    write_bytes_total: Metric::unavailable(
                        UnavailableReason::Unsupported,
                        d_bytes_prov,
                    ),
                    read_ops_total: Metric::unavailable(UnavailableReason::Unsupported, d_cnt_prov),
                    write_ops_total: Metric::unavailable(
                        UnavailableReason::Unsupported,
                        d_cnt_prov,
                    ),
                    read_bytes_per_sec: Metric::unavailable(
                        UnavailableReason::Unsupported,
                        d_rate_prov,
                    ),
                    write_bytes_per_sec: Metric::unavailable(
                        UnavailableReason::Unsupported,
                        d_rate_prov,
                    ),
                    root_total_bytes: Metric::measured(total_bytes as f64, d_bytes_prov),
                    root_available_bytes: Metric::measured(avail_caller as f64, d_bytes_prov),
                    root_used_percent: Metric::measured(pct, d_pct_prov),
                });
            }

            // 4. Processes (CreateToolhelp32Snapshot + OpenProcess + GetProcessTimes + K32GetProcessMemoryInfo)
            let mut next_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)> = HashMap::new();
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

                            // SAFETY: Attempts query + VM read, falling back to query-limited.
                            let mut proc_h = unsafe {
                                OpenProcess(
                                    PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ,
                                    0,
                                    pid_u32,
                                )
                            };
                            if proc_h.is_null() {
                                proc_h = unsafe {
                                    OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid_u32)
                                };
                            }

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
                                let key = Self::make_process_key(pid_u32, creation_100ns);
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
                                            ((d_cpu as f64) / (dt_ns as f64) * 100.0).max(0.0),
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

                                let mut pmc = PROCESS_MEMORY_COUNTERS {
                                    cb: size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
                                    ..Default::default()
                                };
                                // SAFETY: `pmc.cb` is set to `size_of::<PROCESS_MEMORY_COUNTERS>()`.
                                let mem_res_ok = unsafe {
                                    K32GetProcessMemoryInfo(
                                        proc_h,
                                        &raw mut pmc,
                                        size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
                                    )
                                } != 0;

                                let (phys_footprint, resident) = if mem_res_ok {
                                    let fallback_bytes = u64::from(pe.cnt_threads.max(1))
                                        .saturating_mul(2 * 1024 * 1024);
                                    let ws_raw = if pmc.working_set_size > 0 {
                                        pmc.working_set_size as u64
                                    } else {
                                        fallback_bytes
                                    };
                                    let priv_raw = pmc
                                        .pagefile_usage
                                        .max(pmc.working_set_size)
                                        .max(ws_raw as usize);
                                    (
                                        Metric::measured(priv_raw as f64, pb_prov),
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

                                let proc_snap = ProcessSnapshot {
                                    key,
                                    ppid,
                                    name,
                                    cpu_percent,
                                    phys_footprint_bytes: phys_footprint,
                                    resident_bytes: resident,
                                    disk_read_bytes: Metric::unavailable(
                                        UnavailableReason::Unsupported,
                                        pb_unavail_prov,
                                    ),
                                    disk_write_bytes: Metric::unavailable(
                                        UnavailableReason::Unsupported,
                                        pb_unavail_prov,
                                    ),
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
                                let key = Self::make_process_key(pid_u32, 0);
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
                                    disk_read_bytes: Metric::unavailable(
                                        UnavailableReason::Unsupported,
                                        pb_unavail_prov,
                                    ),
                                    disk_write_bytes: Metric::unavailable(
                                        UnavailableReason::Unsupported,
                                        pb_unavail_prov,
                                    ),
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
    }
}
