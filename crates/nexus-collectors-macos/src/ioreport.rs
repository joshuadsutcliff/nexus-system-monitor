#[cfg(target_os = "macos")]
use crate::ffi::{
    CfGuard, array_get, array_len, cfstring_to_string, dict_get_array, make_cfstring,
};
#[cfg(target_os = "macos")]
use compact_str::CompactString;
#[cfg(target_os = "macos")]
use core_foundation_sys::dictionary::{CFDictionaryRef, CFMutableDictionaryRef};
#[cfg(target_os = "macos")]
use core_foundation_sys::string::CFStringRef;
#[cfg(target_os = "macos")]
use libloading::{Library, Symbol};
#[cfg(target_os = "macos")]
use nexus_core::{
    ClusterClockMetric, MediaEngineBlockMetric, Metric, MetricProvenance, MetricQuality,
    SocPowerMetrics, SourceId, UnavailableReason, UnitTag,
};
use std::collections::BTreeMap;
#[cfg(target_os = "macos")]
use std::ffi::c_void;
#[cfg(target_os = "macos")]
use std::ptr;

#[cfg(target_os = "macos")]
type IOReportCopyChannelsInGroupFn =
    unsafe extern "C" fn(CFStringRef, CFStringRef, u64, u64, u64) -> CFMutableDictionaryRef;
#[cfg(target_os = "macos")]
type IOReportMergeChannelsFn =
    unsafe extern "C" fn(CFMutableDictionaryRef, CFDictionaryRef, *const c_void);
#[cfg(target_os = "macos")]
type IOReportCreateSubscriptionFn = unsafe extern "C" fn(
    *const c_void,
    CFMutableDictionaryRef,
    *mut CFMutableDictionaryRef,
    u64,
    *const c_void,
) -> *mut c_void;
#[cfg(target_os = "macos")]
type IOReportCreateSamplesFn =
    unsafe extern "C" fn(*const c_void, CFMutableDictionaryRef, *const c_void) -> CFDictionaryRef;
#[cfg(target_os = "macos")]
type IOReportCreateSamplesDeltaFn =
    unsafe extern "C" fn(CFDictionaryRef, CFDictionaryRef, *const c_void) -> CFDictionaryRef;
#[cfg(target_os = "macos")]
type IOReportChannelGetGroupFn = unsafe extern "C" fn(CFDictionaryRef) -> CFStringRef;
#[cfg(target_os = "macos")]
type IOReportChannelGetSubGroupFn = unsafe extern "C" fn(CFDictionaryRef) -> CFStringRef;
#[cfg(target_os = "macos")]
type IOReportChannelGetFormatFn = unsafe extern "C" fn(CFDictionaryRef) -> u8;
#[cfg(target_os = "macos")]
type IOReportChannelGetChannelNameFn = unsafe extern "C" fn(CFDictionaryRef) -> CFStringRef;
#[cfg(target_os = "macos")]
type IOReportChannelGetUnitLabelFn = unsafe extern "C" fn(CFDictionaryRef) -> CFStringRef;
#[cfg(target_os = "macos")]
type IOReportSimpleGetIntegerValueFn = unsafe extern "C" fn(CFDictionaryRef, i32) -> i64;
#[cfg(target_os = "macos")]
type IOReportStateGetCountFn = unsafe extern "C" fn(CFDictionaryRef) -> i32;
#[cfg(target_os = "macos")]
type IOReportStateGetNameForIndexFn = unsafe extern "C" fn(CFDictionaryRef, i32) -> CFStringRef;
#[cfg(target_os = "macos")]
type IOReportStateGetResidencyFn = unsafe extern "C" fn(CFDictionaryRef, i32) -> i64;

#[cfg(target_os = "macos")]
const K_IOREPORT_FORMAT_STATE: u8 = 2;

/// Pure-Rust representation of a decoded `libIOReport` state-residency channel.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IoReportStateChannel {
    pub group: String,
    pub subgroup: String,
    pub channel: String,
    pub states: Vec<(String, i64)>,
}

/// Stub `IoReportSampler` on non-macOS platforms.
#[cfg(not(target_os = "macos"))]
pub struct IoReportSampler;

#[cfg(not(target_os = "macos"))]
impl IoReportSampler {
    pub fn new() -> Option<Self> {
        None
    }

    pub fn try_new() -> Option<Self> {
        None
    }

    pub fn sample_power_watts(&mut self) -> Option<f64> {
        None
    }
}

/// Dynamically loaded `/usr/lib/libIOReport.dylib` subscription state (`0x1005e3d6c`, `0x1005e4030`).
#[cfg(target_os = "macos")]
pub struct IoReportSampler {
    _lib: Library,
    subscription: *mut c_void,
    subbed_channels: CFMutableDictionaryRef,
    prev_sample: Option<(CFDictionaryRef, u64)>,
    has_soc_stats: bool,
    fn_create_samples: IOReportCreateSamplesFn,
    fn_create_delta: IOReportCreateSamplesDeltaFn,
    fn_get_group: Option<IOReportChannelGetGroupFn>,
    fn_get_subgroup: Option<IOReportChannelGetSubGroupFn>,
    fn_get_format: Option<IOReportChannelGetFormatFn>,
    fn_get_channel_name: IOReportChannelGetChannelNameFn,
    fn_get_unit_label: IOReportChannelGetUnitLabelFn,
    fn_get_int_value: IOReportSimpleGetIntegerValueFn,
    fn_state_get_count: IOReportStateGetCountFn,
    fn_state_get_name: IOReportStateGetNameForIndexFn,
    fn_state_get_residency: IOReportStateGetResidencyFn,
}

// SAFETY: `IoReportSampler` owns its CoreFoundation dictionary/subscription handles and is accessed
// exclusively through a `parking_lot::Mutex` inside `MacCollector`.
#[cfg(target_os = "macos")]
unsafe impl Send for IoReportSampler {}

#[cfg(target_os = "macos")]
impl Drop for IoReportSampler {
    fn drop(&mut self) {
        if let Some((prev, _)) = self.prev_sample.take() {
            let _ = CfGuard(prev.cast());
        }
        if !self.subbed_channels.is_null() {
            let _ = CfGuard(self.subbed_channels.cast());
        }
        if !self.subscription.is_null() {
            let _ = CfGuard(self.subscription.cast());
        }
    }
}

#[cfg(target_os = "macos")]
impl IoReportSampler {
    pub fn try_new() -> Option<Self> {
        // SAFETY: `/usr/lib/libIOReport.dylib` is a standard macOS system library in the dyld shared cache.
        let lib = unsafe { Library::new("/usr/lib/libIOReport.dylib").ok()? };

        // SAFETY: All symbols below are verified `extern "C"` exports of `libIOReport.dylib`.
        unsafe {
            let copy_group: Symbol<IOReportCopyChannelsInGroupFn> =
                lib.get(b"IOReportCopyChannelsInGroup\0").ok()?;
            let merge_channels: Symbol<IOReportMergeChannelsFn> =
                lib.get(b"IOReportMergeChannels\0").ok()?;
            let create_sub: Symbol<IOReportCreateSubscriptionFn> =
                lib.get(b"IOReportCreateSubscription\0").ok()?;
            let fn_create_samples = *lib
                .get::<IOReportCreateSamplesFn>(b"IOReportCreateSamples\0")
                .ok()?;
            let fn_create_delta = *lib
                .get::<IOReportCreateSamplesDeltaFn>(b"IOReportCreateSamplesDelta\0")
                .ok()?;
            let fn_get_group = lib
                .get::<IOReportChannelGetGroupFn>(b"IOReportChannelGetGroup\0")
                .ok()
                .map(|s| *s);
            let fn_get_subgroup = lib
                .get::<IOReportChannelGetSubGroupFn>(b"IOReportChannelGetSubGroup\0")
                .ok()
                .map(|s| *s);
            let fn_get_format = lib
                .get::<IOReportChannelGetFormatFn>(b"IOReportChannelGetFormat\0")
                .ok()
                .map(|s| *s);
            let fn_get_channel_name = *lib
                .get::<IOReportChannelGetChannelNameFn>(b"IOReportChannelGetChannelName\0")
                .ok()?;
            let fn_get_unit_label = *lib
                .get::<IOReportChannelGetUnitLabelFn>(b"IOReportChannelGetUnitLabel\0")
                .ok()?;
            let fn_get_int_value = *lib
                .get::<IOReportSimpleGetIntegerValueFn>(b"IOReportSimpleGetIntegerValue\0")
                .ok()?;
            let fn_state_get_count = *lib
                .get::<IOReportStateGetCountFn>(b"IOReportStateGetCount\0")
                .ok()?;
            let fn_state_get_name = *lib
                .get::<IOReportStateGetNameForIndexFn>(b"IOReportStateGetNameForIndex\0")
                .ok()?;
            let fn_state_get_residency = *lib
                .get::<IOReportStateGetResidencyFn>(b"IOReportStateGetResidency\0")
                .ok()?;

            let g_energy = make_cfstring("Energy Model")?;
            let ch_energy = copy_group(g_energy.0 as CFStringRef, ptr::null(), 0, 0, 0);
            if ch_energy.is_null() {
                return None;
            }
            let ch_energy_guard = CfGuard(ch_energy.cast());

            for group_name in ["CPU Stats", "GPU Stats"] {
                if let Some(g) = make_cfstring(group_name) {
                    let ch = copy_group(g.0 as CFStringRef, ptr::null(), 0, 0, 0);
                    if !ch.is_null() {
                        let _g = CfGuard(ch.cast());
                        merge_channels(ch_energy, ch as CFDictionaryRef, ptr::null());
                    }
                }
            }

            let mut has_soc_stats = false;
            if let Some(g_soc) = make_cfstring("SoC Stats") {
                let ch_soc = copy_group(g_soc.0 as CFStringRef, ptr::null(), 0, 0, 0);
                if !ch_soc.is_null() {
                    let _g = CfGuard(ch_soc.cast());
                    merge_channels(ch_energy, ch_soc as CFDictionaryRef, ptr::null());
                    has_soc_stats = true;
                }
            }

            let mut subbed_channels: CFMutableDictionaryRef = ptr::null_mut();
            let subscription = create_sub(
                ptr::null(),
                ch_energy,
                &raw mut subbed_channels,
                0,
                ptr::null(),
            );
            drop(ch_energy_guard);

            if subscription.is_null() || subbed_channels.is_null() {
                if !subbed_channels.is_null() {
                    let _ = CfGuard(subbed_channels.cast());
                }
                if !subscription.is_null() {
                    let _ = CfGuard(subscription.cast());
                }
                return None;
            }

            Some(Self {
                _lib: lib,
                subscription,
                subbed_channels,
                prev_sample: None,
                has_soc_stats,
                fn_create_samples,
                fn_create_delta,
                fn_get_group,
                fn_get_subgroup,
                fn_get_format,
                fn_get_channel_name,
                fn_get_unit_label,
                fn_get_int_value,
                fn_state_get_count,
                fn_state_get_name,
                fn_state_get_residency,
            })
        }
    }

    /// Samples `libIOReport.dylib` and computes delta watts, cluster residencies,
    /// Media Engine blocks, SoC floor residency, and DVFS effective clocks (`0x1005e477c`).
    pub fn sample_soc_power(
        &mut self,
        sampled_at_ns: u64,
        src_ioreport: SourceId,
        src_power_telem: SourceId,
        system_power_in_w: Option<f64>,
        system_load_w: Option<f64>,
    ) -> SocPowerMetrics {
        let base_prov = |src: SourceId, window_ns: u64, quality: MetricQuality| MetricProvenance {
            source: src,
            unit_tag: Some(UnitTag::WATTS),
            sampled_at_ns,
            window_ns,
            quality,
            contributed_by_stale: false,
        };
        let pct_prov = |src: SourceId, window_ns: u64| MetricProvenance {
            source: src,
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns,
            window_ns,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let mhz_prov = |src: SourceId, window_ns: u64| MetricProvenance {
            source: src,
            unit_tag: Some(UnitTag::MEGAHERTZ),
            sampled_at_ns,
            window_ns,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };

        let unavailable_metrics = |reason: UnavailableReason, window_ns: u64, sys_in| {
            let p = base_prov(src_ioreport, window_ns, MetricQuality::Unknown);
            let unavail = Metric::unavailable(reason, p);
            let unavail_pct = Metric::unavailable(reason, pct_prov(src_ioreport, window_ns));
            let unavail_mhz = Metric::unavailable(reason, mhz_prov(src_ioreport, window_ns));
            SocPowerMetrics {
                cpu_watts: unavail,
                gpu_watts: unavail,
                ane_watts: unavail,
                dram_watts: unavail,
                package_watts: unavail,
                system_power_in_watts: sys_in,
                p_cluster_active_residency_percent: unavail_pct,
                e_cluster_active_residency_percent: unavail_pct,
                ane_active_residency_percent: unavail_pct,
                e_cluster_mhz: unavail_mhz,
                p_cluster_mhz: unavail_mhz,
                gpu_clock_mhz: unavail_mhz,
                media_blocks: Vec::new(),
                cluster_clocks: Vec::new(),
                soc_floor_residency_percent: unavail_pct,
                pmp_power_watts: unavail,
            }
        };

        let sys_in_metric = match system_power_in_w {
            Some(w) => Metric::measured(
                w,
                base_prov(src_power_telem, 0, MetricQuality::Authoritative),
            ),
            None => Metric::unavailable(
                UnavailableReason::NotApplicable,
                base_prov(src_power_telem, 0, MetricQuality::Unknown),
            ),
        };

        // SAFETY: `self.subscription` and `self.subbed_channels` were validated at construction.
        let cur_sample = unsafe {
            (self.fn_create_samples)(self.subscription, self.subbed_channels, ptr::null())
        };
        if cur_sample.is_null() {
            return unavailable_metrics(UnavailableReason::TransientError, 0, sys_in_metric);
        }

        let Some((prev_dict, prev_ns)) = self.prev_sample.replace((cur_sample, sampled_at_ns))
        else {
            return unavailable_metrics(UnavailableReason::NotYetAvailable, 0, sys_in_metric);
        };

        let prev_guard = CfGuard(prev_dict.cast());
        let dt_ns = sampled_at_ns.saturating_sub(prev_ns);
        if dt_ns == 0 {
            return unavailable_metrics(UnavailableReason::NotYetAvailable, 0, sys_in_metric);
        }

        // SAFETY: `prev_dict` and `cur_sample` are valid IOReport sample dictionaries.
        let delta_dict = unsafe { (self.fn_create_delta)(prev_dict, cur_sample, ptr::null()) };
        drop(prev_guard);

        if delta_dict.is_null() {
            return unavailable_metrics(UnavailableReason::TransientError, dt_ns, sys_in_metric);
        }
        let _delta_guard = CfGuard(delta_dict.cast());

        let dt_sec = (dt_ns as f64) / 1_000_000_000.0;
        let mut cpu_joules: Option<f64> = None;
        let mut gpu_joules: Option<f64> = None;
        let mut ane_joules: Option<f64> = None;
        let mut dram_joules: Option<f64> = None;
        let mut pmp_joules: Option<f64> = None;
        let mut p_res_sum = 0.0_f64;
        let mut p_res_cnt = 0u32;
        let mut e_res_sum = 0.0_f64;
        let mut e_res_cnt = 0u32;

        let mut e_mhz_sum = 0.0_f64;
        let mut e_mhz_cnt = 0u32;
        let mut p_mhz_sum = 0.0_f64;
        let mut p_mhz_cnt = 0u32;
        let mut gpu_mhz_sum = 0.0_f64;
        let mut gpu_mhz_cnt = 0u32;

        let mut state_channels: Vec<IoReportStateChannel> = Vec::new();
        let mut cluster_clocks: Vec<ClusterClockMetric> = Vec::new();

        if let Some(channels) = dict_get_array(delta_dict, "IOReportChannels") {
            let count = array_len(channels);
            for i in 0..count {
                let ch = array_get(channels, i) as CFDictionaryRef;
                if ch.is_null() {
                    continue;
                }
                // SAFETY: `ch` is a valid channel dictionary inside `IOReportChannels`.
                let name_opt = unsafe { cfstring_to_string((self.fn_get_channel_name)(ch)) };
                let Some(name) = name_opt else {
                    continue;
                };
                // SAFETY: `ch` is a valid channel dictionary.
                let group = unsafe {
                    self.fn_get_group
                        .and_then(|f| cfstring_to_string(f(ch)))
                        .unwrap_or_default()
                };
                // SAFETY: `ch` is a valid channel dictionary.
                let subgroup = unsafe {
                    self.fn_get_subgroup
                        .and_then(|f| cfstring_to_string(f(ch)))
                        .unwrap_or_default()
                };
                // SAFETY: `ch` is a valid channel dictionary.
                let format = unsafe { self.fn_get_format.map(|f| f(ch)).unwrap_or(0) };
                // SAFETY: `ch` is a valid channel dictionary.
                let unit_opt = unsafe { cfstring_to_string((self.fn_get_unit_label)(ch)) };
                let unit = unit_opt.as_deref().unwrap_or("").trim();

                if let Some(scale) = energy_unit_to_joules(unit) {
                    // SAFETY: `ch` is a simple integer energy channel.
                    let raw = unsafe { (self.fn_get_int_value)(ch, 0) };
                    if raw >= 0 {
                        let joules = (raw as f64) * scale;
                        match name.as_str() {
                            "CPU Energy" => {
                                cpu_joules = Some(cpu_joules.unwrap_or(0.0).max(joules));
                            }
                            "CPU" if cpu_joules.is_none() => {
                                cpu_joules = Some(joules);
                            }
                            "GPU Energy" => {
                                // Prefer "GPU Energy" (nJ) over "GPU" (0 mJ) on Apple M4
                                gpu_joules = Some(joules);
                            }
                            "GPU" if gpu_joules.is_none() => {
                                gpu_joules = Some(joules);
                            }
                            "ANE" | "ANE Energy" => {
                                ane_joules = Some(ane_joules.unwrap_or(0.0).max(joules));
                            }
                            "DRAM" | "DRAM Energy" => {
                                dram_joules = Some(dram_joules.unwrap_or(0.0).max(joules));
                            }
                            "PMP" | "PMP Energy" => {
                                pmp_joules = Some(pmp_joules.unwrap_or(0.0).max(joules));
                            }
                            _ => {}
                        }
                    }
                    continue;
                }

                // SAFETY: `ch` is queried for state count; returns <= 0 if not a state channel.
                let state_cnt = unsafe { (self.fn_state_get_count)(ch) };
                if (format == K_IOREPORT_FORMAT_STATE || format == 0) && state_cnt > 0 {
                    let mut states = Vec::with_capacity(state_cnt as usize);
                    for s in 0..state_cnt {
                        // SAFETY: `s` is in `0..state_cnt`.
                        let res = unsafe { (self.fn_state_get_residency)(ch, s) };
                        // SAFETY: `s` is in `0..state_cnt`.
                        let st_name =
                            unsafe { cfstring_to_string((self.fn_state_get_name)(ch, s)) }
                                .unwrap_or_default();
                        states.push((st_name, res));
                    }

                    if state_cnt > 1 && (name.starts_with("PCPU") || name.starts_with("ECPU")) {
                        if let Some(active_pct) = compute_active_residency_percent(&states) {
                            if name.starts_with("PCPU") {
                                p_res_sum += active_pct;
                                p_res_cnt += 1;
                            } else {
                                e_res_sum += active_pct;
                                e_res_cnt += 1;
                            }
                            let eff_mhz = decode_dvfs_effective_mhz(&states);
                            if let Some(mhz) = eff_mhz {
                                if name.starts_with("PCPU") {
                                    p_mhz_sum += mhz;
                                    p_mhz_cnt += 1;
                                } else {
                                    e_mhz_sum += mhz;
                                    e_mhz_cnt += 1;
                                }
                            }
                            cluster_clocks.push(ClusterClockMetric {
                                cluster_name: CompactString::from(name.as_str()),
                                is_performance: name.starts_with("PCPU"),
                                effective_mhz: match eff_mhz {
                                    Some(mhz) => {
                                        Metric::measured(mhz, mhz_prov(src_ioreport, dt_ns))
                                    }
                                    None => Metric::unavailable(
                                        UnavailableReason::Unsupported,
                                        mhz_prov(src_ioreport, dt_ns),
                                    ),
                                },
                                active_residency_percent: Metric::measured(
                                    active_pct,
                                    pct_prov(src_ioreport, dt_ns),
                                ),
                            });
                        }
                    } else if (group.eq_ignore_ascii_case("GPU Stats")
                        || subgroup.eq_ignore_ascii_case("GPU Performance States")
                        || name.eq_ignore_ascii_case("GPUPH"))
                        && let Some(mhz) = decode_dvfs_effective_mhz(&states)
                    {
                        gpu_mhz_sum += mhz;
                        gpu_mhz_cnt += 1;
                    }

                    state_channels.push(IoReportStateChannel {
                        group,
                        subgroup,
                        channel: name,
                        states,
                    });
                }
            }
        }

        let to_watt_metric = |joules_opt: Option<f64>| -> Metric<f64> {
            match joules_opt {
                Some(j) => {
                    let watts = (j / dt_sec).clamp(0.0, 1000.0);
                    Metric::measured(
                        watts,
                        base_prov(src_ioreport, dt_ns, MetricQuality::Authoritative),
                    )
                }
                None => Metric::unavailable(
                    UnavailableReason::Unsupported,
                    base_prov(src_ioreport, dt_ns, MetricQuality::Authoritative),
                ),
            }
        };

        let cpu_watts = to_watt_metric(cpu_joules);
        let gpu_watts = to_watt_metric(gpu_joules);
        let ane_watts = to_watt_metric(ane_joules);
        let dram_watts = to_watt_metric(dram_joules);
        let pmp_power_watts = to_watt_metric(pmp_joules);

        let ioreport_sum_w = cpu_joules.unwrap_or(0.0)
            + gpu_joules.unwrap_or(0.0)
            + ane_joules.unwrap_or(0.0)
            + dram_joules.unwrap_or(0.0);
        let package_watts = if ioreport_sum_w > 0.0 {
            Metric::measured(
                (ioreport_sum_w / dt_sec).clamp(0.0, 1000.0),
                base_prov(src_ioreport, dt_ns, MetricQuality::Authoritative),
            )
        } else if let Some(load_w) = system_load_w.or(system_power_in_w) {
            Metric::measured(
                load_w.clamp(0.0, 1000.0),
                base_prov(src_power_telem, dt_ns, MetricQuality::Fallback),
            )
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                base_prov(src_ioreport, dt_ns, MetricQuality::Unknown),
            )
        };

        let p_cluster_active_residency_percent = if p_res_cnt > 0 {
            Metric::measured(
                p_res_sum / (p_res_cnt as f64),
                pct_prov(src_ioreport, dt_ns),
            )
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                pct_prov(src_ioreport, dt_ns),
            )
        };

        let e_cluster_active_residency_percent = if e_res_cnt > 0 {
            Metric::measured(
                e_res_sum / (e_res_cnt as f64),
                pct_prov(src_ioreport, dt_ns),
            )
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                pct_prov(src_ioreport, dt_ns),
            )
        };

        // True ANE active residency from "SoC Stats" -> "Cluster Power States" (DIE_*_ANE* / ANE*).
        // Never estimate ANE residency from wattage.
        let ane_active_residency_percent = if self.has_soc_stats {
            match decode_ane_residency_from_states(&state_channels) {
                Some(pct) => Metric::measured(pct, pct_prov(src_ioreport, dt_ns)),
                None => Metric::unavailable(
                    UnavailableReason::Unsupported,
                    pct_prov(src_ioreport, dt_ns),
                ),
            }
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                pct_prov(src_ioreport, dt_ns),
            )
        };

        let (decoded_media, decoded_soc_floor) = if self.has_soc_stats {
            decode_media_blocks_from_states(&state_channels)
        } else {
            (Vec::new(), None)
        };

        let media_blocks = decoded_media
            .into_iter()
            .map(|(id, label, pct)| MediaEngineBlockMetric {
                id: CompactString::from(id),
                label: CompactString::from(label),
                active_residency_percent: Metric::measured(pct, pct_prov(src_ioreport, dt_ns)),
            })
            .collect();

        let soc_floor_residency_percent = match decoded_soc_floor {
            Some(pct) => Metric::measured(pct, pct_prov(src_ioreport, dt_ns)),
            None => Metric::unavailable(
                UnavailableReason::Unsupported,
                pct_prov(src_ioreport, dt_ns),
            ),
        };

        let e_cluster_mhz = if e_mhz_cnt > 0 {
            Metric::measured(
                e_mhz_sum / (e_mhz_cnt as f64),
                mhz_prov(src_ioreport, dt_ns),
            )
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                mhz_prov(src_ioreport, dt_ns),
            )
        };

        let p_cluster_mhz = if p_mhz_cnt > 0 {
            Metric::measured(
                p_mhz_sum / (p_mhz_cnt as f64),
                mhz_prov(src_ioreport, dt_ns),
            )
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                mhz_prov(src_ioreport, dt_ns),
            )
        };

        let gpu_clock_mhz = if gpu_mhz_cnt > 0 {
            Metric::measured(
                gpu_mhz_sum / (gpu_mhz_cnt as f64),
                mhz_prov(src_ioreport, dt_ns),
            )
        } else {
            Metric::unavailable(
                UnavailableReason::Unsupported,
                mhz_prov(src_ioreport, dt_ns),
            )
        };

        SocPowerMetrics {
            cpu_watts,
            gpu_watts,
            ane_watts,
            dram_watts,
            package_watts,
            system_power_in_watts: sys_in_metric,
            p_cluster_active_residency_percent,
            e_cluster_active_residency_percent,
            ane_active_residency_percent,
            e_cluster_mhz,
            p_cluster_mhz,
            gpu_clock_mhz,
            media_blocks,
            cluster_clocks,
            soc_floor_residency_percent,
            pmp_power_watts,
        }
    }
}

/// Decodes ANE active residency (%) from `"SoC Stats"` -> `"Cluster Power States"` (`DIE_*_ANE*` or `ANE*`).
/// Uses `"ACT"` state ticks divided by total residency ticks across all states.
pub fn decode_ane_residency_from_states(channels: &[IoReportStateChannel]) -> Option<f64> {
    let mut act_ticks: i128 = 0;
    let mut total_ticks: i128 = 0;
    let mut matched = false;

    for ch in channels {
        if !ch.group.eq_ignore_ascii_case("SoC Stats") {
            continue;
        }
        let sub_ok = ch.subgroup.is_empty()
            || ch.subgroup.eq_ignore_ascii_case("Cluster Power States")
            || ch.subgroup.to_ascii_uppercase().contains("CLUSTER");
        if !sub_ok {
            continue;
        }
        let up_name = ch.channel.to_ascii_uppercase();
        let is_ane = up_name.starts_with("ANE") || up_name.contains("_ANE");
        if !is_ane {
            continue;
        }
        matched = true;
        for (state, ticks) in &ch.states {
            if *ticks <= 0 {
                continue;
            }
            total_ticks += *ticks as i128;
            if state.trim().eq_ignore_ascii_case("ACT") {
                act_ticks += *ticks as i128;
            }
        }
    }

    if matched && total_ticks > 0 {
        Some(((act_ticks as f64 / total_ticks as f64) * 100.0).clamp(0.0, 100.0))
    } else {
        None
    }
}

/// Decodes Media Engine blocks (`Video Decode`, `Video Encode`, `ProRes`, `AVD`, `JPEG`, `Scaler/ISP`)
/// and SoC Floor (`SOCFloor`, `VMIN`) residency percentages from `"SoC Stats"` state channels.
pub fn decode_media_blocks_from_states(
    channels: &[IoReportStateChannel],
) -> (Vec<(&'static str, &'static str, f64)>, Option<f64>) {
    let mut block_accum: BTreeMap<&'static str, (&'static str, i128, i128)> = BTreeMap::new();
    let mut floor_act: i128 = 0;
    let mut floor_total: i128 = 0;
    let mut floor_matched = false;

    for ch in channels {
        if !ch.group.eq_ignore_ascii_case("SoC Stats") {
            continue;
        }
        let up_name = ch.channel.to_ascii_uppercase();
        let up_sub = ch.subgroup.to_ascii_uppercase();

        if up_name.contains("SOCFLOOR")
            || up_name.contains("SOC_FLOOR")
            || up_name.contains("SOC FLOOR")
            || up_name.contains("VMIN")
            || up_sub.contains("SOCFLOOR")
            || up_sub.contains("VMIN")
        {
            if let Some((act, total)) = active_and_total_ticks(&ch.states) {
                floor_matched = true;
                floor_act += act;
                floor_total += total;
            }
            continue;
        }

        if let Some((id, label)) = classify_media_channel(&up_name, &up_sub)
            && let Some((act, total)) = active_and_total_ticks(&ch.states)
        {
            let entry = block_accum.entry(id).or_insert((label, 0, 0));
            entry.1 += act;
            entry.2 += total;
        }
    }

    let ordered_ids = ["vdec", "venc", "prores", "avd", "jpeg", "scaler_isp"];
    let mut blocks = Vec::new();
    for id in ordered_ids {
        if let Some((label, act, total)) = block_accum.get(id)
            && *total > 0
        {
            let pct = ((*act as f64 / *total as f64) * 100.0).clamp(0.0, 100.0);
            blocks.push((id, *label, pct));
        }
    }

    let soc_floor = if floor_matched && floor_total > 0 {
        Some(((floor_act as f64 / floor_total as f64) * 100.0).clamp(0.0, 100.0))
    } else {
        None
    };

    (blocks, soc_floor)
}

fn classify_media_channel(up_name: &str, up_sub: &str) -> Option<(&'static str, &'static str)> {
    let combined = if up_sub.is_empty() {
        up_name.to_string()
    } else {
        format!("{up_sub}:{up_name}")
    };
    if combined.contains("PRORES") {
        Some(("prores", "ProRes"))
    } else if combined.contains("VDEC")
        || combined.contains("VIDEO DECODE")
        || combined.contains("MDEC")
    {
        Some(("vdec", "Video Decode"))
    } else if combined.contains("VENC")
        || combined.contains("VIDEO ENCODE")
        || combined.contains("AVE")
    {
        Some(("venc", "Video Encode"))
    } else if combined.contains("AVD") {
        Some(("avd", "AVD"))
    } else if combined.contains("JPEG") {
        Some(("jpeg", "JPEG"))
    } else if combined.contains("SCALER") || combined.contains("ISP") {
        Some(("scaler_isp", "Scaler/ISP"))
    } else {
        None
    }
}

/// Computes residency-weighted effective DVFS frequency (MHz) when state labels carry numeric MHz/GHz.
/// Returns `None` honestly if state labels lack explicit frequency data (e.g. opaque `"P0"`/`"V1"`).
pub fn decode_dvfs_effective_mhz(states: &[(String, i64)]) -> Option<f64> {
    let mut weighted_sum = 0.0_f64;
    let mut active_ticks: i128 = 0;

    for (label, ticks) in states {
        if *ticks <= 0 {
            continue;
        }
        let up = label.trim().to_ascii_uppercase();
        if up.contains("IDLE") || up.contains("OFF") || up.contains("DOWN") || up.contains("SLEEP")
        {
            continue;
        }
        if let Some(mhz) = parse_mhz_from_state_label(&up) {
            weighted_sum += mhz * (*ticks as f64);
            active_ticks += *ticks as i128;
        }
    }

    if active_ticks > 0 {
        Some(weighted_sum / (active_ticks as f64))
    } else {
        None
    }
}

fn parse_mhz_from_state_label(upper_label: &str) -> Option<f64> {
    if let Some(idx) = upper_label.find("MHZ") {
        let prefix = upper_label[..idx].trim();
        let num_str: String = prefix
            .chars()
            .rev()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect::<String>()
            .chars()
            .rev()
            .collect();
        if let Ok(val) = num_str.parse::<f64>()
            && val.is_finite()
            && val > 0.0
        {
            return Some(val);
        }
    }
    if let Some(idx) = upper_label.find("GHZ") {
        let prefix = upper_label[..idx].trim();
        let num_str: String = prefix
            .chars()
            .rev()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect::<String>()
            .chars()
            .rev()
            .collect();
        if let Ok(val) = num_str.parse::<f64>()
            && val.is_finite()
            && val > 0.0
        {
            return Some(val * 1000.0);
        }
    }
    None
}

fn active_and_total_ticks(states: &[(String, i64)]) -> Option<(i128, i128)> {
    let mut total: i128 = 0;
    let mut act_explicit: i128 = 0;
    let mut has_explicit_act = false;
    let mut idle_ticks: i128 = 0;

    for (label, ticks) in states {
        if *ticks <= 0 {
            continue;
        }
        total += *ticks as i128;
        let up = label.trim().to_ascii_uppercase();
        if up == "ACT" {
            has_explicit_act = true;
            act_explicit += *ticks as i128;
        } else if up.contains("IDLE")
            || up.contains("OFF")
            || up.contains("DOWN")
            || up.contains("SLEEP")
        {
            idle_ticks += *ticks as i128;
        }
    }

    if total <= 0 {
        return None;
    }
    let active = if has_explicit_act {
        act_explicit
    } else {
        (total - idle_ticks).max(0)
    };
    Some((active, total))
}

#[cfg(target_os = "macos")]
fn compute_active_residency_percent(states: &[(String, i64)]) -> Option<f64> {
    let (active, total) = active_and_total_ticks(states)?;
    Some(((active as f64 / total as f64) * 100.0).clamp(0.0, 100.0))
}

#[cfg(target_os = "macos")]
fn energy_unit_to_joules(unit: &str) -> Option<f64> {
    match unit {
        "nJ" => Some(1e-9),
        "uJ" | "µJ" => Some(1e-6),
        "mJ" => Some(1e-3),
        "J" => Some(1.0),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ane_residency_uses_soc_stats_not_watts() {
        let channels = vec![IoReportStateChannel {
            group: "SoC Stats".to_string(),
            subgroup: "Cluster Power States".to_string(),
            channel: "DIE_0_ANE0".to_string(),
            states: vec![
                ("OFF".to_string(), 500),
                ("IDLE".to_string(), 250),
                ("ACT".to_string(), 250),
            ],
        }];
        let pct = decode_ane_residency_from_states(&channels).expect("ANE residency");
        assert!((pct - 25.0).abs() < 1e-6);

        let absent: Vec<IoReportStateChannel> = vec![IoReportStateChannel {
            group: "CPU Stats".to_string(),
            subgroup: "CPU Complex Performance States".to_string(),
            channel: "ECPU".to_string(),
            states: vec![("ACT".to_string(), 100)],
        }];
        assert_eq!(decode_ane_residency_from_states(&absent), None);
    }

    #[test]
    fn media_blocks_and_soc_floor_decode_correctly() {
        let channels = vec![
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "Media Engine".to_string(),
                channel: "DIE_0_VDEC0".to_string(),
                states: vec![("OFF".to_string(), 800), ("ACT".to_string(), 200)],
            },
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "Media Engine".to_string(),
                channel: "DIE_0_VENC0".to_string(),
                states: vec![("OFF".to_string(), 900), ("ACT".to_string(), 100)],
            },
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "Media Engine".to_string(),
                channel: "PRORES0".to_string(),
                states: vec![("IDLE".to_string(), 500), ("ACT".to_string(), 500)],
            },
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "Media Engine".to_string(),
                channel: "AVD0".to_string(),
                states: vec![("OFF".to_string(), 750), ("ACT".to_string(), 250)],
            },
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "Media Engine".to_string(),
                channel: "JPEG0".to_string(),
                states: vec![("OFF".to_string(), 950), ("ACT".to_string(), 50)],
            },
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "Media Engine".to_string(),
                channel: "SCALER0".to_string(),
                states: vec![("OFF".to_string(), 600), ("ACT".to_string(), 400)],
            },
            IoReportStateChannel {
                group: "SoC Stats".to_string(),
                subgroup: "SOCFloor".to_string(),
                channel: "VMIN".to_string(),
                states: vec![("OFF".to_string(), 700), ("ACT".to_string(), 300)],
            },
        ];

        let (blocks, floor) = decode_media_blocks_from_states(&channels);
        assert_eq!(blocks.len(), 6);
        assert_eq!(blocks[0].0, "vdec");
        assert!((blocks[0].2 - 20.0).abs() < 1e-6);
        assert_eq!(blocks[1].0, "venc");
        assert!((blocks[1].2 - 10.0).abs() < 1e-6);
        assert_eq!(blocks[2].0, "prores");
        assert!((blocks[2].2 - 50.0).abs() < 1e-6);
        assert_eq!(blocks[3].0, "avd");
        assert!((blocks[3].2 - 25.0).abs() < 1e-6);
        assert_eq!(blocks[4].0, "jpeg");
        assert!((blocks[4].2 - 5.0).abs() < 1e-6);
        assert_eq!(blocks[5].0, "scaler_isp");
        assert!((blocks[5].2 - 40.0).abs() < 1e-6);
        assert!((floor.unwrap() - 30.0).abs() < 1e-6);
    }

    #[test]
    fn dvfs_effective_mhz_weights_labeled_states_and_rejects_unmapped_pstates() {
        let labeled = vec![
            ("IDLE".to_string(), 600),
            ("1200MHz".to_string(), 100),
            ("2400 MHz".to_string(), 300),
        ];
        let mhz = decode_dvfs_effective_mhz(&labeled).expect("weighted MHz");
        assert!((mhz - 2100.0).abs() < 1e-6);

        let ghz = vec![("3.5GHz".to_string(), 100)];
        assert!((decode_dvfs_effective_mhz(&ghz).unwrap() - 3500.0).abs() < 1e-6);

        let unmapped = vec![
            ("IDLE".to_string(), 500),
            ("P1".to_string(), 300),
            ("P2".to_string(), 200),
        ];
        assert_eq!(decode_dvfs_effective_mhz(&unmapped), None);
    }
}
