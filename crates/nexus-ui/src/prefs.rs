use crate::faces::{
    ColumnDisplayStyle, TableColumnConfig, WorkspaceProfile, default_app_columns,
    default_process_columns,
};
use crate::theme::{CrystalGlassPreset, ThemeEngine, ThemePresetMetadata};
use compact_str::CompactString;
use nexus_automation::ProcessRule;
use nexus_core::{DisplayMode, PopoutMode, VfdTextColor, canonical_popout_skin_id, constants};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};

fn default_quiet_start() -> CompactString {
    CompactString::const_new("22:00")
}

fn default_quiet_end() -> CompactString {
    CompactString::const_new("07:00")
}

fn default_alerts_cpu() -> f64 {
    95.0
}

fn default_alerts_mem() -> f64 {
    90.0
}

fn default_alerts_temp() -> f64 {
    105.0
}

fn default_alerts_disk() -> f64 {
    95.0
}

fn default_true() -> bool {
    true
}

fn default_one_f32() -> f32 {
    1.0
}

/// Persistent user configuration storing active `DisplayMode`, `VfdTextColor`, `CrystalGlassPreset`,
/// custom preset JSON, workspace profile, `.nexusskin` selection, update cadence, text scale,
/// Quiet Hours, Alert thresholds, and `RulesEngine` rules across application restarts.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UserPreferences {
    pub schema_revision: u32,
    pub display_mode: DisplayMode,
    pub vfd_text_color: VfdTextColor,
    pub glass_preset: CrystalGlassPreset,
    pub use_crystal_glass_backdrop: bool,
    pub popout_mode: PopoutMode,
    pub popout_opacity: f32,
    pub popout_opaque_on_hover: bool,
    pub active_skin_id: CompactString,
    pub update_interval_ms: u64,
    pub text_size_percent: u16,
    pub visual_bloom: bool,
    pub visual_bloom_level: u32,
    pub rules: Vec<ProcessRule>,
    #[serde(default)]
    pub custom_preset: Option<ThemePresetMetadata>,
    #[serde(default)]
    pub workspace_profile: Option<WorkspaceProfile>,
    #[serde(default)]
    pub quiet_hours_enabled: bool,
    #[serde(default = "default_quiet_start")]
    pub quiet_hours_start: CompactString,
    #[serde(default = "default_quiet_end")]
    pub quiet_hours_end: CompactString,
    #[serde(default = "default_alerts_cpu")]
    pub alerts_cpu_threshold: f64,
    #[serde(default = "default_alerts_mem")]
    pub alerts_mem_threshold: f64,
    #[serde(default = "default_alerts_temp")]
    pub alerts_temp_threshold: f64,
    #[serde(default = "default_alerts_disk")]
    pub alerts_disk_threshold: f64,
    #[serde(default)]
    pub alerts_webhook_url: Option<CompactString>,
    #[serde(default)]
    pub sleep_prevention_enabled: bool,
    #[serde(default)]
    pub idle_throttle_enabled: bool,
    #[serde(default = "default_true")]
    pub autobalance_enabled: bool,
    #[serde(default = "default_true")]
    pub smooth_graph_curves: bool,
    #[serde(default = "default_true")]
    pub high_frequency_visuals: bool,
    #[serde(default = "default_one_f32")]
    pub display_saturation: f32,
    #[serde(default)]
    pub saved_custom_presets: Vec<ThemePresetMetadata>,
    #[serde(default = "default_process_columns")]
    pub process_columns: Vec<TableColumnConfig>,
    #[serde(default = "default_app_columns")]
    pub app_columns: Vec<TableColumnConfig>,
    #[serde(default)]
    pub popout_pos_x: Option<i32>,
    #[serde(default)]
    pub popout_pos_y: Option<i32>,
}

impl Default for UserPreferences {
    fn default() -> Self {
        Self {
            schema_revision: 3,
            display_mode: DisplayMode::Color,
            vfd_text_color: VfdTextColor::Automatic,
            glass_preset: CrystalGlassPreset::CrystalGlass,
            use_crystal_glass_backdrop: true,
            popout_mode: PopoutMode::Off,
            popout_opacity: 1.0,
            popout_opaque_on_hover: true,
            active_skin_id: CompactString::const_new("com.nexus.console"),
            update_interval_ms: 500,
            text_size_percent: 100,
            visual_bloom: true,
            visual_bloom_level: 7,
            rules: vec![ProcessRule {
                id: CompactString::const_new("watchdog.renderer_cpu"),
                name: CompactString::const_new("Background Helper Throttle"),
                process_pattern: CompactString::const_new("*Helper*"),
                cpu_percent_threshold: Some(3.0),
                target_priority: Some(10),
                ..Default::default()
            }],
            custom_preset: None,
            workspace_profile: None,
            quiet_hours_enabled: false,
            quiet_hours_start: default_quiet_start(),
            quiet_hours_end: default_quiet_end(),
            alerts_cpu_threshold: default_alerts_cpu(),
            alerts_mem_threshold: default_alerts_mem(),
            alerts_temp_threshold: default_alerts_temp(),
            alerts_disk_threshold: default_alerts_disk(),
            alerts_webhook_url: None,
            sleep_prevention_enabled: false,
            idle_throttle_enabled: false,
            autobalance_enabled: true,
            smooth_graph_curves: true,
            high_frequency_visuals: true,
            display_saturation: 1.0,
            saved_custom_presets: Vec::new(),
            process_columns: default_process_columns(),
            app_columns: default_app_columns(),
            popout_pos_x: None,
            popout_pos_y: None,
        }
    }
}

/// Snaps `percent` to the nearest discrete step in `TEXT_SIZE_PERCENTS` (`[100, 110, 125, 150, 175, 200]`).
pub fn clamp_text_size_percent(percent: u16) -> u16 {
    let mut best = 100u16;
    let mut best_diff = u16::MAX;
    for &step in &constants::TEXT_SIZE_PERCENTS {
        let diff = percent.abs_diff(step);
        if diff < best_diff {
            best_diff = diff;
            best = step;
        }
    }
    best
}

/// Returns the default platform application support directory:
/// - macOS: `~/Library/Application Support/NexusSystemMonitor`
/// - Linux: `$XDG_DATA_HOME/NexusSystemMonitor` or `~/.local/share/NexusSystemMonitor`
/// - Windows: `%APPDATA%\NexusSystemMonitor` or `%LOCALAPPDATA%\NexusSystemMonitor`
pub fn app_support_dir() -> PathBuf {
    #[cfg(target_os = "windows")]
    {
        if let Some(appdata) = std::env::var_os("APPDATA").filter(|v| !v.is_empty()) {
            return PathBuf::from(appdata).join("NexusSystemMonitor");
        }
        if let Some(local) = std::env::var_os("LOCALAPPDATA").filter(|v| !v.is_empty()) {
            return PathBuf::from(local).join("NexusSystemMonitor");
        }
        std::env::temp_dir().join("NexusSystemMonitor")
    }
    #[cfg(target_os = "linux")]
    {
        if let Some(xdg) = std::env::var_os("XDG_DATA_HOME").filter(|v| !v.is_empty()) {
            return PathBuf::from(xdg).join("NexusSystemMonitor");
        }
        if let Ok(home) = std::env::var("HOME")
            && !home.is_empty()
        {
            return PathBuf::from(home)
                .join(".local")
                .join("share")
                .join("NexusSystemMonitor");
        }
        std::env::temp_dir().join("NexusSystemMonitor")
    }
    #[cfg(not(any(target_os = "windows", target_os = "linux")))]
    {
        if let Ok(home) = std::env::var("HOME")
            && !home.is_empty()
        {
            return PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join("NexusSystemMonitor");
        }
        std::env::temp_dir().join("NexusSystemMonitor")
    }
}

/// Returns the default telemetry SQLite DB path: `~/Library/Application Support/NexusSystemMonitor/telemetry.db`.
pub fn default_telemetry_db_path() -> PathBuf {
    if let Ok(custom) = std::env::var("NEXUS_TELEMETRY_DB_PATH")
        && !custom.is_empty()
    {
        return PathBuf::from(custom);
    }
    app_support_dir().join("telemetry.db")
}

impl UserPreferences {
    /// Returns the default macOS Application Support path:
    /// `~/Library/Application Support/NexusSystemMonitor/preferences.json`.
    pub fn default_path() -> PathBuf {
        if let Ok(custom) = std::env::var("NEXUS_PREFS_PATH")
            && !custom.is_empty()
        {
            return PathBuf::from(custom);
        }
        app_support_dir().join("preferences.json")
    }

    pub fn load_from(path: &Path) -> std::io::Result<Self> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let raw = fs::read_to_string(path)?;
        let mut prefs: Self = serde_json::from_str(&raw)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        prefs.active_skin_id = CompactString::from(canonical_popout_skin_id(&prefs.active_skin_id));
        prefs.update_interval_ms = prefs.update_interval_ms.clamp(100, 10_000);
        prefs.visual_bloom_level = prefs.visual_bloom_level.clamp(1, 11);
        prefs.text_size_percent = clamp_text_size_percent(prefs.text_size_percent);
        prefs.display_saturation = prefs.display_saturation.clamp(0.0, 1.5);
        if prefs.schema_revision < 3 {
            prefs.schema_revision = 3;
            let defaults = default_process_columns();
            for col in &mut prefs.process_columns {
                if col.id == "token" {
                    col.style = ColumnDisplayStyle::Hidden;
                }
                if let Some(def) = defaults.iter().find(|d| d.id == col.id) {
                    col.label = def.label.clone();
                }
            }
            if !prefs.process_columns.iter().any(|c| c.id == "impact")
                && let Some(impact_col) = defaults.iter().find(|c| c.id == "impact").cloned()
            {
                let insert_pos = prefs
                    .process_columns
                    .iter()
                    .position(|c| c.id == "name")
                    .map_or(0, |idx| idx + 1)
                    .min(prefs.process_columns.len());
                prefs.process_columns.insert(insert_pos, impact_col);
            }
            // Place Application / Process and Impact first if PID was still at index 0 from v1
            if prefs.process_columns.first().is_some_and(|c| c.id == "pid") {
                prefs.process_columns.sort_by_key(|c| {
                    defaults
                        .iter()
                        .position(|d| d.id == c.id)
                        .unwrap_or(usize::MAX)
                });
            }
        }
        for missing_col in default_app_columns() {
            if !prefs.app_columns.iter().any(|c| c.id == missing_col.id) {
                let insert_pos = prefs
                    .app_columns
                    .iter()
                    .position(|c| c.id == "bundle_id")
                    .unwrap_or(prefs.app_columns.len());
                prefs.app_columns.insert(insert_pos, missing_col);
            }
        }
        Ok(prefs)
    }

    pub fn load_or_default(path: &Path) -> Self {
        match Self::load_from(path) {
            Ok(p) => p,
            Err(e) => {
                eprintln!(
                    "nexus: failed to load preferences from {}: {e}",
                    path.display()
                );
                Self::default()
            }
        }
    }

    pub fn save_to(&self, path: &Path) -> std::io::Result<()> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(self).map_err(std::io::Error::other)?;
        let tmp_path = path.with_extension(format!("json.tmp.{}", std::process::id()));
        fs::write(&tmp_path, json)?;
        fs::rename(&tmp_path, path)
    }

    pub fn apply_to_theme(&self, theme: &mut ThemeEngine) {
        theme.display_mode = self.display_mode;
        theme.vfd_text_color = self.vfd_text_color;
        theme.glass_preset = self.glass_preset;
        theme.custom_preset = self.custom_preset.clone();
        theme.use_crystal_glass_backdrop = self.use_crystal_glass_backdrop;
        theme.popout_mode = self.popout_mode;
        theme.popout_opacity = self.popout_opacity.clamp(0.2, 1.0);
        theme.popout_opaque_on_hover = self.popout_opaque_on_hover;
        theme.active_skin_id = CompactString::from(canonical_popout_skin_id(&self.active_skin_id));
        theme.text_size_percent = clamp_text_size_percent(self.text_size_percent);
        theme.visual_bloom = self.visual_bloom;
        theme.visual_bloom_level = self.visual_bloom_level.clamp(1, 11);
        theme.display_saturation = self.display_saturation.clamp(0.0, 1.5);
        theme.smooth_graph_curves = self.smooth_graph_curves;
        theme.high_frequency_visuals = self.high_frequency_visuals;
    }

    pub fn capture_from(
        theme: &ThemeEngine,
        update_interval_ms: u64,
        rules: &[ProcessRule],
    ) -> Self {
        Self {
            schema_revision: 3,
            display_mode: theme.display_mode,
            vfd_text_color: theme.vfd_text_color,
            glass_preset: theme.glass_preset,
            use_crystal_glass_backdrop: theme.use_crystal_glass_backdrop,
            popout_mode: theme.popout_mode,
            popout_opacity: theme.popout_opacity,
            popout_opaque_on_hover: theme.popout_opaque_on_hover,
            active_skin_id: theme.active_skin_id.clone(),
            update_interval_ms: update_interval_ms.clamp(100, 10_000),
            text_size_percent: clamp_text_size_percent(theme.text_size_percent),
            visual_bloom: theme.visual_bloom,
            visual_bloom_level: theme.visual_bloom_level.clamp(1, 11),
            rules: rules.to_vec(),
            custom_preset: theme.custom_preset.clone(),
            smooth_graph_curves: theme.smooth_graph_curves,
            high_frequency_visuals: theme.high_frequency_visuals,
            display_saturation: theme.display_saturation.clamp(0.0, 1.5),
            ..Self::default()
        }
    }
}
