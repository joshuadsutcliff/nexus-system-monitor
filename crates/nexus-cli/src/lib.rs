#![forbid(unsafe_code)]

pub mod tui;

use clap::{Args, Parser, Subcommand, ValueEnum};
use compact_str::CompactString;
use nexus_automation::{
    ActionCoordinator, ActionRequest, AlertsService, AutoBalanceService, GamingModeService,
    MemoryReclaimService, ProcessActionType, ProcessRule, RulesEngine, now_unix_ms,
};
use nexus_collectors_macos::MacCollector;
use nexus_collectors_macos::system_ops::{
    MacOsUncachedDiskIo, ServiceAction, collect_mounted_volumes, control_service,
    list_network_connections, list_services, list_startup_items, scan_lan_radar, scan_wifi,
};
use nexus_core::{
    ConnectionProtocol, DEFAULT_NEXUS_BASELINE, DEFAULT_REMOTE_PORT, DisplayMode, GrafanaDashboard,
    HealthScoring, Metric, MetricReading, PopoutMode, RemoteServerConfig, RemoteServerHandle,
    SystemSnapshot, TelegrafConfigGenerator, ValueState, VfdTextColor, benchmark_ratios, bundled,
    nexus_score, render_prometheus_metrics, run_cpu_suite, six_character_ratio,
};
use nexus_disk::{
    BenchmarkCategory, BenchmarkHistoryStore, DiskSnapshotStore, MetricsHistoryStore,
    NexusTraceJsonPreflight, NexusTraceRecorder, RectBounds, SnapshotDatabase, SnapshotDiffer,
    compute_squarified_treemap, default_benchmark_history_path, default_disk_snapshots_db_path,
    default_telemetry_db_path, default_traces_dir, find_duplicates, read_trace_file,
    run_disk_benchmark, scan_directory_parallel, write_trace_atomic_no_overwrite,
};
use nexus_ui::desktop::run_internet_benchmark;
use nexus_ui::{
    CrystalGlassPreset, ThemeEngine, UserPreferences, build_disk_shader_blobs,
    validate_all_embedded_shaders,
};
use std::fmt::Write as _;
use std::io::{Read, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::thread;
use std::time::Duration;

macro_rules! safe_println {
    () => {
        safe_print("")
    };
    ($($arg:tt)*) => {
        safe_print(&format!($($arg)*))
    };
}

#[derive(Debug, Parser)]
#[command(
    name = "nexus",
    version,
    about = "Nexus System Monitor unified telemetry, honesty, automation, wireless radar, disk analyzer, and Prometheus CLI"
)]
pub struct Cli {
    #[command(subcommand)]
    pub command: Option<Commands>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum)]
pub enum OutputFormat {
    Table,
    Json,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum)]
pub enum ProcessSortArg {
    Cpu,
    Mem,
    Pid,
    Name,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum)]
pub enum ExportFormatArg {
    Csv,
    Json,
}

#[derive(Debug, Subcommand)]
pub enum AlertsSubcommand {
    /// Lists configured alert thresholds and rules
    List {
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Evaluates active alert rules against a live snapshot
    Status,
    /// Watches live metrics and prints triggered alerts
    Watch {
        #[arg(long, default_value_t = 1000)]
        interval_ms: u64,
        #[arg(long, default_value_t = 5)]
        count: usize,
    },
}

#[derive(Debug, Subcommand)]
pub enum RulesSubcommand {
    /// Lists configured process rules from preferences.json
    List {
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Shows rules engine and autobalance status against a live snapshot
    Status,
    /// Evaluates an ad-hoc wildcard + CPU rule against a live snapshot
    Eval {
        #[arg(long, default_value_t = 5.0)]
        cpu_threshold: f64,
        #[arg(long, default_value = "*")]
        pattern: String,
    },
}

#[derive(Debug, Subcommand)]
pub enum SettingsSubcommand {
    /// Displays all persistent UserPreferences as JSON
    Show,
    /// Gets a single setting value by key
    Get { key: String },
    /// Updates a single setting key and persists preferences.json
    Set { key: String, value: String },
}

#[derive(Debug, Subcommand)]
pub enum DiskSnapshotsSubcommand {
    /// Lists saved SQLite disk snapshots
    List {
        #[arg(long)]
        root: Option<PathBuf>,
    },
    /// Deletes a stored disk snapshot by ID
    Delete { id: i64 },
}

#[derive(Debug, Subcommand)]
pub enum DiskSubcommand {
    /// Scans a directory in parallel, classifies files into 7 categories, optionally builds treemap & saves snapshot
    Scan {
        #[arg(default_value = ".")]
        path: PathBuf,
        #[arg(long, default_value_t = 6)]
        max_depth: usize,
        #[arg(long, default_value_t = false)]
        save: bool,
        #[arg(long)]
        label: Option<String>,
        #[arg(long, default_value_t = false)]
        treemap: bool,
    },
    /// Manage saved SQLite disk snapshots
    Snapshots {
        #[command(subcommand)]
        cmd: DiskSnapshotsSubcommand,
    },
    /// Diffs two saved SQLite disk snapshots
    Diff { baseline_id: i64, target_id: i64 },
    /// Finds exact SHA-256 duplicate files under a directory using 2-pass hashing
    Duplicates {
        #[arg(default_value = ".")]
        path: PathBuf,
        #[arg(long, default_value_t = 6)]
        max_depth: usize,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum)]
pub enum BenchmarkSuiteArg {
    All,
    Cpu,
    Gpu,
    Disk,
    Internet,
}

#[derive(Debug, Subcommand)]
pub enum TraceSubcommand {
    /// Records live snapshots into a portable .nexustrace file
    Record {
        #[arg(long, default_value_t = 3)]
        seconds: u64,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
        #[arg(long)]
        output: Option<PathBuf>,
    },
    /// Runs streaming JSON preflight and schema validation on a .nexustrace file
    Verify { path: PathBuf },
    /// Inspects a .nexustrace file header and sample summary
    Inspect {
        path: PathBuf,
        #[arg(long, default_value_t = 5)]
        samples: usize,
    },
}

#[derive(Debug, Args)]
pub struct RulesArgs {
    #[command(subcommand)]
    pub sub: Option<RulesSubcommand>,
    #[arg(long, default_value_t = 5.0)]
    pub cpu_threshold: f64,
    #[arg(long, default_value = "*")]
    pub pattern: String,
}

#[derive(Debug, Subcommand)]
pub enum Commands {
    /// Launches the interactive full-screen `ratatui` + `crossterm` terminal dashboard (alias: `tui`)
    #[command(visible_alias = "tui")]
    Dashboard {
        /// Optional frame limit for headless verification (`TestBackend`)
        #[arg(long)]
        frames: Option<usize>,
    },
    /// Lists, filters, sorts, or controls live OS processes with double ProcessKey safety (alias: `ps`)
    #[command(visible_alias = "ps")]
    Processes {
        #[arg(long)]
        filter: Option<String>,
        #[arg(long, value_enum, default_value_t = ProcessSortArg::Mem)]
        sort: ProcessSortArg,
        #[arg(short = 'n', long, default_value_t = 20)]
        limit: usize,
        #[arg(long)]
        kill: Option<i32>,
        #[arg(long)]
        terminate: Option<i32>,
        #[arg(long)]
        nice: Option<String>,
        #[arg(long, default_value_t = false)]
        dry_run: bool,
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
    },
    /// Lists and controls OS services (`launchd` / `systemd` / SCM) (alias: `svc`)
    #[command(visible_alias = "svc")]
    Services {
        #[arg(long)]
        filter: Option<String>,
        #[arg(long, default_value_t = false)]
        running: bool,
        #[arg(long, default_value_t = false)]
        stopped: bool,
        #[arg(long)]
        start: Option<String>,
        #[arg(long)]
        stop: Option<String>,
        #[arg(long)]
        restart: Option<String>,
        #[arg(long, default_value_t = false)]
        dry_run: bool,
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Lists startup LaunchAgents / LaunchDaemons / autostart items
    Startup {
        #[arg(long)]
        filter: Option<String>,
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Lists active TCP4/TCP6/UDP4/UDP6 sockets with ProcessKey attribution (alias: `netstat`)
    #[command(visible_alias = "netstat")]
    Connections {
        #[arg(long)]
        protocol: Option<String>,
        #[arg(long)]
        state: Option<String>,
        #[arg(long)]
        filter: Option<String>,
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Scans Wi-Fi RF spectrum and local LAN subnet devices (alias: `radar`)
    #[command(visible_alias = "radar")]
    Wireless {
        #[arg(long, default_value_t = false)]
        wifi_only: bool,
        #[arg(long, default_value_t = false)]
        lan_only: bool,
        #[arg(long)]
        subnet: Option<String>,
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Evaluates composite system health score, bottlenecks, and top resource consumers
    Health {
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
    },
    /// Inspects or watches system threshold alerts
    Alerts {
        #[command(subcommand)]
        cmd: AlertsSubcommand,
    },
    /// Lists, checks, or evaluates automation process rules
    Rules(RulesArgs),
    /// Reads or writes UserPreferences (`preferences.json`)
    Settings {
        #[command(subcommand)]
        cmd: SettingsSubcommand,
    },
    /// Serves Honesty-preserving Prometheus `/metrics` or prints Grafana / Telegraf configs (aliases: `prom`, `serve-metrics`)
    #[command(visible_aliases = ["prom", "serve-metrics"])]
    Prometheus {
        #[arg(long, default_value_t = 9182)]
        port: u16,
        #[arg(long)]
        bind: Option<String>,
        #[arg(long, default_value_t = false)]
        once: bool,
        #[arg(long, default_value_t = false)]
        grafana: bool,
        #[arg(long, default_value_t = false)]
        telegraf: bool,
    },
    /// Exports telemetry history from SQLite (`telemetry.db`) to CSV or JSON
    Export {
        #[arg(long, value_enum, default_value_t = ExportFormatArg::Csv)]
        format: ExportFormatArg,
        #[arg(long)]
        output: Option<PathBuf>,
        #[arg(long, default_value = "1h")]
        last: String,
    },
    /// Disk analyzer, squarified treemap, 2-pass duplicates, and SQLite disk snapshot management
    Disk {
        #[command(subcommand)]
        cmd: DiskSubcommand,
    },
    /// Captures a live telemetry snapshot with MetricProvenance & MetricQuality (alias: `status`)
    #[command(visible_alias = "status")]
    Snapshot {
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
        #[arg(long, default_value_t = false)]
        single_tick: bool,
    },
    /// Captures two snapshots and prints ProcessKey-verified SnapshotDiffer results
    Diff {
        #[arg(long, default_value_t = 500)]
        interval_ms: u64,
    },
    /// Prints top processes ranked by physical memory footprint
    Top {
        #[arg(short = 'n', long, default_value_t = 15)]
        limit: usize,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
    },
    /// Exercises crash-safe Gaming Mode journal persistence and visual throttling
    GamingMode {
        #[arg(long)]
        journal_path: Option<PathBuf>,
    },
    /// Quick disk scan alias classifying files into 7 categories and preparing GPU shader blobs (alias: `disk-usage`)
    #[command(visible_alias = "disk-usage")]
    DiskScan {
        #[arg(long, default_value = ".")]
        path: PathBuf,
        #[arg(long, default_value_t = 6)]
        max_depth: usize,
    },
    /// Audits every metric state, provenance source, quality tier, coherence repairs, WGSL shaders, and bundled skins (alias: `doctor`)
    #[command(visible_alias = "doctor")]
    Honesty {
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
    },
    /// Records, verifies, or inspects portable .nexustrace flight recorder captures
    Trace {
        #[command(subcommand)]
        cmd: TraceSubcommand,
    },
    /// Runs the 4-suite hardware benchmark (CPU, Disk F_NOCACHE, Internet) and prints 100-anchor NexusScore
    Benchmark {
        #[arg(long, value_enum, default_value_t = BenchmarkSuiteArg::Cpu)]
        suite: BenchmarkSuiteArg,
        #[arg(long, default_value_t = 5.0)]
        duration_secs: f64,
        #[arg(long, value_enum, default_value_t = OutputFormat::Table)]
        format: OutputFormat,
    },
    /// Runs the Bonjour companion remote telemetry server (_nexus-remote._tcp) with ChaCha20-Poly1305 sealed wire
    Remote {
        #[arg(long, default_value_t = DEFAULT_REMOTE_PORT)]
        port: u16,
        #[arg(long, default_value_t = false)]
        allow_cgnat: bool,
        #[arg(long, default_value_t = false)]
        no_bonjour: bool,
        #[arg(long)]
        duration_secs: Option<u64>,
    },
}

pub fn run_cli() -> std::process::ExitCode {
    use std::process::ExitCode;

    let cli = Cli::parse();
    let command = cli.command.unwrap_or(Commands::Snapshot {
        format: OutputFormat::Table,
        interval_ms: 250,
        single_tick: false,
    });

    let collector = MacCollector::new();

    match command {
        Commands::Dashboard { frames } => match tui::run_tui(&collector, frames) {
            Ok(Some(rendered)) => {
                safe_print(&rendered);
            }
            Ok(None) => {}
            Err(e) => {
                eprintln!("nexus: TUI execution failed: {e}");
                return ExitCode::FAILURE;
            }
        },
        Commands::Processes {
            filter,
            sort,
            limit,
            kill,
            terminate,
            nice,
            dry_run,
            format,
            interval_ms,
        } => {
            let snap = collect_with_warmup(&collector, interval_ms, false);
            let coord = ActionCoordinator::new();

            if let Some(pid) = kill.or(terminate) {
                let action_type = if kill.is_some() {
                    ProcessActionType::ForceKill
                } else {
                    ProcessActionType::Terminate
                };
                match snap.processes.iter().find(|p| p.key.pid == pid) {
                    Some(proc_snap) => {
                        let res = coord.execute(
                            &proc_snap.key,
                            ActionRequest {
                                action_type,
                                signal: None,
                                priority: None,
                            },
                            dry_run,
                        );
                        safe_println!(
                            "Process {} (PID {}, token={}): {:?} (dry_run={})",
                            proc_snap.name,
                            proc_snap.key.pid,
                            proc_snap.key.creation_token_us,
                            res,
                            dry_run
                        );
                    }
                    None => {
                        eprintln!("nexus: PID {pid} not found in live snapshot.");
                        return std::process::ExitCode::FAILURE;
                    }
                }
                return std::process::ExitCode::SUCCESS;
            }
            if let Some(spec) = nice {
                if let Some((pid_s, prio_s)) = spec.split_once(':')
                    && let (Ok(pid), Ok(prio)) = (pid_s.parse::<i32>(), prio_s.parse::<i32>())
                {
                    match snap.processes.iter().find(|p| p.key.pid == pid) {
                        Some(proc_snap) => {
                            let res = coord.execute(
                                &proc_snap.key,
                                ActionRequest {
                                    action_type: ProcessActionType::SetPriority,
                                    signal: None,
                                    priority: Some(prio),
                                },
                                dry_run,
                            );
                            safe_println!(
                                "SetPriority({prio}) on {} (PID {pid}, token={}): {:?} (dry_run={dry_run})",
                                proc_snap.name,
                                proc_snap.key.creation_token_us,
                                res
                            );
                        }
                        None => {
                            eprintln!("nexus: PID {pid} not found in live snapshot.");
                            return std::process::ExitCode::FAILURE;
                        }
                    }
                } else {
                    eprintln!("nexus: invalid --nice syntax (expected PID:VALUE, e.g. 1234:10)");
                    return std::process::ExitCode::FAILURE;
                }
                return std::process::ExitCode::SUCCESS;
            }

            let filter_lower = filter.map(|f| f.to_ascii_lowercase());
            let mut procs: Vec<_> = snap
                .processes
                .iter()
                .filter(|p| {
                    filter_lower.as_ref().is_none_or(|f| {
                        p.name.to_ascii_lowercase().contains(f) || p.key.pid.to_string().contains(f)
                    })
                })
                .cloned()
                .collect();

            procs.sort_by(|a, b| match sort {
                ProcessSortArg::Mem => metric_num(&b.phys_footprint_bytes)
                    .partial_cmp(&metric_num(&a.phys_footprint_bytes))
                    .unwrap_or(std::cmp::Ordering::Equal),
                ProcessSortArg::Cpu => metric_num(&b.cpu_percent)
                    .partial_cmp(&metric_num(&a.cpu_percent))
                    .unwrap_or(std::cmp::Ordering::Equal),
                ProcessSortArg::Pid => a.key.pid.cmp(&b.key.pid),
                ProcessSortArg::Name => a.name.cmp(&b.name),
            });
            procs.truncate(limit);

            if format == OutputFormat::Json {
                safe_println!(
                    "{}",
                    serde_json::to_string_pretty(&procs).unwrap_or_default()
                );
            } else {
                let mut out = String::new();
                let _ = writeln!(
                    out,
                    "=== Nexus Processes (showing {} of {}, gen={}) ===",
                    procs.len(),
                    snap.processes.len(),
                    snap.generation
                );
                let _ = writeln!(
                    out,
                    "{:<8} | {:<18} | {:<26} | {:<13} | {:<9} | Provenance",
                    "PID", "CreationToken", "Name", "PhysFootprint", "CPU %"
                );
                let _ = writeln!(out, "{}", "-".repeat(105));
                for p in procs {
                    let _ = writeln!(
                        out,
                        "{:<8} | {:<18} | {:<26} | {:<13} | {:<9} | {}",
                        p.key.pid,
                        p.key.creation_token_us,
                        p.name.chars().take(26).collect::<String>(),
                        format_bytes(&p.phys_footprint_bytes),
                        p.cpu_percent.format_with_unit(1, "%"),
                        snap.source_name(p.cpu_percent.provenance.source),
                    );
                }
                safe_print(&out);
            }
        }
        Commands::Services {
            filter,
            running,
            stopped,
            start,
            stop,
            restart,
            dry_run,
            format,
        } => {
            if let Some(name) = start {
                safe_println!(
                    "{:?}",
                    control_service(&name, ServiceAction::Start, dry_run)
                );
                return std::process::ExitCode::SUCCESS;
            }
            if let Some(name) = stop {
                safe_println!("{:?}", control_service(&name, ServiceAction::Stop, dry_run));
                return std::process::ExitCode::SUCCESS;
            }
            if let Some(name) = restart {
                safe_println!(
                    "{:?}",
                    control_service(&name, ServiceAction::Restart, dry_run)
                );
                return std::process::ExitCode::SUCCESS;
            }

            let filter_lower = filter.map(|s| s.to_ascii_lowercase());
            let list: Vec<_> = list_services()
                .into_iter()
                .filter(|s| {
                    filter_lower
                        .as_ref()
                        .is_none_or(|q| s.name.to_ascii_lowercase().contains(q))
                        && (!running || s.state == nexus_core::ServiceState::Running)
                        && (!stopped || s.state == nexus_core::ServiceState::Stopped)
                })
                .collect();
            if format == OutputFormat::Json {
                safe_println!(
                    "{}",
                    serde_json::to_string_pretty(&list).unwrap_or_default()
                );
            } else {
                let mut out = String::new();
                let _ = writeln!(out, "=== Nexus Services ({} matched) ===", list.len());
                let _ = writeln!(
                    out,
                    "{:<48} | {:<9} | {:<10} | {:<7} | Scope",
                    "Label", "State", "StartType", "PID"
                );
                let _ = writeln!(out, "{}", "-".repeat(96));
                for s in list.iter().take(60) {
                    let _ = writeln!(
                        out,
                        "{:<48} | {:<9} | {:<10} | {:<7} | {}",
                        s.name.chars().take(48).collect::<String>(),
                        s.state.as_str(),
                        s.start_type.as_str(),
                        s.pid.map_or_else(|| "-".to_string(), |p| p.to_string()),
                        s.user_account,
                    );
                }
                safe_print(&out);
            }
        }
        Commands::Startup { filter, format } => {
            let q = filter.map(|s| s.to_ascii_lowercase());
            let items: Vec<_> = list_startup_items()
                .into_iter()
                .filter(|i| {
                    q.as_ref()
                        .is_none_or(|f| i.name.to_ascii_lowercase().contains(f))
                })
                .collect();
            if format == OutputFormat::Json {
                safe_print(&serde_json::to_string_pretty(&items).unwrap_or_default());
            } else {
                let mut out = String::new();
                let _ = writeln!(out, "=== Nexus Startup Items ({}) ===", items.len());
                for item in &items {
                    let _ = writeln!(
                        out,
                        "{:<44} | {:<12} | {:<8} | {}",
                        item.name.chars().take(44).collect::<String>(),
                        item.item_type.as_str(),
                        if item.is_enabled {
                            "Enabled"
                        } else {
                            "Disabled"
                        },
                        item.location
                    );
                }
                safe_print(&out);
            }
        }
        Commands::Connections {
            protocol,
            state,
            filter,
            format,
        } => {
            let p_filter = protocol.map(|s| s.to_ascii_lowercase());
            let s_filter = state.map(|s| s.to_ascii_lowercase());
            let q_filter = filter.map(|s| s.to_ascii_lowercase());
            let conns: Vec<_> = list_network_connections()
                .into_iter()
                .filter(|c| {
                    p_filter.as_ref().is_none_or(|p| match p.as_str() {
                        "tcp4" => c.protocol == ConnectionProtocol::Tcp4,
                        "tcp6" => c.protocol == ConnectionProtocol::Tcp6,
                        "udp4" => c.protocol == ConnectionProtocol::Udp4,
                        "udp6" => c.protocol == ConnectionProtocol::Udp6,
                        _ => true,
                    }) && s_filter
                        .as_ref()
                        .is_none_or(|st| c.state.as_str().to_ascii_lowercase().contains(st))
                        && q_filter.as_ref().is_none_or(|q| {
                            c.process_name.to_ascii_lowercase().contains(q)
                                || c.local_address.contains(q)
                                || c.remote_address.contains(q)
                        })
                })
                .collect();
            if format == OutputFormat::Json {
                safe_print(&serde_json::to_string_pretty(&conns).unwrap_or_default());
            } else {
                let mut out = String::new();
                let _ = writeln!(
                    out,
                    "=== Nexus Active Network Connections ({}) ===",
                    conns.len()
                );
                let _ = writeln!(
                    out,
                    "{:<5} | {:<24} | {:<24} | {:<11} | {:<7} | {:<16} | ProcessKey Token",
                    "Proto", "Local", "Remote", "State", "PID", "Process"
                );
                let _ = writeln!(out, "{}", "-".repeat(115));
                for c in conns.iter().take(80) {
                    let _ = writeln!(
                        out,
                        "{:<5} | {:<24} | {:<24} | {:<11} | {:<7} | {:<16} | {}",
                        c.protocol.as_str(),
                        format!("{}:{}", c.local_address, c.local_port),
                        format!("{}:{}", c.remote_address, c.remote_port),
                        c.state.as_str(),
                        c.pid.map_or_else(|| "-".to_string(), |p| p.to_string()),
                        c.process_name.chars().take(16).collect::<String>(),
                        c.process_key
                            .map_or_else(|| "-".to_string(), |k| k.creation_token_us.to_string()),
                    );
                }
                safe_print(&out);
            }
        }
        Commands::Wireless {
            wifi_only,
            lan_only,
            subnet,
            format,
        } => {
            let wifi = if lan_only { None } else { Some(scan_wifi()) };
            let lan = if wifi_only {
                None
            } else {
                Some(scan_lan_radar(subnet.as_deref(), 120))
            };
            if format == OutputFormat::Json {
                let payload = serde_json::json!({ "wifi": wifi, "lan": lan });
                safe_println!(
                    "{}",
                    serde_json::to_string_pretty(&payload).unwrap_or_default()
                );
            } else {
                if let Some(w) = wifi {
                    safe_println!(
                        "=== Wireless RF Spectrum (iface={}, connected={}) ===",
                        w.interface,
                        w.connected_ssid.as_deref().unwrap_or("None/Redacted")
                    );
                    if !w.notes.is_empty() {
                        safe_println!("Notes: {}", w.notes);
                    }
                    for n in &w.networks {
                        safe_println!(
                            "  {:<28} | {:<7} | ch {:>3} | RSSI={} | Noise={} | {}",
                            n.ssid,
                            n.band.as_str(),
                            n.channel,
                            n.rssi_dbm.format_with_unit(0, "dBm"),
                            n.noise_dbm.format_with_unit(0, "dBm"),
                            n.security
                        );
                    }
                }
                if let Some(l) = lan {
                    safe_println!(
                        "=== LAN Radar (subnet={}, backend={}, elapsed={}ms, hosts={}) ===",
                        l.subnet,
                        l.scanner_backend,
                        l.elapsed_ms,
                        l.hosts.len()
                    );
                    for h in &l.hosts {
                        let ports: Vec<String> =
                            h.open_ports.iter().map(|p| p.port.to_string()).collect();
                        safe_println!(
                            "  {:<15} | {:<20} | {:<17} | lat={:<8} | ports={}",
                            h.ip,
                            h.hostname.chars().take(20).collect::<String>(),
                            h.mac.as_deref().unwrap_or("-"),
                            h.latency_ms.format_with_unit(1, "ms"),
                            if ports.is_empty() {
                                "-".to_string()
                            } else {
                                ports.join(",")
                            }
                        );
                    }
                }
            }
        }
        Commands::Health {
            format,
            interval_ms,
        } => {
            let mut snap = collect_with_warmup(&collector, interval_ms, false);
            if !snap.disks.is_empty()
                && let Some(vol0) = collect_mounted_volumes()
                    .into_iter()
                    .find(|v| v.mount_point == "/")
                && let Some(d0) = snap.disks.first_mut()
            {
                d0.root_used_percent = vol0.used_percent;
            }
            let report = HealthScoring::evaluate_snapshot(&snap);
            if format == OutputFormat::Json {
                safe_println!(
                    "{}",
                    serde_json::to_string_pretty(&report).unwrap_or_default()
                );
            } else {
                safe_println!(
                    "=== Nexus System Health: {:.1}/100 ({}) ===",
                    report.overall_score,
                    report.overall_level.as_str()
                );
                safe_println!(
                    "Bottleneck: {:?} ({:?}) - {}",
                    report.bottleneck.bottleneck,
                    report.bottleneck.severity,
                    report.bottleneck.headline
                );
                safe_println!("Explanation: {}", report.bottleneck.explanation);
                safe_println!("Advice:      {}", report.bottleneck.upgrade_advice);
                for sub in [
                    &report.cpu,
                    &report.memory,
                    &report.disk,
                    &report.gpu,
                    &report.thermal,
                ] {
                    safe_println!(
                        "  - {:<8}: {:<8} | {}",
                        sub.name,
                        sub.score
                            .map_or_else(|| "Excluded".to_string(), |s| format!("{s:.0}/100")),
                        sub.summary
                    );
                }
                safe_println!("Top Consumers:");
                for tc in &report.top_consumers {
                    safe_println!(
                        "  * {:<24} (PID {:<6}) CPU={:<7} Mem={:<10} Impact={:.1}",
                        tc.name.chars().take(24).collect::<String>(),
                        tc.key.pid,
                        tc.cpu_percent
                            .map_or_else(|| "-".to_string(), |v| format!("{v:.1}%")),
                        tc.memory_bytes.map_or_else(
                            || "-".to_string(),
                            |b| format!("{:.1} MiB", b / (1024.0 * 1024.0))
                        ),
                        tc.impact_score
                    );
                }
            }
        }
        Commands::Alerts { cmd } => {
            let prefs = UserPreferences::load_or_default(&UserPreferences::default_path());
            let mut alerts = AlertsService::default();
            alerts.cpu_alert_threshold = prefs.alerts_cpu_threshold;
            alerts.mem_alert_threshold = prefs.alerts_mem_threshold;
            alerts.temp_celsius_threshold = prefs.alerts_temp_threshold;
            alerts.disk_alert_threshold = prefs.alerts_disk_threshold;
            alerts.webhook_url = prefs.alerts_webhook_url;
            match cmd {
                AlertsSubcommand::List { format } => {
                    if format == OutputFormat::Json {
                        safe_println!(
                            "{}",
                            serde_json::to_string_pretty(&alerts).unwrap_or_default()
                        );
                    } else {
                        safe_println!("=== Nexus Configured Alert Thresholds ===");
                        safe_println!("  CPU Utilization: >= {:.1}%", alerts.cpu_alert_threshold);
                        safe_println!("  Memory Used:     >= {:.1}%", alerts.mem_alert_threshold);
                        safe_println!(
                            "  Thermal Max Die: >= {:.1} C",
                            alerts.temp_celsius_threshold
                        );
                        safe_println!("  Root Disk Used:  >= {:.1}%", alerts.disk_alert_threshold);
                        safe_println!(
                            "  Webhook URL:     {}",
                            alerts.webhook_url.as_deref().unwrap_or("(none)")
                        );
                    }
                }
                AlertsSubcommand::Status => {
                    let snap = collect_with_warmup(&collector, 250, false);
                    let fired = alerts.check_snapshot(&snap);
                    safe_println!(
                        "Alerts active now: {} (CPU={:.1}%, Mem={:.1}%, Temp={:.1}C)",
                        fired.len(),
                        alerts.cpu_alert_threshold,
                        alerts.mem_alert_threshold,
                        alerts.temp_celsius_threshold
                    );
                    for a in fired {
                        safe_println!(
                            "  ALERT {} = {:.1} >= {:.1}",
                            a.metric_name,
                            a.measured_value,
                            a.threshold
                        );
                    }
                }
                AlertsSubcommand::Watch { interval_ms, count } => {
                    let _ = collector.collect();
                    for _ in 0..count.max(1) {
                        thread::sleep(Duration::from_millis(interval_ms.max(100)));
                        let snap = collector.collect();
                        let fired = alerts.evaluate_with_state(&snap, now_unix_ms(), false);
                        safe_println!(
                            "[gen={}] CPU={} Mem={} Fired={}",
                            snap.generation,
                            snap.system.cpu_percent.format_with_unit(1, "%"),
                            snap.system.mem_used_percent.format_with_unit(1, "%"),
                            fired.len()
                        );
                    }
                }
            }
        }
        Commands::Rules(args) => {
            let prefs = UserPreferences::load_or_default(&UserPreferences::default_path());
            match args.sub {
                Some(RulesSubcommand::List { format }) => {
                    if format == OutputFormat::Json {
                        safe_println!(
                            "{}",
                            serde_json::to_string_pretty(&prefs.rules).unwrap_or_default()
                        );
                    } else {
                        safe_println!("=== Saved Process Rules ({}) ===", prefs.rules.len());
                        for r in &prefs.rules {
                            safe_println!(
                                "  [{}] pattern={:?} | {}",
                                r.id,
                                r.process_pattern,
                                r.summary()
                            );
                        }
                    }
                }
                Some(RulesSubcommand::Status) => {
                    let snap = collect_with_warmup(&collector, 250, false);
                    let mut engine = RulesEngine::new();
                    engine.rules = prefs.rules;
                    let evs = engine.evaluate_snapshot(&snap, now_unix_ms());
                    safe_println!(
                        "Rules Engine Status: {} rules configured, {} live triggers matching",
                        engine.rules.len(),
                        evs.len()
                    );
                }
                Some(RulesSubcommand::Eval {
                    cpu_threshold,
                    pattern,
                }) => eval_rules_once(&collector, &pattern, cpu_threshold),
                None => eval_rules_once(&collector, &args.pattern, args.cpu_threshold),
            }
        }
        Commands::Settings { cmd } => {
            let path = UserPreferences::default_path();
            let mut prefs = UserPreferences::load_or_default(&path);
            match cmd {
                SettingsSubcommand::Show => {
                    safe_println!(
                        "{}",
                        serde_json::to_string_pretty(&prefs).unwrap_or_default()
                    );
                }
                SettingsSubcommand::Get { key } => {
                    if let Some(val) = get_pref_key(&prefs, &key) {
                        safe_println!("{val}");
                    } else {
                        eprintln!("nexus: unknown settings key {key:?}");
                        return ExitCode::FAILURE;
                    }
                }
                SettingsSubcommand::Set { key, value } => {
                    if set_pref_key(&mut prefs, &key, &value) {
                        if let Err(e) = prefs.save_to(&path) {
                            eprintln!(
                                "nexus: failed to save preferences to {}: {e}",
                                path.display()
                            );
                            return ExitCode::FAILURE;
                        }
                        safe_println!("Updated {key}={value} -> {}", path.display());
                    } else {
                        eprintln!("nexus: invalid key or value ({key}={value})");
                        return ExitCode::FAILURE;
                    }
                }
            }
        }
        Commands::Prometheus {
            port,
            bind,
            once,
            grafana,
            telegraf,
        } => {
            if grafana {
                safe_print(GrafanaDashboard::JSON);
                return ExitCode::SUCCESS;
            }
            if telegraf {
                safe_print(&TelegrafConfigGenerator::generate(port));
                return ExitCode::SUCCESS;
            }
            let snap = collect_with_warmup(&collector, 250, false);
            let alerts_svc = AlertsService::default();
            let alert_cnt = alerts_svc.check_snapshot(&snap).len() as u64;
            let body = render_prometheus_metrics(&snap, alert_cnt, 0);
            if once {
                safe_print(&body);
                return ExitCode::SUCCESS;
            }
            let listen_addr = bind.unwrap_or_else(|| format!("127.0.0.1:{port}"));
            let listener = match TcpListener::bind(&listen_addr) {
                Ok(l) => l,
                Err(e) => {
                    eprintln!("nexus: failed to bind Prometheus listener at {listen_addr}: {e}");
                    return ExitCode::FAILURE;
                }
            };
            safe_println!("Serving Nexus Prometheus metrics at http://{listen_addr}/metrics");
            for stream in listener.incoming().flatten() {
                let live_snap = collector.collect();
                let live_alerts = alerts_svc.check_snapshot(&live_snap).len() as u64;
                let payload = render_prometheus_metrics(&live_snap, live_alerts, 0);
                handle_http_connection(stream, &payload);
            }
        }
        Commands::Export {
            format,
            output,
            last,
        } => {
            let store = match MetricsHistoryStore::open_path(&default_telemetry_db_path())
                .or_else(|_| MetricsHistoryStore::open_in_memory())
            {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("nexus: failed to open MetricsHistoryStore: {e}");
                    return ExitCode::FAILURE;
                }
            };
            let mut rows = store.query_recent(500).unwrap_or_default();
            if rows.is_empty() {
                let snap = collect_with_warmup(&collector, 200, false);
                let _ = store.insert_snapshot(&snap, now_unix_ms());
                rows = store.query_recent(10).unwrap_or_default();
            }
            let cutoff_ms = now_unix_ms().saturating_sub(parse_duration_ms(&last));
            let filtered: Vec<_> = rows
                .into_iter()
                .filter(|r| r.sampled_at_unix_ms >= cutoff_ms)
                .collect();

            let content = match format {
                ExportFormatArg::Json => {
                    serde_json::to_string_pretty(&filtered).unwrap_or_default()
                }
                ExportFormatArg::Csv => {
                    let mut out = String::from(
                        "timestamp_unix_ms,generation,cpu_percent,mem_used_bytes,mem_used_percent,gpu_percent,package_watts,max_die_celsius,disk_read_bps,disk_write_bps,net_rx_bps,net_tx_bps\n",
                    );
                    for r in &filtered {
                        let opt =
                            |o: Option<f64>| o.map_or_else(String::new, |v| format!("{v:.3}"));
                        let _ = writeln!(
                            out,
                            "{},{},{},{},{},{},{},{},{},{},{},{}",
                            r.sampled_at_unix_ms,
                            r.generation,
                            opt(r.cpu_percent),
                            opt(r.mem_used_bytes),
                            opt(r.mem_used_percent),
                            opt(r.gpu_percent),
                            opt(r.package_watts),
                            opt(r.max_die_celsius),
                            opt(r.disk_read_bps),
                            opt(r.disk_write_bps),
                            opt(r.net_rx_bps),
                            opt(r.net_tx_bps),
                        );
                    }
                    out
                }
            };
            if let Some(p) = output {
                if let Err(e) = std::fs::write(&p, &content) {
                    eprintln!("nexus: failed to write export file {}: {e}", p.display());
                    return ExitCode::FAILURE;
                }
                safe_println!("Exported {} row(s) to {}", filtered.len(), p.display());
            } else {
                print!("{content}");
            }
        }
        Commands::Disk { cmd } => {
            let mut store = match DiskSnapshotStore::open_path(&default_disk_snapshots_db_path())
                .or_else(|_| DiskSnapshotStore::open_in_memory())
            {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("nexus: failed to open DiskSnapshotStore: {e}");
                    return ExitCode::FAILURE;
                }
            };
            match cmd {
                DiskSubcommand::Scan {
                    path,
                    max_depth,
                    save,
                    label,
                    treemap,
                } => {
                    if !path.exists() {
                        eprintln!("nexus: scan path does not exist: {}", path.display());
                        return ExitCode::FAILURE;
                    }
                    let report = scan_directory_parallel(&path, max_depth);
                    let blobs = build_disk_shader_blobs(&report);
                    safe_println!("=== Nexus Disk Space Scanner ({}) ===", report.root_path);
                    safe_println!(
                        "Total Scanned: {} bytes across {} files ({} GPU metaball/defrag blobs prepared)",
                        report.total_bytes,
                        report.total_files,
                        blobs.len()
                    );
                    for cat in &report.categories {
                        safe_println!(
                            "  {:<20} | {:>12} B | {:>6} files | {:>5.1}%",
                            cat.category.label(),
                            cat.bytes,
                            cat.file_count,
                            cat.share_fraction * 100.0
                        );
                    }
                    if treemap {
                        let rects = compute_squarified_treemap(
                            &report.root_node.children,
                            RectBounds {
                                x: 0.0,
                                y: 0.0,
                                width: 1000.0,
                                height: 600.0,
                            },
                        );
                        safe_println!("Squarified Treemap Rectangles: {}", rects.len());
                    }
                    if save {
                        match store.save_disk_snapshot(&report, label.as_deref()) {
                            Ok(id) => safe_println!("Saved disk snapshot #{id}"),
                            Err(e) => {
                                eprintln!("nexus: failed to save disk snapshot: {e}");
                                return ExitCode::FAILURE;
                            }
                        }
                    }
                }
                DiskSubcommand::Snapshots { cmd } => match cmd {
                    DiskSnapshotsSubcommand::List { root } => {
                        let list = match store
                            .list_disk_snapshots(root.as_deref().and_then(|p| p.to_str()))
                        {
                            Ok(l) => l,
                            Err(e) => {
                                eprintln!("nexus: failed to list disk snapshots: {e}");
                                return ExitCode::FAILURE;
                            }
                        };
                        for s in list {
                            safe_println!(
                                "#{} [{}] {} | {} bytes | {} files",
                                s.id,
                                s.label,
                                s.root_path,
                                s.total_bytes,
                                s.total_files
                            );
                        }
                    }
                    DiskSnapshotsSubcommand::Delete { id } => {
                        match store.delete_disk_snapshot(id) {
                            Ok(ok) => safe_println!("Deleted disk snapshot #{id}: {ok}"),
                            Err(e) => {
                                eprintln!("nexus: failed to delete disk snapshot #{id}: {e}");
                                return ExitCode::FAILURE;
                            }
                        }
                    }
                },
                DiskSubcommand::Diff {
                    baseline_id,
                    target_id,
                } => {
                    let diff = match store.diff_disk_snapshots(baseline_id, target_id) {
                        Ok(d) => d,
                        Err(e) => {
                            eprintln!(
                                "nexus: failed to diff disk snapshots {baseline_id} -> {target_id}: {e}"
                            );
                            return ExitCode::FAILURE;
                        }
                    };
                    safe_println!(
                        "Disk Diff #{} -> #{} ({}): dBytes={:+}, dFiles={:+}, Added={}, Removed={}, Grown={}, Shrunk={}",
                        diff.baseline_id,
                        diff.target_id,
                        diff.root_path,
                        diff.total_delta_bytes,
                        diff.total_delta_files,
                        diff.added_paths.len(),
                        diff.removed_paths.len(),
                        diff.grown_paths.len(),
                        diff.shrunk_paths.len(),
                    );
                }
                DiskSubcommand::Duplicates { path, max_depth } => {
                    if !path.exists() {
                        eprintln!("nexus: scan path does not exist: {}", path.display());
                        return ExitCode::FAILURE;
                    }
                    let report = scan_directory_parallel(&path, max_depth);
                    let groups = find_duplicates(&report.root_node);
                    safe_println!(
                        "=== Nexus 2-Pass SHA-256 Duplicate Groups ({}) ===",
                        groups.len()
                    );
                    for g in groups.iter().take(25) {
                        safe_println!(
                            "  SHA256={}.. | {} bytes/file | {} wasted bytes | {} copies",
                            &g.hash_hex[..16.min(g.hash_hex.len())],
                            g.file_size,
                            g.wasted_bytes,
                            g.paths.len()
                        );
                        for p in &g.paths {
                            safe_println!("    - {p}");
                        }
                    }
                }
            }
        }
        Commands::Snapshot {
            format,
            interval_ms,
            single_tick,
        } => {
            let snap = collect_with_warmup(&collector, interval_ms, single_tick);
            match format {
                OutputFormat::Json => match serde_json::to_string_pretty(&snap) {
                    Ok(json) => safe_println!("{json}"),
                    Err(e) => {
                        eprintln!("nexus: failed to serialize snapshot JSON: {e}");
                        return ExitCode::FAILURE;
                    }
                },
                OutputFormat::Table => print_snapshot_table(&snap),
            }
        }
        Commands::Diff { interval_ms } => {
            let base = collect_with_warmup(&collector, 200, false);
            thread::sleep(Duration::from_millis(interval_ms.max(50)));
            let target = collector.collect();

            let db = match SnapshotDatabase::open_in_memory() {
                Ok(d) => d,
                Err(e) => {
                    eprintln!("nexus: failed to open SnapshotDatabase: {e}");
                    return ExitCode::FAILURE;
                }
            };
            let id1 = match db.save_snapshot("baseline", &base) {
                Ok(id) => id,
                Err(e) => {
                    eprintln!("nexus: failed to save baseline snapshot: {e}");
                    return ExitCode::FAILURE;
                }
            };
            let id2 = match db.save_snapshot("target", &target) {
                Ok(id) => id,
                Err(e) => {
                    eprintln!("nexus: failed to save target snapshot: {e}");
                    return ExitCode::FAILURE;
                }
            };
            let diff = SnapshotDiffer::diff(&base, &target);

            safe_println!(
                "=== Nexus Snapshot & Compare (SQLite IDs #{id1} -> #{id2}, gen {} -> {}) ===",
                diff.baseline_generation,
                diff.target_generation
            );
            safe_println!(
                "CPU Delta: {:+.2}% | Memory Used Delta: {:+.0} B",
                diff.cpu_percent_delta.unwrap_or(0.0),
                diff.mem_used_bytes_delta.unwrap_or(0.0)
            );
            safe_println!(
                "Spawned Processes: {} | Terminated Processes: {} | Modified Active Processes: {}",
                diff.spawned_processes.len(),
                diff.terminated_processes.len(),
                diff.modified_processes.len()
            );
            for m in diff.modified_processes.iter().take(10) {
                safe_println!(
                    "  [PID {:<6} token={}] {:<24} dCPU={:+.1}% dFootprint={:+.0} B",
                    m.key.pid,
                    m.key.creation_token_us,
                    m.name.chars().take(24).collect::<String>(),
                    m.cpu_delta_percent.unwrap_or(0.0),
                    m.footprint_delta_bytes.unwrap_or(0.0)
                );
            }
        }
        Commands::Top { limit, interval_ms } => {
            let snap = collect_with_warmup(&collector, interval_ms, false);
            print_top_processes(&snap, limit);
        }
        Commands::GamingMode { journal_path } => {
            let snap = collect_with_warmup(&collector, 200, false);
            let path =
                journal_path.unwrap_or_else(|| std::env::temp_dir().join("nexus-gaming-mode.json"));
            let mut gm = GamingModeService::new(&path);
            let autobalance = AutoBalanceService {
                enabled: true,
                cpu_hog_threshold_percent: 1.0,
                restore_threshold_percent: 0.5,
                throttle_nice: 10,
                active_throttles: Default::default(),
            };
            let receipts = autobalance.evaluate(&snap, None, now_unix_ms());
            if let Err(e) = gm.activate(None, receipts.clone()) {
                eprintln!(
                    "nexus: failed to write Gaming Mode journal {}: {e}",
                    path.display()
                );
                return ExitCode::FAILURE;
            }
            let throttle = gm.visual_throttle();
            safe_println!("=== Nexus Gaming Mode Activated ===");
            safe_println!(
                "Crash-Safe Receipt Journal: {} ({} receipts written)",
                path.display(),
                receipts.len()
            );
            safe_println!(
                "Zero-Overhead Visual Throttle: visualFrameRate={} Hz, bloom={}, shaders_paused={}, flat_popout={}, cadence={} ms",
                throttle.visual_frame_rate,
                throttle.visual_bloom_enabled,
                throttle.background_shaders_paused,
                throttle.popout_flat_segments,
                throttle.collector_interval_ms
            );
            match gm.deactivate_and_collect_valid_restores(&snap) {
                Ok(restored) => safe_println!(
                    "Gaming Mode Deactivated: {} receipts verified against live ProcessKey creation tokens and restored.",
                    restored.len()
                ),
                Err(e) => {
                    eprintln!("nexus: failed to deactivate Gaming Mode cleanly: {e}");
                    return ExitCode::FAILURE;
                }
            }
        }
        Commands::DiskScan { path, max_depth } => {
            if !path.exists() {
                eprintln!("nexus: scan path does not exist: {}", path.display());
                return ExitCode::FAILURE;
            }
            let report = scan_directory_parallel(&path, max_depth);
            let blobs = build_disk_shader_blobs(&report);
            safe_println!("=== Nexus Disk Space Scanner ({}) ===", report.root_path);
            safe_println!(
                "Total Scanned: {} bytes across {} files ({} GPU metaball/defrag blobs prepared)",
                report.total_bytes,
                report.total_files,
                blobs.len()
            );
            for cat in &report.categories {
                safe_println!(
                    "  {:<20} | {:>12} B | {:>6} files | {:>5.1}%",
                    cat.category.label(),
                    cat.bytes,
                    cat.file_count,
                    cat.share_fraction * 100.0
                );
            }
        }
        Commands::Honesty { interval_ms } => {
            let snap1 = collector.collect();
            thread::sleep(Duration::from_millis(interval_ms.max(50)));
            let snap2 = collector.collect();
            print_honesty_report(&snap1, &snap2);
        }
        Commands::Trace { cmd } => match cmd {
            TraceSubcommand::Record {
                seconds,
                interval_ms,
                output,
            } => {
                let first = collect_with_warmup(&collector, interval_ms.max(100), false);
                let hw = nexus_collectors_macos::system_ops::collect_system_hardware();
                let is_apple_silicon =
                    hw.architecture.contains("arm64") || first.system.gpu_model.contains("Apple");
                let mut rec = NexusTraceRecorder::new();
                if let Err(e) = rec.start(
                    &first,
                    hw.chip_model.as_str(),
                    format!("{} {}", hw.os_name, hw.os_version),
                    is_apple_silicon,
                ) {
                    eprintln!("nexus: failed to start .nexustrace recorder: {e}");
                    return ExitCode::FAILURE;
                }
                let steps = ((seconds.max(1) * 1000) / interval_ms.max(50)).max(1);
                for _ in 0..steps {
                    thread::sleep(Duration::from_millis(interval_ms.max(50)));
                    let snap = collector.collect();
                    if !rec.record_snapshot(&snap) {
                        break;
                    }
                }
                if let Some(out_path) = output {
                    let dir = out_path
                        .parent()
                        .filter(|p| !p.as_os_str().is_empty())
                        .unwrap_or_else(|| std::path::Path::new("."));
                    let saved = match rec.stop_and_save(dir) {
                        Ok(s) => s,
                        Err(e) => {
                            eprintln!(
                                "nexus: failed to save .nexustrace to {}: {e}",
                                dir.display()
                            );
                            return ExitCode::FAILURE;
                        }
                    };
                    if let Some(ref src_path) = saved
                        && *src_path != out_path
                    {
                        let trace = match read_trace_file(src_path) {
                            Ok(t) => t,
                            Err(e) => {
                                eprintln!(
                                    "nexus: failed to re-read trace {}: {e}",
                                    src_path.display()
                                );
                                return ExitCode::FAILURE;
                            }
                        };
                        if let Err(e) = write_trace_atomic_no_overwrite(&trace, &out_path) {
                            eprintln!("nexus: failed to write trace {}: {e}", out_path.display());
                            return ExitCode::FAILURE;
                        }
                        let _ = std::fs::remove_file(src_path);
                        safe_println!(
                            "Saved .nexustrace to {} ({} samples)",
                            out_path.display(),
                            trace.samples.len()
                        );
                    } else if let Some(p) = saved {
                        safe_println!("Saved .nexustrace to {}", p.display());
                    }
                } else {
                    match rec.stop_and_save(&default_traces_dir()) {
                        Ok(Some(saved)) => {
                            safe_println!("Saved .nexustrace to {}", saved.display())
                        }
                        Ok(None) => {}
                        Err(e) => {
                            eprintln!("nexus: failed to save .nexustrace: {e}");
                            return ExitCode::FAILURE;
                        }
                    }
                }
            }
            TraceSubcommand::Verify { path } => {
                let bytes = match std::fs::read(&path) {
                    Ok(b) => b,
                    Err(e) => {
                        eprintln!("nexus: failed to read trace file {}: {e}", path.display());
                        return ExitCode::FAILURE;
                    }
                };
                if let Err(e) = NexusTraceJsonPreflight::validate(&bytes) {
                    eprintln!(
                        "nexus: JSON preflight validation failed for {}: {e}",
                        path.display()
                    );
                    return ExitCode::FAILURE;
                }
                let trace = match read_trace_file(&path) {
                    Ok(t) => t,
                    Err(e) => {
                        eprintln!(
                            "nexus: failed to decode .nexustrace {}: {e}",
                            path.display()
                        );
                        return ExitCode::FAILURE;
                    }
                };
                safe_println!(
                    "OK: {} (format={}, version={}, samples={}, bytes={})",
                    path.display(),
                    trace.format,
                    trace.version,
                    trace.samples.len(),
                    bytes.len()
                );
            }
            TraceSubcommand::Inspect { path, samples } => {
                let trace = match read_trace_file(&path) {
                    Ok(t) => t,
                    Err(e) => {
                        eprintln!(
                            "nexus: failed to inspect .nexustrace {}: {e}",
                            path.display()
                        );
                        return ExitCode::FAILURE;
                    }
                };
                safe_println!("=== .nexustrace Inspection: {} ===", path.display());
                let arch_label = if nexus_core::HostPlatform::current().is_macos() {
                    "AppleSilicon"
                } else {
                    "Arm64SoC"
                };
                safe_println!(
                    "Machine: {} | OS: {} | {arch_label}: {} | RecordedAt: {:?}",
                    trace.machine_name,
                    trace.os_version,
                    trace.is_apple_silicon,
                    trace.recorded_at
                );
                safe_println!(
                    "Topology: E={} P={} GPU={:?} | Samples: {}",
                    trace.e_core_count,
                    trace.p_core_count,
                    trace.gpu_core_count,
                    trace.samples.len()
                );
                for (idx, s) in trace.samples.iter().take(samples).enumerate() {
                    safe_println!(
                        "  [{idx}] t={:.3}s gpu={:?}% pkgWatts={:?}W maxTempC={:?}C",
                        s.elapsed_seconds,
                        s.gpu_load_percent,
                        s.package_watts,
                        s.max_thermal_celsius
                    );
                }
            }
        },
        Commands::Benchmark {
            suite,
            duration_secs,
            format,
        } => {
            let cancel = std::sync::atomic::AtomicBool::new(false);
            let dur = Duration::from_secs_f64(duration_secs.clamp(0.05, 30.0));
            let mut cpu_single = 0.0;
            let mut cpu_multi = 0.0;
            let mut gpu_compute = 0.0;
            let mut gpu_graphics = 0.0;
            let mut disk_read = 0.0;
            let mut disk_write = 0.0;
            let mut net_down = 0.0;
            let mut net_up = 0.0;

            let mut hist = BenchmarkHistoryStore::load(&default_benchmark_history_path());
            let now_ts = (now_unix_ms() / 1000) as i64;

            if matches!(suite, BenchmarkSuiteArg::All | BenchmarkSuiteArg::Cpu) {
                let res = run_cpu_suite(dur, &cancel, |_| {});
                cpu_single = res.single_core_mops;
                cpu_multi = res.multi_core_mops;
                hist.record(
                    BenchmarkCategory::CPU,
                    now_ts,
                    cpu_single,
                    cpu_multi,
                    "CLI CPU benchmark",
                );
            }
            if matches!(suite, BenchmarkSuiteArg::All | BenchmarkSuiteArg::Gpu)
                && let Some((comp, gfx)) = nexus_ui::run_headless_gpu_benchmark(dur, &cancel)
            {
                gpu_compute = comp;
                gpu_graphics = gfx;
                hist.record(
                    BenchmarkCategory::GPU,
                    now_ts,
                    gpu_compute,
                    gpu_graphics,
                    "CLI Headless wgpu GPU benchmark",
                );
            }
            if matches!(suite, BenchmarkSuiteArg::All | BenchmarkSuiteArg::Disk) {
                let io = MacOsUncachedDiskIo;
                if let Ok(res) = run_disk_benchmark(&io, &std::env::temp_dir(), dur, &cancel, None)
                {
                    disk_read = res.read_mb_per_sec;
                    disk_write = res.write_mb_per_sec;
                    hist.record(
                        BenchmarkCategory::Disk,
                        now_ts,
                        disk_read,
                        disk_write,
                        "CLI F_NOCACHE+F_FULLFSYNC disk benchmark",
                    );
                }
            }
            if matches!(suite, BenchmarkSuiteArg::All | BenchmarkSuiteArg::Internet) {
                let (d, u, note) = run_internet_benchmark(dur, &cancel);
                net_down = d;
                net_up = u;
                hist.record(BenchmarkCategory::Internet, now_ts, d, u, &note);
            }
            let _ = hist.save(&default_benchmark_history_path());

            let ratios = benchmark_ratios(
                &[
                    cpu_single,
                    cpu_multi,
                    gpu_compute,
                    gpu_graphics,
                    disk_read,
                    disk_write,
                ],
                &DEFAULT_NEXUS_BASELINE,
            );
            let score = nexus_score(&ratios);
            match format {
                OutputFormat::Json => {
                    let payload = serde_json::json!({
                        "nexusScore": score,
                        "cpuSingleMops": cpu_single,
                        "cpuMultiMops": cpu_multi,
                        "gpuComputeGflops": gpu_compute,
                        "gpuGraphicsGflops": gpu_graphics,
                        "diskReadMBps": disk_read,
                        "diskWriteMBps": disk_write,
                        "internetDownMbps": net_down,
                        "internetUpMbps": net_up,
                    });
                    safe_println!(
                        "{}",
                        serde_json::to_string_pretty(&payload).unwrap_or_default()
                    );
                }
                OutputFormat::Table => {
                    safe_println!(
                        "=== Nexus Hardware Benchmark (NexusScore: {score}/300, 100=baseline) ==="
                    );
                    safe_println!(
                        "CPU:      single={} Mops/s | multi={} Mops/s",
                        six_character_ratio(cpu_single),
                        six_character_ratio(cpu_multi)
                    );
                    safe_println!(
                        "GPU:      compute={} GFLOPS | graphics={} GFLOPS",
                        six_character_ratio(gpu_compute),
                        six_character_ratio(gpu_graphics)
                    );
                    safe_println!(
                        "Disk:     read={} MB/s     | write={} MB/s",
                        six_character_ratio(disk_read),
                        six_character_ratio(disk_write)
                    );
                    safe_println!(
                        "Internet: down={} Mbps     | up={} Mbps",
                        six_character_ratio(net_down),
                        six_character_ratio(net_up)
                    );
                }
            }
        }
        Commands::Remote {
            port,
            allow_cgnat,
            no_bonjour,
            duration_secs,
        } => {
            let rt = match tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
            {
                Ok(r) => r,
                Err(e) => {
                    eprintln!("nexus: failed to build tokio runtime: {e}");
                    return ExitCode::FAILURE;
                }
            };
            let mut srv = match RemoteServerHandle::start(
                rt.handle(),
                RemoteServerConfig {
                    port,
                    allow_cgnat,
                    advertise_bonjour: !no_bonjour,
                },
            ) {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("nexus: failed to start companion remote server: {e}");
                    return ExitCode::FAILURE;
                }
            };
            {
                let st = srv.status.lock().unwrap_or_else(|e| e.into_inner());
                safe_println!("=== Nexus Companion Remote Server ===");
                safe_println!("{}", st.status_message);
                safe_println!(
                    "Pairing Code: {} | Allow CGNAT: {}",
                    st.pairing_code,
                    allow_cgnat
                );
            }
            let started = std::time::Instant::now();
            loop {
                let snap = collector.collect();
                srv.broadcast_snapshot(&snap);
                if let Some(limit) = duration_secs
                    && started.elapsed().as_secs() >= limit
                {
                    break;
                }
                thread::sleep(Duration::from_millis(500));
            }
            srv.stop();
        }
    }
    ExitCode::SUCCESS
}

fn eval_rules_once(collector: &MacCollector, pattern: &str, cpu_threshold: f64) {
    let snap = collect_with_warmup(collector, 250, false);
    let mut engine = RulesEngine::new();
    engine.add_rule(ProcessRule {
        id: CompactString::const_new("cli.watchdog"),
        process_pattern: CompactString::from(pattern),
        cpu_percent_threshold: Some(cpu_threshold),
        target_priority: Some(10),
        ..Default::default()
    });
    let now_ms = now_unix_ms();
    let events = engine.evaluate_snapshot(&snap, now_ms);
    let autobalance = AutoBalanceService {
        enabled: true,
        cpu_hog_threshold_percent: cpu_threshold.max(10.0),
        restore_threshold_percent: 40.0,
        throttle_nice: 10,
        active_throttles: Default::default(),
    };
    let ab_receipts = autobalance.evaluate(&snap, None, now_ms);
    let reclaim = MemoryReclaimService {
        enabled: true,
        used_percent_threshold: 50.0,
        pressure_percent_threshold: 50.0,
    };
    let reclaim_keys = reclaim.candidates(&snap, 5);

    safe_println!(
        "=== Nexus Automation & Rules Evaluation (pattern={pattern:?}, cpu>{cpu_threshold:.1}%) ==="
    );
    safe_println!(
        "Matched Watchdog Rules: {} | AutoBalance Receipts: {} | MemoryReclaim Top Candidates: {}",
        events.len(),
        ab_receipts.len(),
        reclaim_keys.len()
    );
    for ev in events.iter().take(10) {
        safe_println!(
            "  [Rule {}] PID {} (token={}) {:?} -> target_nice={:?}",
            ev.rule_id,
            ev.process_key.pid,
            ev.process_key.creation_token_us,
            ev.process_name,
            ev.target_priority
        );
    }
}

fn get_pref_key(prefs: &UserPreferences, key: &str) -> Option<String> {
    match key {
        "displayMode" | "display_mode" => Some(prefs.display_mode.title().to_string()),
        "vfdTextColor" | "vfd_text_color" => Some(format!("{:?}", prefs.vfd_text_color)),
        "glassPreset" | "glass_preset" => Some(format!("{:?}", prefs.glass_preset)),
        "updateIntervalMs" | "update_interval_ms" => Some(prefs.update_interval_ms.to_string()),
        "textSizePercent" | "text_size_percent" => Some(prefs.text_size_percent.to_string()),
        "popoutMode" | "popout_mode" => Some(format!("{:?}", prefs.popout_mode)),
        "activeSkinId" | "active_skin_id" => Some(prefs.active_skin_id.to_string()),
        "quietHoursEnabled" | "quiet_hours_enabled" => Some(prefs.quiet_hours_enabled.to_string()),
        _ => None,
    }
}

fn set_pref_key(prefs: &mut UserPreferences, key: &str, value: &str) -> bool {
    match key {
        "displayMode" | "display_mode" => {
            prefs.display_mode = match value.to_ascii_lowercase().as_str() {
                "color" => DisplayMode::Color,
                "mono" => DisplayMode::Mono,
                "green" => DisplayMode::Green,
                "amber" => DisplayMode::Amber,
                "blue" => DisplayMode::Blue,
                "red" => DisplayMode::Red,
                "purple" => DisplayMode::Purple,
                _ => return false,
            };
            true
        }
        "vfdTextColor" | "vfd_text_color" => {
            prefs.vfd_text_color = match value.to_ascii_lowercase().as_str() {
                "automatic" | "auto" => VfdTextColor::Automatic,
                "amber" => VfdTextColor::Amber,
                "green" => VfdTextColor::Green,
                "blue" | "cyan" => VfdTextColor::Blue,
                _ => return false,
            };
            true
        }
        "updateIntervalMs" | "update_interval_ms" => {
            if let Ok(ms) = value.parse::<u64>() {
                prefs.update_interval_ms = ms.clamp(100, 10_000);
                true
            } else {
                false
            }
        }
        "textSizePercent" | "text_size_percent" => {
            if let Ok(pct) = value.parse::<u16>() {
                prefs.text_size_percent = nexus_ui::clamp_text_size_percent(pct);
                true
            } else {
                false
            }
        }
        "popoutMode" | "popout_mode" => {
            prefs.popout_mode = match value.to_ascii_lowercase().as_str() {
                "off" => PopoutMode::Off,
                "desktop" => PopoutMode::Desktop,
                "normal" => PopoutMode::Normal,
                "topmost" => PopoutMode::TopMost,
                _ => return false,
            };
            true
        }
        "activeSkinId" | "active_skin_id" => {
            prefs.active_skin_id = CompactString::from(nexus_core::canonical_popout_skin_id(value));
            true
        }
        "quietHoursEnabled" | "quiet_hours_enabled" => {
            if let Ok(b) = value.parse::<bool>() {
                prefs.quiet_hours_enabled = b;
                true
            } else {
                false
            }
        }
        "glassPreset" | "glass_preset" => {
            for p in CrystalGlassPreset::ALL {
                let dbg = format!("{p:?}");
                if dbg.eq_ignore_ascii_case(value)
                    || p.metadata().id.as_str().eq_ignore_ascii_case(value)
                {
                    prefs.glass_preset = p;
                    return true;
                }
            }
            false
        }
        _ => false,
    }
}

fn safe_print(s: &str) {
    let mut out = std::io::stdout().lock();
    let _ = out.write_all(s.as_bytes());
    if !s.ends_with('\n') {
        let _ = out.write_all(b"\n");
    }
    let _ = out.flush();
}

fn parse_duration_ms(s: &str) -> i64 {
    let trimmed = s.trim().to_ascii_lowercase();
    if let Some(num) = trimmed.strip_suffix('h')
        && let Ok(h) = num.parse::<f64>()
    {
        return (h * 3_600_000.0) as i64;
    }
    if let Some(num) = trimmed.strip_suffix('m')
        && let Ok(m) = num.parse::<f64>()
    {
        return (m * 60_000.0) as i64;
    }
    3_600_000
}

fn handle_http_connection(mut stream: std::net::TcpStream, body: &str) {
    let mut req_buf = [0u8; 1024];
    let _ = stream.read(&mut req_buf);
    let response = format!(
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain; version=0.0.4; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        body.len(),
        body
    );
    let _ = stream.write_all(response.as_bytes());
}

fn collect_with_warmup(
    collector: &MacCollector,
    interval_ms: u64,
    single_tick: bool,
) -> SystemSnapshot {
    let snap1 = collector.collect();
    if single_tick {
        return snap1;
    }
    thread::sleep(Duration::from_millis(interval_ms.max(20)));
    collector.collect()
}

fn metric_num(metric: &Metric<f64>) -> f64 {
    match metric.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => value,
        _ => -1.0,
    }
}

fn format_bytes(metric: &Metric<f64>) -> String {
    match metric.reading {
        MetricReading::Measured { value, stale } => {
            let gib = value / (1024.0 * 1024.0 * 1024.0);
            let mib = value / (1024.0 * 1024.0);
            let base = if gib >= 1.0 {
                format!("{gib:.2} GiB")
            } else if mib >= 1.0 {
                format!("{mib:.1} MiB")
            } else {
                format!("{value:.0} B")
            };
            if stale {
                format!("{base} (stale)")
            } else {
                base
            }
        }
        MetricReading::Unavailable(r) => r.placeholder().to_string(),
    }
}

fn format_rate(metric: &Metric<f64>) -> String {
    match metric.reading {
        MetricReading::Measured { value, stale } => {
            let mib_s = value / (1024.0 * 1024.0);
            let kib_s = value / 1024.0;
            let base = if mib_s >= 1.0 {
                format!("{mib_s:.2} MiB/s")
            } else if kib_s >= 1.0 {
                format!("{kib_s:.1} KiB/s")
            } else {
                format!("{value:.0} B/s")
            };
            if stale {
                format!("{base} (stale)")
            } else {
                base
            }
        }
        MetricReading::Unavailable(r) => r.placeholder().to_string(),
    }
}

fn print_metric_row(snap: &SystemSnapshot, label: &str, value_str: String, metric: &Metric<f64>) {
    let state = metric.state().as_str();
    let quality = metric.provenance.quality.as_str();
    let source = snap.source_name(metric.provenance.source);
    let win_ms = (metric.provenance.window_ns as f64) / 1_000_000.0;
    safe_println!(
        "{label:<32} | {value_str:<18} | {state:<16} | {quality:<13} | {win_ms:>7.1} ms | {source}"
    );
}

fn print_snapshot_table(snap: &SystemSnapshot) {
    safe_println!(
        "=== Nexus Snapshot (gen={}, schema=v{}, coherence_fault={:?}) ===",
        snap.generation,
        snap.schema_version,
        snap.first_coherence_fault()
    );
    safe_println!(
        "Topology: {} P-cores, {} E-cores ({} total logical), GPU: {}",
        snap.system.performance_core_count,
        snap.system.efficiency_core_count,
        snap.system.cores.len(),
        snap.system.gpu_model
    );
    safe_println!(
        "{:<32} | {:<18} | {:<16} | {:<13} | {:>10} | Provenance Source",
        "Metric",
        "Reading",
        "ValueState",
        "Quality",
        "Window"
    );
    safe_println!("{}", "-".repeat(120));

    print_metric_row(
        snap,
        "system.cpu_percent",
        snap.system.cpu_percent.format_with_unit(1, "%"),
        &snap.system.cpu_percent,
    );
    print_metric_row(
        snap,
        "system.mem_used_percent",
        snap.system.mem_used_percent.format_with_unit(1, "%"),
        &snap.system.mem_used_percent,
    );
    print_metric_row(
        snap,
        "system.gpu_utilization_percent",
        snap.system.gpu_utilization_percent.format_with_unit(1, "%"),
        &snap.system.gpu_utilization_percent,
    );
    let sp = &snap.hardware_telemetry.soc_power;
    print_metric_row(
        snap,
        "soc.package_watts",
        sp.package_watts.format_with_unit(2, "W"),
        &sp.package_watts,
    );
    print_metric_row(
        snap,
        "soc.system_power_in_watts",
        sp.system_power_in_watts.format_with_unit(2, "W"),
        &sp.system_power_in_watts,
    );
    let th = &snap.hardware_telemetry.thermals;
    print_metric_row(
        snap,
        "thermal.max_die_celsius",
        th.max_die_celsius.format_with_unit(1, "C"),
        &th.max_die_celsius,
    );
    if let Some(d0) = snap.disks.first() {
        print_metric_row(
            snap,
            "disk0.read_bytes_per_sec",
            format_rate(&d0.read_bytes_per_sec),
            &d0.read_bytes_per_sec,
        );
    }
    safe_println!(
        "\nProcesses: {} live ({} exited retained), Disks: {}, Active Net Interfaces: {}",
        snap.processes.len(),
        snap.exited_processes.len(),
        snap.disks.len(),
        snap.networks.len()
    );
}

fn print_top_processes(snap: &SystemSnapshot, limit: usize) {
    let mut procs = snap.processes.clone();
    procs.sort_by(|a, b| {
        metric_num(&b.phys_footprint_bytes)
            .partial_cmp(&metric_num(&a.phys_footprint_bytes))
            .unwrap_or(std::cmp::Ordering::Equal)
    });

    safe_println!(
        "=== Top {} Processes by Physical Footprint (gen={}) ===",
        limit,
        snap.generation
    );
    safe_println!(
        "{:<8} | {:<20} | {:<24} | {:<14} | {:<10} | {:<14} | Source",
        "PID",
        "CreationToken(us)",
        "Name",
        "PhysFootprint",
        "CPU %",
        "FootprintState"
    );
    safe_println!("{}", "-".repeat(115));

    for p in procs.into_iter().take(limit) {
        let fp_str = format_bytes(&p.phys_footprint_bytes);
        let cpu_str = p.cpu_percent.format_with_unit(1, "%");
        let state_str = p.phys_footprint_bytes.state().as_str();
        let src_str = snap.source_name(p.phys_footprint_bytes.provenance.source);
        safe_println!(
            "{:<8} | {:<20} | {:<24} | {:<14} | {:<10} | {:<14} | {}",
            p.key.pid,
            p.key.creation_token_us,
            p.name.chars().take(24).collect::<String>(),
            fp_str,
            cpu_str,
            state_str,
            src_str
        );
    }
}

fn print_honesty_report(snap1: &SystemSnapshot, snap2: &SystemSnapshot) {
    safe_println!("=== Nexus System Monitor Honesty & Provenance Audit ===");
    safe_println!(
        "Tick 1 (cold start):cpu_percent state = {} (placeholder = {:?})",
        snap1.system.cpu_percent.state().as_str(),
        snap1.system.cpu_percent.state().placeholder()
    );
    safe_println!(
        "Tick 2 (warm delta): cpu_percent state = {} (reading     = {})",
        snap2.system.cpu_percent.state().as_str(),
        snap2.system.cpu_percent.format_with_unit(1, "%")
    );
    safe_println!(
        "FirstCoherenceFault: tick1={:?}, tick2={:?}",
        snap1.first_coherence_fault(),
        snap2.first_coherence_fault()
    );
    safe_println!(
        "CoherenceRepairs:    clamped={}, marked_unavailable={}, duplicate_rows_dropped={}",
        snap2.coherence_repairs.percentages_clamped,
        snap2.coherence_repairs.marked_unavailable,
        snap2.coherence_repairs.duplicate_rows_dropped
    );

    let mut rusage_ok = 0usize;
    let mut access_denied = 0usize;
    let mut fallback_taskinfo = 0usize;
    for p in &snap2.processes {
        match p.phys_footprint_bytes.state() {
            ValueState::Valid | ValueState::Zero => rusage_ok += 1,
            ValueState::AccessDenied => access_denied += 1,
            _ => {}
        }
        if matches!(
            p.resident_bytes.state(),
            ValueState::Valid | ValueState::Zero
        ) && p.phys_footprint_bytes.state() == ValueState::AccessDenied
        {
            fallback_taskinfo += 1;
        }
    }
    safe_println!(
        "Process Provenance:  {} authoritative rusage_v4, {} accessDenied (unfabricated), {} proc_pidtaskinfo RSS fallbacks",
        rusage_ok,
        access_denied,
        fallback_taskinfo
    );

    safe_println!("\nInterned Provenance Sources ({}):", snap2.sources.len());
    for (idx, src) in snap2.sources.iter().enumerate() {
        safe_println!("  [SourceId({idx})] {src}");
    }

    validate_all_embedded_shaders().expect("all 3 WGSL shaders must validate");
    let theme = ThemeEngine::new();
    let skins = bundled::load_bundled_skins().expect("bundled skins must validate");
    safe_println!(
        "\nGPU UI & Assets Verified: 3 WGSL shaders (naga OK) + {} font faces + {} .nexusskin v1 definitions",
        theme.font_system.db().faces().count(),
        skins.len()
    );
    for s in &skins {
        safe_println!("  - {} ({:?}) format=v{}", s.id, s.name, s.format);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cli_parses_all_13_commands_and_aliases() {
        assert!(Cli::try_parse_from(["nexus", "dashboard", "--frames", "8"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "tui", "--frames", "2"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "ps", "--sort", "cpu", "--dry-run"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "services", "--running"]).is_ok());
        assert!(
            Cli::try_parse_from(["nexus", "svc", "--start", "com.example", "--dry-run"]).is_ok()
        );
        assert!(Cli::try_parse_from(["nexus", "startup", "--format", "json"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "connections", "--protocol", "tcp4"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "netstat"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "wireless", "--wifi-only"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "radar", "--lan-only"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "health", "--format", "json"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "alerts", "list"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "rules", "status"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "settings", "show"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "prometheus", "--once"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "prom", "--grafana"]).is_ok());
        assert!(
            Cli::try_parse_from(["nexus", "export", "--format", "csv", "--last", "1h"]).is_ok()
        );
        assert!(Cli::try_parse_from(["nexus", "disk", "scan", "."]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "disk", "snapshots", "list"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "disk", "duplicates", "."]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "trace", "record", "--seconds", "1"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "trace", "verify", "test.nexustrace"]).is_ok());
        assert!(Cli::try_parse_from(["nexus", "trace", "inspect", "test.nexustrace"]).is_ok());
        assert!(
            Cli::try_parse_from([
                "nexus",
                "benchmark",
                "--suite",
                "cpu",
                "--duration-secs",
                "0.2"
            ])
            .is_ok()
        );
        assert!(
            Cli::try_parse_from(["nexus", "remote", "--no-bonjour", "--duration-secs", "1"])
                .is_ok()
        );
    }

    #[test]
    fn headless_tui_renders_all_8_panes_cleanly() {
        let collector = MacCollector::new();
        let out = tui::run_tui(&collector, Some(8))
            .expect("headless TUI succeeds")
            .expect("returns rendered text");
        assert!(out.contains("Nexus System Monitor"));
        assert!(out.contains("Honesty Engine"));
    }
}
