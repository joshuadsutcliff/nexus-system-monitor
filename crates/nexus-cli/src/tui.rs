use compact_str::CompactString;
use crossterm::event::{self, Event, KeyCode, KeyEventKind};
use crossterm::execute;
use crossterm::terminal::{
    EnterAlternateScreen, LeaveAlternateScreen, disable_raw_mode, enable_raw_mode,
};
use nexus_automation::{
    ActionCoordinator, ActionRequest, GamingModeService, ProcessActionType, ProcessRule,
    RulesEngine, now_unix_ms,
};
use nexus_collectors_macos::MacCollector;
use nexus_collectors_macos::system_ops::{
    list_network_connections, list_services, list_startup_items,
};
use nexus_core::{
    DisplayMode, HealthScoring, Metric, MetricReading, NetworkConnectionEntry, ServiceEntry,
    StartupItem, SystemSnapshot, bundled,
};
use nexus_disk::{DiskScanReport, RectBounds, compute_squarified_treemap, scan_directory_parallel};
use nexus_ui::{TaskManagerApp, validate_all_embedded_shaders};
use ratatui::prelude::*;
use ratatui::widgets::{Block, Borders, Cell, Gauge, Paragraph, Row, Table, TableState, Tabs};
use std::io;
use std::time::{Duration, Instant};

const TUI_PANES: [&str; 8] = [
    "1:Summary",
    "2:Performance",
    "3:Processes",
    if cfg!(target_os = "macos") {
        "4:AppleSilicon"
    } else {
        "4:SoC&Thermals"
    },
    "5:Svc&Startup",
    "6:Net&Radar",
    "7:Disk&Map",
    "8:Honesty&Rules",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SortColumn {
    Memory,
    Cpu,
    Pid,
    Name,
}

impl SortColumn {
    pub const fn next(self) -> Self {
        match self {
            Self::Memory => Self::Cpu,
            Self::Cpu => Self::Pid,
            Self::Pid => Self::Name,
            Self::Name => Self::Memory,
        }
    }

    pub const fn label(self) -> &'static str {
        match self {
            Self::Memory => "Memory (PhysFootprint)",
            Self::Cpu => "CPU %",
            Self::Pid => "PID",
            Self::Name => "Name",
        }
    }
}

pub struct TuiState {
    pub active_pane: usize,
    pub sort_col: SortColumn,
    pub search_mode: bool,
    pub search_query: String,
    pub selected_row: usize,
    pub display_mode_idx: usize,
    pub gaming_mode: GamingModeService,
    pub rules_engine: RulesEngine,
    pub action_coordinator: ActionCoordinator,
    pub app: TaskManagerApp,
    pub status_banner: String,
    pub snapshot: SystemSnapshot,
    pub cpu_sparkline: Vec<u64>,
    pub cached_disk_report: DiskScanReport,
    pub cached_services: Vec<ServiceEntry>,
    pub cached_startup: Vec<StartupItem>,
    pub cached_connections: Vec<NetworkConnectionEntry>,
}

impl TuiState {
    pub fn new(initial: SystemSnapshot) -> Self {
        let journal = std::env::temp_dir().join("nexus-tui-gaming.json");
        let mut rules_engine = RulesEngine::new();
        rules_engine.add_rule(ProcessRule {
            id: CompactString::const_new("tui.cpu_guard"),
            process_pattern: CompactString::const_new("*"),
            cpu_percent_threshold: Some(25.0),
            target_priority: Some(10),
            ..Default::default()
        });
        let mut app = TaskManagerApp::new();
        app.apply_snapshot(&initial, 0.25);

        let scan_dir = std::path::Path::new("/Users/josh/Github/nexus-system-monitor-rs/crates");
        let fallback = std::env::current_dir().unwrap_or_else(|_| std::path::PathBuf::from("."));
        let scan_target = if scan_dir.exists() {
            scan_dir
        } else {
            fallback.as_path()
        };
        let cached_disk_report = scan_directory_parallel(scan_target, 4);

        Self {
            active_pane: 0,
            sort_col: SortColumn::Memory,
            search_mode: false,
            search_query: String::new(),
            selected_row: 0,
            display_mode_idx: 0,
            gaming_mode: GamingModeService::new(journal),
            rules_engine,
            action_coordinator: ActionCoordinator::new(),
            app,
            status_banner: "Ready | Tab/1-8:Pane  /:Search  s:Sort  d:DisplayMode  g:GamingMode  k:Terminate(DryRun)  q:Quit".to_string(),
            snapshot: initial,
            cpu_sparkline: Vec::with_capacity(64),
            cached_disk_report,
            cached_services: list_services(),
            cached_startup: list_startup_items(),
            cached_connections: list_network_connections(),
        }
    }

    pub fn update_snapshot(&mut self, snap: SystemSnapshot, dt_s: f64) {
        if let MetricReading::Measured { value, .. } = snap.system.cpu_percent.reading {
            if self.cpu_sparkline.len() >= 60 {
                self.cpu_sparkline.remove(0);
            }
            self.cpu_sparkline.push(value.clamp(0.0, 100.0) as u64);
        }
        self.app.process_table.search_query = self.search_query.clone();
        self.app.apply_snapshot(&snap, dt_s);
        self.snapshot = snap;
    }

    pub fn cycle_display_mode(&mut self) {
        const MODES: [DisplayMode; 7] = [
            DisplayMode::Color,
            DisplayMode::Mono,
            DisplayMode::Green,
            DisplayMode::Amber,
            DisplayMode::Blue,
            DisplayMode::Red,
            DisplayMode::Purple,
        ];
        self.display_mode_idx = (self.display_mode_idx + 1) % MODES.len();
        self.app.theme.display_mode = MODES[self.display_mode_idx];
        self.status_banner = format!(
            "Switched DisplayMode -> {}",
            self.app.theme.display_mode.title()
        );
    }

    pub fn toggle_gaming_mode(&mut self) {
        if self.gaming_mode.active {
            let restored = self
                .gaming_mode
                .deactivate_and_collect_valid_restores(&self.snapshot)
                .unwrap_or_default();
            self.app
                .set_gaming_mode_throttle(self.gaming_mode.visual_throttle());
            self.status_banner = format!(
                "Gaming Mode OFF | Restored {} verified ProcessKey receipts",
                restored.len()
            );
        } else {
            let _ = self.gaming_mode.activate(None, Vec::new());
            self.app
                .set_gaming_mode_throttle(self.gaming_mode.visual_throttle());
            self.status_banner =
                "Gaming Mode ON | visualFrameRate=0 (snap), Bloom OFF, Shaders Paused, Cadence=2.0s"
                    .to_string();
        }
    }

    pub fn terminate_selected_dry_run(&mut self) {
        let rows = self.app.process_table.build_rows(&self.snapshot);
        let Some(row) = rows.get(self.selected_row) else {
            self.status_banner = "No process selected".to_string();
            return;
        };
        let key = row.snapshot.key;
        let res = self.action_coordinator.execute(
            &key,
            ActionRequest {
                action_type: ProcessActionType::Terminate,
                signal: None,
                priority: None,
            },
            true,
        );
        match res {
            Ok(()) => {
                self.status_banner = format!(
                    "Verified ProcessKey(pid={}, token={}) twice -> SIGTERM dry-run OK",
                    key.pid, key.creation_token_us
                );
            }
            Err(e) => {
                self.status_banner = format!("Refused action on PID {}: {e}", key.pid);
            }
        }
    }

    pub fn accent_color(&self) -> Color {
        match self.app.theme.display_mode {
            DisplayMode::Color => Color::Cyan,
            DisplayMode::Mono => Color::White,
            DisplayMode::Green => Color::LightGreen,
            DisplayMode::Amber => Color::Yellow,
            DisplayMode::Blue => Color::LightBlue,
            DisplayMode::Red => Color::LightRed,
            DisplayMode::Purple => Color::LightMagenta,
        }
    }
}

/// Runs the interactive `ratatui` + `crossterm` terminal UI (`nexus dashboard` / `nexus tui`).
/// When `max_frames` is `Some(n)`, renders `n` frames into a headless `TestBackend` and returns the rendered text buffer.
pub fn run_tui(collector: &MacCollector, max_frames: Option<usize>) -> io::Result<Option<String>> {
    let snap1 = collector.collect();
    std::thread::sleep(Duration::from_millis(120));
    let snap2 = collector.collect();
    let mut state = TuiState::new(snap2);
    state.update_snapshot(snap1, 0.12);
    state.update_snapshot(collector.collect(), 0.12);

    if let Some(frames) = max_frames {
        let backend = ratatui::backend::TestBackend::new(120, 36);
        let mut terminal = Terminal::new(backend)?;
        for i in 0..frames.max(1) {
            state.active_pane = i % TUI_PANES.len();
            terminal.draw(|f| draw_tui_frame(f, &mut state))?;
        }
        let buf = terminal.backend().buffer().clone();
        let mut text = String::new();
        for y in 0..buf.area.height {
            for x in 0..buf.area.width {
                text.push_str(buf[(x, y)].symbol());
            }
            text.push('\n');
        }
        return Ok(Some(text));
    }

    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen)?;
    let backend = CrosstermBackend::new(stdout);
    let mut terminal = Terminal::new(backend)?;

    let mut last_tick = Instant::now();
    let res = loop {
        terminal.draw(|f| draw_tui_frame(f, &mut state))?;

        let tick_rate = Duration::from_millis(
            state
                .gaming_mode
                .visual_throttle()
                .collector_interval_ms
                .min(500),
        );
        let timeout = tick_rate.saturating_sub(last_tick.elapsed());
        if event::poll(timeout)?
            && let Event::Key(key) = event::read()?
            && key.kind == KeyEventKind::Press
        {
            if state.search_mode {
                match key.code {
                    KeyCode::Enter | KeyCode::Esc => state.search_mode = false,
                    KeyCode::Backspace => {
                        state.search_query.pop();
                    }
                    KeyCode::Char(c) => {
                        state.search_query.push(c);
                    }
                    _ => {}
                }
            } else {
                match key.code {
                    KeyCode::Char('q') | KeyCode::Esc => break Ok(None),
                    KeyCode::Tab | KeyCode::Right => {
                        state.active_pane = (state.active_pane + 1) % TUI_PANES.len();
                    }
                    KeyCode::BackTab | KeyCode::Left => {
                        state.active_pane =
                            (state.active_pane + TUI_PANES.len() - 1) % TUI_PANES.len();
                    }
                    KeyCode::Char(c @ '1'..='8') => {
                        state.active_pane = (c as usize) - ('1' as usize);
                    }
                    KeyCode::Char('/') => {
                        state.search_mode = true;
                        state.active_pane = 2;
                    }
                    KeyCode::Char('s') => {
                        state.sort_col = state.sort_col.next();
                        state.status_banner = format!("Sort column -> {}", state.sort_col.label());
                    }
                    KeyCode::Char('d') => state.cycle_display_mode(),
                    KeyCode::Char('g') => state.toggle_gaming_mode(),
                    KeyCode::Char('k') => state.terminate_selected_dry_run(),
                    KeyCode::Down | KeyCode::Char('j') => {
                        state.selected_row = state.selected_row.saturating_add(1);
                    }
                    KeyCode::Up | KeyCode::Char('i') => {
                        state.selected_row = state.selected_row.saturating_sub(1);
                    }
                    _ => {}
                }
            }
        }

        if last_tick.elapsed() >= tick_rate {
            let dt = last_tick.elapsed().as_secs_f64();
            last_tick = Instant::now();
            state.update_snapshot(collector.collect(), dt);
        }
    };

    disable_raw_mode()?;
    execute!(terminal.backend_mut(), LeaveAlternateScreen)?;
    terminal.show_cursor()?;
    res
}

pub fn draw_tui_frame(f: &mut Frame<'_>, state: &mut TuiState) {
    let accent = state.accent_color();
    let chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints([
            Constraint::Length(3),
            Constraint::Length(4),
            Constraint::Min(12),
            Constraint::Length(3),
        ])
        .split(f.area());

    let titles: Vec<Line<'_>> = TUI_PANES.iter().map(|t| Line::from(*t)).collect();
    let tabs = Tabs::new(titles)
        .block(
            Block::default()
                .borders(Borders::ALL)
                .title(format!(
                    " Nexus System Monitor v0.8.1 | Mode: {} | GamingMode: {} | Gen: {} ",
                    state.app.theme.display_mode.title(),
                    if state.gaming_mode.active {
                        "ON"
                    } else {
                        "OFF"
                    },
                    state.snapshot.generation
                ))
                .border_style(Style::default().fg(accent)),
        )
        .select(state.active_pane)
        .highlight_style(Style::default().fg(Color::Black).bg(accent).bold());
    f.render_widget(tabs, chunks[0]);

    let meter_chunks = Layout::default()
        .direction(Direction::Horizontal)
        .constraints([
            Constraint::Percentage(33),
            Constraint::Percentage(34),
            Constraint::Percentage(33),
        ])
        .split(chunks[1]);

    let cpu_pct = (state.app.cpu_meter.displayed_level * 100.0).clamp(0.0, 100.0) as u16;
    let mem_pct = (state.app.mem_meter.displayed_level * 100.0).clamp(0.0, 100.0) as u16;
    let gpu_pct = (state.app.gpu_meter.displayed_level * 100.0).clamp(0.0, 100.0) as u16;

    f.render_widget(
        Gauge::default()
            .block(Block::default().borders(Borders::ALL).title(" VFD CPU "))
            .gauge_style(Style::default().fg(accent))
            .percent(cpu_pct)
            .label(state.snapshot.system.cpu_percent.format_with_unit(1, "%")),
        meter_chunks[0],
    );
    f.render_widget(
        Gauge::default()
            .block(Block::default().borders(Borders::ALL).title(" VFD Memory "))
            .gauge_style(Style::default().fg(accent))
            .percent(mem_pct)
            .label(
                state
                    .snapshot
                    .system
                    .mem_used_percent
                    .format_with_unit(1, "%"),
            ),
        meter_chunks[1],
    );
    f.render_widget(
        Gauge::default()
            .block(
                Block::default()
                    .borders(Borders::ALL)
                    .title(format!(" VFD GPU ({}) ", state.snapshot.system.gpu_model)),
            )
            .gauge_style(Style::default().fg(accent))
            .percent(gpu_pct)
            .label(
                state
                    .snapshot
                    .system
                    .gpu_utilization_percent
                    .format_with_unit(1, "%"),
            ),
        meter_chunks[2],
    );

    match state.active_pane {
        0 => draw_summary_health_pane(f, chunks[2], state, accent),
        1 => draw_performance_pane(f, chunks[2], state, accent),
        2 => draw_processes_pane(f, chunks[2], state, accent),
        3 => draw_apple_silicon_pane(f, chunks[2], state, accent),
        4 => draw_services_startup_pane(f, chunks[2], state, accent),
        5 => draw_net_radar_pane(f, chunks[2], state, accent),
        6 => draw_disk_space_pane(f, chunks[2], state, accent),
        _ => draw_honesty_and_benchmarks_pane(f, chunks[2], state, accent),
    }

    let footer_text = if state.search_mode {
        format!(
            "SEARCH PROCESSES: {}_ (Press Enter/Esc to finish)",
            state.search_query
        )
    } else {
        state.status_banner.clone()
    };
    let footer = Paragraph::new(footer_text).block(
        Block::default()
            .borders(Borders::ALL)
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(footer, chunks[3]);
}

fn draw_summary_health_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let health = HealthScoring::evaluate_snapshot(&state.snapshot);
    let lines = vec![
        Line::from(format!(
            "Health Score: {:.1}/100 ({}) | Bottleneck: {:?} ({:?}) - {}",
            health.overall_score,
            health.overall_level.as_str(),
            health.bottleneck.bottleneck,
            health.bottleneck.severity,
            health.bottleneck.headline
        )),
        Line::from(format!(
            "Subsystems: CPU={} | Memory={} | Disk={} | GPU={} | Thermal={}",
            health
                .cpu
                .score
                .map_or("N/A".to_string(), |s| format!("{s:.0}")),
            health
                .memory
                .score
                .map_or("N/A".to_string(), |s| format!("{s:.0}")),
            health
                .disk
                .score
                .map_or("N/A".to_string(), |s| format!("{s:.0}")),
            health
                .gpu
                .score
                .map_or("Excluded".to_string(), |s| format!("{s:.0}")),
            health
                .thermal
                .score
                .map_or("N/A".to_string(), |s| format!("{s:.0}")),
        )),
        Line::from(format!(
            "PageEngine: {} tiles | Skin: {} | Presets: {}",
            state.app.page_engine.tiles.len(),
            state.app.theme.active_skin_id,
            nexus_ui::CrystalGlassPreset::ALL.len(),
        )),
        Line::from(format!(
            "Live Inventory: {} processes ({} exited), {} launchd services, {} startup items, {} sockets",
            state.snapshot.processes.len(),
            state.snapshot.exited_processes.len(),
            state.cached_services.len(),
            state.cached_startup.len(),
            state.cached_connections.len(),
        )),
    ];
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(" System Summary & Health Scoring ")
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn draw_performance_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let snap = &state.snapshot;
    let sp = &snap.hardware_telemetry.soc_power;
    let th = &snap.hardware_telemetry.thermals;
    let lines = vec![
        Line::from(format!(
            "Topology:    {} P-cores + {} E-cores ({} logical) | GPU: {}",
            snap.system.performance_core_count,
            snap.system.efficiency_core_count,
            snap.system.cores.len(),
            snap.system.gpu_model
        )),
        Line::from(format!(
            "CPU Load:    Total={} | User={} | Kernel={} | LoadAvg(1m)={}",
            snap.system.cpu_percent.format_with_unit(1, "%"),
            snap.system.cpu_user_percent.format_with_unit(1, "%"),
            snap.system.cpu_kernel_percent.format_with_unit(1, "%"),
            snap.system.load_avg_1m.format_with_unit(2, "")
        )),
        Line::from(format!(
            "Memory:      Used={} ({:.2} GiB / {:.2} GiB) | Pressure={}",
            snap.system.mem_used_percent.format_with_unit(1, "%"),
            metric_gib(&snap.system.mem_used_bytes),
            metric_gib(&snap.system.mem_total_bytes),
            snap.system.system_pressure_percent.format_with_unit(0, "%")
        )),
        Line::from(format!(
            "SoC Power:   Package={} | SystemPowerIn={} | GPU={} | P-Residency={} | E-Residency={}",
            sp.package_watts.format_with_unit(2, "W"),
            sp.system_power_in_watts.format_with_unit(2, "W"),
            sp.gpu_watts.format_with_unit(2, "W"),
            sp.p_cluster_active_residency_percent
                .format_with_unit(1, "%"),
            sp.e_cluster_active_residency_percent
                .format_with_unit(1, "%")
        )),
        Line::from(format!(
            "Thermals:    MaxDie={} | WarningLevel={} | Active SMC Sensors={}",
            th.max_die_celsius.format_with_unit(1, "C"),
            th.warning_level.format_with_unit(0, ""),
            th.sensors.len()
        )),
    ];
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(" Performance Telemetry ")
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn draw_processes_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let mut rows = state.app.process_table.build_rows(&state.snapshot);
    match state.sort_col {
        SortColumn::Memory => {}
        SortColumn::Cpu => rows.sort_by(|a, b| {
            let ca = metric_val(&a.snapshot.cpu_percent);
            let cb = metric_val(&b.snapshot.cpu_percent);
            cb.partial_cmp(&ca).unwrap_or(std::cmp::Ordering::Equal)
        }),
        SortColumn::Pid => rows.sort_by_key(|r| r.snapshot.key.pid),
        SortColumn::Name => rows.sort_by(|a, b| a.snapshot.name.cmp(&b.snapshot.name)),
    }

    if state.selected_row >= rows.len() && !rows.is_empty() {
        state.selected_row = rows.len() - 1;
    }

    let header = Row::new(vec![
        Cell::from("PID"),
        Cell::from("CreationToken"),
        Cell::from("Name"),
        Cell::from("Group"),
        Cell::from("Footprint"),
        Cell::from("CPU %"),
        Cell::from("State"),
    ])
    .style(Style::default().fg(accent).bold());

    let table_rows: Vec<Row<'_>> = rows
        .iter()
        .take(30)
        .map(|r| {
            let status_str = if let Some(w) = &r.exited_wash {
                format!("{} (wash)", w.status_text)
            } else if r.rank_glow_rgba.is_some() {
                "Running [RankGlow]".to_string()
            } else {
                r.snapshot.phys_footprint_bytes.state().as_str().to_string()
            };
            Row::new(vec![
                Cell::from(r.snapshot.key.pid.to_string()),
                Cell::from(r.snapshot.key.creation_token_us.to_string()),
                Cell::from(r.snapshot.name.as_str().to_string()),
                Cell::from(r.group_name.as_str().to_string()),
                Cell::from(format!(
                    "{:.1} MiB",
                    metric_mib(&r.snapshot.phys_footprint_bytes)
                )),
                Cell::from(r.snapshot.cpu_percent.format_with_unit(1, "%")),
                Cell::from(status_str),
            ])
        })
        .collect();

    let mut ts = TableState::default();
    ts.select(Some(state.selected_row));

    let table = Table::new(
        table_rows,
        [
            Constraint::Length(8),
            Constraint::Length(18),
            Constraint::Length(24),
            Constraint::Length(16),
            Constraint::Length(14),
            Constraint::Length(10),
            Constraint::Min(18),
        ],
    )
    .header(header)
    .block(
        Block::default()
            .borders(Borders::ALL)
            .title(format!(
                " Processes ({} live, {} exited | Sort: {} | Filter: {:?}) ",
                state.snapshot.processes.len(),
                state.snapshot.exited_processes.len(),
                state.sort_col.label(),
                state.search_query
            ))
            .border_style(Style::default().fg(accent)),
    )
    .row_highlight_style(Style::default().bg(Color::DarkGray).bold());

    f.render_stateful_widget(table, area, &mut ts);
}

fn draw_apple_silicon_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let sp = &state.snapshot.hardware_telemetry.soc_power;
    let th = &state.snapshot.hardware_telemetry.thermals;
    let plat = nexus_core::HostPlatform::current();
    let heading = if plat.is_macos() {
        format!(
            "Apple Silicon Die Telemetry ({}):",
            state.snapshot.system.gpu_model
        )
    } else {
        format!(
            "Processor, GPU & Thermal Telemetry ({}):",
            state.snapshot.system.gpu_model
        )
    };
    let mut lines = vec![
        Line::from(heading),
        if plat.is_macos() {
            Line::from(format!(
                "  P-Cluster Residency: {} | E-Cluster Residency: {} | ANE Residency: {}",
                sp.p_cluster_active_residency_percent
                    .format_with_unit(1, "%"),
                sp.e_cluster_active_residency_percent
                    .format_with_unit(1, "%"),
                sp.ane_active_residency_percent.format_with_unit(1, "%")
            ))
        } else {
            Line::from(format!(
                "  CPU Active: {} | GPU Active: {} | GPU Clock: {}",
                sp.p_cluster_active_residency_percent
                    .format_with_unit(1, "%"),
                state
                    .snapshot
                    .system
                    .gpu_utilization_percent
                    .format_with_unit(1, "%"),
                sp.gpu_clock_mhz.format_with_unit(0, "MHz")
            ))
        },
        if plat.is_macos() {
            Line::from(format!(
                "  Power: CPU={} | GPU={} | ANE={} | DRAM={} | Package={} | SystemIn={}",
                sp.cpu_watts.format_with_unit(2, "W"),
                sp.gpu_watts.format_with_unit(2, "W"),
                sp.ane_watts.format_with_unit(2, "W"),
                sp.dram_watts.format_with_unit(2, "W"),
                sp.package_watts.format_with_unit(2, "W"),
                sp.system_power_in_watts.format_with_unit(2, "W")
            ))
        } else {
            Line::from(format!(
                "  Power: CPU={} | GPU={} | DRAM={} | Package={} | Source={}",
                sp.cpu_watts.format_with_unit(2, "W"),
                sp.gpu_watts.format_with_unit(2, "W"),
                sp.dram_watts.format_with_unit(2, "W"),
                sp.package_watts.format_with_unit(2, "W"),
                plat.power_telemetry_badge()
            ))
        },
        Line::from(format!(
            "  {} Thermal Sensors:",
            plat.thermal_source_badge()
        )),
    ];
    for s in th.sensors.iter().take(8) {
        lines.push(Line::from(format!(
            "    - {:<22}: {}",
            s.name,
            s.celsius.format_with_unit(1, "C")
        )));
    }
    let block_title = if plat.is_macos() {
        " Apple Silicon & SMC Die Telemetry "
    } else {
        " Processor, GPU & Thermal Telemetry "
    };
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(block_title)
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn draw_services_startup_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let plat = nexus_core::HostPlatform::current();
    let svc_header = match plat {
        nexus_core::HostPlatform::MacOs => format!(
            "Services: {} launchd jobs | Startup Items: {} LaunchAgents/Daemons",
            state.cached_services.len(),
            state.cached_startup.len()
        ),
        nexus_core::HostPlatform::Windows => format!(
            "Services: {} Win32 SCM services | Startup Items: {} Startup Tasks",
            state.cached_services.len(),
            state.cached_startup.len()
        ),
        nexus_core::HostPlatform::Linux => format!(
            "Services: {} systemd units | Startup Items: {} Autostart Units",
            state.cached_services.len(),
            state.cached_startup.len()
        ),
    };
    let mut lines = vec![Line::from(svc_header)];
    lines.push(Line::from("-- Sample Services --"));
    for s in state.cached_services.iter().take(6) {
        lines.push(Line::from(format!(
            "  {:<42} [{}] ({})",
            s.name,
            s.state.as_str(),
            s.start_type.as_str()
        )));
    }
    lines.push(Line::from("-- Sample Startup Items --"));
    for item in state.cached_startup.iter().take(5) {
        lines.push(Line::from(format!(
            "  {:<42} [{}] {}",
            item.name,
            if item.is_enabled {
                "Enabled"
            } else {
                "Disabled"
            },
            item.item_type.as_str()
        )));
    }
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(" Services & Startup Apps ")
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn draw_net_radar_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let mut lines = vec![Line::from(format!(
        "Active TCP/UDP Sockets: {} | Tip: use `nexus wireless` for full RF & LAN Radar probe",
        state.cached_connections.len()
    ))];
    for c in state.cached_connections.iter().take(12) {
        lines.push(Line::from(format!(
            "  {:<4} {:>22}:{:<5} -> {:>18}:{:<5} [{:<11}] {}",
            c.protocol.as_str(),
            c.local_address,
            c.local_port,
            c.remote_address,
            c.remote_port,
            c.state.as_str(),
            c.process_name
        )));
    }
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(" Active Network Connections & Radar ")
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn draw_disk_space_pane(f: &mut Frame<'_>, area: Rect, state: &mut TuiState, accent: Color) {
    let report = &state.cached_disk_report;
    let treemap = compute_squarified_treemap(
        &report.root_node.children,
        RectBounds {
            x: 0.0,
            y: 0.0,
            width: 800.0,
            height: 600.0,
        },
    );
    let mut lines = vec![Line::from(format!(
        "Scanned {}: {} bytes across {} files | Squarified Treemap Rects: {}",
        report.root_path,
        report.total_bytes,
        report.total_files,
        treemap.len()
    ))];
    for c in &report.categories {
        lines.push(Line::from(format!(
            "  {:<20}: {:>10} B ({:>5} files, {:>5.1}%)",
            c.category.label(),
            c.bytes,
            c.file_count,
            c.share_fraction * 100.0
        )));
    }
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(" Disk Space Breakdown & Treemap ")
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn draw_honesty_and_benchmarks_pane(
    f: &mut Frame<'_>,
    area: Rect,
    state: &mut TuiState,
    accent: Color,
) {
    let shaders_ok = validate_all_embedded_shaders().is_ok();
    let skins = bundled::load_bundled_skins().unwrap_or_default();
    let events = state
        .rules_engine
        .evaluate_snapshot(&state.snapshot, now_unix_ms());
    let mut lines = vec![
        Line::from(format!(
            "FirstCoherenceFault: {:?} | Repairs: clamped={}, unavailable={}, dup_dropped={}",
            state.snapshot.first_coherence_fault(),
            state.snapshot.coherence_repairs.percentages_clamped,
            state.snapshot.coherence_repairs.marked_unavailable,
            state.snapshot.coherence_repairs.duplicate_rows_dropped
        )),
        Line::from(format!(
            "WGSL Shaders Validated (naga): {} | Bundled .nexusskin: {} | Rules Triggered: {}",
            shaders_ok,
            skins.len(),
            events.len()
        )),
        Line::from("Interned Provenance Sources:"),
    ];
    for (idx, src) in state.snapshot.sources.iter().enumerate().take(10) {
        lines.push(Line::from(format!("  [SourceId({idx})] {src}")));
    }
    let p = Paragraph::new(lines).block(
        Block::default()
            .borders(Borders::ALL)
            .title(" Honesty Engine, Rules & Shader Verification ")
            .border_style(Style::default().fg(accent)),
    );
    f.render_widget(p, area);
}

fn metric_val(m: &Metric<f64>) -> f64 {
    match m.reading {
        MetricReading::Measured { value, .. } => value,
        _ => -1.0,
    }
}

fn metric_mib(m: &Metric<f64>) -> f64 {
    match m.reading {
        MetricReading::Measured { value, .. } => value / (1024.0 * 1024.0),
        _ => 0.0,
    }
}

fn metric_gib(m: &Metric<f64>) -> f64 {
    match m.reading {
        MetricReading::Measured { value, .. } => value / (1024.0 * 1024.0 * 1024.0),
        _ => 0.0,
    }
}
