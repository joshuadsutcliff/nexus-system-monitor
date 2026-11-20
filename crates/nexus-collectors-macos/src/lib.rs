#[cfg(target_os = "macos")]
pub mod ffi;
pub mod ioreport;
pub mod system_ops;

/// Retains exited processes for `5,000,000,000` ns (5.0 s).
pub const EXITED_PROCESS_RETENTION_NS: u64 = 5_000_000_000;

#[cfg(target_os = "macos")]
mod macos_collector {
    use super::EXITED_PROCESS_RETENTION_NS;
    use crate::ffi::{
        CfGuard, IOConnectCallStructMethod, IOIteratorNext, IOPMGetThermalWarningLevel,
        IORegistryEntryCreateCFProperties, IOServiceClose, IOServiceGetMatchingService,
        IOServiceGetMatchingServices, IOServiceMatching, IOServiceOpen, IoObjectGuard,
        KERN_SUCCESS, SmcKeyData, dict_get_dict, dict_get_i64, dict_get_string, kIOMainPortDefault,
        mach_host_self, notify_cancel, notify_get_state, notify_register_check, sysctl_u32,
        sysctl_u64, vm_deallocate,
    };
    use crate::ioreport::IoReportSampler;
    use compact_str::CompactString;
    use core_foundation_sys::base::kCFAllocatorDefault;
    use core_foundation_sys::dictionary::{CFDictionaryRef, CFMutableDictionaryRef};
    use nexus_core::{
        CoreTelemetry, DiskSnapshot, LogicalProcessorClass, Metric, MetricProvenance,
        MetricQuality, MetricReading, NetworkSnapshot, ProcessKey, ProcessSnapshot, ProviderHealth,
        ProviderHealthState, SocPowerMetrics, SourceId, SystemSnapshot, ThermalMetrics,
        ThermalSensorReading, UnavailableReason, UnitTag, repair_snapshot_coherence,
    };
    use parking_lot::Mutex;
    use std::collections::HashMap;
    use std::ffi::{CStr, CString, c_char, c_int, c_void};
    use std::mem::size_of;
    use std::ptr;
    use std::time::Instant;

    #[derive(Debug, Clone, Copy)]
    struct CpuTicks {
        user: u32,
        system: u32,
        idle: u32,
        nice: u32,
    }

    #[derive(Debug, Clone, Copy)]
    struct PrevIoCounter {
        rx_or_read: u64,
        tx_or_write: u64,
        sampled_at_ns: u64,
    }

    struct CollectorInner {
        epoch: Instant,
        generation: u64,
        perf_cores: u32,
        eff_cores: u32,
        prev_core_ticks: Vec<CpuTicks>,
        prev_cpu_sampled_at_ns: Option<u64>,
        prev_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)>,
        exited_procs: HashMap<ProcessKey, ProcessSnapshot>,
        prev_disks: HashMap<CompactString, PrevIoCounter>,
        prev_nets: HashMap<CompactString, PrevIoCounter>,
        ioreport: Option<IoReportSampler>,
    }

    /// Live macOS (Apple Silicon / Intel) telemetry collector pipeline.
    pub struct MacCollector {
        inner: Mutex<CollectorInner>,
    }

    impl Default for MacCollector {
        fn default() -> Self {
            Self::new()
        }
    }

    impl MacCollector {
        pub fn new() -> Self {
            let perf_cores = sysctl_u32("hw.perflevel0.physicalcpu")
                .or_else(|| sysctl_u32("hw.perflevel0.logicalcpu"))
                .unwrap_or(0);
            let eff_cores = sysctl_u32("hw.perflevel1.physicalcpu")
                .or_else(|| sysctl_u32("hw.perflevel1.logicalcpu"))
                .unwrap_or(0);
            let ioreport = IoReportSampler::try_new();

            Self {
                inner: Mutex::new(CollectorInner {
                    epoch: Instant::now(),
                    generation: 0,
                    perf_cores,
                    eff_cores,
                    prev_core_ticks: Vec::new(),
                    prev_cpu_sampled_at_ns: None,
                    prev_procs: HashMap::new(),
                    exited_procs: HashMap::new(),
                    prev_disks: HashMap::new(),
                    prev_nets: HashMap::new(),
                    ioreport,
                }),
            }
        }

        /// Executes a full collection pass, repairs snapshot coherence (`RepairSnapshotCoherence`),
        /// and returns a coherent `SystemSnapshot`.
        pub fn collect(&self) -> SystemSnapshot {
            let mut guard = self.inner.lock();
            guard.generation += 1;
            let generation = guard.generation;
            let sampled_at_ns = (guard.epoch.elapsed().as_nanos() as u64).max(1);

            let mut snapshot = SystemSnapshot::new_empty(generation, sampled_at_ns);
            snapshot.system.performance_core_count = guard.perf_cores;
            snapshot.system.efficiency_core_count = guard.eff_cores;

            Self::collect_cpu(&mut guard, &mut snapshot, sampled_at_ns);
            Self::collect_memory(&mut snapshot, sampled_at_ns);
            Self::collect_gpu(&mut snapshot, sampled_at_ns);
            Self::collect_power_and_thermals(&mut guard, &mut snapshot, sampled_at_ns);
            Self::collect_disks(&mut guard, &mut snapshot, sampled_at_ns);
            Self::collect_networks(&mut guard, &mut snapshot, sampled_at_ns);
            Self::collect_processes(&mut guard, &mut snapshot, sampled_at_ns);

            let _ = repair_snapshot_coherence(&mut snapshot);
            snapshot
        }

        /// Reads a `ProcessKey(pid, creation_token_us, uid)` via `proc_pidinfo(PROC_PIDTBSDINFO)` (`0x1005a8e70`).
        pub fn read_process_key(pid: i32) -> Option<(ProcessKey, i32, CompactString)> {
            let mut bsd: libc::proc_bsdinfo = unsafe { std::mem::zeroed() };
            let expected_size = size_of::<libc::proc_bsdinfo>() as c_int;
            // SAFETY: `bsd` is a valid writable `proc_bsdinfo` struct of size `expected_size`.
            let ret = unsafe {
                libc::proc_pidinfo(
                    pid,
                    libc::PROC_PIDTBSDINFO,
                    0,
                    (&raw mut bsd).cast::<c_void>(),
                    expected_size,
                )
            };
            if ret != expected_size {
                return None;
            }
            let creation_token_us =
                (bsd.pbi_start_tvsec).saturating_mul(1_000_000) + bsd.pbi_start_tvusec;
            let name_ptr = if bsd.pbi_name[0] != 0 {
                bsd.pbi_name.as_ptr()
            } else {
                bsd.pbi_comm.as_ptr()
            };
            // SAFETY: `pbi_name` / `pbi_comm` are NUL-terminated char arrays in `proc_bsdinfo`.
            let raw_name = unsafe { CStr::from_ptr(name_ptr) }.to_string_lossy();
            let name = if raw_name.is_empty() && pid == 0 {
                CompactString::const_new("kernel_task")
            } else {
                CompactString::from(raw_name.as_ref())
            };
            Some((
                ProcessKey {
                    pid,
                    creation_token_us,
                    uid: Some(bsd.pbi_uid),
                },
                bsd.pbi_ppid as i32,
                name,
            ))
        }

        fn collect_cpu(
            state: &mut CollectorInner,
            snapshot: &mut SystemSnapshot,
            sampled_at_ns: u64,
        ) {
            let src_mach_cpu = snapshot.intern_source("macos.cpu.host_processor_info");
            let src_sysctl = snapshot.intern_source("macos.cpu.sysctl");

            let mut cpu_count: libc::natural_t = 0;
            let mut info_array: *mut libc::integer_t = ptr::null_mut();
            let mut msg_count: libc::mach_msg_type_number_t = 0;

            // SAFETY: `mach_host_self()` returns the host port and `host_processor_info` allocates a VM buffer
            // that we immediately copy and release via `vm_deallocate`.
            let kr = unsafe {
                libc::host_processor_info(
                    mach_host_self(),
                    libc::PROCESSOR_CPU_LOAD_INFO,
                    &raw mut cpu_count,
                    &raw mut info_array,
                    &raw mut msg_count,
                )
            };

            let window_ns = state
                .prev_cpu_sampled_at_ns
                .map(|prev| sampled_at_ns.saturating_sub(prev))
                .unwrap_or(0);
            let pct_prov = MetricProvenance {
                source: src_mach_cpu,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            if kr == KERN_SUCCESS && !info_array.is_null() && cpu_count > 0 {
                let count = cpu_count as usize;
                let stride = libc::CPU_STATE_MAX as usize;
                let mut cur_ticks = Vec::with_capacity(count);

                // SAFETY: `info_array` contains `msg_count` integers (`count * CPU_STATE_MAX`).
                unsafe {
                    let slice = std::slice::from_raw_parts(info_array, msg_count as usize);
                    for core_idx in 0..count {
                        let base = core_idx * stride;
                        if base + 3 < slice.len() {
                            cur_ticks.push(CpuTicks {
                                user: slice[base + libc::CPU_STATE_USER as usize] as u32,
                                system: slice[base + libc::CPU_STATE_SYSTEM as usize] as u32,
                                idle: slice[base + libc::CPU_STATE_IDLE as usize] as u32,
                                nice: slice[base + libc::CPU_STATE_NICE as usize] as u32,
                            });
                        }
                    }
                    let dealloc_bytes = (msg_count as usize) * size_of::<libc::integer_t>();
                    let _ = vm_deallocate(
                        mach2::traps::mach_task_self(),
                        info_array as usize,
                        dealloc_bytes,
                    );
                }

                let has_prev = state.prev_core_ticks.len() == cur_ticks.len();
                let mut total_user_delta = 0u64;
                let mut total_sys_delta = 0u64;
                let mut total_idle_delta = 0u64;
                let mut total_nice_delta = 0u64;
                let mut any_regressed = false;

                for (idx, cur) in cur_ticks.iter().enumerate() {
                    let class = if state.eff_cores > 0 && (idx as u32) < state.eff_cores {
                        LogicalProcessorClass::Efficiency
                    } else if state.perf_cores > 0 {
                        LogicalProcessorClass::Performance
                    } else {
                        LogicalProcessorClass::Unknown
                    };

                    if has_prev {
                        let prev = state.prev_core_ticks[idx];
                        if cur.user < prev.user
                            || cur.system < prev.system
                            || cur.idle < prev.idle
                            || cur.nice < prev.nice
                        {
                            any_regressed = true;
                            snapshot.system.cores.push(CoreTelemetry {
                                core_index: idx as u32,
                                class,
                                total_percent: Metric::unavailable(
                                    UnavailableReason::NotYetAvailable,
                                    pct_prov,
                                ),
                                user_percent: Metric::unavailable(
                                    UnavailableReason::NotYetAvailable,
                                    pct_prov,
                                ),
                                system_percent: Metric::unavailable(
                                    UnavailableReason::NotYetAvailable,
                                    pct_prov,
                                ),
                                idle_percent: Metric::unavailable(
                                    UnavailableReason::NotYetAvailable,
                                    pct_prov,
                                ),
                            });
                        } else {
                            let du = (cur.user - prev.user) as u64;
                            let ds = (cur.system - prev.system) as u64;
                            let di = (cur.idle - prev.idle) as u64;
                            let dn = (cur.nice - prev.nice) as u64;
                            let dtotal = du + ds + di + dn;

                            total_user_delta += du;
                            total_sys_delta += ds;
                            total_idle_delta += di;
                            total_nice_delta += dn;

                            if dtotal > 0 {
                                let denom = dtotal as f64;
                                let user_p = ((du + dn) as f64) / denom * 100.0;
                                let sys_p = (ds as f64) / denom * 100.0;
                                let idle_p = (di as f64) / denom * 100.0;
                                let tot_p = ((du + ds + dn) as f64) / denom * 100.0;
                                snapshot.system.cores.push(CoreTelemetry {
                                    core_index: idx as u32,
                                    class,
                                    total_percent: Metric::measured(tot_p, pct_prov),
                                    user_percent: Metric::measured(user_p, pct_prov),
                                    system_percent: Metric::measured(sys_p, pct_prov),
                                    idle_percent: Metric::measured(idle_p, pct_prov),
                                });
                            } else {
                                snapshot.system.cores.push(CoreTelemetry {
                                    core_index: idx as u32,
                                    class,
                                    total_percent: Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        pct_prov,
                                    ),
                                    user_percent: Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        pct_prov,
                                    ),
                                    system_percent: Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        pct_prov,
                                    ),
                                    idle_percent: Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        pct_prov,
                                    ),
                                });
                            }
                        }
                    } else {
                        snapshot.system.cores.push(CoreTelemetry {
                            core_index: idx as u32,
                            class,
                            total_percent: Metric::unavailable(
                                UnavailableReason::NotYetAvailable,
                                pct_prov,
                            ),
                            user_percent: Metric::unavailable(
                                UnavailableReason::NotYetAvailable,
                                pct_prov,
                            ),
                            system_percent: Metric::unavailable(
                                UnavailableReason::NotYetAvailable,
                                pct_prov,
                            ),
                            idle_percent: Metric::unavailable(
                                UnavailableReason::NotYetAvailable,
                                pct_prov,
                            ),
                        });
                    }
                }

                let d_all =
                    total_user_delta + total_sys_delta + total_idle_delta + total_nice_delta;
                if has_prev && !any_regressed && d_all > 0 {
                    let denom = d_all as f64;
                    let user_pct = ((total_user_delta + total_nice_delta) as f64) / denom * 100.0;
                    let sys_pct = (total_sys_delta as f64) / denom * 100.0;
                    let total_pct =
                        ((total_user_delta + total_sys_delta + total_nice_delta) as f64) / denom
                            * 100.0;
                    snapshot.system.cpu_percent = Metric::measured(total_pct, pct_prov);
                    snapshot.system.cpu_user_percent = Metric::measured(user_pct, pct_prov);
                    snapshot.system.cpu_kernel_percent = Metric::measured(sys_pct, pct_prov);
                } else {
                    snapshot.system.cpu_percent =
                        Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                    snapshot.system.cpu_user_percent =
                        Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                    snapshot.system.cpu_kernel_percent =
                        Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov);
                }

                state.prev_core_ticks = cur_ticks;
                state.prev_cpu_sampled_at_ns = Some(sampled_at_ns);
                snapshot.provider_health.push(ProviderHealth {
                    id: CompactString::const_new("macos.cpu"),
                    state: ProviderHealthState::Healthy,
                    consecutive_failures: 0,
                    last_success_at_ns: sampled_at_ns,
                    last_error: None,
                });
            } else {
                snapshot.system.cpu_percent =
                    Metric::unavailable(UnavailableReason::TransientError, pct_prov);
                snapshot.system.cpu_user_percent =
                    Metric::unavailable(UnavailableReason::TransientError, pct_prov);
                snapshot.system.cpu_kernel_percent =
                    Metric::unavailable(UnavailableReason::TransientError, pct_prov);
                snapshot.provider_health.push(ProviderHealth {
                    id: CompactString::const_new("macos.cpu"),
                    state: ProviderHealthState::Unavailable,
                    consecutive_failures: 1,
                    last_success_at_ns: 0,
                    last_error: Some(CompactString::from(format!("host_processor_info kr={kr}"))),
                });
            }

            let mut loadavg = [0.0_f64; 3];
            // SAFETY: `loadavg` has 3 elements for `getloadavg`.
            let n = unsafe { libc::getloadavg(loadavg.as_mut_ptr(), 3) };
            let load_prov = MetricProvenance {
                source: src_sysctl,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            if n == 3 {
                snapshot.system.load_avg_1m = Metric::measured(loadavg[0], load_prov);
                snapshot.system.load_avg_5m = Metric::measured(loadavg[1], load_prov);
                snapshot.system.load_avg_15m = Metric::measured(loadavg[2], load_prov);
            }

            let mut boottime = libc::timeval {
                tv_sec: 0,
                tv_usec: 0,
            };
            let mut bt_size = size_of::<libc::timeval>();
            let mut mib = [libc::CTL_KERN, libc::KERN_BOOTTIME];
            // SAFETY: `mib` has 2 elements and `boottime` has `bt_size` bytes.
            let bt_rc = unsafe {
                libc::sysctl(
                    mib.as_mut_ptr(),
                    2,
                    (&raw mut boottime).cast::<c_void>(),
                    &raw mut bt_size,
                    ptr::null_mut(),
                    0,
                )
            };
            let up_prov = MetricProvenance {
                source: src_sysctl,
                unit_tag: Some(UnitTag::SECONDS),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            if bt_rc == 0 && boottime.tv_sec > 0 {
                let mut now_tv = libc::timeval {
                    tv_sec: 0,
                    tv_usec: 0,
                };
                // SAFETY: `gettimeofday` populates `now_tv`.
                unsafe {
                    let _ = libc::gettimeofday(&raw mut now_tv, ptr::null_mut());
                }
                let up = (now_tv.tv_sec - boottime.tv_sec).max(0) as f64;
                snapshot.system.uptime_seconds = Metric::measured(up, up_prov);
            }
        }

        fn collect_memory(snapshot: &mut SystemSnapshot, sampled_at_ns: u64) {
            let src_vm = snapshot.intern_source("macos.memory.host_statistics64");
            let src_sysctl = snapshot.intern_source("macos.memory.sysctl");

            let bytes_prov = |src: SourceId| MetricProvenance {
                source: src,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let pct_prov = |src: SourceId| MetricProvenance {
                source: src,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            // SAFETY: `sysconf(_SC_PAGESIZE)` is safe and returns the OS page size in bytes.
            let page_size = unsafe { libc::sysconf(libc::_SC_PAGESIZE) }.max(4096) as u64;
            let total_mem = sysctl_u64("hw.memsize").unwrap_or(0);

            let mut vm_stat: libc::vm_statistics64 = unsafe { std::mem::zeroed() };
            let mut count = libc::HOST_VM_INFO64_COUNT;
            // SAFETY: `vm_stat` and `count` match `libc::vm_statistics64` and `HOST_VM_INFO64_COUNT`.
            let kr = unsafe {
                libc::host_statistics64(
                    mach_host_self(),
                    libc::HOST_VM_INFO64,
                    (&raw mut vm_stat).cast::<libc::integer_t>(),
                    &raw mut count,
                )
            };

            if kr == KERN_SUCCESS && total_mem > 0 {
                let active = (vm_stat.active_count as u64).saturating_mul(page_size);
                let wired = (vm_stat.wire_count as u64).saturating_mul(page_size);
                let compressed = (vm_stat.compressor_page_count as u64).saturating_mul(page_size);
                let cached = ((vm_stat.external_page_count as u64)
                    + (vm_stat.purgeable_count as u64))
                    .saturating_mul(page_size);
                let free_raw = ((vm_stat.free_count as u64)
                    .saturating_sub(vm_stat.speculative_count as u64))
                .saturating_mul(page_size);
                let app_mem = ((vm_stat.internal_page_count as u64)
                    .saturating_sub(vm_stat.purgeable_count as u64))
                .saturating_mul(page_size);
                let used = app_mem
                    .saturating_add(wired)
                    .saturating_add(compressed)
                    .min(total_mem);
                let used_pct = ((used as f64) / (total_mem as f64) * 100.0).clamp(0.0, 100.0);

                let bp = bytes_prov(src_vm);
                snapshot.system.mem_total_bytes =
                    Metric::measured(total_mem as f64, bytes_prov(src_sysctl));
                snapshot.system.mem_used_bytes = Metric::measured(used as f64, bp);
                snapshot.system.mem_active_bytes = Metric::measured(active as f64, bp);
                snapshot.system.mem_wired_bytes = Metric::measured(wired as f64, bp);
                snapshot.system.mem_compressed_bytes = Metric::measured(compressed as f64, bp);
                snapshot.system.mem_cached_files_bytes = Metric::measured(cached as f64, bp);
                snapshot.system.mem_free_bytes = Metric::measured(free_raw as f64, bp);
                snapshot.system.mem_used_percent = Metric::measured(used_pct, pct_prov(src_vm));

                snapshot.provider_health.push(ProviderHealth {
                    id: CompactString::const_new("macos.memory"),
                    state: ProviderHealthState::Healthy,
                    consecutive_failures: 0,
                    last_success_at_ns: sampled_at_ns,
                    last_error: None,
                });
            } else {
                snapshot.provider_health.push(ProviderHealth {
                    id: CompactString::const_new("macos.memory"),
                    state: ProviderHealthState::Unavailable,
                    consecutive_failures: 1,
                    last_success_at_ns: 0,
                    last_error: Some(CompactString::from(format!("host_statistics64 kr={kr}"))),
                });
            }

            if let Ok(cname) = CString::new("vm.swapusage") {
                let mut swap: libc::xsw_usage = unsafe { std::mem::zeroed() };
                let mut sz = size_of::<libc::xsw_usage>();
                // SAFETY: `swap` is a valid `xsw_usage` struct of size `sz`.
                let rc = unsafe {
                    libc::sysctlbyname(
                        cname.as_ptr(),
                        (&raw mut swap).cast::<c_void>(),
                        &raw mut sz,
                        ptr::null_mut(),
                        0,
                    )
                };
                let sp = bytes_prov(src_sysctl);
                if rc == 0 && sz == size_of::<libc::xsw_usage>() {
                    snapshot.system.swap_total_bytes = Metric::measured(swap.xsu_total as f64, sp);
                    snapshot.system.swap_used_bytes = Metric::measured(swap.xsu_used as f64, sp);
                    let swap_pct = if swap.xsu_total > 0 {
                        ((swap.xsu_used as f64) / (swap.xsu_total as f64) * 100.0).clamp(0.0, 100.0)
                    } else {
                        0.0
                    };
                    snapshot.system.swap_used_percent =
                        Metric::measured(swap_pct, pct_prov(src_sysctl));
                }
            }

            if let Some(level) = sysctl_u32("kern.memorystatus_vm_pressure_level") {
                // 1 = NORMAL (10%), 2 = WARN (60%), 4 = CRITICAL (90%)
                let pct = match level {
                    1 => 10.0,
                    2 => 60.0,
                    4 => 90.0,
                    _ => 0.0,
                };
                snapshot.system.system_pressure_percent =
                    Metric::measured(pct, pct_prov(src_sysctl));
            }
        }

        fn collect_gpu(snapshot: &mut SystemSnapshot, sampled_at_ns: u64) {
            let src_gpu = snapshot.intern_source("macos.gpu.ioaccelerator");
            let pct_prov = MetricProvenance {
                source: src_gpu,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let bytes_prov = MetricProvenance {
                source: src_gpu,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let cnt_prov = MetricProvenance {
                source: src_gpu,
                unit_tag: Some(UnitTag::COUNT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            let Ok(cls) = CString::new("IOAccelerator") else {
                return;
            };
            let mut iter = 0;
            // SAFETY: `IOServiceMatching` returns a dictionary consumed by `IOServiceGetMatchingServices`.
            let kr = unsafe {
                let matching = IOServiceMatching(cls.as_ptr());
                if matching.is_null() {
                    return;
                }
                IOServiceGetMatchingServices(kIOMainPortDefault, matching, &raw mut iter)
            };
            if kr != KERN_SUCCESS || iter == 0 {
                snapshot.system.gpu_utilization_percent =
                    Metric::unavailable(UnavailableReason::Unsupported, pct_prov);
                return;
            }
            let _iter_guard = IoObjectGuard(iter);

            let mut found = false;
            loop {
                // SAFETY: `iter` is a valid `io_iterator_t`.
                let entry = unsafe { IOIteratorNext(iter) };
                if entry == 0 {
                    break;
                }
                let _entry_guard = IoObjectGuard(entry);
                let mut props: CFMutableDictionaryRef = ptr::null_mut();
                // SAFETY: `entry` is a valid `io_registry_entry_t`.
                let pkr = unsafe {
                    IORegistryEntryCreateCFProperties(entry, &raw mut props, kCFAllocatorDefault, 0)
                };
                if pkr != KERN_SUCCESS || props.is_null() {
                    continue;
                }
                let _props_guard = CfGuard(props.cast());
                let dict = props as CFDictionaryRef;

                if let Some(model) = dict_get_string(dict, "model") {
                    snapshot.system.gpu_model = CompactString::from(model);
                }
                if let Some(cores) = dict_get_i64(dict, "gpu-core-count") {
                    snapshot.system.gpu_core_count = Metric::measured(cores as f64, cnt_prov);
                }
                if let Some(perf) = dict_get_dict(dict, "PerformanceStatistics") {
                    if let Some(util) = dict_get_i64(perf, "Device Utilization %")
                        .or_else(|| dict_get_i64(perf, "Renderer Utilization %"))
                    {
                        snapshot.system.gpu_utilization_percent =
                            Metric::measured((util as f64).clamp(0.0, 100.0), pct_prov);
                        found = true;
                    }
                    if let Some(in_use) = dict_get_i64(perf, "In use system memory") {
                        snapshot.system.gpu_in_use_memory_bytes =
                            Metric::measured(in_use.max(0) as f64, bytes_prov);
                    }
                    if let Some(alloc) = dict_get_i64(perf, "Alloc system memory") {
                        snapshot.system.gpu_alloc_memory_bytes =
                            Metric::measured(alloc.max(0) as f64, bytes_prov);
                    }
                    break;
                }
            }

            if !found {
                snapshot.system.gpu_utilization_percent =
                    Metric::unavailable(UnavailableReason::Unsupported, pct_prov);
            }
        }

        fn collect_power_and_thermals(
            state: &mut CollectorInner,
            snapshot: &mut SystemSnapshot,
            sampled_at_ns: u64,
        ) {
            let src_ioreport = snapshot.intern_source("macos.power.ioreport.energy_model");
            let src_power_telem = snapshot.intern_source("macos.power.ioreg.power_telemetry");
            let src_iopm = snapshot.intern_source("macos.thermal.iopm");
            let src_notify = snapshot.intern_source("macos.thermal.darwin_notify");
            let src_smc = snapshot.intern_source("macos.thermal.applesmc");

            // 1. Read AppleSmartBattery -> PowerTelemetryData (`SystemPowerIn`, `SystemLoad` in mW)
            let (sys_power_in_w, sys_load_w) = Self::read_power_telemetry_watts();

            // 2. Sample libIOReport Energy Model + CPU Stats
            let soc_power = if let Some(sampler) = &mut state.ioreport {
                sampler.sample_soc_power(
                    sampled_at_ns,
                    src_ioreport,
                    src_power_telem,
                    sys_power_in_w,
                    sys_load_w,
                )
            } else {
                SocPowerMetrics::empty_unavailable(sampled_at_ns)
            };
            snapshot.hardware_telemetry.soc_power = soc_power;

            // 3. Thermal warning level (`IOPMGetThermalWarningLevel` with `notify_get_state` fallback)
            let mut level: u32 = 0;
            // SAFETY: `level` is a valid writable `u32`.
            let kr = unsafe { IOPMGetThermalWarningLevel(&raw mut level) };
            let warning_level = if kr == KERN_SUCCESS {
                Metric::measured(
                    level as f64,
                    MetricProvenance {
                        source: src_iopm,
                        unit_tag: Some(UnitTag::COUNT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Authoritative,
                        contributed_by_stale: false,
                    },
                )
            } else if let Some(n_state) = Self::read_darwin_thermal_pressure() {
                Metric::measured(
                    n_state as f64,
                    MetricProvenance {
                        source: src_notify,
                        unit_tag: Some(UnitTag::COUNT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Fallback,
                        contributed_by_stale: false,
                    },
                )
            } else {
                Metric::unavailable(
                    UnavailableReason::Unsupported,
                    MetricProvenance {
                        source: src_iopm,
                        unit_tag: Some(UnitTag::COUNT),
                        sampled_at_ns,
                        window_ns: 0,
                        quality: MetricQuality::Unknown,
                        contributed_by_stale: false,
                    },
                )
            };

            // 4. AppleSMC thermal sensors (`0x1005db8c4`, selector 2)
            let sensors = Self::read_smc_thermal_sensors(sampled_at_ns, src_smc);
            let mut max_temp: Option<f64> = None;
            for s in &sensors {
                if let MetricReading::Measured { value, .. } = s.celsius.reading {
                    max_temp = Some(match max_temp {
                        None => value,
                        Some(cur) => cur.max(value),
                    });
                }
            }
            let temp_prov = MetricProvenance {
                source: src_smc,
                unit_tag: Some(UnitTag::CELSIUS),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let max_die_celsius = match max_temp {
                Some(t) => Metric::measured(t, temp_prov),
                None => Metric::unavailable(UnavailableReason::Unsupported, temp_prov),
            };

            snapshot.hardware_telemetry.thermals = ThermalMetrics {
                warning_level,
                max_die_celsius,
                sensors,
            };
        }

        fn read_power_telemetry_watts() -> (Option<f64>, Option<f64>) {
            let Ok(cls) = CString::new("AppleSmartBattery") else {
                return (None, None);
            };
            // SAFETY: `IOServiceMatching` is consumed by `IOServiceGetMatchingService`.
            let service = unsafe {
                let matching = IOServiceMatching(cls.as_ptr());
                if matching.is_null() {
                    return (None, None);
                }
                IOServiceGetMatchingService(kIOMainPortDefault, matching)
            };
            if service == 0 {
                return (None, None);
            }
            let _svc_guard = IoObjectGuard(service);
            let mut props: CFMutableDictionaryRef = ptr::null_mut();
            // SAFETY: `service` is a valid `io_registry_entry_t`.
            let kr = unsafe {
                IORegistryEntryCreateCFProperties(service, &raw mut props, kCFAllocatorDefault, 0)
            };
            if kr != KERN_SUCCESS || props.is_null() {
                return (None, None);
            }
            let _props_guard = CfGuard(props.cast());
            let dict = props as CFDictionaryRef;

            let Some(ptd) = dict_get_dict(dict, "PowerTelemetryData") else {
                return (None, None);
            };
            let sys_in_w = dict_get_i64(ptd, "SystemPowerIn")
                .filter(|&mw| mw >= 0)
                .map(|mw| (mw as f64) / 1000.0);
            let sys_load_w = dict_get_i64(ptd, "SystemLoad")
                .filter(|&mw| mw >= 0)
                .map(|mw| (mw as f64) / 1000.0);
            (sys_in_w, sys_load_w)
        }

        fn read_darwin_thermal_pressure() -> Option<u64> {
            let cname = CString::new("com.apple.system.thermalpressurelevel").ok()?;
            let mut token: c_int = 0;
            let mut state: u64 = 0;
            // SAFETY: `notify_register_check` registers a read token released immediately via `notify_cancel`.
            unsafe {
                if notify_register_check(cname.as_ptr(), &raw mut token) != 0 {
                    return None;
                }
                let rc = notify_get_state(token, &raw mut state);
                let _ = notify_cancel(token);
                if rc == 0 { Some(state) } else { None }
            }
        }

        fn read_smc_thermal_sensors(
            sampled_at_ns: u64,
            src_smc: SourceId,
        ) -> Vec<ThermalSensorReading> {
            let mut readings = Vec::new();
            let Ok(cls) = CString::new("AppleSMC") else {
                return readings;
            };
            // SAFETY: `IOServiceMatching` is consumed by `IOServiceGetMatchingService`.
            let service = unsafe {
                let matching = IOServiceMatching(cls.as_ptr());
                if matching.is_null() {
                    return readings;
                }
                IOServiceGetMatchingService(kIOMainPortDefault, matching)
            };
            if service == 0 {
                return readings;
            }
            let _svc_guard = IoObjectGuard(service);

            let mut conn: u32 = 0;
            // SAFETY: Opens user client type `0` on `AppleSMC` and closes it before returning.
            let kr =
                unsafe { IOServiceOpen(service, mach2::traps::mach_task_self(), 0, &raw mut conn) };
            if kr != KERN_SUCCESS || conn == 0 {
                return readings;
            }

            // Verified Apple Silicon SMC thermal keys (`0x1005db8c4` + M-series keys)
            const KEYS: &[(&str, [u8; 4])] = &[
                ("CPU Die 1 (Tp01)", *b"Tp01"),
                ("CPU Die 2 (Tp05)", *b"Tp05"),
                ("CPU Die 3 (Tp09)", *b"Tp09"),
                ("CPU Die 4 (Tp0D)", *b"Tp0D"),
                ("CPU Die 5 (Tp0H)", *b"Tp0H"),
                ("CPU Die 6 (Tp0L)", *b"Tp0L"),
                ("CPU Die 7 (Tp0P)", *b"Tp0P"),
                ("CPU Die 8 (Tp0T)", *b"Tp0T"),
                ("CPU Die 9 (Tp0X)", *b"Tp0X"),
                ("CPU Die 10 (Tp0b)", *b"Tp0b"),
                ("GPU Die (Tg0G)", *b"Tg0G"),
                ("SoC Package (Tp00)", *b"Tp00"),
                ("NAND (TH0x)", *b"TH0x"),
                ("Airflow (TaLP)", *b"TaLP"),
            ];

            let prov = MetricProvenance {
                source: src_smc,
                unit_tag: Some(UnitTag::CELSIUS),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            for &(label, fourcc) in KEYS {
                if let Some(celsius) = Self::read_single_smc_temp(conn, fourcc)
                    && (5.0..=130.0).contains(&celsius)
                {
                    readings.push(ThermalSensorReading {
                        name: CompactString::const_new(label),
                        celsius: Metric::measured(celsius, prov),
                    });
                }
            }

            // SAFETY: `conn` was opened above via `IOServiceOpen`.
            unsafe {
                let _ = IOServiceClose(conn);
            }
            readings
        }

        fn read_single_smc_temp(conn: u32, fourcc: [u8; 4]) -> Option<f64> {
            let key_u32 = u32::from_be_bytes(fourcc);
            let mut req = SmcKeyData {
                key: key_u32,
                data8: 9, // kSMCGetKeyInfo
                ..SmcKeyData::default()
            };
            let mut resp = SmcKeyData::default();
            let mut out_size = size_of::<SmcKeyData>();

            // SAFETY: `req` and `resp` are 80-byte `SmcKeyData` structs passed to selector `2`.
            let kr1 = unsafe {
                IOConnectCallStructMethod(
                    conn,
                    2,
                    (&raw const req).cast::<c_void>(),
                    size_of::<SmcKeyData>(),
                    (&raw mut resp).cast::<c_void>(),
                    &raw mut out_size,
                )
            };
            if kr1 != KERN_SUCCESS || resp.result != 0 || resp.key_info.data_size == 0 {
                return None;
            }

            let data_type = resp.key_info.data_type;
            let data_size = resp.key_info.data_size;
            req.key_info.data_size = data_size;
            req.data8 = 5; // kSMCReadKey
            resp = SmcKeyData::default();
            out_size = size_of::<SmcKeyData>();

            // SAFETY: `req` and `resp` are 80-byte `SmcKeyData` structs passed to selector `2`.
            let kr2 = unsafe {
                IOConnectCallStructMethod(
                    conn,
                    2,
                    (&raw const req).cast::<c_void>(),
                    size_of::<SmcKeyData>(),
                    (&raw mut resp).cast::<c_void>(),
                    &raw mut out_size,
                )
            };
            if kr2 != KERN_SUCCESS || resp.result != 0 {
                return None;
            }

            let flt_tag = u32::from_be_bytes(*b"flt ");
            let sp78_tag = u32::from_be_bytes(*b"sp78");
            if data_type == flt_tag && data_size == 4 {
                let val = f32::from_le_bytes([
                    resp.bytes[0],
                    resp.bytes[1],
                    resp.bytes[2],
                    resp.bytes[3],
                ]) as f64;
                if val.is_finite() { Some(val) } else { None }
            } else if data_type == sp78_tag && data_size == 2 {
                let raw = i16::from_be_bytes([resp.bytes[0], resp.bytes[1]]);
                Some((raw as f64) / 256.0)
            } else {
                None
            }
        }

        fn collect_disks(
            state: &mut CollectorInner,
            snapshot: &mut SystemSnapshot,
            sampled_at_ns: u64,
        ) {
            let src_disk = snapshot.intern_source("macos.disk.ioblockstoragedriver");
            let src_statfs = snapshot.intern_source("macos.disk.statfs");

            // Root filesystem capacity via `statfs("/")` (`0x1005b4c18`)
            let mut sfs: libc::statfs = unsafe { std::mem::zeroed() };
            let root_c = CString::new("/").unwrap();
            // SAFETY: `root_c` is `"/"` and `sfs` is a valid `libc::statfs` struct.
            let sfs_rc = unsafe { libc::statfs(root_c.as_ptr(), &raw mut sfs) };
            let sfs_bytes_prov = MetricProvenance {
                source: src_statfs,
                unit_tag: Some(UnitTag::BYTES),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let sfs_pct_prov = MetricProvenance {
                source: src_statfs,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns,
                window_ns: 0,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };

            let (root_total, root_avail, root_pct) = if sfs_rc == 0 && sfs.f_blocks > 0 {
                let bsize = sfs.f_bsize as u64;
                let total = sfs.f_blocks.saturating_mul(bsize);
                let avail = sfs.f_bavail.saturating_mul(bsize);
                let used = total.saturating_sub(avail);
                let pct = ((used as f64) / (total as f64) * 100.0).clamp(0.0, 100.0);
                (
                    Metric::measured(total as f64, sfs_bytes_prov),
                    Metric::measured(avail as f64, sfs_bytes_prov),
                    Metric::measured(pct, sfs_pct_prov),
                )
            } else {
                (
                    Metric::unavailable(UnavailableReason::TransientError, sfs_bytes_prov),
                    Metric::unavailable(UnavailableReason::TransientError, sfs_bytes_prov),
                    Metric::unavailable(UnavailableReason::TransientError, sfs_pct_prov),
                )
            };

            let Ok(cls) = CString::new("IOBlockStorageDriver") else {
                return;
            };
            let mut iter = 0;
            // SAFETY: `IOServiceMatching` is consumed by `IOServiceGetMatchingServices`.
            let kr = unsafe {
                let matching = IOServiceMatching(cls.as_ptr());
                if matching.is_null() {
                    return;
                }
                IOServiceGetMatchingServices(kIOMainPortDefault, matching, &raw mut iter)
            };
            if kr != KERN_SUCCESS || iter == 0 {
                return;
            }
            let _iter_guard = IoObjectGuard(iter);

            let mut idx = 0u32;
            loop {
                // SAFETY: `iter` is a valid `io_iterator_t`.
                let entry = unsafe { IOIteratorNext(iter) };
                if entry == 0 {
                    break;
                }
                let _entry_guard = IoObjectGuard(entry);
                let mut props: CFMutableDictionaryRef = ptr::null_mut();
                // SAFETY: `entry` is a valid `io_registry_entry_t`.
                let pkr = unsafe {
                    IORegistryEntryCreateCFProperties(entry, &raw mut props, kCFAllocatorDefault, 0)
                };
                if pkr != KERN_SUCCESS || props.is_null() {
                    continue;
                }
                let _props_guard = CfGuard(props.cast());
                let dict = props as CFDictionaryRef;

                let Some(stats) = dict_get_dict(dict, "Statistics") else {
                    continue;
                };
                let r_bytes = dict_get_i64(stats, "Bytes (Read)").unwrap_or(0).max(0) as u64;
                let w_bytes = dict_get_i64(stats, "Bytes (Write)").unwrap_or(0).max(0) as u64;
                let r_ops = dict_get_i64(stats, "Operations (Read)").unwrap_or(0).max(0) as u64;
                let w_ops = dict_get_i64(stats, "Operations (Write)")
                    .unwrap_or(0)
                    .max(0) as u64;

                let bsd_name = CompactString::from(format!("disk{idx}"));
                idx += 1;

                let prev = state.prev_disks.insert(
                    bsd_name.clone(),
                    PrevIoCounter {
                        rx_or_read: r_bytes,
                        tx_or_write: w_bytes,
                        sampled_at_ns,
                    },
                );

                let bytes_prov = MetricProvenance {
                    source: src_disk,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };
                let count_prov = MetricProvenance {
                    source: src_disk,
                    unit_tag: Some(UnitTag::COUNT),
                    sampled_at_ns,
                    window_ns: 0,
                    quality: MetricQuality::Authoritative,
                    contributed_by_stale: false,
                };

                let (read_rate, write_rate) = if let Some(p) = prev {
                    let dt_ns = sampled_at_ns.saturating_sub(p.sampled_at_ns);
                    let rate_prov = MetricProvenance {
                        source: src_disk,
                        unit_tag: Some(UnitTag::BYTES_PER_SEC),
                        sampled_at_ns,
                        window_ns: dt_ns,
                        quality: MetricQuality::Authoritative,
                        contributed_by_stale: false,
                    };
                    if dt_ns > 0 && r_bytes >= p.rx_or_read && w_bytes >= p.tx_or_write {
                        let dt_s = (dt_ns as f64) / 1_000_000_000.0;
                        (
                            Metric::measured(((r_bytes - p.rx_or_read) as f64) / dt_s, rate_prov),
                            Metric::measured(((w_bytes - p.tx_or_write) as f64) / dt_s, rate_prov),
                        )
                    } else {
                        (
                            Metric::unavailable(UnavailableReason::NotYetAvailable, rate_prov),
                            Metric::unavailable(UnavailableReason::NotYetAvailable, rate_prov),
                        )
                    }
                } else {
                    let rate_prov = MetricProvenance {
                        source: src_disk,
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
                };

                snapshot.disks.push(DiskSnapshot {
                    bsd_name,
                    read_bytes_total: Metric::measured(r_bytes as f64, bytes_prov),
                    write_bytes_total: Metric::measured(w_bytes as f64, bytes_prov),
                    read_ops_total: Metric::measured(r_ops as f64, count_prov),
                    write_ops_total: Metric::measured(w_ops as f64, count_prov),
                    read_bytes_per_sec: read_rate,
                    write_bytes_per_sec: write_rate,
                    root_total_bytes: root_total,
                    root_available_bytes: root_avail,
                    root_used_percent: root_pct,
                });
            }
        }

        fn collect_networks(
            state: &mut CollectorInner,
            snapshot: &mut SystemSnapshot,
            sampled_at_ns: u64,
        ) {
            let src_net = snapshot.intern_source("macos.net.sysctl_iflist2");
            let mut mib: [c_int; 6] =
                [libc::CTL_NET, libc::PF_ROUTE, 0, 0, libc::NET_RT_IFLIST2, 0];
            let mut len: usize = 0;
            // SAFETY: Queries required buffer size for `NET_RT_IFLIST2`.
            if unsafe {
                libc::sysctl(
                    mib.as_mut_ptr(),
                    6,
                    ptr::null_mut(),
                    &raw mut len,
                    ptr::null_mut(),
                    0,
                )
            } != 0
                || len == 0
            {
                return;
            }

            let mut buf = vec![0u8; len + 4096];
            let mut actual_len = buf.len();
            // SAFETY: `buf` has `actual_len` writable bytes.
            if unsafe {
                libc::sysctl(
                    mib.as_mut_ptr(),
                    6,
                    buf.as_mut_ptr().cast::<c_void>(),
                    &raw mut actual_len,
                    ptr::null_mut(),
                    0,
                )
            } != 0
            {
                return;
            }

            let mut offset = 0usize;
            while offset + size_of::<libc::if_msghdr>() <= actual_len {
                // SAFETY: Bounds-checked `offset + size_of::<if_msghdr>() <= actual_len` and read via `read_unaligned`.
                let hdr: libc::if_msghdr =
                    unsafe { ptr::read_unaligned(buf.as_ptr().add(offset).cast()) };
                let msg_len = hdr.ifm_msglen as usize;
                if msg_len == 0 || offset + msg_len > actual_len {
                    break;
                }

                if i32::from(hdr.ifm_type) == libc::RTM_IFINFO2
                    && msg_len >= size_of::<libc::if_msghdr2>()
                {
                    // SAFETY: `msg_len >= size_of::<if_msghdr2>()` is verified above; `read_unaligned` handles 4-byte packing.
                    let hdr2: libc::if_msghdr2 =
                        unsafe { ptr::read_unaligned(buf.as_ptr().add(offset).cast()) };

                    // Only include UP non-loopback interfaces
                    if (hdr2.ifm_flags & libc::IFF_UP) != 0
                        && (hdr2.ifm_flags & libc::IFF_LOOPBACK) == 0
                    {
                        let mut name_buf = [0 as c_char; libc::IFNAMSIZ + 1];
                        // SAFETY: `if_indextoname` writes at most `IFNAMSIZ` bytes into `name_buf`.
                        let name_ptr = unsafe {
                            libc::if_indextoname(u32::from(hdr2.ifm_index), name_buf.as_mut_ptr())
                        };
                        if !name_ptr.is_null() {
                            // SAFETY: `name_buf` is NUL-terminated.
                            let if_name = CompactString::from(
                                unsafe { CStr::from_ptr(name_buf.as_ptr()) }
                                    .to_string_lossy()
                                    .as_ref(),
                            );
                            let rx_bytes = hdr2.ifm_data.ifi_ibytes;
                            let tx_bytes = hdr2.ifm_data.ifi_obytes;
                            let rx_pkts = hdr2.ifm_data.ifi_ipackets;
                            let tx_pkts = hdr2.ifm_data.ifi_opackets;

                            let prev = state.prev_nets.insert(
                                if_name.clone(),
                                PrevIoCounter {
                                    rx_or_read: rx_bytes,
                                    tx_or_write: tx_bytes,
                                    sampled_at_ns,
                                },
                            );

                            let bytes_prov = MetricProvenance {
                                source: src_net,
                                unit_tag: Some(UnitTag::BYTES),
                                sampled_at_ns,
                                window_ns: 0,
                                quality: MetricQuality::Authoritative,
                                contributed_by_stale: false,
                            };
                            let pkt_prov = MetricProvenance {
                                source: src_net,
                                unit_tag: Some(UnitTag::COUNT),
                                sampled_at_ns,
                                window_ns: 0,
                                quality: MetricQuality::Authoritative,
                                contributed_by_stale: false,
                            };

                            let (rx_rate, tx_rate) = if let Some(p) = prev {
                                let dt_ns = sampled_at_ns.saturating_sub(p.sampled_at_ns);
                                let rate_prov = MetricProvenance {
                                    source: src_net,
                                    unit_tag: Some(UnitTag::BYTES_PER_SEC),
                                    sampled_at_ns,
                                    window_ns: dt_ns,
                                    quality: MetricQuality::Authoritative,
                                    contributed_by_stale: false,
                                };
                                if dt_ns > 0
                                    && rx_bytes >= p.rx_or_read
                                    && tx_bytes >= p.tx_or_write
                                {
                                    let dt_s = (dt_ns as f64) / 1_000_000_000.0;
                                    (
                                        Metric::measured(
                                            ((rx_bytes - p.rx_or_read) as f64) / dt_s,
                                            rate_prov,
                                        ),
                                        Metric::measured(
                                            ((tx_bytes - p.tx_or_write) as f64) / dt_s,
                                            rate_prov,
                                        ),
                                    )
                                } else {
                                    (
                                        Metric::unavailable(
                                            UnavailableReason::NotYetAvailable,
                                            rate_prov,
                                        ),
                                        Metric::unavailable(
                                            UnavailableReason::NotYetAvailable,
                                            rate_prov,
                                        ),
                                    )
                                }
                            } else {
                                let rate_prov = MetricProvenance {
                                    source: src_net,
                                    unit_tag: Some(UnitTag::BYTES_PER_SEC),
                                    sampled_at_ns,
                                    window_ns: 0,
                                    quality: MetricQuality::Authoritative,
                                    contributed_by_stale: false,
                                };
                                (
                                    Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        rate_prov,
                                    ),
                                    Metric::unavailable(
                                        UnavailableReason::NotYetAvailable,
                                        rate_prov,
                                    ),
                                )
                            };

                            snapshot.networks.push(NetworkSnapshot {
                                interface: if_name,
                                rx_bytes_total: Metric::measured(rx_bytes as f64, bytes_prov),
                                tx_bytes_total: Metric::measured(tx_bytes as f64, bytes_prov),
                                rx_packets_total: Metric::measured(rx_pkts as f64, pkt_prov),
                                tx_packets_total: Metric::measured(tx_pkts as f64, pkt_prov),
                                rx_bytes_per_sec: rx_rate,
                                tx_bytes_per_sec: tx_rate,
                            });
                        }
                    }
                }
                offset += msg_len;
            }
        }

        fn collect_processes(
            state: &mut CollectorInner,
            snapshot: &mut SystemSnapshot,
            sampled_at_ns: u64,
        ) {
            let src_rusage = snapshot.intern_source("macos.proc.proc_pid_rusage");
            let src_taskinfo = snapshot.intern_source("macos.proc.proc_pidtaskinfo");

            // SAFETY: `proc_listallpids(null, 0)` returns the approximate number of active PIDs.
            let est_count = unsafe { libc::proc_listallpids(ptr::null_mut(), 0) };
            if est_count <= 0 {
                return;
            }
            let capacity = (est_count as usize) + 128;
            let mut pids = vec![0 as c_int; capacity];
            // SAFETY: `pids` has `capacity * size_of::<c_int>()` writable bytes.
            let bytes_or_count = unsafe {
                libc::proc_listallpids(
                    pids.as_mut_ptr().cast::<c_void>(),
                    (capacity * size_of::<c_int>()) as c_int,
                )
            };
            if bytes_or_count <= 0 {
                return;
            }
            // Note: `proc_listallpids` returns the number of PIDs written into the buffer
            let pid_count = (bytes_or_count as usize).min(capacity);

            // Compute mach timebase factor to convert mach absolute time ticks to nanoseconds
            let (numer, denom) = mach_timebase();

            let mut next_procs: HashMap<ProcessKey, (u64, u64, ProcessSnapshot)> =
                HashMap::with_capacity(pid_count);

            for &pid in &pids[..pid_count] {
                if pid <= 0 {
                    continue;
                }
                let Some((key, ppid, name)) = Self::read_process_key(pid) else {
                    continue;
                };

                let mut ri: libc::rusage_info_v4 = unsafe { std::mem::zeroed() };
                // SAFETY: `ri` is a zeroed `rusage_info_v4` struct (`RUSAGE_INFO_V4`).
                let r_rc = unsafe {
                    libc::proc_pid_rusage(
                        pid,
                        libc::RUSAGE_INFO_V4,
                        (&raw mut ri).cast::<*mut c_void>(),
                    )
                };

                let bytes_prov = |src: SourceId, quality: MetricQuality| MetricProvenance {
                    source: src,
                    unit_tag: Some(UnitTag::BYTES),
                    sampled_at_ns,
                    window_ns: 0,
                    quality,
                    contributed_by_stale: false,
                };

                if r_rc == 0 {
                    let raw_ticks = ri.ri_user_time.saturating_add(ri.ri_system_time);
                    let total_time_ns = raw_ticks.saturating_mul(numer) / denom.max(1);

                    let cpu_percent =
                        if let Some((prev_time_ns, prev_at_ns, _)) = state.prev_procs.get(&key) {
                            let dt_ns = sampled_at_ns.saturating_sub(*prev_at_ns);
                            let pct_prov = MetricProvenance {
                                source: src_rusage,
                                unit_tag: Some(UnitTag::PERCENT),
                                sampled_at_ns,
                                window_ns: dt_ns,
                                quality: MetricQuality::Authoritative,
                                contributed_by_stale: false,
                            };
                            if dt_ns > 0 && total_time_ns >= *prev_time_ns {
                                let d_cpu = total_time_ns - *prev_time_ns;
                                let pct = ((d_cpu as f64) / (dt_ns as f64) * 100.0).max(0.0);
                                Metric::measured(pct, pct_prov)
                            } else {
                                Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov)
                            }
                        } else {
                            Metric::unavailable(
                                UnavailableReason::NotYetAvailable,
                                MetricProvenance {
                                    source: src_rusage,
                                    unit_tag: Some(UnitTag::PERCENT),
                                    sampled_at_ns,
                                    window_ns: 0,
                                    quality: MetricQuality::Authoritative,
                                    contributed_by_stale: false,
                                },
                            )
                        };

                    let bp = bytes_prov(src_rusage, MetricQuality::Authoritative);
                    let proc_snap = ProcessSnapshot {
                        key,
                        ppid,
                        name,
                        cpu_percent,
                        phys_footprint_bytes: Metric::measured(ri.ri_phys_footprint as f64, bp),
                        resident_bytes: Metric::measured(ri.ri_resident_size as f64, bp),
                        disk_read_bytes: Metric::measured(ri.ri_diskio_bytesread as f64, bp),
                        disk_write_bytes: Metric::measured(ri.ri_diskio_byteswritten as f64, bp),
                        exited_at_ns: None,
                    };
                    snapshot.processes.push(proc_snap.clone());
                    next_procs.insert(key, (total_time_ns, sampled_at_ns, proc_snap));
                } else {
                    // Check `errno` (`EPERM` / `EACCES` -> `AccessDenied`) and try `PROC_PIDTASKINFO` fallback (`0x1005a8e70`)
                    // SAFETY: `__error()` returns a valid pointer to thread-local `errno`.
                    let err = unsafe { *libc::__error() };
                    let reason = if err == libc::EPERM || err == libc::EACCES {
                        UnavailableReason::AccessDenied
                    } else {
                        UnavailableReason::TransientError
                    };

                    let mut ti: libc::proc_taskinfo = unsafe { std::mem::zeroed() };
                    let ti_sz = size_of::<libc::proc_taskinfo>() as c_int;
                    // SAFETY: `ti` is a valid `proc_taskinfo` struct.
                    let ti_ret = unsafe {
                        libc::proc_pidinfo(
                            pid,
                            libc::PROC_PIDTASKINFO,
                            0,
                            (&raw mut ti).cast::<c_void>(),
                            ti_sz,
                        )
                    };

                    if ti_ret == ti_sz {
                        let raw_ticks = ti.pti_total_user.saturating_add(ti.pti_total_system);
                        let total_time_ns = raw_ticks.saturating_mul(numer) / denom.max(1);
                        let cpu_percent = if let Some((prev_time_ns, prev_at_ns, _)) =
                            state.prev_procs.get(&key)
                        {
                            let dt_ns = sampled_at_ns.saturating_sub(*prev_at_ns);
                            let pct_prov = MetricProvenance {
                                source: src_taskinfo,
                                unit_tag: Some(UnitTag::PERCENT),
                                sampled_at_ns,
                                window_ns: dt_ns,
                                quality: MetricQuality::Fallback,
                                contributed_by_stale: false,
                            };
                            if dt_ns > 0 && total_time_ns >= *prev_time_ns {
                                let d_cpu = total_time_ns - *prev_time_ns;
                                let pct = ((d_cpu as f64) / (dt_ns as f64) * 100.0).max(0.0);
                                Metric::measured(pct, pct_prov)
                            } else {
                                Metric::unavailable(UnavailableReason::NotYetAvailable, pct_prov)
                            }
                        } else {
                            Metric::unavailable(
                                UnavailableReason::NotYetAvailable,
                                MetricProvenance {
                                    source: src_taskinfo,
                                    unit_tag: Some(UnitTag::PERCENT),
                                    sampled_at_ns,
                                    window_ns: 0,
                                    quality: MetricQuality::Fallback,
                                    contributed_by_stale: false,
                                },
                            )
                        };

                        let bp_rusage = bytes_prov(src_rusage, MetricQuality::Authoritative);
                        let bp_fallback = bytes_prov(src_taskinfo, MetricQuality::Fallback);
                        let proc_snap = ProcessSnapshot {
                            key,
                            ppid,
                            name,
                            cpu_percent,
                            phys_footprint_bytes: Metric::unavailable(reason, bp_rusage),
                            resident_bytes: Metric::measured(
                                ti.pti_resident_size as f64,
                                bp_fallback,
                            ),
                            disk_read_bytes: Metric::unavailable(reason, bp_rusage),
                            disk_write_bytes: Metric::unavailable(reason, bp_rusage),
                            exited_at_ns: None,
                        };
                        snapshot.processes.push(proc_snap.clone());
                        next_procs.insert(key, (total_time_ns, sampled_at_ns, proc_snap));
                    } else {
                        let bp = bytes_prov(src_rusage, MetricQuality::Authoritative);
                        let pct_prov = MetricProvenance {
                            source: src_rusage,
                            unit_tag: Some(UnitTag::PERCENT),
                            sampled_at_ns,
                            window_ns: 0,
                            quality: MetricQuality::Authoritative,
                            contributed_by_stale: false,
                        };
                        let proc_snap = ProcessSnapshot {
                            key,
                            ppid,
                            name,
                            cpu_percent: Metric::unavailable(reason, pct_prov),
                            phys_footprint_bytes: Metric::unavailable(reason, bp),
                            resident_bytes: Metric::unavailable(reason, bp),
                            disk_read_bytes: Metric::unavailable(reason, bp),
                            disk_write_bytes: Metric::unavailable(reason, bp),
                            exited_at_ns: None,
                        };
                        snapshot.processes.push(proc_snap);
                    }
                }
            }

            // Retain exited processes for `5,000,000,000` ns (`0x100544c7c`)
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
            snapshot
                .exited_processes
                .extend(state.exited_procs.values().cloned());

            state.prev_procs = next_procs;
        }
    }

    fn mach_timebase() -> (u64, u64) {
        let mut info = mach2::mach_time::mach_timebase_info_data_t { numer: 1, denom: 1 };
        // SAFETY: `mach_timebase_info` populates `info` with the system's Mach tick ratio.
        let kr = unsafe { mach2::mach_time::mach_timebase_info(&raw mut info) };
        if kr == KERN_SUCCESS && info.denom > 0 {
            (u64::from(info.numer), u64::from(info.denom))
        } else {
            (1, 1)
        }
    }
}

#[cfg(target_os = "macos")]
pub use macos_collector::MacCollector;

/// Cross-platform Linux facade delegating `MacCollector` to `nexus_collectors_linux::LinuxCollector`.
#[cfg(target_os = "linux")]
pub struct MacCollector {
    inner: nexus_collectors_linux::LinuxCollector,
}

#[cfg(target_os = "linux")]
impl Default for MacCollector {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(target_os = "linux")]
impl MacCollector {
    pub fn new() -> Self {
        Self {
            inner: nexus_collectors_linux::LinuxCollector::new(),
        }
    }

    pub fn collect(&self) -> nexus_core::SystemSnapshot {
        self.inner.collect()
    }

    pub fn read_process_key(
        pid: i32,
    ) -> Option<(nexus_core::ProcessKey, i32, compact_str::CompactString)> {
        nexus_collectors_linux::read_process_key(pid)
    }
}

/// Cross-platform Windows facade delegating `MacCollector` to `nexus_collectors_windows::WindowsCollector`.
#[cfg(target_os = "windows")]
pub struct MacCollector {
    inner: nexus_collectors_windows::WindowsCollector,
}

#[cfg(target_os = "windows")]
impl Default for MacCollector {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(target_os = "windows")]
impl MacCollector {
    pub fn new() -> Self {
        Self {
            inner: nexus_collectors_windows::WindowsCollector::new(),
        }
    }

    pub fn collect(&self) -> nexus_core::SystemSnapshot {
        self.inner.collect()
    }

    pub fn read_process_key(
        pid: i32,
    ) -> Option<(nexus_core::ProcessKey, i32, compact_str::CompactString)> {
        nexus_collectors_windows::read_process_key(pid)
    }
}

#[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
pub struct MacCollector;

#[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
impl Default for MacCollector {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
impl MacCollector {
    pub fn new() -> Self {
        Self
    }

    pub fn collect(&self) -> nexus_core::SystemSnapshot {
        nexus_core::SystemSnapshot::new_empty(1, 1)
    }

    pub fn read_process_key(
        _pid: i32,
    ) -> Option<(nexus_core::ProcessKey, i32, compact_str::CompactString)> {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::ValueState;

    #[test]
    fn live_sigmamini_collection_produces_coherent_snapshot() {
        let collector = MacCollector::new();
        let snap1 = collector.collect();
        assert_eq!(snap1.first_coherence_fault(), None);
        assert_eq!(
            snap1.system.cpu_percent.state(),
            ValueState::NotYetAvailable
        );
        assert!(snap1.system.mem_total_bytes.state() == ValueState::Valid);

        std::thread::sleep(std::time::Duration::from_millis(120));
        let snap2 = collector.collect();
        assert_eq!(snap2.first_coherence_fault(), None);
        assert!(
            matches!(
                snap2.system.cpu_percent.state(),
                ValueState::Valid | ValueState::Zero
            ),
            "second tick CPU percent should be measured, got {:?}",
            snap2.system.cpu_percent.state()
        );
        assert!(!snap2.processes.is_empty());
    }
}
