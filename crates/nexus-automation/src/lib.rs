use compact_str::CompactString;
use nexus_collectors_macos::{MacCollector, system_ops::local_weekday_and_minutes};
use nexus_core::{
    Metric, MetricReading, PriorityRestoreReceipt, ProcessKey, ProcessSnapshot, SystemSnapshot,
};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::Write;
use std::net::TcpStream;
#[cfg(target_os = "windows")]
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use thiserror::Error;

/// Exact 6 priority presets (`[-20, -10, -5, 0, 5, 10]`).
pub const PRIORITY_PRESET_VALUES: [i32; 6] = [-20, -10, -5, 0, 5, 10];

/// Lowest Unix nice value (`19`).
pub const UNIX_LOWEST_NICE: i32 = 19;

fn default_true() -> bool {
    true
}

fn default_restore_threshold() -> f64 {
    40.0
}

fn default_idle_cpu_threshold() -> f64 {
    1.0
}

fn default_idle_ticks_required() -> u32 {
    5
}

fn default_mem_alert_threshold() -> f64 {
    90.0
}

fn default_disk_alert_threshold() -> f64 {
    95.0
}

fn default_cooldown_secs() -> u64 {
    60
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProcessActionType {
    Terminate = 0,   // SIGTERM (15)
    ForceKill = 1,   // SIGKILL (9)
    Suspend = 2,     // SIGSTOP (17)
    Resume = 3,      // SIGCONT (19)
    Signal = 4,      // Custom signal 1..=31
    SetPriority = 5, // Nice -20..=20
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct ActionRequest {
    pub action_type: ProcessActionType,
    pub signal: Option<i32>,
    pub priority: Option<i32>,
}

#[derive(Debug, Error, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ActionError {
    #[error("action.terminate.validationFailed (code={code}, errno={errno})")]
    ValidationFailed { code: u32, errno: i32 },
    #[error("action.terminate.targetChanged (PID {pid} creation token mismatch)")]
    TargetChanged { pid: i32 },
    #[error("action.signal.invalid ({0})")]
    InvalidSignal(i32),
    #[error("action.priority.invalid ({0})")]
    InvalidPriority(i32),
    #[error("action.execution.failed (code={code}, errno={errno}, tag={tag})")]
    ExecutionFailed {
        code: u32,
        errno: i32,
        tag: &'static str,
    },
}

/// Returns the nice value for a preset index `0..6`.
#[inline]
pub const fn priority_preset_value(index: usize) -> i32 {
    if index < PRIORITY_PRESET_VALUES.len() {
        PRIORITY_PRESET_VALUES[index]
    } else {
        0
    }
}

/// Safe process lifecycle dispatcher (`ActionCoordinator`).
/// Verifies `ProcessKey` (`pid` + `creation_token_us`) twice (`Resolve` and `Execute`) before sending any signal or priority change.
#[derive(Debug, Default)]
pub struct ActionCoordinator;

impl ActionCoordinator {
    pub fn new() -> Self {
        Self
    }

    /// Resolves and validates `target` against the live OS process table (Pass 1: `Resolve`).
    pub fn resolve_live(target: &ProcessKey) -> Result<ProcessKey, ActionError> {
        let Some((live_key, _, _)) = MacCollector::read_process_key(target.pid) else {
            return Err(ActionError::ValidationFailed { code: 2, errno: 3 });
        };
        if !target.matches_live(&live_key) {
            return Err(ActionError::TargetChanged { pid: target.pid });
        }
        Ok(live_key)
    }

    /// Validates `target` twice (`Resolve` + `Execute`) and executes `req`.
    /// When `dry_run` is true, performs both live `ProcessKey` validations and argument checks without invoking `kill`/`renice`.
    pub fn execute(
        &self,
        target: &ProcessKey,
        req: ActionRequest,
        dry_run: bool,
    ) -> Result<(), ActionError> {
        let resolved = Self::resolve_live(target)?;
        let live = Self::resolve_live(&resolved)?;
        if !target.matches_live(&live) {
            return Err(ActionError::TargetChanged { pid: target.pid });
        }

        match req.action_type {
            ProcessActionType::Terminate => Self::dispatch_signal(live.pid, 15, dry_run),
            ProcessActionType::ForceKill => Self::dispatch_signal(live.pid, 9, dry_run),
            ProcessActionType::Suspend => Self::dispatch_signal(live.pid, 17, dry_run),
            ProcessActionType::Resume => Self::dispatch_signal(live.pid, 19, dry_run),
            ProcessActionType::Signal => {
                let sig = req.signal.unwrap_or(0);
                if !(1..=31).contains(&sig) {
                    return Err(ActionError::InvalidSignal(sig));
                }
                Self::dispatch_signal(live.pid, sig, dry_run)
            }
            ProcessActionType::SetPriority => {
                let prio = req.priority.unwrap_or(999);
                if !(-20..=20).contains(&prio) {
                    return Err(ActionError::InvalidPriority(prio));
                }
                if dry_run {
                    return Ok(());
                }
                #[cfg(target_os = "macos")]
                let status = Command::new("/usr/bin/renice")
                    .arg(prio.to_string())
                    .arg("-p")
                    .arg(live.pid.to_string())
                    .stdout(Stdio::null())
                    .stderr(Stdio::null())
                    .status();
                #[cfg(target_os = "linux")]
                let status = Command::new("renice")
                    .arg(prio.to_string())
                    .arg("-p")
                    .arg(live.pid.to_string())
                    .stdout(Stdio::null())
                    .stderr(Stdio::null())
                    .status();
                #[cfg(target_os = "windows")]
                let status = {
                    let win_class = if prio <= -15 {
                        "128"
                    } else if prio <= -5 {
                        "32768"
                    } else if prio <= 4 {
                        "32"
                    } else if prio <= 14 {
                        "16384"
                    } else {
                        "64"
                    };
                    let res = Command::new("wmic")
                        .args([
                            "process",
                            "where",
                            &format!("processid={}", live.pid),
                            "CALL",
                            "setpriority",
                            win_class,
                        ])
                        .creation_flags(0x0800_0000)
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .status();
                    match res {
                        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
                        other => other,
                    }
                };
                #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
                let status: std::io::Result<std::process::ExitStatus> = Err(std::io::Error::new(
                    std::io::ErrorKind::Unsupported,
                    "unsupported",
                ));
                match status {
                    Ok(s) if s.success() => Ok(()),
                    _ => Err(ActionError::ExecutionFailed {
                        code: 3,
                        errno: 1,
                        tag: "action.priority.failed",
                    }),
                }
            }
        }
    }

    fn dispatch_signal(pid: i32, signal: i32, dry_run: bool) -> Result<(), ActionError> {
        if dry_run {
            return Ok(());
        }
        #[cfg(target_os = "macos")]
        let status = Command::new("/bin/kill")
            .arg(format!("-{signal}"))
            .arg(pid.to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        #[cfg(target_os = "linux")]
        let status = Command::new("kill")
            .arg(format!("-{signal}"))
            .arg(pid.to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        #[cfg(target_os = "windows")]
        let status = {
            if signal == 15 || signal == 9 {
                Command::new("taskkill")
                    .args(["/PID", &pid.to_string(), "/F"])
                    .creation_flags(0x0800_0000)
                    .stdout(Stdio::null())
                    .stderr(Stdio::null())
                    .status()
            } else {
                return Err(ActionError::ExecutionFailed {
                    code: 3,
                    errno: 1,
                    tag: "action.signal.unsupported_on_windows",
                });
            }
        };
        #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
        let status: std::io::Result<std::process::ExitStatus> = Err(std::io::Error::new(
            std::io::ErrorKind::Unsupported,
            "unsupported",
        ));
        match status {
            Ok(s) if s.success() => Ok(()),
            _ => Err(ActionError::ExecutionFailed {
                code: 3,
                errno: 1,
                tag: "action.signal.failed",
            }),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum WatchdogAction {
    #[default]
    None,
    SetBelowNormal,
    SetIdle,
    Terminate,
    ReduceAffinity,
    SetIoPriorityLow,
    SetEfficiencyMode,
    TrimWorkingSet,
    Restart,
    LogOnly,
}

/// Persistent per-executable preferences surviving process restarts.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProcessPreference {
    pub exe_name: CompactString,
    pub priority: Option<i32>,
    pub affinity_mask: Option<u64>,
    #[serde(default)]
    pub efficiency_mode: Option<bool>,
    #[serde(default)]
    pub prevent_sleep: bool,
    pub updated_at_unix_ms: i64,
}

impl ProcessPreference {
    pub fn normalize_exe_name(name: &str) -> CompactString {
        let base = Path::new(name)
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or(name);
        let stripped = base
            .strip_suffix(".exe")
            .or_else(|| base.strip_suffix(".EXE"))
            .unwrap_or(base);
        CompactString::from(stripped.to_ascii_lowercase())
    }
}

/// JSON-backed `ProcessPreferenceStore` with in-memory normalized lookup cache.
#[derive(Debug, Default, Clone, Serialize, Deserialize)]
pub struct ProcessPreferenceStore {
    prefs: HashMap<CompactString, ProcessPreference>,
}

impl ProcessPreferenceStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn load(path: &Path) -> std::io::Result<Self> {
        if !path.exists() {
            return Ok(Self::new());
        }
        let raw = fs::read_to_string(path)?;
        let list: Vec<ProcessPreference> = serde_json::from_str(&raw)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        let mut store = Self::new();
        for mut pref in list {
            pref.exe_name = ProcessPreference::normalize_exe_name(&pref.exe_name);
            store.prefs.insert(pref.exe_name.clone(), pref);
        }
        Ok(store)
    }

    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let list = self.get_all();
        let json = serde_json::to_string_pretty(&list).map_err(std::io::Error::other)?;
        fs::write(path, json)
    }

    pub fn get(&self, exe_name: &str) -> Option<&ProcessPreference> {
        let key = ProcessPreference::normalize_exe_name(exe_name);
        self.prefs.get(&key)
    }

    pub fn get_all(&self) -> Vec<ProcessPreference> {
        let mut items: Vec<ProcessPreference> = self.prefs.values().cloned().collect();
        items.sort_by(|a, b| a.exe_name.cmp(&b.exe_name));
        items
    }

    pub fn upsert(&mut self, mut pref: ProcessPreference) {
        pref.exe_name = ProcessPreference::normalize_exe_name(&pref.exe_name);
        self.prefs.insert(pref.exe_name.clone(), pref);
    }

    pub fn delete(&mut self, exe_name: &str) -> bool {
        let key = ProcessPreference::normalize_exe_name(exe_name);
        self.prefs.remove(&key).is_some()
    }
}

/// Process rule supporting wildcard matching, CPU/memory thresholds, priority/affinity overrides,
/// watchdog actions, instance caps, and sleep prevention.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProcessRule {
    pub id: CompactString,
    pub process_pattern: CompactString,
    pub cpu_percent_threshold: Option<f64>,
    pub memory_bytes_threshold: Option<f64>,
    pub target_priority: Option<i32>,
    pub affinity_mask: Option<u64>,
    pub action: Option<ProcessActionType>,
    #[serde(default)]
    pub name: CompactString,
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default)]
    pub group_name: Option<CompactString>,
    #[serde(default)]
    pub prevent_sleep: bool,
    #[serde(default)]
    pub disallowed: bool,
    #[serde(default)]
    pub keep_running: bool,
    #[serde(default)]
    pub max_instances: Option<usize>,
    #[serde(default)]
    pub efficiency_mode: Option<bool>,
    #[serde(default)]
    pub watchdog_action: WatchdogAction,
    #[serde(default)]
    pub condition_duration_secs: u64,
}

impl Default for ProcessRule {
    fn default() -> Self {
        Self {
            id: CompactString::const_new("rule"),
            process_pattern: CompactString::const_new("*"),
            cpu_percent_threshold: None,
            memory_bytes_threshold: None,
            target_priority: None,
            affinity_mask: None,
            action: None,
            name: CompactString::const_new("New Rule"),
            enabled: true,
            group_name: None,
            prevent_sleep: false,
            disallowed: false,
            keep_running: false,
            max_instances: None,
            efficiency_mode: None,
            watchdog_action: WatchdogAction::None,
            condition_duration_secs: 0,
        }
    }
}

impl ProcessRule {
    /// Matches `process_name` against `self.process_pattern` (supporting `*` and `?` wildcards, case-insensitive).
    pub fn matches_name(&self, process_name: &str) -> bool {
        if !self.enabled {
            return false;
        }
        wildcard_match(
            &self.process_pattern.to_ascii_lowercase(),
            &process_name.to_ascii_lowercase(),
        )
    }

    /// Evaluates rule against a `ProcessSnapshot`. Never triggers on `Unavailable` or `Stale` readings.
    pub fn evaluate(&self, proc_snap: &ProcessSnapshot) -> bool {
        if !self.matches_name(&proc_snap.name) {
            return false;
        }
        if self.disallowed {
            return true;
        }
        let mut has_threshold = false;
        if let Some(cpu_thresh) = self.cpu_percent_threshold {
            has_threshold = true;
            match proc_snap.cpu_percent.reading {
                MetricReading::Measured {
                    value,
                    stale: false,
                } if value.is_finite() && value > cpu_thresh => {}
                _ => return false,
            }
        }
        if let Some(mem_thresh) = self.memory_bytes_threshold {
            has_threshold = true;
            match proc_snap.phys_footprint_bytes.reading {
                MetricReading::Measured {
                    value,
                    stale: false,
                } if value.is_finite() && value > mem_thresh => {}
                _ => return false,
            }
        }
        if has_threshold {
            return true;
        }
        // Unconditional launch/priority/sleep/affinity rule when matched
        self.target_priority.is_some()
            || self.affinity_mask.is_some()
            || self.action.is_some()
            || self.watchdog_action != WatchdogAction::None
            || self.prevent_sleep
            || self.efficiency_mode.is_some()
    }

    pub fn summary(&self) -> String {
        let mut parts = Vec::new();
        if let Some(p) = self.target_priority {
            parts.push(format!("Priority={p}"));
        }
        if let Some(mask) = self.affinity_mask {
            parts.push(format!("Affinity=0x{mask:X}"));
        }
        if let Some(cpu) = self.cpu_percent_threshold {
            parts.push(format!("CPU>{cpu:.0}%"));
        }
        if let Some(mem) = self.memory_bytes_threshold {
            parts.push(format!("Mem>{:.0}MB", mem / (1024.0 * 1024.0)));
        }
        if self.disallowed {
            parts.push("Block".to_string());
        }
        if self.prevent_sleep {
            parts.push("PreventSleep".to_string());
        }
        if self.keep_running {
            parts.push("KeepRunning".to_string());
        }
        if let Some(max_inst) = self.max_instances {
            parts.push(format!("MaxInstances={max_inst}"));
        }
        if self.watchdog_action != WatchdogAction::None {
            parts.push(format!("Watchdog={:?}", self.watchdog_action));
        }
        if parts.is_empty() {
            "(no actions)".to_string()
        } else {
            parts.join(", ")
        }
    }
}

/// Legacy single-threshold helper retained for backwards compatibility.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ThresholdRule {
    pub id: CompactString,
    pub process_pattern: CompactString,
    pub cpu_percent_threshold: f64,
}

impl ThresholdRule {
    #[inline]
    pub fn should_trigger(&self, cpu_percent: &Metric<f64>) -> bool {
        match cpu_percent.reading {
            MetricReading::Measured {
                value,
                stale: false,
            } => value.is_finite() && value > self.cpu_percent_threshold,
            _ => false,
        }
    }

    pub fn make_receipt(
        &self,
        key: ProcessKey,
        process_name: CompactString,
        original_priority: i32,
        applied_at_unix_ms: i64,
    ) -> PriorityRestoreReceipt {
        PriorityRestoreReceipt {
            key,
            process_name,
            original_priority,
            original_affinity_mask: None,
            applied_by_rule_id: self.id.clone(),
            applied_at_unix_ms,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RuleTriggerEvent {
    pub rule_id: CompactString,
    pub process_key: ProcessKey,
    pub process_name: CompactString,
    pub target_priority: Option<i32>,
    pub affinity_mask: Option<u64>,
    pub receipt: Option<PriorityRestoreReceipt>,
    #[serde(default)]
    pub watchdog_action: WatchdogAction,
    #[serde(default)]
    pub prevent_sleep: bool,
    #[serde(default)]
    pub disallowed: bool,
    #[serde(default)]
    pub max_instances_exceeded: bool,
}

/// Watchdog and rules engine (`RulesEngine`) + persistent `ProcessPreferenceStore`.
#[derive(Debug, Default, Clone, Serialize, Deserialize)]
pub struct RulesEngine {
    pub rules: Vec<ProcessRule>,
    pub preference_overrides: HashMap<CompactString, i32>,
    #[serde(default)]
    pub preference_store: ProcessPreferenceStore,
    #[serde(skip)]
    condition_first_seen_ms: HashMap<(ProcessKey, CompactString), i64>,
}

impl RulesEngine {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add_rule(&mut self, rule: ProcessRule) {
        self.rules.push(rule);
    }

    pub fn set_preference(&mut self, pref: ProcessPreference) {
        let key = ProcessPreference::normalize_exe_name(&pref.exe_name);
        if let Some(p) = pref.priority {
            self.preference_overrides.insert(key, p);
        }
        self.preference_store.upsert(pref);
    }

    pub fn save_rules(path: &Path, rules: &[ProcessRule]) -> std::io::Result<()> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(rules).map_err(std::io::Error::other)?;
        fs::write(path, json)
    }

    pub fn load_rules(path: &Path) -> std::io::Result<Vec<ProcessRule>> {
        if !path.exists() {
            return Ok(Vec::new());
        }
        let raw = fs::read_to_string(path)?;
        serde_json::from_str(&raw)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    }

    /// Evaluates all registered rules against a coherent `SystemSnapshot`, generating `RuleTriggerEvent`s
    /// and `PriorityRestoreReceipt`s for matching non-stale processes.
    pub fn evaluate_snapshot(
        &self,
        snapshot: &SystemSnapshot,
        now_unix_ms: i64,
    ) -> Vec<RuleTriggerEvent> {
        let mut events = Vec::new();
        for proc_snap in &snapshot.processes {
            let pref = self.preference_store.get(&proc_snap.name);
            let mut matched_rule_for_proc = false;
            for rule in &self.rules {
                if rule.evaluate(proc_snap) {
                    matched_rule_for_proc = true;
                    let eff_prio = rule
                        .target_priority
                        .or_else(|| pref.and_then(|p| p.priority));
                    let eff_aff = rule
                        .affinity_mask
                        .or_else(|| pref.and_then(|p| p.affinity_mask));
                    let receipt = eff_prio.map(|_| PriorityRestoreReceipt {
                        key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        original_priority: 0,
                        original_affinity_mask: None,
                        applied_by_rule_id: rule.id.clone(),
                        applied_at_unix_ms: now_unix_ms,
                    });
                    events.push(RuleTriggerEvent {
                        rule_id: rule.id.clone(),
                        process_key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        target_priority: eff_prio,
                        affinity_mask: eff_aff,
                        receipt,
                        watchdog_action: rule.watchdog_action,
                        prevent_sleep: rule.prevent_sleep,
                        disallowed: rule.disallowed,
                        max_instances_exceeded: false,
                    });
                }
            }
            if !matched_rule_for_proc && let Some(pref) = pref {
                events.push(RuleTriggerEvent {
                    rule_id: CompactString::const_new("preference.override"),
                    process_key: proc_snap.key,
                    process_name: proc_snap.name.clone(),
                    target_priority: pref.priority,
                    affinity_mask: pref.affinity_mask,
                    receipt: pref.priority.map(|_| PriorityRestoreReceipt {
                        key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        original_priority: 0,
                        original_affinity_mask: None,
                        applied_by_rule_id: CompactString::const_new("preference.override"),
                        applied_at_unix_ms: now_unix_ms,
                    }),
                    watchdog_action: WatchdogAction::None,
                    prevent_sleep: pref.prevent_sleep,
                    disallowed: false,
                    max_instances_exceeded: false,
                });
            }
        }
        events
    }

    /// Stateful evaluation enforcing `condition_duration_secs` sustain windows and `max_instances` caps.
    pub fn evaluate_with_watchdogs(
        &mut self,
        snapshot: &SystemSnapshot,
        now_unix_ms: i64,
    ) -> Vec<RuleTriggerEvent> {
        let live_keys: HashSet<ProcessKey> = snapshot.processes.iter().map(|p| p.key).collect();
        self.condition_first_seen_ms
            .retain(|(k, _), _| live_keys.contains(k));

        let mut events = Vec::new();
        for rule in &self.rules {
            if !rule.enabled {
                continue;
            }
            let matching_indices: Vec<usize> = snapshot
                .processes
                .iter()
                .enumerate()
                .filter(|(_, p)| rule.matches_name(&p.name))
                .map(|(i, _)| i)
                .collect();

            if let Some(max_inst) = rule.max_instances
                && matching_indices.len() > max_inst
            {
                for &idx in &matching_indices[max_inst..] {
                    let proc_snap = &snapshot.processes[idx];
                    events.push(RuleTriggerEvent {
                        rule_id: rule.id.clone(),
                        process_key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        target_priority: rule.target_priority,
                        affinity_mask: rule.affinity_mask,
                        receipt: None,
                        watchdog_action: WatchdogAction::Terminate,
                        prevent_sleep: false,
                        disallowed: false,
                        max_instances_exceeded: true,
                    });
                }
            }

            for idx in matching_indices {
                let proc_snap = &snapshot.processes[idx];
                let ck = (proc_snap.key, rule.id.clone());
                if !rule.evaluate(proc_snap) {
                    self.condition_first_seen_ms.remove(&ck);
                    continue;
                }
                let first_ms = *self
                    .condition_first_seen_ms
                    .entry(ck)
                    .or_insert(now_unix_ms);
                let elapsed_ms = now_unix_ms.saturating_sub(first_ms) as u64;
                if elapsed_ms < rule.condition_duration_secs.saturating_mul(1000) {
                    continue;
                }
                let eff_prio = match rule.watchdog_action {
                    WatchdogAction::SetBelowNormal => Some(10),
                    WatchdogAction::SetIdle => Some(UNIX_LOWEST_NICE),
                    _ => rule.target_priority,
                };
                events.push(RuleTriggerEvent {
                    rule_id: rule.id.clone(),
                    process_key: proc_snap.key,
                    process_name: proc_snap.name.clone(),
                    target_priority: eff_prio,
                    affinity_mask: rule.affinity_mask,
                    receipt: eff_prio.map(|_| PriorityRestoreReceipt {
                        key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        original_priority: 0,
                        original_affinity_mask: None,
                        applied_by_rule_id: rule.id.clone(),
                        applied_at_unix_ms: now_unix_ms,
                    }),
                    watchdog_action: rule.watchdog_action,
                    prevent_sleep: rule.prevent_sleep,
                    disallowed: rule.disallowed,
                    max_instances_exceeded: false,
                });
            }
        }
        events
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct AutoBalanceOutcome {
    pub throttled: Vec<PriorityRestoreReceipt>,
    pub restored: Vec<PriorityRestoreReceipt>,
}

/// `AutoBalance` service that throttles background CPU hogs (`>= 80%` CPU) to nice `+10`
/// while protecting foreground and system processes, and emits restore receipts when load subsides.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AutoBalanceService {
    pub enabled: bool,
    pub cpu_hog_threshold_percent: f64,
    #[serde(default = "default_restore_threshold")]
    pub restore_threshold_percent: f64,
    pub throttle_nice: i32,
    #[serde(default)]
    pub active_throttles: HashMap<ProcessKey, PriorityRestoreReceipt>,
}

impl Default for AutoBalanceService {
    fn default() -> Self {
        Self {
            enabled: true,
            cpu_hog_threshold_percent: 80.0,
            restore_threshold_percent: 40.0,
            throttle_nice: 10,
            active_throttles: HashMap::new(),
        }
    }
}

impl AutoBalanceService {
    pub fn evaluate(
        &self,
        snapshot: &SystemSnapshot,
        foreground_pid: Option<i32>,
        now_unix_ms: i64,
    ) -> Vec<PriorityRestoreReceipt> {
        if !self.enabled {
            return Vec::new();
        }
        let mut receipts = Vec::new();
        for p in &snapshot.processes {
            if Some(p.key.pid) == foreground_pid || p.key.pid <= 1 {
                continue;
            }
            if let MetricReading::Measured {
                value,
                stale: false,
            } = p.cpu_percent.reading
                && value.is_finite()
                && value >= self.cpu_hog_threshold_percent
            {
                receipts.push(PriorityRestoreReceipt {
                    key: p.key,
                    process_name: p.name.clone(),
                    original_priority: 0,
                    original_affinity_mask: None,
                    applied_by_rule_id: CompactString::const_new("autobalance.cpu_hog"),
                    applied_at_unix_ms: now_unix_ms,
                });
            }
        }
        receipts
    }

    pub fn evaluate_cycle(
        &mut self,
        snapshot: &SystemSnapshot,
        foreground_pid: Option<i32>,
        now_unix_ms: i64,
    ) -> AutoBalanceOutcome {
        let mut outcome = AutoBalanceOutcome::default();
        if !self.enabled {
            let drained: Vec<_> = self.active_throttles.drain().map(|(_, r)| r).collect();
            outcome.restored = drained;
            return outcome;
        }
        let by_key: HashMap<ProcessKey, &ProcessSnapshot> =
            snapshot.processes.iter().map(|p| (p.key, p)).collect();

        // Check currently throttled processes for restore conditions
        let tracked: Vec<ProcessKey> = self.active_throttles.keys().copied().collect();
        for key in tracked {
            let should_restore = match by_key.get(&key) {
                None => true, // exited
                Some(p) => {
                    Some(p.key.pid) == foreground_pid
                        || matches!(
                            p.cpu_percent.reading,
                            MetricReading::Measured { value, stale: false }
                                if value.is_finite() && value <= self.restore_threshold_percent
                        )
                }
            };
            if should_restore && let Some(r) = self.active_throttles.remove(&key) {
                outcome.restored.push(r);
            }
        }

        // Throttle new hogs
        for p in &snapshot.processes {
            if Some(p.key.pid) == foreground_pid
                || p.key.pid <= 1
                || self.active_throttles.contains_key(&p.key)
            {
                continue;
            }
            if let MetricReading::Measured {
                value,
                stale: false,
            } = p.cpu_percent.reading
                && value.is_finite()
                && value >= self.cpu_hog_threshold_percent
            {
                let receipt = PriorityRestoreReceipt {
                    key: p.key,
                    process_name: p.name.clone(),
                    original_priority: 0,
                    original_affinity_mask: None,
                    applied_by_rule_id: CompactString::const_new("autobalance.cpu_hog"),
                    applied_at_unix_ms: now_unix_ms,
                };
                self.active_throttles.insert(p.key, receipt.clone());
                outcome.throttled.push(receipt);
            }
        }

        outcome
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct IdleThrottleOutcome {
    pub newly_throttled: Vec<PriorityRestoreReceipt>,
    pub restored: Vec<PriorityRestoreReceipt>,
    pub skipped_count: usize,
}

/// `IdleThrottle` service that clamps background processes sustained below `cpu_threshold_percent`
/// to utility/idle priority (`UNIX_LOWEST_NICE = 19`), issuing `PriorityRestoreReceipt`s.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IdleThrottleService {
    pub enabled: bool,
    #[serde(default = "default_idle_cpu_threshold")]
    pub cpu_threshold_percent: f64,
    #[serde(default = "default_idle_ticks_required")]
    pub idle_ticks_required: u32,
    pub target_nice: i32,
    #[serde(default)]
    pub exclusions: Vec<CompactString>,
    #[serde(skip)]
    idle_ticks: HashMap<ProcessKey, u32>,
    #[serde(default)]
    throttled_receipts: HashMap<ProcessKey, PriorityRestoreReceipt>,
}

impl Default for IdleThrottleService {
    fn default() -> Self {
        Self {
            enabled: false,
            cpu_threshold_percent: 1.0,
            idle_ticks_required: 5,
            target_nice: UNIX_LOWEST_NICE,
            exclusions: Vec::new(),
            idle_ticks: HashMap::new(),
            throttled_receipts: HashMap::new(),
        }
    }
}

impl IdleThrottleService {
    pub fn throttled_count(&self) -> usize {
        self.throttled_receipts.len()
    }

    pub fn evaluate(
        &mut self,
        snapshot: &SystemSnapshot,
        foreground_pid: Option<i32>,
        now_unix_ms: i64,
    ) -> IdleThrottleOutcome {
        let mut outcome = IdleThrottleOutcome::default();
        if !self.enabled {
            return outcome;
        }
        let self_pid = std::process::id() as i32;
        let alive: HashSet<ProcessKey> = snapshot.processes.iter().map(|p| p.key).collect();

        // Evict dead keys
        self.idle_ticks.retain(|k, _| alive.contains(k));
        let dead_keys: Vec<ProcessKey> = self
            .throttled_receipts
            .keys()
            .copied()
            .filter(|k| !alive.contains(k))
            .collect();
        for k in dead_keys {
            self.throttled_receipts.remove(&k);
        }

        for proc_snap in &snapshot.processes {
            if Some(proc_snap.key.pid) == foreground_pid
                || proc_snap.key.pid <= 1
                || proc_snap.key.pid == self_pid
                || self
                    .exclusions
                    .iter()
                    .any(|ex| ex.eq_ignore_ascii_case(&proc_snap.name))
            {
                if let Some(r) = self.throttled_receipts.remove(&proc_snap.key) {
                    outcome.restored.push(r);
                }
                self.idle_ticks.remove(&proc_snap.key);
                outcome.skipped_count += 1;
                continue;
            }

            let MetricReading::Measured {
                value: cpu,
                stale: false,
            } = proc_snap.cpu_percent.reading
            else {
                self.idle_ticks.remove(&proc_snap.key);
                continue;
            };
            if !cpu.is_finite() {
                continue;
            }

            if self.throttled_receipts.contains_key(&proc_snap.key) {
                // Restore when CPU rises above 2x idle threshold
                if cpu > self.cpu_threshold_percent * 2.0
                    && let Some(r) = self.throttled_receipts.remove(&proc_snap.key)
                {
                    self.idle_ticks.remove(&proc_snap.key);
                    outcome.restored.push(r);
                }
                continue;
            }

            if cpu < self.cpu_threshold_percent {
                let ticks = self.idle_ticks.entry(proc_snap.key).or_insert(0);
                *ticks += 1;
                if *ticks >= self.idle_ticks_required {
                    let receipt = PriorityRestoreReceipt {
                        key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        original_priority: 0,
                        original_affinity_mask: None,
                        applied_by_rule_id: CompactString::const_new("idle_throttle"),
                        applied_at_unix_ms: now_unix_ms,
                    };
                    self.throttled_receipts
                        .insert(proc_snap.key, receipt.clone());
                    outcome.newly_throttled.push(receipt);
                }
            } else {
                self.idle_ticks.remove(&proc_snap.key);
            }
        }
        outcome
    }

    pub fn restore_all_valid(
        &mut self,
        live_snapshot: &SystemSnapshot,
    ) -> Vec<PriorityRestoreReceipt> {
        self.idle_ticks.clear();
        let all: Vec<_> = self.throttled_receipts.drain().map(|(_, r)| r).collect();
        all.into_iter()
            .filter(|r| {
                live_snapshot
                    .processes
                    .iter()
                    .any(|p| r.key.matches_live(&p.key))
            })
            .collect()
    }
}

/// `MemoryReclaim` advisor that triggers when measured `mem_used_percent` or `system_pressure_percent` exceeds threshold.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MemoryReclaimService {
    pub enabled: bool,
    pub used_percent_threshold: f64,
    pub pressure_percent_threshold: f64,
}

impl Default for MemoryReclaimService {
    fn default() -> Self {
        Self {
            enabled: true,
            used_percent_threshold: 85.0,
            pressure_percent_threshold: 60.0,
        }
    }
}

impl MemoryReclaimService {
    /// Returns the top memory-consuming `ProcessKey`s eligible for working-set trim / reclaim when memory pressure is high.
    pub fn candidates(&self, snapshot: &SystemSnapshot, max_candidates: usize) -> Vec<ProcessKey> {
        if !self.enabled {
            return Vec::new();
        }
        let used_high = matches!(
            snapshot.system.mem_used_percent.reading,
            MetricReading::Measured { value, stale: false } if value >= self.used_percent_threshold
        );
        let pressure_high = matches!(
            snapshot.system.system_pressure_percent.reading,
            MetricReading::Measured { value, stale: false } if value >= self.pressure_percent_threshold
        );
        if !used_high && !pressure_high {
            return Vec::new();
        }
        let mut measured_procs: Vec<(&ProcessSnapshot, f64)> = snapshot
            .processes
            .iter()
            .filter_map(|p| match p.phys_footprint_bytes.reading {
                MetricReading::Measured {
                    value,
                    stale: false,
                } if value > 0.0 => Some((p, value)),
                _ => None,
            })
            .collect();
        measured_procs.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        measured_procs
            .into_iter()
            .take(max_candidates)
            .map(|(p, _)| p.key)
            .collect()
    }
}

/// Manages macOS `caffeinate -di` subprocess lifecycle for rule-triggered and manual sleep prevention.
/// Ensures clean RAII subprocess cleanup in `Drop` so no orphan `caffeinate` survives process exit.
#[derive(Debug, Default)]
pub struct SleepPreventionService {
    pub enabled: bool,
    pub manual_override: bool,
    pub dry_run: bool,
    pub matching_rules: Vec<CompactString>,
    currently_preventing: bool,
    child: Option<Child>,
}

impl SleepPreventionService {
    pub fn new(dry_run: bool) -> Self {
        Self {
            enabled: true,
            manual_override: false,
            dry_run,
            matching_rules: Vec::new(),
            currently_preventing: false,
            child: None,
        }
    }

    pub fn is_preventing(&self) -> bool {
        self.currently_preventing
    }

    pub fn set_manual(&mut self, prevent: bool) -> bool {
        self.manual_override = prevent;
        let should = self.manual_override || !self.matching_rules.is_empty();
        self.sync_state(should);
        self.currently_preventing
    }

    pub fn evaluate(&mut self, snapshot: &SystemSnapshot, rules: &[ProcessRule]) -> bool {
        self.matching_rules.clear();
        if self.enabled {
            for rule in rules {
                if rule.enabled
                    && rule.prevent_sleep
                    && snapshot
                        .processes
                        .iter()
                        .any(|p| rule.matches_name(&p.name))
                {
                    self.matching_rules.push(rule.id.clone());
                }
            }
        }
        let should = self.manual_override || !self.matching_rules.is_empty();
        self.sync_state(should);
        self.currently_preventing
    }

    pub fn allow_sleep(&mut self) {
        self.manual_override = false;
        self.matching_rules.clear();
        self.sync_state(false);
    }

    fn sync_state(&mut self, should_prevent: bool) {
        if should_prevent && !self.currently_preventing {
            self.currently_preventing = true;
            if !self.dry_run {
                #[cfg(target_os = "macos")]
                {
                    if self.child.is_none()
                        && let Ok(child) = Command::new("/usr/bin/caffeinate")
                            .arg("-di")
                            .stdin(Stdio::null())
                            .stdout(Stdio::null())
                            .stderr(Stdio::null())
                            .spawn()
                    {
                        self.child = Some(child);
                    }
                }
            }
        } else if !should_prevent && self.currently_preventing {
            self.currently_preventing = false;
            if let Some(mut c) = self.child.take() {
                let _ = c.kill();
                let _ = c.wait();
            }
        }
    }
}

impl Drop for SleepPreventionService {
    fn drop(&mut self) {
        self.allow_sleep();
    }
}

/// Evaluates whether local time falls inside configured Quiet Hours window (`HH:MM` + weekday mask).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct QuietHoursService {
    pub enabled: bool,
    pub start_hhmm: CompactString,
    pub end_hhmm: CompactString,
    /// Weekdays `0=Sun .. 6=Sat`; empty means all days.
    pub days_of_week: Vec<u8>,
    #[serde(skip)]
    active: bool,
}

impl Default for QuietHoursService {
    fn default() -> Self {
        Self {
            enabled: false,
            start_hhmm: CompactString::const_new("22:00"),
            end_hhmm: CompactString::const_new("07:00"),
            days_of_week: Vec::new(),
            active: false,
        }
    }
}

impl QuietHoursService {
    pub fn is_active(&self) -> bool {
        self.active
    }

    pub fn parse_hhmm(value: &str) -> Option<u16> {
        let trimmed = value.trim();
        let (h_str, m_str) = trimmed.split_once(':')?;
        let h: u16 = h_str.parse().ok()?;
        let m: u16 = m_str.parse().ok()?;
        if h <= 23 && m <= 59 {
            Some(h * 60 + m)
        } else {
            None
        }
    }

    /// Evaluates Quiet Hours for an explicit `(weekday_0_sun, minutes_since_midnight)` (injectable clock for unit tests).
    pub fn evaluate_at(&mut self, weekday_0_sun: u8, minutes_since_midnight: u16) -> bool {
        self.active = self.compute_active(weekday_0_sun, minutes_since_midnight);
        self.active
    }

    pub fn evaluate_now(&mut self) -> bool {
        let (wday, mins) = local_weekday_and_minutes();
        self.evaluate_at(wday, mins)
    }

    pub fn compute_active(&self, weekday_0_sun: u8, minutes_since_midnight: u16) -> bool {
        if !self.enabled {
            return false;
        }
        let Some(start) = Self::parse_hhmm(&self.start_hhmm) else {
            return false;
        };
        let Some(end) = Self::parse_hhmm(&self.end_hhmm) else {
            return false;
        };
        let tod = minutes_since_midnight % (24 * 60);
        let overnight = end <= start;
        if !self.days_of_week.is_empty() {
            let effective_day = if overnight && tod < start {
                (weekday_0_sun + 6) % 7
            } else {
                weekday_0_sun % 7
            };
            if !self.days_of_week.contains(&effective_day) {
                return false;
            }
        }
        if overnight {
            tod >= start || tod < end
        } else {
            tod >= start && tod < end
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProfileProcessRule {
    pub process_pattern: CompactString,
    pub priority: Option<i32>,
    pub efficiency_mode: Option<bool>,
}

impl ProfileProcessRule {
    pub fn matches_name(&self, process_name: &str) -> bool {
        !self.process_pattern.trim().is_empty()
            && wildcard_match(
                &self.process_pattern.to_ascii_lowercase(),
                &process_name.to_ascii_lowercase(),
            )
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PerformanceProfile {
    pub id: CompactString,
    pub name: CompactString,
    pub description: CompactString,
    pub process_rules: Vec<ProfileProcessRule>,
    pub change_power_plan: bool,
    pub power_plan_name: CompactString,
}

/// Manages named `PerformanceProfile`s with receipt-backed activation/deactivation.
#[derive(Debug, Default, Clone, Serialize, Deserialize)]
pub struct PerformanceProfileService {
    pub profiles: Vec<PerformanceProfile>,
    pub active_profile_id: Option<CompactString>,
    pub pending_restores: HashMap<ProcessKey, PriorityRestoreReceipt>,
}

impl PerformanceProfileService {
    pub fn with_defaults() -> Self {
        Self {
            profiles: vec![
                PerformanceProfile {
                    id: CompactString::const_new("balanced"),
                    name: CompactString::const_new("Balanced"),
                    description: CompactString::const_new("Default scheduler priorities."),
                    process_rules: Vec::new(),
                    change_power_plan: false,
                    power_plan_name: CompactString::const_new("Balanced"),
                },
                PerformanceProfile {
                    id: CompactString::const_new("high-performance"),
                    name: CompactString::const_new("High Performance"),
                    description: CompactString::const_new(
                        "Boosts creative/build tools and lowers background sync priority.",
                    ),
                    process_rules: vec![
                        ProfileProcessRule {
                            process_pattern: CompactString::const_new("*rustc*"),
                            priority: Some(-5),
                            efficiency_mode: Some(false),
                        },
                        ProfileProcessRule {
                            process_pattern: CompactString::const_new("*Spotlight*"),
                            priority: Some(10),
                            efficiency_mode: Some(true),
                        },
                    ],
                    change_power_plan: true,
                    power_plan_name: CompactString::const_new("High Performance"),
                },
            ],
            active_profile_id: None,
            pending_restores: HashMap::new(),
        }
    }

    pub fn active_profile_name(&self) -> Option<&str> {
        let id = self.active_profile_id.as_ref()?;
        self.profiles
            .iter()
            .find(|p| p.id.as_str() == id.as_str())
            .map(|p| p.name.as_str())
    }

    pub fn activate(
        &mut self,
        profile_id: &str,
        live_snapshot: &SystemSnapshot,
        now_unix_ms: i64,
    ) -> Result<Vec<RuleTriggerEvent>, CompactString> {
        if !self.pending_restores.is_empty() {
            return Err(CompactString::from(format!(
                "Cannot activate profile while {} priority restore(s) remain pending",
                self.pending_restores.len()
            )));
        }
        let Some(profile) = self
            .profiles
            .iter()
            .find(|p| p.id.as_str() == profile_id)
            .cloned()
        else {
            return Err(CompactString::from(format!(
                "Unknown performance profile: {profile_id}"
            )));
        };

        self.active_profile_id = Some(profile.id.clone());
        let mut events = Vec::new();
        for proc_snap in &live_snapshot.processes {
            for rule in &profile.process_rules {
                if rule.matches_name(&proc_snap.name)
                    && let Some(prio) = rule.priority
                    && !self.pending_restores.contains_key(&proc_snap.key)
                {
                    let receipt = PriorityRestoreReceipt {
                        key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        original_priority: 0,
                        original_affinity_mask: None,
                        applied_by_rule_id: profile.id.clone(),
                        applied_at_unix_ms: now_unix_ms,
                    };
                    self.pending_restores.insert(proc_snap.key, receipt.clone());
                    events.push(RuleTriggerEvent {
                        rule_id: profile.id.clone(),
                        process_key: proc_snap.key,
                        process_name: proc_snap.name.clone(),
                        target_priority: Some(prio),
                        affinity_mask: None,
                        receipt: Some(receipt),
                        watchdog_action: WatchdogAction::None,
                        prevent_sleep: false,
                        disallowed: false,
                        max_instances_exceeded: false,
                    });
                }
            }
        }
        Ok(events)
    }

    pub fn deactivate(&mut self, live_snapshot: &SystemSnapshot) -> Vec<PriorityRestoreReceipt> {
        self.active_profile_id = None;
        let drained: Vec<_> = self.pending_restores.drain().map(|(_, r)| r).collect();
        drained
            .into_iter()
            .filter(|r| {
                live_snapshot
                    .processes
                    .iter()
                    .any(|p| r.key.matches_live(&p.key))
            })
            .collect()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum AlertMetricKind {
    CpuPercent,
    MemoryPercent,
    ThermalCelsius,
    DiskPercent,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AlertRuleConfig {
    pub id: CompactString,
    pub name: CompactString,
    pub metric: AlertMetricKind,
    pub threshold: f64,
    pub sustain_secs: u64,
    pub cooldown_secs: u64,
    pub enabled: bool,
}

/// Alert notification and webhook payload generator (`AlertsService`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AlertNotification {
    pub metric_name: CompactString,
    pub measured_value: f64,
    pub threshold: f64,
    pub webhook_url: Option<CompactString>,
    pub json_payload: String,
    #[serde(default)]
    pub suppressed_by_quiet_hours: bool,
    #[serde(default)]
    pub timestamp_unix_ms: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AlertsService {
    pub cpu_alert_threshold: f64,
    #[serde(default = "default_mem_alert_threshold")]
    pub mem_alert_threshold: f64,
    pub temp_celsius_threshold: f64,
    #[serde(default = "default_disk_alert_threshold")]
    pub disk_alert_threshold: f64,
    #[serde(default)]
    pub sustain_secs: u64,
    #[serde(default = "default_cooldown_secs")]
    pub cooldown_secs: u64,
    pub webhook_url: Option<CompactString>,
    #[serde(default)]
    pub custom_rules: Vec<AlertRuleConfig>,
    #[serde(default)]
    pub history: Vec<AlertNotification>,
    #[serde(skip)]
    first_seen_ms: HashMap<CompactString, i64>,
    #[serde(skip)]
    last_fired_ms: HashMap<CompactString, i64>,
}

impl Default for AlertsService {
    fn default() -> Self {
        Self {
            cpu_alert_threshold: 95.0,
            mem_alert_threshold: 90.0,
            temp_celsius_threshold: 105.0,
            disk_alert_threshold: 95.0,
            sustain_secs: 0,
            cooldown_secs: 60,
            webhook_url: None,
            custom_rules: Vec::new(),
            history: Vec::new(),
            first_seen_ms: HashMap::new(),
            last_fired_ms: HashMap::new(),
        }
    }
}

impl AlertsService {
    fn extract_metric_value(snapshot: &SystemSnapshot, kind: AlertMetricKind) -> Option<f64> {
        let reading = match kind {
            AlertMetricKind::CpuPercent => &snapshot.system.cpu_percent.reading,
            AlertMetricKind::MemoryPercent => &snapshot.system.mem_used_percent.reading,
            AlertMetricKind::ThermalCelsius => {
                &snapshot.hardware_telemetry.thermals.max_die_celsius.reading
            }
            AlertMetricKind::DiskPercent => {
                let max_disk =
                    snapshot
                        .disks
                        .iter()
                        .filter_map(|d| match d.root_used_percent.reading {
                            MetricReading::Measured {
                                value,
                                stale: false,
                            } if value.is_finite() => Some(value),
                            _ => None,
                        });
                return max_disk.fold(None, |acc: Option<f64>, v| {
                    Some(match acc {
                        Some(cur) => cur.max(v),
                        None => v,
                    })
                });
            }
        };
        match *reading {
            MetricReading::Measured {
                value,
                stale: false,
            } if value.is_finite() => Some(value),
            _ => None,
        }
    }

    fn effective_rules(&self) -> Vec<AlertRuleConfig> {
        let mut rules = vec![
            AlertRuleConfig {
                id: CompactString::const_new("builtin.cpu"),
                name: CompactString::const_new("system.cpu_percent"),
                metric: AlertMetricKind::CpuPercent,
                threshold: self.cpu_alert_threshold,
                sustain_secs: self.sustain_secs,
                cooldown_secs: self.cooldown_secs,
                enabled: true,
            },
            AlertRuleConfig {
                id: CompactString::const_new("builtin.mem"),
                name: CompactString::const_new("system.mem_used_percent"),
                metric: AlertMetricKind::MemoryPercent,
                threshold: self.mem_alert_threshold,
                sustain_secs: self.sustain_secs,
                cooldown_secs: self.cooldown_secs,
                enabled: true,
            },
            AlertRuleConfig {
                id: CompactString::const_new("builtin.thermal"),
                name: CompactString::const_new("thermal.max_die_celsius"),
                metric: AlertMetricKind::ThermalCelsius,
                threshold: self.temp_celsius_threshold,
                sustain_secs: self.sustain_secs,
                cooldown_secs: self.cooldown_secs,
                enabled: true,
            },
            AlertRuleConfig {
                id: CompactString::const_new("builtin.disk"),
                name: CompactString::const_new("disk.root_used_percent"),
                metric: AlertMetricKind::DiskPercent,
                threshold: self.disk_alert_threshold,
                sustain_secs: self.sustain_secs,
                cooldown_secs: self.cooldown_secs,
                enabled: true,
            },
        ];
        rules.extend(self.custom_rules.iter().cloned());
        rules
    }

    /// Evaluates system metrics immediately without mutating sustain/cooldown state.
    pub fn check_snapshot(&self, snapshot: &SystemSnapshot) -> Vec<AlertNotification> {
        let mut alerts = Vec::new();
        for rule in self.effective_rules() {
            if !rule.enabled {
                continue;
            }
            if let Some(value) = Self::extract_metric_value(snapshot, rule.metric)
                && value >= rule.threshold
            {
                let event_tag = match rule.metric {
                    AlertMetricKind::CpuPercent => "cpu_threshold_exceeded",
                    AlertMetricKind::MemoryPercent => "memory_threshold_exceeded",
                    AlertMetricKind::ThermalCelsius => "thermal_threshold_exceeded",
                    AlertMetricKind::DiskPercent => "disk_threshold_exceeded",
                };
                let payload = serde_json::json!({
                    "event": event_tag,
                    "metric": rule.name.as_str(),
                    "generation": snapshot.generation,
                    "value": value,
                    "threshold": rule.threshold,
                })
                .to_string();
                alerts.push(AlertNotification {
                    metric_name: rule.name,
                    measured_value: value,
                    threshold: rule.threshold,
                    webhook_url: self.webhook_url.clone(),
                    json_payload: payload,
                    suppressed_by_quiet_hours: false,
                    timestamp_unix_ms: (snapshot.sampled_at_ns / 1_000_000) as i64,
                });
            }
        }
        alerts
    }

    /// Stateful alert evaluation honoring sustain duration, cooldown, and Quiet Hours suppression.
    pub fn evaluate_with_state(
        &mut self,
        snapshot: &SystemSnapshot,
        now_unix_ms: i64,
        quiet_hours_active: bool,
    ) -> Vec<AlertNotification> {
        let mut fired = Vec::new();
        for rule in self.effective_rules() {
            if !rule.enabled {
                continue;
            }
            let Some(value) = Self::extract_metric_value(snapshot, rule.metric) else {
                self.first_seen_ms.remove(&rule.id);
                continue;
            };
            if value >= rule.threshold {
                let first_ms = *self
                    .first_seen_ms
                    .entry(rule.id.clone())
                    .or_insert(now_unix_ms);
                let sustained_ms = now_unix_ms.saturating_sub(first_ms) as u64;
                if sustained_ms < rule.sustain_secs.saturating_mul(1000) {
                    continue;
                }
                let cooldown_ok = self.last_fired_ms.get(&rule.id).is_none_or(|&last| {
                    now_unix_ms.saturating_sub(last) as u64
                        >= rule.cooldown_secs.saturating_mul(1000)
                });
                if !cooldown_ok {
                    continue;
                }
                self.last_fired_ms.insert(rule.id.clone(), now_unix_ms);
                let payload = serde_json::json!({
                    "rule": rule.id.as_str(),
                    "metric": rule.name.as_str(),
                    "generation": snapshot.generation,
                    "value": value,
                    "threshold": rule.threshold,
                    "quiet_hours_suppressed": quiet_hours_active,
                    "timestamp_unix_ms": now_unix_ms,
                })
                .to_string();
                let notif = AlertNotification {
                    metric_name: rule.name,
                    measured_value: value,
                    threshold: rule.threshold,
                    webhook_url: self.webhook_url.clone(),
                    json_payload: payload,
                    suppressed_by_quiet_hours: quiet_hours_active,
                    timestamp_unix_ms: now_unix_ms,
                };
                self.history.push(notif.clone());
                if self.history.len() > 200 {
                    let excess = self.history.len() - 200;
                    self.history.drain(0..excess);
                }
                fired.push(notif);
            } else {
                self.first_seen_ms.remove(&rule.id);
            }
        }
        fired
    }

    /// Best-effort webhook JSON POST (`http://` via `TcpStream`, `https://` via `/usr/bin/curl`).
    pub fn deliver_webhook(notification: &AlertNotification) -> Result<(), String> {
        let Some(url) = notification.webhook_url.as_deref() else {
            return Ok(());
        };
        if let Some(rest) = url.strip_prefix("http://") {
            let (host_port, path) = rest.split_once('/').unwrap_or((rest, ""));
            let addr = if host_port.contains(':') {
                host_port.to_string()
            } else {
                format!("{host_port}:80")
            };
            let req = format!(
                "POST /{path} HTTP/1.1\r\nHost: {host_port}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                notification.json_payload.len(),
                notification.json_payload
            );
            let mut stream = TcpStream::connect(addr).map_err(|e| e.to_string())?;
            stream
                .set_write_timeout(Some(Duration::from_secs(3)))
                .map_err(|e| e.to_string())?;
            stream
                .write_all(req.as_bytes())
                .map_err(|e| e.to_string())?;
            Ok(())
        } else if url.starts_with("https://") {
            #[cfg(target_os = "macos")]
            let mut cmd = Command::new("/usr/bin/curl");
            #[cfg(not(target_os = "macos"))]
            let mut cmd = Command::new("curl");
            #[cfg(target_os = "windows")]
            cmd.creation_flags(0x0800_0000);
            let status = cmd
                .args([
                    "-sS",
                    "-m",
                    "5",
                    "-X",
                    "POST",
                    "-H",
                    "Content-Type: application/json",
                    "-d",
                    &notification.json_payload,
                    url,
                ])
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status()
                .map_err(|e| e.to_string())?;
            if status.success() {
                Ok(())
            } else {
                Err(format!("curl webhook returned {status}"))
            }
        } else {
            Err(format!("Unsupported webhook scheme: {url}"))
        }
    }
}

/// Zero-overhead visual throttle configuration applied when `GamingMode` is active.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct GamingModeVisualThrottle {
    pub visual_frame_rate: u32,
    pub visual_bloom_enabled: bool,
    pub background_shaders_paused: bool,
    pub popout_flat_segments: bool,
    pub collector_interval_ms: u64,
}

impl GamingModeVisualThrottle {
    pub const ACTIVE: Self = Self {
        visual_frame_rate: 0,
        visual_bloom_enabled: false,
        background_shaders_paused: true,
        popout_flat_segments: true,
        collector_interval_ms: 2_000,
    };

    pub const INACTIVE_DEFAULT: Self = Self {
        visual_frame_rate: 60,
        visual_bloom_enabled: true,
        background_shaders_paused: false,
        popout_flat_segments: false,
        collector_interval_ms: 1_000,
    };
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct RecoveryReport {
    pub total_in_journal: usize,
    pub valid_restores: Vec<PriorityRestoreReceipt>,
    pub discarded_stale_count: usize,
}

/// Crash-safe `GamingModeService` managing `PriorityRestoreReceipt` persistence and zero-overhead UI throttling.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GamingModeService {
    pub active: bool,
    pub game_process_key: Option<ProcessKey>,
    pub journal_path: PathBuf,
    pub receipts: Vec<PriorityRestoreReceipt>,
}

impl GamingModeService {
    pub fn new(journal_path: impl Into<PathBuf>) -> Self {
        Self {
            active: false,
            game_process_key: None,
            journal_path: journal_path.into(),
            receipts: Vec::new(),
        }
    }

    pub fn visual_throttle(&self) -> GamingModeVisualThrottle {
        if self.active {
            GamingModeVisualThrottle::ACTIVE
        } else {
            GamingModeVisualThrottle::INACTIVE_DEFAULT
        }
    }

    /// Enters Gaming Mode, recording crash-safe `PriorityRestoreReceipt`s to disk before applying changes.
    pub fn activate(
        &mut self,
        game_key: Option<ProcessKey>,
        new_receipts: Vec<PriorityRestoreReceipt>,
    ) -> std::io::Result<()> {
        self.active = true;
        self.game_process_key = game_key;
        self.receipts = new_receipts;
        self.persist_journal()
    }

    /// Exits Gaming Mode, returning all receipts whose `ProcessKey` still matches the live OS process table
    /// and clearing the crash recovery journal.
    pub fn deactivate_and_collect_valid_restores(
        &mut self,
        live_snapshot: &SystemSnapshot,
    ) -> std::io::Result<Vec<PriorityRestoreReceipt>> {
        self.active = false;
        self.game_process_key = None;
        let drained = std::mem::take(&mut self.receipts);
        let valid: Vec<PriorityRestoreReceipt> = drained
            .into_iter()
            .filter(|r| {
                live_snapshot
                    .processes
                    .iter()
                    .any(|p| r.key.matches_live(&p.key))
            })
            .collect();
        if self.journal_path.exists() {
            let _ = fs::remove_file(&self.journal_path);
        }
        Ok(valid)
    }

    /// Loads any uncommitted `PriorityRestoreReceipt` journal left behind by a prior crash.
    pub fn load_crash_journal(path: &Path) -> std::io::Result<Vec<PriorityRestoreReceipt>> {
        if !path.exists() {
            return Ok(Vec::new());
        }
        let raw = fs::read_to_string(path)?;
        let receipts: Vec<PriorityRestoreReceipt> = serde_json::from_str(&raw)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        Ok(receipts)
    }

    /// Startup crash recovery: loads any orphaned Gaming Mode journal, double-checks `ProcessKey`
    /// against `live_snapshot`, optionally applies priority restores via `ActionCoordinator`, and removes the journal.
    pub fn recover_from_crash_journal(
        journal_path: &Path,
        live_snapshot: &SystemSnapshot,
        coord: Option<&ActionCoordinator>,
        dry_run: bool,
    ) -> std::io::Result<RecoveryReport> {
        let receipts = Self::load_crash_journal(journal_path)?;
        if receipts.is_empty() {
            return Ok(RecoveryReport::default());
        }
        let total_in_journal = receipts.len();
        let mut valid_restores = Vec::new();
        let mut discarded_stale_count = 0usize;

        for r in receipts {
            let live_match = live_snapshot
                .processes
                .iter()
                .any(|p| r.key.matches_live(&p.key));
            if !live_match {
                discarded_stale_count += 1;
                continue;
            }
            if let Some(coordinator) = coord {
                let _ = coordinator.execute(
                    &r.key,
                    ActionRequest {
                        action_type: ProcessActionType::SetPriority,
                        signal: None,
                        priority: Some(r.original_priority),
                    },
                    dry_run,
                );
            }
            valid_restores.push(r);
        }

        if journal_path.exists() {
            let _ = fs::remove_file(journal_path);
        }

        Ok(RecoveryReport {
            total_in_journal,
            valid_restores,
            discarded_stale_count,
        })
    }

    fn persist_journal(&self) -> std::io::Result<()> {
        if let Some(parent) = self.journal_path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(&self.receipts).map_err(std::io::Error::other)?;
        fs::write(&self.journal_path, json)
    }
}

pub fn now_unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn wildcard_match(pattern: &str, text: &str) -> bool {
    let p: Vec<char> = pattern.chars().collect();
    let t: Vec<char> = text.chars().collect();
    let (mut pi, mut ti) = (0usize, 0usize);
    let mut star_idx: Option<usize> = None;
    let mut match_idx = 0usize;

    while ti < t.len() {
        if pi < p.len() && (p[pi] == '?' || p[pi] == t[ti]) {
            pi += 1;
            ti += 1;
        } else if pi < p.len() && p[pi] == '*' {
            star_idx = Some(pi);
            match_idx = ti;
            pi += 1;
        } else if let Some(si) = star_idx {
            pi = si + 1;
            match_idx += 1;
            ti = match_idx;
        } else {
            return false;
        }
    }
    while pi < p.len() && p[pi] == '*' {
        pi += 1;
    }
    pi == p.len()
}

#[cfg(test)]
mod tests {
    use super::*;
    use nexus_core::{MetricProvenance, MetricQuality, SourceId, UnavailableReason, UnitTag};

    fn test_prov() -> MetricProvenance {
        MetricProvenance {
            source: SourceId(1),
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns: 1_000_000_000,
            window_ns: 250_000_000,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        }
    }

    fn make_proc(pid: i32, tok: u64, name: &str, cpu: Metric<f64>) -> ProcessSnapshot {
        ProcessSnapshot {
            key: ProcessKey {
                pid,
                creation_token_us: tok,
                uid: Some(501),
            },
            ppid: 1,
            name: CompactString::from(name),
            cpu_percent: cpu,
            phys_footprint_bytes: Metric::measured(64.0 * 1024.0 * 1024.0, test_prov()),
            resident_bytes: Metric::unavailable(UnavailableReason::NotYetAvailable, test_prov()),
            disk_read_bytes: Metric::unavailable(UnavailableReason::NotYetAvailable, test_prov()),
            disk_write_bytes: Metric::unavailable(UnavailableReason::NotYetAvailable, test_prov()),
            exited_at_ns: None,
        }
    }

    #[test]
    fn rules_engine_never_triggers_on_stale_or_unavailable_metrics() {
        let mut engine = RulesEngine::new();
        engine.add_rule(ProcessRule {
            id: CompactString::const_new("throttle_renders"),
            process_pattern: CompactString::const_new("*Renderer*"),
            cpu_percent_threshold: Some(50.0),
            memory_bytes_threshold: None,
            target_priority: Some(10),
            affinity_mask: None,
            action: None,
            ..Default::default()
        });

        let mut snap = SystemSnapshot::new_empty(1, 1_000_000_000);
        snap.processes.push(make_proc(
            500,
            1111,
            "Helper (Renderer)",
            Metric::stale_from(95.0, test_prov()),
        ));
        assert!(engine.evaluate_snapshot(&snap, 1700000).is_empty());

        snap.processes[0].cpu_percent = Metric::measured(82.0, test_prov());
        let events = engine.evaluate_snapshot(&snap, 1700000);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].process_key.pid, 500);
        assert_eq!(events[0].target_priority, Some(10));
    }

    #[test]
    fn idle_throttle_and_quiet_hours_and_profiles_and_alerts_work() {
        let mut idle = IdleThrottleService {
            enabled: true,
            cpu_threshold_percent: 2.0,
            idle_ticks_required: 3,
            target_nice: UNIX_LOWEST_NICE,
            exclusions: vec![CompactString::const_new("Finder")],
            ..Default::default()
        };
        let mut snap = SystemSnapshot::new_empty(1, 1_000_000_000);
        snap.processes.push(make_proc(
            701,
            100,
            "BackgroundSync",
            Metric::measured(0.3, test_prov()),
        ));
        snap.processes.push(make_proc(
            702,
            101,
            "Finder",
            Metric::measured(0.1, test_prov()),
        ));

        assert!(
            idle.evaluate(&snap, Some(999), 1000)
                .newly_throttled
                .is_empty()
        );
        assert!(
            idle.evaluate(&snap, Some(999), 2000)
                .newly_throttled
                .is_empty()
        );
        let out3 = idle.evaluate(&snap, Some(999), 3000);
        assert_eq!(out3.newly_throttled.len(), 1);
        assert_eq!(out3.newly_throttled[0].key.pid, 701);

        // Waking up above 2x threshold restores it
        snap.processes[0].cpu_percent = Metric::measured(12.0, test_prov());
        let out4 = idle.evaluate(&snap, Some(999), 4000);
        assert_eq!(out4.restored.len(), 1);
        assert_eq!(out4.restored[0].key.pid, 701);

        // QuietHoursService overnight wrap + weekday mask
        let mut qh = QuietHoursService {
            enabled: true,
            start_hhmm: CompactString::const_new("22:00"),
            end_hhmm: CompactString::const_new("07:00"),
            days_of_week: vec![5], // Friday night into Saturday morning
            ..Default::default()
        };
        assert!(qh.evaluate_at(5, 23 * 60 + 30)); // Fri 23:30 -> true
        assert!(qh.evaluate_at(6, 6 * 60 + 45)); // Sat 06:45 (eff Fri) -> true
        assert!(!qh.evaluate_at(6, 7 * 60)); // Sat 07:00 -> false
        assert!(!qh.evaluate_at(2, 23 * 60)); // Tue 23:00 -> false

        // PerformanceProfileService activate / pending refusal / deactivate
        let mut prof_svc = PerformanceProfileService::with_defaults();
        let mut prof_snap = SystemSnapshot::new_empty(2, 2_000_000_000);
        prof_snap.processes.push(make_proc(
            801,
            555,
            "rustc",
            Metric::measured(50.0, test_prov()),
        ));
        let evs = prof_svc
            .activate("high-performance", &prof_snap, 5000)
            .expect("activate ok");
        assert_eq!(evs.len(), 1);
        assert!(prof_svc.activate("balanced", &prof_snap, 6000).is_err());
        let restored_prof = prof_svc.deactivate(&prof_snap);
        assert_eq!(restored_prof.len(), 1);

        // SleepPreventionService dry-run toggle + rule match
        let mut sleep_svc = SleepPreventionService::new(true);
        let sleep_rules = vec![ProcessRule {
            id: CompactString::const_new("keep_awake_build"),
            process_pattern: CompactString::const_new("*rustc*"),
            prevent_sleep: true,
            ..Default::default()
        }];
        assert!(sleep_svc.evaluate(&prof_snap, &sleep_rules));
        prof_snap.processes.clear();
        assert!(!sleep_svc.evaluate(&prof_snap, &sleep_rules));
        assert!(sleep_svc.set_manual(true));
        sleep_svc.allow_sleep();
        assert!(!sleep_svc.is_preventing());

        // AlertsService sustain, cooldown, and quiet hours suppression
        let mut alerts = AlertsService {
            cpu_alert_threshold: 80.0,
            sustain_secs: 5,
            cooldown_secs: 30,
            ..Default::default()
        };
        let mut alert_snap = SystemSnapshot::new_empty(3, 3_000_000_000);
        alert_snap.system.cpu_percent = Metric::measured(90.0, test_prov());
        assert!(
            alerts
                .evaluate_with_state(&alert_snap, 10_000, false)
                .is_empty()
        );
        let fired = alerts.evaluate_with_state(&alert_snap, 16_000, true);
        assert_eq!(fired.len(), 1);
        assert!(fired[0].suppressed_by_quiet_hours);
        // Cooldown suppresses immediate repeat
        assert!(
            alerts
                .evaluate_with_state(&alert_snap, 20_000, false)
                .is_empty()
        );
    }

    #[test]
    fn gaming_mode_persists_crash_journal_and_recovers_safely() {
        let tmp_path = std::env::temp_dir().join(format!(
            "nexus_gaming_journal_test_{}.json",
            std::process::id()
        ));
        let mut gm = GamingModeService::new(&tmp_path);
        assert_eq!(gm.visual_throttle().visual_frame_rate, 60);

        let key_valid = ProcessKey {
            pid: 1001,
            creation_token_us: 900_000,
            uid: Some(501),
        };
        let key_recycled = ProcessKey {
            pid: 1002,
            creation_token_us: 800_000,
            uid: Some(501),
        };
        let receipts = vec![
            PriorityRestoreReceipt {
                key: key_valid,
                process_name: CompactString::const_new("bg_indexer"),
                original_priority: 0,
                original_affinity_mask: None,
                applied_by_rule_id: CompactString::const_new("gaming_mode"),
                applied_at_unix_ms: 12345,
            },
            PriorityRestoreReceipt {
                key: key_recycled,
                process_name: CompactString::const_new("old_worker"),
                original_priority: 0,
                original_affinity_mask: None,
                applied_by_rule_id: CompactString::const_new("gaming_mode"),
                applied_at_unix_ms: 12345,
            },
        ];

        gm.activate(None, receipts).expect("journal write succeeds");
        assert_eq!(gm.visual_throttle(), GamingModeVisualThrottle::ACTIVE);

        let mut snap = SystemSnapshot::new_empty(2, 2_000_000_000);
        snap.processes.push(make_proc(
            1001,
            900_000,
            "bg_indexer",
            Metric::measured(1.0, test_prov()),
        ));
        snap.processes.push(make_proc(
            1002,
            999_999,
            "new_unrelated_proc",
            Metric::measured(1.0, test_prov()),
        ));

        // Startup crash journal recovery verifies ProcessKey and cleans journal
        let rec = GamingModeService::recover_from_crash_journal(&tmp_path, &snap, None, true)
            .expect("recovery succeeds");
        assert_eq!(rec.total_in_journal, 2);
        assert_eq!(rec.valid_restores.len(), 1);
        assert_eq!(rec.valid_restores[0].key, key_valid);
        assert_eq!(rec.discarded_stale_count, 1);
        assert!(!tmp_path.exists());

        // Corrupt crash journal file returns Err without panicking
        let corrupt_journal =
            std::env::temp_dir().join(format!("nexus_corrupt_journal_{}.json", std::process::id()));
        fs::write(&corrupt_journal, "{corrupt json").expect("write corrupt journal");
        assert!(
            GamingModeService::recover_from_crash_journal(&corrupt_journal, &snap, None, true)
                .is_err()
        );
        let _ = fs::remove_file(&corrupt_journal);
    }

    #[test]
    fn action_coordinator_validates_live_pid_and_refuses_mismatched_token() {
        let coord = ActionCoordinator::new();
        let self_pid = std::process::id() as i32;
        let (live_key, _, _) =
            MacCollector::read_process_key(self_pid).expect("must read own ProcessKey");

        assert!(
            coord
                .execute(
                    &live_key,
                    ActionRequest {
                        action_type: ProcessActionType::SetPriority,
                        signal: None,
                        priority: Some(0),
                    },
                    true
                )
                .is_ok()
        );

        let forged_key = ProcessKey {
            pid: self_pid,
            creation_token_us: live_key.creation_token_us + 1,
            uid: live_key.uid,
        };
        assert_eq!(
            coord.execute(
                &forged_key,
                ActionRequest {
                    action_type: ProcessActionType::Terminate,
                    signal: None,
                    priority: None,
                },
                true
            ),
            Err(ActionError::TargetChanged { pid: self_pid })
        );
    }
}
