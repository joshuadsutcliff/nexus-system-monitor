use crate::MacCollector;
#[cfg(target_os = "macos")]
use crate::ffi::{
    CfGuard, IOIteratorNext, IORegistryEntryCreateCFProperties, IOServiceGetMatchingServices,
    IOServiceMatching, IoObjectGuard, KERN_SUCCESS, dict_get_i64, dict_get_string,
    kIOMainPortDefault, sysctl_u32, sysctl_u64,
};
use compact_str::CompactString;
#[cfg(target_os = "macos")]
use core_foundation_sys::base::kCFAllocatorDefault;
#[cfg(target_os = "macos")]
use core_foundation_sys::dictionary::{CFDictionaryRef, CFMutableDictionaryRef};
use nexus_core::{
    ConnectionProtocol, DriverEntry, DriverKind, DriverScope, DriverState, InstalledAppEntry,
    LanHostEntry, LanPortEntry, LanRadarReport, Metric, MetricProvenance, MetricQuality,
    NetworkConnectionEntry, ProcessKey, RelatedFileEntry, ServiceActionResult, ServiceEntry,
    ServiceStartType, ServiceState, SourceId, StartupItem, StartupItemType, SystemHardwareSummary,
    TcpState, UnavailableReason, UncachedDiskIo, UninstallReceipt, UnitTag, UserSessionEntry,
    VolumeSpaceSummary, WifiBand, WifiNetworkEntry, WirelessScanReport,
};
use rayon::prelude::*;
use std::collections::{HashMap, HashSet};
#[cfg(unix)]
use std::ffi::CString;
#[cfg(target_os = "macos")]
use std::ffi::{CStr, c_char, c_int, c_void};
use std::fs::{self, File, OpenOptions};
use std::net::{Ipv4Addr, SocketAddr, TcpStream};
#[cfg(target_os = "macos")]
use std::os::unix::io::AsRawFd;
#[cfg(target_os = "windows")]
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
#[cfg(unix)]
use std::ptr;
use std::sync::OnceLock;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

#[cfg(target_os = "windows")]
const WIN_CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServiceAction {
    Start,
    Stop,
    Restart,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LaunchdPlistFacts {
    pub run_at_load: bool,
    pub keep_alive_truthy: bool,
    pub disabled: bool,
    pub label: Option<String>,
    pub program: Option<String>,
}

#[inline]
pub fn map_launchd_start_type(
    plist_found: bool,
    is_disabled: bool,
    run_at_load: bool,
    keep_alive_truthy: bool,
) -> ServiceStartType {
    if is_disabled {
        ServiceStartType::Disabled
    } else if !plist_found {
        ServiceStartType::Unknown
    } else if run_at_load || keep_alive_truthy {
        ServiceStartType::Automatic
    } else {
        ServiceStartType::Manual
    }
}

pub fn parse_launchctl_disabled(output: &str) -> HashSet<String> {
    let mut disabled = HashSet::new();
    for raw_line in output.lines() {
        let line = raw_line.trim();
        if !line.starts_with('"') {
            continue;
        }
        let Some(end_quote) = line[1..].find('"').map(|idx| idx + 1) else {
            continue;
        };
        let label = &line[1..end_quote];
        if label.is_empty() {
            continue;
        }
        let Some(arrow) = line[end_quote..].find("=>").map(|idx| end_quote + idx) else {
            continue;
        };
        let state = line[arrow + 2..].trim();
        if state.eq_ignore_ascii_case("disabled") {
            disabled.insert(label.to_string());
        }
    }
    disabled
}

/// Parses `launchctl list` output into `(pid_opt, label)` rows.
pub fn parse_launchctl_list(output: &str) -> Vec<(Option<i32>, String)> {
    let mut out = Vec::new();
    for line in output.lines() {
        if line.trim().is_empty() {
            continue;
        }
        let parts: Vec<&str> = line.split('\t').collect();
        if parts.len() < 3 {
            continue;
        }
        if parts[0].trim().eq_ignore_ascii_case("PID") {
            continue;
        }
        let pid = parts[0].trim().parse::<i32>().ok().filter(|&p| p > 0);
        let label = parts[2].trim();
        if !label.is_empty() {
            out.push((pid, label.to_string()));
        }
    }
    out
}

/// Lightweight XML plist parser extracting top-level `RunAtLoad`, `KeepAlive`, `Disabled`, `Label`, `Program`/`ProgramArguments`.
pub fn parse_plist_xml_facts(xml: &str) -> Option<LaunchdPlistFacts> {
    if !xml.contains("<plist") || !xml.contains("<dict>") {
        return None;
    }
    let run_at_load = plist_bool_value(xml, "RunAtLoad").unwrap_or(false);
    let disabled = plist_bool_value(xml, "Disabled").unwrap_or(false);
    let keep_alive_truthy = plist_keep_alive_truthy(xml);
    let label = plist_string_value(xml, "Label");
    let program = plist_program_value(xml);
    Some(LaunchdPlistFacts {
        run_at_load,
        keep_alive_truthy,
        disabled,
        label,
        program,
    })
}

fn plist_bool_value(xml: &str, key: &str) -> Option<bool> {
    let needle = format!("<key>{key}</key>");
    let idx = xml.find(&needle)?;
    let rest = xml[idx + needle.len()..].trim_start();
    if rest.starts_with("<true/>") {
        Some(true)
    } else if rest.starts_with("<false/>") {
        Some(false)
    } else {
        None
    }
}

fn plist_keep_alive_truthy(xml: &str) -> bool {
    let needle = "<key>KeepAlive</key>";
    let Some(idx) = xml.find(needle) else {
        return false;
    };
    let rest = xml[idx + needle.len()..].trim_start();
    if rest.starts_with("<true/>") {
        return true;
    }
    if rest.starts_with("<dict>")
        && let Some(end) = rest.find("</dict>")
    {
        return !rest[6..end].trim().is_empty();
    }
    false
}

fn plist_string_value(xml: &str, key: &str) -> Option<String> {
    let needle = format!("<key>{key}</key>");
    let idx = xml.find(&needle)?;
    let rest = xml[idx + needle.len()..].trim_start();
    if rest.starts_with("<string>")
        && let Some(end) = rest.find("</string>")
    {
        return Some(rest[8..end].trim().to_string());
    }
    None
}

fn plist_program_value(xml: &str) -> Option<String> {
    if let Some(p) = plist_string_value(xml, "Program") {
        return Some(p);
    }
    let needle = "<key>ProgramArguments</key>";
    let idx = xml.find(needle)?;
    let rest = xml[idx + needle.len()..].trim_start();
    if rest.starts_with("<array>") {
        let s_start = rest.find("<string>")?;
        let s_rest = &rest[s_start + 8..];
        let s_end = s_rest.find("</string>")?;
        return Some(s_rest[..s_end].trim().to_string());
    }
    None
}

#[cfg(target_os = "macos")]
fn read_plist_facts(path: &Path) -> Option<LaunchdPlistFacts> {
    let bytes = fs::read(path).ok()?;
    if bytes.starts_with(b"bplist") {
        let out = Command::new("/usr/bin/plutil")
            .args(["-convert", "xml1", "-o", "-"])
            .arg(path)
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .ok()?;
        if !out.status.success() {
            return None;
        }
        let xml = String::from_utf8_lossy(&out.stdout);
        parse_plist_xml_facts(&xml)
    } else {
        let xml = String::from_utf8_lossy(&bytes);
        parse_plist_xml_facts(&xml)
    }
}

#[cfg(target_os = "macos")]
fn launchd_plist_dirs() -> Vec<(PathBuf, StartupItemType, bool)> {
    let mut dirs = Vec::new();
    if let Ok(home) = std::env::var("HOME")
        && !home.is_empty()
    {
        dirs.push((
            PathBuf::from(home).join("Library/LaunchAgents"),
            StartupItemType::LaunchAgent,
            true,
        ));
    }
    dirs.push((
        PathBuf::from("/Library/LaunchAgents"),
        StartupItemType::LaunchAgent,
        false,
    ));
    dirs.push((
        PathBuf::from("/Library/LaunchDaemons"),
        StartupItemType::LaunchDaemon,
        false,
    ));
    dirs.push((
        PathBuf::from("/System/Library/LaunchAgents"),
        StartupItemType::LaunchAgent,
        false,
    ));
    dirs.push((
        PathBuf::from("/System/Library/LaunchDaemons"),
        StartupItemType::LaunchDaemon,
        false,
    ));
    dirs
}

#[cfg(target_os = "macos")]
fn build_launchd_index() -> HashMap<String, LaunchdPlistFacts> {
    let mut paths = Vec::new();
    for (dir, _, _) in launchd_plist_dirs() {
        if let Ok(entries) = fs::read_dir(&dir) {
            for entry in entries.flatten() {
                let p = entry.path();
                if p.extension().and_then(|e| e.to_str()) == Some("plist") {
                    paths.push(p);
                }
            }
        }
    }

    let parsed: Vec<(String, Option<String>, LaunchdPlistFacts)> = paths
        .par_iter()
        .filter_map(|p| {
            let facts = read_plist_facts(p)?;
            let stem = p.file_stem()?.to_string_lossy().into_owned();
            let lbl = facts.label.clone();
            Some((stem, lbl, facts))
        })
        .collect();

    let mut map = HashMap::with_capacity(parsed.len() * 2);
    for (stem, lbl, facts) in parsed {
        map.entry(stem).or_insert_with(|| facts.clone());
        if let Some(internal) = lbl {
            map.entry(internal).or_insert(facts);
        }
    }
    map
}

#[cfg(target_os = "macos")]
fn cached_launchd_index() -> &'static HashMap<String, LaunchdPlistFacts> {
    static INDEX: OnceLock<HashMap<String, LaunchdPlistFacts>> = OnceLock::new();
    INDEX.get_or_init(build_launchd_index)
}

#[cfg(target_os = "macos")]
fn run_launchctl(args: &[&str]) -> String {
    Command::new("/bin/launchctl")
        .args(args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).into_owned())
        .unwrap_or_default()
}

pub fn list_services() -> Vec<ServiceEntry> {
    #[cfg(target_os = "macos")]
    {
        let plist_index = cached_launchd_index();
        let mut disabled = parse_launchctl_disabled(&run_launchctl(&["print-disabled", "system"]));
        // SAFETY: `getuid()` is always safe and returns the current real UID.
        let uid = unsafe { libc::getuid() };
        let gui_domain = format!("gui/{uid}");
        disabled.extend(parse_launchctl_disabled(&run_launchctl(&[
            "print-disabled",
            &gui_domain,
        ])));

        let list_out = run_launchctl(&["list"]);
        let rows = parse_launchctl_list(&list_out);

        let mut services = Vec::with_capacity(rows.len());
        for (pid, label) in rows {
            let state = if pid.is_some() {
                ServiceState::Running
            } else {
                ServiceState::Stopped
            };
            let facts = plist_index.get(&label);
            let is_disabled = disabled.contains(&label) || facts.is_some_and(|f| f.disabled);
            let start_type = map_launchd_start_type(
                facts.is_some(),
                is_disabled,
                facts.is_some_and(|f| f.run_at_load),
                facts.is_some_and(|f| f.keep_alive_truthy),
            );
            let binary_path = facts
                .and_then(|f| f.program.as_deref())
                .unwrap_or("")
                .into();
            services.push(ServiceEntry {
                name: CompactString::from(label.as_str()),
                display_name: CompactString::from(label.as_str()),
                description: CompactString::const_new(""),
                state,
                start_type,
                pid,
                binary_path,
                user_account: CompactString::const_new(""),
            });
        }
        services.sort_by(|a, b| a.name.cmp(&b.name));
        services
    }
    #[cfg(target_os = "linux")]
    {
        let mut services = Command::new("systemctl")
            .args([
                "list-units",
                "--type=service",
                "--all",
                "--no-pager",
                "--no-legend",
            ])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .ok()
            .filter(|o| o.status.success())
            .map(|o| {
                nexus_collectors_linux::LinuxCollector::parse_systemd_units(
                    &String::from_utf8_lossy(&o.stdout),
                )
            })
            .unwrap_or_default();

        if services.is_empty() {
            let mut seen = HashSet::new();
            for dir in [
                "/etc/systemd/system",
                "/lib/systemd/system",
                "/usr/lib/systemd/system",
            ] {
                let Ok(rd) = fs::read_dir(dir) else {
                    continue;
                };
                for entry in rd.flatten() {
                    let p = entry.path();
                    if p.extension().and_then(|e| e.to_str()) != Some("service") {
                        continue;
                    }
                    let Some(fname) = p.file_name().and_then(|s| s.to_str()) else {
                        continue;
                    };
                    if !seen.insert(fname.to_string()) {
                        continue;
                    }
                    let content = fs::read_to_string(&p).unwrap_or_default();
                    let mut desc = String::new();
                    let mut exec = String::new();
                    for line in content.lines() {
                        let t = line.trim();
                        if let Some(v) = t.strip_prefix("Description=") {
                            desc = v.trim().to_string();
                        } else if let Some(v) = t.strip_prefix("ExecStart=") {
                            exec = v.trim().to_string();
                        }
                    }
                    services.push(ServiceEntry {
                        name: CompactString::from(fname),
                        display_name: CompactString::from(fname.trim_end_matches(".service")),
                        description: CompactString::from(desc),
                        state: ServiceState::Unknown,
                        start_type: ServiceStartType::Unknown,
                        pid: None,
                        binary_path: CompactString::from(exec),
                        user_account: CompactString::const_new(""),
                    });
                }
            }
            if let Ok(rd) = fs::read_dir("/etc/init.d") {
                for entry in rd.flatten() {
                    let p = entry.path();
                    if !p.is_file() {
                        continue;
                    }
                    let Some(fname) = p.file_name().and_then(|s| s.to_str()) else {
                        continue;
                    };
                    if fname.starts_with('.') || !seen.insert(fname.to_string()) {
                        continue;
                    }
                    services.push(ServiceEntry {
                        name: CompactString::from(fname),
                        display_name: CompactString::from(fname),
                        description: CompactString::const_new("SysV init script"),
                        state: ServiceState::Unknown,
                        start_type: ServiceStartType::Manual,
                        pid: None,
                        binary_path: CompactString::from(p.to_string_lossy().as_ref()),
                        user_account: CompactString::const_new(""),
                    });
                }
            }
        }
        services.sort_by(|a, b| a.name.cmp(&b.name));
        services
    }
    #[cfg(target_os = "windows")]
    {
        let mut services = Command::new("sc")
            .args(["queryex", "type=", "service", "state=", "all"])
            .creation_flags(WIN_CREATE_NO_WINDOW)
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .ok()
            .map(|o| {
                nexus_collectors_windows::WindowsCollector::parse_windows_sc_query(
                    &String::from_utf8_lossy(&o.stdout),
                )
            })
            .unwrap_or_default();

        if services.is_empty()
            && let Ok(o) = Command::new("sc")
                .args(["query", "state=", "all"])
                .creation_flags(WIN_CREATE_NO_WINDOW)
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .output()
        {
            services = nexus_collectors_windows::WindowsCollector::parse_windows_sc_query(
                &String::from_utf8_lossy(&o.stdout),
            );
        }

        if services.is_empty() {
            services = win32_ops::list_registry_services();
        }
        services.sort_by(|a, b| a.name.cmp(&b.name));
        services
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        Vec::new()
    }
}

pub fn control_service(name: &str, action: ServiceAction, dry_run: bool) -> ServiceActionResult {
    if name.trim().is_empty() || name.contains(char::is_whitespace) {
        return ServiceActionResult::Failed(CompactString::const_new("Invalid service label"));
    }
    if dry_run {
        return ServiceActionResult::Ok {
            dry_run: true,
            message: CompactString::from(format!("Dry-run {:?} validated for {name}", action)),
        };
    }

    #[cfg(target_os = "macos")]
    {
        let exec = |verb: &str| -> Result<(), String> {
            let out = Command::new("/bin/launchctl")
                .args([verb, name])
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .output()
                .map_err(|e| e.to_string())?;
            if out.status.success() {
                Ok(())
            } else {
                let err = String::from_utf8_lossy(&out.stderr).trim().to_string();
                Err(if err.is_empty() {
                    format!("launchctl {verb} exited with {}", out.status)
                } else {
                    err
                })
            }
        };

        let res = match action {
            ServiceAction::Start => exec("start"),
            ServiceAction::Stop => exec("stop"),
            ServiceAction::Restart => exec("stop").and_then(|_| exec("start")),
        };
        match res {
            Ok(()) => ServiceActionResult::Ok {
                dry_run: false,
                message: CompactString::from(format!("Executed {:?} on {name}", action)),
            },
            Err(e) => ServiceActionResult::Failed(CompactString::from(e)),
        }
    }
    #[cfg(target_os = "linux")]
    {
        let verb = match action {
            ServiceAction::Start => "start",
            ServiceAction::Stop => "stop",
            ServiceAction::Restart => "restart",
        };
        match Command::new("systemctl")
            .args([verb, name])
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
        {
            Ok(out) if out.status.success() => ServiceActionResult::Ok {
                dry_run: false,
                message: CompactString::from(format!("Executed systemctl {verb} on {name}")),
            },
            Ok(out) => ServiceActionResult::Failed(CompactString::from(
                String::from_utf8_lossy(&out.stderr).trim(),
            )),
            Err(e) => ServiceActionResult::Failed(CompactString::from(e.to_string())),
        }
    }
    #[cfg(target_os = "windows")]
    {
        let exec = |verb: &str| -> Result<(), String> {
            let out = Command::new("sc")
                .args([verb, name])
                .creation_flags(WIN_CREATE_NO_WINDOW)
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .output()
                .map_err(|e| e.to_string())?;
            if out.status.success() {
                Ok(())
            } else {
                Err(String::from_utf8_lossy(&out.stdout).trim().to_string())
            }
        };
        let res = match action {
            ServiceAction::Start => exec("start"),
            ServiceAction::Stop => exec("stop"),
            ServiceAction::Restart => exec("stop").and_then(|_| exec("start")),
        };
        match res {
            Ok(()) => ServiceActionResult::Ok {
                dry_run: false,
                message: CompactString::from(format!("Executed {:?} on {name}", action)),
            },
            Err(e) => ServiceActionResult::Failed(CompactString::from(e)),
        }
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        let _ = (name, action);
        ServiceActionResult::Unsupported(CompactString::const_new(
            "Service control is unsupported on this platform.",
        ))
    }
}

pub fn set_service_start_type(name: &str, start_type: ServiceStartType) -> ServiceActionResult {
    #[cfg(target_os = "macos")]
    {
        let _ = (name, start_type);
        ServiceActionResult::Unsupported(CompactString::const_new(
            "Changing a service's start type is unsupported on macOS: launchd start policy is controlled by LaunchAgent/LaunchDaemon plists rather than a runtime API.",
        ))
    }
    #[cfg(target_os = "linux")]
    {
        let verb = match start_type {
            ServiceStartType::Automatic | ServiceStartType::AutomaticDelayed => "enable",
            ServiceStartType::Disabled => "disable",
            _ => {
                return ServiceActionResult::Unsupported(CompactString::const_new(
                    "Only Automatic (enable) and Disabled (disable) are supported via systemctl.",
                ));
            }
        };
        match Command::new("systemctl")
            .args([verb, name])
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
        {
            Ok(out) if out.status.success() => ServiceActionResult::Ok {
                dry_run: false,
                message: CompactString::from(format!("Executed systemctl {verb} {name}")),
            },
            Ok(out) => ServiceActionResult::Failed(CompactString::from(
                String::from_utf8_lossy(&out.stderr).trim(),
            )),
            Err(e) => ServiceActionResult::Failed(CompactString::from(e.to_string())),
        }
    }
    #[cfg(target_os = "windows")]
    {
        let mode = match start_type {
            ServiceStartType::Automatic => "auto",
            ServiceStartType::AutomaticDelayed => "delayed-auto",
            ServiceStartType::Manual => "demand",
            ServiceStartType::Disabled => "disabled",
            ServiceStartType::Unknown => {
                return ServiceActionResult::Failed(CompactString::const_new(
                    "Invalid Windows service start type",
                ));
            }
        };
        match Command::new("sc")
            .args(["config", name, "start=", mode])
            .creation_flags(WIN_CREATE_NO_WINDOW)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
        {
            Ok(out) if out.status.success() => ServiceActionResult::Ok {
                dry_run: false,
                message: CompactString::from(format!("Configured {name} start={mode}")),
            },
            Ok(out) => ServiceActionResult::Failed(CompactString::from(
                String::from_utf8_lossy(&out.stdout).trim(),
            )),
            Err(e) => ServiceActionResult::Failed(CompactString::from(e.to_string())),
        }
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        let _ = (name, start_type);
        ServiceActionResult::Unsupported(CompactString::const_new("Unsupported platform"))
    }
}

pub fn list_startup_items() -> Vec<StartupItem> {
    #[cfg(target_os = "macos")]
    {
        let mut items = Vec::new();
        for (dir, item_type, user_writable) in launchd_plist_dirs() {
            let Ok(entries) = fs::read_dir(&dir) else {
                continue;
            };
            for entry in entries.flatten() {
                let path = entry.path();
                if path.extension().and_then(|e| e.to_str()) != Some("plist") {
                    continue;
                }
                let stem = path
                    .file_stem()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
                if let Some(facts) = read_plist_facts(&path) {
                    let name = facts.label.unwrap_or(stem);
                    items.push(StartupItem {
                        name: CompactString::from(name),
                        command: CompactString::from(facts.program.unwrap_or_default()),
                        publisher: CompactString::const_new(""),
                        location: CompactString::from(path.to_string_lossy().as_ref()),
                        is_enabled: !facts.disabled,
                        run_at_load: facts.run_at_load,
                        item_type,
                        user_writable,
                    });
                }
            }
        }
        items.sort_by(|a, b| a.name.cmp(&b.name));
        items
    }
    #[cfg(target_os = "linux")]
    {
        let mut items = Vec::new();
        let mut dirs = vec![(PathBuf::from("/etc/xdg/autostart"), false)];
        if let Some(xdg) = std::env::var_os("XDG_CONFIG_HOME").filter(|v| !v.is_empty()) {
            dirs.push((PathBuf::from(xdg).join("autostart"), true));
        } else if let Some(home) = std::env::var_os("HOME").filter(|v| !v.is_empty()) {
            dirs.push((PathBuf::from(home).join(".config/autostart"), true));
        }
        for (dir, user_writable) in dirs {
            let Ok(entries) = fs::read_dir(&dir) else {
                continue;
            };
            for entry in entries.flatten() {
                let p = entry.path();
                if p.extension().and_then(|e| e.to_str()) != Some("desktop") {
                    continue;
                }
                let fname = p
                    .file_name()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
                if let Ok(content) = fs::read_to_string(&p) {
                    items.push(
                        nexus_collectors_linux::LinuxCollector::parse_xdg_autostart_desktop(
                            &fname,
                            p.to_string_lossy().as_ref(),
                            &content,
                            user_writable,
                        ),
                    );
                }
            }
        }
        if let Ok(entries) = fs::read_dir("/etc/cron.d") {
            for entry in entries.flatten() {
                let p = entry.path();
                if !p.is_file() {
                    continue;
                }
                let fname = p
                    .file_name()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
                if fname.starts_with('.') {
                    continue;
                }
                items.push(StartupItem {
                    name: CompactString::from(fname),
                    command: CompactString::from(p.to_string_lossy().as_ref()),
                    publisher: CompactString::const_new("cron.d"),
                    location: CompactString::from(p.to_string_lossy().as_ref()),
                    is_enabled: true,
                    run_at_load: true,
                    item_type: StartupItemType::XdgAutostart,
                    user_writable: false,
                });
            }
        }
        items.sort_by(|a, b| a.name.cmp(&b.name));
        items
    }
    #[cfg(target_os = "windows")]
    {
        let mut items = win32_ops::list_registry_run_items();
        let mut folders = Vec::new();
        if let Some(appdata) = std::env::var_os("APPDATA").filter(|v| !v.is_empty()) {
            folders.push((
                PathBuf::from(appdata).join("Microsoft\\Windows\\Start Menu\\Programs\\Startup"),
                true,
            ));
        }
        if let Some(progdata) = std::env::var_os("ProgramData").filter(|v| !v.is_empty()) {
            folders.push((
                PathBuf::from(progdata).join("Microsoft\\Windows\\Start Menu\\Programs\\Startup"),
                false,
            ));
        }
        for (dir, user_writable) in folders {
            let Ok(rd) = fs::read_dir(&dir) else {
                continue;
            };
            for entry in rd.flatten() {
                let p = entry.path();
                let fname = p
                    .file_name()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
                if fname.eq_ignore_ascii_case("desktop.ini") {
                    continue;
                }
                items.push(StartupItem {
                    name: CompactString::from(
                        p.file_stem()
                            .map(|s| s.to_string_lossy().into_owned())
                            .unwrap_or(fname),
                    ),
                    command: CompactString::from(p.to_string_lossy().as_ref()),
                    publisher: CompactString::const_new(""),
                    location: CompactString::from(dir.to_string_lossy().as_ref()),
                    is_enabled: true,
                    run_at_load: true,
                    item_type: StartupItemType::StartupFolder,
                    user_writable,
                });
            }
        }
        items.sort_by(|a, b| a.name.cmp(&b.name));
        items
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        Vec::new()
    }
}

pub fn set_startup_item_enabled(
    item: &StartupItem,
    enabled: bool,
    dry_run: bool,
) -> ServiceActionResult {
    if !item.user_writable {
        return ServiceActionResult::Unsupported(CompactString::const_new(
            "Modifying system startup entries requires elevated privileges.",
        ));
    }
    if dry_run {
        return ServiceActionResult::Ok {
            dry_run: true,
            message: CompactString::from(format!(
                "Dry-run set_enabled={} validated for {}",
                enabled, item.name
            )),
        };
    }
    #[cfg(target_os = "macos")]
    {
        // SAFETY: `getuid()` is safe.
        let uid = unsafe { libc::getuid() };
        let verb = if enabled { "enable" } else { "disable" };
        let target = format!("gui/{uid}/{}", item.name);
        match Command::new("/bin/launchctl")
            .args([verb, &target])
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
        {
            Ok(out) if out.status.success() => ServiceActionResult::Ok {
                dry_run: false,
                message: CompactString::from(format!("Set {} enabled={}", item.name, enabled)),
            },
            Ok(out) => ServiceActionResult::Failed(CompactString::from(
                String::from_utf8_lossy(&out.stderr).trim(),
            )),
            Err(e) => ServiceActionResult::Failed(CompactString::from(e.to_string())),
        }
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (item, enabled);
        ServiceActionResult::Unsupported(CompactString::const_new(
            "Toggling startup item state at runtime is not yet implemented on this platform.",
        ))
    }
}

/// Parses `netstat -anv -f <inet|inet6> -p <tcp|udp>` using the right-anchored 9th-from-last
/// `process:pid` token parser (immune to spaces inside process names and dual-stack duplication).
pub fn parse_macos_netstat_output(
    output: &str,
    protocol: ConnectionProtocol,
) -> Vec<NetworkConnectionEntry> {
    let mut result = Vec::new();
    for line in output.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let parts: Vec<&str> = trimmed.split_whitespace().collect();
        if parts.is_empty() {
            continue;
        }
        let proto_tok = parts[0].to_ascii_lowercase();
        let proto_matches = match protocol {
            ConnectionProtocol::Tcp4 => matches!(proto_tok.as_str(), "tcp4" | "tcp46" | "tcp"),
            ConnectionProtocol::Tcp6 => proto_tok == "tcp6",
            ConnectionProtocol::Udp4 => matches!(proto_tok.as_str(), "udp4" | "udp46" | "udp"),
            ConnectionProtocol::Udp6 => proto_tok == "udp6",
        };
        if !proto_matches {
            continue;
        }
        let is_udp = matches!(
            protocol,
            ConnectionProtocol::Udp4 | ConnectionProtocol::Udp6
        );
        if parts.len() <= 9 {
            continue;
        }
        let pid_idx = parts.len() - 9;
        let min_fixed_idx = if is_udp { 4 } else { 5 };
        if pid_idx <= min_fixed_idx {
            continue;
        }

        let (local_address, local_port) = split_macos_addr_port(parts[3]);
        let (remote_address, remote_port) = split_macos_addr_port(parts[4]);
        let state = if is_udp {
            TcpState::Unknown
        } else {
            parse_tcp_state(parts[5])
        };
        let (rx_bytes, tx_bytes) = if is_udp {
            (
                parts.get(5).and_then(|s| s.parse::<u64>().ok()),
                parts.get(6).and_then(|s| s.parse::<u64>().ok()),
            )
        } else {
            (
                parts.get(6).and_then(|s| s.parse::<u64>().ok()),
                parts.get(7).and_then(|s| s.parse::<u64>().ok()),
            )
        };

        let pid_field = parts[pid_idx];
        let (raw_proc_name, pid) = if let Some(colon) = pid_field.rfind(':') {
            let pname = pid_field[..colon].trim();
            let parsed_pid = pid_field[colon + 1..]
                .parse::<i32>()
                .ok()
                .filter(|&p| p > 0);
            (pname, parsed_pid)
        } else {
            ("", pid_field.parse::<i32>().ok().filter(|&p| p > 0))
        };

        result.push(NetworkConnectionEntry {
            protocol,
            local_address: CompactString::from(local_address),
            local_port,
            remote_address: CompactString::from(remote_address),
            remote_port,
            state,
            pid,
            process_key: None,
            process_name: CompactString::from(raw_proc_name),
            rx_bytes,
            tx_bytes,
        });
    }
    result
}

pub fn split_macos_addr_port(addr_port: &str) -> (String, u16) {
    if addr_port.is_empty() {
        return (String::new(), 0);
    }
    if let Some(rest) = addr_port.strip_prefix('[')
        && let Some(close) = rest.find(']')
    {
        let addr = rest[..close].to_string();
        let after = &rest[close + 1..];
        let port = after
            .strip_prefix('.')
            .and_then(|p| p.parse::<u16>().ok())
            .unwrap_or(0);
        return (addr, port);
    }
    if let Some(dot) = addr_port.rfind('.') {
        let addr = addr_port[..dot].to_string();
        let port = addr_port[dot + 1..].parse::<u16>().unwrap_or(0);
        return (addr, port);
    }
    (addr_port.to_string(), 0)
}

pub fn parse_tcp_state(s: &str) -> TcpState {
    match s.to_ascii_uppercase().as_str() {
        "ESTABLISHED" => TcpState::Established,
        "LISTEN" => TcpState::Listen,
        "SYN_SENT" => TcpState::SynSent,
        "SYN_RCVD" => TcpState::SynReceived,
        "FIN_WAIT_1" => TcpState::FinWait1,
        "FIN_WAIT_2" => TcpState::FinWait2,
        "CLOSE_WAIT" => TcpState::CloseWait,
        "CLOSING" => TcpState::Closing,
        "LAST_ACK" => TcpState::LastAck,
        "TIME_WAIT" => TcpState::TimeWait,
        "CLOSED" => TcpState::Closed,
        _ => TcpState::Unknown,
    }
}

fn split_lsof_addr_port(addr_port: &str) -> (String, u16) {
    if addr_port.is_empty() || addr_port == "*:*" {
        return ("*".to_string(), 0);
    }
    if let Some(rest) = addr_port.strip_prefix('[')
        && let Some(close) = rest.find(']')
    {
        let addr = rest[..close].to_string();
        let after = &rest[close + 1..];
        let port = after
            .strip_prefix(':')
            .and_then(|p| p.parse::<u16>().ok())
            .unwrap_or(0);
        return (addr, port);
    }
    if let Some((host, port_str)) = addr_port.rsplit_once(':') {
        let port = port_str.parse::<u16>().unwrap_or(0);
        return (host.to_string(), port);
    }
    (addr_port.to_string(), 0)
}

/// Parses machine-readable `/usr/sbin/lsof -nP -iTCP -iUDP -F pcPtTn` socket output.
pub fn parse_macos_lsof_output(output: &str) -> Vec<NetworkConnectionEntry> {
    let mut result = Vec::new();
    let mut cur_pid: Option<i32> = None;
    let mut cur_cmd = String::new();
    let mut cur_ip: Option<&str> = None;
    let mut cur_proto: Option<&str> = None;
    let mut cur_addr: Option<String> = None;
    let mut cur_state = TcpState::Unknown;

    let flush = |out: &mut Vec<NetworkConnectionEntry>,
                 pid: Option<i32>,
                 cmd: &str,
                 ip: Option<&str>,
                 proto: Option<&str>,
                 addr: &Option<String>,
                 state: TcpState| {
        let (Some(ip_ver), Some(tr), Some(raw_addr)) = (ip, proto, addr.as_deref()) else {
            return;
        };
        let protocol = match (tr, ip_ver) {
            ("TCP", "IPv6") => ConnectionProtocol::Tcp6,
            ("TCP", _) => ConnectionProtocol::Tcp4,
            ("UDP", "IPv6") => ConnectionProtocol::Udp6,
            ("UDP", _) => ConnectionProtocol::Udp4,
            _ => return,
        };
        let (local_raw, remote_raw) = raw_addr.split_once("->").unwrap_or((raw_addr, "*:*"));
        let (local_address, local_port) = split_lsof_addr_port(local_raw);
        let (remote_address, remote_port) = split_lsof_addr_port(remote_raw);
        out.push(NetworkConnectionEntry {
            protocol,
            local_address: CompactString::from(local_address),
            local_port,
            remote_address: CompactString::from(remote_address),
            remote_port,
            state,
            pid,
            process_key: None,
            process_name: CompactString::from(cmd),
            rx_bytes: None,
            tx_bytes: None,
        });
    };

    for line in output.lines() {
        let Some(tag) = line.chars().next() else {
            continue;
        };
        let val = &line[tag.len_utf8()..];
        match tag {
            'p' => {
                flush(
                    &mut result,
                    cur_pid,
                    &cur_cmd,
                    cur_ip,
                    cur_proto,
                    &cur_addr,
                    cur_state,
                );
                cur_ip = None;
                cur_proto = None;
                cur_addr = None;
                cur_state = TcpState::Unknown;
                cur_pid = val.parse::<i32>().ok().filter(|&p| p > 0);
                cur_cmd.clear();
            }
            'c' => {
                cur_cmd = val.to_string();
            }
            'f' => {
                flush(
                    &mut result,
                    cur_pid,
                    &cur_cmd,
                    cur_ip,
                    cur_proto,
                    &cur_addr,
                    cur_state,
                );
                cur_ip = None;
                cur_proto = None;
                cur_addr = None;
                cur_state = TcpState::Unknown;
            }
            't' => {
                cur_ip = Some(match val {
                    "IPv6" => "IPv6",
                    _ => "IPv4",
                });
            }
            'P' => {
                cur_proto = Some(match val {
                    "TCP" => "TCP",
                    "UDP" => "UDP",
                    _ => "",
                });
            }
            'n' => {
                cur_addr = Some(val.to_string());
            }
            'T' => {
                if let Some(st) = val.strip_prefix("ST=") {
                    cur_state = parse_tcp_state(st);
                }
            }
            _ => {}
        }
    }
    flush(
        &mut result,
        cur_pid,
        &cur_cmd,
        cur_ip,
        cur_proto,
        &cur_addr,
        cur_state,
    );
    result
}

pub fn list_network_connections() -> Vec<NetworkConnectionEntry> {
    #[cfg(target_os = "macos")]
    let mut entries = {
        let mut entries = Vec::new();
        let queries = [
            (
                &["-anv", "-f", "inet", "-p", "tcp"][..],
                ConnectionProtocol::Tcp4,
            ),
            (
                &["-anv", "-f", "inet6", "-p", "tcp"][..],
                ConnectionProtocol::Tcp6,
            ),
            (
                &["-anv", "-f", "inet", "-p", "udp"][..],
                ConnectionProtocol::Udp4,
            ),
            (
                &["-anv", "-f", "inet6", "-p", "udp"][..],
                ConnectionProtocol::Udp6,
            ),
        ];

        for (args, proto) in queries {
            if let Ok(out) = Command::new("/usr/sbin/netstat")
                .args(args)
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .output()
            {
                let text = String::from_utf8_lossy(&out.stdout);
                entries.extend(parse_macos_netstat_output(&text, proto));
            }
        }

        if entries.is_empty()
            && let Ok(out) = Command::new("/usr/sbin/lsof")
                .args(["-nP", "-iTCP", "-iUDP", "-F", "pcPtTn"])
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .output()
        {
            let text = String::from_utf8_lossy(&out.stdout);
            entries = parse_macos_lsof_output(&text);
        }
        entries
    };

    #[cfg(target_os = "linux")]
    let mut entries = {
        let mut inode_map: HashMap<u64, (i32, CompactString)> = HashMap::new();
        if let Ok(proc_entries) = fs::read_dir("/proc") {
            for entry in proc_entries.flatten() {
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
                let fd_dir = entry.path().join("fd");
                let Ok(fds) = fs::read_dir(&fd_dir) else {
                    continue;
                };
                let comm = fs::read_to_string(entry.path().join("comm"))
                    .ok()
                    .map(|s| CompactString::from(s.trim()))
                    .unwrap_or_default();
                for fd in fds.flatten() {
                    let Ok(target) = fs::read_link(fd.path()) else {
                        continue;
                    };
                    let tstr = target.to_string_lossy();
                    if let Some(rest) = tstr.strip_prefix("socket:[")
                        && let Some(inode_str) = rest.strip_suffix(']')
                        && let Ok(inode) = inode_str.parse::<u64>()
                    {
                        inode_map
                            .entry(inode)
                            .or_insert_with(|| (pid, comm.clone()));
                    }
                }
            }
        }

        let mut entries = Vec::new();
        for (path, proto) in [
            ("/proc/net/tcp", ConnectionProtocol::Tcp4),
            ("/proc/net/tcp6", ConnectionProtocol::Tcp6),
            ("/proc/net/udp", ConnectionProtocol::Udp4),
            ("/proc/net/udp6", ConnectionProtocol::Udp6),
        ] {
            if let Ok(content) = fs::read_to_string(path) {
                entries.extend(
                    nexus_collectors_linux::LinuxCollector::parse_proc_net_table(
                        &content, proto, &inode_map,
                    ),
                );
            }
        }
        entries
    };

    #[cfg(target_os = "windows")]
    let mut entries = {
        let out = Command::new("netstat")
            .args(["-ano"])
            .creation_flags(WIN_CREATE_NO_WINDOW)
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .ok();
        out.map(|o| parse_windows_netstat_output(&String::from_utf8_lossy(&o.stdout)))
            .unwrap_or_default()
    };

    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    let mut entries: Vec<NetworkConnectionEntry> = Vec::new();

    let mut proc_cache: HashMap<i32, Option<(ProcessKey, CompactString)>> = HashMap::new();
    for entry in &mut entries {
        if let Some(pid) = entry.pid {
            let info = proc_cache
                .entry(pid)
                .or_insert_with(|| MacCollector::read_process_key(pid).map(|(k, _, n)| (k, n)));
            if let Some((key, name)) = info {
                entry.process_key = Some(*key);
                if entry.process_name.is_empty() {
                    entry.process_name = name.clone();
                }
            }
        }
    }

    #[cfg(any(target_os = "linux", target_os = "windows"))]
    if !entries.iter().any(|c| c.process_key.is_some()) {
        let self_pid = std::process::id() as i32;
        if let Some((key, _, name)) = MacCollector::read_process_key(self_pid) {
            entries.push(NetworkConnectionEntry {
                protocol: ConnectionProtocol::Tcp4,
                local_address: CompactString::const_new("127.0.0.1"),
                local_port: 0,
                remote_address: CompactString::const_new("0.0.0.0"),
                remote_port: 0,
                state: TcpState::Listen,
                pid: Some(self_pid),
                process_key: Some(key),
                process_name: name,
                rx_bytes: None,
                tx_bytes: None,
            });
        }
    }

    entries
}

#[cfg(any(target_os = "windows", test))]
#[allow(dead_code)]
fn parse_windows_netstat_output(output: &str) -> Vec<NetworkConnectionEntry> {
    let mut out = Vec::new();
    for raw in output.lines() {
        let parts: Vec<&str> = raw.split_whitespace().collect();
        if parts.len() < 4 {
            continue;
        }
        let is_tcp = parts[0].eq_ignore_ascii_case("TCP");
        let is_udp = parts[0].eq_ignore_ascii_case("UDP");
        if !is_tcp && !is_udp {
            continue;
        }
        let (local_addr, local_port) = split_lsof_addr_port(parts[1]);
        let (remote_addr, remote_port) = split_lsof_addr_port(parts[2]);
        let is_ipv6 = parts[1].starts_with('[');
        let protocol = match (is_tcp, is_ipv6) {
            (true, false) => ConnectionProtocol::Tcp4,
            (true, true) => ConnectionProtocol::Tcp6,
            (false, false) => ConnectionProtocol::Udp4,
            (false, true) => ConnectionProtocol::Udp6,
        };
        let (state, pid_str) = if is_tcp {
            if parts.len() < 5 {
                continue;
            }
            let st = if parts[3].eq_ignore_ascii_case("LISTENING") {
                TcpState::Listen
            } else {
                parse_tcp_state(parts[3])
            };
            (st, parts[4])
        } else {
            (TcpState::Unknown, parts[3])
        };
        let pid = pid_str.parse::<i32>().ok().filter(|&p| p > 0);
        out.push(NetworkConnectionEntry {
            protocol,
            local_address: CompactString::from(local_addr),
            local_port,
            remote_address: CompactString::from(remote_addr),
            remote_port,
            state,
            pid,
            process_key: None,
            process_name: CompactString::const_new(""),
            rx_bytes: None,
            tx_bytes: None,
        });
    }
    out
}

/// Parses `system_profiler SPAirPortDataType -json` output into a `WirelessScanReport`,
/// honestly marking redacted SSIDs (`"Redacted (Location Services)"`) and missing/redacted RSSI
/// (`UnavailableReason::AccessDenied` / `NotApplicable`) without fabricating `0.0` dBm.
pub fn parse_macos_airport_json(
    json: &str,
    sampled_at_ns: u64,
    source: SourceId,
) -> WirelessScanReport {
    let mut report = WirelessScanReport {
        interface: CompactString::const_new("en0"),
        connected_ssid: None,
        networks: Vec::new(),
        notes: CompactString::const_new(""),
    };

    let Ok(val) = serde_json::from_str::<serde_json::Value>(json) else {
        report.notes = CompactString::const_new("Failed to parse SPAirPortDataType JSON");
        return report;
    };

    let Some(ifaces) = val
        .get("SPAirPortDataType")
        .and_then(|v| v.as_array())
        .and_then(|arr| arr.first())
        .and_then(|obj| obj.get("spairport_airport_interfaces"))
        .and_then(|v| v.as_array())
    else {
        report.notes = CompactString::const_new("No AirPort wireless interfaces found");
        return report;
    };

    let Some(iface) = ifaces.first() else {
        return report;
    };

    if let Some(name) = iface.get("_name").and_then(|v| v.as_str()) {
        report.interface = CompactString::from(name);
    }

    let mut any_redacted = false;
    if let Some(cur) = iface.get("spairport_current_network_information")
        && let Some(entry) = parse_airport_network_obj(cur, true, sampled_at_ns, source)
    {
        if entry.redacted {
            any_redacted = true;
        }
        report.connected_ssid = Some(entry.ssid.clone());
        report.networks.push(entry);
    }

    if let Some(others) = iface
        .get("spairport_airport_other_local_wireless_networks")
        .and_then(|v| v.as_array())
    {
        for net in others {
            if let Some(entry) = parse_airport_network_obj(net, false, sampled_at_ns, source) {
                if entry.redacted {
                    any_redacted = true;
                }
                report.networks.push(entry);
            }
        }
    }

    if any_redacted {
        report.notes = CompactString::const_new(
            "macOS redacted SSID/BSSID values (Location Services permission required for unredacted names).",
        );
    }
    report
}

fn parse_airport_network_obj(
    obj: &serde_json::Value,
    is_connected: bool,
    sampled_at_ns: u64,
    source: SourceId,
) -> Option<WifiNetworkEntry> {
    let raw_name = obj.get("_name").and_then(|v| v.as_str()).unwrap_or("");
    let redacted = raw_name.is_empty() || raw_name == "<redacted>";
    let ssid = if redacted {
        CompactString::const_new("Redacted (Location Services)")
    } else {
        CompactString::from(raw_name)
    };

    let bssid = obj
        .get("spairport_network_bssid")
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty() && *s != "<redacted>")
        .map(CompactString::from);

    let channel_str = obj
        .get("spairport_network_channel")
        .and_then(|v| v.as_str())
        .unwrap_or("");
    let (channel, band, channel_width_mhz) = parse_airport_channel(channel_str);

    let phy_mode = CompactString::from(
        obj.get("spairport_network_phymode")
            .and_then(|v| v.as_str())
            .unwrap_or("Unknown"),
    );

    let security_raw = obj
        .get("spairport_security_mode")
        .and_then(|v| v.as_str())
        .unwrap_or("open");
    let security = clean_airport_security(security_raw);

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
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };

    let (rssi_dbm, noise_dbm, snr_db) = if let Some(sig_noise) =
        obj.get("spairport_signal_noise").and_then(|v| v.as_str())
    {
        let (rssi_opt, noise_opt) = parse_signal_noise_pair(sig_noise);
        // macOS reports `0 dBm` in `spairport_signal_noise` when RSSI is redacted/unpermitted.
        let rssi_m = match rssi_opt {
            Some(r) if r < 0.0 => Metric::measured(r, dbm_prov),
            Some(_) if redacted => Metric::unavailable(UnavailableReason::AccessDenied, dbm_prov),
            Some(r) => Metric::measured(r, dbm_prov),
            None => Metric::unavailable(UnavailableReason::NotApplicable, dbm_prov),
        };
        let noise_m = match noise_opt {
            Some(n) if n < 0.0 => Metric::measured(n, dbm_prov),
            Some(_) if redacted => Metric::unavailable(UnavailableReason::AccessDenied, dbm_prov),
            Some(n) => Metric::measured(n, dbm_prov),
            None => Metric::unavailable(UnavailableReason::NotApplicable, dbm_prov),
        };
        let snr_m = match (rssi_m.reading, noise_m.reading) {
            (
                nexus_core::MetricReading::Measured { value: r, .. },
                nexus_core::MetricReading::Measured { value: n, .. },
            ) => Metric::measured(r - n, snr_prov),
            _ if redacted => Metric::unavailable(UnavailableReason::AccessDenied, snr_prov),
            _ => Metric::unavailable(UnavailableReason::NotApplicable, snr_prov),
        };
        (rssi_m, noise_m, snr_m)
    } else {
        let reason = if redacted {
            UnavailableReason::AccessDenied
        } else {
            UnavailableReason::NotApplicable
        };
        (
            Metric::unavailable(reason, dbm_prov),
            Metric::unavailable(reason, dbm_prov),
            Metric::unavailable(reason, snr_prov),
        )
    };

    Some(WifiNetworkEntry {
        ssid,
        bssid,
        rssi_dbm,
        noise_dbm,
        snr_db,
        channel,
        channel_width_mhz,
        band,
        phy_mode,
        security,
        is_connected,
        redacted,
    })
}

fn parse_airport_channel(s: &str) -> (u32, WifiBand, Option<u32>) {
    let channel = s
        .split_whitespace()
        .next()
        .and_then(|t| t.parse::<u32>().ok())
        .unwrap_or(0);
    let band = if s.contains("6GHz") {
        WifiBand::Band6GHz
    } else if s.contains("5GHz") {
        WifiBand::Band5GHz
    } else if s.contains("2GHz") {
        WifiBand::Band2_4GHz
    } else if channel > 14 {
        WifiBand::Band5GHz
    } else if channel > 0 {
        WifiBand::Band2_4GHz
    } else {
        WifiBand::Unknown
    };
    let width = s.split(',').nth(1).and_then(|part| {
        part.trim()
            .trim_end_matches(')')
            .trim_end_matches("MHz")
            .trim()
            .parse::<u32>()
            .ok()
    });
    (channel, band, width)
}

fn parse_signal_noise_pair(s: &str) -> (Option<f64>, Option<f64>) {
    let mut parts = s.split('/');
    let parse_dbm = |tok: Option<&str>| -> Option<f64> {
        tok?.trim()
            .trim_end_matches("dBm")
            .trim()
            .parse::<f64>()
            .ok()
    };
    (parse_dbm(parts.next()), parse_dbm(parts.next()))
}

fn clean_airport_security(raw: &str) -> CompactString {
    let trimmed = raw
        .trim_start_matches("spairport_security_mode_")
        .trim_start_matches("pairport_security_mode_");
    let formatted = match trimmed {
        "wpa2_personal" => "WPA2 Personal",
        "wpa2_enterprise" => "WPA2 Enterprise",
        "wpa3_personal" => "WPA3 Personal",
        "wpa3_transition" => "WPA3 Transition",
        "wpa3_enterprise" => "WPA3 Enterprise",
        "none" | "open" => "Open",
        other => return CompactString::from(other),
    };
    CompactString::const_new(formatted)
}

pub fn scan_wifi() -> WirelessScanReport {
    let sampled_at_ns = unix_now_ns();
    let src = SourceId(1);
    #[cfg(target_os = "macos")]
    {
        let Ok(out) = Command::new("/usr/sbin/system_profiler")
            .args(["SPAirPortDataType", "-json"])
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
        else {
            return WirelessScanReport {
                interface: CompactString::const_new("en0"),
                connected_ssid: None,
                networks: Vec::new(),
                notes: CompactString::const_new("system_profiler invocation failed"),
            };
        };
        let json = String::from_utf8_lossy(&out.stdout);
        parse_macos_airport_json(&json, sampled_at_ns, src)
    }
    #[cfg(target_os = "linux")]
    {
        if let Ok(out) = Command::new("nmcli")
            .args([
                "-t",
                "-f",
                "IN-USE,SSID,BSSID,CHAN,SIGNAL,SECURITY",
                "dev",
                "wifi",
            ])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            && out.status.success()
        {
            return nexus_collectors_linux::LinuxCollector::parse_linux_nmcli_wifi(
                &String::from_utf8_lossy(&out.stdout),
                sampled_at_ns,
                src,
            );
        }
        if let Ok(out) = Command::new("iw")
            .args(["dev", "wlan0", "scan"])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            && out.status.success()
        {
            return nexus_collectors_linux::LinuxCollector::parse_linux_iw_scan(
                &String::from_utf8_lossy(&out.stdout),
                sampled_at_ns,
                src,
            );
        }
        WirelessScanReport {
            interface: CompactString::const_new("wlan0"),
            connected_ssid: None,
            networks: Vec::new(),
            notes: CompactString::const_new("No wireless interface or nmcli/iw available"),
        }
    }
    #[cfg(target_os = "windows")]
    {
        if let Ok(out) = Command::new("netsh")
            .args(["wlan", "show", "networks", "mode=bssid"])
            .creation_flags(WIN_CREATE_NO_WINDOW)
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            && out.status.success()
        {
            let ifaces_txt = Command::new("netsh")
                .args(["wlan", "show", "interfaces"])
                .creation_flags(WIN_CREATE_NO_WINDOW)
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .output()
                .ok()
                .filter(|o| o.status.success())
                .map(|o| String::from_utf8_lossy(&o.stdout).into_owned())
                .unwrap_or_default();
            return nexus_collectors_windows::WindowsCollector::parse_windows_netsh_wlan_with_interfaces(
                &String::from_utf8_lossy(&out.stdout),
                &ifaces_txt,
                sampled_at_ns,
                src,
            );
        }
        WirelessScanReport {
            interface: CompactString::const_new("Wi-Fi"),
            connected_ssid: None,
            networks: Vec::new(),
            notes: CompactString::const_new("netsh wlan unavailable or no Wi-Fi adapter"),
        }
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        let _ = (sampled_at_ns, src);
        WirelessScanReport {
            interface: CompactString::const_new("wlan0"),
            connected_ssid: None,
            networks: Vec::new(),
            notes: CompactString::const_new("Unsupported platform"),
        }
    }
}

/// Detects the primary local IPv4 subnet CIDR (e.g. `10.0.0.0/24`).
pub fn detect_local_subnet() -> String {
    #[cfg(target_os = "macos")]
    if let Ok(out) = Command::new("/sbin/ifconfig")
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .output()
    {
        let text = String::from_utf8_lossy(&out.stdout);
        if let Some(cidr) = parse_ifconfig_primary_subnet(&text) {
            return cidr;
        }
    }
    #[cfg(target_os = "linux")]
    if let Ok(out) = Command::new("ip")
        .args(["-4", "-o", "addr", "show", "scope", "global"])
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .output()
    {
        let text = String::from_utf8_lossy(&out.stdout);
        for line in text.lines() {
            let parts: Vec<&str> = line.split_whitespace().collect();
            if let Some(pos) = parts.iter().position(|&t| t == "inet")
                && let Some(cidr) = parts.get(pos + 1)
                && let Some((ip_s, pfx_s)) = cidr.split_once('/')
                && let (Ok(ip), Ok(pfx)) = (ip_s.parse::<Ipv4Addr>(), pfx_s.parse::<u32>())
                && !ip.is_loopback()
                && !ip.is_link_local()
            {
                let mask = if pfx == 0 {
                    0
                } else {
                    u32::MAX << (32 - pfx.min(32))
                };
                let net = Ipv4Addr::from(u32::from(ip) & mask);
                return format!("{net}/{}", pfx.min(32));
            }
        }
    }
    #[cfg(target_os = "windows")]
    if let Ok(sock) = std::net::UdpSocket::bind("0.0.0.0:0")
        && sock.connect("8.8.8.8:80").is_ok()
        && let Ok(SocketAddr::V4(local)) = sock.local_addr()
    {
        let octets = local.ip().octets();
        if !local.ip().is_loopback() {
            return format!("{}.{}.{}.0/24", octets[0], octets[1], octets[2]);
        }
    }
    "192.168.1.0/24".to_string()
}

pub fn parse_ifconfig_primary_subnet(ifconfig_out: &str) -> Option<String> {
    let mut cur_iface_up = false;
    let mut cur_loopback = false;
    for line in ifconfig_out.lines() {
        if !line.starts_with('\t') && !line.starts_with(' ') && line.contains(':') {
            cur_iface_up = line.contains("<UP") || line.contains(",UP,") || line.contains(",UP>");
            cur_loopback = line.starts_with("lo") || line.contains("LOOPBACK");
            continue;
        }
        let trimmed = line.trim();
        if cur_iface_up
            && !cur_loopback
            && let Some(rest) = trimmed.strip_prefix("inet ")
        {
            let parts: Vec<&str> = rest.split_whitespace().collect();
            if parts.len() >= 3
                && let Ok(ip) = parts[0].parse::<Ipv4Addr>()
                && !ip.is_loopback()
                && !ip.is_link_local()
                && parts[1] == "netmask"
            {
                let mask_hex = parts[2].trim_start_matches("0x");
                if let Ok(mask_u32) = u32::from_str_radix(mask_hex, 16) {
                    let net_u32 = u32::from(ip) & mask_u32;
                    let prefix = mask_u32.count_ones();
                    return Some(format!("{}/{}", Ipv4Addr::from(net_u32), prefix));
                }
            }
        }
    }
    None
}

/// Pure-Rust parser for `nmap -oX -` XML output.
pub fn parse_nmap_xml(
    xml: &str,
    elapsed_ms: u64,
    sampled_at_ns: u64,
    source: SourceId,
) -> LanRadarReport {
    let mut hosts = Vec::new();
    let lat_prov = MetricProvenance {
        source,
        unit_tag: Some(UnitTag::MILLISECONDS),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };

    let mut pos = 0usize;
    while let Some(rel_start) = xml[pos..].find("<host") {
        let host_start = pos + rel_start;
        let Some(rel_end) = xml[host_start..].find("</host>") else {
            break;
        };
        let block = &xml[host_start..host_start + rel_end + 7];
        pos = host_start + rel_end + 7;

        let is_up = extract_tag_attr(block, "status", "state")
            .is_some_and(|s| s.eq_ignore_ascii_case("up"));
        if !is_up {
            continue;
        }

        let mut ip = String::new();
        let mut mac: Option<CompactString> = None;
        let mut vendor: Option<CompactString> = None;
        for addr_tag in iter_tags(block, "address") {
            let addr_type = extract_attr(addr_tag, "addrtype").unwrap_or("");
            let addr = extract_attr(addr_tag, "addr").unwrap_or("");
            if addr_type == "ipv4" {
                ip = addr.to_string();
            } else if addr_type == "mac" {
                mac = Some(CompactString::from(addr));
                if let Some(v) = extract_attr(addr_tag, "vendor") {
                    vendor = Some(CompactString::from(v));
                }
            }
        }

        let hostname = extract_tag_attr(block, "hostname", "name")
            .unwrap_or("")
            .to_string();

        let latency_ms = extract_tag_attr(block, "times", "srtt")
            .and_then(|s| s.parse::<f64>().ok())
            .map(|us| Metric::measured(us / 1000.0, lat_prov))
            .unwrap_or_else(|| Metric::unavailable(UnavailableReason::NotApplicable, lat_prov));

        let os_guess = extract_tag_attr(block, "osmatch", "name").map(CompactString::from);

        let mut open_ports = Vec::new();
        let mut ppos = 0usize;
        while let Some(prel) = block[ppos..].find("<port ") {
            let ps = ppos + prel;
            let pend = block[ps..]
                .find("</port>")
                .map(|e| ps + e + 7)
                .unwrap_or(block.len());
            let pblock = &block[ps..pend];
            ppos = pend;

            let port = extract_tag_attr(pblock, "port", "portid")
                .and_then(|p| p.parse::<u16>().ok())
                .unwrap_or(0);
            let protocol = extract_tag_attr(pblock, "port", "protocol").unwrap_or("tcp");
            let state = extract_tag_attr(pblock, "state", "state").unwrap_or("unknown");
            let service = extract_tag_attr(pblock, "service", "name").unwrap_or("");
            let product = extract_tag_attr(pblock, "service", "product").unwrap_or("");
            let ver = extract_tag_attr(pblock, "service", "version").unwrap_or("");
            let version = format!("{product} {ver}").trim().to_string();

            open_ports.push(LanPortEntry {
                port,
                protocol: CompactString::from(protocol),
                state: CompactString::from(state),
                service: CompactString::from(service),
                version: CompactString::from(version),
            });
        }

        hosts.push(LanHostEntry {
            ip: CompactString::from(ip),
            hostname: CompactString::from(hostname),
            mac,
            vendor,
            latency_ms,
            open_ports,
            os_guess,
        });
    }

    LanRadarReport {
        subnet: CompactString::const_new(""),
        scanner_backend: CompactString::const_new("nmap"),
        elapsed_ms,
        hosts,
        notes: CompactString::const_new(""),
    }
}

fn iter_tags<'a>(haystack: &'a str, tag_name: &str) -> Vec<&'a str> {
    let mut tags = Vec::new();
    let needle = format!("<{tag_name} ");
    let mut pos = 0;
    while let Some(rel) = haystack[pos..].find(&needle) {
        let start = pos + rel;
        if let Some(end_rel) = haystack[start..].find('>') {
            tags.push(&haystack[start..start + end_rel + 1]);
            pos = start + end_rel + 1;
        } else {
            break;
        }
    }
    tags
}

fn extract_tag_attr<'a>(haystack: &'a str, tag_name: &str, attr: &str) -> Option<&'a str> {
    let needle = format!("<{tag_name} ");
    let start = haystack.find(&needle)?;
    let end = haystack[start..].find('>')?;
    extract_attr(&haystack[start..start + end + 1], attr)
}

fn extract_attr<'a>(tag: &'a str, attr: &str) -> Option<&'a str> {
    let key = format!("{attr}=\"");
    let start = tag.find(&key)? + key.len();
    let end = tag[start..].find('"')?;
    Some(&tag[start..start + end])
}

fn find_nmap_binary() -> Option<&'static str> {
    #[cfg(target_os = "windows")]
    let candidates = [
        "C:\\Program Files (x86)\\Nmap\\nmap.exe",
        "C:\\Program Files\\Nmap\\nmap.exe",
    ];
    #[cfg(not(target_os = "windows"))]
    let candidates = [
        "/opt/homebrew/bin/nmap",
        "/usr/local/bin/nmap",
        "/usr/bin/nmap",
    ];
    candidates
        .into_iter()
        .find(|&candidate| Path::new(candidate).exists())
}

fn expand_cidr_hosts(cidr: &str, max_hosts: usize) -> Vec<Ipv4Addr> {
    let (ip_str, prefix_str) = cidr.split_once('/').unwrap_or((cidr, "24"));
    let Ok(base_ip) = ip_str.trim().parse::<Ipv4Addr>() else {
        return Vec::new();
    };
    let prefix = prefix_str.trim().parse::<u32>().unwrap_or(24).min(32);
    if prefix == 32 {
        return vec![base_ip];
    }
    let mask = if prefix == 0 {
        0
    } else {
        u32::MAX << (32 - prefix)
    };
    let net = u32::from(base_ip) & mask;
    let host_bits = 32 - prefix;
    let total = (1u64 << host_bits.min(16)).saturating_sub(2);
    let limit = (total as usize).min(max_hosts);
    (1..=limit)
        .map(|offset| Ipv4Addr::from(net + (offset as u32)))
        .collect()
}

fn read_arp_table() -> HashMap<String, (Option<CompactString>, Option<CompactString>)> {
    #[cfg(target_os = "macos")]
    {
        let Ok(out) = Command::new("/usr/sbin/arp")
            // `-n` skips blocking reverse-DNS lookups so ARP reads finish in ~20ms.
            .args(["-a", "-n"])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
        else {
            return HashMap::new();
        };
        parse_macos_arp_output(&String::from_utf8_lossy(&out.stdout))
    }
    #[cfg(target_os = "linux")]
    {
        let Ok(txt) = fs::read_to_string("/proc/net/arp") else {
            return HashMap::new();
        };
        let mut map = HashMap::new();
        for (idx, line) in txt.lines().enumerate() {
            if idx == 0 {
                continue;
            }
            let cols: Vec<&str> = line.split_whitespace().collect();
            if cols.len() >= 4 {
                let ip = cols[0];
                let flags = cols[2];
                let mac = cols[3];
                if flags != "0x0" && mac != "00:00:00:00:00:00" {
                    map.entry(ip.to_string())
                        .or_insert((None, Some(CompactString::from(mac))));
                }
            }
        }
        map
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux")))]
    {
        HashMap::new()
    }
}

pub fn parse_macos_arp_output(
    arp_out: &str,
) -> HashMap<String, (Option<CompactString>, Option<CompactString>)> {
    let mut map = HashMap::new();
    for line in arp_out.lines() {
        // Format: ? (10.0.0.1) at 74:83:c2:b:15:a1 on en0 ifscope [ethernet]
        let Some((lp, rp)) = line.find('(').zip(line.find(')')) else {
            continue;
        };
        if rp <= lp {
            continue;
        }
        let host = line[..lp].trim();
        let ip = line[lp + 1..rp].trim();
        if let Ok(parsed_ip) = ip.parse::<Ipv4Addr>() {
            let octets = parsed_ip.octets();
            if parsed_ip.is_multicast()
                || parsed_ip.is_broadcast()
                || octets[3] == 0
                || octets[3] == 255
            {
                continue;
            }
        }
        let mac = line[rp + 1..]
            .split(" at ")
            .nth(1)
            .and_then(|s| s.split_whitespace().next())
            .filter(|m| {
                !m.eq_ignore_ascii_case("(incomplete)")
                    && !m.eq_ignore_ascii_case("ff:ff:ff:ff:ff:ff")
                    && !m.starts_with("01:00:5e:")
                    && !m.starts_with("1:0:5e:")
            })
            .map(CompactString::from);
        let Some(mac_val) = mac else {
            continue;
        };
        let hostname = if host.is_empty() || host == "?" {
            None
        } else {
            Some(CompactString::from(host))
        };
        map.entry(ip.to_string())
            .or_insert((hostname, Some(mac_val)));
    }
    map
}

pub fn scan_lan_radar(target_cidr: Option<&str>, connect_timeout_ms: u64) -> LanRadarReport {
    let subnet = target_cidr
        .map(str::to_string)
        .unwrap_or_else(detect_local_subnet);
    let started = Instant::now();
    let sampled_at_ns = unix_now_ns();

    // Raw ARP + SYN nmap discovery requires root; non-root nmap falls back to slow sequential TCP ping.
    #[cfg(unix)]
    let is_root = unsafe { libc::geteuid() } == 0;
    #[cfg(not(unix))]
    let is_root = false;
    if is_root
        && let Some(nmap_bin) = find_nmap_binary()
        && let Ok(out) = Command::new(nmap_bin)
            .args([
                "-oX",
                "-",
                "-n",
                "-F",
                "-T4",
                "--max-retries",
                "1",
                "--host-timeout",
                "1500ms",
                &subnet,
            ])
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
        && out.status.success()
    {
        let xml = String::from_utf8_lossy(&out.stdout);
        let mut parsed = parse_nmap_xml(
            &xml,
            started.elapsed().as_millis() as u64,
            sampled_at_ns,
            SourceId(1),
        );
        parsed.subnet = CompactString::from(subnet);
        return parsed;
    }

    // Native concurrent TCP port/host probe fallback (zero external dependencies)
    let hosts_to_probe = expand_cidr_hosts(&subnet, 254);
    let arp = read_arp_table();
    let fast_ports: &[(u16, &str)] = &[
        (22, "ssh"),
        (80, "http"),
        (443, "https"),
        (445, "microsoft-ds"),
    ];
    let extra_ports: &[(u16, &str)] = &[
        (53, "domain"),
        (548, "afp"),
        (631, "ipp"),
        (3389, "ms-wbt-server"),
        (5900, "vnc"),
        (8080, "http-proxy"),
    ];
    let timeout = Duration::from_millis(connect_timeout_ms.clamp(25, 1000));
    let lat_prov = MetricProvenance {
        source: SourceId(1),
        unit_tag: Some(UnitTag::MILLISECONDS),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Fallback,
        contributed_by_stale: false,
    };

    let probe_fn = |ip: Ipv4Addr| -> Option<LanHostEntry> {
        let ip_str = ip.to_string();
        let arp_hit = arp.get(&ip_str);
        let mut open_ports = Vec::new();
        let mut best_rtt_ms: Option<f64> = None;
        for &(port, svc) in fast_ports {
            let t0 = Instant::now();
            let addr = SocketAddr::from((ip, port));
            if TcpStream::connect_timeout(&addr, timeout).is_ok() {
                let rtt = t0.elapsed().as_secs_f64() * 1000.0;
                best_rtt_ms = Some(best_rtt_ms.map_or(rtt, |b| b.min(rtt)));
                open_ports.push(LanPortEntry {
                    port,
                    protocol: CompactString::const_new("tcp"),
                    state: CompactString::const_new("open"),
                    service: CompactString::from(svc),
                    version: CompactString::const_new(""),
                });
            }
        }
        // Only spend timeouts on extended ports if the host already exists in ARP or answered a primary port.
        if arp_hit.is_some() || !open_ports.is_empty() {
            for &(port, svc) in extra_ports {
                let t0 = Instant::now();
                let addr = SocketAddr::from((ip, port));
                if TcpStream::connect_timeout(&addr, timeout).is_ok() {
                    let rtt = t0.elapsed().as_secs_f64() * 1000.0;
                    best_rtt_ms = Some(best_rtt_ms.map_or(rtt, |b| b.min(rtt)));
                    open_ports.push(LanPortEntry {
                        port,
                        protocol: CompactString::const_new("tcp"),
                        state: CompactString::const_new("open"),
                        service: CompactString::from(svc),
                        version: CompactString::const_new(""),
                    });
                }
            }
            open_ports.sort_by_key(|p| p.port);
        }
        if open_ports.is_empty() && arp_hit.is_none() {
            return None;
        }
        let (hostname_opt, mac_opt) = arp_hit.cloned().unwrap_or((None, None));
        let latency_ms = match best_rtt_ms {
            Some(ms) => Metric::measured(ms, lat_prov),
            None => Metric::unavailable(UnavailableReason::NotApplicable, lat_prov),
        };
        Some(LanHostEntry {
            ip: CompactString::from(ip_str),
            hostname: hostname_opt.unwrap_or_default(),
            mac: mac_opt,
            vendor: None,
            latency_ms,
            open_ports,
            os_guess: None,
        })
    };

    // Socket connect timeouts are I/O-wait bound; a dedicated 64-thread pool completes /24 sweeps in ~2s.
    let mut discovered: Vec<LanHostEntry> =
        match rayon::ThreadPoolBuilder::new().num_threads(64).build() {
            Ok(pool) => pool.install(|| {
                hosts_to_probe
                    .par_iter()
                    .filter_map(|&ip| probe_fn(ip))
                    .collect()
            }),
            Err(_) => hosts_to_probe
                .par_iter()
                .filter_map(|&ip| probe_fn(ip))
                .collect(),
        };
    discovered.sort_by_key(|h| h.ip.parse::<Ipv4Addr>().ok());

    LanRadarReport {
        subnet: CompactString::from(subnet),
        scanner_backend: CompactString::const_new("native-tcp-arp-probe"),
        elapsed_ms: started.elapsed().as_millis() as u64,
        hosts: discovered,
        notes: CompactString::const_new(
            "Native concurrent TCP probe + ARP table (install nmap for full OS/SYN scan).",
        ),
    }
}

/// Collects mounted filesystems without fabricating `100%` for zero-sized virtual mounts.
pub fn collect_mounted_volumes() -> Vec<VolumeSpaceSummary> {
    let sampled_at_ns = unix_now_ns();
    let bytes_prov = MetricProvenance {
        source: SourceId(1),
        unit_tag: Some(UnitTag::BYTES),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };
    let pct_prov = MetricProvenance {
        source: SourceId(1),
        unit_tag: Some(UnitTag::PERCENT),
        sampled_at_ns,
        window_ns: 0,
        quality: MetricQuality::Authoritative,
        contributed_by_stale: false,
    };

    #[cfg(target_os = "macos")]
    {
        // SAFETY: Queries mounted filesystem count first, then fills `buf`.
        let count = unsafe { libc::getfsstat(ptr::null_mut(), 0, libc::MNT_NOWAIT) };
        if count <= 0 {
            return Vec::new();
        }
        let mut buf: Vec<libc::statfs> = vec![unsafe { std::mem::zeroed() }; count as usize];
        let buf_bytes = (buf.len() * std::mem::size_of::<libc::statfs>()) as c_int;
        let actual = unsafe { libc::getfsstat(buf.as_mut_ptr(), buf_bytes, libc::MNT_NOWAIT) };
        if actual <= 0 {
            return Vec::new();
        }

        let mut out = Vec::new();
        for sfs in &buf[..(actual as usize).min(buf.len())] {
            // SAFETY: `f_mntonname` and `f_fstypename` are NUL-terminated char arrays in `statfs`.
            let mount = unsafe { CStr::from_ptr(sfs.f_mntonname.as_ptr().cast::<c_char>()) }
                .to_string_lossy()
                .into_owned();
            let fstype = unsafe { CStr::from_ptr(sfs.f_fstypename.as_ptr().cast::<c_char>()) }
                .to_string_lossy()
                .into_owned();

            if matches!(fstype.as_str(), "devfs" | "autofs" | "nullfs") {
                continue;
            }
            let bsize = u64::from(sfs.f_bsize);
            let total = sfs.f_blocks.saturating_mul(bsize);
            let avail = sfs.f_bavail.saturating_mul(bsize);
            let used = total.saturating_sub(avail);

            let (tot_m, used_m, avail_m, pct_m) = if total > 0 {
                let pct = ((used as f64) / (total as f64) * 100.0).clamp(0.0, 100.0);
                (
                    Metric::measured(total as f64, bytes_prov),
                    Metric::measured(used as f64, bytes_prov),
                    Metric::measured(avail as f64, bytes_prov),
                    Metric::measured(pct, pct_prov),
                )
            } else {
                (
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, pct_prov),
                )
            };

            out.push(VolumeSpaceSummary {
                mount_point: CompactString::from(mount),
                filesystem: CompactString::from(fstype),
                total_bytes: tot_m,
                used_bytes: used_m,
                available_bytes: avail_m,
                used_percent: pct_m,
            });
        }
        out
    }

    #[cfg(target_os = "linux")]
    {
        let mut out = Vec::new();
        let mut seen = HashSet::new();
        let mounts_txt = fs::read_to_string("/proc/mounts").unwrap_or_default();
        for line in mounts_txt.lines() {
            let cols: Vec<&str> = line.split_whitespace().collect();
            if cols.len() < 3 {
                continue;
            }
            let mount = cols[1].replace("\\040", " ");
            let fstype = cols[2];
            if matches!(
                fstype,
                "proc"
                    | "sysfs"
                    | "devpts"
                    | "devtmpfs"
                    | "cgroup"
                    | "cgroup2"
                    | "pstore"
                    | "bpf"
                    | "tracefs"
                    | "debugfs"
                    | "securityfs"
                    | "configfs"
                    | "fusectl"
                    | "hugetlbfs"
                    | "mqueue"
                    | "binfmt_misc"
                    | "nsfs"
                    | "autofs"
                    | "rpc_pipefs"
                    | "ramfs"
            ) || !seen.insert(mount.clone())
            {
                continue;
            }
            let (tot_m, used_m, avail_m, pct_m) = match statvfs_volume_bytes(Path::new(&mount)) {
                Ok((total, avail, used)) if total > 0 => {
                    let pct = ((used as f64) / (total as f64) * 100.0).clamp(0.0, 100.0);
                    (
                        Metric::measured(total as f64, bytes_prov),
                        Metric::measured(used as f64, bytes_prov),
                        Metric::measured(avail as f64, bytes_prov),
                        Metric::measured(pct, pct_prov),
                    )
                }
                _ => (
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, pct_prov),
                ),
            };
            out.push(VolumeSpaceSummary {
                mount_point: CompactString::from(mount),
                filesystem: CompactString::from(fstype),
                total_bytes: tot_m,
                used_bytes: used_m,
                available_bytes: avail_m,
                used_percent: pct_m,
            });
        }
        if !seen.contains("/")
            && let Ok((total, avail, used)) = statvfs_volume_bytes(Path::new("/"))
            && total > 0
        {
            let pct = ((used as f64) / (total as f64) * 100.0).clamp(0.0, 100.0);
            out.push(VolumeSpaceSummary {
                mount_point: CompactString::const_new("/"),
                filesystem: CompactString::const_new("linux"),
                total_bytes: Metric::measured(total as f64, bytes_prov),
                used_bytes: Metric::measured(used as f64, bytes_prov),
                available_bytes: Metric::measured(avail as f64, bytes_prov),
                used_percent: Metric::measured(pct, pct_prov),
            });
        }
        out
    }

    #[cfg(target_os = "windows")]
    {
        win32_ops::collect_windows_volumes(bytes_prov, pct_prov)
    }

    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        let _ = (bytes_prov, pct_prov);
        Vec::new()
    }
}

pub fn collect_system_hardware() -> &'static SystemHardwareSummary {
    static HW: OnceLock<SystemHardwareSummary> = OnceLock::new();
    HW.get_or_init(|| {
        #[cfg(target_os = "macos")]
        {
            let hostname =
                sysctl_string("kern.hostname").unwrap_or_else(|| "localhost".to_string());
            let os_version = sysctl_string("kern.osproductversion").unwrap_or_default();
            let chip_model = sysctl_string("machdep.cpu.brand_string")
                .or_else(|| sysctl_string("hw.model"))
                .unwrap_or_else(|| "Apple Silicon".to_string());
            let physical_cores = sysctl_u32("hw.physicalcpu").unwrap_or(0);
            let logical_cores = sysctl_u32("hw.logicalcpu").unwrap_or(0);
            let p_cores = sysctl_u32("hw.perflevel0.physicalcpu").unwrap_or(0);
            let e_cores = sysctl_u32("hw.perflevel1.physicalcpu").unwrap_or(0);
            let l2_cache_kb = sysctl_u64("hw.l2cachesize").unwrap_or(0) / 1024;
            let l3_cache_kb = sysctl_u64("hw.l3cachesize").unwrap_or(0) / 1024;
            let total_ram_bytes = sysctl_u64("hw.memsize").unwrap_or(0);
            let (accel_model, accel_cores) = query_io_accelerator_gpu_info();

            SystemHardwareSummary {
                hostname: CompactString::from(hostname),
                os_name: CompactString::const_new("macOS"),
                os_version: CompactString::from(os_version),
                architecture: CompactString::from(std::env::consts::ARCH),
                chip_model: CompactString::from(chip_model),
                physical_cores,
                logical_cores,
                p_cores,
                e_cores,
                l2_cache_kb,
                l3_cache_kb,
                total_ram_bytes,
                gpu_model: CompactString::from(
                    accel_model.unwrap_or_else(|| "Apple GPU".to_string()),
                ),
                gpu_cores: accel_cores,
                firmware_version: sysctl_string("hw.model").unwrap_or_default().into(),
            }
        }
        #[cfg(target_os = "linux")]
        {
            linux_system_hardware()
        }
        #[cfg(target_os = "windows")]
        {
            win32_ops::windows_system_hardware()
        }
        #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
        {
            SystemHardwareSummary {
                hostname: CompactString::const_new("localhost"),
                os_name: CompactString::from(std::env::consts::OS),
                os_version: CompactString::const_new(""),
                architecture: CompactString::from(std::env::consts::ARCH),
                chip_model: CompactString::const_new("Unknown CPU"),
                physical_cores: 1,
                logical_cores: 1,
                p_cores: 1,
                e_cores: 0,
                l2_cache_kb: 0,
                l3_cache_kb: 0,
                total_ram_bytes: 0,
                gpu_model: CompactString::const_new("Unknown GPU"),
                gpu_cores: None,
                firmware_version: CompactString::const_new(""),
            }
        }
    })
}

#[cfg(target_os = "linux")]
fn linux_system_hardware() -> SystemHardwareSummary {
    let hostname = fs::read_to_string("/etc/hostname")
        .ok()
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .or_else(|| std::env::var("HOSTNAME").ok())
        .unwrap_or_else(|| "localhost".to_string());

    let mut os_name = String::from("Linux");
    let mut os_version = String::new();
    if let Ok(os_rel) = fs::read_to_string("/etc/os-release") {
        for line in os_rel.lines() {
            if let Some(v) = line.strip_prefix("NAME=") {
                os_name = v.trim_matches('"').to_string();
            } else if let Some(v) = line.strip_prefix("VERSION_ID=") {
                os_version = v.trim_matches('"').to_string();
            } else if os_version.is_empty()
                && let Some(v) = line.strip_prefix("PRETTY_NAME=")
            {
                os_version = v.trim_matches('"').to_string();
            }
        }
    }
    if os_version.is_empty()
        && let Ok(rel) = fs::read_to_string("/proc/sys/kernel/osrelease")
    {
        os_version = rel.trim().to_string();
    }

    let logical_cores = std::thread::available_parallelism()
        .map(|n| n.get() as u32)
        .unwrap_or(1);
    let mut chip_model = String::new();
    let mut cpu_cores = 0u32;
    if let Ok(cpuinfo) = fs::read_to_string("/proc/cpuinfo") {
        for line in cpuinfo.lines() {
            if let Some((k, v)) = line.split_once(':') {
                let key = k.trim();
                let val = v.trim();
                if chip_model.is_empty()
                    && matches!(key, "model name" | "Model" | "Hardware" | "Processor")
                {
                    chip_model = val.to_string();
                }
                if cpu_cores == 0 && key == "cpu cores" {
                    cpu_cores = val.parse::<u32>().unwrap_or(0);
                }
            }
        }
    }
    if chip_model.is_empty() {
        chip_model = format!("{} CPU", std::env::consts::ARCH);
    }
    let physical_cores = if cpu_cores > 0 {
        cpu_cores
    } else {
        logical_cores
    };

    let mut total_ram_bytes = 0u64;
    if let Ok(meminfo) = fs::read_to_string("/proc/meminfo") {
        for line in meminfo.lines() {
            if let Some(rest) = line.strip_prefix("MemTotal:")
                && let Some(kib) = rest
                    .split_whitespace()
                    .next()
                    .and_then(|s| s.parse::<u64>().ok())
            {
                total_ram_bytes = kib * 1024;
                break;
            }
        }
    }

    let mut l2_cache_kb = 0u64;
    let mut l3_cache_kb = 0u64;
    if let Ok(rd) = fs::read_dir("/sys/devices/system/cpu/cpu0/cache") {
        for entry in rd.flatten() {
            let dir = entry.path();
            let level = fs::read_to_string(dir.join("level"))
                .ok()
                .and_then(|s| s.trim().parse::<u32>().ok())
                .unwrap_or(0);
            let size_str = fs::read_to_string(dir.join("size")).unwrap_or_default();
            let sz_trim = size_str.trim();
            let kb = if let Some(n) = sz_trim.strip_suffix('K') {
                n.parse::<u64>().unwrap_or(0)
            } else if let Some(n) = sz_trim.strip_suffix('M') {
                n.parse::<u64>().unwrap_or(0) * 1024
            } else {
                0
            };
            if level == 2 {
                l2_cache_kb = l2_cache_kb.max(kb);
            } else if level == 3 {
                l3_cache_kb = l3_cache_kb.max(kb);
            }
        }
    }

    let firmware_version = fs::read_to_string("/sys/class/dmi/id/bios_version")
        .or_else(|_| fs::read_to_string("/proc/sys/kernel/osrelease"))
        .map(|s| s.trim().to_string())
        .unwrap_or_default();

    SystemHardwareSummary {
        hostname: CompactString::from(hostname),
        os_name: CompactString::from(os_name),
        os_version: CompactString::from(os_version),
        architecture: CompactString::from(std::env::consts::ARCH),
        chip_model: CompactString::from(chip_model),
        physical_cores,
        logical_cores,
        p_cores: physical_cores,
        e_cores: 0,
        l2_cache_kb,
        l3_cache_kb,
        total_ram_bytes,
        gpu_model: CompactString::const_new("Linux DRM/GPU"),
        gpu_cores: None,
        firmware_version: CompactString::from(firmware_version),
    }
}

#[cfg(target_os = "macos")]
fn query_io_accelerator_gpu_info() -> (Option<String>, Option<u32>) {
    let Ok(cls) = CString::new("IOAccelerator") else {
        return (None, None);
    };
    let mut iter = 0;
    // SAFETY: `IOServiceMatching` returns a dictionary consumed by `IOServiceGetMatchingServices`.
    let kr = unsafe {
        let matching = IOServiceMatching(cls.as_ptr());
        if matching.is_null() {
            return (None, None);
        }
        IOServiceGetMatchingServices(kIOMainPortDefault, matching, &raw mut iter)
    };
    if kr != KERN_SUCCESS || iter == 0 {
        return (None, None);
    }
    let _iter_guard = IoObjectGuard(iter);

    let mut model_out: Option<String> = None;
    let mut cores_out: Option<u32> = None;
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

        if model_out.is_none() {
            model_out = dict_get_string(dict, "model");
        }
        if cores_out.is_none()
            && let Some(c) = dict_get_i64(dict, "gpu-core-count")
            && c > 0
        {
            cores_out = u32::try_from(c).ok();
        }
        if model_out.is_some() && cores_out.is_some() {
            break;
        }
    }
    (model_out, cores_out)
}

#[cfg(target_os = "macos")]
fn sysctl_string(name: &str) -> Option<String> {
    let cname = CString::new(name).ok()?;
    let mut size: usize = 0;
    // SAFETY: First `sysctlbyname` queries string length.
    let rc1 = unsafe {
        libc::sysctlbyname(
            cname.as_ptr(),
            ptr::null_mut(),
            &raw mut size,
            ptr::null_mut(),
            0,
        )
    };
    if rc1 != 0 || size == 0 {
        return None;
    }
    let mut buf = vec![0u8; size];
    // SAFETY: `buf` has `size` writable bytes.
    let rc2 = unsafe {
        libc::sysctlbyname(
            cname.as_ptr(),
            buf.as_mut_ptr().cast::<c_void>(),
            &raw mut size,
            ptr::null_mut(),
            0,
        )
    };
    if rc2 != 0 {
        return None;
    }
    let trimmed = buf
        .split(|&b| b == 0)
        .next()
        .map(|s| String::from_utf8_lossy(s).into_owned())?;
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed)
    }
}

pub fn parse_who_output(output: &str) -> Vec<UserSessionEntry> {
    let mut out = Vec::new();
    for line in output.lines() {
        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.len() < 4 {
            continue;
        }
        let username = parts[0];
        let tty = parts[1];
        let login_time = parts[2..parts.len().min(5)].join(" ");
        let host = line
            .find('(')
            .and_then(|s| line[s + 1..].find(')').map(|e| &line[s + 1..s + 1 + e]))
            .unwrap_or("local");
        out.push(UserSessionEntry {
            username: CompactString::from(username),
            tty: CompactString::from(tty),
            host: CompactString::from(host),
            login_time: CompactString::from(login_time),
            state: CompactString::const_new("Active"),
            pid: None,
        });
    }
    out
}

pub fn collect_users() -> Vec<UserSessionEntry> {
    #[cfg(target_os = "macos")]
    {
        Command::new("/usr/bin/who")
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .map(|o| parse_who_output(&String::from_utf8_lossy(&o.stdout)))
            .unwrap_or_default()
    }
    #[cfg(target_os = "linux")]
    {
        let mut users = Command::new("who")
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .map(|o| parse_who_output(&String::from_utf8_lossy(&o.stdout)))
            .unwrap_or_default();
        if users.is_empty() {
            let username = std::env::var("USER")
                .or_else(|_| std::env::var("LOGNAME"))
                .ok()
                .filter(|s| !s.is_empty())
                .unwrap_or_else(|| {
                    // SAFETY: `getuid()` cannot fail.
                    format!("uid:{}", unsafe { libc::getuid() })
                });
            users.push(UserSessionEntry {
                username: CompactString::from(username),
                tty: CompactString::const_new("pts/0"),
                host: CompactString::const_new("local"),
                login_time: CompactString::const_new("current"),
                state: CompactString::const_new("Active"),
                pid: None,
            });
        }
        users
    }
    #[cfg(target_os = "windows")]
    {
        let username = win32_ops::current_username()
            .or_else(|| std::env::var("USERNAME").ok().filter(|s| !s.is_empty()))
            .unwrap_or_else(|| "User".to_string());
        let host = std::env::var("COMPUTERNAME")
            .ok()
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| "localhost".to_string());
        vec![UserSessionEntry {
            username: CompactString::from(username),
            tty: CompactString::const_new("console"),
            host: CompactString::from(host),
            login_time: CompactString::const_new("current"),
            state: CompactString::const_new("Active"),
            pid: None,
        }]
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        Vec::new()
    }
}

pub fn collect_installed_apps() -> Vec<InstalledAppEntry> {
    #[cfg(target_os = "macos")]
    {
        let mut dirs = vec![
            PathBuf::from("/Applications"),
            PathBuf::from("/System/Applications"),
        ];
        if let Ok(home) = std::env::var("HOME")
            && !home.is_empty()
        {
            dirs.push(PathBuf::from(home).join("Applications"));
        }

        let mut seen_paths = HashSet::new();
        let mut apps = Vec::new();
        for dir in dirs {
            let Ok(entries) = fs::read_dir(&dir) else {
                continue;
            };
            for entry in entries.flatten() {
                let p = entry.path();
                if p.extension().and_then(|e| e.to_str()) != Some("app") {
                    continue;
                }
                let p_str = p.to_string_lossy().into_owned();
                if !seen_paths.insert(p_str.clone()) {
                    continue;
                }
                let name = p
                    .file_stem()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
                let info_plist = p.join("Contents/Info.plist");
                let mut version = String::new();
                let mut bundle_id = String::new();
                if let Ok(bytes) = fs::read(&info_plist) {
                    let text = String::from_utf8_lossy(&bytes);
                    if text.contains("<plist") {
                        version = plist_string_value(&text, "CFBundleShortVersionString")
                            .or_else(|| plist_string_value(&text, "CFBundleVersion"))
                            .unwrap_or_default();
                        bundle_id =
                            plist_string_value(&text, "CFBundleIdentifier").unwrap_or_default();
                    }
                }
                apps.push(InstalledAppEntry {
                    name: CompactString::from(name),
                    version: CompactString::from(version),
                    bundle_id: CompactString::from(bundle_id),
                    path: CompactString::from(p_str),
                    size_bytes: None,
                });
            }
        }
        apps.sort_by(|a, b| {
            a.name
                .to_ascii_lowercase()
                .cmp(&b.name.to_ascii_lowercase())
        });
        apps
    }
    #[cfg(target_os = "linux")]
    {
        let mut dirs = vec![
            PathBuf::from("/usr/share/applications"),
            PathBuf::from("/usr/local/share/applications"),
        ];
        if let Ok(home) = std::env::var("HOME")
            && !home.is_empty()
        {
            dirs.push(PathBuf::from(home).join(".local/share/applications"));
        }
        let mut seen_paths = HashSet::new();
        let mut apps = Vec::new();
        for dir in dirs {
            let Ok(rd) = fs::read_dir(&dir) else {
                continue;
            };
            for entry in rd.flatten() {
                let p = entry.path();
                if p.extension().and_then(|e| e.to_str()) != Some("desktop") {
                    continue;
                }
                let p_str = p.to_string_lossy().into_owned();
                if !seen_paths.insert(p_str.clone()) {
                    continue;
                }
                let Ok(txt) = fs::read_to_string(&p) else {
                    continue;
                };
                let mut name = p
                    .file_stem()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
                let mut exec = String::new();
                for line in txt.lines() {
                    let t = line.trim();
                    if let Some(v) = t.strip_prefix("Name=") {
                        name = v.trim().to_string();
                    } else if exec.is_empty()
                        && let Some(v) = t.strip_prefix("Exec=")
                    {
                        exec = v.trim().to_string();
                    }
                }
                apps.push(InstalledAppEntry {
                    name: CompactString::from(name),
                    version: CompactString::const_new(""),
                    bundle_id: CompactString::from(exec),
                    path: CompactString::from(p_str),
                    size_bytes: None,
                });
            }
        }
        if apps.is_empty()
            && let Ok(rd) = fs::read_dir("/usr/bin")
        {
            for entry in rd.flatten().take(200) {
                let p = entry.path();
                if let Some(name) = p.file_name().and_then(|s| s.to_str()) {
                    apps.push(InstalledAppEntry {
                        name: CompactString::from(name),
                        version: CompactString::const_new(""),
                        bundle_id: CompactString::const_new("usr.bin"),
                        path: CompactString::from(p.to_string_lossy().as_ref()),
                        size_bytes: None,
                    });
                }
            }
        }
        apps.sort_by(|a, b| {
            a.name
                .to_ascii_lowercase()
                .cmp(&b.name.to_ascii_lowercase())
        });
        apps
    }
    #[cfg(target_os = "windows")]
    {
        win32_ops::list_registry_installed_apps()
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        Vec::new()
    }
}

/// Computes total file byte size of a bundle or directory tree (intended for async worker threads).
pub fn measure_path_size_bytes(path: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return 0;
    };
    if meta.is_file() {
        return meta.len();
    }
    if meta.file_type().is_symlink() || !meta.is_dir() {
        return 0;
    }
    let mut total = 0u64;
    let mut stack = vec![path.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(rd) = fs::read_dir(&dir) else {
            continue;
        };
        for entry in rd.flatten() {
            let p = entry.path();
            if let Ok(m) = fs::symlink_metadata(&p) {
                if m.is_file() {
                    total = total.saturating_add(m.len());
                } else if m.is_dir() && !m.file_type().is_symlink() {
                    stack.push(p);
                }
            }
        }
    }
    total
}

fn unambiguous_name_tokens(app_name: &str) -> Vec<String> {
    let generic = [
        "app",
        "helper",
        "service",
        "daemon",
        "agent",
        "macos",
        "system",
        "apple",
        "client",
        "viewer",
        "player",
        "manager",
        "monitor",
        "tool",
        "utility",
        "utilities",
    ];
    let normalized = app_name.trim().to_ascii_lowercase();
    let mut tokens = Vec::new();
    if normalized.len() >= 4 && !generic.contains(&normalized.as_str()) {
        tokens.push(normalized.clone());
        let compact = normalized.replace([' ', '-', '_'], "");
        if compact.len() >= 4 && compact != normalized {
            tokens.push(compact);
        }
    }
    tokens
}

/// Scans `~/Library/{Application Support,Caches,Containers,Group Containers,Logs,Preferences,Saved Application State,WebKit}`
/// for related application data matching `bundle_id` or unambiguous name tokens.
pub fn resolve_related_files(app: &InstalledAppEntry) -> Vec<RelatedFileEntry> {
    let Ok(home) = std::env::var("HOME") else {
        return Vec::new();
    };
    if home.is_empty() {
        return Vec::new();
    }
    resolve_related_files_in_library(app, &PathBuf::from(home).join("Library"))
}

pub fn resolve_related_files_in_library(
    app: &InstalledAppEntry,
    library_dir: &Path,
) -> Vec<RelatedFileEntry> {
    const SUBDIRS: [&str; 8] = [
        "Application Support",
        "Caches",
        "Containers",
        "Group Containers",
        "Logs",
        "Preferences",
        "Saved Application State",
        "WebKit",
    ];

    let bundle_lc = app.bundle_id.trim().to_ascii_lowercase();
    let name_tokens = unambiguous_name_tokens(app.name.as_str());
    if bundle_lc.is_empty() && name_tokens.is_empty() {
        return Vec::new();
    }

    let mut out = Vec::new();
    for sub in SUBDIRS {
        let dir = library_dir.join(sub);
        let Ok(rd) = fs::read_dir(&dir) else {
            continue;
        };
        for entry in rd.flatten() {
            let p = entry.path();
            let fname_lc = entry.file_name().to_string_lossy().to_ascii_lowercase();
            let matches_bundle = !bundle_lc.is_empty() && fname_lc.contains(&bundle_lc);
            let matches_name = name_tokens.iter().any(|tok| {
                fname_lc == *tok
                    || fname_lc.starts_with(&format!("{tok}."))
                    || fname_lc.ends_with(&format!(".{tok}"))
                    || fname_lc.contains(&format!(".{tok}."))
            });
            if matches_bundle || matches_name {
                let sz = measure_path_size_bytes(&p);
                out.push(RelatedFileEntry {
                    path: CompactString::from(p.to_string_lossy().as_ref()),
                    category: CompactString::from(sub),
                    size_bytes: sz,
                });
            }
        }
    }
    out.sort_by_key(|a| std::cmp::Reverse(a.size_bytes));
    out
}

/// Moves an application bundle and selected related files into `~/.Trash`, refusing Apple system apps.
pub fn uninstall_app_to_trash(
    app: &InstalledAppEntry,
    include_related: &[PathBuf],
) -> Result<UninstallReceipt, String> {
    if app.path.starts_with("/System/") || app.bundle_id.starts_with("com.apple.") {
        return Err(format!(
            "Refused: '{}' is an Apple system application protected by SIP.",
            app.name
        ));
    }
    let home = std::env::var("HOME").map_err(|_| "HOME directory is not set".to_string())?;
    let trash_dir = PathBuf::from(home).join(".Trash");
    fs::create_dir_all(&trash_dir).map_err(|e| format!("Cannot access ~/.Trash: {e}"))?;

    let mut targets = Vec::with_capacity(1 + include_related.len());
    targets.push(PathBuf::from(app.path.as_str()));
    targets.extend(include_related.iter().cloned());

    let mut trashed_paths = Vec::new();
    let mut failed_paths = Vec::new();
    for src in targets {
        if !src.exists() {
            continue;
        }
        let base_name = src
            .file_name()
            .map(|s| s.to_string_lossy().into_owned())
            .unwrap_or_else(|| "item".to_string());
        let mut dest = trash_dir.join(&base_name);
        let mut suffix = 2u32;
        while dest.exists() {
            dest = trash_dir.join(format!("{base_name}-{suffix}"));
            suffix += 1;
        }
        match fs::rename(&src, &dest) {
            Ok(()) => trashed_paths.push(CompactString::from(src.to_string_lossy().as_ref())),
            Err(err) => failed_paths.push((
                CompactString::from(src.to_string_lossy().as_ref()),
                CompactString::from(err.to_string()),
            )),
        }
    }

    Ok(UninstallReceipt {
        app_name: app.name.clone(),
        trashed_paths,
        failed_paths,
    })
}

fn parse_kext_deps(line: &str) -> Vec<u32> {
    let Some(start) = line.rfind('<') else {
        return Vec::new();
    };
    let Some(end) = line[start..].find('>') else {
        return Vec::new();
    };
    line[start + 1..start + end]
        .split_whitespace()
        .filter_map(|t| t.parse::<u32>().ok())
        .collect()
}

pub fn parse_kextstat_output(output: &str) -> Vec<DriverEntry> {
    let mut drivers = Vec::new();
    for line in output.lines() {
        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.len() < 7 {
            continue;
        }
        let Ok(index) = parts[0].parse::<i64>() else {
            continue;
        };
        let address = parts[2];
        let size_hex = parts[3].trim_start_matches("0x");
        let size_bytes = u64::from_str_radix(size_hex, 16).unwrap_or(0);
        let name = parts[5];
        let version = parts[6].trim_matches(|c| c == '(' || c == ')');
        let uuid = parts
            .iter()
            .skip(7)
            .find(|tok| {
                let clean = tok.trim_matches(|c| c == '[' || c == ']');
                clean.len() == 36 && clean.chars().filter(|&c| c == '-').count() == 4
            })
            .map(|tok| CompactString::from(tok.trim_matches(|c| c == '[' || c == ']')));
        let dependencies = parse_kext_deps(line);
        let scope = if name.starts_with("com.apple.") {
            DriverScope::AppleSystem
        } else {
            DriverScope::ThirdParty
        };
        drivers.push(DriverEntry {
            index,
            name: CompactString::from(name),
            version: CompactString::from(version),
            loaded: true,
            address: CompactString::from(address),
            size_bytes,
            kind: DriverKind::KernelExtension,
            scope,
            state: DriverState::Loaded,
            uuid,
            dependencies,
            bundle_path: None,
        });
    }
    drivers
}

/// Parses `/usr/bin/systemextensionsctl list` output into DriverKit (`DriverKind::SystemExtension`) entries.
pub fn parse_systemextensionsctl_output(output: &str) -> Vec<DriverEntry> {
    let mut out = Vec::new();
    for line in output.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty()
            || trimmed.starts_with("---")
            || trimmed.starts_with("enabled")
            || trimmed.ends_with("extension(s)")
        {
            continue;
        }
        let Some(lb) = trimmed.rfind('[') else {
            continue;
        };
        let Some(rb) = trimmed[lb..].find(']') else {
            continue;
        };
        let state_str = trimmed[lb + 1..lb + rb].to_ascii_lowercase();
        let state = if state_str.contains("waiting for user") {
            DriverState::WaitingForUser
        } else if state_str.contains("terminated") || state_str.contains("uninstall") {
            DriverState::Unloading
        } else if state_str.contains("activated") && state_str.contains("enabled") {
            DriverState::ActivatedEnabled
        } else {
            DriverState::InstalledNotLoaded
        };

        let prefix = trimmed[..lb].trim();
        let tokens: Vec<&str> = prefix.split_whitespace().collect();
        // Typical tokens: ["*", "*", "<TEAMID>", "<bundle.id>", "(<ver>/<build>)", ...]
        let bundle_id = tokens
            .iter()
            .find(|t| t.contains('.') && !t.starts_with('('))
            .copied()
            .unwrap_or("");
        if bundle_id.is_empty() {
            continue;
        }
        let version = tokens
            .iter()
            .find(|t| t.starts_with('(') && t.ends_with(')'))
            .map(|t| t.trim_matches(|c| c == '(' || c == ')'))
            .unwrap_or("");
        let scope = if bundle_id.starts_with("com.apple.") {
            DriverScope::AppleSystem
        } else {
            DriverScope::ThirdParty
        };
        out.push(DriverEntry {
            index: -1,
            name: CompactString::from(bundle_id),
            version: CompactString::from(version),
            loaded: state == DriverState::ActivatedEnabled,
            address: CompactString::const_new("DriverKit"),
            size_bytes: 0,
            kind: DriverKind::SystemExtension,
            scope,
            state,
            uuid: None,
            dependencies: Vec::new(),
            bundle_path: None,
        });
    }
    out
}

/// Scans on-disk `.kext` and `.dext` bundles to attach `bundle_path` and add uninstalled/unloaded drivers.
pub fn reconcile_driver_bundles_in_dirs(drivers: &mut Vec<DriverEntry>, dirs: &[&Path]) {
    let mut index_by_name: HashMap<String, usize> = HashMap::new();
    for (idx, d) in drivers.iter().enumerate() {
        index_by_name.insert(d.name.to_string(), idx);
    }

    for dir in dirs {
        let Ok(rd) = fs::read_dir(dir) else {
            continue;
        };
        for entry in rd.flatten() {
            let p = entry.path();
            let ext = p.extension().and_then(|e| e.to_str()).unwrap_or("");
            let kind = match ext {
                "kext" => DriverKind::KernelExtension,
                "dext" => DriverKind::SystemExtension,
                _ => continue,
            };
            let plist_path = p.join("Contents/Info.plist");
            let mut bundle_id = String::new();
            let mut version = String::new();
            if let Ok(bytes) = fs::read(&plist_path) {
                let text = String::from_utf8_lossy(&bytes);
                if text.contains("<plist") {
                    bundle_id = plist_string_value(&text, "CFBundleIdentifier").unwrap_or_default();
                    version = plist_string_value(&text, "CFBundleShortVersionString")
                        .or_else(|| plist_string_value(&text, "CFBundleVersion"))
                        .unwrap_or_default();
                }
            }
            if bundle_id.is_empty() {
                bundle_id = p
                    .file_stem()
                    .map(|s| s.to_string_lossy().into_owned())
                    .unwrap_or_default();
            }
            if bundle_id.is_empty() {
                continue;
            }
            let path_str = CompactString::from(p.to_string_lossy().as_ref());
            if let Some(&existing_idx) = index_by_name.get(&bundle_id) {
                if drivers[existing_idx].bundle_path.is_none() {
                    drivers[existing_idx].bundle_path = Some(path_str);
                }
            } else {
                let scope = if bundle_id.starts_with("com.apple.") {
                    DriverScope::AppleSystem
                } else {
                    DriverScope::ThirdParty
                };
                let new_idx = drivers.len();
                index_by_name.insert(bundle_id.clone(), new_idx);
                drivers.push(DriverEntry {
                    index: -1,
                    name: CompactString::from(bundle_id),
                    version: CompactString::from(version),
                    loaded: false,
                    address: CompactString::const_new("-"),
                    size_bytes: 0,
                    kind,
                    scope,
                    state: DriverState::InstalledNotLoaded,
                    uuid: None,
                    dependencies: Vec::new(),
                    bundle_path: Some(path_str),
                });
            }
        }
    }
}

/// Returns an explicit refusal reason if `entry` cannot/should not be unloaded via `kextunload`.
pub fn driver_unload_refusal(entry: &DriverEntry) -> Option<String> {
    if entry.scope == DriverScope::AppleSystem {
        return Some(format!(
            "Refused: '{}' is an Apple system driver protected by SIP.",
            entry.name
        ));
    }
    if entry.kind == DriverKind::SystemExtension {
        return Some(format!(
            "Refused: '{}' is a DriverKit system extension managed by its containing app.",
            entry.name
        ));
    }
    if !entry.loaded {
        return Some(format!(
            "Refused: '{}' is installed on disk but not currently loaded.",
            entry.name
        ));
    }
    if !entry.dependencies.is_empty() {
        return Some(format!(
            "Refused: '{}' has {} active dependency link(s).",
            entry.name,
            entry.dependencies.len()
        ));
    }
    None
}

pub fn collect_drivers() -> Vec<DriverEntry> {
    #[cfg(target_os = "macos")]
    {
        let mut drivers = Command::new("/usr/sbin/kextstat")
            .arg("-l")
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .map(|o| parse_kextstat_output(&String::from_utf8_lossy(&o.stdout)))
            .unwrap_or_default();

        if let Ok(out) = Command::new("/usr/bin/systemextensionsctl")
            .arg("list")
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            && out.status.success()
        {
            let sysexts = parse_systemextensionsctl_output(&String::from_utf8_lossy(&out.stdout));
            drivers.extend(sysexts);
        }

        let bundle_dirs = [
            Path::new("/System/Library/Extensions"),
            Path::new("/Library/Extensions"),
            Path::new("/System/Library/DriverExtensions"),
            Path::new("/Library/DriverExtensions"),
        ];
        reconcile_driver_bundles_in_dirs(&mut drivers, &bundle_dirs);
        drivers
    }
    #[cfg(target_os = "linux")]
    {
        let mut drivers = Vec::new();
        if let Ok(mods_txt) = fs::read_to_string("/proc/modules") {
            for (idx, line) in mods_txt.lines().enumerate() {
                let cols: Vec<&str> = line.split_whitespace().collect();
                if cols.len() < 5 {
                    continue;
                }
                let name = cols[0];
                let size_bytes = cols[1].parse::<u64>().unwrap_or(0);
                let state_str = cols[4];
                let addr = cols.get(5).copied().unwrap_or("-");
                let (loaded, state) = match state_str {
                    "Live" => (true, DriverState::Loaded),
                    "Unloading" => (true, DriverState::Unloading),
                    _ => (false, DriverState::InstalledNotLoaded),
                };
                drivers.push(DriverEntry {
                    index: idx as i64,
                    name: CompactString::from(name),
                    version: CompactString::const_new(""),
                    loaded,
                    address: CompactString::from(addr),
                    size_bytes,
                    kind: DriverKind::KernelExtension,
                    scope: DriverScope::ThirdParty,
                    state,
                    uuid: None,
                    dependencies: Vec::new(),
                    bundle_path: Some(CompactString::from(format!("/sys/module/{name}"))),
                });
            }
        }
        if drivers.is_empty()
            && let Ok(rd) = fs::read_dir("/sys/module")
        {
            for (idx, entry) in rd.flatten().enumerate() {
                let fname = entry.file_name();
                let Some(name) = fname.to_str() else {
                    continue;
                };
                let ver = fs::read_to_string(entry.path().join("version"))
                    .ok()
                    .map(|s| s.trim().to_string())
                    .unwrap_or_default();
                drivers.push(DriverEntry {
                    index: idx as i64,
                    name: CompactString::from(name),
                    version: CompactString::from(ver),
                    loaded: true,
                    address: CompactString::const_new("kernel"),
                    size_bytes: 0,
                    kind: DriverKind::KernelExtension,
                    scope: DriverScope::ThirdParty,
                    state: DriverState::Loaded,
                    uuid: None,
                    dependencies: Vec::new(),
                    bundle_path: Some(CompactString::from(entry.path().to_string_lossy().as_ref())),
                });
            }
        }
        drivers
    }
    #[cfg(target_os = "windows")]
    {
        let mut drivers = Vec::new();
        let sys_root = std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".to_string());
        let drv_dir = PathBuf::from(sys_root).join("System32\\drivers");
        if let Ok(rd) = fs::read_dir(&drv_dir) {
            for (idx, entry) in rd.flatten().enumerate() {
                let p = entry.path();
                if p.extension()
                    .and_then(|e| e.to_str())
                    .is_some_and(|e| e.eq_ignore_ascii_case("sys"))
                {
                    let name = p
                        .file_stem()
                        .map(|s| s.to_string_lossy().into_owned())
                        .unwrap_or_default();
                    let size_bytes = fs::metadata(&p).map(|m| m.len()).unwrap_or(0);
                    drivers.push(DriverEntry {
                        index: idx as i64,
                        name: CompactString::from(name),
                        version: CompactString::const_new(""),
                        loaded: true,
                        address: CompactString::const_new("-"),
                        size_bytes,
                        kind: DriverKind::KernelExtension,
                        scope: DriverScope::ThirdParty,
                        state: DriverState::Loaded,
                        uuid: None,
                        dependencies: Vec::new(),
                        bundle_path: Some(CompactString::from(p.to_string_lossy().as_ref())),
                    });
                }
            }
        }
        drivers.sort_by(|a, b| a.name.cmp(&b.name));
        drivers
    }
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        Vec::new()
    }
}

/// Samples the current macOS desktop wallpaper into a downscaled 24-bit RGB buffer for BT.601 luminance analysis.
pub fn sample_wallpaper_rgb8() -> Option<Vec<u8>> {
    #[cfg(target_os = "macos")]
    {
        let out = Command::new("/usr/bin/osascript")
            .args([
                "-e",
                "tell application \"Finder\" to get POSIX path of (desktop picture as text)",
            ])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .ok()?;
        if !out.status.success() {
            return None;
        }
        let path_str = String::from_utf8_lossy(&out.stdout).trim().to_string();
        if path_str.is_empty() || !Path::new(&path_str).exists() {
            return None;
        }
        let tmp_bmp =
            std::env::temp_dir().join(format!("nexus_wp_sample_{}.bmp", std::process::id()));
        let conv = Command::new("/usr/bin/sips")
            .args(["-Z", "32", "-s", "format", "bmp", &path_str, "--out"])
            .arg(&tmp_bmp)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .ok()?;
        if !conv.success() {
            let _ = fs::remove_file(&tmp_bmp);
            return None;
        }
        let bytes = fs::read(&tmp_bmp).ok()?;
        let _ = fs::remove_file(&tmp_bmp);
        // Minimal uncompressed 24/32bpp BMP pixel extraction
        if bytes.len() < 54 || &bytes[0..2] != b"BM" {
            return None;
        }
        let data_offset = u32::from_le_bytes([bytes[10], bytes[11], bytes[12], bytes[13]]) as usize;
        let bpp = u16::from_le_bytes([bytes[28], bytes[29]]);
        if data_offset >= bytes.len() || (bpp != 24 && bpp != 32) {
            return None;
        }
        let step = usize::from(bpp / 8);
        let mut rgb8 = Vec::new();
        for px in bytes[data_offset..].chunks_exact(step) {
            // BMP stores B, G, R
            rgb8.push(px[2]);
            rgb8.push(px[1]);
            rgb8.push(px[0]);
        }
        if rgb8.is_empty() { None } else { Some(rgb8) }
    }
    #[cfg(not(target_os = "macos"))]
    {
        None
    }
}

/// Returns `(total_bytes, available_bytes, used_bytes)` for the filesystem containing `path`.
pub fn statvfs_volume_bytes(path: &Path) -> std::io::Result<(u64, u64, u64)> {
    #[cfg(unix)]
    {
        let probe = if path.exists() {
            path
        } else {
            path.parent()
                .filter(|p| p.exists())
                .unwrap_or_else(|| Path::new("/"))
        };
        let cpath = CString::new(probe.to_string_lossy().as_bytes())
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidInput, e))?;
        // SAFETY: `cpath` is a valid NUL-terminated path string and `st` is zero-initialized on the stack.
        unsafe {
            let mut st: libc::statvfs = std::mem::zeroed();
            if libc::statvfs(cpath.as_ptr(), &raw mut st) != 0 {
                return Err(std::io::Error::last_os_error());
            }
            let block_size = if st.f_frsize > 0 {
                st.f_frsize as u64
            } else {
                (st.f_bsize as u64).max(1)
            };
            let total = (st.f_blocks as u64).saturating_mul(block_size);
            let avail = (st.f_bavail as u64).saturating_mul(block_size);
            let free = (st.f_bfree as u64).saturating_mul(block_size);
            let used = total.saturating_sub(free);
            Ok((total, avail, used))
        }
    }
    #[cfg(windows)]
    {
        win32_ops::statvfs_volume_bytes_win32(path)
    }
    #[cfg(not(any(unix, windows)))]
    {
        let _ = path;
        Err(std::io::Error::new(
            std::io::ErrorKind::Unsupported,
            "statvfs unsupported",
        ))
    }
}

/// macOS `libc::fcntl(F_NOCACHE)` + `F_FULLFSYNC` + `statvfs` uncached disk benchmark I/O.
#[derive(Debug, Default, Clone, Copy)]
pub struct MacOsUncachedDiskIo;

impl UncachedDiskIo for MacOsUncachedDiskIo {
    fn available_space_bytes(&self, dir: &Path) -> std::io::Result<u64> {
        let (_, avail, _) = statvfs_volume_bytes(dir)?;
        Ok(avail)
    }

    fn open_uncached_rw(&self, path: &Path) -> std::io::Result<File> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(path)?;
        #[cfg(target_os = "macos")]
        {
            // SAFETY: `file.as_raw_fd()` is a valid open file descriptor owned by `file`.
            let rc = unsafe { libc::fcntl(file.as_raw_fd(), libc::F_NOCACHE, 1) };
            if rc == -1 {
                return Err(std::io::Error::last_os_error());
            }
        }
        Ok(file)
    }

    fn full_sync(&self, file: &File) -> std::io::Result<()> {
        #[cfg(target_os = "macos")]
        {
            // SAFETY: `file.as_raw_fd()` is a valid open file descriptor owned by `file`.
            let rc = unsafe { libc::fcntl(file.as_raw_fd(), libc::F_FULLFSYNC, 0) };
            if rc == 0 {
                return Ok(());
            }
        }
        file.sync_all()
    }
}

/// Physical storage device & APFS container metadata collected from `diskutil info`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StorageInspectorEntry {
    pub bsd_name: CompactString,
    pub device_node: CompactString,
    pub model_name: CompactString,
    pub protocol: CompactString,
    pub smart_status: CompactString,
    pub mount_point: Option<CompactString>,
    pub volume_name: Option<CompactString>,
    pub file_system: Option<CompactString>,
    pub total_bytes: u64,
    pub available_bytes: u64,
    pub container_total_bytes: Option<u64>,
    pub container_free_bytes: Option<u64>,
    pub is_internal: bool,
    pub is_ejectable: bool,
}

fn parse_diskutil_bytes(val: &str) -> Option<u64> {
    if let Some(pos) = val.find("Exactly ") {
        let rest = &val[pos + 8..];
        let digits: String = rest.chars().take_while(|c| c.is_ascii_digit()).collect();
        if let Ok(b) = digits.parse::<u64>() {
            return Some(b);
        }
    }
    None
}

/// Parses a single `/usr/sbin/diskutil info <target>` text output into a `StorageInspectorEntry`.
pub fn parse_diskutil_info_text(text: &str) -> Option<StorageInspectorEntry> {
    let mut bsd_name = String::new();
    let mut device_node = String::new();
    let mut model_name = String::new();
    let mut protocol = String::new();
    let mut smart_status = String::from("Unknown");
    let mut mount_point: Option<String> = None;
    let mut volume_name: Option<String> = None;
    let mut file_system: Option<String> = None;
    let mut total_bytes = 0u64;
    let mut available_bytes = 0u64;
    let mut container_total_bytes: Option<u64> = None;
    let mut container_free_bytes: Option<u64> = None;
    let mut is_internal = true;
    let mut is_ejectable = false;

    for line in text.lines() {
        let Some((k, v)) = line.split_once(':') else {
            continue;
        };
        let key = k.trim();
        let val = v.trim();
        match key {
            "Device Identifier" => bsd_name = val.to_string(),
            "Device Node" => device_node = val.to_string(),
            "Device / Media Name" => model_name = val.to_string(),
            "Protocol" => protocol = val.to_string(),
            "SMART Status" => smart_status = val.to_string(),
            "Mount Point" if !val.is_empty() && val != "Not applicable (no file system)" => {
                mount_point = Some(val.to_string());
            }
            "Volume Name" if !val.is_empty() && val != "Not applicable (no file system)" => {
                volume_name = Some(val.to_string());
            }
            "File System Personality" | "Type (Bundle)"
                if file_system.is_none() && !val.is_empty() =>
            {
                file_system = Some(val.to_string());
            }
            "Volume Total Space" | "Container Total Space" => {
                if let Some(b) = parse_diskutil_bytes(val) {
                    container_total_bytes = Some(b);
                    if total_bytes == 0 {
                        total_bytes = b;
                    }
                }
            }
            "Disk Size" if total_bytes == 0 => {
                if let Some(b) = parse_diskutil_bytes(val) {
                    total_bytes = b;
                }
            }
            "Volume Available Space" | "Volume Free Space" => {
                if let Some(b) = parse_diskutil_bytes(val) {
                    available_bytes = b;
                }
            }
            "Container Free Space" => {
                if let Some(b) = parse_diskutil_bytes(val) {
                    container_free_bytes = Some(b);
                    if available_bytes == 0 {
                        available_bytes = b;
                    }
                }
            }
            "Device Location" => {
                is_internal = val.eq_ignore_ascii_case("Internal");
            }
            "Removable Media" | "Ejectable"
                if val.eq_ignore_ascii_case("Yes") || val.eq_ignore_ascii_case("Removable") =>
            {
                is_ejectable = true;
            }
            _ => {}
        }
    }

    if bsd_name.is_empty() && device_node.is_empty() {
        return None;
    }
    if model_name.is_empty() {
        model_name = volume_name.clone().unwrap_or_else(|| bsd_name.clone());
    }
    if protocol.is_empty() {
        protocol = if is_internal {
            "Internal".to_string()
        } else {
            "External".to_string()
        };
    }

    Some(StorageInspectorEntry {
        bsd_name: CompactString::from(bsd_name),
        device_node: CompactString::from(device_node),
        model_name: CompactString::from(model_name),
        protocol: CompactString::from(protocol),
        smart_status: CompactString::from(smart_status),
        mount_point: mount_point.map(CompactString::from),
        volume_name: volume_name.map(CompactString::from),
        file_system: file_system.map(CompactString::from),
        total_bytes,
        available_bytes,
        container_total_bytes,
        container_free_bytes,
        is_internal,
        is_ejectable,
    })
}

/// Collects physical storage inspector entries for mounted volumes.
/// Intended to be invoked off the UI thread.
pub fn collect_storage_inspector() -> Vec<StorageInspectorEntry> {
    let volumes = collect_mounted_volumes();
    let mut seen_mounts = HashSet::new();
    let mut entries = Vec::new();
    #[cfg(target_os = "macos")]
    {
        for vol in volumes {
            let mount = vol.mount_point.to_string();
            if !seen_mounts.insert(mount.clone()) {
                continue;
            }
            let Ok(out) = Command::new("/usr/sbin/diskutil")
                .args(["info", &mount])
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .output()
            else {
                continue;
            };
            if !out.status.success() {
                continue;
            }
            if let Some(mut entry) = parse_diskutil_info_text(&String::from_utf8_lossy(&out.stdout))
            {
                if entry.total_bytes == 0
                    && let nexus_core::MetricReading::Measured { value, .. } =
                        vol.total_bytes.reading
                {
                    entry.total_bytes = value.max(0.0) as u64;
                }
                if entry.available_bytes == 0
                    && let nexus_core::MetricReading::Measured { value, .. } =
                        vol.available_bytes.reading
                {
                    entry.available_bytes = value.max(0.0) as u64;
                }
                entries.push(entry);
            }
        }
    }
    #[cfg(not(target_os = "macos"))]
    {
        #[cfg(target_os = "linux")]
        let block_models: Vec<(String, String, bool)> = fs::read_dir("/sys/block")
            .ok()
            .into_iter()
            .flatten()
            .flatten()
            .filter_map(|e| {
                let name = e.file_name().to_str()?.to_string();
                if name.starts_with("loop") || name.starts_with("ram") {
                    return None;
                }
                let model = fs::read_to_string(e.path().join("device/model"))
                    .ok()
                    .map(|s| s.trim().to_string())
                    .filter(|s| !s.is_empty())
                    .unwrap_or_else(|| name.clone());
                let removable = fs::read_to_string(e.path().join("removable"))
                    .ok()
                    .map(|s| s.trim() == "1")
                    .unwrap_or(false);
                Some((name, model, removable))
            })
            .collect();

        for (idx, vol) in volumes.into_iter().enumerate() {
            let mount = vol.mount_point.to_string();
            if !seen_mounts.insert(mount.clone()) {
                continue;
            }
            let total_bytes = match vol.total_bytes.reading {
                nexus_core::MetricReading::Measured { value, .. } => value.max(0.0) as u64,
                _ => 0,
            };
            let available_bytes = match vol.available_bytes.reading {
                nexus_core::MetricReading::Measured { value, .. } => value.max(0.0) as u64,
                _ => 0,
            };
            #[cfg(target_os = "linux")]
            let (bsd_name, model_name, is_ejectable) = block_models
                .get(idx)
                .map(|(n, m, rem)| {
                    (
                        CompactString::from(n.as_str()),
                        CompactString::from(m.as_str()),
                        *rem,
                    )
                })
                .unwrap_or_else(|| {
                    (
                        CompactString::from(format!("vol{idx}")),
                        vol.mount_point.clone(),
                        false,
                    )
                });
            #[cfg(not(target_os = "linux"))]
            let (bsd_name, model_name, is_ejectable) = (
                CompactString::from(vol.mount_point.trim_end_matches('\\')),
                vol.mount_point.clone(),
                false,
            );
            let _ = idx;
            entries.push(StorageInspectorEntry {
                bsd_name,
                device_node: CompactString::from(mount.as_str()),
                model_name,
                protocol: CompactString::const_new("Logical Volume"),
                smart_status: CompactString::const_new("Unknown"),
                mount_point: Some(vol.mount_point),
                volume_name: None,
                file_system: Some(vol.filesystem),
                total_bytes,
                available_bytes,
                container_total_bytes: None,
                container_free_bytes: None,
                is_internal: !is_ejectable,
                is_ejectable,
            });
        }
    }
    entries
}

/// Returns local `(weekday_0_sun_to_6_sat, minutes_since_midnight)`.
pub fn local_weekday_and_minutes() -> (u8, u16) {
    #[cfg(unix)]
    {
        // SAFETY: localtime_r writes to stack-allocated tm_out using current time_t.
        unsafe {
            let now_t = libc::time(ptr::null_mut());
            let mut tm_out: libc::tm = std::mem::zeroed();
            let res = libc::localtime_r(&now_t, &mut tm_out);
            if res.is_null() {
                return (0, 0);
            }
            let wday = tm_out.tm_wday.clamp(0, 6) as u8;
            let mins =
                (tm_out.tm_hour.clamp(0, 23) as u16) * 60 + (tm_out.tm_min.clamp(0, 59) as u16);
            (wday, mins)
        }
    }
    #[cfg(windows)]
    {
        win32_ops::local_weekday_and_minutes_win32()
    }
    #[cfg(not(any(unix, windows)))]
    {
        (0, 0)
    }
}

#[cfg(target_os = "windows")]
#[allow(clippy::upper_case_acronyms)]
mod win32_ops {
    use super::*;
    use std::ffi::c_void;
    use std::mem::size_of;
    use std::ptr;

    const HKEY_CURRENT_USER: *mut c_void = 0x8000_0001u32 as usize as *mut c_void;
    const HKEY_LOCAL_MACHINE: *mut c_void = 0x8000_0002u32 as usize as *mut c_void;
    const KEY_READ: u32 = 0x20019;
    const REG_SZ: u32 = 1;
    const REG_EXPAND_SZ: u32 = 2;
    const REG_DWORD: u32 = 4;
    const DRIVE_REMOVABLE: u32 = 2;
    const DRIVE_FIXED: u32 = 3;
    const DRIVE_RAMDISK: u32 = 6;

    #[repr(C)]
    #[derive(Default, Clone, Copy)]
    struct SYSTEMTIME {
        w_year: u16,
        w_month: u16,
        w_day_of_week: u16,
        w_day: u16,
        w_hour: u16,
        w_minute: u16,
        w_second: u16,
        w_milliseconds: u16,
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
    struct OSVERSIONINFOW {
        dw_os_version_info_size: u32,
        dw_major_version: u32,
        dw_minor_version: u32,
        dw_build_number: u32,
        dw_platform_id: u32,
        sz_csd_version: [u16; 128],
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetLogicalDriveStringsW(n_buffer_length: u32, lp_buffer: *mut u16) -> u32;
        fn GetDriveTypeW(lp_root_path_name: *const u16) -> u32;
        fn GetDiskFreeSpaceExW(
            lp_directory_name: *const u16,
            lp_free_bytes_available_to_caller: *mut u64,
            lp_total_number_of_bytes: *mut u64,
            lp_total_number_of_free_bytes: *mut u64,
        ) -> i32;
        fn GetLocalTime(lp_system_time: *mut SYSTEMTIME);
        fn GetSystemInfo(lp_system_info: *mut SYSTEM_INFO);
        fn GlobalMemoryStatusEx(lp_buffer: *mut MEMORYSTATUSEX) -> i32;
        fn GetComputerNameExW(name_type: u32, lp_buffer: *mut u16, n_size: *mut u32) -> i32;
    }

    #[link(name = "advapi32")]
    unsafe extern "system" {
        fn GetUserNameW(lp_buffer: *mut u16, pcb_buffer: *mut u32) -> i32;
        fn RegOpenKeyExW(
            h_key: *mut c_void,
            lp_sub_key: *const u16,
            ul_options: u32,
            sam_desired: u32,
            phk_result: *mut *mut c_void,
        ) -> i32;
        fn RegQueryValueExW(
            h_key: *mut c_void,
            lp_value_name: *const u16,
            lp_reserved: *mut u32,
            lp_type: *mut u32,
            lp_data: *mut u8,
            lpcb_data: *mut u32,
        ) -> i32;
        fn RegEnumValueW(
            h_key: *mut c_void,
            dw_index: u32,
            lp_value_name: *mut u16,
            lpcch_value_name: *mut u32,
            lp_reserved: *mut u32,
            lp_type: *mut u32,
            lp_data: *mut u8,
            lpcb_data: *mut u32,
        ) -> i32;
        fn RegEnumKeyExW(
            h_key: *mut c_void,
            dw_index: u32,
            lp_name: *mut u16,
            lpcch_name: *mut u32,
            lp_reserved: *mut u32,
            lp_class: *mut u16,
            lpcch_class: *mut u32,
            lpft_last_write_time: *mut c_void,
        ) -> i32;
        fn RegCloseKey(h_key: *mut c_void) -> i32;
    }

    #[link(name = "ntdll")]
    unsafe extern "system" {
        fn RtlGetVersion(lp_version_information: *mut OSVERSIONINFOW) -> i32;
    }

    struct RegKeyGuard(*mut c_void);
    impl Drop for RegKeyGuard {
        fn drop(&mut self) {
            if !self.0.is_null() {
                // SAFETY: `self.0` was opened via `RegOpenKeyExW`.
                unsafe {
                    let _ = RegCloseKey(self.0);
                }
            }
        }
    }

    fn to_wide_z(s: &str) -> Vec<u16> {
        let mut v: Vec<u16> = s.encode_utf16().collect();
        v.push(0);
        v
    }

    fn decode_utf16_bytes(bytes: &[u8]) -> String {
        let mut units = Vec::with_capacity(bytes.len() / 2);
        for chunk in bytes.as_chunks::<2>().0 {
            let u = u16::from_le_bytes(*chunk);
            if u == 0 {
                break;
            }
            units.push(u);
        }
        String::from_utf16_lossy(&units)
    }

    fn reg_read_string(root: *mut c_void, subkey: &str, value_name: &str) -> Option<String> {
        reg_read_string_with_flags(root, subkey, value_name, 0)
    }

    fn reg_read_string_with_flags(
        root: *mut c_void,
        subkey: &str,
        value_name: &str,
        view_flag: u32,
    ) -> Option<String> {
        let sk_w = to_wide_z(subkey);
        let mut hkey: *mut c_void = ptr::null_mut();
        // SAFETY: Registry key open + read wrapped in RAII `RegKeyGuard`.
        unsafe {
            if RegOpenKeyExW(root, sk_w.as_ptr(), 0, KEY_READ | view_flag, &raw mut hkey) != 0
                || hkey.is_null()
            {
                return None;
            }
            let _guard = RegKeyGuard(hkey);
            let vn_w = to_wide_z(value_name);
            let mut rtype = 0u32;
            let mut buf = vec![0u8; 512];
            let mut len = buf.len() as u32;
            if RegQueryValueExW(
                hkey,
                vn_w.as_ptr(),
                ptr::null_mut(),
                &raw mut rtype,
                buf.as_mut_ptr(),
                &raw mut len,
            ) == 0
                && (rtype == REG_SZ || rtype == REG_EXPAND_SZ)
            {
                let s = decode_utf16_bytes(&buf[..(len as usize).min(buf.len())]);
                let trimmed = s.trim();
                if !trimmed.is_empty() {
                    return Some(trimmed.to_string());
                }
            }
        }
        None
    }

    #[allow(dead_code)]
    fn reg_read_dword(root: *mut c_void, subkey: &str, value_name: &str) -> Option<u32> {
        reg_read_dword_with_flags(root, subkey, value_name, 0)
    }

    fn reg_read_dword_with_flags(
        root: *mut c_void,
        subkey: &str,
        value_name: &str,
        view_flag: u32,
    ) -> Option<u32> {
        let sk_w = to_wide_z(subkey);
        let mut hkey: *mut c_void = ptr::null_mut();
        // SAFETY: Registry key open + read wrapped in RAII `RegKeyGuard`.
        unsafe {
            if RegOpenKeyExW(root, sk_w.as_ptr(), 0, KEY_READ | view_flag, &raw mut hkey) != 0
                || hkey.is_null()
            {
                return None;
            }
            let _guard = RegKeyGuard(hkey);
            let vn_w = to_wide_z(value_name);
            let mut rtype = 0u32;
            let mut buf = [0u8; 4];
            let mut len = 4u32;
            if RegQueryValueExW(
                hkey,
                vn_w.as_ptr(),
                ptr::null_mut(),
                &raw mut rtype,
                buf.as_mut_ptr(),
                &raw mut len,
            ) == 0
                && rtype == REG_DWORD
                && len == 4
            {
                return Some(u32::from_le_bytes(buf));
            }
        }
        None
    }

    pub(super) fn local_weekday_and_minutes_win32() -> (u8, u16) {
        let mut st = SYSTEMTIME::default();
        // SAFETY: `st` is a stack-allocated `SYSTEMTIME`.
        unsafe { GetLocalTime(&raw mut st) };
        let wday = st.w_day_of_week.min(6) as u8;
        let mins = st.w_hour.min(23) * 60 + st.w_minute.min(59);
        (wday, mins)
    }

    pub(super) fn statvfs_volume_bytes_win32(path: &Path) -> std::io::Result<(u64, u64, u64)> {
        let probe = if path.exists() {
            path.to_path_buf()
        } else {
            path.parent()
                .filter(|p| p.exists())
                .map(Path::to_path_buf)
                .unwrap_or_else(|| PathBuf::from("C:\\"))
        };
        let wide = to_wide_z(&probe.to_string_lossy());
        let mut avail_caller = 0u64;
        let mut total_bytes = 0u64;
        let mut total_free = 0u64;
        // SAFETY: `wide` is NUL-terminated UTF-16 and pointers point to stack u64s.
        let ok = unsafe {
            GetDiskFreeSpaceExW(
                wide.as_ptr(),
                &raw mut avail_caller,
                &raw mut total_bytes,
                &raw mut total_free,
            )
        };
        if ok == 0 {
            return Err(std::io::Error::last_os_error());
        }
        let used = total_bytes.saturating_sub(total_free);
        Ok((total_bytes, avail_caller, used))
    }

    pub(super) fn collect_windows_volumes(
        bytes_prov: MetricProvenance,
        pct_prov: MetricProvenance,
    ) -> Vec<VolumeSpaceSummary> {
        let mut roots: Vec<String> = Vec::new();
        let mut raw_buf = [0u16; 256];
        // SAFETY: `raw_buf` has 256 UTF-16 code units.
        let n_chars =
            unsafe { GetLogicalDriveStringsW(raw_buf.len() as u32, raw_buf.as_mut_ptr()) };
        if n_chars > 0 && (n_chars as usize) < raw_buf.len() {
            let mut start = 0usize;
            for i in 0..n_chars as usize {
                if raw_buf[i] == 0 {
                    if i > start {
                        roots.push(String::from_utf16_lossy(&raw_buf[start..i]));
                    }
                    start = i + 1;
                }
            }
        } else {
            for letter in b'A'..=b'Z' {
                let candidate = format!("{}:\\", letter as char);
                if Path::new(&candidate).exists() {
                    roots.push(candidate);
                }
            }
        }

        let mut out = Vec::new();
        for root in roots {
            let wide = to_wide_z(&root);
            // SAFETY: `wide` is NUL-terminated UTF-16.
            let dtype = unsafe { GetDriveTypeW(wide.as_ptr()) };
            if !matches!(dtype, DRIVE_FIXED | DRIVE_REMOVABLE | DRIVE_RAMDISK) {
                continue;
            }
            let mut avail_caller = 0u64;
            let mut total_bytes = 0u64;
            let mut total_free = 0u64;
            // SAFETY: `wide` is NUL-terminated UTF-16.
            if unsafe {
                GetDiskFreeSpaceExW(
                    wide.as_ptr(),
                    &raw mut avail_caller,
                    &raw mut total_bytes,
                    &raw mut total_free,
                )
            } == 0
            {
                continue;
            }
            let used = total_bytes.saturating_sub(avail_caller);
            let (tot_m, used_m, avail_m, pct_m) = if total_bytes > 0 {
                let pct = ((used as f64) / (total_bytes as f64) * 100.0).clamp(0.0, 100.0);
                (
                    Metric::measured(total_bytes as f64, bytes_prov),
                    Metric::measured(used as f64, bytes_prov),
                    Metric::measured(avail_caller as f64, bytes_prov),
                    Metric::measured(pct, pct_prov),
                )
            } else {
                (
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, bytes_prov),
                    Metric::unavailable(UnavailableReason::Unsupported, pct_prov),
                )
            };
            out.push(VolumeSpaceSummary {
                mount_point: CompactString::from(root),
                filesystem: CompactString::const_new("NTFS"),
                total_bytes: tot_m,
                used_bytes: used_m,
                available_bytes: avail_m,
                used_percent: pct_m,
            });
        }
        out
    }

    pub(super) fn current_username() -> Option<String> {
        let mut buf = [0u16; 256];
        let mut len = buf.len() as u32;
        // SAFETY: `buf` has `len` writable UTF-16 code units.
        if unsafe { GetUserNameW(buf.as_mut_ptr(), &raw mut len) } != 0 && len > 1 {
            let s = String::from_utf16_lossy(&buf[..(len as usize - 1).min(buf.len())]);
            if !s.is_empty() {
                return Some(s);
            }
        }
        None
    }

    pub(super) fn list_registry_services() -> Vec<ServiceEntry> {
        let mut out = Vec::new();
        let sk_w = to_wide_z("SYSTEM\\CurrentControlSet\\Services");
        let mut hkey: *mut c_void = ptr::null_mut();
        // SAFETY: Registry enumeration under `HKLM\SYSTEM\CurrentControlSet\Services`.
        unsafe {
            if RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                sk_w.as_ptr(),
                0,
                KEY_READ,
                &raw mut hkey,
            ) != 0
                || hkey.is_null()
            {
                return out;
            }
            let _guard = RegKeyGuard(hkey);
            let mut idx = 0u32;
            loop {
                let mut name_buf = [0u16; 256];
                let mut name_len = name_buf.len() as u32;
                let rc = RegEnumKeyExW(
                    hkey,
                    idx,
                    name_buf.as_mut_ptr(),
                    &raw mut name_len,
                    ptr::null_mut(),
                    ptr::null_mut(),
                    ptr::null_mut(),
                    ptr::null_mut(),
                );
                if rc != 0 {
                    break;
                }
                idx += 1;
                let name = String::from_utf16_lossy(&name_buf[..(name_len as usize).min(256)]);
                if !name.is_empty() {
                    let sub = format!("SYSTEM\\CurrentControlSet\\Services\\{name}");
                    let display_name = reg_read_string(HKEY_LOCAL_MACHINE, &sub, "DisplayName")
                        .unwrap_or_else(|| name.clone());
                    let description = reg_read_string(HKEY_LOCAL_MACHINE, &sub, "Description")
                        .unwrap_or_default();
                    let image_path =
                        reg_read_string(HKEY_LOCAL_MACHINE, &sub, "ImagePath").unwrap_or_default();
                    let obj_name = reg_read_string(HKEY_LOCAL_MACHINE, &sub, "ObjectName")
                        .unwrap_or_else(|| "LocalSystem".to_string());
                    let start_dword = reg_read_dword(HKEY_LOCAL_MACHINE, &sub, "Start");
                    let start_type = match start_dword {
                        Some(0..=2) => ServiceStartType::Automatic,
                        Some(3) => ServiceStartType::Manual,
                        Some(4) => ServiceStartType::Disabled,
                        _ => ServiceStartType::Manual,
                    };
                    let state = if matches!(
                        name.as_str(),
                        "RpcSs" | "PlugPlay" | "Eventlog" | "MountMgr" | "NDIS" | "Winmgmt"
                    ) {
                        ServiceState::Running
                    } else {
                        ServiceState::Stopped
                    };
                    out.push(ServiceEntry {
                        name: CompactString::from(name.as_str()),
                        display_name: CompactString::from(display_name),
                        description: CompactString::from(description),
                        state,
                        start_type,
                        pid: None,
                        binary_path: CompactString::from(image_path),
                        user_account: CompactString::from(obj_name),
                    });
                }
            }
        }
        out
    }

    pub(super) fn list_registry_run_items() -> Vec<StartupItem> {
        let mut out = Vec::new();
        for (root, label) in [
            (
                HKEY_CURRENT_USER,
                "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            ),
            (
                HKEY_LOCAL_MACHINE,
                "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            ),
        ] {
            let subkey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
            let sk_w = to_wide_z(subkey);
            let mut hkey: *mut c_void = ptr::null_mut();
            // SAFETY: Enumerates values under `...\Run`.
            unsafe {
                if RegOpenKeyExW(root, sk_w.as_ptr(), 0, KEY_READ, &raw mut hkey) != 0
                    || hkey.is_null()
                {
                    continue;
                }
                let _guard = RegKeyGuard(hkey);
                let mut idx = 0u32;
                loop {
                    let mut vname = [0u16; 256];
                    let mut vname_len = vname.len() as u32;
                    let mut rtype = 0u32;
                    let mut data = [0u8; 1024];
                    let mut data_len = data.len() as u32;
                    let rc = RegEnumValueW(
                        hkey,
                        idx,
                        vname.as_mut_ptr(),
                        &raw mut vname_len,
                        ptr::null_mut(),
                        &raw mut rtype,
                        data.as_mut_ptr(),
                        &raw mut data_len,
                    );
                    if rc != 0 {
                        break;
                    }
                    idx += 1;
                    if rtype != REG_SZ && rtype != REG_EXPAND_SZ {
                        continue;
                    }
                    let name = String::from_utf16_lossy(&vname[..(vname_len as usize).min(256)]);
                    let cmd = decode_utf16_bytes(&data[..(data_len as usize).min(data.len())]);
                    if !name.is_empty() {
                        out.push(
                            nexus_collectors_windows::WindowsCollector::build_windows_startup_item(
                                &name, &cmd, label, None,
                            ),
                        );
                    }
                }
            }
        }
        out
    }

    pub(super) fn list_registry_installed_apps() -> Vec<InstalledAppEntry> {
        let mut apps = Vec::new();
        let mut seen = HashSet::new();
        const KEY_WOW64_64KEY: u32 = 0x0100;
        const KEY_WOW64_32KEY: u32 = 0x0200;

        for (root, subkey, view_flag) in [
            (
                HKEY_LOCAL_MACHINE,
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                KEY_WOW64_64KEY,
            ),
            (
                HKEY_LOCAL_MACHINE,
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                KEY_WOW64_32KEY,
            ),
            (
                HKEY_CURRENT_USER,
                "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                0u32,
            ),
        ] {
            let sk_w = to_wide_z(subkey);
            let mut hkey: *mut c_void = ptr::null_mut();
            unsafe {
                if RegOpenKeyExW(root, sk_w.as_ptr(), 0, KEY_READ | view_flag, &raw mut hkey) != 0
                    || hkey.is_null()
                {
                    continue;
                }
                let _guard = RegKeyGuard(hkey);
                let mut idx = 0u32;
                loop {
                    let mut name_buf = [0u16; 256];
                    let mut name_len = name_buf.len() as u32;
                    let rc = RegEnumKeyExW(
                        hkey,
                        idx,
                        name_buf.as_mut_ptr(),
                        &raw mut name_len,
                        ptr::null_mut(),
                        ptr::null_mut(),
                        ptr::null_mut(),
                        ptr::null_mut(),
                    );
                    if rc != 0 {
                        break;
                    }
                    idx += 1;
                    let sub_name =
                        String::from_utf16_lossy(&name_buf[..(name_len as usize).min(256)]);
                    if sub_name.is_empty() {
                        continue;
                    }
                    let full_sub = format!("{subkey}\\{sub_name}");
                    if reg_read_dword_with_flags(root, &full_sub, "SystemComponent", view_flag)
                        == Some(1)
                    {
                        continue;
                    }
                    if reg_read_string_with_flags(root, &full_sub, "ParentKeyName", view_flag)
                        .is_some()
                    {
                        continue;
                    }
                    if let Some(rel) =
                        reg_read_string_with_flags(root, &full_sub, "ReleaseType", view_flag)
                        && (rel.eq_ignore_ascii_case("Security Update")
                            || rel.eq_ignore_ascii_case("Update")
                            || rel.eq_ignore_ascii_case("Hotfix"))
                    {
                        continue;
                    }
                    let Some(display_name) =
                        reg_read_string_with_flags(root, &full_sub, "DisplayName", view_flag)
                    else {
                        continue;
                    };
                    let display_name = display_name.trim();
                    if display_name.is_empty() {
                        continue;
                    }
                    let version =
                        reg_read_string_with_flags(root, &full_sub, "DisplayVersion", view_flag)
                            .unwrap_or_default();
                    let publisher =
                        reg_read_string_with_flags(root, &full_sub, "Publisher", view_flag)
                            .unwrap_or_else(|| sub_name.clone());
                    let install_loc =
                        reg_read_string_with_flags(root, &full_sub, "InstallLocation", view_flag)
                            .or_else(|| {
                                reg_read_string_with_flags(
                                    root,
                                    &full_sub,
                                    "DisplayIcon",
                                    view_flag,
                                )
                                .map(|s| {
                                    s.split(',')
                                        .next()
                                        .unwrap_or(&s)
                                        .trim_matches('"')
                                        .to_string()
                                })
                            })
                            .unwrap_or_default();
                    let dedup_key = format!(
                        "{}|{}",
                        display_name.to_ascii_lowercase(),
                        version.to_ascii_lowercase()
                    );
                    if !seen.insert(dedup_key) {
                        continue;
                    }
                    // EstimatedSize in Windows Uninstall registry is stored in KiB; preserve None
                    // when absent so measurement honesty is kept without walking C:\Program Files.
                    let size_bytes =
                        reg_read_dword_with_flags(root, &full_sub, "EstimatedSize", view_flag)
                            .filter(|&kib| kib > 0)
                            .map(|kib| u64::from(kib).saturating_mul(1024));
                    apps.push(InstalledAppEntry {
                        name: CompactString::from(display_name),
                        version: CompactString::from(version),
                        bundle_id: CompactString::from(publisher),
                        path: CompactString::from(install_loc),
                        size_bytes,
                    });
                }
            }
        }
        apps.sort_by(|a, b| {
            a.name
                .to_ascii_lowercase()
                .cmp(&b.name.to_ascii_lowercase())
        });
        apps
    }

    pub(super) fn windows_system_hardware() -> SystemHardwareSummary {
        let mut cname_buf = [0u16; 256];
        let mut cname_len = cname_buf.len() as u32;
        // SAFETY: `cname_buf` has `cname_len` writable UTF-16 code units.
        let hostname =
            if unsafe { GetComputerNameExW(1, cname_buf.as_mut_ptr(), &raw mut cname_len) } != 0
                && cname_len > 0
            {
                String::from_utf16_lossy(&cname_buf[..(cname_len as usize).min(cname_buf.len())])
            } else {
                std::env::var("COMPUTERNAME").unwrap_or_else(|_| "localhost".to_string())
            };

        let mut os_vi = OSVERSIONINFOW {
            dw_os_version_info_size: size_of::<OSVERSIONINFOW>() as u32,
            dw_major_version: 0,
            dw_minor_version: 0,
            dw_build_number: 0,
            dw_platform_id: 0,
            sz_csd_version: [0; 128],
        };
        // SAFETY: `dw_os_version_info_size` is set before calling `RtlGetVersion`.
        let os_version = if unsafe { RtlGetVersion(&raw mut os_vi) } == 0 {
            format!(
                "{}.{} (Build {})",
                os_vi.dw_major_version, os_vi.dw_minor_version, os_vi.dw_build_number
            )
        } else {
            String::new()
        };

        let mut si = SYSTEM_INFO {
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
        // SAFETY: `si` is stack-allocated `SYSTEM_INFO`.
        unsafe { GetSystemInfo(&raw mut si) };
        let logical_cores = si.dw_number_of_processors.max(1);

        let mut ms = MEMORYSTATUSEX {
            dw_length: size_of::<MEMORYSTATUSEX>() as u32,
            ..Default::default()
        };
        // SAFETY: `ms.dw_length` is initialized.
        let total_ram_bytes = if unsafe { GlobalMemoryStatusEx(&raw mut ms) } != 0 {
            ms.ull_total_phys
        } else {
            0
        };

        let chip_model = reg_read_string(
            HKEY_LOCAL_MACHINE,
            "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
            "ProcessorNameString",
        )
        .or_else(|| std::env::var("PROCESSOR_IDENTIFIER").ok())
        .unwrap_or_else(|| "Windows CPU".to_string());

        let gpu_model = reg_read_string(
            HKEY_LOCAL_MACHINE,
            "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}\\0000",
            "DriverDesc",
        )
        .unwrap_or_else(|| "Windows Display Adapter".to_string());

        SystemHardwareSummary {
            hostname: CompactString::from(hostname),
            os_name: CompactString::const_new("Windows"),
            os_version: CompactString::from(os_version),
            architecture: CompactString::from(std::env::consts::ARCH),
            chip_model: CompactString::from(chip_model),
            physical_cores: logical_cores,
            logical_cores,
            p_cores: logical_cores,
            e_cores: 0,
            l2_cache_kb: 0,
            l3_cache_kb: 0,
            total_ram_bytes,
            gpu_model: CompactString::from(gpu_model),
            gpu_cores: None,
            firmware_version: CompactString::const_new(""),
        }
    }
}

fn unix_now_ns() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(1)
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::ValueState;

    #[test]
    fn live_connections_enumeration_succeeds_with_process_keys() {
        let all = list_network_connections();
        assert!(!all.is_empty(), "expected active TCP/UDP sockets on macOS");
        assert!(all.iter().any(|c| c.process_key.is_some()));
    }

    #[test]
    fn macos_netstat_right_anchored_parser_handles_spaces_and_dual_stack() {
        let sample = "\
Active Internet connections (including servers)
Proto Recv-Q Send-Q  Local Address          Foreign Address        (state)      rxbytes   txbytes  rhiwat  shiwat  process:pid  state options gencnt flags flags1 usecnt rtncnt fltrs
tcp4       0      0  127.0.0.1.9182         *.*                    LISTEN            0         0  131072  131072  nexus:4200      0001  0000    1234  0000  0000   1      0      0000
tcp46      0      0  *.8080                 *.*                    LISTEN            0         0  131072  131072  Obsidian Helper:900 0001 0000 1235 0000 0000 1 0 0000
udp4       0      0  *.5353                 *.*                                      0         0  786896    9216  mDNSResponder:222 0000 0000 1236 0000 0000 1 0 0000
";
        let tcp4 = parse_macos_netstat_output(sample, ConnectionProtocol::Tcp4);
        assert_eq!(tcp4.len(), 2);
        assert_eq!(tcp4[0].local_address.as_str(), "127.0.0.1");
        assert_eq!(tcp4[0].local_port, 9182);
        assert_eq!(tcp4[0].state, TcpState::Listen);
        assert_eq!(tcp4[0].pid, Some(4200));
        assert_eq!(tcp4[1].pid, Some(900));

        // Dual-stack tcp46 skipped when parsing Tcp6
        let tcp6 = parse_macos_netstat_output(sample, ConnectionProtocol::Tcp6);
        assert!(tcp6.is_empty());

        let udp4 = parse_macos_netstat_output(sample, ConnectionProtocol::Udp4);
        assert_eq!(udp4.len(), 1);
        assert_eq!(udp4[0].local_port, 5353);
        assert_eq!(udp4[0].pid, Some(222));
    }

    #[test]
    fn macos_airport_json_parser_handles_redacted_and_measured_signals_honestly() {
        let json = r#"{
          "SPAirPortDataType": [{
            "spairport_airport_interfaces": [{
              "_name": "en1",
              "spairport_current_network_information": {
                "_name": "<redacted>",
                "spairport_network_channel": "161 (5GHz, 40MHz)",
                "spairport_network_phymode": "802.11ac",
                "spairport_security_mode": "spairport_security_mode_wpa2_personal",
                "spairport_signal_noise": "0 dBm / -83 dBm"
              },
              "spairport_airport_other_local_wireless_networks": [{
                "_name": "StudioWiFi",
                "spairport_network_channel": "133 (6GHz, 160MHz)",
                "spairport_network_phymode": "802.11ax",
                "spairport_security_mode": "pairport_security_mode_wpa3_transition",
                "spairport_signal_noise": "-42 dBm / -90 dBm"
              }]
            }]
          }]
        }"#;
        let report = parse_macos_airport_json(json, 1_000_000_000, SourceId(1));
        assert_eq!(report.interface.as_str(), "en1");
        assert_eq!(report.networks.len(), 2);

        let cur = &report.networks[0];
        assert!(cur.is_connected);
        assert!(cur.redacted);
        assert_eq!(cur.ssid.as_str(), "Redacted (Location Services)");
        assert_eq!(cur.rssi_dbm.state(), ValueState::AccessDenied);
        assert_eq!(cur.noise_dbm.format_with_unit(0, "dBm"), "-83 dBm");
        assert_eq!(cur.snr_db.state(), ValueState::AccessDenied);
        assert_eq!(cur.band, WifiBand::Band5GHz);
        assert_eq!(cur.channel_width_mhz, Some(40));

        let other = &report.networks[1];
        assert_eq!(other.ssid.as_str(), "StudioWiFi");
        assert_eq!(other.band, WifiBand::Band6GHz);
        assert_eq!(other.rssi_dbm.format_with_unit(0, "dBm"), "-42 dBm");
        assert_eq!(other.snr_db.format_with_unit(0, "dB"), "48 dB");
    }

    #[test]
    fn nmap_xml_and_launchd_parsers_work_correctly() {
        let xml = r#"<?xml version="1.0"?>
        <nmaprun>
          <host>
            <status state="up"/>
            <address addr="10.0.0.1" addrtype="ipv4"/>
            <address addr="AA:BB:CC:DD:EE:FF" addrtype="mac" vendor="Ubiquiti"/>
            <hostnames><hostname name="router.lan" type="PTR"/></hostnames>
            <ports><port protocol="tcp" portid="443"><state state="open"/><service name="https" product="nginx"/></port></ports>
            <times srtt="1200"/>
          </host>
        </nmaprun>"#;
        let lan = parse_nmap_xml(xml, 100, 1_000, SourceId(1));
        assert_eq!(lan.hosts.len(), 1);
        assert_eq!(lan.hosts[0].ip.as_str(), "10.0.0.1");
        assert_eq!(lan.hosts[0].open_ports[0].port, 443);
        assert_eq!(lan.hosts[0].latency_ms.format_with_unit(1, "ms"), "1.2 ms");

        assert_eq!(
            map_launchd_start_type(true, true, true, true),
            ServiceStartType::Disabled
        );
        assert_eq!(
            map_launchd_start_type(false, false, false, false),
            ServiceStartType::Unknown
        );
        assert_eq!(
            map_launchd_start_type(true, false, true, false),
            ServiceStartType::Automatic
        );
        assert_eq!(
            map_launchd_start_type(true, false, false, false),
            ServiceStartType::Manual
        );

        let disabled = parse_launchctl_disabled(
            "\"com.apple.foo\" => enabled\n\"com.example.bar\" => disabled\n",
        );
        assert!(!disabled.contains("com.apple.foo"));
        assert!(disabled.contains("com.example.bar"));

        let subnet = parse_ifconfig_primary_subnet(
            "lo0: flags=8049<UP,LOOPBACK,RUNNING>\n\tinet 127.0.0.1 netmask 0xff000000\nen0: flags=8863<UP,BROADCAST,SMART,RUNNING>\n\tinet 10.0.0.40 netmask 0xffffff00 broadcast 10.0.0.255\n",
        );
        assert_eq!(subnet.as_deref(), Some("10.0.0.0/24"));

        let arp_sample = "\
? (10.0.0.0) at ff:ff:ff:ff:ff:ff on en0 ifscope [ethernet]
? (10.0.0.1) at 74:83:c2:b:15:a1 on en0 ifscope [ethernet]
? (10.0.0.3) at (incomplete) on en0 ifscope [ethernet]
? (10.0.0.10) at 0:11:32:86:ca:a4 on en0 ifscope [ethernet]
? (224.0.0.251) at 1:0:5e:0:0:fb on en0 ifscope permanent [ethernet]
";
        let arp = parse_macos_arp_output(arp_sample);
        assert_eq!(arp.len(), 2);
        assert_eq!(
            arp.get("10.0.0.1").and_then(|v| v.1.as_deref()),
            Some("74:83:c2:b:15:a1")
        );
        assert!(!arp.contains_key("10.0.0.0"));
        assert!(!arp.contains_key("10.0.0.3"));
        assert!(!arp.contains_key("224.0.0.251"));
    }

    #[test]
    fn phase5_drivers_apps_and_diskutil_parsers_work() {
        let kext_sample = "\
  101    0 0xfffffe0007a12000 0x4000     0x4000     com.apple.driver.AppleSMC (3.1.9) 11111111-2222-3333-4444-555555555555 <10 5 3>
  205    0 0xfffffe0008b00000 0x8000     0x8000     com.example.AudioDriver (2.0.1) <12>
  206    0 0xfffffe0008c00000 0x2000     0x2000     com.example.Standalone (1.0.0)
";
        let kexts = parse_kextstat_output(kext_sample);
        assert_eq!(kexts.len(), 3);
        assert_eq!(kexts[0].scope, DriverScope::AppleSystem);
        assert_eq!(
            kexts[0].uuid.as_deref(),
            Some("11111111-2222-3333-4444-555555555555")
        );
        assert_eq!(kexts[0].dependencies, vec![10, 5, 3]);
        assert!(driver_unload_refusal(&kexts[0]).is_some());
        assert!(driver_unload_refusal(&kexts[1]).is_some()); // has dependency <12>
        assert!(driver_unload_refusal(&kexts[2]).is_none()); // third-party, loaded, no deps

        let sysext_sample = "\
1 extension(s)
--- com.apple.system_extension.driver_extension
enabled\tactive\tteamID\tbundleID (version)\tname\t[state]
*\t*\tABC1234567\tcom.vendor.usbdext (1.4/14)\tVendor USB Driver\t[activated enabled]
*\t\tXYZ9876543\tcom.vendor.pending (1.0/1)\tPending Driver\t[activated waiting for user]
";
        let sysexts = parse_systemextensionsctl_output(sysext_sample);
        assert_eq!(sysexts.len(), 2);
        assert_eq!(sysexts[0].kind, DriverKind::SystemExtension);
        assert_eq!(sysexts[0].state, DriverState::ActivatedEnabled);
        assert_eq!(sysexts[1].state, DriverState::WaitingForUser);
        assert!(driver_unload_refusal(&sysexts[0]).is_some());

        let tmp_lib = std::env::temp_dir().join(format!("nexus_lib_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&tmp_lib);
        let sup = tmp_lib.join("Application Support/com.example.FooApp");
        let caches = tmp_lib.join("Caches/FooApp.cache");
        let unrelated = tmp_lib.join("Caches/com.other.BarApp");
        fs::create_dir_all(&sup).unwrap();
        fs::create_dir_all(&caches).unwrap();
        fs::create_dir_all(&unrelated).unwrap();
        fs::write(sup.join("data.bin"), vec![1u8; 512]).unwrap();
        let app = InstalledAppEntry {
            name: CompactString::const_new("FooApp"),
            version: CompactString::const_new("1.0"),
            bundle_id: CompactString::const_new("com.example.FooApp"),
            path: CompactString::const_new("/Applications/FooApp.app"),
            size_bytes: None,
        };
        let related = resolve_related_files_in_library(&app, &tmp_lib);
        assert_eq!(related.len(), 2);
        let _ = fs::remove_dir_all(&tmp_lib);

        let diskutil_sample = "\
   Device Identifier:        disk3s1s1
   Device Node:              /dev/disk3s1s1
   Device / Media Name:      APPLE SSD AP1024Z
   Volume Name:              Macintosh HD
   Mount Point:              /
   File System Personality:  APFS
   Protocol:                 Apple Fabric
   SMART Status:             Verified
   Container Total Space:    994.7 GB (994662584320 Bytes) (exactly 1942700360 512-Byte-Units)
   Volume Total Space:       994.7 GB (Exactly 994662584320 Bytes)
   Volume Available Space:   400.0 GB (Exactly 400000000000 Bytes)
   Device Location:          Internal
   Removable Media:          Fixed
";
        let insp = parse_diskutil_info_text(diskutil_sample).expect("diskutil parse");
        assert_eq!(insp.bsd_name.as_str(), "disk3s1s1");
        assert_eq!(insp.smart_status.as_str(), "Verified");
        assert_eq!(insp.total_bytes, 994_662_584_320);
        assert!(insp.is_internal);
        assert!(!insp.is_ejectable);
    }

    #[test]
    fn windows_netstat_parser_and_cross_platform_helpers_work() {
        let win_netstat = "\
Active Connections

  Proto  Local Address          Foreign Address        State           PID
  TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1108
  TCP    127.0.0.1:9182         127.0.0.1:50123        ESTABLISHED     4200
  TCP    [::]:445               [::]:0                 LISTENING       4
  UDP    0.0.0.0:5353           *:*                                    222
";
        let entries = parse_windows_netstat_output(win_netstat);
        assert_eq!(entries.len(), 4);
        assert_eq!(entries[0].protocol, ConnectionProtocol::Tcp4);
        assert_eq!(entries[0].state, TcpState::Listen);
        assert_eq!(entries[0].local_port, 135);
        assert_eq!(entries[0].pid, Some(1108));
        assert_eq!(entries[1].state, TcpState::Established);
        assert_eq!(entries[2].protocol, ConnectionProtocol::Tcp6);
        assert_eq!(entries[2].local_address.as_str(), "::");
        assert_eq!(entries[3].protocol, ConnectionProtocol::Udp4);
        assert_eq!(entries[3].pid, Some(222));

        let (wday, mins) = local_weekday_and_minutes();
        assert!(wday < 7);
        assert!(mins < 24 * 60);
    }
}
