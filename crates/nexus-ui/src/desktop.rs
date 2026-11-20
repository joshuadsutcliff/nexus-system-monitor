use crate::TaskManagerApp;
use crate::assets::{UI_APP_ICON_PNG, load_standalone_fonts};
use crate::desktop_views::{
    AutomationRuntimeState, ColorWheelState, DiskSubTab, GuiView, PlatformDataCache,
    draw_configure_columns_panel, draw_connections_view, draw_disk_space_extra_tabs,
    draw_drivers_view, draw_inline_hbar, draw_inline_sparkline, draw_installed_apps_view,
    draw_multi_series_history_canvas, draw_permission_guidance_card, draw_power_and_freq_view,
    draw_services_view, draw_settings_view, draw_single_vfd_bar, draw_startup_view,
    draw_system_info_and_health_view, draw_users_view, draw_wireless_radar_view, format_bytes,
    format_metric_bytes, hig_card, hig_card_header, hig_status_pill, hig_view_header, metric_mib,
    srgb_rgba_to_color32, srgb_to_color32, trigger_wireless_scans,
};
use crate::faces::{
    ColumnDisplayStyle, CompressedHistoryGraph, DashboardTile, FaceKind, PageEditSession,
    SensorPath, SensorRegistry, TableColumnConfig, WorkspaceProfile, build_disk_shader_blobs,
    build_vector_tunnel_segments, default_process_columns,
};
use crate::ipc::{
    InstanceLockOutcome, IpcCommand, SCREENSAVER_STALENESS_LIMIT, SharedScreensaverTelemetry,
    SingleInstanceGuard, acquire_single_instance_with_telemetry, default_ipc_socket_path,
};
use crate::prefs::{
    UserPreferences, app_support_dir, clamp_text_size_percent, default_telemetry_db_path,
};
use crate::shaders::{
    GpuDiskBlob, GpuDiskUniforms, GpuTunnelUniforms, SHADER_BENCHMARK_WGSL, SHADER_DISK_SPACE_WGSL,
    SHADER_VECTOR_TUNNEL_WGSL,
};
use crate::theme::{CrystalGlassPreset, ResolvedPopoutView, ThemePresetMetadata};
use crate::views::{Destination, ProcessGroupingMode};
use compact_str::CompactString;
use egui::{
    Color32, CornerRadius, FontData, FontDefinitions, FontFamily, FontId, Key, Pos2, Rect,
    RichText, Stroke, Vec2, ViewportId,
};
use egui_wgpu::winit::Painter;
use egui_wgpu::{Callback, CallbackResources, CallbackTrait, ScreenDescriptor};
use nexus_automation::{
    ActionCoordinator, ActionRequest, AlertsService, AutoBalanceService, GamingModeService,
    IdleThrottleService, MemoryReclaimService, PRIORITY_PRESET_VALUES, PerformanceProfileService,
    ProcessActionType, ProcessRule, QuietHoursService, RulesEngine, SleepPreventionService,
    now_unix_ms,
};
use nexus_collectors_macos::MacCollector;
use nexus_collectors_macos::system_ops::{
    MacOsUncachedDiskIo, StorageInspectorEntry, collect_mounted_volumes, collect_storage_inspector,
    collect_system_hardware, sample_wallpaper_rgb8, statvfs_volume_bytes,
};
use nexus_core::{
    CadenceCoordinator, CadenceLease, DEFAULT_BENCHMARK_DURATION_SECS, DEFAULT_NEXUS_BASELINE,
    DisplayMode, DomainMask, HealthLevel, HealthScoring, LanRadarReport, LogicalProcessorClass,
    Metric, MetricQuality, MetricReading, PopoutMode, ProcessKey, RemoteServerHandle, SnapshotHub,
    SystemSnapshot, ValueState, VfdTextColor, WirelessScanReport, benchmark_ratios, nexus_score,
    run_cpu_suite, six_character_ratio,
};
use nexus_disk::{
    BenchmarkCategory, BenchmarkHistoryStore, CategoryTotal, DiskCategory, DiskNode,
    DiskScanOmissions, DiskScanReport, DiskScanUpdate, DiskSnapshotDiff, DiskSnapshotInfo,
    DiskSnapshotStore, DuplicateGroup, MAX_TRACE_BYTES, MetricsHistoryStore, NexusTracePlayer,
    NexusTraceRecorder, SnapshotDatabase, SnapshotDiffer, TreemapRect,
    default_benchmark_history_path, default_disk_snapshots_db_path, default_traces_dir,
    entry_at_normalized, run_disk_benchmark, scan_directory_streaming,
};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
#[cfg(any(target_os = "macos", target_os = "windows"))]
use tray_icon::menu::{Menu, MenuEvent, MenuId, MenuItem, PredefinedMenuItem};
#[cfg(any(target_os = "macos", target_os = "windows"))]
use tray_icon::{Icon, TrayIcon, TrayIconBuilder};
use winit::application::ApplicationHandler;
use winit::dpi::LogicalSize;
use winit::event::{StartCause, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy};
use winit::window::{Window, WindowId, WindowLevel};

#[derive(Debug, Clone)]
enum UserEvent {
    SnapshotReady,
    Ipc(IpcCommand),
    #[cfg(any(target_os = "macos", target_os = "windows"))]
    TrayMenu(MenuId),
}

#[cfg(any(target_os = "macos", target_os = "windows"))]
struct TrayMenuState {
    _tray_icon: TrayIcon,
    _menu: Menu,
    summary_item: MenuItem,
    id_open_main: MenuId,
    id_toggle_popout: MenuId,
    id_toggle_gaming: MenuId,
    id_quit: MenuId,
    last_title: String,
}

#[cfg(not(any(target_os = "macos", target_os = "windows")))]
struct TrayMenuState;

#[derive(Debug, Clone)]
struct SharedAutomationConfig {
    rules: Vec<ProcessRule>,
    autobalance_enabled: bool,
    idle_throttle_enabled: bool,
    sleep_manual_override: bool,
    quiet_hours_enabled: bool,
    quiet_hours_start: CompactString,
    quiet_hours_end: CompactString,
    alerts_cpu_threshold: f64,
    alerts_mem_threshold: f64,
    alerts_temp_threshold: f64,
    alerts_disk_threshold: f64,
    alerts_webhook_url: Option<CompactString>,
}

/// Custom `wgpu` pipeline resources stored inside `egui_wgpu::Renderer::callback_resources`.
struct CustomGpuPipelines {
    format: wgpu::TextureFormat,
    disk_pipeline: wgpu::RenderPipeline,
    disk_bind_group: wgpu::BindGroup,
    disk_blob_buf: wgpu::Buffer,
    disk_uniform_buf: wgpu::Buffer,
    tunnel_pipeline: wgpu::RenderPipeline,
    tunnel_bind_group: wgpu::BindGroup,
    tunnel_seg_buf: wgpu::Buffer,
    tunnel_uniform_buf: wgpu::Buffer,
    tunnel_seg_count: u32,
}

impl CustomGpuPipelines {
    fn new(device: &wgpu::Device, format: wgpu::TextureFormat) -> Self {
        let disk_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("nexus_disk_space_wgsl"),
            source: wgpu::ShaderSource::Wgsl(SHADER_DISK_SPACE_WGSL.into()),
        });
        let disk_bind_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("nexus_disk_bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Storage { read_only: true },
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
            ],
        });
        let disk_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("nexus_disk_pl"),
            bind_group_layouts: &[Some(&disk_bind_layout)],
            immediate_size: 0,
        });
        let disk_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("nexus_disk_rp"),
            layout: Some(&disk_layout),
            vertex: wgpu::VertexState {
                module: &disk_shader,
                entry_point: Some("diskVertex"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            fragment: Some(wgpu::FragmentState {
                module: &disk_shader,
                entry_point: Some("diskFragment"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });
        let disk_blob_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("nexus_disk_blobs"),
            size: (48 * 16) as u64,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let disk_uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("nexus_disk_uniforms"),
            size: 80,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let disk_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("nexus_disk_bg"),
            layout: &disk_bind_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: disk_blob_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: disk_uniform_buf.as_entire_binding(),
                },
            ],
        });

        let tunnel_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("nexus_vector_tunnel_wgsl"),
            source: wgpu::ShaderSource::Wgsl(SHADER_VECTOR_TUNNEL_WGSL.into()),
        });
        let tunnel_bind_layout =
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("nexus_tunnel_bgl"),
                entries: &[
                    wgpu::BindGroupLayoutEntry {
                        binding: 0,
                        visibility: wgpu::ShaderStages::VERTEX,
                        ty: wgpu::BindingType::Buffer {
                            ty: wgpu::BufferBindingType::Storage { read_only: true },
                            has_dynamic_offset: false,
                            min_binding_size: None,
                        },
                        count: None,
                    },
                    wgpu::BindGroupLayoutEntry {
                        binding: 1,
                        visibility: wgpu::ShaderStages::VERTEX,
                        ty: wgpu::BindingType::Buffer {
                            ty: wgpu::BufferBindingType::Uniform,
                            has_dynamic_offset: false,
                            min_binding_size: None,
                        },
                        count: None,
                    },
                ],
            });
        let tunnel_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("nexus_tunnel_pl"),
            bind_group_layouts: &[Some(&tunnel_bind_layout)],
            immediate_size: 0,
        });
        let tunnel_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("nexus_tunnel_rp"),
            layout: Some(&tunnel_layout),
            vertex: wgpu::VertexState {
                module: &tunnel_shader,
                entry_point: Some("tunnelVertex"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            fragment: Some(wgpu::FragmentState {
                module: &tunnel_shader,
                entry_point: Some("tunnelFragment"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });
        let tunnel_seg_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("nexus_tunnel_segs"),
            size: (48 * 256) as u64,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let tunnel_uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("nexus_tunnel_uniforms"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let tunnel_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("nexus_tunnel_bg"),
            layout: &tunnel_bind_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: tunnel_seg_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: tunnel_uniform_buf.as_entire_binding(),
                },
            ],
        });

        Self {
            format,
            disk_pipeline,
            disk_bind_group,
            disk_blob_buf,
            disk_uniform_buf,
            tunnel_pipeline,
            tunnel_bind_group,
            tunnel_seg_buf,
            tunnel_uniform_buf,
            tunnel_seg_count: 144,
        }
    }
}

#[derive(Clone)]
enum ShaderPassKind {
    DiskSpace {
        blobs: Vec<GpuDiskBlob>,
        uniforms: GpuDiskUniforms,
    },
    VectorTunnel {
        time_sec: f32,
        viewport: [f32; 2],
        edr_headroom: f32,
    },
}

struct WgslPaintCallback {
    format: wgpu::TextureFormat,
    pass: ShaderPassKind,
}

impl CallbackTrait for WgslPaintCallback {
    fn prepare(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        _screen_descriptor: &ScreenDescriptor,
        _egui_encoder: &mut wgpu::CommandEncoder,
        resources: &mut CallbackResources,
    ) -> Vec<wgpu::CommandBuffer> {
        let needs_init = match resources.get::<CustomGpuPipelines>() {
            Some(p) => p.format != self.format,
            None => true,
        };
        if needs_init {
            resources.insert(CustomGpuPipelines::new(device, self.format));
        }
        let Some(pipes) = resources.get_mut::<CustomGpuPipelines>() else {
            return Vec::new();
        };

        match &self.pass {
            ShaderPassKind::DiskSpace { blobs, uniforms } => {
                let mut raw_blobs = Vec::with_capacity(blobs.len() * 48);
                for b in blobs.iter().take(16) {
                    raw_blobs.extend_from_slice(&b.to_le_bytes());
                }
                if !raw_blobs.is_empty() {
                    queue.write_buffer(&pipes.disk_blob_buf, 0, &raw_blobs);
                }
                queue.write_buffer(&pipes.disk_uniform_buf, 0, &uniforms.to_le_bytes());
            }
            ShaderPassKind::VectorTunnel {
                time_sec,
                viewport,
                edr_headroom,
            } => {
                let segs = build_vector_tunnel_segments(viewport[0], viewport[1], *time_sec, 12);
                pipes.tunnel_seg_count = (segs.len().min(256)) as u32;
                let mut raw_segs = Vec::with_capacity(segs.len() * 48);
                for s in segs.iter().take(256) {
                    raw_segs.extend_from_slice(&s.to_le_bytes());
                }
                if !raw_segs.is_empty() {
                    queue.write_buffer(&pipes.tunnel_seg_buf, 0, &raw_segs);
                }
                let u = GpuTunnelUniforms {
                    viewport: *viewport,
                    dynamic_range: [*edr_headroom, if *edr_headroom > 1.0 { 1.0 } else { 0.0 }],
                };
                queue.write_buffer(&pipes.tunnel_uniform_buf, 0, &u.to_le_bytes());
            }
        }
        Vec::new()
    }

    fn paint(
        &self,
        _info: egui::PaintCallbackInfo,
        render_pass: &mut wgpu::RenderPass<'static>,
        callback_resources: &CallbackResources,
    ) {
        let Some(pipes) = callback_resources.get::<CustomGpuPipelines>() else {
            return;
        };
        match &self.pass {
            ShaderPassKind::DiskSpace { .. } => {
                render_pass.set_pipeline(&pipes.disk_pipeline);
                render_pass.set_bind_group(0, &pipes.disk_bind_group, &[]);
                render_pass.draw(0..3, 0..1);
            }
            ShaderPassKind::VectorTunnel { .. } => {
                render_pass.set_pipeline(&pipes.tunnel_pipeline);
                render_pass.set_bind_group(0, &pipes.tunnel_bind_group, &[]);
                render_pass.draw(0..6, 0..pipes.tunnel_seg_count);
            }
        }
    }
}

struct BenchmarkUiState {
    running: bool,
    active_suite: String,
    progress: f32,
    status: String,
    cpu_single_mops: f64,
    cpu_multi_mops: f64,
    gpu_compute_gflops: f64,
    gpu_graphics_gflops: f64,
    disk_read_mbps: f64,
    disk_write_mbps: f64,
    net_down_mbps: f64,
    net_up_mbps: f64,
    history: BenchmarkHistoryStore,
}

impl BenchmarkUiState {
    fn load() -> Self {
        let history = BenchmarkHistoryStore::load(&default_benchmark_history_path());
        let (cpu_l, cpu_r) = history
            .latest(BenchmarkCategory::CPU)
            .map(|e| (e.left, e.right))
            .unwrap_or((0.0, 0.0));
        let (gpu_l, gpu_r) = history
            .latest(BenchmarkCategory::GPU)
            .map(|e| (e.left, e.right))
            .unwrap_or((0.0, 0.0));
        let (disk_l, disk_r) = history
            .latest(BenchmarkCategory::Disk)
            .map(|e| (e.left, e.right))
            .unwrap_or((0.0, 0.0));
        let (net_l, net_r) = history
            .latest(BenchmarkCategory::Internet)
            .map(|e| (e.left, e.right))
            .unwrap_or((0.0, 0.0));
        Self {
            running: false,
            active_suite: "Idle".to_string(),
            progress: 0.0,
            status: "Ready (5.0s per pass)".to_string(),
            cpu_single_mops: cpu_l,
            cpu_multi_mops: cpu_r,
            gpu_compute_gflops: gpu_l,
            gpu_graphics_gflops: gpu_r,
            disk_read_mbps: disk_l,
            disk_write_mbps: disk_r,
            net_down_mbps: net_l,
            net_up_mbps: net_r,
            history,
        }
    }
}

#[derive(Debug, Clone, Default)]
struct LaunchHarnessOptions {
    preset_query: Option<String>,
    popout_skin_id: Option<String>,
    text_scale: Option<u16>,
    screensaver: bool,
    test_edit_drawer: bool,
    test_configure_columns: bool,
}

impl LaunchHarnessOptions {
    fn is_test_mode(&self) -> bool {
        self.preset_query.is_some()
            || self.popout_skin_id.is_some()
            || self.text_scale.is_some()
            || self.screensaver
            || self.test_edit_drawer
            || self.test_configure_columns
    }
}

struct GuiApp {
    window: Option<Arc<Window>>,
    window_visible: bool,
    egui_ctx: egui::Context,
    egui_state: Option<egui_winit::State>,
    painter: Painter,
    target_format: wgpu::TextureFormat,
    tokio_rt: tokio::runtime::Runtime,

    tray: Option<TrayMenuState>,
    _ipc_guard: Option<SingleInstanceGuard>,
    prefs_path: PathBuf,
    preferred_interval_ms: u64,
    show_popout_overlay: bool,
    popout_hovered: bool,
    popout_window: Option<Arc<Window>>,
    popout_egui_ctx: egui::Context,
    popout_egui_state: Option<egui_winit::State>,
    popout_painter: Option<Painter>,
    popout_pos_x: Option<i32>,
    popout_pos_y: Option<i32>,
    popout_last_skin_size: Option<(u32, u32)>,
    popout_pos_dirty_at: Option<Instant>,
    popout_frame_count: u64,
    popout_screenshot_mode: bool,
    last_tint_mode: DisplayMode,
    new_rule_pattern: String,
    new_rule_cpu_threshold: f64,

    app: TaskManagerApp,
    active_view: GuiView,
    snapshot: Arc<SystemSnapshot>,
    ui_inbox: tokio::sync::watch::Receiver<Arc<SystemSnapshot>>,
    _cadence_lease: CadenceLease,
    cadence_coord: CadenceCoordinator,

    // Multi-Series Performance Telemetry History
    perf_cpu_user_history: CompressedHistoryGraph,
    perf_cpu_sys_history: CompressedHistoryGraph,
    perf_mem_history: CompressedHistoryGraph,
    perf_gpu_history: CompressedHistoryGraph,
    perf_pkg_watts_history: CompressedHistoryGraph,
    perf_disk_read_history: CompressedHistoryGraph,
    perf_disk_write_history: CompressedHistoryGraph,
    perf_net_down_history: CompressedHistoryGraph,
    perf_net_up_history: CompressedHistoryGraph,
    perf_core_sparklines: Vec<std::collections::VecDeque<f64>>,
    dashboard_mode: u8,

    // Composable Dashboard Edit Mode & Tile Histories
    page_edit: PageEditSession,
    tile_histories: HashMap<CompactString, CompressedHistoryGraph>,
    sensor_histories: HashMap<CompactString, CompressedHistoryGraph>,
    add_tile_title: String,
    add_tile_face_idx: usize,
    add_tile_sensor_idx: usize,
    editing_tile_id: Option<CompactString>,
    picker_category: String,
    picker_instance: String,
    picker_search: String,
    picker_bind_target: usize,
    color_wheel: ColorWheelState,
    saved_custom_presets: Vec<ThemePresetMetadata>,

    // Modals & Overlays
    command_palette_open: bool,
    command_palette_query: String,
    screensaver_active: bool,
    screensaver_last_pointer_activity: Instant,
    last_snapshot_received_at: Instant,
    shared_telemetry: Arc<parking_lot::Mutex<SharedScreensaverTelemetry>>,

    // Companion Remote Server
    remote_server: Option<RemoteServerHandle>,
    remote_allow_cgnat: bool,
    remote_advertise_bonjour: bool,

    // Flight Recorder & DB
    flight_recorder: Vec<Arc<SystemSnapshot>>,
    scrub_index: Option<usize>,
    snapshot_db: SnapshotDatabase,
    baseline_snapshot: Option<Arc<SystemSnapshot>>,
    last_diff_summary: String,
    trace_recorder: NexusTraceRecorder,
    trace_player: Option<NexusTracePlayer>,
    trace_path_input: String,
    trace_saved_files: Vec<PathBuf>,
    shutdown_done: bool,

    // Automation & Services
    gaming_mode: GamingModeService,
    rules_engine: RulesEngine,
    autobalance: AutoBalanceService,
    idle_throttle: IdleThrottleService,
    quiet_hours: QuietHoursService,
    perf_profiles: PerformanceProfileService,
    mem_reclaim: MemoryReclaimService,
    action_coord: ActionCoordinator,
    action_status: String,
    sleep_preventing: bool,
    alerts_cpu_thresh: f64,
    alerts_mem_thresh: f64,
    alerts_temp_thresh: f64,
    alerts_disk_thresh: f64,
    alerts_webhook_buf: String,
    preset_json_buf: String,
    auto_config_shared: Arc<Mutex<SharedAutomationConfig>>,
    auto_runtime_shared: Arc<Mutex<AutomationRuntimeState>>,

    // Async Platform & Wireless Caches
    platform_cache: PlatformDataCache,
    wifi_async_slot: Arc<Mutex<Option<WirelessScanReport>>>,
    lan_async_slot: Arc<Mutex<Option<LanRadarReport>>>,
    wallpaper_rgb_slot: Arc<Mutex<Option<Vec<u8>>>>,

    // Disk Space Subviews
    disk_subtab: DiskSubTab,
    disk_report: DiskScanReport,
    disk_blobs: Vec<GpuDiskBlob>,
    disk_shader_mode: i32,
    disk_patterned: bool,
    disk_scan_rx: Option<std::sync::mpsc::Receiver<DiskScanUpdate>>,
    disk_scan_cancel: Arc<AtomicBool>,
    disk_scanning: bool,
    disk_scan_progress: f32,
    disk_scan_completion: f32,
    disk_scan_completed_at: Option<Instant>,
    disk_water_level: f32,
    disk_omissions: DiskScanOmissions,
    disk_selected_category: Option<DiskCategory>,
    disk_hovered_category: Option<DiskCategory>,
    disk_scan_path_input: String,
    storage_inspector: Vec<StorageInspectorEntry>,
    storage_inspector_slot: Arc<Mutex<Option<Vec<StorageInspectorEntry>>>>,
    storage_inspector_loading: bool,
    treemap_rects: Vec<TreemapRect>,
    treemap_last_size: Option<Vec2>,
    dup_groups_slot: Arc<Mutex<Vec<DuplicateGroup>>>,
    dup_scanning: bool,
    disk_store: DiskSnapshotStore,
    disk_snapshots: Vec<DiskSnapshotInfo>,
    disk_diff: Option<DiskSnapshotDiff>,
    edr_headroom: f32,
    bench_state: Arc<Mutex<BenchmarkUiState>>,
    bench_cancel: Arc<AtomicBool>,

    start_instant: Instant,
    last_frame_instant: Instant,
    frame_count: u64,
    screenshot_out: Option<PathBuf>,
    screenshot_saved: bool,
    auto_exit_ms: Option<u64>,
    skip_save_prefs: bool,
}

impl GuiApp {
    #[allow(clippy::too_many_arguments)]
    fn new(
        proxy: EventLoopProxy<UserEvent>,
        ipc_guard: Option<SingleInstanceGuard>,
        shared_telemetry: Arc<parking_lot::Mutex<SharedScreensaverTelemetry>>,
        screenshot_out: Option<PathBuf>,
        auto_exit_ms: Option<u64>,
        initial_view: GuiView,
        initial_disk_mode: i32,
        harness: LaunchHarnessOptions,
    ) -> Self {
        let tokio_rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("tokio current_thread runtime");

        let prefs_path = UserPreferences::default_path();
        let mut saved_prefs = UserPreferences::load_or_default(&prefs_path);
        let preferred_interval_ms = saved_prefs.update_interval_ms.clamp(100, 10_000);

        let collector = Arc::new(MacCollector::new());
        let _ = collector.collect();
        std::thread::sleep(Duration::from_millis(120));
        let initial_snap = Arc::new(collector.collect());

        let cadence_coord = CadenceCoordinator::new();
        let cadence_lease = cadence_coord.acquire_lease(
            Duration::from_millis(preferred_interval_ms),
            DomainMask::CPU_MEM_GPU
                | DomainMask::SOC_POWER
                | DomainMask::THERMALS
                | DomainMask::DISK_NET_IO
                | DomainMask::PROCESSES,
        );
        let (publisher, hub) = SnapshotHub::new(Arc::clone(&initial_snap));
        let mut sched_rx = cadence_coord.subscribe_schedule();

        // 1. Collector Loop Thread
        let bg_collector = Arc::clone(&collector);
        let col_proxy = proxy.clone();
        std::thread::spawn(move || {
            loop {
                let (opt_interval, _domains) = *sched_rx.borrow_and_update();
                let sleep_dur = opt_interval.unwrap_or(Duration::from_millis(1000));
                std::thread::sleep(sleep_dur.max(Duration::from_millis(100)));
                let next_snap = Arc::new(bg_collector.collect());
                publisher.publish(next_snap);
                let _ = col_proxy.send_event(UserEvent::SnapshotReady);
            }
        });

        // 2. Shared Automation State & Rules Engine
        let mut rules_engine = RulesEngine::new();
        for rule in &saved_prefs.rules {
            rules_engine.add_rule(rule.clone());
        }
        let auto_config_shared = Arc::new(Mutex::new(SharedAutomationConfig {
            rules: saved_prefs.rules.clone(),
            autobalance_enabled: saved_prefs.autobalance_enabled,
            idle_throttle_enabled: saved_prefs.idle_throttle_enabled,
            sleep_manual_override: saved_prefs.sleep_prevention_enabled,
            quiet_hours_enabled: saved_prefs.quiet_hours_enabled,
            quiet_hours_start: saved_prefs.quiet_hours_start.clone(),
            quiet_hours_end: saved_prefs.quiet_hours_end.clone(),
            alerts_cpu_threshold: saved_prefs.alerts_cpu_threshold,
            alerts_mem_threshold: saved_prefs.alerts_mem_threshold,
            alerts_temp_threshold: saved_prefs.alerts_temp_threshold,
            alerts_disk_threshold: saved_prefs.alerts_disk_threshold,
            alerts_webhook_url: saved_prefs.alerts_webhook_url.clone(),
        }));
        let auto_runtime_shared = Arc::new(Mutex::new(AutomationRuntimeState::default()));

        // Wire `SnapshotHub::storage_rx` -> rolling `telemetry.db` SQLite history store
        let mut storage_rx = hub.storage_rx;
        let st_runtime = Arc::clone(&auto_runtime_shared);
        std::thread::spawn(move || {
            let store = MetricsHistoryStore::open_path(&default_telemetry_db_path())
                .or_else(|_| MetricsHistoryStore::open_in_memory())
                .ok();
            let rt = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .ok();
            if let (Some(db), Some(rt)) = (store, rt) {
                rt.block_on(async move {
                    while let Some(snap) = storage_rx.recv().await {
                        let _ = db.insert_snapshot(&snap, now_unix_ms());
                        let _ = db.prune_to_max_rows(20_000);
                        if let Ok(cnt) = db.row_count() {
                            st_runtime
                                .lock()
                                .unwrap_or_else(|e| e.into_inner())
                                .storage_rows_written = cnt;
                        }
                    }
                });
            }
        });

        // Wire `SnapshotHub::automation_rx` -> `RulesEngine`, `AutoBalance`, `IdleThrottle`,
        // `SleepPreventionService` (`caffeinate -di`), `QuietHoursService`, and `AlertsService`
        let mut auto_rx = hub.automation_rx;
        let cfg_bg = Arc::clone(&auto_config_shared);
        let rt_bg = Arc::clone(&auto_runtime_shared);
        let is_headless_screenshot = screenshot_out.is_some();
        std::thread::spawn(move || {
            let mut r_engine = RulesEngine::new();
            let mut ab = AutoBalanceService::default();
            let mut idle = IdleThrottleService::default();
            let mut sleep_svc = SleepPreventionService::new(is_headless_screenshot);
            let mut qh = QuietHoursService::default();
            let mut alerts = AlertsService::default();

            let rt = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .ok();
            if let Some(rt) = rt {
                rt.block_on(async move {
                    while let Some(snap) = auto_rx.recv().await {
                        let cfg = cfg_bg.lock().unwrap_or_else(|e| e.into_inner()).clone();
                        r_engine.rules = cfg.rules;
                        ab.enabled = cfg.autobalance_enabled;
                        idle.enabled = cfg.idle_throttle_enabled;
                        qh.enabled = cfg.quiet_hours_enabled;
                        qh.start_hhmm = cfg.quiet_hours_start;
                        qh.end_hhmm = cfg.quiet_hours_end;
                        alerts.cpu_alert_threshold = cfg.alerts_cpu_threshold;
                        alerts.mem_alert_threshold = cfg.alerts_mem_threshold;
                        alerts.temp_celsius_threshold = cfg.alerts_temp_threshold;
                        alerts.disk_alert_threshold = cfg.alerts_disk_threshold;
                        alerts.webhook_url = cfg.alerts_webhook_url;

                        let now_ms = now_unix_ms();
                        let qh_active = qh.evaluate_now();
                        let triggers = r_engine.evaluate_with_watchdogs(&snap, now_ms);
                        let ab_out = ab.evaluate_cycle(&snap, None, now_ms);
                        let _idle_out = idle.evaluate(&snap, None, now_ms);
                        sleep_svc.manual_override = cfg.sleep_manual_override;
                        let sleep_on = sleep_svc.evaluate(&snap, &r_engine.rules);
                        let fired = alerts.evaluate_with_state(&snap, now_ms, qh_active);
                        for al in &fired {
                            if !al.suppressed_by_quiet_hours && al.webhook_url.is_some() {
                                let _ = AlertsService::deliver_webhook(al);
                            }
                        }
                        let mut st = rt_bg.lock().unwrap_or_else(|e| e.into_inner());
                        st.active_rule_triggers = triggers;
                        st.recent_alerts = alerts.history.clone();
                        st.quiet_hours_active = qh_active;
                        st.sleep_preventing = sleep_on;
                        st.autobalance_throttled_count =
                            ab.active_throttles.len() + ab_out.throttled.len();
                        st.idle_throttled_count = idle.throttled_count();
                    }
                    drop(sleep_svc);
                });
            }
        });

        // 3. Non-blocking Desktop Wallpaper Luminance Thread (BT.601)
        let wallpaper_rgb_slot: Arc<Mutex<Option<Vec<u8>>>> = Arc::new(Mutex::new(None));
        if screenshot_out.is_none() {
            let wp_slot = Arc::clone(&wallpaper_rgb_slot);
            std::thread::spawn(move || {
                loop {
                    if let Some(rgb8) = sample_wallpaper_rgb8() {
                        *wp_slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(rgb8);
                    }
                    std::thread::sleep(Duration::from_secs(30));
                }
            });
        }

        let egui_ctx = egui::Context::default();
        configure_bundled_fonts(&egui_ctx);
        let popout_egui_ctx = egui::Context::default();
        configure_bundled_fonts(&popout_egui_ctx);

        let painter = tokio_rt.block_on(Painter::new(
            egui_ctx.clone(),
            egui_wgpu::WgpuConfiguration::default(),
            false,
            egui_wgpu::RendererOptions::default(),
        ));

        let mut app = TaskManagerApp::new();
        if let Some(wp) = saved_prefs.workspace_profile.take() {
            app.page_engine = wp.page;
        } else {
            let profile_path = app_support_dir().join("workspace_profile.json");
            if let Ok(wp) = WorkspaceProfile::load_from(&profile_path) {
                app.page_engine = wp.page;
            }
        }
        saved_prefs.apply_to_theme(&mut app.theme);
        if let Some(ref q) = harness.preset_query {
            let ql = q.trim().to_ascii_lowercase();
            if let Some(matched) = CrystalGlassPreset::ALL.into_iter().find(|p| {
                let m = p.metadata();
                m.id.eq_ignore_ascii_case(&ql) || m.name.eq_ignore_ascii_case(&ql)
            }) {
                app.theme.glass_preset = matched;
                app.theme.custom_preset = None;
            }
        }
        let mut popout_forced = false;
        if let Some(ref skin_id) = harness.popout_skin_id {
            app.theme.active_skin_id =
                CompactString::from(nexus_core::canonical_popout_skin_id(skin_id));
            app.theme.popout_mode = PopoutMode::Normal;
            popout_forced = true;
        }
        if let Some(pct) = harness.text_scale {
            app.theme.text_size_percent = clamp_text_size_percent(pct);
        }
        let active_meta = app.theme.active_preset_metadata();
        egui_ctx.set_zoom_factor(
            (f32::from(app.theme.text_size_percent) / 100.0)
                * active_meta.font_size_multiplier.clamp(0.75, 2.0),
        );

        let default_scan_dir = Path::new("/Users/josh/Github/nexus-system-monitor-rs");
        let fallback_dir = std::env::current_dir().unwrap_or_else(|_| {
            if cfg!(windows) {
                PathBuf::from("C:\\")
            } else {
                PathBuf::from("/")
            }
        });
        let scan_target = if default_scan_dir.exists() {
            default_scan_dir.to_path_buf()
        } else {
            fallback_dir
        };
        let disk_report = empty_disk_report(&scan_target);
        let disk_blobs = build_disk_shader_blobs(&disk_report);
        let (vol_total, _vol_avail, vol_used) =
            statvfs_volume_bytes(&scan_target).unwrap_or((1, 0, 0));
        let disk_water_level = if vol_total > 0 {
            (vol_used as f32 / vol_total as f32).clamp(0.0, 1.0)
        } else {
            0.0
        };

        let snapshot_db = SnapshotDatabase::open_in_memory().expect("in-memory SnapshotDatabase");
        let _ = snapshot_db.save_snapshot("Initial Launch Baseline", &initial_snap);

        let disk_store = DiskSnapshotStore::open_path(&default_disk_snapshots_db_path())
            .or_else(|_| DiskSnapshotStore::open_in_memory())
            .expect("disk snapshot store");
        let disk_snapshots = disk_store.list_disk_snapshots(None).unwrap_or_default();

        // Startup crash-journal recovery check (Gaming Mode)
        let journal_path = app_support_dir().join("gaming-mode-journal.json");
        let action_coord = ActionCoordinator::new();
        let initial_status = match GamingModeService::recover_from_crash_journal(
            &journal_path,
            &initial_snap,
            Some(&action_coord),
            screenshot_out.is_some(),
        ) {
            Ok(rec) if rec.total_in_journal > 0 => format!(
                "Crash journal recovered {} valid receipt(s), discarded {} stale PID(s).",
                rec.valid_restores.len(),
                rec.discarded_stale_count
            ),
            _ => "Ready. Double-verified ProcessKey & Single-Instance Lock active.".to_string(),
        };

        let mut platform_cache = PlatformDataCache::new();
        platform_cache.process_columns = saved_prefs.process_columns.clone();
        platform_cache.app_columns = saved_prefs.app_columns.clone();
        if harness.test_configure_columns {
            platform_cache.process_columns_open = true;
            platform_cache.app_columns_open = true;
        }
        platform_cache.hardware = Some(collect_system_hardware());
        platform_cache.ensure_loaded_sync_for_view(initial_view);
        if initial_view == GuiView::Dest(Destination::DiskSpace) {
            platform_cache.volumes = collect_mounted_volumes();
        }

        let wifi_async_slot: Arc<Mutex<Option<WirelessScanReport>>> = Arc::new(Mutex::new(None));
        let lan_async_slot: Arc<Mutex<Option<LanRadarReport>>> = Arc::new(Mutex::new(None));
        if initial_view == GuiView::WirelessRadar {
            trigger_wireless_scans(
                Arc::clone(&wifi_async_slot),
                Arc::clone(&lan_async_slot),
                &mut platform_cache,
            );
        }

        let preset_json_buf = app.theme.export_active_preset_json().unwrap_or_default();
        let mut color_wheel = ColorWheelState::new();
        color_wheel.sync_from_meta(&active_meta);
        let saved_custom_presets = saved_prefs.saved_custom_presets.clone();
        let mut page_edit = PageEditSession::new(app.page_engine.clone());
        let mut editing_tile_id = None;
        if harness.test_edit_drawer {
            page_edit.begin(app.page_engine.clone());
            editing_tile_id = page_edit.current.tiles.first().map(|t| t.id.clone());
        }
        let skip_save_prefs = screenshot_out.is_some() || harness.is_test_mode();
        let screensaver_active = harness.screensaver;

        let mut idle_throttle = IdleThrottleService::default();
        idle_throttle.enabled = saved_prefs.idle_throttle_enabled;
        let mut quiet_hours = QuietHoursService::default();
        quiet_hours.enabled = saved_prefs.quiet_hours_enabled;
        quiet_hours.start_hhmm = saved_prefs.quiet_hours_start;
        quiet_hours.end_hhmm = saved_prefs.quiet_hours_end;

        shared_telemetry.lock().update_from_snapshot(&initial_snap);

        let popout_screenshot_mode = popout_forced && screenshot_out.is_some();
        let last_tint_mode = match app.theme.display_mode {
            DisplayMode::Color | DisplayMode::Mono => DisplayMode::Green,
            other => other,
        };
        let dashboard_mode = if harness.test_edit_drawer { 2 } else { 1 };

        let mut app_obj = Self {
            window: None,
            window_visible: true,
            egui_ctx,
            egui_state: None,
            painter,
            target_format: wgpu::TextureFormat::Bgra8Unorm,
            tokio_rt,
            tray: None,
            _ipc_guard: ipc_guard,
            prefs_path,
            preferred_interval_ms,
            show_popout_overlay: popout_forced || saved_prefs.popout_mode != PopoutMode::Off,
            popout_hovered: false,
            popout_window: None,
            popout_egui_ctx,
            popout_egui_state: None,
            popout_painter: None,
            popout_pos_x: saved_prefs.popout_pos_x,
            popout_pos_y: saved_prefs.popout_pos_y,
            popout_last_skin_size: None,
            popout_pos_dirty_at: None,
            popout_frame_count: 0,
            popout_screenshot_mode,
            last_tint_mode,
            new_rule_pattern: "*Helper*".to_string(),
            new_rule_cpu_threshold: 5.0,
            app,
            active_view: initial_view,
            snapshot: Arc::clone(&initial_snap),
            ui_inbox: hub.ui_inbox,
            _cadence_lease: cadence_lease,
            cadence_coord,
            perf_cpu_user_history: CompressedHistoryGraph::new(256),
            perf_cpu_sys_history: CompressedHistoryGraph::new(256),
            perf_mem_history: CompressedHistoryGraph::new(256),
            perf_gpu_history: CompressedHistoryGraph::new(256),
            perf_pkg_watts_history: CompressedHistoryGraph::new(256),
            perf_disk_read_history: CompressedHistoryGraph::new(256),
            perf_disk_write_history: CompressedHistoryGraph::new(256),
            perf_net_down_history: CompressedHistoryGraph::new(256),
            perf_net_up_history: CompressedHistoryGraph::new(256),
            perf_core_sparklines: Vec::new(),
            dashboard_mode,
            page_edit,
            tile_histories: HashMap::new(),
            sensor_histories: HashMap::new(),
            add_tile_title: "Custom Sensor".to_string(),
            add_tile_face_idx: 0,
            add_tile_sensor_idx: 0,
            editing_tile_id,
            picker_category: "cpu".to_string(),
            picker_instance: "total".to_string(),
            picker_search: String::new(),
            picker_bind_target: 0,
            color_wheel,
            saved_custom_presets,
            command_palette_open: false,
            command_palette_query: String::new(),
            screensaver_active,
            screensaver_last_pointer_activity: Instant::now(),
            last_snapshot_received_at: Instant::now(),
            shared_telemetry,
            remote_server: None,
            remote_allow_cgnat: false,
            remote_advertise_bonjour: true,
            flight_recorder: vec![initial_snap.clone()],
            scrub_index: None,
            snapshot_db,
            baseline_snapshot: Some(initial_snap),
            last_diff_summary:
                "Baseline captured at launch. Click 'Capture & Diff Now' to compare.".to_string(),
            trace_recorder: NexusTraceRecorder::new(),
            trace_player: None,
            trace_path_input: String::new(),
            trace_saved_files: list_trace_files(&default_traces_dir()),
            shutdown_done: false,
            gaming_mode: GamingModeService::new(journal_path),
            rules_engine,
            autobalance: AutoBalanceService {
                enabled: saved_prefs.autobalance_enabled,
                ..Default::default()
            },
            idle_throttle,
            quiet_hours,
            perf_profiles: PerformanceProfileService::with_defaults(),
            mem_reclaim: MemoryReclaimService::default(),
            action_coord,
            action_status: initial_status,
            sleep_preventing: saved_prefs.sleep_prevention_enabled,
            alerts_cpu_thresh: saved_prefs.alerts_cpu_threshold,
            alerts_mem_thresh: saved_prefs.alerts_mem_threshold,
            alerts_temp_thresh: saved_prefs.alerts_temp_threshold,
            alerts_disk_thresh: saved_prefs.alerts_disk_threshold,
            alerts_webhook_buf: saved_prefs
                .alerts_webhook_url
                .map(|s| s.to_string())
                .unwrap_or_default(),
            preset_json_buf,
            auto_config_shared,
            auto_runtime_shared,
            platform_cache,
            wifi_async_slot,
            lan_async_slot,
            wallpaper_rgb_slot,
            disk_subtab: DiskSubTab::Visualizer,
            disk_report,
            disk_blobs,
            disk_shader_mode: initial_disk_mode,
            disk_patterned: false,
            disk_scan_rx: None,
            disk_scan_cancel: Arc::new(AtomicBool::new(false)),
            disk_scanning: false,
            disk_scan_progress: 0.0,
            disk_scan_completion: 0.0,
            disk_scan_completed_at: None,
            disk_water_level,
            disk_omissions: DiskScanOmissions::default(),
            disk_selected_category: None,
            disk_hovered_category: None,
            disk_scan_path_input: scan_target.display().to_string(),
            storage_inspector: Vec::new(),
            storage_inspector_slot: Arc::new(Mutex::new(None)),
            storage_inspector_loading: false,
            treemap_rects: Vec::new(),
            treemap_last_size: None,
            dup_groups_slot: Arc::new(Mutex::new(Vec::new())),
            dup_scanning: false,
            disk_store,
            disk_snapshots,
            disk_diff: None,
            edr_headroom: 1.5,
            bench_state: Arc::new(Mutex::new(BenchmarkUiState::load())),
            bench_cancel: Arc::new(AtomicBool::new(false)),
            start_instant: Instant::now(),
            last_frame_instant: Instant::now(),
            frame_count: 0,
            screenshot_out,
            screenshot_saved: false,
            auto_exit_ms,
            skip_save_prefs,
        };
        app_obj.record_all_histories_from_snapshot(&Arc::clone(&app_obj.snapshot), true);
        app_obj.update_live_process_and_health_caches(&Arc::clone(&app_obj.snapshot));
        app_obj.start_disk_scan(scan_target);
        app_obj
    }

    fn start_disk_scan(&mut self, root: PathBuf) {
        self.disk_scan_cancel.store(true, AtomicOrdering::Relaxed);
        let cancel = Arc::new(AtomicBool::new(false));
        self.disk_scan_cancel = Arc::clone(&cancel);
        self.disk_scanning = true;
        self.disk_scan_progress = 0.0;
        self.disk_scan_completion = 0.0;
        self.disk_scan_completed_at = None;
        self.disk_omissions = DiskScanOmissions::default();
        self.disk_selected_category = None;
        self.treemap_rects.clear();
        self.treemap_last_size = None;

        let (vol_total, _avail, vol_used) = statvfs_volume_bytes(&root).unwrap_or((1, 0, 0));
        self.disk_water_level = if vol_total > 0 {
            (vol_used as f32 / vol_total as f32).clamp(0.0, 1.0)
        } else {
            0.0
        };

        let (tx, rx) = std::sync::mpsc::channel();
        self.disk_scan_rx = Some(rx);
        std::thread::spawn(move || {
            let _ = scan_directory_streaming(&root, 5, vol_used, vol_total, tx, &cancel);
        });
    }

    fn poll_disk_scan_updates(&mut self) {
        if let Some(insp) = self
            .storage_inspector_slot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            self.storage_inspector = insp;
            self.storage_inspector_loading = false;
        }

        let mut finished = false;
        if let Some(rx) = &self.disk_scan_rx {
            while let Ok(upd) = rx.try_recv() {
                self.disk_omissions = upd.omissions.clone();
                if upd.volume_total_bytes > 0 {
                    self.disk_water_level = (upd.volume_used_bytes as f32
                        / upd.volume_total_bytes as f32)
                        .clamp(0.0, 1.0);
                }
                let denom = upd.volume_used_bytes.max(upd.scanned_bytes).max(1);
                self.disk_scan_progress = if upd.is_complete {
                    1.0
                } else {
                    (upd.scanned_bytes as f32 / denom as f32).clamp(0.02, 0.98)
                };

                if let Some(report) = upd.report {
                    self.disk_blobs = build_disk_shader_blobs(&report);
                    if self
                        .disk_store
                        .list_disk_snapshots(None)
                        .unwrap_or_default()
                        .is_empty()
                    {
                        let _ = self
                            .disk_store
                            .save_disk_snapshot(&report, Some("Launch Baseline"));
                        self.disk_snapshots = self
                            .disk_store
                            .list_disk_snapshots(None)
                            .unwrap_or_default();
                    }
                    self.disk_report = report;
                    self.treemap_rects.clear();
                    self.treemap_last_size = None;
                    self.disk_scanning = false;
                    self.disk_scan_completed_at = Some(Instant::now());
                    finished = true;
                } else {
                    // Update intermediate live blobs from streaming category totals
                    let total = upd.scanned_bytes.max(1);
                    let categories: Vec<CategoryTotal> = DiskCategory::ALL
                        .iter()
                        .map(|&cat| {
                            let b = upd.category_bytes[cat as usize];
                            CategoryTotal {
                                category: cat,
                                bytes: b,
                                file_count: upd.category_counts[cat as usize],
                                share_fraction: (b as f64 / total as f64) as f32,
                            }
                        })
                        .collect();
                    self.disk_report.total_bytes = upd.scanned_bytes;
                    self.disk_report.total_files = upd.file_count;
                    self.disk_report.categories = categories;
                    self.disk_blobs = build_disk_shader_blobs(&self.disk_report);
                }
            }
        }
        if finished {
            self.disk_scan_rx = None;
        }
        if let Some(done_at) = self.disk_scan_completed_at {
            self.disk_scan_completion = (done_at.elapsed().as_secs_f32() / 0.4).clamp(0.0, 1.0);
        }
    }

    fn sync_automation_config(&self) {
        let mut cfg = self
            .auto_config_shared
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        cfg.rules = self.rules_engine.rules.clone();
        cfg.autobalance_enabled = self.autobalance.enabled;
        cfg.idle_throttle_enabled = self.idle_throttle.enabled;
        cfg.sleep_manual_override = self.sleep_preventing;
        cfg.quiet_hours_enabled = self.quiet_hours.enabled;
        cfg.quiet_hours_start = self.quiet_hours.start_hhmm.clone();
        cfg.quiet_hours_end = self.quiet_hours.end_hhmm.clone();
        cfg.alerts_cpu_threshold = self.alerts_cpu_thresh;
        cfg.alerts_mem_threshold = self.alerts_mem_thresh;
        cfg.alerts_temp_threshold = self.alerts_temp_thresh;
        cfg.alerts_disk_threshold = self.alerts_disk_thresh;
        cfg.alerts_webhook_url = if self.alerts_webhook_buf.trim().is_empty() {
            None
        } else {
            Some(CompactString::from(self.alerts_webhook_buf.trim()))
        };
    }

    fn save_preferences(&self) {
        self.sync_automation_config();
        if self.skip_save_prefs {
            return;
        }
        let mut prefs = UserPreferences::capture_from(
            &self.app.theme,
            self.preferred_interval_ms,
            &self.rules_engine.rules,
        );
        prefs.workspace_profile = Some(WorkspaceProfile {
            id: CompactString::const_new("default"),
            name: CompactString::const_new("Default Workspace"),
            page: self.app.page_engine.clone(),
        });
        prefs.quiet_hours_enabled = self.quiet_hours.enabled;
        prefs.quiet_hours_start = self.quiet_hours.start_hhmm.clone();
        prefs.quiet_hours_end = self.quiet_hours.end_hhmm.clone();
        prefs.alerts_cpu_threshold = self.alerts_cpu_thresh;
        prefs.alerts_mem_threshold = self.alerts_mem_thresh;
        prefs.alerts_temp_threshold = self.alerts_temp_thresh;
        prefs.alerts_disk_threshold = self.alerts_disk_thresh;
        prefs.alerts_webhook_url = if self.alerts_webhook_buf.trim().is_empty() {
            None
        } else {
            Some(CompactString::from(self.alerts_webhook_buf.trim()))
        };
        prefs.sleep_prevention_enabled = self.sleep_preventing;
        prefs.idle_throttle_enabled = self.idle_throttle.enabled;
        prefs.autobalance_enabled = self.autobalance.enabled;
        prefs.saved_custom_presets = self.saved_custom_presets.clone();
        prefs.process_columns = self.platform_cache.process_columns.clone();
        prefs.app_columns = self.platform_cache.app_columns.clone();
        prefs.popout_pos_x = self.popout_pos_x;
        prefs.popout_pos_y = self.popout_pos_y;
        let _ = prefs.save_to(&self.prefs_path);
    }

    #[cfg(any(target_os = "macos", target_os = "windows"))]
    fn init_tray_if_needed(&mut self) {
        if self.tray.is_some() || self.screenshot_out.is_some() {
            return;
        }
        let menu = Menu::new();
        let summary_item = MenuItem::new("CPU: --% | Mem: --%", false, None);
        let open_item = MenuItem::new("Open Nexus System Monitor", true, None);
        let popout_item = MenuItem::new("Toggle Popout Mini-Monitor", true, None);
        let gaming_item = MenuItem::new("Toggle Gaming Mode", true, None);
        let quit_item = MenuItem::new("Quit Nexus System Monitor", true, None);

        let id_open_main = open_item.id().clone();
        let id_toggle_popout = popout_item.id().clone();
        let id_toggle_gaming = gaming_item.id().clone();
        let id_quit = quit_item.id().clone();

        let _ = menu.append(&summary_item);
        let _ = menu.append(&PredefinedMenuItem::separator());
        let _ = menu.append(&open_item);
        let _ = menu.append(&popout_item);
        let _ = menu.append(&gaming_item);
        let _ = menu.append(&PredefinedMenuItem::separator());
        let _ = menu.append(&quit_item);

        let mut rgba = vec![0u8; 22 * 22 * 4];
        for y in 4..18 {
            for x in [3, 4, 5, 8, 9, 10, 13, 14, 15, 18, 19] {
                let bar_top = match x {
                    3..=5 => 8,
                    8..=10 => 5,
                    13..=15 => 10,
                    _ => 6,
                };
                if y >= bar_top {
                    let idx = (y * 22 + x) * 4;
                    rgba[idx] = 48;
                    rgba[idx + 1] = 209;
                    rgba[idx + 2] = 88;
                    rgba[idx + 3] = 255;
                }
            }
        }
        let Ok(icon) = Icon::from_rgba(rgba, 22, 22) else {
            return;
        };

        let builder = TrayIconBuilder::new()
            .with_menu(Box::new(menu.clone()))
            .with_tooltip("Nexus System Monitor")
            .with_title("NEXUS")
            .with_icon(icon);

        #[cfg(target_os = "macos")]
        let builder = builder.with_autosave_name("com.joshsutcliff.nexusmonitor.tray");

        if let Ok(tray_icon) = builder.build() {
            self.tray = Some(TrayMenuState {
                _tray_icon: tray_icon,
                _menu: menu,
                summary_item,
                id_open_main,
                id_toggle_popout,
                id_toggle_gaming,
                id_quit,
                last_title: String::new(),
            });
            self.update_tray_telemetry();
        }
    }

    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    fn init_tray_if_needed(&mut self) {}

    #[cfg(any(target_os = "macos", target_os = "windows"))]
    fn update_tray_telemetry(&mut self) {
        let Some(tray) = &mut self.tray else {
            return;
        };
        let cpu_s = self.snapshot.system.cpu_percent.format_with_unit(0, "%");
        let mem_s = self
            .snapshot
            .system
            .mem_used_percent
            .format_with_unit(0, "%");
        let title = format!("CPU {cpu_s} Mem {mem_s}");
        if title != tray.last_title {
            tray._tray_icon.set_title(Some(&title));
            tray.summary_item.set_text(format!(
                "Live Telemetry: CPU {} | Memory {} | Package {}",
                self.snapshot.system.cpu_percent.format_with_unit(1, "%"),
                self.snapshot
                    .system
                    .mem_used_percent
                    .format_with_unit(1, "%"),
                self.snapshot
                    .hardware_telemetry
                    .soc_power
                    .package_watts
                    .format_with_unit(1, "W")
            ));
            tray.last_title = title;
        }
    }

    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    fn update_tray_telemetry(&mut self) {}

    fn activate_main_window(&mut self) {
        self.window_visible = true;
        if let Some(w) = &self.window {
            w.set_minimized(false);
            w.set_visible(true);
            w.focus_window();
            w.request_redraw();
        }
    }

    fn toggle_popout_mode(&mut self) {
        self.show_popout_overlay = !self.show_popout_overlay;
        self.app.theme.popout_mode = if self.show_popout_overlay {
            PopoutMode::TopMost
        } else {
            PopoutMode::Off
        };
        self.sync_window_level();
        self.save_preferences();
    }

    fn sync_window_level(&self) {
        if let Some(w) = &self.window {
            w.set_window_level(WindowLevel::Normal);
        }
        if let Some(pop_win) = &self.popout_window {
            let level = match self.app.theme.popout_mode {
                PopoutMode::TopMost => WindowLevel::AlwaysOnTop,
                PopoutMode::Desktop => WindowLevel::AlwaysOnBottom,
                _ => WindowLevel::Normal,
            };
            pop_win.set_window_level(level);
        }
    }

    fn flush_popout_pos_if_dirty(&mut self) {
        if let Some(dirty_at) = self.popout_pos_dirty_at
            && dirty_at.elapsed() >= Duration::from_millis(400)
        {
            self.popout_pos_dirty_at = None;
            self.save_preferences();
        }
    }

    fn sync_popout_window(&mut self, event_loop: &ActiveEventLoop) {
        let want_popout = self.show_popout_overlay && self.app.theme.popout_mode != PopoutMode::Off;
        if want_popout {
            let resolved = self
                .app
                .theme
                .resolve_popout(self.popout_hovered, self.gaming_mode.active);
            let w = resolved.width.max(240.0);
            let h = resolved.height.max(180.0);
            let target_size = (w as u32, h as u32);
            let level = match self.app.theme.popout_mode {
                PopoutMode::TopMost => WindowLevel::AlwaysOnTop,
                PopoutMode::Desktop => WindowLevel::AlwaysOnBottom,
                _ => WindowLevel::Normal,
            };

            if let Some(pop_win) = &self.popout_window {
                if self.popout_last_skin_size != Some(target_size) {
                    let _ =
                        pop_win.request_inner_size(LogicalSize::new(f64::from(w), f64::from(h)));
                    self.popout_last_skin_size = Some(target_size);
                }
                pop_win.set_window_level(level);
                if let Some(main_win) = &self.window {
                    main_win.set_window_level(WindowLevel::Normal);
                }
                return;
            }

            let base_attrs = Window::default_attributes()
                .with_title(format!("Nexus Popout · {}", resolved.skin_name))
                .with_decorations(false)
                .with_resizable(false)
                .with_transparent(true)
                .with_inner_size(LogicalSize::new(f64::from(w), f64::from(h)))
                .with_window_level(level)
                .with_window_icon(load_winit_window_icon());
            let attrs = if let (Some(px), Some(py)) = (self.popout_pos_x, self.popout_pos_y) {
                base_attrs.with_position(winit::dpi::PhysicalPosition::new(px, py))
            } else {
                base_attrs.with_position(winit::dpi::LogicalPosition::new(960.0, 96.0))
            };

            let pop_win = Arc::new(
                event_loop
                    .create_window(attrs)
                    .expect("create popout window"),
            );
            self.popout_egui_ctx = egui::Context::default();
            configure_bundled_fonts(&self.popout_egui_ctx);

            let wgpu_cfg = if let Some(rs) = self.painter.render_state() {
                egui_wgpu::WgpuConfiguration {
                    wgpu_setup: egui_wgpu::WgpuSetup::Existing(egui_wgpu::WgpuSetupExisting {
                        instance: rs.instance.clone(),
                        adapter: rs.adapter.clone(),
                        device: rs.device.clone(),
                        queue: rs.queue.clone(),
                    }),
                    ..Default::default()
                }
            } else {
                egui_wgpu::WgpuConfiguration::default()
            };

            let mut pop_painter = self.tokio_rt.block_on(Painter::new(
                self.popout_egui_ctx.clone(),
                wgpu_cfg.clone(),
                cfg!(target_os = "macos"),
                egui_wgpu::RendererOptions::default(),
            ));
            if self
                .tokio_rt
                .block_on(pop_painter.set_window(ViewportId::ROOT, Some(Arc::clone(&pop_win))))
                .is_err()
            {
                pop_painter = self.tokio_rt.block_on(Painter::new(
                    self.popout_egui_ctx.clone(),
                    wgpu_cfg,
                    false,
                    egui_wgpu::RendererOptions::default(),
                ));
                let _ = self
                    .tokio_rt
                    .block_on(pop_painter.set_window(ViewportId::ROOT, Some(Arc::clone(&pop_win))));
            }

            let pop_state = egui_winit::State::new(
                self.popout_egui_ctx.clone(),
                ViewportId::ROOT,
                event_loop,
                Some(pop_win.scale_factor() as f32),
                event_loop.system_theme(),
                pop_painter.max_texture_side(),
            );
            pop_win.request_redraw();
            self.popout_window = Some(pop_win);
            self.popout_egui_state = Some(pop_state);
            self.popout_painter = Some(pop_painter);
            self.popout_last_skin_size = Some(target_size);
            self.popout_frame_count = 0;
            if let Some(main_win) = &self.window {
                main_win.set_window_level(WindowLevel::Normal);
            }
        } else if self.popout_window.is_some() {
            self.popout_egui_state = None;
            self.popout_painter = None;
            self.popout_window = None;
            self.popout_last_skin_size = None;
            self.popout_hovered = false;
        }
    }

    fn toggle_gaming_mode(&mut self) {
        if self.gaming_mode.active {
            let restored = self
                .gaming_mode
                .deactivate_and_collect_valid_restores(&self.snapshot)
                .unwrap_or_default();
            self.app
                .set_gaming_mode_throttle(self.gaming_mode.visual_throttle());
            self._cadence_lease.update(
                Duration::from_millis(self.preferred_interval_ms),
                DomainMask::CPU_MEM_GPU
                    | DomainMask::SOC_POWER
                    | DomainMask::THERMALS
                    | DomainMask::DISK_NET_IO
                    | DomainMask::PROCESSES,
            );
            self.action_status = format!(
                "Gaming Mode OFF: Restored {} verified ProcessKey receipts.",
                restored.len()
            );
        } else {
            let receipts = self
                .autobalance
                .evaluate(&self.snapshot, None, now_unix_ms());
            let _ = self.gaming_mode.activate(None, receipts.clone());
            self.app
                .set_gaming_mode_throttle(self.gaming_mode.visual_throttle());
            self._cadence_lease.update(
                Duration::from_millis(2000),
                DomainMask::CPU_MEM_GPU | DomainMask::PROCESSES,
            );
            self.action_status = format!(
                "Gaming Mode ON: Journaled {} receipts, visualFrameRate=0, shaders paused.",
                receipts.len()
            );
        }
        if let Some(w) = &self.window {
            w.request_redraw();
        }
        if let Some(pw) = &self.popout_window {
            pw.request_redraw();
        }
    }

    fn record_all_histories_from_snapshot(&mut self, snap: &SystemSnapshot, seed_two_points: bool) {
        if seed_two_points {
            self.app.step_meters(snap, 0.25);
            self.app.seed_initial_history(snap);
        } else if !self.app.record_snapshot_history(snap) {
            return;
        }

        let sec = (snap.sampled_at_ns as f64) / 1_000_000_000.0;
        let pre_sec = (sec - 0.5).max(0.0);

        let (user_pct, sys_pct) = if !snap.system.cores.is_empty() {
            let mut u_sum = 0.0;
            let mut u_cnt = 0usize;
            let mut s_sum = 0.0;
            let mut s_cnt = 0usize;
            for core in &snap.system.cores {
                if let Some(u) = measured_opt(&core.user_percent) {
                    u_sum += u;
                    u_cnt += 1;
                }
                if let Some(s) = measured_opt(&core.system_percent) {
                    s_sum += s;
                    s_cnt += 1;
                }
            }
            let u = if u_cnt > 0 {
                Some(u_sum / u_cnt as f64)
            } else {
                measured_opt(&snap.system.cpu_percent).map(|c| c * 0.7)
            };
            let s = if s_cnt > 0 {
                Some(s_sum / s_cnt as f64)
            } else {
                measured_opt(&snap.system.cpu_percent).map(|c| c * 0.3)
            };
            (u, s)
        } else {
            let cpu = measured_opt(&snap.system.cpu_percent);
            (cpu.map(|c| c * 0.7), cpu.map(|c| c * 0.3))
        };

        push_graph_sample(
            &mut self.perf_cpu_user_history,
            pre_sec,
            sec,
            seed_two_points,
            user_pct,
        );
        push_graph_sample(
            &mut self.perf_cpu_sys_history,
            pre_sec,
            sec,
            seed_two_points,
            sys_pct,
        );
        push_graph_metric(
            &mut self.perf_mem_history,
            pre_sec,
            sec,
            seed_two_points,
            &snap.system.mem_used_percent,
        );
        push_graph_metric(
            &mut self.perf_gpu_history,
            pre_sec,
            sec,
            seed_two_points,
            &snap.system.gpu_utilization_percent,
        );
        push_graph_metric(
            &mut self.perf_pkg_watts_history,
            pre_sec,
            sec,
            seed_two_points,
            &snap.hardware_telemetry.soc_power.package_watts,
        );

        let disk_read_mbs = sum_measured_metrics(snap.disks.iter().map(|d| &d.read_bytes_per_sec))
            .map(|b| b / (1024.0 * 1024.0));
        let disk_write_mbs =
            sum_measured_metrics(snap.disks.iter().map(|d| &d.write_bytes_per_sec))
                .map(|b| b / (1024.0 * 1024.0));
        push_graph_sample(
            &mut self.perf_disk_read_history,
            pre_sec,
            sec,
            seed_two_points,
            disk_read_mbs,
        );
        push_graph_sample(
            &mut self.perf_disk_write_history,
            pre_sec,
            sec,
            seed_two_points,
            disk_write_mbs,
        );

        let net_down_mbps = sum_measured_metrics(snap.networks.iter().map(|n| &n.rx_bytes_per_sec))
            .map(|b| b * 8.0 / 1_000_000.0);
        let net_up_mbps = sum_measured_metrics(snap.networks.iter().map(|n| &n.tx_bytes_per_sec))
            .map(|b| b * 8.0 / 1_000_000.0);
        push_graph_sample(
            &mut self.perf_net_down_history,
            pre_sec,
            sec,
            seed_two_points,
            net_down_mbps,
        );
        push_graph_sample(
            &mut self.perf_net_up_history,
            pre_sec,
            sec,
            seed_two_points,
            net_up_mbps,
        );

        for core in &snap.system.cores {
            let idx = core.core_index as usize;
            if self.perf_core_sparklines.len() <= idx {
                self.perf_core_sparklines
                    .resize_with(idx + 1, || std::collections::VecDeque::with_capacity(30));
            }
            let val = measured_opt(&core.total_percent)
                .unwrap_or(0.0)
                .clamp(0.0, 100.0);
            let deque = &mut self.perf_core_sparklines[idx];
            let repeats = if seed_two_points { 2 } else { 1 };
            for _ in 0..repeats {
                if deque.len() >= 30 {
                    deque.pop_front();
                }
                deque.push_back(val);
            }
        }

        for tile in &self.page_edit.current.tiles {
            for sp in tile
                .sensors
                .iter()
                .chain(tile.config.low_priority_sensors.iter())
                .chain(tile.config.total_sensors.iter())
            {
                for (expanded_sp, _) in sp.expand(snap) {
                    if let Some(m) = expanded_sp.resolve_metric(snap) {
                        let hist = self
                            .sensor_histories
                            .entry(CompactString::from(expanded_sp.as_path_string()))
                            .or_insert_with(|| CompressedHistoryGraph::new(256));
                        push_graph_metric(hist, pre_sec, sec, seed_two_points, &m);
                    }
                }
            }
            if tile.face == FaceKind::CompressedHistoryGraph
                && let Some(sp) = tile.sensors.first()
                && let Some((expanded_sp, _)) = sp.expand(snap).into_iter().next()
                && let Some(m) = expanded_sp.resolve_metric(snap)
            {
                let hist = self
                    .tile_histories
                    .entry(tile.id.clone())
                    .or_insert_with(|| CompressedHistoryGraph::new(256));
                push_graph_metric(hist, pre_sec, sec, seed_two_points, &m);
            }
        }
    }

    fn update_live_process_and_health_caches(&mut self, snap: &SystemSnapshot) {
        let live_keys: std::collections::HashSet<ProcessKey> = snap
            .processes
            .iter()
            .chain(snap.exited_processes.iter())
            .map(|p| p.key)
            .collect();
        self.platform_cache
            .process_sparklines
            .retain(|k, _| live_keys.contains(k));
        for p in &snap.processes {
            let cpu_val = match p.cpu_percent.reading {
                MetricReading::Measured { value, .. } if value.is_finite() => value,
                _ => 0.0,
            };
            let mem_mib = metric_mib(&p.phys_footprint_bytes);
            self.platform_cache
                .process_sparklines
                .entry(p.key)
                .or_default()
                .push(cpu_val, mem_mib);
        }
        let health_score = HealthScoring::evaluate_snapshot(snap).overall_score;
        self.platform_cache
            .health_tracker
            .record(snap.sampled_at_ns, health_score);
        self.platform_cache.update_app_live_usage(snap);
    }

    fn ingest_latest_snapshot(&mut self) {
        self.poll_disk_scan_updates();
        if let Some(rgb8) = self
            .wallpaper_rgb_slot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            self.app.theme.luminance_analyzer.analyze_rgb8(&rgb8);
        }
        if let Some(rep) = self
            .wifi_async_slot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            self.platform_cache.wifi_report = Some(rep);
            self.platform_cache.wifi_scanning = false;
        }
        if let Some(lan) = self
            .lan_async_slot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            self.platform_cache.lan_report = Some(lan);
            self.platform_cache.lan_scanning = false;
        }

        if self.ui_inbox.has_changed().unwrap_or(false) {
            let newest = Arc::clone(&*self.ui_inbox.borrow_and_update());
            self.last_snapshot_received_at = Instant::now();
            self.shared_telemetry.lock().update_from_snapshot(&newest);
            if let Some(srv) = &self.remote_server {
                srv.broadcast_snapshot(&newest);
            }
            if self.flight_recorder.len() >= 300 {
                self.flight_recorder.remove(0);
            }
            self.flight_recorder.push(Arc::clone(&newest));
            if self.trace_recorder.is_recording() {
                self.trace_recorder.record_snapshot(&newest);
            }
            if self.scrub_index.is_none() {
                self.snapshot = newest;
                let snap = Arc::clone(&self.snapshot);
                self.record_all_histories_from_snapshot(&snap, false);
                self.update_live_process_and_health_caches(&snap);
            }
            self.update_tray_telemetry();
        }
    }

    fn active_accent_color32(&self) -> Color32 {
        if self.app.theme.display_mode == DisplayMode::Color {
            let meta = self.app.theme.active_preset_metadata();
            let sat = self.app.theme.apply_display_saturation(meta.accent_srgb);
            srgb_to_color32(sat)
        } else {
            let srgb = self.app.theme.primary_vfd_srgb();
            srgb_to_color32([srgb[0], srgb[1], srgb[2]])
        }
    }

    fn draw_ui(&mut self, ui: &mut egui::Ui) {
        let now = Instant::now();
        let dt_s = now
            .duration_since(self.last_frame_instant)
            .as_secs_f64()
            .clamp(0.001, 0.25);
        self.last_frame_instant = now;

        self.app.theme.luminance_analyzer.step(dt_s);
        self.app.step_meters(&self.snapshot, dt_s);
        let accent = self.active_accent_color32();
        let mut prefs_dirty = false;

        // Global Keyboard Shortcuts: Cmd/Ctrl+K Command Palette & F12 Screensaver & Escape
        ui.ctx().input(|inp| {
            if (inp.modifiers.command || inp.modifiers.ctrl) && inp.key_pressed(Key::K) {
                self.command_palette_open = !self.command_palette_open;
                self.command_palette_query.clear();
            }
            if inp.key_pressed(Key::F12) {
                self.screensaver_active = !self.screensaver_active;
                if self.screensaver_active {
                    self.screensaver_last_pointer_activity = Instant::now();
                }
            }
            if inp.key_pressed(Key::Escape) {
                if self.command_palette_open {
                    self.command_palette_open = false;
                } else if self.screensaver_active {
                    self.screensaver_active = false;
                }
            }
        });

        // Fullscreen F12 VFD Vector Tunnel Screensaver with 5s honesty & 8s auto-fade HUD
        if self.screensaver_active {
            self.draw_screensaver_overlay(ui, accent);
            return;
        }

        // Apply DisplayMode window background, Crystal-Glass adaptive surfaces & specular borders
        let meta = self.app.theme.active_preset_metadata();
        let mut visuals = if meta.is_dark {
            egui::Visuals::dark()
        } else {
            egui::Visuals::light()
        };
        let win_bg = if let Some(bg) = self.app.theme.display_mode.window_background_srgb() {
            srgb_rgba_to_color32(bg)
        } else {
            let alpha = if self.app.theme.use_crystal_glass_backdrop && meta.is_glass_enabled {
                (self.app.theme.luminance_analyzer.current_opacity_floor * 255.0)
                    .clamp(120.0, 250.0) as u8
            } else {
                255
            };
            let bg = self.app.theme.apply_display_saturation(meta.window_bg_srgb);
            Color32::from_rgba_unmultiplied(
                (bg[0] * 255.0) as u8,
                (bg[1] * 255.0) as u8,
                (bg[2] * 255.0) as u8,
                alpha,
            )
        };
        let surf_rgb = self
            .app
            .theme
            .apply_display_saturation(meta.surface_bg_srgb);
        let surf_col = srgb_to_color32(surf_rgb);
        let faint_col =
            Color32::from_rgba_unmultiplied(surf_col.r(), surf_col.g(), surf_col.b(), 215);
        let hover_col = Color32::from_rgba_unmultiplied(accent.r(), accent.g(), accent.b(), 42);
        let active_col = Color32::from_rgba_unmultiplied(accent.r(), accent.g(), accent.b(), 72);
        visuals.panel_fill = win_bg;
        visuals.window_fill = win_bg;
        visuals.extreme_bg_color = surf_col;
        visuals.faint_bg_color = faint_col;
        visuals.code_bg_color = surf_col;
        visuals.widgets.noninteractive.bg_fill = surf_col;
        visuals.widgets.inactive.bg_fill = surf_col;
        visuals.widgets.hovered.bg_fill = hover_col;
        visuals.widgets.active.bg_fill = active_col;
        visuals.widgets.open.bg_fill = surf_col;
        visuals.selection.bg_fill =
            Color32::from_rgba_unmultiplied(accent.r(), accent.g(), accent.b(), 64);
        visuals.selection.stroke = Stroke::new(1.0, accent);
        let text_acc = srgb_to_color32(
            self.app
                .theme
                .apply_display_saturation(meta.text_accent_srgb),
        );
        visuals.hyperlink_color = text_acc;
        if meta.is_specular_enabled {
            let spec_alpha = (meta.specular_intensity.clamp(0.1, 1.0) * 140.0) as u8;
            let border_col = if meta.is_dark {
                Color32::from_rgba_unmultiplied(255, 255, 255, spec_alpha)
            } else {
                Color32::from_rgba_unmultiplied(15, 23, 42, spec_alpha / 2)
            };
            visuals.widgets.noninteractive.bg_stroke = Stroke::new(1.0, border_col);
        }
        ui.ctx().set_visuals(visuals);
        ui.ctx().set_zoom_factor(
            (f32::from(self.app.theme.text_size_percent) / 100.0)
                * meta.font_size_multiplier.clamp(0.75, 2.0),
        );

        let health_summary = HealthScoring::evaluate_snapshot(&self.snapshot);
        let pill_bg = surf_col;
        let pill_border = ui.visuals().widgets.noninteractive.bg_stroke;

        // Clean Apple HIG Top Toolbar
        egui::Panel::top("nexus_top_toolbar")
            .frame(
                egui::Frame::side_top_panel(ui.style())
                    .fill(win_bg)
                    .inner_margin(egui::Margin::symmetric(12, 8)),
            )
            .show(ui, |ui| {
                ui.horizontal_wrapped(|ui| {
                    ui.label(
                        RichText::new("Nexus System Monitor")
                            .font(FontId::proportional(15.0))
                            .strong()
                            .color(ui.visuals().strong_text_color()),
                    );
                    let (h_word, h_col) = match health_summary.overall_level {
                        HealthLevel::Excellent => ("Optimal", hig_green(ui)),
                        HealthLevel::Good => ("Good", hig_green(ui)),
                        HealthLevel::Fair => ("Fair", hig_amber(ui)),
                        HealthLevel::Poor => ("Degraded", hig_red(ui)),
                        HealthLevel::Critical => ("Critical", hig_red(ui)),
                    };
                    hig_status_pill(
                        ui,
                        &format!("Health: {:.0} · {h_word}", health_summary.overall_score),
                        h_col,
                    );
                    ui.add_space(6.0);

                    // Segmented Display Pill Group
                    egui::Frame::new()
                        .fill(pill_bg)
                        .stroke(pill_border)
                        .corner_radius(CornerRadius::same(8))
                        .inner_margin(egui::Margin::symmetric(8, 3))
                        .show(ui, |ui| {
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new("Display:")
                                        .font(FontId::proportional(11.5))
                                        .color(ui.visuals().weak_text_color()),
                                );
                                let is_tint = !matches!(
                                    self.app.theme.display_mode,
                                    DisplayMode::Color | DisplayMode::Mono
                                );
                                if ui
                                    .selectable_label(
                                        self.app.theme.display_mode == DisplayMode::Color,
                                        "Color",
                                    )
                                    .on_hover_text("Use full-color Crystal Glass theme palette")
                                    .clicked()
                                {
                                    self.app.theme.display_mode = DisplayMode::Color;
                                    prefs_dirty = true;
                                }
                                if ui
                                    .selectable_label(
                                        self.app.theme.display_mode == DisplayMode::Mono,
                                        "Mono",
                                    )
                                    .on_hover_text("Desaturate display to neutral monochrome")
                                    .clicked()
                                {
                                    self.app.theme.display_mode = DisplayMode::Mono;
                                    prefs_dirty = true;
                                }
                                if ui
                                    .selectable_label(is_tint, "Tint")
                                    .on_hover_text("Apply phosphor monochrome color tint")
                                    .clicked()
                                {
                                    self.app.theme.display_mode = self.last_tint_mode;
                                    prefs_dirty = true;
                                }
                                if is_tint {
                                    egui::ComboBox::from_id_salt("toolbar_tint_picker")
                                        .width(68.0)
                                        .selected_text(self.app.theme.display_mode.title())
                                        .show_ui(ui, |ui| {
                                            for tint in [
                                                DisplayMode::Green,
                                                DisplayMode::Amber,
                                                DisplayMode::Blue,
                                                DisplayMode::Red,
                                                DisplayMode::Purple,
                                            ] {
                                                if ui
                                                    .selectable_value(
                                                        &mut self.app.theme.display_mode,
                                                        tint,
                                                        tint.title(),
                                                    )
                                                    .changed()
                                                {
                                                    self.last_tint_mode = tint;
                                                    prefs_dirty = true;
                                                }
                                            }
                                        })
                                        .response
                                        .on_hover_text("Choose phosphor tint hue");
                                }
                            });
                        });

                    // Segmented VFD Pill Group
                    egui::Frame::new()
                        .fill(pill_bg)
                        .stroke(pill_border)
                        .corner_radius(CornerRadius::same(8))
                        .inner_margin(egui::Margin::symmetric(8, 3))
                        .show(ui, |ui| {
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new("VFD:")
                                        .font(FontId::proportional(11.5))
                                        .color(ui.visuals().weak_text_color()),
                                );
                                for (vc, label) in [
                                    (VfdTextColor::Automatic, "Auto"),
                                    (VfdTextColor::Amber, "Amber"),
                                    (VfdTextColor::Green, "Green"),
                                    (VfdTextColor::Blue, "Cyan"),
                                ] {
                                    if ui
                                        .selectable_label(self.app.theme.vfd_text_color == vc, label)
                                        .on_hover_text("Set VFD instrument phosphor color")
                                        .clicked()
                                    {
                                        self.app.theme.vfd_text_color = vc;
                                        prefs_dirty = true;
                                    }
                                }
                            });
                        });

                    // Segmented Refresh Rate Pill Group
                    egui::Frame::new()
                        .fill(pill_bg)
                        .stroke(pill_border)
                        .corner_radius(CornerRadius::same(8))
                        .inner_margin(egui::Margin::symmetric(8, 3))
                        .show(ui, |ui| {
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new("Rate:")
                                        .font(FontId::proportional(11.5))
                                        .color(ui.visuals().weak_text_color()),
                                );
                                for (ms, label, tip) in [
                                    (250u64, "4Hz", "Fast telemetry sampling (250 ms)"),
                                    (500, "2Hz", "Normal telemetry sampling (500 ms)"),
                                    (1000, "1Hz", "Low-overhead telemetry sampling (1000 ms)"),
                                    (2000, "0.5Hz", "Eco telemetry sampling (2000 ms)"),
                                ] {
                                    if ui
                                        .selectable_label(self.preferred_interval_ms == ms, label)
                                        .on_hover_text(tip)
                                        .clicked()
                                    {
                                        self.preferred_interval_ms = ms;
                                        if !self.gaming_mode.active {
                                            self._cadence_lease.update(
                                                Duration::from_millis(ms),
                                                DomainMask::CPU_MEM_GPU
                                                    | DomainMask::SOC_POWER
                                                    | DomainMask::THERMALS
                                                    | DomainMask::DISK_NET_IO
                                                    | DomainMask::PROCESSES,
                                            );
                                        }
                                        prefs_dirty = true;
                                    }
                                }
                            });
                        });

                    // Quick Action Pills (right-aligned when room exists, wraps cleanly when narrow)
                    let mut draw_quick_actions = |ui: &mut egui::Ui| {
                        let gm_text = if self.gaming_mode.active {
                            RichText::new("Gaming: ON")
                                .strong()
                                .color(hig_green(ui))
                        } else {
                            RichText::new("Gaming").color(ui.visuals().text_color())
                        };
                        if ui
                            .button(gm_text)
                            .on_hover_text(
                                "Gaming Mode: pause background shaders and throttle background hogs for maximum frame rate",
                            )
                            .clicked()
                        {
                            self.toggle_gaming_mode();
                        }
                        if ui
                            .button("F12")
                            .on_hover_text(
                                "Launch fullscreen 3D Vector Tunnel telemetry screensaver (F12)",
                            )
                            .clicked()
                        {
                            self.screensaver_active = true;
                            self.screensaver_last_pointer_activity = Instant::now();
                        }
                        if ui
                            .button("Cmd+K")
                            .on_hover_text(
                                "Open quick navigation and action command palette (Cmd+K)",
                            )
                            .clicked()
                        {
                            self.command_palette_open = !self.command_palette_open;
                            self.command_palette_query.clear();
                        }
                        if ui
                            .button(format!("{}%", self.app.theme.text_size_percent))
                            .on_hover_text("Cycle interface text zoom scale (100% - 200%)")
                            .clicked()
                        {
                            let pct = if self.app.theme.text_size_percent >= 200 {
                                self.app.theme.text_size_percent = 100;
                                100
                            } else {
                                self.app.theme.increase_text_size()
                            };
                            ui.ctx().set_zoom_factor(
                                (f32::from(pct) / 100.0)
                                    * meta.font_size_multiplier.clamp(0.75, 2.0),
                            );
                            prefs_dirty = true;
                        }
                    };
                    if ui.available_width() >= 230.0 {
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            draw_quick_actions(ui);
                        });
                    } else {
                        draw_quick_actions(ui);
                    }
                });
            });

        // Bottom Status Bar
        let rt_state = self
            .auto_runtime_shared
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        egui::Panel::bottom("nexus_status_bar")
            .frame(
                egui::Frame::side_top_panel(ui.style())
                    .fill(win_bg)
                    .inner_margin(egui::Margin::symmetric(12, 5)),
            )
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new(format!(
                            "Gen #{} · Procs: {} ({} exited) · CPU: {} · Mem: {} · Pkg: {} · Die: {} · Alerts: {} · {}",
                            self.snapshot.generation,
                            self.snapshot.processes.len(),
                            self.snapshot.exited_processes.len(),
                            self.snapshot.system.cpu_percent.format_with_unit(1, "%"),
                            self.snapshot.system.mem_used_percent.format_with_unit(1, "%"),
                            self.snapshot
                                .hardware_telemetry
                                .soc_power
                                .package_watts
                                .format_with_unit(1, "W"),
                            self.snapshot
                                .hardware_telemetry
                                .thermals
                                .max_die_celsius
                                .format_with_unit(1, "C"),
                            rt_state.recent_alerts.len(),
                            self.action_status
                        ))
                        .font(FontId::proportional(11.5))
                        .color(ui.visuals().weak_text_color()),
                    );
                });
            });

        // Left 4-Section Grouped macOS Sidebar + Popout Launcher + Theme Preset Selector
        let sidebar_rgb = self
            .app
            .theme
            .apply_display_saturation(meta.sidebar_bg_srgb);
        let sidebar_fill = if self.app.theme.display_mode == DisplayMode::Color {
            srgb_to_color32(sidebar_rgb)
        } else {
            win_bg
        };
        egui::Panel::left("nexus_sidebar")
            .frame(
                egui::Frame::side_top_panel(ui.style())
                    .fill(sidebar_fill)
                    .inner_margin(egui::Margin::symmetric(10, 10)),
            )
            .default_size(210.0)
            .min_size(196.0)
            .show(ui, |ui| {
                let nav_height = (ui.available_height() - 210.0).max(200.0);
                egui::ScrollArea::vertical()
                    .id_salt("nav_scroll")
                    .max_height(nav_height)
                    .show(ui, |ui| {
                        for (sec_idx, (section_title, views)) in
                            GuiView::SIDEBAR_SECTIONS.iter().enumerate()
                        {
                            if sec_idx > 0 {
                                ui.add_space(8.0);
                            }
                            ui.label(
                                RichText::new(*section_title)
                                    .font(FontId::proportional(11.0))
                                    .strong()
                                    .color(ui.visuals().weak_text_color()),
                            );
                            ui.add_space(2.0);
                            for &nav in *views {
                                let selected = self.active_view == nav;
                                let row_fill = if selected {
                                    Color32::from_rgba_unmultiplied(
                                        accent.r(),
                                        accent.g(),
                                        accent.b(),
                                        44,
                                    )
                                } else {
                                    Color32::TRANSPARENT
                                };
                                let row_stroke = if selected {
                                    Stroke::new(
                                        1.0,
                                        Color32::from_rgba_unmultiplied(
                                            accent.r(),
                                            accent.g(),
                                            accent.b(),
                                            110,
                                        ),
                                    )
                                } else {
                                    Stroke::NONE
                                };
                                let text = if selected {
                                    RichText::new(nav.title())
                                        .font(FontId::proportional(13.0))
                                        .strong()
                                        .color(accent)
                                } else {
                                    RichText::new(nav.title())
                                        .font(FontId::proportional(13.0))
                                        .color(ui.visuals().text_color())
                                };
                                let resp = egui::Frame::new()
                                    .fill(row_fill)
                                    .stroke(row_stroke)
                                    .corner_radius(CornerRadius::same(7))
                                    .inner_margin(egui::Margin::symmetric(8, 4))
                                    .show(ui, |ui| {
                                        ui.set_min_width(ui.available_width());
                                        ui.add(egui::Label::new(text).selectable(false));
                                    })
                                    .response
                                    .interact(egui::Sense::click());
                                if resp.clicked() {
                                    self.active_view = nav;
                                    self.platform_cache.ensure_loaded_sync_for_view(nav);
                                }
                            }
                        }
                    });

                ui.add_space(8.0);

                // Compact Popout Mini-Monitor Launcher Card
                egui::Frame::new()
                    .fill(surf_col)
                    .stroke(pill_border)
                    .corner_radius(CornerRadius::same(9))
                    .inner_margin(egui::Margin::symmetric(10, 8))
                    .show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Popout Mini-Monitor")
                                    .font(FontId::proportional(12.0))
                                    .strong(),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if self.show_popout_overlay
                                        && self.app.theme.popout_mode != PopoutMode::Off
                                    {
                                        hig_status_pill(ui, "Active", hig_green(ui));
                                    } else {
                                        hig_status_pill(ui, "Off", hig_gray(ui));
                                    }
                                },
                            );
                        });
                        ui.add_space(4.0);
                        let toggle_lbl = if self.show_popout_overlay
                            && self.app.theme.popout_mode != PopoutMode::Off
                        {
                            "Dock Popout (On Desktop)"
                        } else {
                            "Launch Popout Window"
                        };
                        if ui
                            .button(toggle_lbl)
                            .on_hover_text(
                                "Open or close the independent floating .nexusskin desktop mini-monitor window",
                            )
                            .clicked()
                        {
                            self.toggle_popout_mode();
                        }
                        ui.add_space(3.0);
                        let cur_skin_label =
                            popout_skin_short_label(self.app.theme.active_skin_id.as_str());
                        egui::ComboBox::from_id_salt("sidebar_popout_skin_combo")
                            .width(ui.available_width())
                            .selected_text(format!("Skin: {cur_skin_label}"))
                            .show_ui(ui, |ui| {
                                for (skin_id, skin_label) in [
                                    ("com.nexus.console", "Console (Default)"),
                                    ("com.nexus.traces", "Traces (Graphs)"),
                                    ("winamp", "Winamp (Positional)"),
                                    ("mediaPlayer", "Media Player"),
                                    ("com.nexus.wintaskman", "Win2000 Task Manager"),
                                ] {
                                    if ui
                                        .selectable_label(
                                            self.app.theme.active_skin_id.as_str() == skin_id,
                                            skin_label,
                                        )
                                        .clicked()
                                    {
                                        self.app.theme.active_skin_id =
                                            CompactString::from(skin_id);
                                        prefs_dirty = true;
                                    }
                                }
                            });
                        ui.add_space(2.0);
                        if ui
                            .small_button("Reset Popout Position")
                            .on_hover_text(
                                "Move the floating desktop mini-monitor back to its default screen position",
                            )
                            .clicked()
                        {
                            self.popout_pos_x = None;
                            self.popout_pos_y = None;
                            self.popout_pos_dirty_at = None;
                            if let Some(pop_win) = &self.popout_window {
                                pop_win.set_outer_position(winit::dpi::LogicalPosition::new(
                                    960.0, 96.0,
                                ));
                            }
                            prefs_dirty = true;
                        }
                    });

                ui.add_space(6.0);

                // Compact Theme Preset Selector
                egui::Frame::new()
                    .fill(surf_col)
                    .stroke(pill_border)
                    .corner_radius(CornerRadius::same(9))
                    .inner_margin(egui::Margin::symmetric(10, 8))
                    .show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.label(
                            RichText::new(format!(
                                "Theme Preset ({})",
                                CrystalGlassPreset::ALL.len()
                            ))
                            .font(FontId::proportional(11.5))
                            .strong(),
                        );
                        ui.add_space(3.0);
                        let active_name = meta.name.to_string();
                        egui::ComboBox::from_id_salt("sidebar_theme_preset_combo")
                            .width(ui.available_width())
                            .selected_text(&active_name)
                            .show_ui(ui, |ui| {
                                for preset in CrystalGlassPreset::ALL {
                                    let pmeta = preset.metadata();
                                    let selected = self.app.theme.glass_preset == preset
                                        && self.app.theme.custom_preset.is_none();
                                    ui.horizontal(|ui| {
                                        let (sw_rect, _) = ui.allocate_exact_size(
                                            Vec2::new(10.0, 10.0),
                                            egui::Sense::hover(),
                                        );
                                        ui.painter().rect_filled(
                                            sw_rect,
                                            CornerRadius::same(2),
                                            srgb_to_color32(pmeta.accent_srgb),
                                        );
                                        if ui
                                            .selectable_label(selected, pmeta.name.as_str())
                                            .clicked()
                                        {
                                            self.app.theme.glass_preset = preset;
                                            self.app.theme.custom_preset = None;
                                            self.color_wheel.sync_from_meta(&pmeta);
                                            prefs_dirty = true;
                                        }
                                    });
                                }
                            });
                    });
            });

        // Right Inspector Drawer
        if self.app.process_table.inspector_open
            && let Some(sel_key) = self.app.process_table.selected_key
            && let Some(dossier) = self
                .app
                .process_table
                .inspect_process(&self.snapshot, sel_key)
        {
            egui::Panel::right("nexus_process_inspector")
                .default_size(310.0)
                .show(ui, |ui| {
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Process Inspector")
                                    .font(FontId::proportional(15.0))
                                    .strong(),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.button("Close").clicked() {
                                        self.app.process_table.inspector_open = false;
                                    }
                                },
                            );
                        });
                        ui.add_space(4.0);
                        egui::Grid::new("inspector_facts_grid")
                            .num_columns(2)
                            .spacing([12.0, 6.0])
                            .show(ui, |ui| {
                                ui.label(
                                    RichText::new("Name").color(ui.visuals().weak_text_color()),
                                );
                                ui.label(RichText::new(dossier.name.as_str()).strong());
                                ui.end_row();
                                ui.label(
                                    RichText::new("PID").color(ui.visuals().weak_text_color()),
                                );
                                ui.label(dossier.key.pid.to_string());
                                ui.end_row();
                                ui.label(
                                    RichText::new("Creation Token")
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(format!("{} us", dossier.key.creation_token_us));
                                ui.end_row();
                                ui.label(
                                    RichText::new("Parent PID")
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(dossier.ppid.to_string());
                                ui.end_row();
                                ui.label(
                                    RichText::new("Coalition Group")
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(dossier.group_key.as_str());
                                ui.end_row();
                                ui.label(
                                    RichText::new("Status").color(ui.visuals().weak_text_color()),
                                );
                                let st_col = if dossier.status_label == "Exited" {
                                    hig_red(ui)
                                } else {
                                    hig_green(ui)
                                };
                                hig_status_pill(ui, dossier.status_label.as_str(), st_col);
                                ui.end_row();
                                ui.label(
                                    RichText::new("CPU Load").color(ui.visuals().weak_text_color()),
                                );
                                ui.label(&dossier.cpu_formatted);
                                ui.end_row();
                                ui.label(
                                    RichText::new("Phys Footprint")
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(&dossier.phys_footprint_formatted);
                                ui.end_row();
                                ui.label(
                                    RichText::new("Resident (RSS)")
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(&dossier.resident_formatted);
                                ui.end_row();
                            });

                        ui.add_space(8.0);
                        hig_card_header(
                            ui,
                            "PID-Reuse-Safe Controls",
                            "Double-verifies PID and creation token before executing dry-run actions.",
                            accent,
                        );
                        ui.horizontal_wrapped(|ui| {
                            if ui.button("Quit (Dry)").clicked() {
                                self.run_process_action(
                                    dossier.key,
                                    ProcessActionType::Terminate,
                                    None,
                                    None,
                                );
                            }
                            if ui.button("Force Quit (Dry)").clicked() {
                                self.run_process_action(
                                    dossier.key,
                                    ProcessActionType::ForceKill,
                                    None,
                                    None,
                                );
                            }
                            if ui.button("Suspend (Dry)").clicked() {
                                self.run_process_action(
                                    dossier.key,
                                    ProcessActionType::Suspend,
                                    None,
                                    None,
                                );
                            }
                            if ui.button("Resume (Dry)").clicked() {
                                self.run_process_action(
                                    dossier.key,
                                    ProcessActionType::Resume,
                                    None,
                                    None,
                                );
                            }
                        });
                        ui.add_space(4.0);
                        ui.horizontal_wrapped(|ui| {
                            for &nice in &PRIORITY_PRESET_VALUES {
                                if ui.button(format!("Nice {nice:+}")).clicked() {
                                    self.run_process_action(
                                        dossier.key,
                                        ProcessActionType::SetPriority,
                                        None,
                                        Some(nice),
                                    );
                                }
                            }
                        });
                    });
                });
        }

        // Right Non-Modal Tile Configure Drawer (`PageEditSession`) in Summary View
        if self.active_view == GuiView::Dest(Destination::Summary)
            && self.page_edit.active
            && let Some(edit_tid) = self.editing_tile_id.clone()
        {
            self.draw_tile_configure_drawer(ui, &edit_tid, accent);
        }

        // Main Central Panel dispatching all 18 views
        egui::CentralPanel::default().show(ui, |ui| {
            self.draw_vfd_meter_strip(ui, accent);
            ui.add_space(6.0);

            match self.active_view {
                GuiView::Dest(Destination::Summary) => {
                    if self.draw_summary_view(ui, accent) {
                        prefs_dirty = true;
                    }
                }
                GuiView::Dest(Destination::Performance) => self.draw_performance_view(ui, accent),
                GuiView::Dest(Destination::Processes) => {
                    if self.draw_processes_view(ui, accent) {
                        prefs_dirty = true;
                    }
                }
                GuiView::Dest(Destination::Services) => {
                    draw_services_view(
                        ui,
                        &mut self.platform_cache,
                        &mut self.action_status,
                        accent,
                    );
                }
                GuiView::Dest(Destination::Startup) => {
                    draw_startup_view(
                        ui,
                        &mut self.platform_cache,
                        &mut self.action_status,
                        accent,
                    );
                }
                GuiView::Dest(Destination::Connections) => {
                    draw_connections_view(ui, &mut self.platform_cache, accent);
                }
                GuiView::WirelessRadar => {
                    draw_wireless_radar_view(
                        ui,
                        &mut self.platform_cache,
                        &self.wifi_async_slot,
                        &self.lan_async_slot,
                        accent,
                    );
                }
                GuiView::Dest(Destination::AppleSilicon) => {
                    self.draw_apple_silicon_view(ui, accent);
                }
                GuiView::Dest(Destination::DiskSpace) => self.draw_disk_space_view(ui, accent),
                GuiView::Dest(Destination::FlightRecorder) => {
                    if self.draw_flight_recorder_view(ui, accent) {
                        prefs_dirty = true;
                    }
                }
                GuiView::Dest(Destination::Benchmarks) => self.draw_benchmarks_view(ui, accent),
                GuiView::Dest(Destination::SystemInfo) => {
                    if self.storage_inspector.is_empty() && !self.storage_inspector_loading {
                        self.storage_inspector_loading = true;
                        let slot = Arc::clone(&self.storage_inspector_slot);
                        std::thread::spawn(move || {
                            let data = collect_storage_inspector();
                            *slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(data);
                        });
                    }
                    draw_system_info_and_health_view(
                        ui,
                        &self.snapshot,
                        &mut self.platform_cache,
                        &self.storage_inspector,
                        &self.app.theme,
                        accent,
                    );
                }
                GuiView::Dest(Destination::Users) => {
                    draw_users_view(ui, &mut self.platform_cache, accent);
                }
                GuiView::Dest(Destination::InstalledApps) => {
                    if draw_installed_apps_view(ui, &mut self.platform_cache, accent) {
                        prefs_dirty = true;
                    }
                }
                GuiView::Dest(Destination::Drivers) => {
                    draw_drivers_view(ui, &mut self.platform_cache, accent);
                }
                GuiView::Dest(Destination::PowerAndFreq) => {
                    let prev_sleep = self.sleep_preventing;
                    draw_power_and_freq_view(
                        ui,
                        &self.snapshot,
                        &mut self.sleep_preventing,
                        &mut self.perf_profiles,
                        &mut self.action_status,
                        accent,
                    );
                    if self.sleep_preventing != prev_sleep {
                        prefs_dirty = true;
                    }
                }
                GuiView::Dest(Destination::Settings) => {
                    if draw_settings_view(
                        ui,
                        &mut self.app.theme,
                        &mut self.color_wheel,
                        &mut self.saved_custom_presets,
                        &mut self.preset_json_buf,
                        &mut self.quiet_hours,
                        &mut self.autobalance,
                        &mut self.idle_throttle,
                        &mut self.alerts_cpu_thresh,
                        &mut self.alerts_mem_thresh,
                        &mut self.alerts_temp_thresh,
                        &mut self.alerts_disk_thresh,
                        &mut self.alerts_webhook_buf,
                        &rt_state,
                        &mut self.remote_server,
                        &mut self.remote_allow_cgnat,
                        &mut self.remote_advertise_bonjour,
                        self.tokio_rt.handle(),
                        &mut self.action_status,
                        accent,
                    ) {
                        prefs_dirty = true;
                    }
                }
                GuiView::Honesty => self.draw_honesty_view(ui, accent),
            }
        });

        // Cmd+K Command Palette Modal
        if self.command_palette_open {
            self.draw_command_palette_modal(ui, accent);
        }

        if prefs_dirty {
            self.save_preferences();
        }
    }

    fn draw_native_popout_window(&mut self, pop_win: &Arc<Window>, event_loop: &ActiveEventLoop) {
        if !self.window_visible {
            let now = Instant::now();
            let dt_s = now
                .duration_since(self.last_frame_instant)
                .as_secs_f64()
                .clamp(0.001, 0.25);
            self.last_frame_instant = now;
            self.app.step_meters(&self.snapshot, dt_s);
        }

        self.popout_frame_count += 1;
        if self.popout_screenshot_mode && !self.screenshot_saved && self.popout_frame_count >= 5 {
            let out_path = self.screenshot_out.clone();
            self.popout_egui_ctx
                .send_viewport_cmd(egui::ViewportCommand::Screenshot(
                    egui::ScreenshotCallback::new(move |image: Arc<egui::ColorImage>| {
                        if let Some(path) = &out_path {
                            save_color_image_png(&image, path);
                        }
                    }),
                ));
        }

        let Some(mut raw_input) = self
            .popout_egui_state
            .as_mut()
            .map(|st| st.take_egui_input(pop_win))
        else {
            return;
        };

        let pop_ctx = self.popout_egui_ctx.clone();
        let mut pop_dirty = false;
        let mut full_output = pop_ctx.run_ui(raw_input.take(), |ui| {
            pop_dirty = self.draw_popout_window_contents(ui, pop_win);
        });

        if let Some(st) = self.popout_egui_state.as_mut() {
            st.handle_platform_output_with_event_loop(
                pop_win,
                event_loop,
                full_output.platform_output,
            );
        }

        let mut screenshot_callbacks = Vec::new();
        for (vp_id, vp_output) in full_output.viewport_output {
            if vp_id == ViewportId::ROOT {
                for cmd in vp_output.commands {
                    if let egui::ViewportCommand::Screenshot(cb) = cmd {
                        screenshot_callbacks.push(cb);
                    }
                }
            }
        }

        let primitives = self
            .popout_egui_ctx
            .tessellate(full_output.shapes, full_output.pixels_per_point);
        if let Some(pop_painter) = self.popout_painter.as_mut() {
            let _ = pop_painter.paint_and_update_textures(
                ViewportId::ROOT,
                full_output.pixels_per_point,
                [0.0, 0.0, 0.0, 0.0],
                &primitives,
                &mut full_output.textures_delta,
                screenshot_callbacks,
                pop_win,
            );
            if self.popout_screenshot_mode
                && !self.screenshot_saved
                && self.popout_frame_count >= 5
                && let Some(rs) = pop_painter.render_state()
            {
                let _ = rs.device.poll(wgpu::PollType::wait_indefinitely());
                self.screenshot_saved = true;
            }
        }

        if pop_dirty {
            self.save_preferences();
        }
        self.sync_popout_window(event_loop);
    }

    fn draw_popout_window_contents(&mut self, ui: &mut egui::Ui, pop_win: &Arc<Window>) -> bool {
        let mut dirty = false;
        let accent = self.active_accent_color32();
        let pop: ResolvedPopoutView = self
            .app
            .theme
            .resolve_popout(self.popout_hovered, self.gaming_mode.active);

        let alpha = pop.effective_opacity;
        let apply_alpha = |rgba: [f32; 4]| -> Color32 {
            srgb_rgba_to_color32([
                rgba[0],
                rgba[1],
                rgba[2],
                (rgba[3] * alpha).clamp(0.15, 1.0),
            ])
        };
        let win_alpha = if alpha >= 0.70 {
            (pop.window_bg_rgba[3].max(0.95) * alpha.max(0.94)).clamp(0.88, 1.0)
        } else {
            (pop.window_bg_rgba[3] * alpha).clamp(0.35, 1.0)
        };
        let win_bg = srgb_rgba_to_color32([
            pop.window_bg_rgba[0],
            pop.window_bg_rgba[1],
            pop.window_bg_rgba[2],
            win_alpha,
        ]);
        let win_stroke = apply_alpha(pop.window_stroke_rgba);
        let tile_bg = apply_alpha(pop.tile_bg_rgba);
        let tile_stroke = apply_alpha(pop.tile_stroke_rgba);
        let title_ink = apply_alpha(pop.title_ink_rgba);
        let primary_ink = apply_alpha(pop.primary_ink_rgba);
        let secondary_ink = apply_alpha(pop.secondary_ink_rgba);

        let mut visuals =
            if pop.window_bg_rgba[0] + pop.window_bg_rgba[1] + pop.window_bg_rgba[2] < 1.5 {
                egui::Visuals::dark()
            } else {
                egui::Visuals::light()
            };
        visuals.panel_fill = win_bg;
        visuals.window_fill = win_bg;
        visuals.extreme_bg_color = tile_bg;
        visuals.faint_bg_color = tile_bg;
        visuals.widgets.noninteractive.bg_fill = tile_bg;
        visuals.widgets.noninteractive.fg_stroke = Stroke::new(1.0, primary_ink);
        visuals.widgets.inactive.bg_fill = tile_bg;
        visuals.widgets.inactive.fg_stroke = Stroke::new(1.0, primary_ink);
        visuals.widgets.hovered.bg_fill =
            Color32::from_rgba_unmultiplied(accent.r(), accent.g(), accent.b(), 64);
        visuals.widgets.hovered.fg_stroke = Stroke::new(1.0, Color32::WHITE);
        visuals.widgets.active.bg_fill =
            Color32::from_rgba_unmultiplied(accent.r(), accent.g(), accent.b(), 110);
        visuals.widgets.active.fg_stroke = Stroke::new(1.0, Color32::WHITE);
        visuals.selection.bg_fill =
            Color32::from_rgba_unmultiplied(accent.r(), accent.g(), accent.b(), 96);
        visuals.selection.stroke = Stroke::new(1.0, Color32::WHITE);
        ui.ctx().set_visuals(visuals);

        let net_mbps = sum_measured_metrics(
            self.snapshot
                .networks
                .iter()
                .flat_map(|n| [&n.rx_bytes_per_sec, &n.tx_bytes_per_sec]),
        )
        .map(|b| b * 8.0 / 1_000_000.0);

        egui::CentralPanel::default()
            .frame(egui::Frame::NONE)
            .show(ui, |ui| {
                let bg_drag_resp = ui.interact(
                    ui.max_rect(),
                    ui.make_persistent_id("popout_bg_drag"),
                    egui::Sense::drag(),
                );
                let mut header_drag_resp: Option<egui::Response> = None;

                egui::Frame::new()
                    .fill(win_bg)
                    .stroke(Stroke::new(1.5, win_stroke))
                    .corner_radius(CornerRadius::same(pop.corner_radius.clamp(0.0, 20.0) as u8))
                    .inner_margin(egui::Margin::same(8))
                    .show(ui, |ui| {
                        ui.set_min_size(ui.available_size());

                        // Skin Header Bar + Close / Dock button
                        egui::Frame::new()
                            .fill(apply_alpha(pop.header_bg_rgba))
                            .corner_radius(CornerRadius::same(4))
                            .inner_margin(egui::Margin::symmetric(6, 4))
                            .show(ui, |ui| {
                                ui.horizontal(|ui| {
                                    let hdr = ui.add(
                                        egui::Label::new(
                                            RichText::new(pop.skin_name.as_str())
                                                .font(FontId::new(
                                                    11.0,
                                                    FontFamily::Name("michroma".into()),
                                                ))
                                                .color(title_ink),
                                        )
                                        .sense(egui::Sense::drag()),
                                    );
                                    header_drag_resp = Some(hdr);
                                    ui.with_layout(
                                        egui::Layout::right_to_left(egui::Align::Center),
                                        |ui| {
                                            if ui
                                                .small_button("Close / Dock")
                                                .on_hover_text(
                                                    "Close floating desktop mini-monitor",
                                                )
                                                .clicked()
                                            {
                                                self.show_popout_overlay = false;
                                                self.app.theme.popout_mode = PopoutMode::Off;
                                                dirty = true;
                                            }
                                        },
                                    );
                                });
                            });

                        ui.add_space(4.0);

                        // Skin Selector Pills
                        ui.horizontal_wrapped(|ui| {
                            ui.spacing_mut().item_spacing.x = 3.0;
                            for (skin_id, short_lbl) in [
                                ("com.nexus.console", "Console"),
                                ("com.nexus.traces", "Traces"),
                                ("winamp", "Winamp"),
                                ("mediaPlayer", "WMP"),
                                ("com.nexus.wintaskman", "Win2000"),
                            ] {
                                let is_cur = self.app.theme.active_skin_id.as_str() == skin_id;
                                if ui
                                    .selectable_label(
                                        is_cur,
                                        RichText::new(short_lbl).font(FontId::proportional(10.5)),
                                    )
                                    .clicked()
                                {
                                    self.app.theme.active_skin_id = CompactString::from(skin_id);
                                    dirty = true;
                                }
                            }
                        });

                        // Window Level Pills
                        ui.horizontal(|ui| {
                            ui.spacing_mut().item_spacing.x = 4.0;
                            for (pm, lbl) in [
                                (PopoutMode::TopMost, "TopMost"),
                                (PopoutMode::Normal, "Normal"),
                                (PopoutMode::Desktop, "Desktop"),
                            ] {
                                if ui
                                    .selectable_label(
                                        self.app.theme.popout_mode == pm,
                                        RichText::new(lbl).font(FontId::proportional(10.5)),
                                    )
                                    .clicked()
                                {
                                    self.app.theme.popout_mode = pm;
                                    dirty = true;
                                }
                            }
                        });

                        // Opacity Slider + Hover=100%
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Opacity")
                                    .font(FontId::proportional(10.5))
                                    .color(secondary_ink),
                            );
                            if ui
                                .add(
                                    egui::Slider::new(
                                        &mut self.app.theme.popout_opacity,
                                        0.20..=1.0,
                                    )
                                    .show_value(false),
                                )
                                .changed()
                            {
                                dirty = true;
                            }
                            if ui
                                .checkbox(
                                    &mut self.app.theme.popout_opaque_on_hover,
                                    RichText::new("Hover=100%").font(FontId::proportional(10.5)),
                                )
                                .changed()
                            {
                                dirty = true;
                            }
                        });

                        ui.add_space(3.0);

                        // .nexusskin Quadrants Grid with VFD digital readouts
                        let cols = (pop.columns as usize).clamp(1, 2);
                        egui::ScrollArea::vertical()
                            .id_salt("popout_quadrants_scroll")
                            .show(ui, |ui| {
                                ui.columns(cols, |columns| {
                                    for (idx, q) in pop.quadrants.iter().enumerate() {
                                        let col_ui = &mut columns[idx % cols];
                                        let (val_str, norm_opt) = match q.as_str() {
                                            "cpu" => (
                                                self.snapshot
                                                    .system
                                                    .cpu_percent
                                                    .format_with_unit(1, "%"),
                                                Some(self.app.cpu_meter.displayed_level),
                                            ),
                                            "memory" => (
                                                self.snapshot
                                                    .system
                                                    .mem_used_percent
                                                    .format_with_unit(1, "%"),
                                                Some(self.app.mem_meter.displayed_level),
                                            ),
                                            "gpu" => (
                                                self.snapshot
                                                    .system
                                                    .gpu_utilization_percent
                                                    .format_with_unit(1, "%"),
                                                Some(self.app.gpu_meter.displayed_level),
                                            ),
                                            "thermal" => {
                                                let m = &self
                                                    .snapshot
                                                    .hardware_telemetry
                                                    .thermals
                                                    .max_die_celsius;
                                                (m.format_with_unit(1, "C"), None)
                                            }
                                            "power" => {
                                                let m = &self
                                                    .snapshot
                                                    .hardware_telemetry
                                                    .soc_power
                                                    .package_watts;
                                                (m.format_with_unit(1, "W"), None)
                                            }
                                            "disk" => {
                                                let str_v =
                                                    self.snapshot.disks.first().map_or_else(
                                                        || "-".to_string(),
                                                        |d| {
                                                            d.root_used_percent
                                                                .format_with_unit(0, "%")
                                                        },
                                                    );
                                                (str_v, None)
                                            }
                                            "network" => {
                                                let str_v = net_mbps.map_or_else(
                                                    || "-".to_string(),
                                                    |m| format!("{m:.1}M"),
                                                );
                                                (str_v, None)
                                            }
                                            _ => ("-".to_string(), None),
                                        };
                                        let val_str = if val_str.eq_ignore_ascii_case("Unavailable")
                                        {
                                            "—".to_string()
                                        } else {
                                            val_str
                                        };
                                        let q_accent = pop
                                            .channel_colors
                                            .get(q)
                                            .and_then(|c| c.lit.as_deref().or(c.accent.as_deref()))
                                            .and_then(crate::theme::parse_hex_rgba)
                                            .map(apply_alpha)
                                            .unwrap_or(accent);
                                        egui::Frame::new()
                                            .fill(tile_bg)
                                            .stroke(Stroke::new(1.0, tile_stroke))
                                            .corner_radius(CornerRadius::same(4))
                                            .inner_margin(egui::Margin::symmetric(6, 4))
                                            .show(col_ui, |ui| {
                                                ui.set_min_width(ui.available_width());
                                                ui.horizontal(|ui| {
                                                    ui.label(
                                                        RichText::new(q.to_ascii_uppercase())
                                                            .font(FontId::new(
                                                                9.5,
                                                                FontFamily::Name("michroma".into()),
                                                            ))
                                                            .color(primary_ink),
                                                    );
                                                    ui.with_layout(
                                                        egui::Layout::right_to_left(
                                                            egui::Align::Center,
                                                        ),
                                                        |ui| {
                                                            ui.label(
                                                                RichText::new(val_str)
                                                                    .font(FontId::new(
                                                                        12.5,
                                                                        FontFamily::Name(
                                                                            "led".into(),
                                                                        ),
                                                                    ))
                                                                    .color(q_accent),
                                                            );
                                                        },
                                                    );
                                                });
                                                if let Some(lvl) = norm_opt {
                                                    let (bar_r, _) = ui.allocate_exact_size(
                                                        Vec2::new(
                                                            ui.available_width().max(32.0),
                                                            5.0,
                                                        ),
                                                        egui::Sense::hover(),
                                                    );
                                                    let fill_w = bar_r.width()
                                                        * (lvl as f32).clamp(0.0, 1.0);
                                                    ui.painter().rect_filled(
                                                        bar_r,
                                                        CornerRadius::same(1),
                                                        apply_alpha(pop.ground_rgba),
                                                    );
                                                    ui.painter().rect_filled(
                                                        Rect::from_min_size(
                                                            bar_r.min,
                                                            Vec2::new(fill_w, 5.0),
                                                        ),
                                                        CornerRadius::same(1),
                                                        q_accent,
                                                    );
                                                }
                                            });
                                        col_ui.add_space(3.0);
                                    }
                                });
                            });
                    });

                let hdr_dragged = header_drag_resp
                    .is_some_and(|r| r.drag_started_by(egui::PointerButton::Primary));
                if hdr_dragged || bg_drag_resp.drag_started_by(egui::PointerButton::Primary) {
                    let _ = pop_win.drag_window();
                }
            });

        dirty
    }

    fn draw_command_palette_modal(&mut self, ui: &mut egui::Ui, accent: Color32) {
        let mut open = self.command_palette_open;
        egui::Window::new("Command Palette (Cmd+K / Ctrl+K)")
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_TOP, Vec2::new(0.0, 72.0))
            .default_size([440.0, 320.0])
            .show(ui.ctx(), |ui| {
                ui.add(
                    egui::TextEdit::singleline(&mut self.command_palette_query)
                        .hint_text("Type a destination or action...")
                        .desired_width(ui.available_width()),
                );
                ui.separator();
                let q = self.command_palette_query.trim().to_ascii_lowercase();
                egui::ScrollArea::vertical()
                    .max_height(250.0)
                    .show(ui, |ui| {
                        for nav in GuiView::ALL_SIDEBAR {
                            let label = format!("Go to: {}", nav.title());
                            if (q.is_empty() || label.to_ascii_lowercase().contains(&q))
                                && ui
                                    .selectable_label(false, RichText::new(label).color(accent))
                                    .clicked()
                            {
                                self.active_view = nav;
                                self.platform_cache.ensure_loaded_sync_for_view(nav);
                                self.command_palette_open = false;
                            }
                        }
                        let actions: [(&str, u8); 5] = [
                            ("Action: Toggle Gaming Mode", 0),
                            ("Action: Toggle Popout Mini-Monitor Window", 1),
                            ("Action: Toggle Edit Pro Tiles Mode", 2),
                            ("Action: Toggle Prevent Sleep (caffeinate -di)", 3),
                            ("Action: Launch F12 Tempest Tunnel Screensaver", 4),
                        ];
                        for (lbl, code) in actions {
                            if (q.is_empty() || lbl.to_ascii_lowercase().contains(&q))
                                && ui.selectable_label(false, lbl).clicked()
                            {
                                match code {
                                    0 => self.toggle_gaming_mode(),
                                    1 => self.toggle_popout_mode(),
                                    2 => {
                                        self.active_view = GuiView::Dest(Destination::Summary);
                                        if self.page_edit.active {
                                            self.app.page_engine = self.page_edit.commit();
                                            self.editing_tile_id = None;
                                            self.dashboard_mode = 1;
                                            self.save_preferences();
                                        } else {
                                            self.page_edit.begin(self.app.page_engine.clone());
                                            self.dashboard_mode = 2;
                                        }
                                    }
                                    3 => {
                                        self.sleep_preventing = !self.sleep_preventing;
                                        self.save_preferences();
                                    }
                                    _ => self.screensaver_active = true,
                                }
                                self.command_palette_open = false;
                            }
                        }
                    });
            });
        if !open {
            self.command_palette_open = false;
        }
    }

    fn draw_vfd_meter_strip(&mut self, ui: &mut egui::Ui, accent: Color32) {
        let popout = self.app.theme.resolve_popout(true, self.gaming_mode.active);
        let positional =
            popout.positional_palette || self.app.theme.display_mode == DisplayMode::Color;

        ui.columns(3, |cols| {
            draw_single_vfd_bar(
                &mut cols[0],
                "CPU LOAD",
                &self.snapshot.system.cpu_percent.format_with_unit(1, "%"),
                self.app.cpu_meter.displayed_level,
                positional,
                accent,
                &self.app.theme,
            );
            draw_single_vfd_bar(
                &mut cols[1],
                "MEMORY USED",
                &self
                    .snapshot
                    .system
                    .mem_used_percent
                    .format_with_unit(1, "%"),
                self.app.mem_meter.displayed_level,
                positional,
                accent,
                &self.app.theme,
            );
            draw_single_vfd_bar(
                &mut cols[2],
                "GPU ACTIVE",
                &self
                    .snapshot
                    .system
                    .gpu_utilization_percent
                    .format_with_unit(1, "%"),
                self.app.gpu_meter.displayed_level,
                positional,
                accent,
                &self.app.theme,
            );
        });
    }

    fn draw_tile_configure_drawer(&mut self, ui: &mut egui::Ui, tile_id: &str, accent: Color32) {
        let Some(tile_snapshot) = self
            .page_edit
            .current
            .tiles
            .iter()
            .find(|t| t.id.as_str() == tile_id)
            .cloned()
        else {
            self.editing_tile_id = None;
            return;
        };

        let discovered = SensorRegistry::discover(&self.snapshot);
        let props = tile_snapshot.face.properties();
        let mut close_drawer = false;
        let mut updated_title = tile_snapshot.title.to_string();
        let mut updated_col_span = tile_snapshot.col_span;
        let mut updated_face = tile_snapshot.face;
        let mut updated_cfg = tile_snapshot.config.clone();
        let mut remove_sensor_idx: Option<usize> = None;
        let mut remove_total_idx: Option<usize> = None;
        let mut remove_low_idx: Option<usize> = None;
        let mut bind_sensor: Option<SensorPath> = None;

        egui::Panel::right("nexus_tile_configure_drawer")
            .default_size(340.0)
            .show(ui, |ui| {
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new(format!("Configure: {}", tile_snapshot.title))
                                .font(FontId::proportional(15.0))
                                .strong(),
                        );
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            if ui.button("Close").clicked() {
                                close_drawer = true;
                            }
                        });
                    });
                    ui.add_space(4.0);

                    egui::ScrollArea::vertical()
                        .id_salt("tile_configure_scroll")
                        .show(ui, |ui| {
                            ui.horizontal(|ui| {
                                ui.label("Title:");
                                ui.add(
                                    egui::TextEdit::singleline(&mut updated_title)
                                        .desired_width(180.0),
                                );
                            });
                            ui.checkbox(&mut updated_cfg.show_title, "Show Tile Title");

                            ui.horizontal(|ui| {
                                ui.label("Face Type:");
                                egui::ComboBox::from_id_salt("drawer_face_combo")
                                    .selected_text(updated_face.label())
                                    .show_ui(ui, |ui| {
                                        for fk in FaceKind::ALL {
                                            ui.selectable_value(&mut updated_face, fk, fk.label());
                                        }
                                    });
                            });

                            ui.horizontal(|ui| {
                                ui.label("Column Span:");
                                ui.selectable_value(&mut updated_col_span, 1, "1 Col (Half)");
                                ui.selectable_value(&mut updated_col_span, 2, "2 Cols (Full)");
                            });

                            ui.horizontal(|ui| {
                                ui.label("Min Update Interval:");
                                egui::ComboBox::from_id_salt("drawer_update_ms")
                                    .selected_text(if updated_cfg.min_update_ms == 0 {
                                        "Live".to_string()
                                    } else {
                                        format!("{} ms", updated_cfg.min_update_ms)
                                    })
                                    .show_ui(ui, |ui| {
                                        for (ms, lbl) in [
                                            (0u64, "Live (Default)"),
                                            (250, "250 ms"),
                                            (500, "500 ms"),
                                            (1000, "1000 ms"),
                                            (2000, "2000 ms"),
                                        ] {
                                            ui.selectable_value(
                                                &mut updated_cfg.min_update_ms,
                                                ms,
                                                lbl,
                                            );
                                        }
                                    });
                            });

                            // Face-specific options driven by FaceKind & FaceProperties
                            match updated_face {
                                FaceKind::CompressedHistoryGraph | FaceKind::BarChart => {
                                    ui.separator();
                                    ui.label(RichText::new("Chart Options").strong());
                                    ui.checkbox(&mut updated_cfg.show_legend, "Show Legend");
                                    ui.checkbox(&mut updated_cfg.grid_lines, "Grid Lines");
                                    ui.checkbox(&mut updated_cfg.stacked, "Stacked Series");
                                    if updated_face == FaceKind::CompressedHistoryGraph {
                                        ui.checkbox(&mut updated_cfg.smooth, "Smooth Curves");
                                        ui.add(
                                            egui::Slider::new(
                                                &mut updated_cfg.fill_opacity,
                                                0.0..=1.0,
                                            )
                                            .text("Fill Opacity"),
                                        );
                                    }
                                    ui.checkbox(&mut updated_cfg.range_auto_y, "Auto Y Range");
                                    if !updated_cfg.range_auto_y {
                                        ui.horizontal(|ui| {
                                            ui.label("From:");
                                            ui.add(egui::DragValue::new(
                                                &mut updated_cfg.range_from,
                                            ));
                                            ui.label("To:");
                                            ui.add(egui::DragValue::new(&mut updated_cfg.range_to));
                                        });
                                    }
                                }
                                FaceKind::PieChart => {
                                    ui.separator();
                                    ui.label(RichText::new("Pie / Ring Gauge Options").strong());
                                    ui.checkbox(&mut updated_cfg.show_legend, "Show Legend");
                                    ui.add(
                                        egui::Slider::new(
                                            &mut updated_cfg.pie_start_angle,
                                            -180.0..=180.0,
                                        )
                                        .text("Start Angle"),
                                    );
                                    ui.add(
                                        egui::Slider::new(
                                            &mut updated_cfg.pie_end_angle,
                                            -180.0..=360.0,
                                        )
                                        .text("End Angle"),
                                    );
                                }
                                FaceKind::ColorGrid | FaceKind::FaceGrid => {
                                    ui.separator();
                                    ui.label(RichText::new("Grid Options").strong());
                                    ui.add(
                                        egui::Slider::new(&mut updated_cfg.grid_columns, 2..=8)
                                            .text("Grid Columns"),
                                    );
                                    if updated_face == FaceKind::FaceGrid {
                                        ui.horizontal(|ui| {
                                            ui.label("Nested Face:");
                                            egui::ComboBox::from_id_salt("drawer_nested_face")
                                                .selected_text(updated_cfg.nested_face.label())
                                                .show_ui(ui, |ui| {
                                                    for fk in FaceKind::ALL
                                                        .into_iter()
                                                        .filter(|f| {
                                                            f.properties().allows_in_face_grid
                                                        })
                                                    {
                                                        ui.selectable_value(
                                                            &mut updated_cfg.nested_face,
                                                            fk,
                                                            fk.label(),
                                                        );
                                                    }
                                                });
                                        });
                                    }
                                }
                                _ => {}
                            }

                            if props.supports_sensors {
                                ui.separator();
                                ui.label(RichText::new("Bound Sensors").strong().color(accent));
                                for (idx, sp) in tile_snapshot.sensors.iter().enumerate() {
                                    let key = CompactString::from(sp.as_path_string());
                                    ui.horizontal(|ui| {
                                        if sp.is_group() {
                                            let cnt = sp.expand(&self.snapshot).len();
                                            ui.label(format!(
                                                "[Group] {} ({cnt} sensors)",
                                                sp.as_path_string()
                                            ));
                                        } else {
                                            ui.label(sp.as_path_string());
                                        }
                                        if props.supports_sensors_colors {
                                            let mut c = updated_cfg
                                                .sensor_colors
                                                .get(&key)
                                                .copied()
                                                .unwrap_or([accent.r(), accent.g(), accent.b()]);
                                            if ui.color_edit_button_srgb(&mut c).changed() {
                                                updated_cfg.sensor_colors.insert(key.clone(), c);
                                            }
                                        }
                                        if ui.small_button("x").clicked() {
                                            remove_sensor_idx = Some(idx);
                                        }
                                    });
                                }

                                if props.supports_total_sensors {
                                    ui.label(
                                        RichText::new("Total Sensor (Denominator)").small().strong(),
                                    );
                                    for (idx, sp) in updated_cfg.total_sensors.iter().enumerate() {
                                        ui.horizontal(|ui| {
                                            ui.label(sp.as_path_string());
                                            if ui.small_button("x").clicked() {
                                                remove_total_idx = Some(idx);
                                            }
                                        });
                                    }
                                }
                                if props.supports_low_priority_sensors {
                                    ui.label(
                                        RichText::new("Low-Priority Background Sensors")
                                            .small()
                                            .strong(),
                                    );
                                    for (idx, sp) in
                                        updated_cfg.low_priority_sensors.iter().enumerate()
                                    {
                                        ui.horizontal(|ui| {
                                            ui.label(sp.as_path_string());
                                            if ui.small_button("x").clicked() {
                                                remove_low_idx = Some(idx);
                                            }
                                        });
                                    }
                                }

                                ui.separator();
                                ui.label(
                                    RichText::new("3-Tier Sensor Picker")
                                        .strong()
                                        .color(accent),
                                );
                                if props.supports_total_sensors
                                    || props.supports_low_priority_sensors
                                {
                                    ui.horizontal(|ui| {
                                        ui.label("Bind To:");
                                        ui.selectable_value(
                                            &mut self.picker_bind_target,
                                            0,
                                            "Primary",
                                        );
                                        if props.supports_total_sensors {
                                            ui.selectable_value(
                                                &mut self.picker_bind_target,
                                                1,
                                                "Total",
                                            );
                                        }
                                        if props.supports_low_priority_sensors {
                                            ui.selectable_value(
                                                &mut self.picker_bind_target,
                                                2,
                                                "Low-Prio",
                                            );
                                        }
                                    });
                                } else {
                                    self.picker_bind_target = 0;
                                }

                                ui.horizontal(|ui| {
                                    ui.label("Filter:");
                                    ui.text_edit_singleline(&mut self.picker_search);
                                });

                                let pq = self.picker_search.trim().to_ascii_lowercase();
                                let filtered: Vec<_> = discovered
                                    .iter()
                                    .filter(|d| {
                                        pq.is_empty()
                                            || d.label.to_ascii_lowercase().contains(&pq)
                                            || d.path
                                                .as_path_string()
                                                .to_ascii_lowercase()
                                                .contains(&pq)
                                    })
                                    .collect();

                                let mut categories: Vec<String> = Vec::new();
                                for d in &filtered {
                                    let c = d.path.category.to_string();
                                    if !categories.contains(&c) {
                                        categories.push(c);
                                    }
                                }
                                if !categories.iter().any(|c| c == &self.picker_category)
                                    && let Some(first) = categories.first()
                                {
                                    self.picker_category = first.clone();
                                }

                                let mut instances: Vec<String> = Vec::new();
                                for d in filtered
                                    .iter()
                                    .filter(|d| d.path.category.as_str() == self.picker_category)
                                {
                                    let inst = d.path.instance.to_string();
                                    if !instances.contains(&inst) {
                                        instances.push(inst);
                                    }
                                }
                                if !instances.iter().any(|i| i == &self.picker_instance)
                                    && let Some(first) = instances.first()
                                {
                                    self.picker_instance = first.clone();
                                }

                                ui.columns(3, |cols| {
                                    cols[0].label(RichText::new("Category").small().strong());
                                    egui::ScrollArea::vertical()
                                        .id_salt("picker_col_cat")
                                        .max_height(155.0)
                                        .show(&mut cols[0], |ui| {
                                            for cat in &categories {
                                                if ui
                                                    .selectable_label(
                                                        self.picker_category == *cat,
                                                        cat.as_str(),
                                                    )
                                                    .clicked()
                                                {
                                                    self.picker_category = cat.clone();
                                                }
                                            }
                                        });

                                    cols[1].label(
                                        RichText::new("Instance / [Group]").small().strong(),
                                    );
                                    egui::ScrollArea::vertical()
                                        .id_salt("picker_col_inst")
                                        .max_height(155.0)
                                        .show(&mut cols[1], |ui| {
                                            for inst in &instances {
                                                if ui
                                                    .selectable_label(
                                                        self.picker_instance == *inst,
                                                        inst.as_str(),
                                                    )
                                                    .clicked()
                                                {
                                                    self.picker_instance = inst.clone();
                                                }
                                            }
                                        });

                                    cols[2].label(RichText::new("Metric (Click +)").small().strong());
                                    let requires_numeric = !matches!(
                                        updated_face,
                                        FaceKind::KpiTextReadout
                                            | FaceKind::ProcessTable
                                            | FaceKind::ApplicationsTable
                                    );
                                    egui::ScrollArea::vertical()
                                        .id_salt("picker_col_met")
                                        .max_height(155.0)
                                        .show(&mut cols[2], |ui| {
                                            for d in filtered.iter().filter(|d| {
                                                d.path.category.as_str() == self.picker_category
                                                    && d.path.instance.as_str()
                                                        == self.picker_instance
                                            }) {
                                                let can_bind = !requires_numeric || d.is_numeric;
                                                let btn_text = if d.is_numeric {
                                                    format!("+ {}", d.path.metric)
                                                } else {
                                                    format!("+ {} [Text]", d.path.metric)
                                                };
                                                let resp = ui
                                                    .add_enabled(
                                                        can_bind,
                                                        egui::Button::new(btn_text),
                                                    )
                                                    .on_hover_text(d.label.as_str())
                                                    .on_disabled_hover_text(
                                                        "Switch face to 'Text Only (KPI)' to bind non-numeric text sensors.",
                                                    );
                                                if resp.clicked() {
                                                    bind_sensor = Some(d.path.clone());
                                                }
                                            }
                                        });
                                });
                            }
                        });
                });
            });

        if close_drawer {
            self.editing_tile_id = None;
        }
        let changed_basic = updated_title != tile_snapshot.title.as_str()
            || updated_col_span != tile_snapshot.col_span
            || updated_face != tile_snapshot.face
            || updated_cfg != tile_snapshot.config
            || remove_sensor_idx.is_some()
            || remove_total_idx.is_some()
            || remove_low_idx.is_some()
            || bind_sensor.is_some();

        if changed_basic {
            self.page_edit.update_tile(tile_id, |t| {
                t.title = CompactString::from(updated_title);
                t.col_span = updated_col_span.clamp(1, 2);
                t.face = updated_face;
                t.config = updated_cfg;
                if let Some(i) = remove_sensor_idx
                    && i < t.sensors.len()
                {
                    t.sensors.remove(i);
                }
                if let Some(i) = remove_total_idx
                    && i < t.config.total_sensors.len()
                {
                    t.config.total_sensors.remove(i);
                }
                if let Some(i) = remove_low_idx
                    && i < t.config.low_priority_sensors.len()
                {
                    t.config.low_priority_sensors.remove(i);
                }
                if let Some(sp) = bind_sensor {
                    match self.picker_bind_target {
                        1 => {
                            t.config.total_sensors.clear();
                            t.config.total_sensors.push(sp);
                        }
                        2 => {
                            if !t.config.low_priority_sensors.contains(&sp) {
                                t.config.low_priority_sensors.push(sp);
                            }
                        }
                        _ => {
                            if !t.sensors.contains(&sp) {
                                t.sensors.push(sp);
                            }
                        }
                    }
                }
            });
        }
    }

    fn draw_summary_view(&mut self, ui: &mut egui::Ui, accent: Color32) -> bool {
        let mut dirty = false;
        let popout = self.app.theme.resolve_popout(true, self.gaming_mode.active);
        let positional =
            popout.positional_palette || self.app.theme.display_mode == DisplayMode::Color;

        if self.platform_cache.volumes.is_empty() {
            self.platform_cache.volumes = collect_mounted_volumes();
        }

        // View Header + Right-Aligned Mode Switcher Pill Bar
        ui.horizontal_wrapped(|ui| {
            ui.vertical(|ui| {
                let dash_sub = if cfg!(target_os = "macos") {
                    "Real-time overview of your Mac's health, processor load, memory pressure, and active applications."
                } else if cfg!(target_os = "linux") {
                    "Real-time overview of your Linux host's health, processor load, memory pressure, and active processes."
                } else {
                    "Real-time overview of your Windows system's health, processor load, memory pressure, and active processes."
                };
                hig_view_header(ui, "Dashboard", dash_sub, accent);
            });
            ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                egui::Frame::new()
                    .fill(ui.visuals().extreme_bg_color)
                    .stroke(ui.visuals().widgets.noninteractive.bg_stroke)
                    .corner_radius(CornerRadius::same(8))
                    .inner_margin(egui::Margin::symmetric(6, 4))
                    .show(ui, |ui| {
                        ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                            for (mode_id, label, tip) in [
                                (
                                    0u8,
                                    "Guided Overview",
                                    "Show plain-English health summary, 4 environment pillars, and top active apps",
                                ),
                                (
                                    1u8,
                                    "Overview + Pro Tiles",
                                    "Show guided overview plus customizable Face x Sensor telemetry tiles",
                                ),
                                (
                                    2u8,
                                    "Edit Pro Tiles",
                                    "Add, reorder, remove, or configure Pro telemetry tiles",
                                ),
                            ] {
                                if ui
                                    .selectable_label(self.dashboard_mode == mode_id, label)
                                    .on_hover_text(tip)
                                    .clicked()
                                    && self.dashboard_mode != mode_id
                                {
                                    if mode_id == 2 && !self.page_edit.active {
                                        self.page_edit.begin(self.app.page_engine.clone());
                                    } else if mode_id != 2 && self.page_edit.active {
                                        self.app.page_engine = self.page_edit.commit();
                                        self.editing_tile_id = None;
                                        dirty = true;
                                    }
                                    self.dashboard_mode = mode_id;
                                }
                            }
                        });
                    });
            });
        });

        let snap = Arc::clone(&self.snapshot);
        let health = HealthScoring::evaluate_snapshot(&snap);
        let sp = &snap.hardware_telemetry.soc_power;
        let th = &snap.hardware_telemetry.thermals;
        let cpu_pct = measured_opt(&snap.system.cpu_percent);
        let mem_pct = measured_opt(&snap.system.mem_used_percent);
        let gpu_pct = measured_opt(&snap.system.gpu_utilization_percent);
        let die_c = measured_opt(&th.max_die_celsius);
        let pkg_w = measured_opt(&sp.package_watts);
        let warn_lvl = measured_opt(&th.warning_level).unwrap_or(0.0);

        let mut throttle_action_target: Option<(ProcessKey, String)> = None;
        let mut inspect_action_target: Option<ProcessKey> = None;

        egui::ScrollArea::vertical()
            .id_salt("dashboard_main_scroll")
            .max_height((ui.available_height() - 4.0).max(200.0))
            .show(ui, |ui| {
                // Section 1: Hero Status & Plain-English Environment Card
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal(|ui| {
                        draw_health_ring_mini(ui, health.overall_score, health.overall_level);
                        ui.add_space(12.0);

                        // Center Plain-English Summary
                        ui.vertical(|ui| {
                            ui.set_max_width((ui.available_width() - 210.0).max(260.0));
                            let host_noun = if cfg!(target_os = "macos") {
                                "Mac"
                            } else if cfg!(target_os = "linux") {
                                "Linux host"
                            } else {
                                "Windows system"
                            };
                            let (headline, headline_col) = if health.overall_score >= 80.0 {
                                (format!("Your {host_noun} is running smoothly."), hig_green(ui))
                            } else if health.overall_score >= 55.0 {
                                (
                                    format!("Your {host_noun} is under moderate load."),
                                    hig_amber(ui),
                                )
                            } else {
                                ("High resource pressure detected.".to_string(), hig_red(ui))
                            };
                            ui.horizontal_wrapped(|ui| {
                                ui.label(
                                    RichText::new(headline)
                                        .font(FontId::proportional(17.0))
                                        .strong()
                                        .color(ui.visuals().strong_text_color()),
                                );
                                hig_status_pill(
                                    ui,
                                    health.overall_level.as_str(),
                                    headline_col,
                                );
                            });

                            if let Some(hw) = self.platform_cache.hardware {
                                let up_text = SensorPath::new("os", "system", "uptime")
                                    .resolve_value_with_hw(&self.snapshot, Some(hw))
                                    .map(|v| v.format_display(""))
                                    .unwrap_or_else(|| "n/a".to_string());
                                ui.label(
                                    RichText::new(format!(
                                        "{} ({}) · {} {} · Host: {} · Uptime: {}",
                                        hw.chip_model,
                                        hw.architecture,
                                        hw.os_name,
                                        hw.os_version,
                                        hw.hostname,
                                        up_text
                                    ))
                                    .font(FontId::proportional(12.0))
                                    .color(ui.visuals().weak_text_color()),
                                );
                            }
                            ui.add_space(4.0);

                            let cpu_desc = match cpu_pct {
                                Some(c) if c < 35.0 => format!("light ({c:.1}%)"),
                                Some(c) if c < 75.0 => format!("moderate ({c:.1}%)"),
                                Some(c) => format!("heavy ({c:.1}%)"),
                                None => "unavailable".to_string(),
                            };
                            let swap_used_str = format_metric_bytes(&self.snapshot.system.swap_used_bytes);
                            let mem_desc = match mem_pct {
                                Some(m) if m < 75.0 => {
                                    format!("normal ({m:.1}% used, {swap_used_str} swap)")
                                }
                                Some(m) if m < 88.0 => {
                                    format!("elevated ({m:.1}% used, {swap_used_str} swap)")
                                }
                                Some(m) => format!("high ({m:.1}% used, {swap_used_str} swap)"),
                                None => "unavailable".to_string(),
                            };
                            let temp_desc = match die_c {
                                Some(t) if t < 65.0 => format!("cool ({t:.1} C)"),
                                Some(t) if t <= 85.0 => format!("warm ({t:.1} C)"),
                                Some(t) => format!("hot ({t:.1} C)"),
                                None => "unavailable".to_string(),
                            };
                            let p_cnt = self.snapshot.system.performance_core_count;
                            let e_cnt = self.snapshot.system.efficiency_core_count;
                            let proc_cnt = self.snapshot.processes.len();
                            let pwr_str = pkg_w.map_or_else(
                                || "unavailable".to_string(),
                                |w| format!("{w:.1} W"),
                            );
                            let throttle_str = if warn_lvl > 0.0 || die_c.is_some_and(|t| t > 95.0) {
                                "with thermal throttling pressure active."
                            } else {
                                "with no thermal throttling."
                            };

                            let summary_prose = format!(
                                "Processor load is {cpu_desc}, memory pressure is {mem_desc}, and silicon temperatures are {temp_desc}. {proc_cnt} processes are active across {p_cnt} Performance and {e_cnt} Efficiency cores. Power draw is {pwr_str} {throttle_str}"
                            );
                            ui.label(
                                RichText::new(summary_prose)
                                    .font(FontId::proportional(12.5))
                                    .color(ui.visuals().text_color()),
                            );
                        });

                        // Right Quick Actions Column
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                            ui.vertical(|ui| {
                                ui.set_min_width(155.0);
                                ui.label(
                                    RichText::new("Quick Actions")
                                        .font(FontId::proportional(11.5))
                                        .strong()
                                        .color(ui.visuals().weak_text_color()),
                                );
                                if ui
                                    .button("Optimize Memory")
                                    .on_hover_text(
                                        "Identify heavy memory consumers eligible for background working-set trim",
                                    )
                                    .clicked()
                                {
                                    let cands = self.mem_reclaim.candidates(&self.snapshot, 3);
                                    if cands.is_empty() {
                                        self.action_status =
                                            "Memory pressure is normal; no background trim needed."
                                                .to_string();
                                    } else {
                                        self.action_status = format!(
                                            "Memory Optimize (Dry): {} high-footprint candidate(s) ready.",
                                            cands.len()
                                        );
                                    }
                                }
                                let gm_btn_txt = if self.gaming_mode.active {
                                    "Gaming Mode: ON"
                                } else {
                                    "Gaming Mode"
                                };
                                if ui
                                    .button(gm_btn_txt)
                                    .on_hover_text(
                                        "Pause background shaders and throttle background processes",
                                    )
                                    .clicked()
                                {
                                    self.toggle_gaming_mode();
                                }
                                let pop_btn_txt = if self.show_popout_overlay
                                    && self.app.theme.popout_mode != PopoutMode::Off
                                {
                                    "Dock Popout Monitor"
                                } else {
                                    "Popout Monitor"
                                };
                                if ui
                                    .button(pop_btn_txt)
                                    .on_hover_text(
                                        "Launch or close the floating desktop .nexusskin mini-monitor window",
                                    )
                                    .clicked()
                                {
                                    self.toggle_popout_mode();
                                }
                                if ui
                                    .button("Run Benchmark")
                                    .on_hover_text(
                                        "Open Hardware Benchmark Studio to measure CPU, GPU, Disk, and Network",
                                    )
                                    .clicked()
                                {
                                    self.active_view = GuiView::Dest(Destination::Benchmarks);
                                }
                                let cust_txt = if self.dashboard_mode == 2 {
                                    "Done Customizing"
                                } else {
                                    "Customize Tiles"
                                };
                                if ui
                                    .button(cust_txt)
                                    .on_hover_text("Add, reorder, or configure Pro telemetry tiles")
                                    .clicked()
                                {
                                    if self.dashboard_mode == 2 {
                                        if self.page_edit.active {
                                            self.app.page_engine = self.page_edit.commit();
                                            dirty = true;
                                        }
                                        self.editing_tile_id = None;
                                        self.dashboard_mode = 1;
                                    } else {
                                        if !self.page_edit.active {
                                            self.page_edit.begin(self.app.page_engine.clone());
                                        }
                                        self.dashboard_mode = 2;
                                    }
                                }
                            });
                        });
                    });
                });

                ui.add_space(10.0);

                // Section 2: Four Visual At-a-Glance Environment Pillars (2x2 Grid)
                ui.columns(2, |cols| {
                    // Pillar 1: Processor (CPU & Clusters)
                    let col0_w = cols[0].available_width();
                    hig_card(&mut cols[0], |ui| {
                        ui.set_max_width((col0_w - 32.0).max(180.0));
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Processor (CPU & Clusters)")
                                    .font(FontId::proportional(14.5))
                                    .strong()
                                    .color(ui.visuals().strong_text_color()),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("View Details ->").clicked() {
                                        self.active_view = GuiView::Dest(Destination::Performance);
                                    }
                                },
                            );
                        });
                        ui.label(
                            RichText::new("Total, user-space, and kernel utilization across Performance and Efficiency cores.")
                                .font(FontId::proportional(12.0))
                                .color(ui.visuals().weak_text_color()),
                        );
                        ui.add_space(6.0);
                        let p_user = self
                            .perf_cpu_user_history
                            .samples
                            .last()
                            .and_then(|s| s.value);
                        let p_sys = self
                            .perf_cpu_sys_history
                            .samples
                            .last()
                            .and_then(|s| s.value);
                        ui.horizontal_wrapped(|ui| {
                            hig_status_pill(
                                ui,
                                &format!(
                                    "Total: {}",
                                    self.snapshot.system.cpu_percent.format_with_unit(1, "%")
                                ),
                                accent,
                            );
                            hig_status_pill(
                                ui,
                                &format!(
                                    "User: {}",
                                    p_user.map_or_else(
                                        || "n/a".to_string(),
                                        |v| format!("{v:.1}%")
                                    )
                                ),
                                hig_blue(ui),
                            );
                            hig_status_pill(
                                ui,
                                &format!(
                                    "System: {}",
                                    p_sys.map_or_else(
                                        || "n/a".to_string(),
                                        |v| format!("{v:.1}%")
                                    )
                                ),
                                hig_red(ui),
                            );
                        });
                        ui.add_space(4.0);
                        let series = [
                            ("Total CPU", accent, &self.app.cpu_history),
                            ("User", hig_blue(ui), &self.perf_cpu_user_history),
                            ("System", hig_red(ui), &self.perf_cpu_sys_history),
                        ];
                        draw_multi_series_history_canvas(
                            ui,
                            "",
                            &series,
                            accent,
                            76.0,
                            false,
                            48,
                            true,
                            self.app.theme.smooth_graph_curves,
                            self.app.theme.visual_bloom,
                            self.app.theme.visual_bloom_level,
                            false,
                            0.0,
                            100.0,
                            "%",
                        );
                        ui.add_space(4.0);
                        let p_mhz_part = measured_opt(&sp.p_cluster_mhz)
                            .map_or_else(String::new, |m| format!(" ({m:.0} MHz)"));
                        let e_mhz_part = measured_opt(&sp.e_cluster_mhz)
                            .map_or_else(String::new, |m| format!(" ({m:.0} MHz)"));
                        ui.label(
                            RichText::new(format!(
                                "P-Cluster {}{} · E-Cluster {}{} · 1m load {}",
                                sp.p_cluster_active_residency_percent.format_with_unit(1, "%"),
                                p_mhz_part,
                                sp.e_cluster_active_residency_percent.format_with_unit(1, "%"),
                                e_mhz_part,
                                self.snapshot.system.load_avg_1m.format_with_unit(2, "")
                            ))
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                        );
                    });

                    // Pillar 2: Memory (RAM Composition & Pressure)
                    let col1_w = cols[1].available_width();
                    hig_card(&mut cols[1], |ui| {
                        ui.set_max_width((col1_w - 32.0).max(180.0));
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Memory (RAM Composition)")
                                    .font(FontId::proportional(14.5))
                                    .strong()
                                    .color(ui.visuals().strong_text_color()),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("View Details ->").clicked() {
                                        self.active_view = GuiView::Dest(Destination::Performance);
                                    }
                                },
                            );
                        });
                        ui.label(
                            RichText::new("Physical memory allocation across apps, kernel wired pages, compression, and free headroom.")
                                .font(FontId::proportional(12.0))
                                .color(ui.visuals().weak_text_color()),
                        );
                        ui.add_space(6.0);

                        let total_b = measured_zero(&self.snapshot.system.mem_total_bytes).max(1.0);
                        let active_b = measured_zero(&self.snapshot.system.mem_active_bytes);
                        let wired_b = measured_zero(&self.snapshot.system.mem_wired_bytes);
                        let comp_b = measured_zero(&self.snapshot.system.mem_compressed_bytes);
                        let cached_free_b =
                            measured_zero(&self.snapshot.system.mem_cached_files_bytes)
                                + measured_zero(&self.snapshot.system.mem_free_bytes);
                        let (pres_lbl, pres_col) = match mem_pct {
                            Some(m) if m > 88.0 => ("High Pressure", hig_red(ui)),
                            Some(m) if m >= 75.0 => ("Moderate Pressure", hig_amber(ui)),
                            _ => ("Normal Pressure", hig_green(ui)),
                        };
                        ui.horizontal_wrapped(|ui| {
                            hig_status_pill(ui, pres_lbl, pres_col);
                            ui.label(format!(
                                "{} / {} used · Swap: {}",
                                format_metric_bytes(&self.snapshot.system.mem_used_bytes),
                                format_bytes(total_b as u64),
                                format_metric_bytes(&self.snapshot.system.swap_used_bytes),
                            ));
                        });
                        ui.add_space(4.0);
                        let mem_segs = [
                            ("App Memory", active_b, hig_blue(ui)),
                            ("Wired System", wired_b, hig_amber(ui)),
                            ("Compressed", comp_b, hig_purple(ui)),
                            ("Cached / Free", cached_free_b, hig_green(ui)),
                        ];
                        draw_stacked_bar(ui, ui.available_width(), 14.0, 4, &mem_segs, total_b);
                        ui.add_space(4.0);
                        ui.horizontal_wrapped(|ui| {
                            for (lbl, bytes, col) in mem_segs {
                                ui.colored_label(
                                    col,
                                    RichText::new(format!(
                                        "* {lbl}: {}",
                                        format_bytes(bytes as u64)
                                    ))
                                    .font(FontId::proportional(11.0)),
                                );
                            }
                        });
                        ui.add_space(4.0);
                        let mem_series = [("Memory %", hig_blue(ui), &self.perf_mem_history)];
                        draw_multi_series_history_canvas(
                            ui,
                            "",
                            &mem_series,
                            accent,
                            56.0,
                            false,
                            40,
                            true,
                            self.app.theme.smooth_graph_curves,
                            self.app.theme.visual_bloom,
                            self.app.theme.visual_bloom_level,
                            false,
                            0.0,
                            100.0,
                            "%",
                        );
                    });
                });

                ui.add_space(10.0);

                ui.columns(2, |cols| {
                    // Pillar 3: Graphics & Silicon Power
                    let col0_w = cols[0].available_width();
                    hig_card(&mut cols[0], |ui| {
                        ui.set_max_width((col0_w - 32.0).max(180.0));
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Graphics & Silicon Power")
                                    .font(FontId::proportional(14.5))
                                    .strong()
                                    .color(ui.visuals().strong_text_color()),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("View Details ->").clicked() {
                                        self.active_view = GuiView::Dest(Destination::AppleSilicon);
                                    }
                                },
                            );
                        });
                        ui.label(
                            RichText::new("Apple GPU activity, package power draw, and max die thermal state.")
                                .font(FontId::proportional(12.0))
                                .color(ui.visuals().weak_text_color()),
                        );
                        ui.add_space(6.0);
                        ui.horizontal_wrapped(|ui| {
                            let (temp_lbl, temp_col) = match die_c {
                                Some(t) if t > 85.0 => (format!("Hot · {t:.1} C"), hig_red(ui)),
                                Some(t) if t >= 65.0 => {
                                    (format!("Warm · {t:.1} C"), hig_amber(ui))
                                }
                                Some(t) => (format!("Cool · {t:.1} C"), hig_green(ui)),
                                None => ("Temp Unavailable".to_string(), hig_gray(ui)),
                            };
                            hig_status_pill(ui, &temp_lbl, temp_col);
                            hig_status_pill(
                                ui,
                                &format!("Package: {}", sp.package_watts.format_with_unit(1, "W")),
                                hig_amber(ui),
                            );
                            hig_status_pill(
                                ui,
                                &format!(
                                    "GPU: {}",
                                    self.snapshot
                                        .system
                                        .gpu_utilization_percent
                                        .format_with_unit(1, "%")
                                ),
                                hig_green(ui),
                            );
                        });
                        ui.add_space(4.0);
                        let gfx_series = [
                            ("GPU %", hig_green(ui), &self.perf_gpu_history),
                            ("Package W", hig_amber(ui), &self.perf_pkg_watts_history),
                        ];
                        draw_multi_series_history_canvas(
                            ui,
                            "",
                            &gfx_series,
                            accent,
                            76.0,
                            false,
                            44,
                            true,
                            self.app.theme.smooth_graph_curves,
                            self.app.theme.visual_bloom,
                            self.app.theme.visual_bloom_level,
                            true,
                            0.0,
                            100.0,
                            "",
                        );
                        ui.add_space(4.0);
                        let gpu_explain = match (gpu_pct, pkg_w) {
                            (Some(g), Some(w)) => format!(
                                "{} is at {g:.1}% utilization while the SoC draws {w:.1} W.",
                                self.snapshot.system.gpu_model
                            ),
                            _ => format!(
                                "Live telemetry for {} and Apple Silicon power rails.",
                                self.snapshot.system.gpu_model
                            ),
                        };
                        ui.label(
                            RichText::new(gpu_explain)
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                        );
                    });

                    // Pillar 4: Storage & Network
                    let col1_w = cols[1].available_width();
                    hig_card(&mut cols[1], |ui| {
                        ui.set_max_width((col1_w - 32.0).max(180.0));
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new("Storage & Network")
                                    .font(FontId::proportional(14.5))
                                    .strong()
                                    .color(ui.visuals().strong_text_color()),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("View Details ->").clicked() {
                                        self.active_view = GuiView::Dest(Destination::DiskSpace);
                                    }
                                },
                            );
                        });
                        ui.label(
                            RichText::new("Root volume capacity and live disk and network throughput.")
                                .font(FontId::proportional(12.0))
                                .color(ui.visuals().weak_text_color()),
                        );
                        ui.add_space(6.0);

                        let (vol_total_b, vol_used_b, vol_avail_b) =
                            if let Some(root_v) = self
                                .platform_cache
                                .volumes
                                .iter()
                                .find(|v| v.mount_point == "/")
                                .or_else(|| self.platform_cache.volumes.first())
                            {
                                let tot = measured_zero(&root_v.total_bytes) as u64;
                                let avail = measured_zero(&root_v.available_bytes) as u64;
                                let used = tot.saturating_sub(avail);
                                (tot.max(1), used, avail)
                            } else {
                                let (t, a, u) =
                                    statvfs_volume_bytes(Path::new(&self.disk_scan_path_input))
                                        .unwrap_or((1, 0, 0));
                                (t.max(1), u, a)
                            };
                        let used_frac = (vol_used_b as f64 / vol_total_b as f64).clamp(0.0, 1.0) as f32;
                        let vol_col = if used_frac > 0.90 {
                            hig_red(ui)
                        } else if used_frac > 0.78 {
                            hig_amber(ui)
                        } else {
                            hig_blue(ui)
                        };
                        ui.label(
                            RichText::new(format!(
                                "Startup Disk: {} used of {} ({} free)",
                                format_bytes(vol_used_b),
                                format_bytes(vol_total_b),
                                format_bytes(vol_avail_b)
                            ))
                            .strong(),
                        );
                        ui.add_space(4.0);
                        draw_hig_progress_bar(ui, ui.available_width(), 14.0, used_frac, vol_col);
                        ui.add_space(8.0);

                        let d_read = sum_measured_metrics(
                            self.snapshot.disks.iter().map(|d| &d.read_bytes_per_sec),
                        )
                        .unwrap_or(0.0)
                            / (1024.0 * 1024.0);
                        let d_write = sum_measured_metrics(
                            self.snapshot.disks.iter().map(|d| &d.write_bytes_per_sec),
                        )
                        .unwrap_or(0.0)
                            / (1024.0 * 1024.0);
                        let n_down = sum_measured_metrics(
                            self.snapshot.networks.iter().map(|n| &n.rx_bytes_per_sec),
                        )
                        .unwrap_or(0.0)
                            * 8.0
                            / 1_000_000.0;
                        let n_up = sum_measured_metrics(
                            self.snapshot.networks.iter().map(|n| &n.tx_bytes_per_sec),
                        )
                        .unwrap_or(0.0)
                            * 8.0
                            / 1_000_000.0;

                        ui.horizontal_wrapped(|ui| {
                            hig_status_pill(
                                ui,
                                &format!("Disk Read: {d_read:.1} MB/s"),
                                hig_blue(ui),
                            );
                            hig_status_pill(
                                ui,
                                &format!("Disk Write: {d_write:.1} MB/s"),
                                hig_amber(ui),
                            );
                            hig_status_pill(
                                ui,
                                &format!("Net Down: {n_down:.1} Mbps"),
                                hig_green(ui),
                            );
                            hig_status_pill(
                                ui,
                                &format!("Net Up: {n_up:.1} Mbps"),
                                hig_purple(ui),
                            );
                        });
                        ui.add_space(6.0);
                        ui.label(
                            RichText::new(format!(
                                "Volume is {:.1}% full with {:.1} MB/s total disk activity and {:.1} Mbps combined network traffic.",
                                used_frac * 100.0,
                                d_read + d_write,
                                n_down + n_up
                            ))
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                        );
                    });
                });

                ui.add_space(10.0);

                // Section 3: What Is Using My Mac Right Now (Top 5 Active Processes)
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new("What Is Using My Mac Right Now")
                                .font(FontId::proportional(14.5))
                                .strong()
                                .color(ui.visuals().strong_text_color()),
                        );
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            if ui.button("View All Processes ->").clicked() {
                                self.active_view = GuiView::Dest(Destination::Processes);
                            }
                        });
                    });
                    ui.label(
                        RichText::new("Top 5 active applications and processes ranked by combined processor and memory impact.")
                            .font(FontId::proportional(12.0))
                            .color(ui.visuals().weak_text_color()),
                    );
                    ui.add_space(6.0);

                    let total_mem_mib = measured_opt(&self.snapshot.system.mem_total_bytes)
                        .map_or(16_384.0, |b| (b / (1024.0 * 1024.0)).max(1.0));
                    let mut ranked: Vec<(&nexus_core::ProcessSnapshot, f64, f64, f64)> = self
                        .snapshot
                        .processes
                        .iter()
                        .map(|p| {
                            let c = measured_opt(&p.cpu_percent).unwrap_or(0.0);
                            let m_mib = metric_mib(&p.phys_footprint_bytes);
                            let impact = c * 1.5 + (m_mib / 1024.0) * 10.0;
                            (p, c, m_mib, impact)
                        })
                        .collect();
                    ranked.sort_by(|a, b| {
                        b.3.partial_cmp(&a.3).unwrap_or(std::cmp::Ordering::Equal)
                    });

                    if ranked.is_empty() {
                        ui.label("No active process metrics available in snapshot.");
                    } else {
                        egui::Grid::new("dashboard_top_processes_grid")
                            .num_columns(5)
                            .striped(true)
                            .spacing([14.0, 6.0])
                            .show(ui, |ui| {
                                ui.label(RichText::new("Application / Process").strong());
                                ui.label(RichText::new("Impact").strong());
                                ui.label(RichText::new("CPU %").strong());
                                ui.label(RichText::new("Memory").strong());
                                ui.label(RichText::new("Actions").strong());
                                ui.end_row();

                                for &(proc_snap, cpu_pct_val, mem_mib, _) in ranked.iter().take(5) {
                                    ui.horizontal(|ui| {
                                        ui.label(RichText::new(proc_snap.name.as_str()).strong());
                                        ui.label(
                                            RichText::new(format!("PID {}", proc_snap.key.pid))
                                                .font(FontId::proportional(11.0))
                                                .color(ui.visuals().weak_text_color()),
                                        );
                                    });

                                    let (imp_lbl, imp_col) = if cpu_pct_val >= 35.0 {
                                        ("High CPU", hig_red(ui))
                                    } else if mem_mib >= 1536.0 {
                                        ("High Memory", hig_purple(ui))
                                    } else if cpu_pct_val >= 10.0 || mem_mib >= 512.0 {
                                        ("Moderate", hig_blue(ui))
                                    } else {
                                        ("Light", hig_green(ui))
                                    };
                                    hig_status_pill(ui, imp_lbl, imp_col);

                                    let cpu_lbl = proc_snap.cpu_percent.format_with_unit(1, "%");
                                    if let Some(spark) =
                                        self.platform_cache.process_sparklines.get(&proc_snap.key)
                                    {
                                        draw_inline_sparkline(
                                            ui,
                                            &spark.cpu,
                                            false,
                                            &cpu_lbl,
                                            accent,
                                        );
                                    } else {
                                        ui.label(cpu_lbl);
                                    }

                                    let mem_lbl = format!("{mem_mib:.1} MiB");
                                    let mem_frac = (mem_mib / total_mem_mib).clamp(0.0, 1.0) as f32;
                                    draw_inline_hbar(ui, mem_frac, &mem_lbl, accent);

                                    ui.horizontal(|ui| {
                                        if ui
                                            .small_button("Inspect")
                                            .on_hover_text("Open detailed Process Inspector dossier")
                                            .clicked()
                                        {
                                            inspect_action_target = Some(proc_snap.key);
                                        }
                                        if ui
                                            .small_button("Throttle (Nice +10)")
                                            .on_hover_text(
                                                "Lower process scheduling priority (Nice +10, PID+token verified dry-run)",
                                            )
                                            .clicked()
                                        {
                                            throttle_action_target =
                                                Some((proc_snap.key, proc_snap.name.to_string()));
                                        }
                                    });
                                    ui.end_row();
                                }
                            });
                    }
                });

                // Section 4: Composable Power-User Workspace (`KDE Face x Sensor` Grid)
                if self.dashboard_mode >= 1 {
                    ui.add_space(12.0);
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal_wrapped(|ui| {
                            ui.vertical(|ui| {
                                hig_card_header(
                                    ui,
                                    "Pro Telemetry Workspace",
                                    "Customizable multi-face sensor tiles powered by live SnapshotHub telemetry.",
                                    accent,
                                );
                            });
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::TOP),
                                |ui| {
                                    if !self.page_edit.active {
                                        if ui.button("Edit Pro Tiles").clicked() {
                                            self.page_edit.begin(self.app.page_engine.clone());
                                            self.dashboard_mode = 2;
                                        }
                                    } else {
                                        if ui.button("Done & Save Layout").clicked() {
                                            self.app.page_engine = self.page_edit.commit();
                                            self.editing_tile_id = None;
                                            self.dashboard_mode = 1;
                                            dirty = true;
                                        }
                                        if ui.button("Cancel Edits").clicked() {
                                            self.app.page_engine = self.page_edit.cancel();
                                            self.editing_tile_id = None;
                                            self.dashboard_mode = 1;
                                        }
                                        if ui
                                            .add_enabled(
                                                self.page_edit.can_undo(),
                                                egui::Button::new("Undo"),
                                            )
                                            .clicked()
                                        {
                                            self.page_edit.undo();
                                        }
                                        if ui
                                            .add_enabled(
                                                self.page_edit.can_redo(),
                                                egui::Button::new("Redo"),
                                            )
                                            .clicked()
                                        {
                                            self.page_edit.redo();
                                        }
                                        if ui.button("Reset Default").clicked() {
                                            self.page_edit.reset_to_default();
                                            self.editing_tile_id = None;
                                        }
                                    }
                                },
                            );
                        });

                        if self.page_edit.active {
                            let discovered = SensorRegistry::discover(&self.snapshot);
                            egui::Frame::new()
                                .fill(ui.visuals().faint_bg_color)
                                .stroke(ui.visuals().widgets.noninteractive.bg_stroke)
                                .corner_radius(CornerRadius::same(8))
                                .inner_margin(egui::Margin::symmetric(10, 8))
                                .show(ui, |ui| {
                                    ui.horizontal_wrapped(|ui| {
                                        ui.label("New Tile Title:");
                                        ui.add(
                                            egui::TextEdit::singleline(&mut self.add_tile_title)
                                                .desired_width(130.0),
                                        );
                                        ui.label("Face:");
                                        egui::ComboBox::from_id_salt("add_face_combo")
                                            .selected_text(
                                                FaceKind::ALL[self
                                                    .add_tile_face_idx
                                                    .min(FaceKind::ALL.len() - 1)]
                                                .label(),
                                            )
                                            .show_ui(ui, |ui| {
                                                for (i, fk) in FaceKind::ALL.iter().enumerate() {
                                                    ui.selectable_value(
                                                        &mut self.add_tile_face_idx,
                                                        i,
                                                        fk.label(),
                                                    );
                                                }
                                            });
                                        ui.label("Sensor:");
                                        if !discovered.is_empty() {
                                            let safe_idx =
                                                self.add_tile_sensor_idx.min(discovered.len() - 1);
                                            egui::ComboBox::from_id_salt("add_sensor_combo")
                                                .selected_text(discovered[safe_idx].label.as_str())
                                                .show_ui(ui, |ui| {
                                                    for (i, d) in discovered.iter().enumerate() {
                                                        ui.selectable_value(
                                                            &mut self.add_tile_sensor_idx,
                                                            i,
                                                            format!(
                                                                "{} [{}]",
                                                                d.label,
                                                                d.path.as_path_string()
                                                            ),
                                                        );
                                                    }
                                                });
                                            if ui.button("+ Add Tile").clicked() {
                                                let fk = FaceKind::ALL[self
                                                    .add_tile_face_idx
                                                    .min(FaceKind::ALL.len() - 1)];
                                                let sp = discovered[safe_idx].path.clone();
                                                let title = self.add_tile_title.trim();
                                                let final_title = if title.is_empty() {
                                                    discovered[safe_idx].label.as_str()
                                                } else {
                                                    title
                                                };
                                                let new_id =
                                                    self.page_edit.add_tile(final_title, fk, sp);
                                                self.editing_tile_id = Some(new_id);
                                            }
                                        }
                                    });
                                });
                            ui.add_space(6.0);
                        }
                    });

                    ui.add_space(8.0);

                    let tiles_snapshot = self.page_edit.current.tiles.clone();
                    let mut remove_id: Option<CompactString> = None;
                    let mut move_up_id: Option<CompactString> = None;
                    let mut move_down_id: Option<CompactString> = None;
                    let mut configure_id: Option<CompactString> = None;

                    let mut rows: Vec<Vec<DashboardTile>> = Vec::new();
                    let mut pending_half: Option<DashboardTile> = None;
                    for tile in tiles_snapshot {
                        if tile.col_span >= 2 {
                            if let Some(prev) = pending_half.take() {
                                rows.push(vec![prev]);
                            }
                            rows.push(vec![tile]);
                        } else if let Some(prev) = pending_half.take() {
                            rows.push(vec![prev, tile]);
                        } else {
                            pending_half = Some(tile);
                        }
                    }
                    if let Some(last) = pending_half {
                        rows.push(vec![last]);
                    }

                    for row in rows {
                        if row.len() == 1 && row[0].col_span >= 2 {
                            let tile = &row[0];
                            self.draw_single_dashboard_tile(
                                ui,
                                tile,
                                positional,
                                accent,
                                &mut configure_id,
                                &mut move_up_id,
                                &mut move_down_id,
                                &mut remove_id,
                            );
                            ui.add_space(8.0);
                        } else {
                            ui.columns(2, |cols| {
                                for (c_idx, tile) in row.iter().enumerate() {
                                    if let Some(col_ui) = cols.get_mut(c_idx) {
                                        self.draw_single_dashboard_tile(
                                            col_ui,
                                            tile,
                                            positional,
                                            accent,
                                            &mut configure_id,
                                            &mut move_up_id,
                                            &mut move_down_id,
                                            &mut remove_id,
                                        );
                                    }
                                }
                            });
                            ui.add_space(8.0);
                        }
                    }

                    if let Some(tid) = configure_id {
                        self.editing_tile_id =
                            if self.editing_tile_id.as_deref() == Some(tid.as_str()) {
                                None
                            } else {
                                self.app.process_table.inspector_open = false;
                                Some(tid)
                            };
                    }
                    if let Some(tid) = move_up_id {
                        self.page_edit.move_tile_up(tid.as_str());
                    }
                    if let Some(tid) = move_down_id {
                        self.page_edit.move_tile_down(tid.as_str());
                    }
                    if let Some(tid) = remove_id {
                        if self.editing_tile_id.as_deref() == Some(tid.as_str()) {
                            self.editing_tile_id = None;
                        }
                        self.page_edit.remove_tile(tid.as_str());
                    }
                }
            });

        if let Some(key) = inspect_action_target {
            self.app.process_table.selected_key = Some(key);
            self.app.process_table.inspector_open = true;
            self.editing_tile_id = None;
        }
        if let Some((key, name)) = throttle_action_target {
            self.run_process_action(key, ProcessActionType::SetPriority, None, Some(10));
            self.action_status = format!("{} (PID {}): {}", name, key.pid, self.action_status);
        }

        dirty
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_single_dashboard_tile(
        &mut self,
        ui: &mut egui::Ui,
        tile: &DashboardTile,
        positional: bool,
        accent: Color32,
        configure_id: &mut Option<CompactString>,
        move_up_id: &mut Option<CompactString>,
        move_down_id: &mut Option<CompactString>,
        remove_id: &mut Option<CompactString>,
    ) {
        hig_card(ui, |ui| {
            ui.set_min_width(ui.available_width());
            if tile.config.show_title || self.page_edit.active {
                ui.horizontal_wrapped(|ui| {
                    ui.vertical(|ui| {
                        let subtitle = tile
                            .sensors
                            .first()
                            .map(|sp| human_sensor_subtitle(sp, &self.snapshot))
                            .unwrap_or_else(|| tile.face.label().to_string());
                        hig_card_header(ui, tile.title.as_str(), &subtitle, accent);
                    });
                    if self.page_edit.active {
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                            if ui.small_button("x").on_hover_text("Remove tile").clicked() {
                                *remove_id = Some(tile.id.clone());
                            }
                            if ui.small_button("v").on_hover_text("Move down").clicked() {
                                *move_down_id = Some(tile.id.clone());
                            }
                            if ui.small_button("^").on_hover_text("Move up").clicked() {
                                *move_up_id = Some(tile.id.clone());
                            }
                            let is_cfg = self.editing_tile_id.as_ref() == Some(&tile.id);
                            if ui.selectable_label(is_cfg, "Configure").clicked() {
                                *configure_id = Some(tile.id.clone());
                            }
                        });
                    }
                });
            }

            let expanded: Vec<(SensorPath, CompactString)> = tile
                .sensors
                .iter()
                .flat_map(|sp| sp.expand(&self.snapshot))
                .collect();
            self.draw_tile_face_body(ui, tile, tile.face, &expanded, positional, accent, false);
        });
    }

    fn sensor_accent(
        &self,
        tile: &DashboardTile,
        sp: &SensorPath,
        idx: usize,
        default_accent: Color32,
    ) -> Color32 {
        let key = sp.as_path_string();
        if let Some(&[r, g, b]) = tile.config.sensor_colors.get(key.as_str()) {
            return Color32::from_rgb(r, g, b);
        }
        const PALETTE: [[u8; 3]; 6] = [
            [10, 132, 255],
            [48, 209, 88],
            [255, 214, 10],
            [191, 90, 242],
            [255, 159, 10],
            [255, 69, 58],
        ];
        if idx == 0 {
            default_accent
        } else {
            let [r, g, b] = PALETTE[idx % PALETTE.len()];
            Color32::from_rgb(r, g, b)
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_tile_face_body(
        &mut self,
        ui: &mut egui::Ui,
        tile: &DashboardTile,
        face: FaceKind,
        expanded: &[(SensorPath, CompactString)],
        positional: bool,
        accent: Color32,
        compact: bool,
    ) {
        let hw = self.platform_cache.hardware;
        match face {
            FaceKind::VfdSegmentedMeter => {
                let items = if expanded.is_empty() {
                    vec![(
                        SensorPath::new("cpu", "total", "utilization"),
                        tile.title.clone(),
                    )]
                } else {
                    expanded.iter().take(4).cloned().collect()
                };
                for (i, (sp, lbl)) in items.iter().enumerate() {
                    let col = self.sensor_accent(tile, sp, i, accent);
                    let val = sp.resolve_value_with_hw(&self.snapshot, hw);
                    let lvl = val
                        .as_ref()
                        .and_then(|v| v.normalized_fraction(100.0))
                        .unwrap_or(0.0);
                    let unit = if sp.metric.as_str() == "watts" {
                        "W"
                    } else if sp.metric.as_str().contains("celsius") {
                        "C"
                    } else if sp.metric.as_str() == "mhz" {
                        "MHz"
                    } else {
                        "%"
                    };
                    let text = val
                        .map(|v| v.format_display(unit))
                        .unwrap_or_else(|| "Unavailable".to_string());
                    let title = if items.len() == 1 {
                        tile.title.as_str()
                    } else {
                        lbl.as_str()
                    };
                    draw_single_vfd_bar(ui, title, &text, lvl, positional, col, &self.app.theme);
                }
            }
            FaceKind::CompressedHistoryGraph => {
                let mut series_owned: Vec<(String, Color32, &CompressedHistoryGraph)> = Vec::new();
                for (i, (sp, lbl)) in expanded.iter().enumerate() {
                    let key = sp.as_path_string();
                    if let Some(g) = self.sensor_histories.get(key.as_str()) {
                        let col = self.sensor_accent(tile, sp, i, accent);
                        let name = if expanded.len() == 1 {
                            tile.title.to_string()
                        } else {
                            lbl.to_string()
                        };
                        series_owned.push((name, col, g));
                    }
                }
                if series_owned.is_empty() {
                    let fallback = self
                        .tile_histories
                        .get(&tile.id)
                        .unwrap_or(&self.app.cpu_history);
                    series_owned.push((tile.title.to_string(), accent, fallback));
                }
                let series_refs: Vec<(&str, Color32, &CompressedHistoryGraph)> = series_owned
                    .iter()
                    .map(|(n, c, g)| (n.as_str(), *c, *g))
                    .collect();
                let unit = expanded.first().map_or("%", |(sp, _)| {
                    if sp.metric.as_str() == "watts" {
                        "W"
                    } else if sp.metric.as_str().contains("celsius") {
                        "C"
                    } else if sp.metric.as_str() == "mhz" {
                        "MHz"
                    } else if sp.metric.as_str().contains("utilization")
                        || sp.metric.as_str().contains("percent")
                        || sp.metric.as_str().contains("residency")
                    {
                        "%"
                    } else {
                        ""
                    }
                });
                let fill_alpha = (tile.config.fill_opacity.clamp(0.0, 1.0) * 255.0) as u8;
                draw_multi_series_history_canvas(
                    ui,
                    if compact { "" } else { tile.title.as_str() },
                    &series_refs,
                    accent,
                    if compact { 64.0 } else { 115.0 },
                    tile.config.show_legend && !compact,
                    fill_alpha,
                    tile.config.grid_lines,
                    self.app.theme.smooth_graph_curves && tile.config.smooth,
                    self.app.theme.visual_bloom,
                    self.app.theme.visual_bloom_level,
                    tile.config.range_auto_y,
                    tile.config.range_from,
                    tile.config.range_to,
                    unit,
                );
            }
            FaceKind::BarChart => {
                let h = if compact { 68.0 } else { 115.0 };
                let (rect, _) = ui.allocate_exact_size(
                    Vec2::new(ui.available_width().max(120.0), h),
                    egui::Sense::hover(),
                );
                let painter = ui.painter_at(rect);
                painter.rect_filled(rect, CornerRadius::same(4), Color32::from_rgb(7, 10, 14));
                if tile.config.grid_lines {
                    for g in 1..4 {
                        let y = rect.min.y + rect.height() * (g as f32 * 0.25);
                        painter.line_segment(
                            [
                                egui::Pos2::new(rect.min.x, y),
                                egui::Pos2::new(rect.max.x, y),
                            ],
                            Stroke::new(1.0, Color32::from_rgba_unmultiplied(120, 140, 160, 32)),
                        );
                    }
                }
                let n = expanded.len().max(1);
                let slot_w = rect.width() / (n as f32);
                let bar_w = (slot_w * 0.68).max(4.0);
                let mut legend = Vec::new();
                for (i, (sp, lbl)) in expanded.iter().enumerate() {
                    let col = self.sensor_accent(tile, sp, i, accent);
                    let val_opt = sp.resolve_value_with_hw(&self.snapshot, hw);
                    let frac = val_opt
                        .as_ref()
                        .and_then(|v| {
                            v.normalized_fraction(if tile.config.range_auto_y {
                                100.0
                            } else {
                                tile.config.range_to.max(1.0)
                            })
                        })
                        .unwrap_or(0.0) as f32;
                    let bh = (rect.height() - 16.0) * frac.clamp(0.0, 1.0);
                    let x0 = rect.min.x + (i as f32) * slot_w + (slot_w - bar_w) * 0.5;
                    let bar_r = Rect::from_min_size(
                        egui::Pos2::new(x0, rect.max.y - 4.0 - bh),
                        Vec2::new(bar_w, bh.max(2.0)),
                    );
                    painter.rect_filled(bar_r, CornerRadius::same(2), col);
                    legend.push((
                        lbl.clone(),
                        col,
                        val_opt
                            .map(|v| v.format_display("%"))
                            .unwrap_or_else(|| "n/a".into()),
                    ));
                }
                if tile.config.show_legend && !compact {
                    ui.horizontal_wrapped(|ui| {
                        for (lbl, col, val) in legend.iter().take(8) {
                            ui.colored_label(*col, format!("* {lbl}: {val}"));
                        }
                    });
                }
            }
            FaceKind::PieChart => {
                let sz = Vec2::new(
                    ui.available_width().max(120.0),
                    if compact { 82.0 } else { 128.0 },
                );
                let (rect, _) = ui.allocate_exact_size(sz, egui::Sense::hover());
                let painter = ui.painter_at(rect);
                let center = rect.center();
                let radius = (rect.width().min(rect.height()) * 0.42).max(20.0);
                let start_rad = tile.config.pie_start_angle.to_radians();
                let end_rad = tile.config.pie_end_angle.to_radians();
                let total_sweep = (end_rad - start_rad).abs().max(0.2);

                painter.circle_stroke(
                    center,
                    radius,
                    Stroke::new(10.0, Color32::from_rgba_unmultiplied(120, 140, 160, 38)),
                );

                let denom = tile
                    .config
                    .total_sensors
                    .first()
                    .and_then(|ts| ts.resolve_metric(&self.snapshot))
                    .and_then(|m| match m.reading {
                        MetricReading::Measured { value, .. } if value > 0.0 => Some(value),
                        _ => None,
                    })
                    .unwrap_or(100.0);

                let mut cur_angle = start_rad;
                let mut total_val = 0.0_f64;
                let mut legend = Vec::new();
                for (i, (sp, lbl)) in expanded.iter().enumerate() {
                    let col = self.sensor_accent(tile, sp, i, accent);
                    let v = match sp.resolve_metric(&self.snapshot).map(|m| m.reading) {
                        Some(MetricReading::Measured { value, .. }) if value.is_finite() => {
                            value.max(0.0)
                        }
                        _ => 0.0,
                    };
                    total_val += v;
                    let slice_frac = (v / denom).clamp(0.0, 1.0) as f32;
                    let sweep =
                        (slice_frac * total_sweep).min((start_rad + total_sweep) - cur_angle);
                    if sweep > 0.02 {
                        let steps = ((sweep * 24.0).ceil() as usize).max(2);
                        let mut pts = Vec::with_capacity(steps + 1);
                        for k in 0..=steps {
                            let a = cur_angle + sweep * (k as f32 / steps as f32);
                            pts.push(egui::Pos2::new(
                                center.x + a.cos() * radius,
                                center.y + a.sin() * radius,
                            ));
                        }
                        painter.add(egui::Shape::line(pts, Stroke::new(10.0, col)));
                        cur_angle += sweep;
                    }
                    legend.push((lbl.clone(), col, v));
                }
                painter.text(
                    center,
                    egui::Align2::CENTER_CENTER,
                    format!("{total_val:.0}%"),
                    FontId::new(16.0, FontFamily::Name("led".into())),
                    accent,
                );
                if tile.config.show_legend && !compact {
                    ui.horizontal_wrapped(|ui| {
                        for (lbl, col, v) in legend.iter().take(6) {
                            ui.colored_label(*col, format!("* {lbl}: {v:.1}"));
                        }
                    });
                }
            }
            FaceKind::ColorGrid => {
                let cols = usize::from(tile.config.grid_columns.clamp(2, 8));
                egui::Grid::new(format!("color_grid_{}", tile.id))
                    .num_columns(cols)
                    .spacing([6.0, 6.0])
                    .show(ui, |ui| {
                        for (idx, (sp, lbl)) in expanded.iter().enumerate() {
                            let val = sp.resolve_value_with_hw(&self.snapshot, hw);
                            let frac = val
                                .as_ref()
                                .and_then(|v| v.normalized_fraction(100.0))
                                .unwrap_or(0.0) as f32;
                            let base_col = self.sensor_accent(tile, sp, idx, accent);
                            let fill = Color32::from_rgba_unmultiplied(
                                base_col.r(),
                                base_col.g(),
                                base_col.b(),
                                (35.0 + frac * 185.0).clamp(35.0, 220.0) as u8,
                            );
                            egui::Frame::new()
                                .fill(fill)
                                .corner_radius(CornerRadius::same(4))
                                .inner_margin(4.0)
                                .show(ui, |ui| {
                                    ui.label(RichText::new(lbl.as_str()).small().strong());
                                    ui.label(
                                        RichText::new(
                                            val.map(|v| v.format_display("%"))
                                                .unwrap_or_else(|| "n/a".into()),
                                        )
                                        .small(),
                                    );
                                });
                            if (idx + 1).is_multiple_of(cols) {
                                ui.end_row();
                            }
                        }
                    });
            }
            FaceKind::FaceGrid => {
                let nested = if tile.config.nested_face.properties().allows_in_face_grid {
                    tile.config.nested_face
                } else {
                    FaceKind::VfdSegmentedMeter
                };
                let cols = usize::from(tile.config.grid_columns.clamp(1, 6));
                egui::Grid::new(format!("face_grid_{}", tile.id))
                    .num_columns(cols)
                    .spacing([8.0, 8.0])
                    .show(ui, |ui| {
                        for (idx, item) in expanded.iter().enumerate() {
                            ui.group(|ui| {
                                ui.label(
                                    RichText::new(item.1.as_str())
                                        .small()
                                        .strong()
                                        .color(accent),
                                );
                                let sub = std::slice::from_ref(item);
                                self.draw_tile_face_body(
                                    ui, tile, nested, sub, positional, accent, true,
                                );
                            });
                            if (idx + 1).is_multiple_of(cols) {
                                ui.end_row();
                            }
                        }
                    });
            }
            FaceKind::ProcessorClusterGrid => {
                let sp = &self.snapshot.hardware_telemetry.soc_power;
                ui.label(format!(
                    "P-Cores ({}): {} ({}) | E-Cores ({}): {} ({}) | Package: {}",
                    self.snapshot.system.performance_core_count,
                    sp.p_cluster_active_residency_percent
                        .format_with_unit(1, "%"),
                    sp.p_cluster_mhz.format_with_unit(0, "MHz"),
                    self.snapshot.system.efficiency_core_count,
                    sp.e_cluster_active_residency_percent
                        .format_with_unit(1, "%"),
                    sp.e_cluster_mhz.format_with_unit(0, "MHz"),
                    sp.package_watts.format_with_unit(1, "W"),
                ));
            }
            FaceKind::DiskOceanMetaballs => {
                let _ =
                    self.embed_disk_shader_with_mode(ui, Vec2::new(ui.available_width(), 145.0), 1);
            }
            FaceKind::DiskDefragBlocks => {
                let _ =
                    self.embed_disk_shader_with_mode(ui, Vec2::new(ui.available_width(), 145.0), 2);
            }
            FaceKind::VectorTunnelTempest => {
                let sz = Vec2::new(ui.available_width(), 145.0);
                embed_wgsl_pass(
                    ui,
                    self.target_format,
                    sz,
                    ShaderPassKind::VectorTunnel {
                        time_sec: self.start_instant.elapsed().as_secs_f32(),
                        viewport: [sz.x.max(1.0), sz.y.max(1.0)],
                        edr_headroom: self.edr_headroom,
                    },
                );
            }
            FaceKind::KpiTextReadout => {
                let sp = expanded
                    .first()
                    .map(|(s, _)| s.clone())
                    .unwrap_or_else(|| SensorPath::new("soc", "package", "watts"));
                let unit = if sp.metric.as_str() == "watts" {
                    "W"
                } else if sp.metric.as_str().contains("celsius") {
                    "C"
                } else if sp.metric.as_str() == "mhz" {
                    "MHz"
                } else {
                    "%"
                };
                let display = sp
                    .resolve_value_with_hw(&self.snapshot, hw)
                    .map(|v| v.format_display(unit))
                    .unwrap_or_else(|| "Unavailable".to_string());
                ui.label(
                    RichText::new(display)
                        .font(FontId::new(
                            if compact { 16.0 } else { 22.0 },
                            FontFamily::Name("led".into()),
                        ))
                        .color(accent),
                );
            }
            FaceKind::HorizontalCompositionBar => {
                for (i, (sp, lbl)) in expanded.iter().enumerate() {
                    if let Some(val) = sp.resolve_value_with_hw(&self.snapshot, hw) {
                        let col = self.sensor_accent(tile, sp, i, accent);
                        let frac = val.normalized_fraction(100.0).unwrap_or(0.0) as f32;
                        let txt = val.format_display("%");
                        ui.horizontal(|ui| {
                            ui.label(format!("{lbl}: {txt}"));
                        });
                        let (r, _) = ui.allocate_exact_size(
                            Vec2::new(ui.available_width().max(80.0), 8.0),
                            egui::Sense::hover(),
                        );
                        ui.painter().rect_filled(
                            r,
                            CornerRadius::same(2),
                            Color32::from_rgb(12, 16, 22),
                        );
                        ui.painter().rect_filled(
                            Rect::from_min_size(r.min, Vec2::new(r.width() * frac, 8.0)),
                            CornerRadius::same(2),
                            col,
                        );
                    }
                }
            }
            FaceKind::ProcessTable => {
                let rows = self.app.process_table.build_rows(&self.snapshot);
                egui::Grid::new(format!("tile_proc_{}", tile.id))
                    .num_columns(4)
                    .striped(true)
                    .spacing([10.0, 3.0])
                    .show(ui, |ui| {
                        ui.label(RichText::new("PID").small().strong().color(accent));
                        ui.label(RichText::new("Name").small().strong().color(accent));
                        ui.label(RichText::new("CPU %").small().strong().color(accent));
                        ui.label(RichText::new("Mem").small().strong().color(accent));
                        ui.end_row();
                        for r in rows.iter().take(6) {
                            ui.label(r.snapshot.key.pid.to_string());
                            ui.label(r.snapshot.name.as_str());
                            ui.label(r.snapshot.cpu_percent.format_with_unit(1, "%"));
                            ui.label(format!(
                                "{:.1} MiB",
                                metric_mib(&r.snapshot.phys_footprint_bytes)
                            ));
                            ui.end_row();
                        }
                    });
            }
            FaceKind::ApplicationsTable => {
                self.platform_cache.poll_async_workers();
                if self.platform_cache.installed_apps.is_empty()
                    && !self.platform_cache.apps_loading
                {
                    self.platform_cache.trigger_apps_refresh();
                }
                if self.platform_cache.installed_apps.is_empty() {
                    ui.label("Scanning installed applications...");
                } else {
                    egui::Grid::new(format!("tile_apps_{}", tile.id))
                        .num_columns(5)
                        .striped(true)
                        .spacing([10.0, 3.0])
                        .show(ui, |ui| {
                            ui.label(RichText::new("App").small().strong().color(accent));
                            ui.label(RichText::new("Version").small().strong().color(accent));
                            ui.label(RichText::new("CPU %").small().strong().color(accent));
                            ui.label(RichText::new("Mem").small().strong().color(accent));
                            ui.label(RichText::new("Size").small().strong().color(accent));
                            ui.end_row();
                            for app in self.platform_cache.installed_apps.iter().take(6) {
                                let usage = self.platform_cache.app_live_usage.get(&app.path);
                                let cpu_pct = usage.map_or(0.0, |u| u.cpu_percent);
                                let mem_b = usage.map_or(0, |u| u.memory_bytes);
                                ui.label(app.name.as_str());
                                ui.label(app.version.as_str());
                                ui.label(format!("{cpu_pct:.1}%"));
                                ui.label(format_bytes(mem_b));
                                ui.label(
                                    app.size_bytes
                                        .map(format_bytes)
                                        .unwrap_or_else(|| "...".into()),
                                );
                                ui.end_row();
                            }
                        });
                }
            }
        }
    }

    fn draw_performance_view(&mut self, ui: &mut egui::Ui, accent: Color32) {
        hig_view_header(
            ui,
            "Performance Telemetry Studio",
            "Live high-resolution processor, core cluster, unified memory, graphics, and I/O throughput curves.",
            accent,
        );

        let sp = &self.snapshot.hardware_telemetry.soc_power;
        let th = &self.snapshot.hardware_telemetry.thermals;
        let latest_user = self
            .perf_cpu_user_history
            .samples
            .last()
            .and_then(|s| s.value);
        let latest_sys = self
            .perf_cpu_sys_history
            .samples
            .last()
            .and_then(|s| s.value);

        egui::ScrollArea::vertical()
            .id_salt("performance_studio_scroll")
            .max_height((ui.available_height() - 4.0).max(200.0))
            .show(ui, |ui| {
                // Card 1: Processor Load Card
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Processor Load (Total, User & System)",
                        "Real-time CPU utilization across all logical cores with 4-band non-linear history.",
                        accent,
                    );
                    ui.horizontal_wrapped(|ui| {
                        hig_status_pill(
                            ui,
                            &format!(
                                "Current: {}",
                                self.snapshot.system.cpu_percent.format_with_unit(1, "%")
                            ),
                            accent,
                        );
                        hig_status_pill(
                            ui,
                            &format!(
                                "User: {}",
                                latest_user.map_or_else(
                                    || self.snapshot.system.cpu_user_percent.format_with_unit(1, "%"),
                                    |v| format!("{v:.1}%")
                                )
                            ),
                            hig_blue(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!(
                                "System: {}",
                                latest_sys.map_or_else(
                                    || self.snapshot.system.cpu_kernel_percent.format_with_unit(1, "%"),
                                    |v| format!("{v:.1}%")
                                )
                            ),
                            hig_red(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!(
                                "Load Avg: {} / {} / {}",
                                self.snapshot.system.load_avg_1m.format_with_unit(2, ""),
                                self.snapshot.system.load_avg_5m.format_with_unit(2, ""),
                                self.snapshot.system.load_avg_15m.format_with_unit(2, "")
                            ),
                            hig_gray(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Active Processes: {}", self.snapshot.processes.len()),
                            hig_green(ui),
                        );
                    });
                    ui.add_space(6.0);
                    let cpu_series = [
                        ("Total CPU", accent, &self.app.cpu_history),
                        (
                            "User",
                            Color32::from_rgb(10, 132, 255),
                            &self.perf_cpu_user_history,
                        ),
                        (
                            "System",
                            Color32::from_rgb(255, 69, 58),
                            &self.perf_cpu_sys_history,
                        ),
                    ];
                    draw_multi_series_history_canvas(
                        ui,
                        "",
                        &cpu_series,
                        accent,
                        155.0,
                        true,
                        48,
                        true,
                        self.app.theme.smooth_graph_curves,
                        self.app.theme.visual_bloom,
                        self.app.theme.visual_bloom_level,
                        false,
                        0.0,
                        100.0,
                        "%",
                    );
                });

                ui.add_space(10.0);

                // Card 2: Efficiency & Performance Core Cards
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Efficiency & Performance Core Activity",
                        "Per-core utilization, cluster classification, and recent load history.",
                        accent,
                    );
                    if self.snapshot.system.cores.is_empty() {
                        ui.label("Per-core processor telemetry is pending warm-up.");
                    } else {
                        egui::Grid::new("per_core_grid")
                            .num_columns(5)
                            .spacing([10.0, 10.0])
                            .show(ui, |ui| {
                                for (idx, core) in self.snapshot.system.cores.iter().enumerate() {
                                    let (cluster_tag, cluster_col) = match core.class {
                                        LogicalProcessorClass::Efficiency => {
                                            ("Efficiency", hig_green(ui))
                                        }
                                        LogicalProcessorClass::Performance => {
                                            ("Performance", hig_blue(ui))
                                        }
                                        LogicalProcessorClass::Unknown => {
                                            ("Core", hig_gray(ui))
                                        }
                                    };
                                    let pct_opt = measured_opt(&core.total_percent);
                                    let pct_frac =
                                        (pct_opt.unwrap_or(0.0) / 100.0).clamp(0.0, 1.0) as f32;
                                    let pct_text = core.total_percent.format_with_unit(1, "%");
                                    let val_col = if pct_opt.is_some_and(|v| v >= 85.0) {
                                        hig_red(ui)
                                    } else if pct_opt.is_some_and(|v| v >= 55.0) {
                                        hig_amber(ui)
                                    } else {
                                        cluster_col
                                    };

                                    egui::Frame::new()
                                        .fill(ui.visuals().faint_bg_color)
                                        .stroke(ui.visuals().widgets.noninteractive.bg_stroke)
                                        .corner_radius(CornerRadius::same(8))
                                        .inner_margin(egui::Margin::symmetric(10, 8))
                                        .show(ui, |ui| {
                                            ui.vertical(|ui| {
                                                ui.set_min_width(142.0);
                                                ui.set_max_width(168.0);
                                                ui.label(
                                                    RichText::new(format!(
                                                        "Core {} · {cluster_tag}",
                                                        core.core_index + 1
                                                    ))
                                                    .font(FontId::proportional(11.5))
                                                    .strong(),
                                                );
                                                ui.add_space(3.0);
                                                ui.horizontal(|ui| {
                                                    ui.label(
                                                        RichText::new(&pct_text)
                                                            .font(FontId::proportional(15.0))
                                                            .strong()
                                                            .color(val_col),
                                                    );
                                                    let badge = match core.class {
                                                        LogicalProcessorClass::Efficiency => "E",
                                                        LogicalProcessorClass::Performance => "P",
                                                        LogicalProcessorClass::Unknown => "C",
                                                    };
                                                    hig_status_pill(ui, badge, cluster_col);
                                                });
                                                ui.add_space(3.0);
                                                draw_hig_progress_bar(
                                                    ui, 130.0, 6.0, pct_frac, val_col,
                                                );
                                                if let Some(spark) = self
                                                    .perf_core_sparklines
                                                    .get(core.core_index as usize)
                                                {
                                                    ui.add_space(4.0);
                                                    draw_inline_sparkline(
                                                        ui,
                                                        spark,
                                                        true,
                                                        "",
                                                        cluster_col,
                                                    );
                                                }
                                            });
                                        });
                                    if (idx + 1).is_multiple_of(5) {
                                        ui.end_row();
                                    }
                                }
                            });
                    }
                });

                ui.add_space(10.0);

                // Card 3: Memory & Swap Pressure Card
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Unified Memory & Swap Pressure",
                        "Physical RAM allocation across apps, kernel wired memory, compression, and disk swap.",
                        accent,
                    );
                    let mem_pct = measured_opt(&self.snapshot.system.mem_used_percent);
                    let (pres_lbl, pres_col) = match mem_pct {
                        Some(m) if m > 88.0 => ("High", hig_red(ui)),
                        Some(m) if m >= 75.0 => ("Moderate", hig_amber(ui)),
                        _ => ("Normal", hig_green(ui)),
                    };
                    let total_b = measured_zero(&self.snapshot.system.mem_total_bytes).max(1.0);
                    let active_b = measured_zero(&self.snapshot.system.mem_active_bytes);
                    let wired_b = measured_zero(&self.snapshot.system.mem_wired_bytes);
                    let comp_b = measured_zero(&self.snapshot.system.mem_compressed_bytes);
                    let cached_free_b = measured_zero(&self.snapshot.system.mem_cached_files_bytes)
                        + measured_zero(&self.snapshot.system.mem_free_bytes);

                    ui.horizontal_wrapped(|ui| {
                        hig_status_pill(ui, &format!("Memory Pressure: {pres_lbl}"), pres_col);
                        hig_status_pill(
                            ui,
                            &format!("Total RAM: {}", format_bytes(total_b as u64)),
                            hig_gray(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("App Memory: {}", format_bytes(active_b as u64)),
                            hig_blue(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Wired: {}", format_bytes(wired_b as u64)),
                            hig_amber(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Compressed: {}", format_bytes(comp_b as u64)),
                            hig_purple(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Cached / Free: {}", format_bytes(cached_free_b as u64)),
                            hig_green(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!(
                                "Swap Used: {} / {}",
                                format_metric_bytes(&self.snapshot.system.swap_used_bytes),
                                format_metric_bytes(&self.snapshot.system.swap_total_bytes)
                            ),
                            hig_gray(ui),
                        );
                    });
                    ui.add_space(6.0);
                    let mem_segs = [
                        ("App Memory", active_b, hig_blue(ui)),
                        ("Wired", wired_b, hig_amber(ui)),
                        ("Compressed", comp_b, hig_purple(ui)),
                        ("Cached / Free", cached_free_b, hig_green(ui)),
                    ];
                    draw_stacked_bar(ui, ui.available_width(), 18.0, 5, &mem_segs, total_b);
                    ui.add_space(6.0);
                    let mem_series = [("Memory Used %", hig_blue(ui), &self.perf_mem_history)];
                    draw_multi_series_history_canvas(
                        ui,
                        "",
                        &mem_series,
                        accent,
                        120.0,
                        true,
                        48,
                        true,
                        self.app.theme.smooth_graph_curves,
                        self.app.theme.visual_bloom,
                        self.app.theme.visual_bloom_level,
                        false,
                        0.0,
                        100.0,
                        "%",
                    );
                });

                ui.add_space(10.0);

                // Card 4: 2-Column Side-by-Side Graphics/Power & Disk/Network Throughput
                ui.columns(2, |cols| {
                    hig_card(&mut cols[0], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Graphics & Package Power",
                            "Live Apple GPU utilization, SoC package power draw, Neural Engine, and die thermals.",
                            accent,
                        );
                        ui.horizontal_wrapped(|ui| {
                            hig_status_pill(
                                ui,
                                &format!("GPU Clock: {}", sp.gpu_clock_mhz.format_with_unit(0, "MHz")),
                                hig_green(ui),
                            );
                            hig_status_pill(
                                ui,
                                &format!(
                                    "ANE Residency: {}",
                                    sp.ane_active_residency_percent.format_with_unit(1, "%")
                                ),
                                hig_blue(ui),
                            );
                            let die_col = match measured_opt(&th.max_die_celsius) {
                                Some(t) if t > 85.0 => hig_red(ui),
                                Some(t) if t >= 65.0 => hig_amber(ui),
                                Some(_) => hig_green(ui),
                                None => hig_gray(ui),
                            };
                            hig_status_pill(
                                ui,
                                &format!("Die Temp: {}", th.max_die_celsius.format_with_unit(1, "C")),
                                die_col,
                            );
                        });
                        ui.add_space(6.0);
                        let gfx_series = [
                            ("GPU Active %", hig_green(ui), &self.perf_gpu_history),
                            ("Package Watts", hig_amber(ui), &self.perf_pkg_watts_history),
                        ];
                        draw_multi_series_history_canvas(
                            ui,
                            "",
                            &gfx_series,
                            accent,
                            164.0,
                            true,
                            44,
                            true,
                            self.app.theme.smooth_graph_curves,
                            self.app.theme.visual_bloom,
                            self.app.theme.visual_bloom_level,
                            true,
                            0.0,
                            100.0,
                            "",
                        );
                    });

                    hig_card(&mut cols[1], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Disk & Network I/O Throughput",
                            "Aggregate volume read/write rate and network interface download/upload bandwidth.",
                            accent,
                        );
                        let disk_series = [
                            ("Disk Read", hig_blue(ui), &self.perf_disk_read_history),
                            ("Disk Write", hig_amber(ui), &self.perf_disk_write_history),
                        ];
                        draw_multi_series_history_canvas(
                            ui,
                            "Disk Throughput (MB/s)",
                            &disk_series,
                            accent,
                            82.0,
                            true,
                            44,
                            true,
                            self.app.theme.smooth_graph_curves,
                            self.app.theme.visual_bloom,
                            self.app.theme.visual_bloom_level,
                            true,
                            0.0,
                            100.0,
                            " MB/s",
                        );
                        ui.add_space(6.0);
                        let net_series = [
                            ("Net Down", hig_green(ui), &self.perf_net_down_history),
                            ("Net Up", hig_purple(ui), &self.perf_net_up_history),
                        ];
                        draw_multi_series_history_canvas(
                            ui,
                            "Network Throughput (Mbps)",
                            &net_series,
                            accent,
                            82.0,
                            true,
                            44,
                            true,
                            self.app.theme.smooth_graph_curves,
                            self.app.theme.visual_bloom,
                            self.app.theme.visual_bloom_level,
                            true,
                            0.0,
                            100.0,
                            " Mbps",
                        );
                    });
                });
            });
    }

    fn draw_processes_view(&mut self, ui: &mut egui::Ui, accent: Color32) -> bool {
        let mut cols_dirty = false;
        hig_view_header(
            ui,
            "Processes & Applications",
            "Inspect running applications and system daemons, resource impact, and PID-reuse-safe process controls.",
            accent,
        );

        let total_procs = self.snapshot.processes.len();
        let user_procs = self
            .snapshot
            .processes
            .iter()
            .filter(|p| p.key.uid.is_some_and(|u| u >= 500))
            .count();
        let daemon_procs = total_procs.saturating_sub(user_procs);
        let cpu_leader = self
            .snapshot
            .processes
            .iter()
            .max_by(|a, b| {
                measured_zero(&a.cpu_percent)
                    .partial_cmp(&measured_zero(&b.cpu_percent))
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
            .map(|p| format!("{} ({})", p.name, p.cpu_percent.format_with_unit(1, "%")))
            .unwrap_or_else(|| "None".to_string());
        let mem_leader = self
            .snapshot
            .processes
            .iter()
            .max_by(|a, b| {
                measured_zero(&a.phys_footprint_bytes)
                    .partial_cmp(&measured_zero(&b.phys_footprint_bytes))
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
            .map(|p| {
                format!(
                    "{} ({:.0} MiB)",
                    p.name,
                    metric_mib(&p.phys_footprint_bytes)
                )
            })
            .unwrap_or_else(|| "None".to_string());

        hig_card(ui, |ui| {
            ui.set_min_width(ui.available_width());
            ui.horizontal_wrapped(|ui| {
                hig_status_pill(ui, &format!("Total Processes: {total_procs}"), accent);
                hig_status_pill(ui, &format!("User Apps: {user_procs}"), hig_blue(ui));
                hig_status_pill(ui, &format!("System Daemons: {daemon_procs}"), hig_gray(ui));
                hig_status_pill(ui, &format!("CPU Leader: {cpu_leader}"), hig_amber(ui));
                hig_status_pill(ui, &format!("Memory Leader: {mem_leader}"), hig_purple(ui));
            });
        });
        ui.add_space(8.0);

        if daemon_procs == 0 && total_procs > 0 {
            let (headline, why, steps) = if cfg!(target_os = "macos") {
                (
                    "Showing processes owned by your macOS user account (UID >= 500)",
                    "Standard macOS user privileges allow live CPU, memory footprint, and coalition inspection for your own apps without root access.",
                    "How to inspect root & system daemons (UID 0): Launch Nexus with elevated privileges in Terminal (`sudo nexus-monitor` or `sudo nexus top`) to unlock kernel/root process telemetry.",
                )
            } else if cfg!(target_os = "linux") {
                (
                    "Showing processes visible to your Linux user account",
                    "Standard Linux user privileges allow live /proc/[pid]/stat CPU, RSS memory, and thread inspection without root access.",
                    "How to inspect root-only /proc/[pid]/fd & io descriptors: Run `sudo nexus-monitor` or `sudo nexus top` in Terminal.",
                )
            } else {
                (
                    "Showing processes visible to your Windows user session",
                    "Standard Windows privileges allow live Win32 Toolhelp32 and working-set memory inspection for active user processes.",
                    "How to inspect protected SYSTEM services: Right-click Nexus System Monitor and select 'Run as administrator'.",
                )
            };
            let _ = draw_permission_guidance_card(
                ui,
                "User-Session Scope",
                hig_blue(ui),
                headline,
                why,
                steps,
                None,
                false,
                None,
            );
            ui.add_space(8.0);
        }

        hig_card(ui, |ui| {
            ui.set_min_width(ui.available_width());
            ui.horizontal_wrapped(|ui| {
                ui.label(RichText::new("Search:").strong());
                ui.add(
                    egui::TextEdit::singleline(&mut self.app.process_table.search_query)
                        .hint_text("Filter by process name or PID...")
                        .desired_width(210.0),
                );
                ui.separator();

                let cur_mode = self
                    .app
                    .process_table
                    .grouping_mode
                    .unwrap_or(ProcessGroupingMode::Flat);
                if ui
                    .selectable_label(cur_mode == ProcessGroupingMode::Flat, "Flat")
                    .clicked()
                {
                    self.app.process_table.grouping_mode = Some(ProcessGroupingMode::Flat);
                }
                if ui
                    .selectable_label(
                        cur_mode == ProcessGroupingMode::ParentChildHierarchy,
                        "Parent-Child Tree",
                    )
                    .clicked()
                {
                    self.app.process_table.grouping_mode =
                        Some(ProcessGroupingMode::ParentChildHierarchy);
                }
                if ui
                    .selectable_label(
                        cur_mode == ProcessGroupingMode::CoalitionAppGroup,
                        "Application Group",
                    )
                    .clicked()
                {
                    self.app.process_table.grouping_mode =
                        Some(ProcessGroupingMode::CoalitionAppGroup);
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .selectable_label(
                            self.platform_cache.process_columns_open,
                            "Configure Columns...",
                        )
                        .clicked()
                    {
                        self.platform_cache.process_columns_open =
                            !self.platform_cache.process_columns_open;
                    }
                });
            });

            if self.platform_cache.process_columns_open {
                ui.add_space(6.0);
                if draw_configure_columns_panel(
                    ui,
                    "proc_cols",
                    &mut self.platform_cache.process_columns,
                    default_process_columns,
                    accent,
                ) {
                    cols_dirty = true;
                }
            }
            ui.add_space(6.0);

            let rows = self.app.process_table.build_rows(&self.snapshot);
            let visible_cols: Vec<TableColumnConfig> = self
                .platform_cache
                .process_columns
                .iter()
                .filter(|c| c.style != ColumnDisplayStyle::Hidden)
                .cloned()
                .collect();
            let total_mem_mib = match self.snapshot.system.mem_total_bytes.reading {
                MetricReading::Measured { value, .. } if value > 0.0 => value / (1024.0 * 1024.0),
                _ => 16_384.0,
            };
            let mut quit_target: Option<ProcessKey> = None;

            egui::ScrollArea::both()
                .id_salt("processes_table_scroll")
                .max_height((ui.available_height() - 12.0).max(220.0))
                .show(ui, |ui| {
                    egui::Grid::new("processes_table_grid")
                        .num_columns(visible_cols.len().max(1))
                        .striped(true)
                        .spacing([9.0, 5.0])
                        .show(ui, |ui| {
                            for col in &visible_cols {
                                ui.label(RichText::new(col.label.as_str()).strong());
                            }
                            ui.end_row();

                            for row in rows.iter().take(60) {
                                let is_sel =
                                    self.app.process_table.selected_key == Some(row.snapshot.key);
                                let name_color = if row.exited_wash.is_some() {
                                    hig_red(ui)
                                } else if row.rank_glow_rgba.is_some() {
                                    hig_green(ui)
                                } else {
                                    ui.visuals().text_color()
                                };
                                let spark = self
                                    .platform_cache
                                    .process_sparklines
                                    .get(&row.snapshot.key);
                                let cpu_pct = measured_opt(&row.snapshot.cpu_percent).unwrap_or(0.0);
                                let mem_mib = metric_mib(&row.snapshot.phys_footprint_bytes);

                                for col in &visible_cols {
                                    match col.id.as_str() {
                                        "pid" => {
                                            ui.label(row.snapshot.key.pid.to_string());
                                        }
                                        "token" => {
                                            ui.label(row.snapshot.key.creation_token_us.to_string());
                                        }
                                        "name" => {
                                            let indent = if row.depth > 0 { "  └─ " } else { "" };
                                            if ui
                                                .selectable_label(
                                                    is_sel,
                                                    RichText::new(format!(
                                                        "{indent}{}",
                                                        row.snapshot.name
                                                    ))
                                                    .color(name_color),
                                                )
                                                .clicked()
                                            {
                                                self.app.process_table.selected_key =
                                                    Some(row.snapshot.key);
                                                self.app.process_table.inspector_open = true;
                                                self.editing_tile_id = None;
                                            }
                                        }
                                        "impact" => {
                                            if row.exited_wash.is_some() {
                                                hig_status_pill(ui, "Exited", hig_red(ui));
                                            } else if cpu_pct >= 35.0 {
                                                hig_status_pill(ui, "High CPU", hig_red(ui));
                                            } else if mem_mib >= 1536.0 {
                                                hig_status_pill(ui, "High Memory", hig_purple(ui));
                                            } else if cpu_pct >= 10.0 || mem_mib >= 512.0 {
                                                hig_status_pill(ui, "Moderate", hig_blue(ui));
                                            } else {
                                                hig_status_pill(ui, "Light", hig_green(ui));
                                            }
                                        }
                                        "group" => {
                                            ui.label(row.group_name.as_str());
                                        }
                                        "mem" => {
                                            let label = format!("{mem_mib:.1} MiB");
                                            let mem_bg = srgb_rgba_to_color32(row.mem_heat_rgba);
                                            egui::Frame::new()
                                                .fill(mem_bg)
                                                .corner_radius(CornerRadius::same(4))
                                                .inner_margin(egui::Margin::symmetric(4, 2))
                                                .show(ui, |ui| match col.style {
                                                    ColumnDisplayStyle::LineChart
                                                    | ColumnDisplayStyle::LineChartScaled100 => {
                                                        if let Some(sp) = spark {
                                                            draw_inline_sparkline(
                                                                ui,
                                                                &sp.mem_mib,
                                                                false,
                                                                &label,
                                                                accent,
                                                            );
                                                        } else {
                                                            ui.label(label);
                                                        }
                                                    }
                                                    ColumnDisplayStyle::HorizontalBar => {
                                                        let frac = (mem_mib / total_mem_mib)
                                                            .clamp(0.0, 1.0)
                                                            as f32;
                                                        draw_inline_hbar(ui, frac, &label, accent);
                                                    }
                                                    _ => {
                                                        ui.label(label);
                                                    }
                                                });
                                        }
                                        "cpu" => {
                                            let label =
                                                row.snapshot.cpu_percent.format_with_unit(1, "%");
                                            let cpu_bg = srgb_rgba_to_color32(row.cpu_heat_rgba);
                                            egui::Frame::new()
                                                .fill(cpu_bg)
                                                .corner_radius(CornerRadius::same(4))
                                                .inner_margin(egui::Margin::symmetric(4, 2))
                                                .show(ui, |ui| match col.style {
                                                    ColumnDisplayStyle::LineChart => {
                                                        if let Some(sp) = spark {
                                                            draw_inline_sparkline(
                                                                ui, &sp.cpu, false, &label, accent,
                                                            );
                                                        } else {
                                                            ui.label(label);
                                                        }
                                                    }
                                                    ColumnDisplayStyle::LineChartScaled100 => {
                                                        if let Some(sp) = spark {
                                                            draw_inline_sparkline(
                                                                ui, &sp.cpu, true, &label, accent,
                                                            );
                                                        } else {
                                                            ui.label(label);
                                                        }
                                                    }
                                                    ColumnDisplayStyle::HorizontalBar => {
                                                        draw_inline_hbar(
                                                            ui,
                                                            (cpu_pct / 100.0) as f32,
                                                            &label,
                                                            accent,
                                                        );
                                                    }
                                                    _ => {
                                                        ui.label(label);
                                                    }
                                                });
                                        }
                                        "actions" => {
                                            ui.horizontal(|ui| {
                                                if ui
                                                    .small_button("Inspect")
                                                    .on_hover_text("Open Process Inspector dossier")
                                                    .clicked()
                                                {
                                                    self.app.process_table.selected_key =
                                                        Some(row.snapshot.key);
                                                    self.app.process_table.inspector_open = true;
                                                    self.editing_tile_id = None;
                                                }
                                                if ui
                                                    .small_button("Quit (Dry)")
                                                    .on_hover_text(
                                                        "Verify PID + creation token and simulate SIGTERM without killing process",
                                                    )
                                                    .clicked()
                                                {
                                                    quit_target = Some(row.snapshot.key);
                                                }
                                                if ui
                                                    .small_button("Pin")
                                                    .on_hover_text("Pin process record to SQLite SnapshotDatabase")
                                                    .clicked()
                                                {
                                                    let _ = self
                                                        .snapshot_db
                                                        .pin_exited_process(&row.snapshot);
                                                    self.action_status = format!(
                                                        "Pinned PID {} (token={}) to SQLite SnapshotDatabase",
                                                        row.snapshot.key.pid,
                                                        row.snapshot.key.creation_token_us
                                                    );
                                                }
                                            });
                                        }
                                        _ => {
                                            ui.label("-");
                                        }
                                    }
                                }
                                ui.end_row();
                            }
                        });
                });

            if let Some(key) = quit_target {
                self.run_process_action(key, ProcessActionType::Terminate, None, None);
            }
        });
        cols_dirty
    }

    fn draw_apple_silicon_view(&mut self, ui: &mut egui::Ui, accent: Color32) {
        let sp = &self.snapshot.hardware_telemetry.soc_power;
        let th = &self.snapshot.hardware_telemetry.thermals;
        let chip_name = self
            .platform_cache
            .hardware
            .map(|h| h.chip_model.as_str())
            .unwrap_or_else(|| self.snapshot.system.gpu_model.as_str());

        let (soc_title, soc_sub) = if cfg!(target_os = "macos") {
            (
                "Apple Silicon Architecture & Thermals",
                "Authoritative libIOReport cluster residency, Neural Engine, Media Engine, and AppleSMC die temperatures.",
            )
        } else if cfg!(target_os = "linux") {
            (
                "Processor Architecture & Thermals",
                "Linux CPU cluster topology, cpufreq scaling telemetry, and sysfs thermal zone sensors.",
            )
        } else {
            (
                "Processor Architecture & Thermals",
                "Windows processor topology, power domains, and hardware thermal sensors.",
            )
        };
        hig_view_header(ui, soc_title, soc_sub, accent);

        let die_c = measured_opt(&th.max_die_celsius);
        let (temp_badge, temp_col) = match die_c {
            Some(t) if t > 85.0 => (format!("Hot · {t:.1} C"), hig_red(ui)),
            Some(t) if t >= 65.0 => (format!("Warm · {t:.1} C"), hig_amber(ui)),
            Some(t) => (format!("Cool · {t:.1} C"), hig_green(ui)),
            None => ("Temp Unavailable".to_string(), hig_gray(ui)),
        };

        egui::ScrollArea::vertical()
            .id_salt("apple_silicon_scroll")
            .max_height((ui.available_height() - 4.0).max(200.0))
            .show(ui, |ui| {
                // Top Summary Strip
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal_wrapped(|ui| {
                        hig_status_pill(ui, &format!("Chip: {chip_name}"), accent);
                        hig_status_pill(
                            ui,
                            &format!(
                                "Package Power: {}",
                                sp.package_watts.format_with_unit(2, "W")
                            ),
                            hig_amber(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!(
                                "System Power In: {}",
                                sp.system_power_in_watts.format_with_unit(2, "W")
                            ),
                            hig_blue(ui),
                        );
                        hig_status_pill(ui, &temp_badge, temp_col);
                    });
                });

                ui.add_space(10.0);

                // Row 1: Efficiency Cluster & Performance Cluster
                ui.columns(2, |cols| {
                    hig_card(&mut cols[0], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Efficiency Cluster",
                            "Background tasks and low-power background threads run on E-cores to save battery.",
                            accent,
                        );
                        let e_res = measured_opt(&sp.e_cluster_active_residency_percent);
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(
                                    sp.e_cluster_active_residency_percent.format_with_unit(1, "%"),
                                )
                                .font(FontId::proportional(20.0))
                                .strong()
                                .color(hig_green(ui)),
                            );
                            let e_mhz_suffix = measured_opt(&sp.e_cluster_mhz)
                                .map_or_else(String::new, |m| format!(" · {m:.0} MHz"));
                            hig_status_pill(
                                ui,
                                &format!(
                                    "{} cores{e_mhz_suffix}",
                                    self.snapshot.system.efficiency_core_count
                                ),
                                hig_green(ui),
                            );
                        });
                        ui.add_space(4.0);
                        draw_hig_progress_bar(
                            ui,
                            ui.available_width(),
                            10.0,
                            (e_res.unwrap_or(0.0) / 100.0) as f32,
                            hig_green(ui),
                        );
                    });

                    hig_card(&mut cols[1], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Performance Cluster",
                            "Interactive applications, builds, and heavy compute run on high-clock P-cores.",
                            accent,
                        );
                        let p_res = measured_opt(&sp.p_cluster_active_residency_percent);
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(
                                    sp.p_cluster_active_residency_percent.format_with_unit(1, "%"),
                                )
                                .font(FontId::proportional(20.0))
                                .strong()
                                .color(hig_blue(ui)),
                            );
                            let p_mhz_suffix = measured_opt(&sp.p_cluster_mhz)
                                .map_or_else(String::new, |m| format!(" · {m:.0} MHz"));
                            hig_status_pill(
                                ui,
                                &format!(
                                    "{} cores{p_mhz_suffix}",
                                    self.snapshot.system.performance_core_count
                                ),
                                hig_blue(ui),
                            );
                        });
                        ui.add_space(4.0);
                        draw_hig_progress_bar(
                            ui,
                            ui.available_width(),
                            10.0,
                            (p_res.unwrap_or(0.0) / 100.0) as f32,
                            hig_blue(ui),
                        );
                    });
                });

                ui.add_space(10.0);

                // Row 2: Apple GPU & Neural Engine (ANE)
                ui.columns(2, |cols| {
                    hig_card(&mut cols[0], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Apple GPU",
                            "Unified-memory Metal graphics and compute cores.",
                            accent,
                        );
                        let gpu_pct = measured_opt(&self.snapshot.system.gpu_utilization_percent);
                        ui.horizontal_wrapped(|ui| {
                            ui.label(
                                RichText::new(
                                    self.snapshot
                                        .system
                                        .gpu_utilization_percent
                                        .format_with_unit(1, "%"),
                                )
                                .font(FontId::proportional(20.0))
                                .strong()
                                .color(accent),
                            );
                            let gpu_badge = match measured_opt(&sp.gpu_clock_mhz) {
                                Some(mhz) => format!(
                                    "{mhz:.0} MHz · {}",
                                    sp.gpu_watts.format_with_unit(2, "W")
                                ),
                                None => format!("Power: {}", sp.gpu_watts.format_with_unit(2, "W")),
                            };
                            hig_status_pill(ui, &gpu_badge, accent);
                        });
                        ui.add_space(4.0);
                        draw_hig_progress_bar(
                            ui,
                            ui.available_width(),
                            10.0,
                            (gpu_pct.unwrap_or(0.0) / 100.0) as f32,
                            accent,
                        );
                        ui.add_space(4.0);
                        ui.label(
                            RichText::new(format!(
                                "Memory In-Use: {} · Allocated: {}",
                                format_metric_bytes(&self.snapshot.system.gpu_in_use_memory_bytes),
                                format_metric_bytes(&self.snapshot.system.gpu_alloc_memory_bytes),
                            ))
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                        );
                    });

                    hig_card(&mut cols[1], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Neural Engine (ANE)",
                            "Dedicated machine-learning matrix accelerators for CoreML and on-device AI.",
                            accent,
                        );
                        let ane_res = measured_opt(&sp.ane_active_residency_percent);
                        let ane_watts = measured_opt(&sp.ane_watts);
                        let ane_headline = if ane_res.is_some() {
                            sp.ane_active_residency_percent.format_with_unit(1, "%")
                        } else if let Some(w) = ane_watts {
                            if w <= 0.005 {
                                "Idle (0.00 W)".to_string()
                            } else {
                                format!("Active ({w:.2} W)")
                            }
                        } else {
                            sp.ane_active_residency_percent.format_with_unit(1, "%")
                        };
                        ui.horizontal_wrapped(|ui| {
                            ui.label(
                                RichText::new(ane_headline)
                                    .font(FontId::proportional(20.0))
                                    .strong()
                                    .color(hig_purple(ui)),
                            );
                            hig_status_pill(
                                ui,
                                &format!(
                                    "ANE Power: {} · SoC Floor: {}",
                                    sp.ane_watts.format_with_unit(2, "W"),
                                    sp.soc_floor_residency_percent.format_with_unit(1, "%")
                                ),
                                hig_purple(ui),
                            );
                        });
                        ui.add_space(4.0);
                        draw_hig_progress_bar(
                            ui,
                            ui.available_width(),
                            10.0,
                            (ane_res.unwrap_or(0.0) / 100.0) as f32,
                            hig_purple(ui),
                        );
                        ui.add_space(4.0);
                        ui.label(
                            RichText::new(format!(
                                "PMP / Auxiliary SoC Power: {}",
                                sp.pmp_power_watts.format_with_unit(2, "W")
                            ))
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                        );
                    });
                });

                ui.add_space(10.0);

                // Row 3: Media Engine Blocks & Thermal Sensors
                ui.columns(2, |cols| {
                    hig_card(&mut cols[0], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Media Engine Blocks",
                            "Dedicated hardware video encoders, decoders, and ProRes pipelines.",
                            accent,
                        );
                        if sp.media_blocks.is_empty() {
                            ui.label(
                                RichText::new("No active media engine residency counters reported.")
                                    .color(ui.visuals().weak_text_color()),
                            );
                        } else {
                            for block in &sp.media_blocks {
                                let v = measured_opt(&block.active_residency_percent).unwrap_or(0.0);
                                ui.horizontal(|ui| {
                                    ui.add_sized(
                                        [140.0, 18.0],
                                        egui::Label::new(RichText::new(block.label.as_str()).strong()),
                                    );
                                    draw_hig_progress_bar(
                                        ui,
                                        120.0,
                                        8.0,
                                        (v / 100.0).clamp(0.0, 1.0) as f32,
                                        hig_blue(ui),
                                    );
                                    ui.label(block.active_residency_percent.format_with_unit(1, "%"));
                                });
                            }
                        }
                    });

                    hig_card(&mut cols[1], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Thermal Sensors (AppleSMC)",
                            "Selector 2 die temperature sensors and macOS thermal warning state.",
                            accent,
                        );
                        ui.horizontal_wrapped(|ui| {
                            hig_status_pill(ui, &temp_badge, temp_col);
                            hig_status_pill(
                                ui,
                                &format!(
                                    "Warning Level: {}",
                                    th.warning_level.format_with_unit(0, "")
                                ),
                                if measured_zero(&th.warning_level) > 0.0 {
                                    hig_amber(ui)
                                } else {
                                    hig_green(ui)
                                },
                            );
                        });
                        ui.add_space(6.0);
                        if th.sensors.is_empty() {
                            ui.label(
                                RichText::new("No individual SMC die sensors available.")
                                    .color(ui.visuals().weak_text_color()),
                            );
                        } else {
                            egui::ScrollArea::vertical()
                                .id_salt("smc_thermals_scroll")
                                .max_height(210.0)
                                .show(ui, |ui| {
                                    for s in &th.sensors {
                                        let tc = measured_opt(&s.celsius);
                                        let col = match tc {
                                            Some(t) if t > 85.0 => hig_red(ui),
                                            Some(t) if t >= 65.0 => hig_amber(ui),
                                            Some(_) => hig_green(ui),
                                            None => hig_gray(ui),
                                        };
                                        ui.horizontal(|ui| {
                                            ui.add_sized(
                                                [155.0, 18.0],
                                                egui::Label::new(s.name.as_str()).truncate(),
                                            );
                                            draw_hig_progress_bar(
                                                ui,
                                                105.0,
                                                7.0,
                                                (tc.unwrap_or(0.0) / 110.0).clamp(0.0, 1.0) as f32,
                                                col,
                                            );
                                            ui.label(
                                                RichText::new(s.celsius.format_with_unit(1, "C"))
                                                    .color(col),
                                            );
                                        });
                                    }
                                });
                        }
                    });
                });
            });
    }

    fn draw_disk_space_view(&mut self, ui: &mut egui::Ui, accent: Color32) {
        if self.platform_cache.volumes.is_empty() {
            self.platform_cache.volumes = collect_mounted_volumes();
        }
        if self.storage_inspector.is_empty() && !self.storage_inspector_loading {
            self.storage_inspector_loading = true;
            let slot = Arc::clone(&self.storage_inspector_slot);
            std::thread::spawn(move || {
                let data = collect_storage_inspector();
                *slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(data);
            });
        }

        hig_view_header(
            ui,
            "Storage & Disk Space Analyzer",
            "Inspect volume capacity, file category breakdown, interactive 3D/2D maps, duplicate files, and snapshots.",
            accent,
        );

        let (vol_total_b, vol_avail_b, vol_used_b) =
            statvfs_volume_bytes(Path::new(&self.disk_scan_path_input)).unwrap_or_else(|_| {
                if let Some(v) = self
                    .platform_cache
                    .volumes
                    .iter()
                    .find(|v| v.mount_point == "/")
                    .or_else(|| self.platform_cache.volumes.first())
                {
                    let tot = measured_zero(&v.total_bytes) as u64;
                    let avail = measured_zero(&v.available_bytes) as u64;
                    (tot.max(1), avail, tot.saturating_sub(avail))
                } else {
                    (1, 0, 0)
                }
            });

        egui::ScrollArea::vertical()
            .id_salt("disk_space_main_scroll")
            .max_height((ui.available_height() - 4.0).max(220.0))
            .show(ui, |ui| {
                // Top Apple Storage-style Horizontal Multi-Color Capacity Bar
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal_wrapped(|ui| {
                        ui.label(
                            RichText::new(format!("Volume Total: {}", format_bytes(vol_total_b)))
                                .font(FontId::proportional(14.0))
                                .strong(),
                        );
                        ui.separator();
                        ui.label(format!(
                            "Used: {} ({:.1}%)",
                            format_bytes(vol_used_b),
                            self.disk_water_level * 100.0
                        ));
                        ui.separator();
                        ui.label(
                            RichText::new(format!("Free: {}", format_bytes(vol_avail_b)))
                                .strong()
                                .color(hig_green(ui)),
                        );
                    });
                    ui.add_space(6.0);

                    let cap_segs: Vec<(&str, f64, Color32)> = self
                        .disk_report
                        .categories
                        .iter()
                        .map(|c| {
                            (
                                c.category.label(),
                                f64::from(c.share_fraction.max(0.0))
                                    * f64::from(self.disk_water_level.clamp(0.0, 1.0)),
                                srgb_to_color32(c.category.default_color_srgb()),
                            )
                        })
                        .collect();
                    draw_stacked_bar(ui, ui.available_width(), 20.0, 6, &cap_segs, 1.0);
                    ui.add_space(6.0);
                    ui.horizontal_wrapped(|ui| {
                        for c in &self.disk_report.categories {
                            let col = srgb_to_color32(c.category.default_color_srgb());
                            ui.colored_label(
                                col,
                                RichText::new(format!(
                                    "* {} ({:.1}% · {})",
                                    c.category.label(),
                                    c.share_fraction * 100.0,
                                    format_bytes(c.bytes)
                                ))
                                .font(FontId::proportional(11.5)),
                            );
                        }
                    });
                });

                ui.add_space(8.0);

                // Guided Full Disk Access / Storage Permission & Action Card
                if self.disk_omissions.permission_denied_count > 0
                    || self.disk_omissions.other_error_count > 0
                    || !self.disk_omissions.full_disk_access_granted
                {
                    let rescan_clicked = if self.disk_omissions.full_disk_access_granted {
                        let (badge, headline, tip) = if cfg!(target_os = "macos") {
                            (
                                "Full Disk Access Granted",
                                "Protected macOS directories are unlocked",
                                "Tip: Some system-sealed kernel folders remain read-protected by SIP even with Full Disk Access.",
                            )
                        } else if cfg!(target_os = "linux") {
                            (
                                "Filesystem Access Active",
                                "Directory tree scanned with current user permissions",
                                "Tip: Run `sudo nexus-monitor` to inspect root-only paths such as /root or /var/lib/docker.",
                            )
                        } else {
                            (
                                "Volume Access Active",
                                "Directory tree scanned with current Windows privileges",
                                "Tip: Run as Administrator to inspect protected System Volume Information or Windows Update folders.",
                            )
                        };
                        draw_permission_guidance_card(
                            ui,
                            badge,
                            hig_green(ui),
                            headline,
                            &format!(
                                "Storage access is active. {} root/system-only items were skipped ({} other read errors).",
                                self.disk_omissions.permission_denied_count,
                                self.disk_omissions.other_error_count
                            ),
                            tip,
                            None,
                            false,
                            Some("Rescan Now"),
                        )
                    } else if cfg!(target_os = "macos") {
                        draw_permission_guidance_card(
                            ui,
                            "Limited Scope (TCC)",
                            hig_amber(ui),
                            "Grant Full Disk Access to scan protected macOS folders",
                            &format!(
                                "macOS Transparency, Consent, and Control (TCC) protects ~/Library/Mail, Messages, Safari, Containers, and Time Machine folders ({} items skipped in current scan).",
                                self.disk_omissions.permission_denied_count
                            ),
                            "How to enable: 1. Click 'Open Full Disk Access Settings'  ->  2. Click '+' (or drag Nexus System Monitor.app from Finder)  ->  3. Toggle Nexus System Monitor ON and click 'Rescan Now'.",
                            Some((
                                "Open Full Disk Access Settings",
                                "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles",
                            )),
                            true,
                            Some("Rescan Now"),
                        )
                    } else if cfg!(target_os = "linux") {
                        draw_permission_guidance_card(
                            ui,
                            "Limited Scope (POSIX Permissions)",
                            hig_amber(ui),
                            "Some root-owned Linux directories were skipped",
                            &format!(
                                "Linux POSIX permissions restrict non-root access to /root, /proc/*/fd, and protected /var subdirectories ({} items skipped in current scan).",
                                self.disk_omissions.permission_denied_count
                            ),
                            "How to unlock full filesystem scanning: Launch `sudo nexus-monitor` or grant read permissions to the target directory, then click 'Rescan Now'.",
                            None,
                            false,
                            Some("Rescan Now"),
                        )
                    } else {
                        draw_permission_guidance_card(
                            ui,
                            "Limited Scope (UAC / ACL)",
                            hig_amber(ui),
                            "Some protected Windows system folders were skipped",
                            &format!(
                                "Windows ACLs restrict standard user access to System Volume Information and protected administrator directories ({} items skipped in current scan).",
                                self.disk_omissions.permission_denied_count
                            ),
                            "How to unlock full volume scanning: Launch Nexus System Monitor as Administrator, then click 'Rescan Now'.",
                            None,
                            false,
                            Some("Rescan Now"),
                        )
                    };
                    if rescan_clicked {
                        let target = PathBuf::from(self.disk_scan_path_input.trim());
                        if target.exists() {
                            self.start_disk_scan(target);
                        }
                    }
                    ui.add_space(8.0);
                }

                // Segmented Subtab Bar + Scan Root Controls
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal_wrapped(|ui| {
                        for (tab, lbl) in [
                            (DiskSubTab::Visualizer, "3D Visualizer"),
                            (DiskSubTab::Treemap, "Treemap"),
                            (DiskSubTab::Duplicates, "Duplicate Finder"),
                            (DiskSubTab::Snapshots, "Snapshots"),
                            (DiskSubTab::Volumes, "Physical Volumes"),
                        ] {
                            if ui.selectable_label(self.disk_subtab == tab, lbl).clicked() {
                                self.disk_subtab = tab;
                            }
                        }
                    });
                    ui.add_space(6.0);
                    ui.horizontal_wrapped(|ui| {
                        ui.label(RichText::new("Scan Root:").strong());
                        ui.add(
                            egui::TextEdit::singleline(&mut self.disk_scan_path_input)
                                .desired_width(280.0),
                        );
                        if ui.button("Rescan Streaming").clicked() {
                            let target = PathBuf::from(self.disk_scan_path_input.trim());
                            if target.exists() {
                                self.start_disk_scan(target);
                            }
                        }
                        if self.disk_scanning {
                            hig_status_pill(
                                ui,
                                &format!(
                                    "Scanning {:.1}% · {} files",
                                    self.disk_scan_progress * 100.0,
                                    self.disk_report.total_files
                                ),
                                hig_blue(ui),
                            );
                        } else {
                            hig_status_pill(
                                ui,
                                &format!(
                                    "{} files · {} scanned",
                                    self.disk_report.total_files,
                                    format_bytes(self.disk_report.total_bytes)
                                ),
                                hig_green(ui),
                            );
                        }
                    });
                });

                ui.add_space(8.0);

                if self.disk_subtab == DiskSubTab::Visualizer {
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal_wrapped(|ui| {
                            ui.selectable_value(
                                &mut self.disk_shader_mode,
                                1,
                                "Mode 1: Ocean Metaballs",
                            );
                            ui.selectable_value(
                                &mut self.disk_shader_mode,
                                2,
                                "Mode 2: 48x26 Defrag Blocks",
                            );
                            ui.selectable_value(
                                &mut self.disk_shader_mode,
                                3,
                                "Mode 3: Tempest Vector Tunnel",
                            );
                            ui.checkbox(&mut self.disk_patterned, "Accessibility Hatch Patterns");
                            if let Some(sel) = self.disk_selected_category {
                                hig_status_pill(ui, &format!("Filter: {}", sel.label()), accent);
                                if ui.small_button("Clear Filter").clicked() {
                                    self.disk_selected_category = None;
                                }
                            }
                        });
                        ui.add_space(6.0);
                        let shader_sz = Vec2::new(ui.available_width(), 200.0);
                        let resp =
                            self.embed_disk_shader_with_mode(ui, shader_sz, self.disk_shader_mode);

                        if self.disk_shader_mode == 3 {
                            self.disk_hovered_category = None;
                        } else {
                            let mut fracs = [0.0_f32; 7];
                            for c in &self.disk_report.categories {
                                fracs[c.category as usize] = c.share_fraction;
                            }
                            let elapsed =
                                if self.gaming_mode.visual_throttle().background_shaders_paused {
                                    0.0
                                } else {
                                    self.start_instant.elapsed().as_secs_f32()
                                };
                            let aspect = resp.rect.width().max(1.0) / resp.rect.height().max(1.0);
                            let prog = if self.disk_scanning {
                                self.disk_scan_progress.max(0.05)
                            } else {
                                1.0
                            };
                            self.disk_hovered_category = resp.hover_pos().and_then(|pos| {
                                let nx = ((pos.x - resp.rect.min.x) / resp.rect.width().max(1.0))
                                    .clamp(0.0, 1.0);
                                let ny = ((pos.y - resp.rect.min.y) / resp.rect.height().max(1.0))
                                    .clamp(0.0, 1.0);
                                entry_at_normalized(
                                    (nx, 1.0 - ny),
                                    aspect,
                                    self.disk_shader_mode as u32,
                                    elapsed,
                                    &fracs,
                                    prog,
                                )
                            });
                            if let Some(hov) = self.disk_hovered_category {
                                if let Some(ct) = self
                                    .disk_report
                                    .categories
                                    .iter()
                                    .find(|c| c.category == hov)
                                {
                                    resp.clone().on_hover_text(format!(
                                        "{}: {:.1}% ({}, {} files) — click to filter",
                                        hov.label(),
                                        ct.share_fraction * 100.0,
                                        format_bytes(ct.bytes),
                                        ct.file_count
                                    ));
                                }
                                if resp.clicked() {
                                    self.disk_selected_category = Some(hov);
                                }
                            }
                        }

                        ui.add_space(6.0);
                        ui.horizontal_wrapped(|ui| {
                            for cat in &self.disk_report.categories {
                                let is_sel = self.disk_selected_category == Some(cat.category);
                                let is_hov = self.disk_hovered_category == Some(cat.category);
                                let rgb = cat.category.default_color_srgb();
                                let col = srgb_to_color32(rgb);
                                egui::Frame::new()
                                    .fill(ui.visuals().faint_bg_color)
                                    .stroke(Stroke::new(
                                        1.0,
                                        if is_sel || is_hov {
                                            col
                                        } else {
                                            ui.visuals().widgets.noninteractive.bg_stroke.color
                                        },
                                    ))
                                    .corner_radius(CornerRadius::same(8))
                                    .inner_margin(egui::Margin::symmetric(8, 6))
                                    .show(ui, |ui| {
                                        if ui
                                            .selectable_label(
                                                is_sel,
                                                RichText::new(cat.category.label())
                                                    .color(col)
                                                    .strong(),
                                            )
                                            .clicked()
                                        {
                                            self.disk_selected_category =
                                                if is_sel { None } else { Some(cat.category) };
                                        }
                                        ui.label(
                                            RichText::new(format!(
                                                "{:.1}% · {} ({} files)",
                                                cat.share_fraction * 100.0,
                                                format_bytes(cat.bytes),
                                                cat.file_count
                                            ))
                                            .font(FontId::proportional(11.0))
                                            .color(ui.visuals().weak_text_color()),
                                        );
                                    });
                            }
                        });
                        if let Some(sel) = self.disk_selected_category {
                            ui.add_space(6.0);
                            ui.label(
                                RichText::new(format!("Filtered Subdirectories ({})", sel.label()))
                                    .strong(),
                            );
                            for node in self
                                .disk_report
                                .root_node
                                .children
                                .iter()
                                .filter(|n| n.category == sel)
                                .take(8)
                            {
                                ui.label(format!(
                                    "• {} — {} ({} files)",
                                    node.full_path,
                                    format_bytes(node.size),
                                    node.file_count
                                ));
                            }
                        }
                    });
                } else if self.disk_subtab == DiskSubTab::Volumes {
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.vertical(|ui| {
                                hig_card_header(
                                    ui,
                                    "Physical Storage Inspector (diskutil)",
                                    "Hardware disk models, bus protocol, SMART status, and quick Finder actions.",
                                    accent,
                                );
                            });
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::TOP),
                                |ui| {
                                    if ui.button("Refresh Hardware").clicked()
                                        && !self.storage_inspector_loading
                                    {
                                        self.storage_inspector_loading = true;
                                        let slot = Arc::clone(&self.storage_inspector_slot);
                                        std::thread::spawn(move || {
                                            let data = collect_storage_inspector();
                                            *slot.lock().unwrap_or_else(|e| e.into_inner()) =
                                                Some(data);
                                        });
                                    }
                                },
                            );
                        });
                        if self.storage_inspector_loading {
                            ui.label("Inspecting physical storage devices...");
                        }
                        for dev in &self.storage_inspector {
                            ui.horizontal_wrapped(|ui| {
                                let mount_str = dev.mount_point.as_deref().unwrap_or("-");
                                ui.label(
                                    RichText::new(format!("{} [{}]", dev.model_name, dev.bsd_name))
                                        .strong(),
                                );
                                hig_status_pill(ui, &dev.protocol, hig_blue(ui));
                                let smart_col = if dev.smart_status.eq_ignore_ascii_case("Verified") {
                                    hig_green(ui)
                                } else {
                                    hig_amber(ui)
                                };
                                hig_status_pill(
                                    ui,
                                    &format!("SMART: {}", dev.smart_status),
                                    smart_col,
                                );
                                ui.label(format!(
                                    "FS: {} · Free: {} / {} · Mount: {}",
                                    dev.file_system.as_deref().unwrap_or("Unknown"),
                                    format_bytes(dev.available_bytes),
                                    format_bytes(dev.total_bytes),
                                    mount_str
                                ));
                                if let Some(m) = &dev.mount_point {
                                    if ui.small_button("Reveal").clicked() {
                                        let _ = std::process::Command::new("/usr/bin/open")
                                            .args(["-R", m.as_str()])
                                            .spawn();
                                    }
                                    if dev.is_ejectable && ui.small_button("Eject").clicked() {
                                        let _ = std::process::Command::new("/usr/sbin/diskutil")
                                            .args(["eject", m.as_str()])
                                            .spawn();
                                    }
                                }
                            });
                            ui.add_space(4.0);
                        }
                    });
                    ui.add_space(8.0);
                }

                draw_disk_space_extra_tabs(
                    ui,
                    self.disk_subtab,
                    &self.disk_report,
                    &mut self.treemap_rects,
                    &mut self.treemap_last_size,
                    &self.dup_groups_slot,
                    &mut self.dup_scanning,
                    &mut self.disk_store,
                    &mut self.disk_snapshots,
                    &mut self.disk_diff,
                    &self.platform_cache.volumes,
                    accent,
                );
            });
    }

    fn draw_flight_recorder_view(&mut self, ui: &mut egui::Ui, accent: Color32) -> bool {
        let mut rules_changed = false;
        hig_view_header(
            ui,
            "Flight Recorder & Timeline Replay",
            "Record, scrub, and compare historical system telemetry snapshots or replay portable .nexustrace captures.",
            accent,
        );

        egui::ScrollArea::vertical()
            .id_salt("flight_recorder_scroll")
            .max_height((ui.available_height() - 4.0).max(200.0))
            .show(ui, |ui| {
                // Step 1: Capture or Load .nexustrace
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "1. Capture or Load .nexustrace",
                        "Record portable compressed telemetry traces or open saved captures for offline inspection.",
                        accent,
                    );
                    ui.horizontal_wrapped(|ui| {
                        if !self.trace_recorder.is_recording() {
                            if ui.button("Start .nexustrace Recording").clicked() {
                                let summary =
                                    nexus_collectors_macos::system_ops::collect_system_hardware();
                                let is_apple_silicon = summary.architecture.contains("arm64")
                                    || self.snapshot.system.gpu_model.contains("Apple");
                                if let Err(err) = self.trace_recorder.start(
                                    &self.snapshot,
                                    summary.chip_model.as_str(),
                                    format!("{} {}", summary.os_name, summary.os_version),
                                    is_apple_silicon,
                                ) {
                                    self.trace_recorder.last_status = err.to_string();
                                }
                            }
                            hig_status_pill(ui, "Recorder Idle", hig_gray(ui));
                        } else {
                            if ui.button("Stop & Save .nexustrace").clicked() {
                                match self.trace_recorder.stop_and_save(&default_traces_dir()) {
                                    Ok(Some(path)) => {
                                        self.trace_path_input = path.display().to_string();
                                        self.trace_saved_files =
                                            list_trace_files(&default_traces_dir());
                                    }
                                    Ok(None) => {}
                                    Err(err) => {
                                        self.trace_recorder.last_status = err.to_string();
                                    }
                                }
                            }
                            if ui.button("Discard").clicked() {
                                self.trace_recorder.discard();
                            }
                            hig_status_pill(ui, "Recording Live", hig_red(ui));
                        }
                        let used_mib =
                            (self.trace_recorder.encoded_bytes() as f64) / (1024.0 * 1024.0);
                        let max_mib = (MAX_TRACE_BYTES as f64) / (1024.0 * 1024.0);
                        ui.label(format!(
                            "Samples: {} · Size: {:.2} / {:.0} MiB · {}",
                            self.trace_recorder.sample_count(),
                            used_mib,
                            max_mib,
                            self.trace_recorder.last_status
                        ));
                    });
                    ui.add_space(6.0);
                    ui.horizontal_wrapped(|ui| {
                        ui.label(RichText::new("Trace Path:").strong());
                        ui.add(
                            egui::TextEdit::singleline(&mut self.trace_path_input)
                                .desired_width(300.0),
                        );
                        if ui.button("Load Trace").clicked()
                            && !self.trace_path_input.trim().is_empty()
                        {
                            let path = PathBuf::from(self.trace_path_input.trim());
                            match NexusTracePlayer::load(&path) {
                                Ok(player) => {
                                    self.trace_player = Some(player);
                                }
                                Err(err) => {
                                    self.trace_recorder.last_status = format!("Load failed: {err}");
                                }
                            }
                        }
                        if ui.button("Refresh Saved").clicked() {
                            self.trace_saved_files = list_trace_files(&default_traces_dir());
                        }
                    });
                    if !self.trace_saved_files.is_empty() {
                        ui.add_space(4.0);
                        ui.horizontal_wrapped(|ui| {
                            ui.label(RichText::new("Saved Traces:").strong());
                            for path in self.trace_saved_files.clone().into_iter().take(6) {
                                let name = path
                                    .file_name()
                                    .and_then(|n| n.to_str())
                                    .unwrap_or("trace.nexustrace");
                                if ui.small_button(name).clicked() {
                                    self.trace_path_input = path.display().to_string();
                                    match NexusTracePlayer::load(&path) {
                                        Ok(player) => self.trace_player = Some(player),
                                        Err(err) => {
                                            self.trace_recorder.last_status =
                                                format!("Load failed: {err}");
                                        }
                                    }
                                }
                            }
                        });
                    }
                });

                ui.add_space(10.0);

                // Step 2: Scrub & Compare Timeline
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "2. Scrub & Compare Timeline",
                        "Pause live updates to scrub recent frames or diff against your saved baseline snapshot.",
                        accent,
                    );
                    ui.horizontal_wrapped(|ui| {
                        let max_idx = self.flight_recorder.len().saturating_sub(1);
                        let mut cur_idx = self.scrub_index.unwrap_or(max_idx);
                        if self.scrub_index.is_some() {
                            hig_status_pill(ui, "Scrubbing History", hig_amber(ui));
                        } else {
                            hig_status_pill(ui, "Live Stream", hig_green(ui));
                        }
                        ui.label(format!(
                            "Retained Frames: {} · Frame:",
                            self.flight_recorder.len()
                        ));
                        if ui
                            .add(egui::Slider::new(&mut cur_idx, 0..=max_idx))
                            .changed()
                        {
                            self.scrub_index = Some(cur_idx);
                            if let Some(f) = self.flight_recorder.get(cur_idx) {
                                self.snapshot = Arc::clone(f);
                            }
                        }
                        if ui.button("Resume Live").clicked() {
                            self.scrub_index = None;
                            if let Some(last) = self.flight_recorder.last() {
                                self.snapshot = Arc::clone(last);
                            }
                        }
                        if ui.button("Capture & Diff Now").clicked() {
                            if let Some(base) = &self.baseline_snapshot {
                                let diff = SnapshotDiffer::diff(base, &self.snapshot);
                                self.last_diff_summary = format!(
                                    "Diff Gen #{} -> #{}: dCPU={:+.1}%, Spawned={}, Terminated={}, Modified={}",
                                    diff.baseline_generation,
                                    diff.target_generation,
                                    diff.cpu_percent_delta.unwrap_or(0.0),
                                    diff.spawned_processes.len(),
                                    diff.terminated_processes.len(),
                                    diff.modified_processes.len()
                                );
                            }
                            let _ = self
                                .snapshot_db
                                .save_snapshot("User Capture", &self.snapshot);
                            self.baseline_snapshot = Some(Arc::clone(&self.snapshot));
                        }
                    });
                    ui.add_space(4.0);
                    ui.label(RichText::new(&self.last_diff_summary).color(accent));

                    if let Some(player) = &mut self.trace_player {
                        player.tick();
                        let dur = player.duration_seconds().max(0.001);
                        let mut t = player.playback_elapsed();
                        ui.add_space(6.0);
                        ui.separator();
                        ui.horizontal_wrapped(|ui| {
                            ui.label(
                                RichText::new("Loaded .nexustrace Replay (Decoupled from Live DB/Alerts)")
                                    .strong(),
                            );
                            if player.playing {
                                if ui.button("Pause").clicked() {
                                    player.pause();
                                }
                            } else if ui.button("Play").clicked() {
                                player.play();
                            }
                            if ui.button("Restart").clicked() {
                                player.restart();
                            }
                            if ui
                                .add(egui::Slider::new(&mut t, 0.0..=dur).text("t (s)"))
                                .changed()
                            {
                                player.seek(t);
                            }
                            ui.label(format!(
                                "Frame {}/{} ({:.2}s / {:.2}s)",
                                player.current_index + 1,
                                player.trace.samples.len(),
                                player.playback_elapsed(),
                                player.duration_seconds()
                            ));
                        });
                        if let Some(sample) = player.current_sample() {
                            let e_avg = mean_opt(&sample.e_core_loads);
                            let p_avg = mean_opt(&sample.p_core_loads);
                            ui.label(format!(
                                "Machine: {} ({}) · E-Avg: {} · P-Avg: {} · GPU: {} · Pkg: {} · MaxTemp: {}",
                                player.trace.machine_name,
                                player.trace.os_version,
                                fmt_opt(e_avg, "%"),
                                fmt_opt(p_avg, "%"),
                                fmt_opt(sample.gpu_load_percent, "%"),
                                fmt_opt(sample.package_watts, "W"),
                                fmt_opt(sample.max_thermal_celsius, "C"),
                            ));
                        }
                    }
                });

                ui.add_space(10.0);

                // Step 3: Watchdog Rules
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "3. Watchdog Rules",
                        "Create persistent process patterns that automatically adjust priority or trigger alerts.",
                        accent,
                    );
                    ui.horizontal_wrapped(|ui| {
                        ui.label(RichText::new("Process Pattern:").strong());
                        ui.add(
                            egui::TextEdit::singleline(&mut self.new_rule_pattern)
                                .desired_width(170.0),
                        );
                        ui.add(
                            egui::Slider::new(&mut self.new_rule_cpu_threshold, 0.5..=95.0)
                                .text("CPU % Threshold"),
                        );
                        if ui.button("Add Rule & Persist").clicked() {
                            let id = format!("rule.{}", self.rules_engine.rules.len() + 1);
                            self.rules_engine.add_rule(ProcessRule {
                                id: CompactString::from(id),
                                process_pattern: CompactString::from(self.new_rule_pattern.as_str()),
                                cpu_percent_threshold: Some(self.new_rule_cpu_threshold),
                                target_priority: Some(10),
                                ..Default::default()
                            });
                            rules_changed = true;
                        }
                    });
                    ui.add_space(6.0);
                    let events = self
                        .rules_engine
                        .evaluate_snapshot(&self.snapshot, now_unix_ms());
                    let reclaim_candidates = self.mem_reclaim.candidates(&self.snapshot, 5);
                    ui.horizontal_wrapped(|ui| {
                        hig_status_pill(
                            ui,
                            &format!("Persisted Rules: {}", self.rules_engine.rules.len()),
                            hig_blue(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Active Triggers: {}", events.len()),
                            if events.is_empty() {
                                hig_green(ui)
                            } else {
                                hig_amber(ui)
                            },
                        );
                        hig_status_pill(
                            ui,
                            &format!("Memory Reclaim Candidates: {}", reclaim_candidates.len()),
                            hig_purple(ui),
                        );
                    });
                    if !events.is_empty() {
                        ui.add_space(6.0);
                        for ev in events.iter().take(8) {
                            ui.label(format!(
                                "• [{}] PID {} (token={}) {:?} -> target nice {:?}",
                                ev.rule_id,
                                ev.process_key.pid,
                                ev.process_key.creation_token_us,
                                ev.process_name,
                                ev.target_priority
                            ));
                        }
                    }
                });
            });

        rules_changed
    }

    fn draw_benchmarks_view(&mut self, ui: &mut egui::Ui, accent: Color32) {
        hig_view_header(
            ui,
            "Hardware Benchmark Studio",
            "Measure processor, graphics, uncached disk, and internet throughput against the 100-point Nexus reference baseline.",
            accent,
        );

        let snapshot_state = {
            let st = self.bench_state.lock().unwrap_or_else(|e| e.into_inner());
            (
                st.running,
                st.active_suite.clone(),
                st.progress,
                st.status.clone(),
                st.cpu_single_mops,
                st.cpu_multi_mops,
                st.gpu_compute_gflops,
                st.gpu_graphics_gflops,
                st.disk_read_mbps,
                st.disk_write_mbps,
                st.net_down_mbps,
                st.net_up_mbps,
            )
        };
        let (
            running,
            active_suite,
            progress,
            status,
            cpu_s,
            cpu_m,
            gpu_c,
            gpu_g,
            disk_r,
            disk_w,
            net_d,
            net_u,
        ) = snapshot_state;

        let raw_6 = [cpu_s, cpu_m, gpu_c, gpu_g, disk_r, disk_w];
        let ratios = benchmark_ratios(&raw_6, &DEFAULT_NEXUS_BASELINE);
        let score = nexus_score(&ratios);

        egui::ScrollArea::vertical()
            .id_salt("benchmarks_scroll")
            .max_height((ui.available_height() - 4.0).max(200.0))
            .show(ui, |ui| {
                // Hero NexusScore Comparison Card
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal_wrapped(|ui| {
                        ui.vertical(|ui| {
                            ui.label(
                                RichText::new("Composite NexusScore")
                                    .font(FontId::proportional(13.0))
                                    .strong()
                                    .color(ui.visuals().weak_text_color()),
                            );
                            ui.label(
                                RichText::new(format!("{score}"))
                                    .font(FontId::new(30.0, FontFamily::Name("led".into())))
                                    .color(accent),
                            );
                            ui.label(
                                RichText::new("100 = Reference Baseline · 300 Max")
                                    .font(FontId::proportional(11.5))
                                    .color(ui.visuals().weak_text_color()),
                            );
                        });
                        ui.add_space(16.0);
                        ui.vertical(|ui| {
                            ui.set_min_width((ui.available_width() - 210.0).max(280.0));
                            ui.horizontal_wrapped(|ui| {
                                if !running {
                                    if ui.button("Run CPU (5s+5s)").clicked() {
                                        self.launch_benchmark_worker(vec![BenchmarkCategory::CPU]);
                                    }
                                    if ui.button("Run GPU (Compute+GFX)").clicked() {
                                        self.launch_benchmark_worker(vec![BenchmarkCategory::GPU]);
                                    }
                                    if ui.button("Run Disk (F_NOCACHE)").clicked() {
                                        self.launch_benchmark_worker(vec![BenchmarkCategory::Disk]);
                                    }
                                    if ui.button("Run Internet").clicked() {
                                        self.launch_benchmark_worker(vec![
                                            BenchmarkCategory::Internet,
                                        ]);
                                    }
                                    if ui.button("Run All 4 Suites").clicked() {
                                        self.launch_benchmark_worker(
                                            BenchmarkCategory::ALL.to_vec(),
                                        );
                                    }
                                } else if ui.button("Cancel Benchmark").clicked() {
                                    self.bench_cancel.store(true, AtomicOrdering::Relaxed);
                                }
                            });
                            ui.add_space(6.0);
                            ui.horizontal(|ui| {
                                let status_col = if running {
                                    hig_blue(ui)
                                } else if status == "Completed" {
                                    hig_green(ui)
                                } else {
                                    hig_gray(ui)
                                };
                                hig_status_pill(
                                    ui,
                                    &format!("{active_suite} · {status}"),
                                    status_col,
                                );
                                if score > 0 {
                                    hig_status_pill(
                                        ui,
                                        &format!("{:.2}x Reference Baseline", (score as f64) / 100.0),
                                        accent,
                                    );
                                }
                            });
                            ui.add_space(6.0);

                            // Visual 100 = Baseline comparison gauge bar (0..300)
                            let gauge_w = ui.available_width().max(180.0);
                            let (g_rect, _) = ui.allocate_exact_size(
                                Vec2::new(gauge_w, 16.0),
                                egui::Sense::hover(),
                            );
                            let painter = ui.painter_at(g_rect);
                            let track = if ui.visuals().dark_mode {
                                Color32::from_rgba_unmultiplied(255, 255, 255, 22)
                            } else {
                                Color32::from_rgba_unmultiplied(15, 23, 42, 18)
                            };
                            painter.rect_filled(g_rect, CornerRadius::same(6), track);
                            let fill_frac = (score.clamp(0, 300) as f32 / 300.0).clamp(0.0, 1.0);
                            if fill_frac > 0.005 {
                                painter.rect_filled(
                                    Rect::from_min_size(
                                        g_rect.min,
                                        Vec2::new(g_rect.width() * fill_frac, g_rect.height()),
                                    ),
                                    CornerRadius::same(6),
                                    accent,
                                );
                            }
                            let baseline_x = g_rect.min.x + g_rect.width() * (100.0 / 300.0);
                            painter.line_segment(
                                [
                                    Pos2::new(baseline_x, g_rect.min.y - 2.0),
                                    Pos2::new(baseline_x, g_rect.max.y + 2.0),
                                ],
                                Stroke::new(2.0, hig_amber(ui)),
                            );
                            if running {
                                ui.add_space(4.0);
                                ui.add(egui::ProgressBar::new(progress).show_percentage());
                            }
                        });
                    });
                });

                ui.add_space(10.0);

                // 4 Category Cards with plain-English relative multipliers
                ui.columns(4, |cols| {
                    hig_card(&mut cols[0], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(ui, "Processor (CPU)", "SplitMix64 throughput (Mops/s)", accent);
                        ui.label(
                            RichText::new(format!("Single: {} Mops/s", six_character_ratio(cpu_s)))
                                .strong(),
                        );
                        ui.label(
                            RichText::new(format!("{:.2}x Reference Single-Core", ratios[0]))
                                .font(FontId::proportional(11.5))
                                .color(hig_blue(ui)),
                        );
                        ui.add_space(4.0);
                        ui.label(
                            RichText::new(format!("Multi: {} Mops/s", six_character_ratio(cpu_m)))
                                .strong(),
                        );
                        ui.label(
                            RichText::new(format!("{:.2}x Reference Multi-Core", ratios[1]))
                                .font(FontId::proportional(11.5))
                                .color(hig_green(ui)),
                        );
                    });
                    hig_card(&mut cols[1], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(ui, "Graphics (GPU)", "Offscreen Metal/wgpu (GFLOPS/s)", accent);
                        ui.label(
                            RichText::new(format!(
                                "Compute: {} GFLOPS",
                                six_character_ratio(gpu_c)
                            ))
                            .strong(),
                        );
                        ui.label(
                            RichText::new(format!("{:.2}x Reference Compute", ratios[2]))
                                .font(FontId::proportional(11.5))
                                .color(hig_blue(ui)),
                        );
                        ui.add_space(4.0);
                        ui.label(
                            RichText::new(format!(
                                "Graphics: {} GFLOPS",
                                six_character_ratio(gpu_g)
                            ))
                            .strong(),
                        );
                        ui.label(
                            RichText::new(format!("{:.2}x Reference Graphics", ratios[3]))
                                .font(FontId::proportional(11.5))
                                .color(hig_green(ui)),
                        );
                    });
                    hig_card(&mut cols[2], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(ui, "Storage (Disk)", "Uncached F_NOCACHE (MB/s)", accent);
                        ui.label(
                            RichText::new(format!("Read: {} MB/s", six_character_ratio(disk_r)))
                                .strong(),
                        );
                        ui.label(
                            RichText::new(format!("{:.2}x Reference Read", ratios[4]))
                                .font(FontId::proportional(11.5))
                                .color(hig_blue(ui)),
                        );
                        ui.add_space(4.0);
                        ui.label(
                            RichText::new(format!("Write: {} MB/s", six_character_ratio(disk_w)))
                                .strong(),
                        );
                        ui.label(
                            RichText::new(format!("{:.2}x Reference Write", ratios[5]))
                                .font(FontId::proportional(11.5))
                                .color(hig_green(ui)),
                        );
                    });
                    hig_card(&mut cols[3], |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(ui, "Network (Internet)", "Live HTTPS throughput (Mbps)", accent);
                        ui.label(
                            RichText::new(format!("Down: {} Mbps", six_character_ratio(net_d)))
                                .strong(),
                        );
                        ui.label(
                            RichText::new("Download transfer rate")
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                        );
                        ui.add_space(4.0);
                        ui.label(
                            RichText::new(format!("Up: {} Mbps", six_character_ratio(net_u)))
                                .strong(),
                        );
                        ui.label(
                            RichText::new("Upload transfer rate")
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                        );
                    });
                });

                ui.add_space(10.0);

                // Recent Benchmark History Card
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Recent Benchmark History",
                        "Persisted benchmark runs stored locally (up to 100 entries per category).",
                        accent,
                    );
                    let st = self.bench_state.lock().unwrap_or_else(|e| e.into_inner());
                    let mut any_entries = false;
                    for cat in BenchmarkCategory::ALL {
                        let entries = st.history.entries(cat);
                        if entries.is_empty() {
                            continue;
                        }
                        any_entries = true;
                        ui.label(RichText::new(cat.as_str()).strong().color(accent));
                        for e in entries.iter().take(5) {
                            ui.label(format!(
                                "• ts={} · primary={} · secondary={} · {}",
                                e.recorded_at,
                                six_character_ratio(e.left),
                                six_character_ratio(e.right),
                                e.note
                            ));
                        }
                        ui.add_space(4.0);
                    }
                    if !any_entries {
                        ui.label(
                            RichText::new("No saved benchmark runs yet. Run any suite above to record baseline history.")
                                .color(ui.visuals().weak_text_color()),
                        );
                    }
                });
            });
    }

    fn launch_benchmark_worker(&self, suites: Vec<BenchmarkCategory>) {
        self.bench_cancel.store(false, AtomicOrdering::Relaxed);
        let state_arc = Arc::clone(&self.bench_state);
        let cancel_arc = Arc::clone(&self.bench_cancel);
        let gpu_handles = self
            .painter
            .render_state()
            .map(|rs| (rs.device.clone(), rs.queue.clone(), rs.target_format));

        {
            let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
            if st.running {
                return;
            }
            st.running = true;
            st.progress = 0.0;
            st.status = "Starting...".to_string();
        }

        std::thread::spawn(move || {
            let duration = Duration::from_secs_f64(DEFAULT_BENCHMARK_DURATION_SECS);
            let total_suites = suites.len().max(1) as f32;
            for (idx, cat) in suites.into_iter().enumerate() {
                if cancel_arc.load(AtomicOrdering::Relaxed) {
                    break;
                }
                let base_p = (idx as f32) / total_suites;
                let span_p = 1.0 / total_suites;
                {
                    let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
                    st.active_suite = cat.as_str().to_string();
                    st.status = format!("Running {} benchmark...", cat.as_str());
                }

                let now_ts = (now_unix_ms() / 1000) as i64;
                match cat {
                    BenchmarkCategory::CPU => {
                        let res = run_cpu_suite(duration, &cancel_arc, |p| {
                            if let Ok(mut st) = state_arc.lock() {
                                st.progress = base_p + p * span_p;
                            }
                        });
                        let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
                        st.cpu_single_mops = res.single_core_mops;
                        st.cpu_multi_mops = res.multi_core_mops;
                        st.history.record(
                            BenchmarkCategory::CPU,
                            now_ts,
                            res.single_core_mops,
                            res.multi_core_mops,
                            &format!(
                                "SplitMix64 single={:.1} multi={:.1} Mops/s",
                                res.single_core_mops, res.multi_core_mops
                            ),
                        );
                        let _ = st.history.save(&default_benchmark_history_path());
                    }
                    BenchmarkCategory::GPU => {
                        let (comp, gfx, note) = if let Some((dev, queue, fmt)) = &gpu_handles {
                            let (c, g) = run_gpu_benchmark_offscreen(
                                dev,
                                queue,
                                *fmt,
                                duration,
                                &cancel_arc,
                                |p| {
                                    if let Ok(mut st) = state_arc.lock() {
                                        st.progress = base_p + p * span_p;
                                    }
                                },
                            );
                            (c, g, format!("wgpu compute={c:.1} gfx={g:.1} GFLOPS/s"))
                        } else {
                            (0.0, 0.0, "No wgpu device available".to_string())
                        };
                        let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
                        st.gpu_compute_gflops = comp;
                        st.gpu_graphics_gflops = gfx;
                        st.history
                            .record(BenchmarkCategory::GPU, now_ts, comp, gfx, &note);
                        let _ = st.history.save(&default_benchmark_history_path());
                    }
                    BenchmarkCategory::Disk => {
                        let io = MacOsUncachedDiskIo;
                        let dir = std::env::temp_dir();
                        let res = run_disk_benchmark(&io, &dir, duration, &cancel_arc, None);
                        let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
                        match res {
                            Ok(r) => {
                                st.disk_read_mbps = r.read_mb_per_sec;
                                st.disk_write_mbps = r.write_mb_per_sec;
                                st.history.record(
                                    BenchmarkCategory::Disk,
                                    now_ts,
                                    r.read_mb_per_sec,
                                    r.write_mb_per_sec,
                                    &format!(
                                        "F_NOCACHE+F_FULLFSYNC read={:.1} write={:.1} MB/s",
                                        r.read_mb_per_sec, r.write_mb_per_sec
                                    ),
                                );
                                let _ = st.history.save(&default_benchmark_history_path());
                            }
                            Err(err) => {
                                st.status = format!("Disk bench failed: {err}");
                            }
                        }
                    }
                    BenchmarkCategory::Internet => {
                        let (down, up, note) = run_internet_benchmark(duration, &cancel_arc);
                        let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
                        st.net_down_mbps = down;
                        st.net_up_mbps = up;
                        st.history
                            .record(BenchmarkCategory::Internet, now_ts, down, up, &note);
                        let _ = st.history.save(&default_benchmark_history_path());
                    }
                }
            }

            let mut st = state_arc.lock().unwrap_or_else(|e| e.into_inner());
            st.running = false;
            st.progress = 1.0;
            st.active_suite = "Idle".to_string();
            if cancel_arc.load(AtomicOrdering::Relaxed) {
                st.status = "Cancelled".to_string();
            } else {
                st.status = "Completed".to_string();
            }
        });
    }

    fn draw_honesty_view(&mut self, ui: &mut egui::Ui, accent: Color32) {
        hig_view_header(
            ui,
            "Measurement Honesty & Sensor Provenance",
            "Audit which hardware and operating system sensors are directly measured, derived via fallback, or unavailable on this Mac.",
            accent,
        );

        let sp = &self.snapshot.hardware_telemetry.soc_power;
        let th = &self.snapshot.hardware_telemetry.thermals;
        let items: [(&str, &str, &Metric<f64>, &str); 10] = [
            (
                "system.cpu_percent",
                "Host CPU utilization",
                &self.snapshot.system.cpu_percent,
                "%",
            ),
            (
                "system.mem_used_percent",
                "Physical memory used",
                &self.snapshot.system.mem_used_percent,
                "%",
            ),
            (
                "system.gpu_utilization_percent",
                "Apple Silicon GPU utilization",
                &self.snapshot.system.gpu_utilization_percent,
                "%",
            ),
            (
                "soc.package_watts",
                "Total SoC package power",
                &sp.package_watts,
                "W",
            ),
            (
                "soc.system_power_in_watts",
                "System DC / wall input power",
                &sp.system_power_in_watts,
                "W",
            ),
            (
                "soc.p_cluster_residency_pct",
                "Performance cluster residency",
                &sp.p_cluster_active_residency_percent,
                "%",
            ),
            (
                "soc.e_cluster_residency_pct",
                "Efficiency cluster residency",
                &sp.e_cluster_active_residency_percent,
                "%",
            ),
            (
                "thermal.max_die_celsius",
                "Peak SoC die temperature",
                &th.max_die_celsius,
                "C",
            ),
            (
                "thermal.warning_level",
                "macOS thermal pressure level",
                &th.warning_level,
                "",
            ),
            (
                "system.load_avg_1m",
                "POSIX 1-minute load average",
                &self.snapshot.system.load_avg_1m,
                "",
            ),
        ];

        let mut authoritative_cnt = 0usize;
        let mut derived_cnt = 0usize;
        let mut unavailable_cnt = 0usize;
        for (_, _, m, _) in items {
            match m.state() {
                ValueState::Valid | ValueState::Zero => {
                    if m.provenance.quality == MetricQuality::Authoritative {
                        authoritative_cnt += 1;
                    } else {
                        derived_cnt += 1;
                    }
                }
                _ => unavailable_cnt += 1,
            }
        }

        egui::ScrollArea::vertical()
            .id_salt("honesty_view_scroll")
            .max_height((ui.available_height() - 4.0).max(200.0))
            .show(ui, |ui| {
                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Coherence & Provenance Contract",
                        "Nexus validates cross-sensor totals and refuses to invent placeholder zeros when hardware channels require privileges or are absent.",
                        accent,
                    );
                    ui.horizontal_wrapped(|ui| {
                        match self.snapshot.first_coherence_fault() {
                            None => hig_status_pill(ui, "Coherence: Passed", hig_green(ui)),
                            Some(fault) => hig_status_pill(
                                ui,
                                &format!("Coherence Fault: {fault}"),
                                hig_red(ui),
                            ),
                        }
                        hig_status_pill(
                            ui,
                            &format!("Authoritative: {authoritative_cnt}"),
                            hig_green(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Fallback / Estimated: {derived_cnt}"),
                            hig_amber(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("Unavailable: {unavailable_cnt}"),
                            if unavailable_cnt > 0 {
                                hig_gray(ui)
                            } else {
                                hig_green(ui)
                            },
                        );
                        hig_status_pill(
                            ui,
                            &format!(
                                "Repairs · clamped={} · unavailable={} · dedup={}",
                                self.snapshot.coherence_repairs.percentages_clamped,
                                self.snapshot.coherence_repairs.marked_unavailable,
                                self.snapshot.coherence_repairs.duplicate_rows_dropped
                            ),
                            hig_blue(ui),
                        );
                    });
                });

                ui.add_space(10.0);

                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Audited Sensor Provenance Matrix",
                        "Current reading, 8-state value contract, measurement quality tier, and collector origin.",
                        accent,
                    );
                    egui::Grid::new("honesty_provenance_grid")
                        .num_columns(5)
                        .striped(true)
                        .spacing([18.0, 8.0])
                        .show(ui, |ui| {
                            ui.label(RichText::new("Sensor").strong().color(accent));
                            ui.label(RichText::new("Reading").strong().color(accent));
                            ui.label(RichText::new("ValueState").strong().color(accent));
                            ui.label(RichText::new("Quality").strong().color(accent));
                            ui.label(RichText::new("Collector Source").strong().color(accent));
                            ui.end_row();

                            for (name, desc, m, unit) in items {
                                ui.vertical(|ui| {
                                    ui.set_min_width(220.0);
                                    ui.label(RichText::new(desc).strong());
                                    ui.label(
                                        RichText::new(name)
                                            .font(FontId::proportional(11.0))
                                            .color(ui.visuals().weak_text_color()),
                                    );
                                });
                                ui.horizontal(|ui| {
                                    ui.set_min_width(72.0);
                                    ui.label(RichText::new(m.format_with_unit(1, unit)).monospace());
                                });
                                let st = m.state();
                                let st_col = match st {
                                    ValueState::Valid => hig_green(ui),
                                    ValueState::Zero => hig_blue(ui),
                                    ValueState::NotYetAvailable | ValueState::Stale => hig_amber(ui),
                                    ValueState::NotApplicable => hig_gray(ui),
                                    ValueState::Unsupported
                                    | ValueState::AccessDenied
                                    | ValueState::TransientError => hig_red(ui),
                                };
                                ui.horizontal(|ui| {
                                    hig_status_pill(ui, st.as_str(), st_col);
                                });

                                let q = m.provenance.quality;
                                let q_col = match q {
                                    MetricQuality::Authoritative => hig_green(ui),
                                    MetricQuality::Fallback => hig_amber(ui),
                                    MetricQuality::Estimated => hig_blue(ui),
                                    MetricQuality::Unknown => hig_gray(ui),
                                };
                                ui.horizontal(|ui| {
                                    hig_status_pill(ui, q.as_str(), q_col);
                                });

                                ui.label(self.snapshot.source_name(m.provenance.source));
                                ui.end_row();
                            }
                        });
                });

                ui.add_space(10.0);

                hig_card(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    let (guide_title, guide_sub, col_how) = if cfg!(target_os = "macos") {
                        (
                            "macOS Permissions & Feature Unlock Guide",
                            "Quick links and step-by-step instructions to unlock any permission-gated macOS telemetry or storage feature.",
                            "How to Unlock on macOS",
                        )
                    } else if cfg!(target_os = "linux") {
                        (
                            "Linux Privileges & Capabilities Guide",
                            "How to unlock root-owned /proc descriptors, systemd control, and wireless netlink telemetry on Linux.",
                            "How to Unlock on Linux",
                        )
                    } else {
                        (
                            "Windows Administrator & Privilege Guide",
                            "How to unlock protected SYSTEM processes, Service Control Manager actions, and volume scans on Windows.",
                            "How to Unlock on Windows",
                        )
                    };
                    hig_card_header(ui, guide_title, guide_sub, accent);
                    egui::Grid::new("honesty_permissions_guide_grid")
                        .num_columns(3)
                        .striped(true)
                        .spacing([14.0, 8.0])
                        .show(ui, |ui| {
                            ui.label(RichText::new("Feature / Scope").strong().color(accent));
                            ui.label(RichText::new(col_how).strong().color(accent));
                            ui.label(RichText::new("Quick Action").strong().color(accent));
                            ui.end_row();

                            if cfg!(target_os = "macos") {
                                ui.label(RichText::new("Full Disk Access (Disk Space & Containers)").strong());
                                ui.label("System Settings -> Privacy & Security -> Full Disk Access -> Add & toggle Nexus System Monitor ON.");
                                if ui.small_button("Open Full Disk Access").clicked() {
                                    crate::desktop_views::open_macos_privacy_pane(
                                        "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles",
                                    );
                                }
                                ui.end_row();

                                ui.label(RichText::new("App Management (Safe Trash Uninstaller)").strong());
                                ui.label("System Settings -> Privacy & Security -> App Management -> Toggle Nexus System Monitor ON.");
                                if ui.small_button("Open App Management").clicked() {
                                    crate::desktop_views::open_macos_privacy_pane(
                                        "x-apple.systempreferences:com.apple.preference.security?Privacy_AppBundles",
                                    );
                                }
                                ui.end_row();

                                ui.label(RichText::new("Location Services (Wi-Fi SSID Names)").strong());
                                ui.label("System Settings -> Privacy & Security -> Location Services -> Toggle Nexus System Monitor ON to unredact SSIDs.");
                                if ui.small_button("Open Location Services").clicked() {
                                    crate::desktop_views::open_macos_privacy_pane(
                                        "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices",
                                    );
                                }
                                ui.end_row();

                                ui.label(RichText::new("Login Items & Extensions (Launchd / DriverKit)").strong());
                                ui.label("System Settings -> General -> Login Items & Extensions -> Toggle background items or DriverKit extensions.");
                                if ui.small_button("Open Login Items").clicked() {
                                    crate::desktop_views::open_macos_privacy_pane(
                                        "x-apple.systempreferences:com.apple.LoginItems-Settings.extension",
                                    );
                                }
                                ui.end_row();
                            } else if cfg!(target_os = "linux") {
                                ui.label(RichText::new("Full Filesystem & /proc/[pid]/fd Attribution").strong());
                                ui.label("Run `sudo nexus-monitor` or grant CAP_DAC_READ_SEARCH / CAP_SYS_PTRACE to inspect root-owned sockets and directories.");
                                if ui.small_button("Reveal Binary").clicked() {
                                    crate::desktop_views::reveal_app_in_finder();
                                }
                                ui.end_row();

                                ui.label(RichText::new("Wireless & Netlink RF Scanning").strong());
                                ui.label("Grant CAP_NET_ADMIN (`sudo setcap cap_net_admin+ep ./nexus-monitor`) for active nl80211 Wi-Fi scans.");
                                ui.label("CAP_NET_ADMIN");
                                ui.end_row();
                            } else {
                                ui.label(RichText::new("Administrator Elevation (SYSTEM Processes & SCM)").strong());
                                ui.label("Right-click nexus-monitor.exe and choose 'Run as administrator' to inspect protected SYSTEM processes and control services.");
                                if ui.small_button("Reveal Executable").clicked() {
                                    crate::desktop_views::reveal_app_in_finder();
                                }
                                ui.end_row();
                            }

                            ui.label(RichText::new("Root Daemons & Kernel Sockets (UID 0 / SYSTEM)").strong());
                            ui.label(if cfg!(windows) {
                                "Run an elevated Administrator terminal (`nexus.exe top`) to inspect all system-wide handles."
                            } else {
                                "Run `sudo nexus-monitor` or `sudo nexus top` in Terminal to inspect root-owned process footprints and socket PIDs."
                            });
                            if ui.small_button(if cfg!(target_os = "macos") { "Reveal App in Finder" } else { "Reveal Binary" }).clicked() {
                                crate::desktop_views::reveal_app_in_finder();
                            }
                            ui.end_row();
                        });
                });
            });
    }

    fn embed_disk_shader_with_mode(
        &self,
        ui: &mut egui::Ui,
        size: Vec2,
        mode: i32,
    ) -> egui::Response {
        let elapsed = if self.gaming_mode.visual_throttle().background_shaders_paused {
            0.0
        } else {
            self.start_instant.elapsed().as_secs_f32()
        };
        let pass = if mode == 3 {
            ShaderPassKind::VectorTunnel {
                time_sec: elapsed,
                viewport: [size.x.max(1.0), size.y.max(1.0)],
                edr_headroom: self.edr_headroom,
            }
        } else {
            let progress = if self.disk_scanning {
                self.disk_scan_progress.clamp(0.02, 1.0)
            } else {
                1.0
            };
            let uniforms = GpuDiskUniforms {
                viewport: [size.x.max(1.0), size.y.max(1.0)],
                time: elapsed,
                progress,
                completion: self.disk_scan_completion,
                blob_count: (self.disk_blobs.len().min(16)) as i32,
                mode,
                water_level: self.disk_water_level,
                patterned: i32::from(self.disk_patterned),
                flat: i32::from(self.gaming_mode.active),
                _pad0: [0.0, 0.0],
                ground: [0.04, 0.06, 0.09, 1.0],
                empty: [0.08, 0.10, 0.14, 1.0],
            };
            ShaderPassKind::DiskSpace {
                blobs: self.disk_blobs.clone(),
                uniforms,
            }
        };
        embed_wgsl_pass(ui, self.target_format, size, pass)
    }

    fn run_process_action(
        &mut self,
        key: ProcessKey,
        action_type: ProcessActionType,
        signal: Option<i32>,
        priority: Option<i32>,
    ) {
        match self.action_coord.execute(
            &key,
            ActionRequest {
                action_type,
                signal,
                priority,
            },
            true,
        ) {
            Ok(()) => {
                self.action_status = format!(
                    "Action {:?} verified ProcessKey(pid={}, token={}) twice (Dry-Run OK)",
                    action_type, key.pid, key.creation_token_us
                );
            }
            Err(e) => {
                self.action_status = format!("Refused action on PID {}: {e}", key.pid);
            }
        }
    }
}

impl ApplicationHandler<UserEvent> for GuiApp {
    fn new_events(&mut self, _event_loop: &ActiveEventLoop, cause: StartCause) {
        if cause == StartCause::Init {
            self.init_tray_if_needed();
        }
    }

    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }
        let attrs = Window::default_attributes()
            .with_title("Nexus System Monitor (v0.1.0)")
            .with_inner_size(LogicalSize::new(1280.0, 820.0))
            .with_window_icon(load_winit_window_icon());
        let window = Arc::new(
            event_loop
                .create_window(attrs)
                .expect("failed to create macOS window"),
        );

        if self
            .tokio_rt
            .block_on(
                self.painter
                    .set_window(ViewportId::ROOT, Some(Arc::clone(&window))),
            )
            .is_err()
        {
            let mut fallback_cfg = egui_wgpu::WgpuConfiguration::default();
            if let egui_wgpu::WgpuSetup::CreateNew(ref mut create_new) = fallback_cfg.wgpu_setup {
                create_new.native_adapter_selector = Some(Arc::new(|adapters, surface| {
                    adapters
                        .iter()
                        .find(|a| surface.is_none_or(|s| a.is_surface_supported(s)))
                        .cloned()
                        .or_else(|| adapters.first().cloned())
                        .ok_or_else(|| "No wgpu adapter found".to_string())
                }));
            }
            self.painter = self.tokio_rt.block_on(Painter::new(
                self.egui_ctx.clone(),
                fallback_cfg,
                false,
                egui_wgpu::RendererOptions::default(),
            ));
            self.tokio_rt
                .block_on(
                    self.painter
                        .set_window(ViewportId::ROOT, Some(Arc::clone(&window))),
                )
                .expect("failed to attach wgpu surface to window");
        }

        if let Some(rs) = self.painter.render_state() {
            self.target_format = rs.target_format;
        }

        let max_tex_side = self.painter.max_texture_side();
        let egui_state = egui_winit::State::new(
            self.egui_ctx.clone(),
            ViewportId::ROOT,
            event_loop,
            Some(window.scale_factor() as f32),
            event_loop.system_theme(),
            max_tex_side,
        );
        window.request_redraw();
        self.window = Some(window);
        self.egui_state = Some(egui_state);
        self.sync_window_level();
        self.sync_popout_window(event_loop);
    }

    fn user_event(&mut self, event_loop: &ActiveEventLoop, event: UserEvent) {
        match event {
            UserEvent::SnapshotReady => {
                self.ingest_latest_snapshot();
                self.flush_popout_pos_if_dirty();
                if self.window_visible
                    && let Some(w) = &self.window
                {
                    w.request_redraw();
                }
                if let Some(pop_win) = &self.popout_window {
                    pop_win.request_redraw();
                }
            }
            UserEvent::Ipc(cmd) => match cmd {
                IpcCommand::ActivateWindow => self.activate_main_window(),
                IpcCommand::TogglePopout => {
                    self.toggle_popout_mode();
                    self.sync_popout_window(event_loop);
                }
                IpcCommand::ToggleGamingMode => self.toggle_gaming_mode(),
                IpcCommand::ReadTelemetry => {}
                IpcCommand::Quit => {
                    self.on_shutdown();
                    event_loop.exit();
                }
            },
            #[cfg(any(target_os = "macos", target_os = "windows"))]
            UserEvent::TrayMenu(menu_id) => {
                let (open_id, pop_id, gm_id, quit_id) = match &self.tray {
                    Some(t) => (
                        t.id_open_main.clone(),
                        t.id_toggle_popout.clone(),
                        t.id_toggle_gaming.clone(),
                        t.id_quit.clone(),
                    ),
                    None => return,
                };
                if menu_id == open_id {
                    self.activate_main_window();
                } else if menu_id == pop_id {
                    self.toggle_popout_mode();
                    self.sync_popout_window(event_loop);
                } else if menu_id == gm_id {
                    self.toggle_gaming_mode();
                } else if menu_id == quit_id {
                    self.on_shutdown();
                    event_loop.exit();
                }
            }
        }
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        window_id: WindowId,
        event: WindowEvent,
    ) {
        if let Some(pop_win) = self.popout_window.as_ref().cloned()
            && pop_win.id() == window_id
        {
            let repaint = if let Some(pop_state) = &mut self.popout_egui_state {
                pop_state.on_window_event(&pop_win, &event).repaint
            } else {
                false
            };
            if repaint {
                pop_win.request_redraw();
            }

            match event {
                WindowEvent::CloseRequested => {
                    self.show_popout_overlay = false;
                    self.app.theme.popout_mode = PopoutMode::Off;
                    self.save_preferences();
                    self.sync_popout_window(event_loop);
                    if let Some(main_win) = &self.window {
                        main_win.request_redraw();
                    }
                }
                WindowEvent::Moved(phys_pos) => {
                    self.popout_pos_x = Some(phys_pos.x);
                    self.popout_pos_y = Some(phys_pos.y);
                    self.popout_pos_dirty_at = Some(Instant::now());
                }
                WindowEvent::Resized(phys_size) => {
                    if let (Some(w), Some(h), Some(pop_painter)) = (
                        std::num::NonZeroU32::new(phys_size.width),
                        std::num::NonZeroU32::new(phys_size.height),
                        self.popout_painter.as_mut(),
                    ) {
                        pop_painter.on_window_resized(ViewportId::ROOT, w, h);
                    }
                    pop_win.request_redraw();
                }
                WindowEvent::CursorEntered { .. } => {
                    self.popout_hovered = true;
                    pop_win.request_redraw();
                }
                WindowEvent::CursorLeft { .. } => {
                    self.popout_hovered = false;
                    pop_win.request_redraw();
                }
                WindowEvent::RedrawRequested => {
                    self.ingest_latest_snapshot();
                    self.flush_popout_pos_if_dirty();
                    self.draw_native_popout_window(&pop_win, event_loop);

                    if let Some(limit_ms) = self.auto_exit_ms
                        && self.start_instant.elapsed().as_millis() as u64 >= limit_ms
                    {
                        self.on_shutdown();
                        event_loop.exit();
                        return;
                    }

                    let main_minimized = self
                        .window
                        .as_ref()
                        .and_then(|w| w.is_minimized())
                        .unwrap_or(true);
                    let main_animating = self.window_visible
                        && !main_minimized
                        && self.gaming_mode.visual_throttle().visual_frame_rate != 0;
                    if !main_animating && self.gaming_mode.visual_throttle().visual_frame_rate != 0
                    {
                        let pace_ms = if self.app.theme.high_frequency_visuals {
                            16
                        } else {
                            50
                        };
                        pop_win.request_redraw();
                        event_loop.set_control_flow(ControlFlow::WaitUntil(
                            Instant::now() + Duration::from_millis(pace_ms),
                        ));
                    }
                }
                _ => {}
            }
            return;
        }

        let Some(window) = self.window.as_ref().cloned() else {
            return;
        };
        if window.id() != window_id {
            return;
        }

        if let Some(egui_state) = &mut self.egui_state {
            let response = egui_state.on_window_event(&window, &event);
            if response.repaint && self.window_visible {
                window.request_redraw();
            }
        } else {
            return;
        }

        match event {
            WindowEvent::CloseRequested => {
                if self.tray.is_some() && self.auto_exit_ms.is_none() {
                    self.save_preferences();
                    self.window_visible = false;
                    window.set_visible(false);
                    if let Some(pop_win) = &self.popout_window {
                        pop_win.request_redraw();
                    } else {
                        event_loop.set_control_flow(ControlFlow::Wait);
                    }
                } else {
                    self.on_shutdown();
                    event_loop.exit();
                }
            }
            WindowEvent::ThemeChanged(winit_theme) => {
                self.app.theme.system_is_dark = matches!(winit_theme, winit::window::Theme::Dark);
                if self.window_visible {
                    window.request_redraw();
                }
                if let Some(pop_win) = &self.popout_window {
                    pop_win.request_redraw();
                }
            }
            WindowEvent::Resized(phys_size) => {
                if let (Some(w), Some(h)) = (
                    std::num::NonZeroU32::new(phys_size.width),
                    std::num::NonZeroU32::new(phys_size.height),
                ) {
                    self.painter.on_window_resized(ViewportId::ROOT, w, h);
                }
                if self.window_visible {
                    window.request_redraw();
                }
            }
            WindowEvent::RedrawRequested => {
                self.flush_popout_pos_if_dirty();
                if !self.window_visible {
                    if let Some(pop_win) = &self.popout_window {
                        pop_win.request_redraw();
                    } else {
                        event_loop.set_control_flow(ControlFlow::Wait);
                    }
                    return;
                }
                self.ingest_latest_snapshot();
                self.frame_count += 1;

                let elapsed_ms = self.start_instant.elapsed().as_millis() as u64;
                let ready_for_shot = if self.active_view == GuiView::WirelessRadar {
                    self.frame_count >= 6
                        && ((self.platform_cache.wifi_report.is_some()
                            && self.platform_cache.lan_report.is_some())
                            || elapsed_ms >= 9500)
                } else {
                    self.frame_count >= 5
                };

                if !self.popout_screenshot_mode
                    && self.screenshot_out.is_some()
                    && !self.screenshot_saved
                    && ready_for_shot
                {
                    let out_path = self.screenshot_out.clone();
                    self.egui_ctx
                        .send_viewport_cmd(egui::ViewportCommand::Screenshot(
                            egui::ScreenshotCallback::new(move |image: Arc<egui::ColorImage>| {
                                if let Some(path) = &out_path {
                                    save_color_image_png(&image, path);
                                }
                            }),
                        ));
                }

                let prev_pop = (
                    self.show_popout_overlay,
                    self.app.theme.active_skin_id.clone(),
                    self.app.theme.popout_mode,
                );

                let Some(mut raw_input) = self
                    .egui_state
                    .as_mut()
                    .map(|st| st.take_egui_input(&window))
                else {
                    return;
                };
                let egui_ctx = self.egui_ctx.clone();
                let mut full_output = egui_ctx.run_ui(raw_input.take(), |ui| {
                    self.draw_ui(ui);
                });

                if (
                    self.show_popout_overlay,
                    &self.app.theme.active_skin_id,
                    self.app.theme.popout_mode,
                ) != (prev_pop.0, &prev_pop.1, prev_pop.2)
                {
                    self.sync_popout_window(event_loop);
                }

                if let Some(st) = self.egui_state.as_mut() {
                    st.handle_platform_output_with_event_loop(
                        &window,
                        event_loop,
                        full_output.platform_output,
                    );
                }

                let mut screenshot_callbacks = Vec::new();
                for (vp_id, vp_output) in full_output.viewport_output {
                    if vp_id == ViewportId::ROOT {
                        for cmd in vp_output.commands {
                            if let egui::ViewportCommand::Screenshot(cb) = cmd {
                                screenshot_callbacks.push(cb);
                            }
                        }
                    }
                }

                let primitives = self
                    .egui_ctx
                    .tessellate(full_output.shapes, full_output.pixels_per_point);
                let _ = self.painter.paint_and_update_textures(
                    ViewportId::ROOT,
                    full_output.pixels_per_point,
                    [0.04, 0.05, 0.07, 1.0],
                    &primitives,
                    &mut full_output.textures_delta,
                    screenshot_callbacks,
                    &window,
                );

                if !self.popout_screenshot_mode
                    && self.screenshot_out.is_some()
                    && !self.screenshot_saved
                    && ready_for_shot
                {
                    if let Some(rs) = self.painter.render_state() {
                        let _ = rs.device.poll(wgpu::PollType::wait_indefinitely());
                    }
                    self.screenshot_saved = true;
                }

                if let Some(limit_ms) = self.auto_exit_ms
                    && self.start_instant.elapsed().as_millis() as u64 >= limit_ms
                {
                    self.on_shutdown();
                    event_loop.exit();
                    return;
                }

                let is_minimized = window.is_minimized().unwrap_or(false);
                if !self.window_visible
                    || is_minimized
                    || self.gaming_mode.visual_throttle().visual_frame_rate == 0
                {
                    if let Some(pop_win) = &self.popout_window
                        && self.gaming_mode.visual_throttle().visual_frame_rate != 0
                    {
                        pop_win.request_redraw();
                    } else {
                        event_loop.set_control_flow(ControlFlow::Wait);
                    }
                } else {
                    let pace_ms = if self.app.theme.high_frequency_visuals {
                        16
                    } else {
                        50
                    };
                    window.request_redraw();
                    if let Some(pop_win) = &self.popout_window {
                        pop_win.request_redraw();
                    }
                    event_loop.set_control_flow(ControlFlow::WaitUntil(
                        Instant::now() + Duration::from_millis(pace_ms),
                    ));
                }
            }
            _ => {}
        }
    }

    fn exiting(&mut self, _event_loop: &ActiveEventLoop) {
        self.on_shutdown();
    }
}

impl GuiApp {
    fn draw_screensaver_overlay(&mut self, ui: &mut egui::Ui, accent: Color32) {
        let pointer_moved = ui
            .ctx()
            .input(|inp| inp.pointer.delta().length_sq() > 0.0 || inp.pointer.any_pressed());
        if pointer_moved {
            self.screensaver_last_pointer_activity = Instant::now();
        }

        let is_stale = self.last_snapshot_received_at.elapsed() > SCREENSAVER_STALENESS_LIMIT;
        let metric_load = |m: &Metric<f64>| -> f64 {
            if is_stale {
                return -1.0;
            }
            match m.reading {
                MetricReading::Measured { value, .. } if value.is_finite() => value,
                _ => -1.0,
            }
        };
        let cpu_load = metric_load(&self.snapshot.system.cpu_percent);
        let mem_load = metric_load(&self.snapshot.system.mem_used_percent);
        let gpu_load = metric_load(&self.snapshot.system.gpu_utilization_percent);
        let disk_load = self
            .snapshot
            .disks
            .first()
            .map(|d| metric_load(&d.root_used_percent))
            .unwrap_or(-1.0);

        let speed_boost = if cpu_load >= 0.0 {
            1.0 + (cpu_load as f32 / 100.0) * 0.6
        } else {
            1.0
        };

        egui::CentralPanel::default().show(ui, |ui| {
            ui.horizontal(|ui| {
                ui.label(
                    RichText::new("NEXUS VECTOR TUNNEL SCREENSAVER (F12 / ESC to exit)")
                        .font(FontId::new(13.0, FontFamily::Name("michroma".into())))
                        .color(accent),
                );
                if ui.button("Exit Screensaver").clicked() {
                    self.screensaver_active = false;
                }
            });

            let hud_h = 34.0;
            let avail = ui.available_size();
            let shader_size = Vec2::new(avail.x.max(1.0), (avail.y - hud_h).max(120.0));
            embed_wgsl_pass(
                ui,
                self.target_format,
                shader_size,
                ShaderPassKind::VectorTunnel {
                    time_sec: self.start_instant.elapsed().as_secs_f32() * speed_boost,
                    viewport: [shader_size.x, shader_size.y],
                    edr_headroom: self.edr_headroom,
                },
            );

            // Auto-fade bottom HUD legend after 8.0 seconds of pointer inactivity
            let inactive_s = self.screensaver_last_pointer_activity.elapsed().as_secs_f32();
            let alpha = if inactive_s <= 8.0 {
                1.0
            } else {
                (1.0 - (inactive_s - 8.0)).clamp(0.0, 1.0)
            };
            if alpha > 0.01 {
                let fmt_hud = |v: f64| -> String {
                    if v < 0.0 {
                        "n/a".to_string()
                    } else {
                        format!("{v:.1}%")
                    }
                };
                let hud_color = Color32::from_rgba_unmultiplied(
                    accent.r(),
                    accent.g(),
                    accent.b(),
                    (alpha * 255.0) as u8,
                );
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new(format!(
                            "CPU: {}  |  Memory: {}  |  GPU: {}  |  Disk: {}  |  Press Esc to return to Nexus{}",
                            fmt_hud(cpu_load),
                            fmt_hud(mem_load),
                            fmt_hud(gpu_load),
                            fmt_hud(disk_load),
                            if is_stale { " [STALE >5s]" } else { "" }
                        ))
                        .monospace()
                        .color(hud_color),
                    );
                });
            }
        });
    }

    pub fn on_shutdown(&mut self) {
        if self.shutdown_done {
            return;
        }
        self.shutdown_done = true;
        self.popout_pos_dirty_at = None;
        self.save_preferences();
        if self.trace_recorder.is_recording() {
            let _ = self.trace_recorder.stop_and_save(&default_traces_dir());
        }
        if let Some(mut srv) = self.remote_server.take() {
            srv.stop();
        }
    }
}

fn embed_wgsl_pass(
    ui: &mut egui::Ui,
    target_format: wgpu::TextureFormat,
    size: Vec2,
    pass: ShaderPassKind,
) -> egui::Response {
    let (rect, resp) =
        ui.allocate_exact_size(size, egui::Sense::click().union(egui::Sense::hover()));
    let cb = Callback::new_paint_callback(
        rect,
        WgslPaintCallback {
            format: target_format,
            pass,
        },
    );
    ui.painter().add(cb);
    resp
}

fn empty_disk_report(root: &Path) -> DiskScanReport {
    let categories = DiskCategory::ALL
        .iter()
        .map(|&cat| CategoryTotal {
            category: cat,
            bytes: 0,
            file_count: 0,
            share_fraction: 1.0 / 7.0,
        })
        .collect();
    DiskScanReport {
        root_path: CompactString::from(root.to_string_lossy().as_ref()),
        total_bytes: 0,
        total_files: 0,
        categories,
        root_node: DiskNode {
            name: CompactString::from(root.file_name().and_then(|s| s.to_str()).unwrap_or("root")),
            full_path: CompactString::from(root.to_string_lossy().as_ref()),
            is_dir: true,
            size: 0,
            file_count: 0,
            folder_count: 0,
            category: DiskCategory::Other,
            children: Vec::new(),
        },
        top_directories: Vec::new(),
        largest_files: Vec::new(),
    }
}

fn list_trace_files(dir: &Path) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut files: Vec<PathBuf> = entries
        .filter_map(Result::ok)
        .map(|e| e.path())
        .filter(|p| p.extension().and_then(|e| e.to_str()) == Some("nexustrace"))
        .collect();
    files.sort();
    files.reverse();
    files
}

fn mean_opt(vals: &[Option<f64>]) -> Option<f64> {
    let mut sum = 0.0;
    let mut cnt = 0usize;
    for v in vals.iter().flatten() {
        if v.is_finite() {
            sum += *v;
            cnt += 1;
        }
    }
    if cnt > 0 {
        Some(sum / cnt as f64)
    } else {
        None
    }
}

fn fmt_opt(val: Option<f64>, unit: &str) -> String {
    match val {
        Some(v) if unit.is_empty() => format!("{v:.1}"),
        Some(v) => format!("{v:.1} {unit}"),
        None => "null".to_string(),
    }
}

fn load_winit_window_icon() -> Option<winit::window::Icon> {
    let mut decoder = png::Decoder::new(std::io::Cursor::new(UI_APP_ICON_PNG));
    decoder.set_transformations(png::Transformations::EXPAND | png::Transformations::STRIP_16);
    let mut reader = decoder.read_info().ok()?;
    let buf_size = reader.output_buffer_size()?;
    let mut buf = vec![0u8; buf_size];
    let info = reader.next_frame(&mut buf).ok()?;
    let bytes = &buf[..info.buffer_size()];
    let rgba = match info.color_type {
        png::ColorType::Rgba => bytes.to_vec(),
        png::ColorType::Rgb => {
            let mut out = Vec::with_capacity((info.width * info.height * 4) as usize);
            for chunk in bytes.as_chunks::<3>().0 {
                out.extend_from_slice(&[chunk[0], chunk[1], chunk[2], 255]);
            }
            out
        }
        _ => return None,
    };
    winit::window::Icon::from_rgba(rgba, info.width, info.height).ok()
}

fn configure_bundled_fonts(ctx: &egui::Context) {
    let (michroma_bytes, led_bytes) = load_standalone_fonts(None);
    let mut fonts = FontDefinitions::default();
    fonts.font_data.insert(
        "michroma".to_owned(),
        Arc::new(FontData::from_owned(michroma_bytes)),
    );
    fonts
        .font_data
        .insert("led".to_owned(), Arc::new(FontData::from_owned(led_bytes)));

    let prop_fallbacks = fonts
        .families
        .get(&FontFamily::Proportional)
        .cloned()
        .unwrap_or_default();
    let mono_fallbacks = fonts
        .families
        .get(&FontFamily::Monospace)
        .cloned()
        .unwrap_or_default();

    let mut michroma_list = vec!["michroma".to_owned()];
    michroma_list.extend(prop_fallbacks);
    fonts
        .families
        .insert(FontFamily::Name("michroma".into()), michroma_list);

    let mut led_list = vec!["led".to_owned()];
    led_list.extend(mono_fallbacks);
    fonts
        .families
        .insert(FontFamily::Name("led".into()), led_list);

    ctx.set_fonts(fonts);
}

fn save_color_image_png(image: &egui::ColorImage, path: &PathBuf) {
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let Ok(file) = std::fs::File::create(path) else {
        return;
    };
    let w = std::io::BufWriter::new(file);
    let mut encoder = png::Encoder::new(w, image.width() as u32, image.height() as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let Ok(mut writer) = encoder.write_header() else {
        return;
    };
    let mut raw = Vec::with_capacity(image.pixels.len() * 4);
    for px in &image.pixels {
        raw.extend_from_slice(&px.to_array());
    }
    let _ = writer.write_image_data(&raw);
}

/// Entrypoint for `nexus-monitor` / `nexus-gui` desktop binaries.
pub fn run_desktop_app() {
    let mut screenshot_out: Option<PathBuf> = None;
    let mut auto_exit_ms: Option<u64> = None;
    let mut initial_view = GuiView::Dest(Destination::Summary);
    let mut initial_disk_mode: i32 = 1;
    let mut no_single_instance = false;
    let mut harness = LaunchHarnessOptions::default();

    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--screenshot-out" => {
                if let Some(p) = args.next() {
                    screenshot_out = Some(PathBuf::from(p));
                }
            }
            "--auto-exit-ms" => {
                if let Some(ms) = args.next().and_then(|s| s.parse::<u64>().ok()) {
                    auto_exit_ms = Some(ms);
                }
            }
            "--disk-mode" => {
                if let Some(m) = args.next().and_then(|s| s.parse::<i32>().ok()) {
                    initial_disk_mode = m.clamp(1, 3);
                }
            }
            "--no-single-instance" => {
                no_single_instance = true;
            }
            "--view" => {
                if let Some(v) = args.next() {
                    initial_view = GuiView::from_slug(v.as_str());
                }
            }
            "--preset" => {
                if let Some(p) = args.next() {
                    harness.preset_query = Some(p);
                }
            }
            "--popout-skin" => {
                if let Some(s) = args.next() {
                    harness.popout_skin_id = Some(s);
                }
            }
            "--text-scale" => {
                if let Some(pct) = args.next().and_then(|s| s.parse::<u16>().ok()) {
                    harness.text_scale = Some(pct);
                }
            }
            "--screensaver" => {
                harness.screensaver = true;
            }
            "--test-edit-drawer" => {
                initial_view = GuiView::Dest(Destination::Summary);
                harness.test_edit_drawer = true;
            }
            "--test-configure-columns" => {
                harness.test_configure_columns = true;
            }
            _ => {}
        }
    }

    if auto_exit_ms.is_none()
        && let Ok(env_ms) = std::env::var("NEXUS_AUTO_EXIT_MS")
        && let Ok(parsed) = env_ms.trim().parse::<u64>()
    {
        auto_exit_ms = Some(parsed);
    }

    let event_loop = EventLoop::<UserEvent>::with_user_event()
        .build()
        .expect("failed to build winit EventLoop");
    let proxy = event_loop.create_proxy();
    let shared_telemetry = Arc::new(parking_lot::Mutex::new(SharedScreensaverTelemetry::new()));

    let ipc_guard = if no_single_instance || screenshot_out.is_some() {
        None
    } else {
        let ipc_proxy = proxy.clone();
        match acquire_single_instance_with_telemetry(
            default_ipc_socket_path(),
            Arc::clone(&shared_telemetry),
            move |cmd| {
                let _ = ipc_proxy.send_event(UserEvent::Ipc(cmd));
            },
        ) {
            Ok(InstanceLockOutcome::Primary(guard)) => Some(guard),
            Ok(InstanceLockOutcome::ForwardedToExistingPrimary) => {
                println!(
                    "nexus-monitor: existing instance activated via single-instance IPC socket."
                );
                return;
            }
            Err(e) => {
                eprintln!("nexus-monitor: single-instance lock warning: {e}");
                None
            }
        }
    };

    #[cfg(any(target_os = "macos", target_os = "windows"))]
    {
        let menu_proxy = proxy.clone();
        MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
            let _ = menu_proxy.send_event(UserEvent::TrayMenu(event.id));
        }));
    }

    let mut app = GuiApp::new(
        proxy,
        ipc_guard,
        shared_telemetry,
        screenshot_out,
        auto_exit_ms,
        initial_view,
        initial_disk_mode,
        harness,
    );
    let _ = &app.cadence_coord;
    event_loop
        .run_app(&mut app)
        .expect("winit event loop failed");
}

/// Runs timed offscreen `wgpu` Compute (`benchmarkCompute`) and Fragment (`benchmarkFragment`)
/// benchmark passes using `device.poll(wgpu::PollType::wait_indefinitely())` on each batch.
pub fn run_gpu_benchmark_offscreen(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    target_format: wgpu::TextureFormat,
    duration: Duration,
    cancel: &AtomicBool,
    mut progress_cb: impl FnMut(f32),
) -> (f64, f64) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("nexus_bench_offscreen_wgsl"),
        source: wgpu::ShaderSource::Wgsl(SHADER_BENCHMARK_WGSL.into()),
    });

    let compute_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("nexus_bench_offscreen_bgl"),
        entries: &[wgpu::BindGroupLayoutEntry {
            binding: 0,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only: false },
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        }],
    });
    let compute_pl = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("nexus_bench_offscreen_cpl"),
        bind_group_layouts: &[Some(&compute_bgl)],
        immediate_size: 0,
    });
    let compute_pipe = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some("nexus_bench_offscreen_cp"),
        layout: Some(&compute_pl),
        module: &shader,
        entry_point: Some("benchmarkCompute"),
        compilation_options: Default::default(),
        cache: None,
    });
    let storage_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("nexus_bench_offscreen_buf"),
        size: 16_384 * 4,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let compute_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("nexus_bench_offscreen_bg"),
        layout: &compute_bgl,
        entries: &[wgpu::BindGroupEntry {
            binding: 0,
            resource: storage_buf.as_entire_binding(),
        }],
    });

    // Pass 1: Compute (256 workgroups * 64 threads * 512 FLOPs = 8,388,608 FLOPs per dispatch)
    const WORKGROUPS: u32 = 256;
    const THREADS_PER_WG: f64 = 64.0;
    const COMPUTE_FLOPS_PER_THREAD: f64 = 512.0;
    const DISPATCHES_PER_BATCH: u32 = 8;
    let flops_per_compute_batch = (WORKGROUPS as f64)
        * THREADS_PER_WG
        * COMPUTE_FLOPS_PER_THREAD
        * (DISPATCHES_PER_BATCH as f64);

    let comp_start = Instant::now();
    let mut comp_flops = 0.0_f64;
    let dur_secs = duration.as_secs_f64().max(1e-6);
    while comp_start.elapsed() < duration && !cancel.load(AtomicOrdering::Relaxed) {
        let mut enc = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("nexus_bench_comp_enc"),
        });
        {
            let mut cpass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("nexus_bench_comp_pass"),
                timestamp_writes: None,
            });
            cpass.set_pipeline(&compute_pipe);
            cpass.set_bind_group(0, &compute_bg, &[]);
            for _ in 0..DISPATCHES_PER_BATCH {
                cpass.dispatch_workgroups(WORKGROUPS, 1, 1);
            }
        }
        queue.submit(Some(enc.finish()));
        let _ = device.poll(wgpu::PollType::wait_indefinitely());
        comp_flops += flops_per_compute_batch;
        progress_cb(((comp_start.elapsed().as_secs_f64() / dur_secs) * 0.5).clamp(0.0, 0.5) as f32);
    }
    let comp_gflops = comp_flops / (comp_start.elapsed().as_secs_f64().max(1e-6) * 1e9);

    // Pass 2: Offscreen Graphics (`benchmarkFragment`, 512 FLOPs/pixel over 1024x1024 target)
    let render_pl = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("nexus_bench_offscreen_rpl"),
        bind_group_layouts: &[],
        immediate_size: 0,
    });
    let render_pipe = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("nexus_bench_offscreen_rp"),
        layout: Some(&render_pl),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some("benchmarkVertex"),
            compilation_options: Default::default(),
            buffers: &[],
        },
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some("benchmarkFragment"),
            compilation_options: Default::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format: target_format,
                blend: Some(wgpu::BlendState::REPLACE),
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        primitive: wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample: wgpu::MultisampleState::default(),
        multiview_mask: None,
        cache: None,
    });

    const TEX_DIM: u32 = 1024;
    const GFX_DRAWS_PER_BATCH: u32 = 4;
    const FRAG_FLOPS_PER_PIXEL: f64 = 512.0;
    let flops_per_gfx_batch =
        (TEX_DIM as f64) * (TEX_DIM as f64) * FRAG_FLOPS_PER_PIXEL * (GFX_DRAWS_PER_BATCH as f64);

    let offscreen_tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("nexus_bench_offscreen_tex"),
        size: wgpu::Extent3d {
            width: TEX_DIM,
            height: TEX_DIM,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: target_format,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    });
    let offscreen_view = offscreen_tex.create_view(&wgpu::TextureViewDescriptor::default());

    let gfx_start = Instant::now();
    let mut gfx_flops = 0.0_f64;
    while gfx_start.elapsed() < duration && !cancel.load(AtomicOrdering::Relaxed) {
        let mut enc = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("nexus_bench_gfx_enc"),
        });
        {
            let mut rpass = enc.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("nexus_bench_gfx_pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &offscreen_view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            rpass.set_pipeline(&render_pipe);
            for _ in 0..GFX_DRAWS_PER_BATCH {
                rpass.draw(0..3, 0..1);
            }
        }
        queue.submit(Some(enc.finish()));
        let _ = device.poll(wgpu::PollType::wait_indefinitely());
        gfx_flops += flops_per_gfx_batch;
        progress_cb(
            (0.5 + (gfx_start.elapsed().as_secs_f64() / dur_secs) * 0.5).clamp(0.5, 1.0) as f32,
        );
    }
    let gfx_gflops = gfx_flops / (gfx_start.elapsed().as_secs_f64().max(1e-6) * 1e9);
    progress_cb(1.0);
    (comp_gflops, gfx_gflops)
}

/// Runs headless `wgpu` Compute (`benchmarkCompute`) and Fragment (`benchmarkFragment`)
/// benchmarks using a temporary current-thread Tokio runtime and default high-performance adapter
/// (falling back to software/Lavapipe adapter when running inside a headless VM or Wine).
pub fn run_headless_gpu_benchmark(duration: Duration, cancel: &AtomicBool) -> Option<(f64, f64)> {
    let rt = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .ok()?;
    let instance = wgpu::Instance::default();
    let adapter = match rt.block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
        power_preference: wgpu::PowerPreference::HighPerformance,
        compatible_surface: None,
        force_fallback_adapter: false,
        ..Default::default()
    })) {
        Ok(a) => a,
        Err(_) => rt
            .block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::None,
                compatible_surface: None,
                force_fallback_adapter: true,
                ..Default::default()
            }))
            .ok()?,
    };
    let (device, queue) = rt
        .block_on(adapter.request_device(&wgpu::DeviceDescriptor::default()))
        .ok()?;
    Some(run_gpu_benchmark_offscreen(
        &device,
        &queue,
        wgpu::TextureFormat::Bgra8Unorm,
        duration,
        cancel,
        |_| {},
    ))
}

/// Runs an HTTP download/upload transfer benchmark via `curl` (returning `(down_mbps, up_mbps, note)`).
/// Returns `0.0` honestly if offline or unreachable.
pub fn run_internet_benchmark(duration: Duration, cancel: &AtomicBool) -> (f64, f64, String) {
    if cancel.load(AtomicOrdering::Relaxed) {
        return (0.0, 0.0, "Cancelled".to_string());
    }
    let max_secs = duration.as_secs().clamp(2, 10).to_string();
    let curl_bin = if cfg!(target_os = "macos") && Path::new("/usr/bin/curl").exists() {
        "/usr/bin/curl"
    } else {
        "curl"
    };
    let dev_null = if cfg!(windows) { "NUL" } else { "/dev/null" };

    let mut down_cmd = std::process::Command::new(curl_bin);
    down_cmd.args([
        "-sS",
        "-o",
        dev_null,
        "--max-time",
        &max_secs,
        "-w",
        "%{speed_download}",
        "https://speed.cloudflare.com/__down?bytes=25000000",
    ]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        down_cmd.creation_flags(0x0800_0000);
    }
    let down_mbps = down_cmd
        .output()
        .ok()
        .filter(|o| o.status.success())
        .and_then(|o| {
            String::from_utf8_lossy(&o.stdout)
                .trim()
                .parse::<f64>()
                .ok()
        })
        .filter(|v| v.is_finite() && *v > 0.0)
        .map(|bps| (bps * 8.0) / 1_000_000.0)
        .unwrap_or(0.0);

    if cancel.load(AtomicOrdering::Relaxed) {
        return (down_mbps, 0.0, "Cancelled".to_string());
    }

    let tmp_upload = std::env::temp_dir().join(format!("nexus_up_{}.bin", std::process::id()));
    let _ = std::fs::write(&tmp_upload, vec![0xA5u8; 2 * 1024 * 1024]);
    let mut up_cmd = std::process::Command::new(curl_bin);
    up_cmd
        .args(["-sS", "-o", dev_null, "--max-time", &max_secs, "-T"])
        .arg(&tmp_upload)
        .args(["-w", "%{speed_upload}", "https://speed.cloudflare.com/__up"]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        up_cmd.creation_flags(0x0800_0000);
    }
    let up_mbps = up_cmd
        .output()
        .ok()
        .filter(|o| o.status.success())
        .and_then(|o| {
            String::from_utf8_lossy(&o.stdout)
                .trim()
                .parse::<f64>()
                .ok()
        })
        .filter(|v| v.is_finite() && *v > 0.0)
        .map(|bps| (bps * 8.0) / 1_000_000.0)
        .unwrap_or(0.0);
    let _ = std::fs::remove_file(&tmp_upload);

    let note = if down_mbps == 0.0 && up_mbps == 0.0 {
        "Offline or transfer endpoint unreachable".to_string()
    } else {
        format!("curl HTTP down={down_mbps:.2} Mbps up={up_mbps:.2} Mbps")
    };
    (down_mbps, up_mbps, note)
}

fn hig_green(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(48, 209, 88)
    } else {
        Color32::from_rgb(28, 138, 56)
    }
}

fn hig_blue(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(10, 132, 255)
    } else {
        Color32::from_rgb(0, 102, 214)
    }
}

fn hig_amber(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(255, 195, 50)
    } else {
        Color32::from_rgb(186, 114, 0)
    }
}

fn hig_red(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(255, 69, 58)
    } else {
        Color32::from_rgb(204, 42, 34)
    }
}

fn hig_purple(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(191, 90, 242)
    } else {
        Color32::from_rgb(137, 68, 171)
    }
}

fn hig_gray(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(142, 142, 147)
    } else {
        Color32::from_rgb(108, 108, 114)
    }
}

fn measured_opt(m: &Metric<f64>) -> Option<f64> {
    match m.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
        _ => None,
    }
}

fn measured_zero(m: &Metric<f64>) -> f64 {
    measured_opt(m).unwrap_or(0.0)
}

fn sum_measured_metrics<'a>(iter: impl IntoIterator<Item = &'a Metric<f64>>) -> Option<f64> {
    let mut sum = 0.0;
    let mut any = false;
    for m in iter {
        if let Some(v) = measured_opt(m) {
            sum += v;
            any = true;
        }
    }
    any.then_some(sum)
}

fn push_graph_sample(
    graph: &mut CompressedHistoryGraph,
    pre_sec: f64,
    sec: f64,
    seed_two_points: bool,
    val: Option<f64>,
) {
    let clean = val.filter(|v| v.is_finite());
    if seed_two_points {
        graph.push_raw(pre_sec, clean);
    }
    graph.push_raw(sec, clean);
}

fn push_graph_metric(
    graph: &mut CompressedHistoryGraph,
    pre_sec: f64,
    sec: f64,
    seed_two_points: bool,
    m: &Metric<f64>,
) {
    if seed_two_points {
        graph.push_metric(pre_sec, m);
    }
    graph.push_metric(sec, m);
}

fn draw_hig_progress_bar(ui: &mut egui::Ui, width: f32, height: f32, frac: f32, fill: Color32) {
    let (rect, _) =
        ui.allocate_exact_size(Vec2::new(width.max(40.0), height), egui::Sense::hover());
    let track = if ui.visuals().dark_mode {
        Color32::from_rgba_unmultiplied(255, 255, 255, 22)
    } else {
        Color32::from_rgba_unmultiplied(15, 23, 42, 18)
    };
    let radius = CornerRadius::same((height * 0.5).round().max(2.0) as u8);
    let painter = ui.painter_at(rect);
    painter.rect_filled(rect, radius, track);
    let clamped = frac.clamp(0.0, 1.0);
    let fw = (rect.width() * clamped).max(if clamped > 0.005 { height } else { 0.0 });
    if fw > 0.5 {
        painter.rect_filled(
            Rect::from_min_size(rect.min, Vec2::new(fw.min(rect.width()), rect.height())),
            radius,
            fill,
        );
    }
}

fn draw_stacked_bar(
    ui: &mut egui::Ui,
    width: f32,
    height: f32,
    radius: u8,
    segments: &[(&str, f64, Color32)],
    total: f64,
) {
    let (rect, resp) =
        ui.allocate_exact_size(Vec2::new(width.max(40.0), height), egui::Sense::hover());
    let track = if ui.visuals().dark_mode {
        Color32::from_rgba_unmultiplied(255, 255, 255, 22)
    } else {
        Color32::from_rgba_unmultiplied(15, 23, 42, 18)
    };
    let cr = CornerRadius::same(radius);
    let painter = ui.painter_at(rect);
    painter.rect_filled(rect, cr, track);

    let denom = if total.is_finite() && total > 0.0 {
        total
    } else {
        segments
            .iter()
            .map(|(_, v, _)| v.max(0.0))
            .sum::<f64>()
            .max(1e-9)
    };

    let mut cur_x = rect.min.x;
    let total_w = rect.width();
    for (idx, (_, val, col)) in segments.iter().enumerate() {
        let frac = (val.max(0.0) / denom).clamp(0.0, 1.0) as f32;
        if frac <= 0.001 {
            continue;
        }
        let seg_w = (total_w * frac).min(rect.max.x - cur_x);
        if seg_w <= 0.5 {
            continue;
        }
        let seg_rect = Rect::from_min_size(Pos2::new(cur_x, rect.min.y), Vec2::new(seg_w, height));
        let seg_cr = if idx == 0 && (cur_x + seg_w + 1.0) >= rect.max.x {
            cr
        } else if idx == 0 {
            CornerRadius {
                nw: radius,
                sw: radius,
                ne: 0,
                se: 0,
            }
        } else if (cur_x + seg_w + 1.0) >= rect.max.x {
            CornerRadius {
                nw: 0,
                sw: 0,
                ne: radius,
                se: radius,
            }
        } else {
            CornerRadius::ZERO
        };
        painter.rect_filled(seg_rect, seg_cr, *col);
        cur_x += seg_w;
    }

    if resp.hovered() && !segments.is_empty() {
        resp.on_hover_ui(|ui| {
            for (lbl, val, col) in segments {
                let pct = (val.max(0.0) / denom) * 100.0;
                ui.colored_label(*col, format!("{lbl}: {pct:.1}%"));
            }
        });
    }
}

fn draw_health_ring_mini(ui: &mut egui::Ui, score: f64, level: HealthLevel) {
    let (rect, _) = ui.allocate_exact_size(Vec2::new(76.0, 76.0), egui::Sense::hover());
    let center = rect.center();
    let radius = 31.0;
    let ring_col = match level {
        HealthLevel::Excellent | HealthLevel::Good => hig_green(ui),
        HealthLevel::Fair => hig_amber(ui),
        HealthLevel::Poor | HealthLevel::Critical => hig_red(ui),
    };
    let track = if ui.visuals().dark_mode {
        Color32::from_rgba_unmultiplied(255, 255, 255, 26)
    } else {
        Color32::from_rgba_unmultiplied(15, 23, 42, 22)
    };
    let painter = ui.painter_at(rect);
    painter.circle_stroke(center, radius, Stroke::new(7.0, track));

    let frac = (score.clamp(0.0, 100.0) / 100.0) as f32;
    let steps = ((frac * 36.0).ceil() as usize).max(2);
    if frac > 0.01 {
        let mut pts = Vec::with_capacity(steps + 1);
        for i in 0..=steps {
            let t = (i as f32) / (steps as f32);
            let angle = -std::f32::consts::FRAC_PI_2 + t * frac * std::f32::consts::TAU;
            pts.push(Pos2::new(
                center.x + radius * angle.cos(),
                center.y + radius * angle.sin(),
            ));
        }
        painter.add(egui::Shape::line(pts, Stroke::new(7.0, ring_col)));
    }

    painter.text(
        center - Vec2::new(0.0, 5.0),
        egui::Align2::CENTER_CENTER,
        format!("{:.0}", score.clamp(0.0, 100.0)),
        FontId::proportional(19.0),
        ring_col,
    );
    painter.text(
        center + Vec2::new(0.0, 13.0),
        egui::Align2::CENTER_CENTER,
        "Health",
        FontId::proportional(10.0),
        ui.visuals().weak_text_color(),
    );
}

fn human_sensor_subtitle(sp: &SensorPath, _snap: &SystemSnapshot) -> String {
    match (
        sp.category.as_str(),
        sp.instance.as_str(),
        sp.metric.as_str(),
    ) {
        ("cpu", "total", "utilization") => "Host CPU utilization".to_string(),
        ("cpu", "[Group:AllCores]", "utilization") => "Per-core CPU utilization".to_string(),
        ("cpu", "[P-Cluster]", _) => "Performance core cluster".to_string(),
        ("cpu", "[E-Cluster]", _) => "Efficiency core cluster".to_string(),
        ("memory", "system", "used_percent") => "Physical memory utilization".to_string(),
        ("memory", "system", "pressure") => "macOS memory pressure index".to_string(),
        ("gpu", "0", "utilization") => "Apple Silicon GPU activity".to_string(),
        ("soc", "package", "watts") => "SoC total package power".to_string(),
        ("soc", "p_cluster", "residency") => "Performance & Efficiency clusters".to_string(),
        ("soc", "e_cluster", "residency") => "Efficiency core cluster residency".to_string(),
        ("thermal", _, "celsius") => "Silicon thermal sensor".to_string(),
        ("disk", _, "categories") => "Storage category breakdown".to_string(),
        ("os", "system", "uptime") => "macOS host uptime".to_string(),
        (cat, inst, met) => {
            let pretty_inst = inst
                .trim_start_matches('[')
                .trim_end_matches(']')
                .replace('_', " ");
            let pretty_met = met.replace('_', " ");
            format!(
                "{} · {} ({})",
                cat.to_ascii_uppercase(),
                pretty_inst,
                pretty_met
            )
        }
    }
}

fn popout_skin_short_label(skin_id: &str) -> &'static str {
    match nexus_core::canonical_popout_skin_id(skin_id) {
        "com.nexus.console" => "Console",
        "com.nexus.traces" => "Traces",
        "winamp" => "Winamp",
        "mediaPlayer" => "Media Player",
        "com.nexus.wintaskman" => "Win2000",
        _ => "Console",
    }
}
