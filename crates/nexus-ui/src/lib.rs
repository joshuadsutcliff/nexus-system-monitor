#![forbid(unsafe_code)]

pub mod assets;
pub mod desktop;
pub mod desktop_views;
pub mod faces;
pub mod ipc;
pub mod prefs;
pub mod shaders;
pub mod theme;
pub mod views;

use nexus_automation::GamingModeVisualThrottle;
use nexus_core::SystemSnapshot;

pub use assets::{
    UI_APP_ICON_PNG, UI_FONT_LED_DOT_MATRIX_TTF, UI_FONT_MICHROMA_REGULAR_TTF,
    UI_SKIN_CONSOLE_JSON, UI_SKIN_TRACES_JSON, UI_SKIN_WINAMP_JSON, UI_SKIN_WINTASKMAN_JSON,
    UI_SKIN_WINTASKMAN_LOGO_PNG, UI_SKIN_WMP_JSON, load_standalone_fonts, load_standalone_skins,
    resolve_bundle_resources_dir,
};
pub use desktop::run_headless_gpu_benchmark;
pub use desktop_views::{
    HigGuidanceCard, draw_hig_command_box, hig_amber, hig_blue, hig_gray, hig_green, hig_purple,
    hig_red, hig_secondary_text,
};
pub use faces::{
    BezierSegment, ColumnDisplayStyle, CompressedHistoryGraph, DashboardTile, FaceKind,
    FaceProperties, PageEditSession, PageEngine, ProcessorClusterGridState, SensorDescriptor,
    SensorPath, SensorRegistry, SensorValue, TableColumnConfig, TileFaceConfig, TimedHistorySample,
    VfdSegmentGeometry, VfdSegmentedMeterState, WorkspaceProfile, build_disk_shader_blobs,
    build_vector_tunnel_segments, default_app_columns, default_process_columns,
};
pub use ipc::{
    InstanceLockOutcome, IpcCommand, SCREENSAVER_STALENESS_LIMIT, ScreensaverTelemetryPayload,
    SharedScreensaverTelemetry, SingleInstanceGuard, acquire_single_instance_or_activate,
    acquire_single_instance_with_telemetry, default_ipc_socket_path, query_ipc_telemetry,
    send_ipc_command,
};
pub use prefs::{
    UserPreferences, app_support_dir, clamp_text_size_percent, default_telemetry_db_path,
};
pub use shaders::{
    GpuDiskBlob, GpuDiskUniforms, GpuTunnelSegment, GpuTunnelUniforms, SHADER_BENCHMARK_WGSL,
    SHADER_DISK_SPACE_WGSL, SHADER_VECTOR_TUNNEL_WGSL, ShaderError, validate_all_embedded_shaders,
    validate_wgsl_shader,
};
pub use theme::{
    CrystalGlassPreset, ResolvedPopoutView, ThemeEngine, ThemePresetMetadata,
    VFD_LIGHT_INSTRUMENT_FACE_SRGB, VFD_LIGHT_UNLIT_SEGMENT_SRGB, VFD_SEGMENT_STYLE_TABLE,
    VfdLitAppearance, WallpaperLuminanceAnalyzer, hsv_to_rgb, parse_hex_rgba, rgb_to_hex,
    rgb_to_hsv, vfd_dark_unlit_segment_srgb, vfd_positional_lit_srgb, vfd_positional_step,
    vfd_positional_unlit_srgb,
};
pub use views::{
    Destination, ExitedRowWashStyle, ProcessGroupingMode, ProcessInspectorDossier,
    ProcessTableRowView, ProcessTableState, RankGlowEntry, compute_exited_row_wash, heat_ramp_rgba,
    process_update_excitation,
};

/// Root application shell coordinating `ThemeEngine`, `PageEngine`, `ProcessTableState`,
/// VFD meters, and `GamingModeVisualThrottle`.
pub struct TaskManagerApp {
    pub active_destination: Destination,
    pub theme: ThemeEngine,
    pub page_engine: PageEngine,
    pub process_table: ProcessTableState,
    pub cpu_meter: VfdSegmentedMeterState,
    pub mem_meter: VfdSegmentedMeterState,
    pub gpu_meter: VfdSegmentedMeterState,
    pub cpu_history: CompressedHistoryGraph,
    pub visual_throttle: GamingModeVisualThrottle,
    pub last_recorded_ns: Option<u64>,
}

impl Default for TaskManagerApp {
    fn default() -> Self {
        Self::new()
    }
}

impl TaskManagerApp {
    pub fn new() -> Self {
        Self {
            active_destination: Destination::Summary,
            theme: ThemeEngine::new(),
            page_engine: PageEngine::new_default_summary(),
            process_table: ProcessTableState::new(),
            cpu_meter: VfdSegmentedMeterState::new(),
            mem_meter: VfdSegmentedMeterState::new(),
            gpu_meter: VfdSegmentedMeterState::new(),
            cpu_history: CompressedHistoryGraph::new(512),
            visual_throttle: GamingModeVisualThrottle::INACTIVE_DEFAULT,
            last_recorded_ns: None,
        }
    }

    pub fn set_gaming_mode_throttle(&mut self, throttle: GamingModeVisualThrottle) {
        self.visual_throttle = throttle;
        self.theme.visual_bloom = throttle.visual_bloom_enabled;
    }

    /// Steps the VFD ballistic meters at display refresh cadence without mutating history graphs.
    pub fn step_meters(&mut self, snap: &SystemSnapshot, dt_seconds: f64) {
        let snap_now = self.visual_throttle.visual_frame_rate == 0;
        let to_norm = |m: &nexus_core::Metric<f64>| match m.reading {
            nexus_core::MetricReading::Measured { value, .. } => (value / 100.0).clamp(0.0, 1.0),
            _ => 0.0,
        };

        self.cpu_meter
            .step(to_norm(&snap.system.cpu_percent), dt_seconds, snap_now);
        self.mem_meter
            .step(to_norm(&snap.system.mem_used_percent), dt_seconds, snap_now);
        self.gpu_meter.step(
            to_norm(&snap.system.gpu_utilization_percent),
            dt_seconds,
            snap_now,
        );
    }

    /// Records a snapshot into history and rebuilds process rows only when `sampled_at_ns` advances.
    pub fn record_snapshot_history(&mut self, snap: &SystemSnapshot) -> bool {
        if let Some(prev) = self.last_recorded_ns
            && snap.sampled_at_ns <= prev
        {
            return false;
        }
        self.last_recorded_ns = Some(snap.sampled_at_ns);
        let sec = (snap.sampled_at_ns as f64) / 1_000_000_000.0;
        self.cpu_history.push_metric(sec, &snap.system.cpu_percent);
        let _ = self.process_table.build_rows(snap);
        true
    }

    /// Seeds two closely spaced initial samples so history curves draw immediately on frame 1.
    pub fn seed_initial_history(&mut self, snap: &SystemSnapshot) {
        let sec = (snap.sampled_at_ns as f64) / 1_000_000_000.0;
        let pre_sec = (sec - 0.5).max(0.0);
        self.cpu_history
            .push_metric(pre_sec, &snap.system.cpu_percent);
        self.cpu_history.push_metric(sec, &snap.system.cpu_percent);
        self.last_recorded_ns = Some(snap.sampled_at_ns);
        let _ = self.process_table.build_rows(snap);
    }

    pub fn apply_snapshot(&mut self, snap: &SystemSnapshot, dt_seconds: f64) {
        self.step_meters(snap, dt_seconds);
        let _ = self.record_snapshot_history(snap);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use compact_str::CompactString;
    use nexus_core::{
        DisplayMode, Metric, MetricProvenance, MetricQuality, PopoutMode, ProcessKey,
        ProcessSnapshot, SourceId, UnitTag, VfdTextColor,
    };

    #[test]
    fn all_three_wgsl_shaders_parse_and_validate_via_naga() {
        validate_all_embedded_shaders().expect("all 3 WGSL shaders must pass naga validation");
    }

    #[test]
    fn theme_engine_loads_bundled_fonts_skins_and_crystal_glass_luminance() {
        let mut theme = ThemeEngine::new();
        assert_eq!(theme.skins.len(), 5);
        assert_eq!(CrystalGlassPreset::ALL.len(), 20);

        for preset in CrystalGlassPreset::ALL {
            let meta = preset.metadata();
            assert!(!meta.id.is_empty());
            assert!(meta.accent_hex.starts_with('#'));
            assert!(meta.glass_opacity_floor <= meta.glass_opacity_ceiling);
        }

        // Legacy serde aliases must continue to deserialize cleanly
        assert_eq!(
            serde_json::from_str::<CrystalGlassPreset>("\"ObsidianGlass\"").unwrap(),
            CrystalGlassPreset::CrystalGlass
        );
        assert_eq!(
            serde_json::from_str::<CrystalGlassPreset>("\"CyberCyan\"").unwrap(),
            CrystalGlassPreset::Futuristic
        );

        // HSV conversion round-trip & Mono saturation clamp
        let (h, s, v) = rgb_to_hsv(0.04, 0.52, 1.0);
        let rt = hsv_to_rgb(h, s, v);
        assert!((rt[0] - 0.04).abs() < 1e-4);
        assert!((rt[1] - 0.52).abs() < 1e-4);
        assert!((rt[2] - 1.0).abs() < 1e-4);
        assert_eq!(rgb_to_hex([1.0, 0.0, 0.5]), "#FF0080");

        theme.display_mode = DisplayMode::Mono;
        let mono_rgb = theme.apply_display_saturation([0.2, 0.8, 0.4]);
        assert!((mono_rgb[0] - mono_rgb[1]).abs() < 1e-5);
        assert!((mono_rgb[1] - mono_rgb[2]).abs() < 1e-5);
        theme.display_mode = DisplayMode::Color;

        // JSON export/import roundtrip for custom preset
        let exported = theme.export_active_preset_json().expect("export preset");
        let imported = theme
            .import_custom_preset_json(&exported)
            .expect("import preset");
        assert_eq!(imported.id.as_str(), "crystal-glass");

        // Verify Michroma and LED Dot-Matrix fonts were loaded into cosmic-text fontdb
        let face_count = theme.font_system.db().faces().count();
        assert!(
            face_count >= 2,
            "expected at least 2 bundled font faces, got {face_count}"
        );

        // Positional VFD ramp stops
        let s0 = vfd_positional_lit_srgb(0);
        let s9 = vfd_positional_lit_srgb(9);
        let s15 = vfd_positional_lit_srgb(15);
        assert!(s0[1] > s0[0]);
        assert!(s9[0] > 0.9 && s9[1] > 0.8);
        assert!(s15[0] > 0.9 && s15[1] < 0.35);

        // Unlit ghost segments in dark mode (`max(lit * 0.14, 0.055)`)
        let ghost = vfd_dark_unlit_segment_srgb([0.0, 1.0, 0.0]);
        assert!((ghost[0] - 0.055).abs() < 1e-5);
        assert!((ghost[1] - 0.14).abs() < 1e-5);

        // BT.601 WallpaperLuminanceAnalyzer
        let white_rgb = [255u8, 255, 255, 255, 255, 255];
        let y = theme.luminance_analyzer.analyze_rgb8(&white_rgb);
        assert!((y - 1.0).abs() < 1e-6);
        assert!((theme.luminance_analyzer.target_opacity_floor - 0.87).abs() < 1e-6);
        let adapted = theme.luminance_analyzer.step(0.15);
        assert!((adapted - 0.87).abs() < 1e-6);

        // Popout skin resolution & hover opacity restore
        theme.popout_mode = PopoutMode::TopMost;
        theme.popout_opacity = 0.5;
        theme.active_skin_id = "winamp".into();
        let pop_idle = theme.resolve_popout(false, false);
        assert_eq!(pop_idle.skin_id.as_str(), "winamp");
        assert!(pop_idle.positional_palette);
        assert!((pop_idle.effective_opacity - 0.5).abs() < 1e-5);

        let pop_hover = theme.resolve_popout(true, false);
        assert!((pop_hover.effective_opacity - 1.0).abs() < 1e-5);

        // Text size zoom steps
        assert_eq!(theme.increase_text_size(), 110);
        assert_eq!(theme.increase_text_size(), 125);
        assert_eq!(theme.decrease_text_size(), 110);

        // DisplayMode + VfdTextColor
        theme.display_mode = DisplayMode::Amber;
        theme.vfd_text_color = VfdTextColor::Automatic;
        let lit = theme.active_vfd_lit_appearance();
        assert!(lit.glow_alpha > 0.0);
    }

    #[test]
    fn vfd_ballistics_and_compressed_history_graph_match_curves() {
        let geom = VfdSegmentedMeterState::compute_geometry(100.0, 8.0, 2.0, 12.0);
        assert_eq!(geom.segment_count, 10);
        assert!((geom.glow_inset - 17.6).abs() < 1e-5);

        let mut meter = VfdSegmentedMeterState::new();
        let after_rise = meter.step(1.0, 0.10, false);
        assert!((after_rise - (1.0 - (-1.0_f64).exp())).abs() < 1e-4);

        let snapped = meter.step(0.25, 0.016, true);
        assert!((snapped - 0.25).abs() < 1e-6);

        let r = CompressedHistoryGraph::history_band_ratio(15.0);
        let poly = 1.0 + r + r * r + r * r * r;
        assert!((poly - 15.0).abs() < 1e-6);
        let age_mid = CompressedHistoryGraph::compressed_age_at_distance(0.5, 15.0);
        assert!(age_mid > 0.25 && age_mid < 15.0);

        let x_recent = CompressedHistoryGraph::map_age_to_normalized_x(1.0, 300.0, 15.0);
        let x_old = CompressedHistoryGraph::map_age_to_normalized_x(250.0, 300.0, 15.0);
        assert!(x_recent > x_old);

        let mut graph = CompressedHistoryGraph::new(64);
        graph.push_raw(1.0, Some(10.0));
        graph.push_raw(2.0, Some(20.0));
        graph.push_raw(3.0, Some(20.0));
        graph.push_raw(13.0, Some(50.0));
        graph.push_raw(14.0, Some(60.0));

        let subpaths = graph.build_curve_subpaths();
        assert_eq!(subpaths.len(), 2);
        assert!(!subpaths[0][0].is_straight);
        assert!(subpaths[0][1].is_straight);
    }

    #[test]
    fn page_edit_session_and_sensor_registry_work_end_to_end() {
        let mut snap = SystemSnapshot::new_empty(1, 5_000_000_000);
        let prov = MetricProvenance {
            source: SourceId(1),
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns: 5_000_000_000,
            window_ns: 250_000_000,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        snap.system.gpu_model = CompactString::const_new("Apple M4 Max");
        snap.system.uptime_seconds = Metric::measured(3720.0, prov);

        let discovered = SensorRegistry::discover(&snap);
        assert!(discovered.len() >= 15);
        assert!(matches!(
            SensorPath::new("gpu", "0", "model").resolve_value(&snap),
            Some(SensorValue::Text(_))
        ));
        assert!(matches!(
            SensorPath::new("system", "uptime", "duration").resolve_value(&snap),
            Some(SensorValue::DurationSeconds(_))
        ));

        let page = PageEngine::new_default_summary();
        let start_count = page.tiles.len();
        let mut edit = PageEditSession::new(page);
        let custom_id = edit.add_tile(
            "Thermal Test",
            FaceKind::KpiTextReadout,
            SensorPath::new("thermal", "soc", "max_celsius"),
        );
        assert_eq!(edit.current.tiles.len(), start_count + 1);
        assert!(edit.swap_face(&custom_id, FaceKind::VfdSegmentedMeter));
        assert!(edit.can_undo());
        assert!(edit.undo());
        assert_eq!(
            edit.current.tiles.last().unwrap().face,
            FaceKind::KpiTextReadout
        );
        assert!(edit.redo());
        assert_eq!(
            edit.current.tiles.last().unwrap().face,
            FaceKind::VfdSegmentedMeter
        );
        let committed = edit.commit();
        assert_eq!(committed.tiles.len(), start_count + 1);

        assert_eq!(FaceKind::ALL.len(), 14);
        assert!(
            FaceKind::CompressedHistoryGraph
                .properties()
                .supports_low_priority_sensors
        );
        assert!(FaceKind::PieChart.properties().supports_total_sensors);
        assert!(!FaceKind::FaceGrid.properties().allows_in_face_grid);
        assert!(!FaceKind::ProcessTable.properties().supports_sensors);

        assert_eq!(ColumnDisplayStyle::allowed_for(true).len(), 6);
        assert_eq!(ColumnDisplayStyle::allowed_for(false).len(), 4);

        snap.system.cores.push(nexus_core::CoreTelemetry {
            core_index: 0,
            class: nexus_core::LogicalProcessorClass::Performance,
            total_percent: Metric::measured(42.0, prov),
            user_percent: Metric::measured(30.0, prov),
            system_percent: Metric::measured(12.0, prov),
            idle_percent: Metric::measured(58.0, prov),
        });
        snap.system.cores.push(nexus_core::CoreTelemetry {
            core_index: 1,
            class: nexus_core::LogicalProcessorClass::Efficiency,
            total_percent: Metric::measured(15.0, prov),
            user_percent: Metric::measured(10.0, prov),
            system_percent: Metric::measured(5.0, prov),
            idle_percent: Metric::measured(85.0, prov),
        });
        let group = SensorPath::new("cpu", "[Group:AllCores]", "utilization");
        assert!(group.is_group());
        let expanded = group.expand(&snap);
        assert_eq!(expanded.len(), 2);
        assert_eq!(expanded[0].0.as_path_string(), "cpu/core_0/utilization");
        assert!(matches!(
            SensorPath::new("os", "system", "arch").resolve_value(&snap),
            Some(SensorValue::Text(_))
        ));
    }

    #[test]
    fn process_table_heat_ramp_rank_glow_and_exited_wash_work_end_to_end() {
        let low_heat = heat_ramp_rgba(0.0);
        assert!((low_heat[3] - 0.10).abs() < 1e-5);
        let high_heat = heat_ramp_rgba(1.0);
        assert!((high_heat[0] - 1.0).abs() < 1e-5);
        assert!((high_heat[3] - 0.38).abs() < 1e-5);

        let mut app = TaskManagerApp::new();
        let mut snap = SystemSnapshot::new_empty(1, 10_000_000_000);
        let prov = MetricProvenance {
            source: SourceId(1),
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns: 10_000_000_000,
            window_ns: 250_000_000,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let live_key = ProcessKey {
            pid: 500,
            creation_token_us: 1000,
            uid: Some(501),
        };
        let exited_key = ProcessKey {
            pid: 501,
            creation_token_us: 2000,
            uid: Some(501),
        };
        snap.processes.push(ProcessSnapshot {
            key: live_key,
            ppid: 1,
            name: CompactString::const_new("Obsidian Helper (Renderer)"),
            cpu_percent: Metric::measured(75.0, prov),
            phys_footprint_bytes: Metric::measured(500_000_000.0, prov),
            resident_bytes: Metric::measured(500_000_000.0, prov),
            disk_read_bytes: Metric::measured(0.0, prov),
            disk_write_bytes: Metric::measured(0.0, prov),
            exited_at_ns: None,
        });
        snap.exited_processes.push(ProcessSnapshot {
            key: exited_key,
            ppid: 1,
            name: CompactString::const_new("short_lived_worker"),
            cpu_percent: Metric::measured(0.0, prov),
            phys_footprint_bytes: Metric::measured(0.0, prov),
            resident_bytes: Metric::measured(0.0, prov),
            disk_read_bytes: Metric::measured(0.0, prov),
            disk_write_bytes: Metric::measured(0.0, prov),
            exited_at_ns: Some(8_000_000_000),
        });

        app.process_table
            .collapsed_process_ids
            .insert("process|99999|123|-".to_string());
        app.process_table
            .collapsed_process_ids
            .insert(live_key.to_collapsed_id_string());

        let rows = app.process_table.build_rows(&snap);
        assert_eq!(rows.len(), 2);
        assert_eq!(app.process_table.collapsed_process_ids.len(), 1);
        assert_eq!(rows[0].group_name.as_str(), "Obsidian");
        assert!(rows[0].rank_glow_rgba.is_some());
        assert!(rows[1].exited_wash.is_some());
        assert_eq!(
            rows[1].exited_wash.as_ref().unwrap().status_text.as_str(),
            "Exited"
        );

        let dossier = app
            .process_table
            .inspect_process(&snap, live_key)
            .expect("dossier must resolve");
        assert_eq!(dossier.group_key.as_str(), "Obsidian");
        assert_eq!(dossier.status_label.as_str(), "Running");
    }

    #[test]
    fn standalone_assets_preferences_and_single_instance_ipc_roundtrip() {
        let (m_ttf, l_ttf) = load_standalone_fonts(None);
        assert_eq!(m_ttf.len(), 64_344);
        assert_eq!(l_ttf.len(), 46_436);
        let skins = load_standalone_skins(None).expect("standalone skins must validate");
        assert_eq!(skins.len(), 5);

        let tmp_prefs =
            std::env::temp_dir().join(format!("nexus_prefs_test_{}.json", std::process::id()));
        let mut theme = ThemeEngine::new();
        theme.display_mode = DisplayMode::Amber;
        theme.vfd_text_color = VfdTextColor::Green;
        theme.glass_preset = CrystalGlassPreset::Futuristic;
        theme.active_skin_id = "winamp".into();
        theme.text_size_percent = 125;

        let prefs = UserPreferences::capture_from(&theme, 250, &[]);
        prefs.save_to(&tmp_prefs).expect("save prefs");
        let loaded = UserPreferences::load_from(&tmp_prefs).expect("load prefs");
        assert_eq!(loaded.display_mode, DisplayMode::Amber);
        assert_eq!(loaded.vfd_text_color, VfdTextColor::Green);
        assert_eq!(loaded.glass_preset, CrystalGlassPreset::Futuristic);
        assert_eq!(loaded.active_skin_id.as_str(), "winamp");
        assert_eq!(loaded.update_interval_ms, 250);
        assert_eq!(loaded.text_size_percent, 125);
        let _ = std::fs::remove_file(&tmp_prefs);

        let sock_path =
            std::env::temp_dir().join(format!("nexus_ipc_test_{}.sock", std::process::id()));
        let received = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let rx_clone = std::sync::Arc::clone(&received);
        let outcome1 = acquire_single_instance_or_activate(sock_path.clone(), move |cmd| {
            rx_clone.lock().unwrap().push(cmd);
        })
        .expect("primary bind");
        let guard = match outcome1 {
            InstanceLockOutcome::Primary(g) => g,
            InstanceLockOutcome::ForwardedToExistingPrimary => panic!("expected Primary"),
        };

        let outcome2 =
            acquire_single_instance_or_activate(sock_path.clone(), |_| {}).expect("secondary");
        assert!(matches!(
            outcome2,
            InstanceLockOutcome::ForwardedToExistingPrimary
        ));

        std::thread::sleep(std::time::Duration::from_millis(40));
        assert_eq!(*received.lock().unwrap(), vec![IpcCommand::ActivateWindow]);
        drop(guard);
        assert!(!sock_path.exists());
    }

    #[test]
    fn corrupt_persistence_files_and_dynamic_light_dark_presets_behave_safely() {
        let corrupt_prefs =
            std::env::temp_dir().join(format!("nexus_corrupt_prefs_{}.json", std::process::id()));
        std::fs::write(&corrupt_prefs, "{truncated_json: [1, 2").expect("write corrupt prefs");
        let fallback = UserPreferences::load_or_default(&corrupt_prefs);
        assert_eq!(fallback, UserPreferences::default());
        let _ = std::fs::remove_file(&corrupt_prefs);

        let corrupt_wp = std::env::temp_dir().join(format!(
            "nexus_corrupt_workspace_{}.json",
            std::process::id()
        ));
        std::fs::write(&corrupt_wp, "not json at all").expect("write corrupt workspace");
        assert!(WorkspaceProfile::load_from(&corrupt_wp).is_err());
        let _ = std::fs::remove_file(&corrupt_wp);

        let mut theme = ThemeEngine::new();
        theme.glass_preset = CrystalGlassPreset::NexusDefault;
        theme.custom_preset = None;
        theme.system_is_dark = false;
        let light_meta = theme.active_preset_metadata();
        assert!(!light_meta.is_dark);
        assert_eq!(light_meta.window_bg_hex.as_str(), "#F5F7FA");

        theme.system_is_dark = true;
        let dark_meta = theme.active_preset_metadata();
        assert!(dark_meta.is_dark);
        assert_eq!(dark_meta.window_bg_hex.as_str(), "#090D14");

        let snap = SystemSnapshot::new_empty(1, 1_000_000);
        let discovered = SensorRegistry::discover(&snap);
        let gpu_model = discovered
            .iter()
            .find(|d| d.path.as_path_string() == "gpu/0/model")
            .unwrap();
        assert!(!gpu_model.is_numeric);
        let cpu_util = discovered
            .iter()
            .find(|d| d.path.as_path_string() == "cpu/total/utilization")
            .unwrap();
        assert!(cpu_util.is_numeric);
        assert_eq!(default_app_columns().len(), 7);
    }

    #[test]
    fn snapshot_history_deduplicates_60hz_frames_and_seeds_frame_one_curve() {
        let prov = MetricProvenance {
            source: SourceId(1),
            unit_tag: Some(UnitTag::PERCENT),
            sampled_at_ns: 2_000_000_000,
            window_ns: 250_000_000,
            quality: MetricQuality::Authoritative,
            contributed_by_stale: false,
        };
        let mut snap = SystemSnapshot::new_empty(1, 2_000_000_000);
        snap.system.cpu_percent = Metric::measured(37.5, prov);

        let mut app = TaskManagerApp::new();
        for _ in 0..60 {
            app.apply_snapshot(&snap, 1.0 / 60.0);
        }
        assert_eq!(app.cpu_history.samples.len(), 1);
        assert_eq!(app.last_recorded_ns, Some(2_000_000_000));

        // Seeded frame-1 app should immediately produce >= 1 non-empty curve subpath
        let mut seeded_app = TaskManagerApp::new();
        seeded_app.seed_initial_history(&snap);
        assert_eq!(seeded_app.cpu_history.samples.len(), 2);
        let subpaths = seeded_app.cpu_history.build_curve_subpaths();
        assert!(!subpaths.is_empty());
        assert!(!subpaths[0].is_empty());
    }

    #[test]
    fn phase1_prefs_schema_v2_migration_columns_and_hig_sidebar_sections() {
        use desktop_views::GuiView;

        let proc_cols = default_process_columns();
        assert_eq!(proc_cols[0].id.as_str(), "name");
        assert!(proc_cols.iter().any(|c| c.id == "impact"));
        let tok_col = proc_cols.iter().find(|c| c.id == "token").unwrap();
        assert_eq!(tok_col.style, ColumnDisplayStyle::Hidden);

        // Migrate schema v1 preferences on load
        let legacy_path =
            std::env::temp_dir().join(format!("nexus_legacy_prefs_v1_{}.json", std::process::id()));
        let mut v1_prefs = UserPreferences {
            schema_revision: 1,
            popout_pos_x: Some(120),
            popout_pos_y: Some(84),
            ..UserPreferences::default()
        };
        v1_prefs.process_columns.retain(|c| c.id != "impact");
        if let Some(tok) = v1_prefs
            .process_columns
            .iter_mut()
            .find(|c| c.id == "token")
        {
            tok.style = ColumnDisplayStyle::TextOnly;
        }
        v1_prefs.save_to(&legacy_path).expect("save v1 prefs");

        let migrated = UserPreferences::load_from(&legacy_path).expect("load v1 prefs");
        let _ = std::fs::remove_file(&legacy_path);
        assert_eq!(migrated.schema_revision, 3);
        assert_eq!(migrated.popout_pos_x, Some(120));
        assert_eq!(migrated.popout_pos_y, Some(84));
        assert!(migrated.process_columns.iter().any(|c| c.id == "impact"));
        assert_eq!(
            migrated
                .process_columns
                .iter()
                .find(|c| c.id == "token")
                .unwrap()
                .style,
            ColumnDisplayStyle::Hidden
        );

        assert_eq!(GuiView::Dest(Destination::Summary).title(), "Dashboard");
        assert_eq!(
            GuiView::from_slug("dashboard"),
            GuiView::Dest(Destination::Summary)
        );
        assert_eq!(
            GuiView::from_slug("summary"),
            GuiView::Dest(Destination::Summary)
        );
        let section_views: Vec<GuiView> = GuiView::SIDEBAR_SECTIONS
            .iter()
            .flat_map(|(_, views)| views.iter().copied())
            .collect();
        assert_eq!(section_views.len(), GuiView::ALL_SIDEBAR.len());
        for expected in GuiView::ALL_SIDEBAR {
            assert!(section_views.contains(&expected));
        }
    }

    #[test]
    fn hig_contrast_palette_and_guidance_card_render_in_dark_and_light() {
        use egui::Color32;

        for is_dark in [true, false] {
            let ctx = egui::Context::default();
            ctx.set_visuals(if is_dark {
                egui::Visuals::dark()
            } else {
                egui::Visuals::light()
            });
            let mut out = ctx.run_ui(egui::RawInput::default(), |ui| {
                if is_dark {
                    assert_eq!(hig_green(ui), Color32::from_rgb(48, 209, 88));
                    assert_eq!(hig_blue(ui), Color32::from_rgb(56, 166, 255));
                    assert_eq!(hig_amber(ui), Color32::from_rgb(255, 204, 56));
                    assert_eq!(hig_red(ui), Color32::from_rgb(255, 85, 74));
                    assert_eq!(hig_purple(ui), Color32::from_rgb(203, 114, 255));
                    assert_eq!(hig_gray(ui), Color32::from_rgb(186, 198, 214));
                    assert_eq!(hig_secondary_text(ui), Color32::from_rgb(194, 206, 222));
                } else {
                    assert_eq!(hig_green(ui), Color32::from_rgb(21, 128, 61));
                    assert_eq!(hig_blue(ui), Color32::from_rgb(0, 92, 197));
                    assert_eq!(hig_amber(ui), Color32::from_rgb(161, 98, 7));
                    assert_eq!(hig_red(ui), Color32::from_rgb(185, 28, 28));
                    assert_eq!(hig_purple(ui), Color32::from_rgb(126, 34, 206));
                    assert_eq!(hig_gray(ui), Color32::from_rgb(71, 85, 105));
                    assert_eq!(hig_secondary_text(ui), Color32::from_rgb(51, 65, 85));
                }

                let clicked = HigGuidanceCard {
                    badge_text: "Test Badge",
                    badge_color: hig_blue(ui),
                    headline: "Test Guidance Card",
                    summary: "Summary text for contrast and layout test.",
                    steps: &["Click Test Button.", "Return here."],
                    command_snippet: Some("sudo nexus-monitor"),
                    primary_button: Some((
                        "Open Settings",
                        "x-apple.systempreferences:com.apple.preference.security",
                    )),
                    show_reveal_app_button: true,
                    extra_action_button: Some("Rescan Now"),
                }
                .draw(ui);
                assert!(!clicked);
            });
            out.textures_delta.clear();
        }
    }

    fn collect_text_from_shape(shape: &egui::epaint::Shape, out: &mut Vec<String>) {
        match shape {
            egui::epaint::Shape::Text(text_shape) => {
                out.push(text_shape.galley.job.text.clone());
            }
            egui::epaint::Shape::Vec(shapes) => {
                for s in shapes {
                    collect_text_from_shape(s, out);
                }
            }
            _ => {}
        }
    }

    #[test]
    fn windows_and_linux_ui_views_and_labels_contain_zero_macos_strings() {
        use nexus_core::{
            ClusterClockMetric, CoreTelemetry, DriverKind, DriverScope, HostPlatform,
            LogicalProcessorClass, MediaEngineBlockMetric, StartupItemType, SystemHardwareSummary,
            ThermalSensorReading,
        };

        let forbidden_macos_substrings = [
            "What Is Using My Mac",
            "Apple Silicon",
            "Apple GPU",
            "Neural Engine",
            "AppleSMC",
            "libIOReport",
            "Reveal in Finder",
            "LaunchDaemon",
            "LaunchAgent",
            "Startup Disk",
            "About This Mac",
            "DriverKit",
            ".kext",
        ];

        for platform in [HostPlatform::Windows, HostPlatform::Linux] {
            // 1. Check all platform-aware label helpers and enum titles
            let mut platform_strings = vec![
                platform.what_is_using_my_host_title().to_string(),
                platform.system_drive_title().to_string(),
                platform.reveal_in_file_manager_label().to_string(),
                platform.move_to_trash_label().to_string(),
                platform.trash_noun().to_string(),
                platform.hardware_architecture_heading().to_string(),
                platform.thermal_source_badge().to_string(),
                platform.power_telemetry_badge().to_string(),
                StartupItemType::LaunchAgent
                    .as_str_for(platform)
                    .to_string(),
                StartupItemType::LaunchDaemon
                    .as_str_for(platform)
                    .to_string(),
                DriverKind::KernelExtension.as_str_for(platform).to_string(),
                DriverKind::SystemExtension.as_str_for(platform).to_string(),
                DriverScope::AppleSystem.as_str_for(platform).to_string(),
            ];
            for dest in Destination::ALL {
                platform_strings.push(dest.title_for(platform).to_string());
            }
            let joined_labels = platform_strings.join(" | ");
            for forbidden in forbidden_macos_substrings {
                assert!(
                    !joined_labels.contains(forbidden),
                    "Forbidden macOS string '{forbidden}' found in {platform:?} labels: {joined_labels}"
                );
            }

            // 2. Build a realistic Windows / Linux SystemSnapshot and render the Hardware Architecture & Thermals view
            let mut snap = SystemSnapshot::new_empty(7, 7_000_000_000);
            let gpu_src = if matches!(platform, HostPlatform::Windows) {
                snap.intern_source("windows.nvml.gpu")
            } else {
                snap.intern_source("linux.sysfs.drm")
            };
            let th_src = if matches!(platform, HostPlatform::Windows) {
                snap.intern_source("windows.wmi.thermal")
            } else {
                snap.intern_source("linux.sysfs.hwmon")
            };
            let prov_pct = MetricProvenance {
                source: gpu_src,
                unit_tag: Some(UnitTag::PERCENT),
                sampled_at_ns: 7_000_000_000,
                window_ns: 500_000_000,
                quality: MetricQuality::Authoritative,
                contributed_by_stale: false,
            };
            let prov_bytes = MetricProvenance {
                unit_tag: Some(UnitTag::BYTES),
                ..prov_pct
            };
            let prov_watts = MetricProvenance {
                unit_tag: Some(UnitTag::WATTS),
                ..prov_pct
            };
            let prov_mhz = MetricProvenance {
                unit_tag: Some(UnitTag::MEGAHERTZ),
                ..prov_pct
            };
            let prov_temp = MetricProvenance {
                source: th_src,
                unit_tag: Some(UnitTag::CELSIUS),
                ..prov_pct
            };

            snap.system.gpu_model = CompactString::const_new("NVIDIA GeForce RTX 4070 Ti SUPER");
            snap.system.cpu_percent = Metric::measured(18.4, prov_pct);
            snap.system.gpu_utilization_percent = Metric::measured(42.0, prov_pct);
            snap.system.gpu_in_use_memory_bytes =
                Metric::measured(6.5 * 1024.0 * 1024.0 * 1024.0, prov_bytes);
            snap.system.gpu_alloc_memory_bytes =
                Metric::measured(16.0 * 1024.0 * 1024.0 * 1024.0, prov_bytes);
            snap.system.performance_core_count = 8;
            snap.system.efficiency_core_count = 0;
            for idx in 0..16 {
                snap.system.cores.push(CoreTelemetry {
                    core_index: idx,
                    class: LogicalProcessorClass::Performance,
                    user_percent: Metric::measured(12.0, prov_pct),
                    system_percent: Metric::measured(4.0, prov_pct),
                    idle_percent: Metric::measured(84.0, prov_pct),
                    total_percent: Metric::measured(16.0, prov_pct),
                });
            }
            snap.hardware_telemetry.soc_power.gpu_watts = Metric::measured(148.5, prov_watts);
            snap.hardware_telemetry.soc_power.package_watts = Metric::measured(148.5, prov_watts);
            snap.hardware_telemetry.soc_power.p_cluster_mhz = Metric::measured(4050.0, prov_mhz);
            snap.hardware_telemetry.soc_power.gpu_clock_mhz = Metric::measured(2610.0, prov_mhz);
            snap.hardware_telemetry
                .soc_power
                .cluster_clocks
                .push(ClusterClockMetric {
                    cluster_name: CompactString::const_new("GPU Graphics Clock"),
                    is_performance: true,
                    effective_mhz: Metric::measured(2610.0, prov_mhz),
                    active_residency_percent: Metric::measured(42.0, prov_pct),
                });
            snap.hardware_telemetry
                .soc_power
                .media_blocks
                .push(MediaEngineBlockMetric {
                    id: CompactString::const_new("gpu_3d"),
                    label: CompactString::const_new("GPU 3D / Compute Core"),
                    active_residency_percent: Metric::measured(42.0, prov_pct),
                });
            snap.hardware_telemetry
                .soc_power
                .media_blocks
                .push(MediaEngineBlockMetric {
                    id: CompactString::const_new("gpu_fan"),
                    label: CompactString::const_new("GPU Cooling Fan Speed"),
                    active_residency_percent: Metric::measured(35.0, prov_pct),
                });
            snap.hardware_telemetry.thermals.max_die_celsius = Metric::measured(58.0, prov_temp);
            snap.hardware_telemetry.thermals.warning_level = Metric::measured(0.0, prov_temp);
            snap.hardware_telemetry
                .thermals
                .sensors
                .push(ThermalSensorReading {
                    name: CompactString::const_new("GPU Core (NVML)"),
                    celsius: Metric::measured(58.0, prov_temp),
                });

            let hw = SystemHardwareSummary {
                hostname: CompactString::const_new("RYZEN-5700X3D"),
                os_name: CompactString::const_new(if matches!(platform, HostPlatform::Windows) {
                    "Windows"
                } else {
                    "Linux"
                }),
                os_version: CompactString::const_new("11 Pro 24H2"),
                architecture: CompactString::const_new("x86_64"),
                chip_model: CompactString::const_new("AMD Ryzen 7 5700X3D 8-Core Processor"),
                physical_cores: 8,
                logical_cores: 16,
                p_cores: 8,
                e_cores: 0,
                l2_cache_kb: 4096,
                l3_cache_kb: 98304,
                total_ram_bytes: 32 * 1024 * 1024 * 1024,
                gpu_model: CompactString::const_new("NVIDIA GeForce RTX 4070 Ti SUPER"),
                gpu_cores: None,
                firmware_version: CompactString::const_new("UEFI"),
            };

            let ctx = egui::Context::default();
            let mut full_output = ctx.run_ui(egui::RawInput::default(), |ui| {
                crate::desktop::render_hardware_architecture_view_for_platform(
                    ui,
                    &snap,
                    Some(&hw),
                    platform,
                    egui::Color32::from_rgb(56, 166, 255),
                );
            });
            full_output.textures_delta.clear();

            let mut rendered_texts = Vec::new();
            for clipped in &full_output.shapes {
                collect_text_from_shape(&clipped.shape, &mut rendered_texts);
            }
            let all_rendered = rendered_texts.join("\n");
            assert!(
                all_rendered.contains("Processor, GPU & Thermal Architecture"),
                "Expected cross-platform heading in rendered UI, got:\n{all_rendered}"
            );
            assert!(
                all_rendered.contains("CPU Core & Thread Topology"),
                "Expected CPU Core & Thread Topology card in rendered UI"
            );
            assert!(
                all_rendered.contains("Dedicated & Integrated GPU Telemetry"),
                "Expected Dedicated & Integrated GPU Telemetry card in rendered UI"
            );
            assert!(
                all_rendered.contains("Compute & Media Engines"),
                "Expected Compute & Media Engines card in rendered UI"
            );
            assert!(
                all_rendered.contains("Hardware Thermal Sensors"),
                "Expected Hardware Thermal Sensors card in rendered UI"
            );
            assert!(
                all_rendered.contains("NVIDIA GeForce RTX 4070 Ti SUPER"),
                "Expected GPU model name in rendered UI"
            );

            for forbidden in forbidden_macos_substrings {
                assert!(
                    !all_rendered.contains(forbidden),
                    "Forbidden macOS string '{forbidden}' rendered on {platform:?}:\n{all_rendered}"
                );
            }
        }
    }
}
