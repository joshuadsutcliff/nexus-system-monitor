use crate::faces::{
    ColumnDisplayStyle, CompressedHistoryGraph, TableColumnConfig, VfdSegmentedMeterState,
    default_app_columns, default_process_columns,
};
use crate::prefs::default_telemetry_db_path;
use crate::theme::{
    CrystalGlassPreset, ThemeEngine, ThemePresetMetadata, VfdLitAppearance, hsv_to_rgb,
    parse_hex_rgba, rgb_to_hex, rgb_to_hsv, vfd_dark_unlit_segment_srgb, vfd_positional_lit_srgb,
    vfd_positional_unlit_srgb,
};
use crate::views::Destination;
use compact_str::CompactString;
use egui::{
    Color32, CornerRadius, FontFamily, FontId, Pos2, Rect, RichText, Stroke, StrokeKind, Vec2,
};
use nexus_automation::{
    AlertNotification, AutoBalanceService, IdleThrottleService, PerformanceProfileService,
    QuietHoursService, RuleTriggerEvent,
};
use nexus_collectors_macos::system_ops::{
    ServiceAction, StorageInspectorEntry, collect_drivers, collect_installed_apps,
    collect_mounted_volumes, collect_system_hardware, collect_users, control_service,
    detect_local_subnet, driver_unload_refusal, list_network_connections, list_services,
    list_startup_items, measure_path_size_bytes, resolve_related_files, scan_lan_radar, scan_wifi,
    set_service_start_type, set_startup_item_enabled, uninstall_app_to_trash,
};
use nexus_core::{
    BONJOUR_SERVICE_TYPE, BottleneckSeverity, DEFAULT_REMOTE_PORT, DriverEntry, DriverKind,
    DriverScope, DriverState, HealthLevel, HealthScoring, HostPlatform, InstalledAppEntry,
    LanRadarReport, Metric, MetricReading, NetworkConnectionEntry, ProcessKey, RelatedFileEntry,
    RemoteServerConfig, RemoteServerHandle, ServiceEntry, ServiceStartType, ServiceState,
    StartupItem, StartupItemType, SystemHardwareSummary, SystemHealthTracker, SystemSnapshot,
    TcpState, TrendDirection, UserSessionEntry, VolumeSpaceSummary, WifiBand, WirelessScanReport,
    constants,
};
use nexus_disk::{
    DiskScanReport, DiskSnapshotDiff, DiskSnapshotInfo, DiskSnapshotStore, DuplicateGroup,
    MetricsHistoryStore, RectBounds, TreemapRect, compute_squarified_treemap, find_duplicates,
};
use std::collections::{HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GuiView {
    Dest(Destination),
    WirelessRadar,
    Honesty,
}

impl GuiView {
    pub const ALL_SIDEBAR: [Self; 18] = [
        Self::Dest(Destination::Summary),
        Self::Dest(Destination::Performance),
        Self::Dest(Destination::Processes),
        Self::Dest(Destination::Services),
        Self::Dest(Destination::Startup),
        Self::Dest(Destination::Connections),
        Self::WirelessRadar,
        Self::Dest(Destination::DiskSpace),
        Self::Dest(Destination::AppleSilicon),
        Self::Dest(Destination::PowerAndFreq),
        Self::Dest(Destination::SystemInfo),
        Self::Dest(Destination::Users),
        Self::Dest(Destination::InstalledApps),
        Self::Dest(Destination::Drivers),
        Self::Dest(Destination::FlightRecorder),
        Self::Dest(Destination::Benchmarks),
        Self::Dest(Destination::Settings),
        Self::Honesty,
    ];

    pub const SIDEBAR_SECTIONS: [(&'static str, &'static [Self]); 4] = [
        (
            "OVERVIEW",
            &[
                Self::Dest(Destination::Summary),
                Self::Dest(Destination::Performance),
                Self::Dest(Destination::SystemInfo),
            ],
        ),
        (
            "APPS & PROCESSES",
            &[
                Self::Dest(Destination::Processes),
                Self::Dest(Destination::InstalledApps),
                Self::Dest(Destination::Services),
                Self::Dest(Destination::Startup),
                Self::Dest(Destination::Users),
            ],
        ),
        (
            "STORAGE & NETWORK",
            &[
                Self::Dest(Destination::DiskSpace),
                Self::Dest(Destination::Connections),
                Self::WirelessRadar,
            ],
        ),
        (
            "HARDWARE & PRO TOOLS",
            &[
                Self::Dest(Destination::AppleSilicon),
                Self::Dest(Destination::PowerAndFreq),
                Self::Dest(Destination::Drivers),
                Self::Dest(Destination::FlightRecorder),
                Self::Dest(Destination::Benchmarks),
                Self::Honesty,
                Self::Dest(Destination::Settings),
            ],
        ),
    ];

    pub const fn title(self) -> &'static str {
        match self {
            Self::Dest(Destination::Summary) => "Dashboard",
            Self::Dest(d) => d.title(),
            Self::WirelessRadar => "Wireless & LAN Radar",
            Self::Honesty => "Honesty & Provenance",
        }
    }

    pub const fn slug(self) -> &'static str {
        match self {
            Self::Dest(d) => d.slug(),
            Self::WirelessRadar => "wireless",
            Self::Honesty => "honesty",
        }
    }

    pub fn from_slug(s: &str) -> Self {
        match s {
            "dashboard" | "summary" => Self::Dest(Destination::Summary),
            "performance" => Self::Dest(Destination::Performance),
            "processes" => Self::Dest(Destination::Processes),
            "system-info" => Self::Dest(Destination::SystemInfo),
            "startup" => Self::Dest(Destination::Startup),
            "users" => Self::Dest(Destination::Users),
            "services" => Self::Dest(Destination::Services),
            "power-and-freq" => Self::Dest(Destination::PowerAndFreq),
            "apple-silicon" => Self::Dest(Destination::AppleSilicon),
            "flight-recorder" => Self::Dest(Destination::FlightRecorder),
            "connections" => Self::Dest(Destination::Connections),
            "installed-apps" => Self::Dest(Destination::InstalledApps),
            "drivers" => Self::Dest(Destination::Drivers),
            "disk-space" => Self::Dest(Destination::DiskSpace),
            "benchmarks" => Self::Dest(Destination::Benchmarks),
            "settings" => Self::Dest(Destination::Settings),
            "wireless" | "radar" => Self::WirelessRadar,
            "honesty" => Self::Honesty,
            _ => Self::Dest(Destination::Summary),
        }
    }
}

/// Apple HIG inset grouped card container (`NSBox` / SwiftUI `GroupBox`).
pub fn hig_card<R>(
    ui: &mut egui::Ui,
    add_contents: impl FnOnce(&mut egui::Ui) -> R,
) -> egui::InnerResponse<R> {
    let fill = ui.visuals().extreme_bg_color;
    let existing = ui.visuals().widgets.noninteractive.bg_stroke;
    let hairline_color = if existing.width > 0.0 && existing.color != Color32::TRANSPARENT {
        existing.color
    } else if ui.visuals().dark_mode {
        Color32::from_rgba_unmultiplied(255, 255, 255, 24)
    } else {
        Color32::from_rgba_unmultiplied(15, 23, 42, 22)
    };
    egui::Frame::new()
        .fill(fill)
        .stroke(Stroke::new(1.0, hairline_color))
        .corner_radius(CornerRadius::same(10))
        .inner_margin(egui::Margin::symmetric(16, 14))
        .show(ui, add_contents)
}

/// Apple HIG card title + plain-English 1-line subtitle.
pub fn hig_card_header(ui: &mut egui::Ui, title: &str, subtitle: &str, _accent: Color32) {
    ui.label(
        RichText::new(title)
            .font(FontId::proportional(15.5))
            .strong()
            .color(ui.visuals().strong_text_color()),
    );
    if !subtitle.is_empty() {
        ui.label(
            RichText::new(subtitle)
                .font(FontId::proportional(13.0))
                .color(ui.visuals().weak_text_color()),
        );
    }
    ui.add_space(6.0);
}

/// Apple HIG view title + calm 1-line plain-English explanation.
pub fn hig_view_header(ui: &mut egui::Ui, title: &str, subtitle: &str, _accent: Color32) {
    ui.label(
        RichText::new(title)
            .font(FontId::proportional(21.0))
            .strong()
            .color(ui.visuals().strong_text_color()),
    );
    if !subtitle.is_empty() {
        ui.label(
            RichText::new(subtitle)
                .font(FontId::proportional(13.0))
                .color(ui.visuals().weak_text_color()),
        );
    }
    ui.add_space(8.0);
}

/// Apple HIG rounded semantic status pill badge.
pub fn hig_status_pill(ui: &mut egui::Ui, text: &str, color: Color32) {
    let fill_alpha = if ui.visuals().dark_mode { 44 } else { 30 };
    let fill = Color32::from_rgba_unmultiplied(color.r(), color.g(), color.b(), fill_alpha);
    let border = Color32::from_rgba_unmultiplied(color.r(), color.g(), color.b(), 135);
    egui::Frame::new()
        .fill(fill)
        .stroke(Stroke::new(1.0, border))
        .corner_radius(CornerRadius::same(6))
        .inner_margin(egui::Margin::symmetric(8, 3))
        .show(ui, |ui| {
            ui.label(
                RichText::new(text)
                    .font(FontId::proportional(12.0))
                    .strong()
                    .color(color),
            );
        });
}

pub fn hig_green(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(48, 209, 88)
    } else {
        Color32::from_rgb(21, 128, 61)
    }
}

pub fn hig_blue(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(56, 166, 255)
    } else {
        Color32::from_rgb(0, 92, 197)
    }
}

pub fn hig_amber(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(255, 204, 56)
    } else {
        Color32::from_rgb(161, 98, 7)
    }
}

pub fn hig_red(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(255, 85, 74)
    } else {
        Color32::from_rgb(185, 28, 28)
    }
}

pub fn hig_purple(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(203, 114, 255)
    } else {
        Color32::from_rgb(126, 34, 206)
    }
}

pub fn hig_gray(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(186, 198, 214)
    } else {
        Color32::from_rgb(71, 85, 105)
    }
}

pub fn hig_secondary_text(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(194, 206, 222)
    } else {
        Color32::from_rgb(51, 65, 85)
    }
}

struct HigKpiItem {
    label: String,
    value: String,
    detail: String,
    color: Color32,
}

fn draw_hig_kpi_strip(ui: &mut egui::Ui, items: &[HigKpiItem]) {
    if items.is_empty() {
        return;
    }
    ui.columns(items.len(), |cols| {
        for (idx, item) in items.iter().enumerate() {
            hig_card(&mut cols[idx], |ui| {
                ui.set_min_width(ui.available_width());
                ui.label(
                    RichText::new(&item.label)
                        .font(FontId::proportional(12.5))
                        .strong()
                        .color(ui.visuals().weak_text_color()),
                );
                ui.label(
                    RichText::new(&item.value)
                        .font(FontId::proportional(21.0))
                        .strong()
                        .color(item.color),
                );
                if !item.detail.is_empty() {
                    ui.label(
                        RichText::new(&item.detail)
                            .font(FontId::proportional(12.0))
                            .color(ui.visuals().weak_text_color()),
                    );
                }
            });
        }
    });
    ui.add_space(10.0);
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

/// Opens a system settings or privacy pane deep link on macOS, Linux, or Windows.
pub fn open_macos_privacy_pane(pane_url: &str) {
    #[cfg(target_os = "macos")]
    {
        let _ = std::process::Command::new("/usr/bin/open")
            .arg(pane_url)
            .spawn();
    }
    #[cfg(target_os = "linux")]
    {
        let _ = std::process::Command::new("xdg-open").arg(pane_url).spawn();
    }
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        let _ = std::process::Command::new("cmd")
            .args(["/C", "start", "", pane_url])
            .creation_flags(0x0800_0000)
            .spawn();
    }
}

/// Reveals the running application bundle or binary in Finder / File Explorer / file manager.
pub fn reveal_app_in_finder() {
    if let Ok(exe) = std::env::current_exe() {
        #[cfg(target_os = "macos")]
        {
            let target = exe
                .ancestors()
                .find(|p| p.extension().and_then(|e| e.to_str()) == Some("app"))
                .unwrap_or(exe.as_path());
            let _ = std::process::Command::new("/usr/bin/open")
                .args(["-R", &target.to_string_lossy()])
                .spawn();
        }
        #[cfg(target_os = "linux")]
        {
            if let Some(parent) = exe.parent() {
                let _ = std::process::Command::new("xdg-open").arg(parent).spawn();
            }
        }
        #[cfg(target_os = "windows")]
        {
            use std::os::windows::process::CommandExt;
            let _ = std::process::Command::new("explorer")
                .arg(format!("/select,{}", exe.display()))
                .creation_flags(0x0800_0000)
                .spawn();
        }
    }
}

/// Renders an inset monospace terminal command snippet with a 1-click `"Copy"` button and 2-second `"Copied!"` feedback.
pub fn draw_hig_command_box(ui: &mut egui::Ui, command: &str, id_salt: &str) {
    let copy_id = ui.make_persistent_id(("nexus_hig_cmd_copy", id_salt, command));
    let now = ui.input(|i| i.time);
    let copied_recently = ui
        .ctx()
        .data(|d| d.get_temp::<f64>(copy_id))
        .is_some_and(|t| (now - t) >= 0.0 && (now - t) < 2.0);
    if copied_recently {
        ui.ctx().request_repaint_after(Duration::from_millis(250));
    }

    let dark = ui.visuals().dark_mode;
    let box_bg = if dark {
        Color32::from_rgb(9, 13, 20)
    } else {
        Color32::from_rgb(238, 242, 248)
    };
    let box_border = if dark {
        Color32::from_rgba_unmultiplied(88, 166, 255, 96)
    } else {
        Color32::from_rgba_unmultiplied(0, 102, 214, 96)
    };
    let cmd_color = if dark {
        Color32::from_rgb(147, 197, 253)
    } else {
        Color32::from_rgb(15, 23, 42)
    };

    egui::Frame::new()
        .fill(box_bg)
        .stroke(Stroke::new(1.0, box_border))
        .corner_radius(CornerRadius::same(7))
        .inner_margin(egui::Margin::symmetric(10, 6))
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                ui.label(
                    RichText::new("$")
                        .font(FontId::monospace(12.5))
                        .strong()
                        .color(hig_blue(ui)),
                );
                ui.label(
                    RichText::new(command)
                        .font(FontId::monospace(12.5))
                        .strong()
                        .color(cmd_color),
                );
                ui.add_space(6.0);
                let (btn_txt, btn_col) = if copied_recently {
                    ("Copied!", hig_green(ui))
                } else {
                    ("Copy", ui.visuals().strong_text_color())
                };
                let btn = egui::Button::new(
                    RichText::new(btn_txt)
                        .font(FontId::proportional(12.0))
                        .strong()
                        .color(btn_col),
                )
                .min_size(Vec2::new(64.0, 22.0));
                if ui
                    .add(btn)
                    .on_hover_text(format!("Copy {command} to clipboard"))
                    .clicked()
                {
                    ui.ctx().copy_text(command.to_string());
                    ui.ctx().data_mut(|d| d.insert_temp(copy_id, now));
                }
            });
        });
}

fn draw_numbered_step_row(ui: &mut egui::Ui, step_num: usize, step_text: &str) {
    ui.horizontal_top(|ui| {
        ui.label(
            RichText::new(format!("{step_num}."))
                .font(FontId::proportional(13.0))
                .strong()
                .color(hig_blue(ui)),
        );
        let mut job = egui::text::LayoutJob::default();
        let strong_fmt = egui::TextFormat {
            font_id: FontId::proportional(13.0),
            color: ui.visuals().strong_text_color(),
            ..Default::default()
        };
        let body_fmt = egui::TextFormat {
            font_id: FontId::proportional(13.0),
            color: ui.visuals().text_color(),
            ..Default::default()
        };
        if let Some((first_word, rest)) = step_text.split_once(' ') {
            job.append(first_word, 0.0, strong_fmt);
            job.append(&format!(" {rest}"), 0.0, body_fmt);
        } else {
            job.append(step_text, 0.0, body_fmt);
        }
        ui.add(egui::Label::new(job).wrap());
    });
}

pub struct HigGuidanceCard<'a> {
    pub badge_text: &'a str,
    pub badge_color: Color32,
    pub headline: &'a str,
    pub summary: &'a str,
    pub steps: &'a [&'a str],
    pub command_snippet: Option<&'a str>,
    pub primary_button: Option<(&'a str, &'a str)>,
    pub show_reveal_app_button: bool,
    pub extra_action_button: Option<&'a str>,
}

impl<'a> HigGuidanceCard<'a> {
    pub fn draw(&self, ui: &mut egui::Ui) -> bool {
        let mut extra_clicked = false;
        hig_card(ui, |ui| {
            ui.set_min_width(ui.available_width());
            ui.horizontal_wrapped(|ui| {
                hig_status_pill(ui, self.badge_text, self.badge_color);
                ui.label(
                    RichText::new(self.headline)
                        .font(FontId::proportional(15.0))
                        .strong()
                        .color(ui.visuals().strong_text_color()),
                );
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if let Some(extra_lbl) = self.extra_action_button
                        && ui
                            .button(
                                RichText::new(extra_lbl)
                                    .font(FontId::proportional(12.5))
                                    .strong(),
                            )
                            .clicked()
                    {
                        extra_clicked = true;
                    }
                    if self.show_reveal_app_button
                        && ui
                            .button(
                                RichText::new(
                                    HostPlatform::current().reveal_in_file_manager_label(),
                                )
                                .font(FontId::proportional(12.5))
                                .strong(),
                            )
                            .on_hover_text("Show Nexus System Monitor in your file manager")
                            .clicked()
                    {
                        reveal_app_in_finder();
                    }
                    if let Some((btn_label, pane_url)) = self.primary_button
                        && ui
                            .button(
                                RichText::new(btn_label)
                                    .font(FontId::proportional(12.5))
                                    .strong(),
                            )
                            .on_hover_text(format!("Open macOS System Settings ({pane_url})"))
                            .clicked()
                    {
                        open_macos_privacy_pane(pane_url);
                    }
                });
            });
            ui.add_space(6.0);
            ui.label(
                RichText::new(self.summary)
                    .font(FontId::proportional(13.0))
                    .color(ui.visuals().text_color()),
            );
            if !self.steps.is_empty() {
                ui.add_space(6.0);
                for (idx, step) in self.steps.iter().enumerate() {
                    if idx > 0 {
                        ui.add_space(2.0);
                    }
                    draw_numbered_step_row(ui, idx + 1, step);
                }
            }
            if let Some(cmd) = self.command_snippet {
                ui.add_space(6.0);
                draw_hig_command_box(ui, cmd, self.headline);
            }
        });
        extra_clicked
    }
}

/// Renders a guided Apple HIG permission & scope notice card with 1-click System Settings action buttons,
/// numbered step instructions, and an optional interactive copyable terminal command box.
#[allow(clippy::too_many_arguments)]
pub fn draw_permission_guidance_card(
    ui: &mut egui::Ui,
    badge_text: &str,
    badge_color: Color32,
    headline: &str,
    summary: &str,
    steps: &[&str],
    command_snippet: Option<&str>,
    primary_button: Option<(&str, &str)>,
    show_reveal_app_button: bool,
    extra_action_button: Option<&str>,
) -> bool {
    HigGuidanceCard {
        badge_text,
        badge_color,
        headline,
        summary,
        steps,
        command_snippet,
        primary_button,
        show_reveal_app_button,
        extra_action_button,
    }
    .draw(ui)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ColorWheelTarget {
    Accent,
    TextAccent,
    WindowBg,
    SurfaceBg,
    SidebarBg,
}

impl ColorWheelTarget {
    pub const ALL: [Self; 5] = [
        Self::Accent,
        Self::TextAccent,
        Self::WindowBg,
        Self::SurfaceBg,
        Self::SidebarBg,
    ];

    pub const fn label(self) -> &'static str {
        match self {
            Self::Accent => "Accent",
            Self::TextAccent => "Text Accent",
            Self::WindowBg => "Window Bg",
            Self::SurfaceBg => "Surface Bg",
            Self::SidebarBg => "Sidebar Bg",
        }
    }

    pub fn get_rgb(self, meta: &ThemePresetMetadata) -> [f32; 3] {
        match self {
            Self::Accent => meta.accent_srgb,
            Self::TextAccent => meta.text_accent_srgb,
            Self::WindowBg => meta.window_bg_srgb,
            Self::SurfaceBg => meta.surface_bg_srgb,
            Self::SidebarBg => meta.sidebar_bg_srgb,
        }
    }

    pub fn set_rgb(self, meta: &mut ThemePresetMetadata, rgb: [f32; 3]) {
        let hex = CompactString::from(rgb_to_hex(rgb));
        match self {
            Self::Accent => {
                meta.accent_srgb = rgb;
                meta.accent_hex = hex;
            }
            Self::TextAccent => {
                meta.text_accent_srgb = rgb;
                meta.text_accent_hex = hex;
            }
            Self::WindowBg => {
                meta.window_bg_srgb = rgb;
                meta.window_bg_hex = hex;
            }
            Self::SurfaceBg => {
                meta.surface_bg_srgb = rgb;
                meta.surface_bg_hex = hex;
            }
            Self::SidebarBg => {
                meta.sidebar_bg_srgb = rgb;
                meta.sidebar_bg_hex = hex;
            }
        }
    }
}

pub struct ColorWheelState {
    pub target: ColorWheelTarget,
    pub h: f32,
    pub s: f32,
    pub v: f32,
    pub hex_buf: String,
    pub preset_name_buf: String,
    wheel_tex: Option<egui::TextureHandle>,
    last_v: f32,
}

impl Default for ColorWheelState {
    fn default() -> Self {
        Self::new()
    }
}

impl ColorWheelState {
    pub fn new() -> Self {
        let meta = CrystalGlassPreset::CrystalGlass.metadata();
        let (h, s, v) = rgb_to_hsv(
            meta.accent_srgb[0],
            meta.accent_srgb[1],
            meta.accent_srgb[2],
        );
        Self {
            target: ColorWheelTarget::Accent,
            h,
            s,
            v,
            hex_buf: rgb_to_hex(meta.accent_srgb),
            preset_name_buf: "My Custom Theme".to_string(),
            wheel_tex: None,
            last_v: -1.0,
        }
    }

    pub fn sync_from_meta(&mut self, meta: &ThemePresetMetadata) {
        let rgb = self.target.get_rgb(meta);
        let (h, s, v) = rgb_to_hsv(rgb[0], rgb[1], rgb[2]);
        self.h = h;
        self.s = s;
        self.v = v;
        self.hex_buf = rgb_to_hex(rgb);
    }

    fn ensure_wheel_texture(&mut self, ctx: &egui::Context) -> &egui::TextureHandle {
        if self.wheel_tex.is_none() || (self.v - self.last_v).abs() > 0.02 {
            const DIM: usize = 144;
            let radius = (DIM as f32) * 0.5;
            let mut pixels = vec![Color32::TRANSPARENT; DIM * DIM];
            for y in 0..DIM {
                for x in 0..DIM {
                    let dx = (x as f32 + 0.5) - radius;
                    let dy = (y as f32 + 0.5) - radius;
                    let dist = (dx * dx + dy * dy).sqrt();
                    if dist <= radius {
                        let angle = dy.atan2(dx).to_degrees().rem_euclid(360.0);
                        let sat = (dist / radius).clamp(0.0, 1.0);
                        let rgb = hsv_to_rgb(angle, sat, self.v.clamp(0.05, 1.0));
                        let edge_aa = ((radius - dist).clamp(0.0, 1.2) / 1.2 * 255.0) as u8;
                        pixels[y * DIM + x] = Color32::from_rgba_unmultiplied(
                            (rgb[0] * 255.0) as u8,
                            (rgb[1] * 255.0) as u8,
                            (rgb[2] * 255.0) as u8,
                            edge_aa,
                        );
                    }
                }
            }
            let image = egui::ColorImage::new([DIM, DIM], pixels);
            let tex = ctx.load_texture("nexus_hsv_wheel", image, egui::TextureOptions::LINEAR);
            self.last_v = self.v;
            return self.wheel_tex.insert(tex);
        }
        if let Some(ref tex) = self.wheel_tex {
            return tex;
        }
        let image = egui::ColorImage::new([1, 1], vec![Color32::BLACK]);
        let tex = ctx.load_texture("nexus_hsv_wheel", image, egui::TextureOptions::LINEAR);
        self.wheel_tex.insert(tex)
    }
}

#[allow(clippy::too_many_arguments)]
pub fn draw_hsv_color_wheel(
    ui: &mut egui::Ui,
    theme: &mut ThemeEngine,
    state: &mut ColorWheelState,
    saved_custom_presets: &mut Vec<ThemePresetMetadata>,
    preset_json_buf: &mut String,
    status_out: &mut String,
    accent: Color32,
) -> bool {
    let mut dirty = false;
    hig_card_header(
        ui,
        "Interactive Color Wheel & Custom Preset Studio",
        "Customize accent, text, window, surface, and sidebar colors or save your own theme.",
        accent,
    );

    ui.horizontal_wrapped(|ui| {
        ui.label("Target:");
        for tgt in ColorWheelTarget::ALL {
            if ui
                .selectable_label(state.target == tgt, tgt.label())
                .clicked()
            {
                state.target = tgt;
                let meta = theme.active_preset_metadata();
                state.sync_from_meta(&meta);
            }
        }
    });

    ui.horizontal(|ui| {
        let tex_id = state.ensure_wheel_texture(ui.ctx()).id();
        let wheel_size = Vec2::splat(144.0);
        let (wheel_rect, wheel_resp) =
            ui.allocate_exact_size(wheel_size, egui::Sense::click_and_drag());
        if (wheel_resp.dragged() || wheel_resp.clicked())
            && let Some(pos) = wheel_resp.interact_pointer_pos()
        {
            let center = wheel_rect.center();
            let radius = wheel_rect.width() * 0.5;
            let dx = pos.x - center.x;
            let dy = pos.y - center.y;
            let dist = (dx * dx + dy * dy).sqrt().min(radius);
            state.h = dy.atan2(dx).to_degrees().rem_euclid(360.0);
            state.s = (dist / radius).clamp(0.0, 1.0);
            state.hex_buf = rgb_to_hex(hsv_to_rgb(state.h, state.s, state.v));
        }

        let painter = ui.painter_at(wheel_rect);
        painter.image(
            tex_id,
            wheel_rect,
            Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
            Color32::WHITE,
        );
        let rad = state.h.to_radians();
        let r_px = state.s * (wheel_rect.width() * 0.5);
        let cursor_pos = Pos2::new(
            wheel_rect.center().x + rad.cos() * r_px,
            wheel_rect.center().y + rad.sin() * r_px,
        );
        painter.circle_stroke(cursor_pos, 5.5, Stroke::new(2.0, Color32::WHITE));
        painter.circle_stroke(cursor_pos, 3.5, Stroke::new(1.5, Color32::BLACK));

        // Vertical Brightness (Value) strip
        let (val_rect, val_resp) =
            ui.allocate_exact_size(Vec2::new(20.0, 144.0), egui::Sense::click_and_drag());
        if (val_resp.dragged() || val_resp.clicked())
            && let Some(pos) = val_resp.interact_pointer_pos()
        {
            state.v = (1.0 - ((pos.y - val_rect.min.y) / val_rect.height())).clamp(0.0, 1.0);
            state.hex_buf = rgb_to_hex(hsv_to_rgb(state.h, state.s, state.v));
        }
        let top_rgb = hsv_to_rgb(state.h, state.s, 1.0);
        let top_c = srgb_to_color32(top_rgb);
        let mut mesh = egui::Mesh::default();
        mesh.colored_vertex(val_rect.left_top(), top_c);
        mesh.colored_vertex(val_rect.right_top(), top_c);
        mesh.colored_vertex(val_rect.left_bottom(), Color32::BLACK);
        mesh.colored_vertex(val_rect.right_bottom(), Color32::BLACK);
        mesh.add_triangle(0, 1, 2);
        mesh.add_triangle(1, 3, 2);
        ui.painter().add(egui::Shape::mesh(mesh));
        ui.painter().rect_stroke(
            val_rect,
            CornerRadius::same(2),
            Stroke::new(1.0, Color32::from_gray(110)),
            StrokeKind::Inside,
        );
        let vy = val_rect.min.y + (1.0 - state.v) * val_rect.height();
        ui.painter().line_segment(
            [
                Pos2::new(val_rect.min.x - 2.0, vy),
                Pos2::new(val_rect.max.x + 2.0, vy),
            ],
            Stroke::new(2.0, Color32::WHITE),
        );

        ui.vertical(|ui| {
            let cur_rgb = hsv_to_rgb(state.h, state.s, state.v);
            let (sw_rect, _) = ui.allocate_exact_size(Vec2::new(48.0, 24.0), egui::Sense::hover());
            ui.painter()
                .rect_filled(sw_rect, CornerRadius::same(4), srgb_to_color32(cur_rgb));
            ui.painter().rect_stroke(
                sw_rect,
                CornerRadius::same(4),
                Stroke::new(1.0, Color32::WHITE),
                StrokeKind::Inside,
            );
            ui.label(format!(
                "H:{:.0} S:{:.0}% V:{:.0}%",
                state.h,
                state.s * 100.0,
                state.v * 100.0
            ));
            ui.horizontal(|ui| {
                ui.label("Hex:");
                if ui
                    .add(egui::TextEdit::singleline(&mut state.hex_buf).desired_width(76.0))
                    .changed()
                    && let Some(rgba) = parse_hex_rgba(&state.hex_buf)
                {
                    let (h, s, v) = rgb_to_hsv(rgba[0], rgba[1], rgba[2]);
                    state.h = h;
                    state.s = s;
                    state.v = v;
                }
            });
            if ui.button("Apply Color to Active Theme").clicked() {
                let mut meta = theme.active_preset_metadata();
                state.target.set_rgb(&mut meta, cur_rgb);
                if meta.id == theme.glass_preset.metadata().id {
                    meta.id = CompactString::from(format!("{}-custom", meta.id));
                    meta.name = CompactString::from(format!("{} (Custom)", meta.name));
                }
                theme.custom_preset = Some(meta);
                if let Ok(j) = theme.export_active_preset_json() {
                    *preset_json_buf = j;
                }
                *status_out = format!(
                    "Applied {} color {} to active theme.",
                    state.target.label(),
                    state.hex_buf
                );
                dirty = true;
            }
            if ui.button("Reset to Built-In Preset").clicked() {
                theme.custom_preset = None;
                let meta = theme.active_preset_metadata();
                state.sync_from_meta(&meta);
                if let Ok(j) = theme.export_active_preset_json() {
                    *preset_json_buf = j;
                }
                *status_out = format!("Reset to built-in preset '{}'.", meta.name);
                dirty = true;
            }
        });
    });

    ui.horizontal(|ui| {
        ui.label("Preset Name:");
        ui.add(egui::TextEdit::singleline(&mut state.preset_name_buf).desired_width(150.0));
        if ui.button("Save as Named Preset").clicked() {
            let clean_name = state.preset_name_buf.trim();
            if !clean_name.is_empty() {
                let mut meta = theme.active_preset_metadata();
                let slug = clean_name
                    .to_ascii_lowercase()
                    .chars()
                    .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
                    .collect::<String>();
                meta.id = CompactString::from(format!("custom-{slug}"));
                meta.name = CompactString::from(clean_name);
                if let Some(existing) = saved_custom_presets.iter_mut().find(|p| p.id == meta.id) {
                    *existing = meta.clone();
                } else {
                    saved_custom_presets.push(meta.clone());
                }
                theme.custom_preset = Some(meta);
                if let Ok(j) = theme.export_active_preset_json() {
                    *preset_json_buf = j;
                }
                *status_out = format!("Saved custom preset '{}'.", clean_name);
                dirty = true;
            }
        }
    });

    if !saved_custom_presets.is_empty() {
        ui.label(RichText::new("Saved Custom Presets:").small().strong());
        let mut delete_idx: Option<usize> = None;
        for (idx, cp) in saved_custom_presets.iter().enumerate() {
            ui.horizontal(|ui| {
                let (sw, _) = ui.allocate_exact_size(Vec2::new(12.0, 12.0), egui::Sense::hover());
                ui.painter().rect_filled(
                    sw,
                    CornerRadius::same(2),
                    srgb_to_color32(cp.accent_srgb),
                );
                let active = theme.custom_preset.as_ref().is_some_and(|c| c.id == cp.id);
                if ui
                    .selectable_label(active, format!("{} ({})", cp.name, cp.accent_hex))
                    .clicked()
                {
                    theme.custom_preset = Some(cp.clone());
                    theme.use_crystal_glass_backdrop = cp.is_glass_enabled;
                    state.sync_from_meta(cp);
                    dirty = true;
                }
                if ui.small_button("x").clicked() {
                    delete_idx = Some(idx);
                }
            });
        }
        if let Some(di) = delete_idx {
            saved_custom_presets.remove(di);
            dirty = true;
        }
    }

    dirty
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiskSubTab {
    Visualizer,
    Treemap,
    Duplicates,
    Snapshots,
    Volumes,
}

#[derive(Debug, Clone, Default)]
pub struct AutomationRuntimeState {
    pub active_rule_triggers: Vec<RuleTriggerEvent>,
    pub recent_alerts: Vec<AlertNotification>,
    pub quiet_hours_active: bool,
    pub sleep_preventing: bool,
    pub autobalance_throttled_count: usize,
    pub idle_throttled_count: usize,
    pub storage_rows_written: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectionProjection {
    AllSockets,
    ByProcess,
    ByRemoteHost,
}

#[derive(Debug, Clone)]
pub struct ConnectionTrafficRecord {
    pub prev_rx: u64,
    pub prev_tx: u64,
    pub last_sampled_at: Instant,
    pub history_bps: VecDeque<f64>,
}

#[derive(Debug, Clone, Default)]
pub struct AppLiveUsage {
    pub cpu_percent: f64,
    pub memory_bytes: u64,
    pub cpu_history: VecDeque<f64>,
}

#[derive(Debug, Clone, Default)]
pub struct ProcessSparklineHistory {
    pub cpu: VecDeque<f64>,
    pub mem_mib: VecDeque<f64>,
}

impl ProcessSparklineHistory {
    pub fn push(&mut self, cpu_pct: f64, mem_mib: f64) {
        if self.cpu.len() >= 30 {
            self.cpu.pop_front();
        }
        self.cpu.push_back(if cpu_pct.is_finite() {
            cpu_pct.max(0.0)
        } else {
            0.0
        });
        if self.mem_mib.len() >= 30 {
            self.mem_mib.pop_front();
        }
        self.mem_mib.push_back(if mem_mib.is_finite() {
            mem_mib.max(0.0)
        } else {
            0.0
        });
    }
}

pub fn draw_inline_sparkline(
    ui: &mut egui::Ui,
    values: &VecDeque<f64>,
    scale_100: bool,
    label: &str,
    accent: Color32,
) {
    ui.horizontal(|ui| {
        let (rect, _) = ui.allocate_exact_size(Vec2::new(62.0, 14.0), egui::Sense::hover());
        let painter = ui.painter_at(rect);
        let track = if ui.visuals().dark_mode {
            Color32::from_rgb(12, 16, 24)
        } else {
            Color32::from_rgb(232, 236, 242)
        };
        painter.rect_filled(rect, CornerRadius::same(3), track);
        if values.len() >= 2 {
            let y_max = if scale_100 {
                100.0
            } else {
                values.iter().copied().fold(1.0_f64, f64::max).max(1.0)
            };
            let n = values.len();
            let mut pts = Vec::with_capacity(n);
            for (i, &v) in values.iter().enumerate() {
                let nx = (i as f32) / ((n - 1) as f32);
                let ny = (v / y_max).clamp(0.0, 1.0) as f32;
                pts.push(Pos2::new(
                    rect.min.x + nx * rect.width(),
                    rect.max.y - ny * rect.height(),
                ));
            }
            painter.add(egui::Shape::line(pts, Stroke::new(1.4, accent)));
        }
        ui.label(label);
    });
}

pub fn draw_inline_hbar(ui: &mut egui::Ui, frac: f32, label: &str, accent: Color32) {
    ui.horizontal(|ui| {
        draw_hig_progress_bar(ui, 56.0, 8.0, frac, accent);
        ui.label(label);
    });
}

pub fn draw_configure_columns_panel(
    ui: &mut egui::Ui,
    salt: &str,
    columns: &mut Vec<TableColumnConfig>,
    defaults_fn: fn() -> Vec<TableColumnConfig>,
    accent: Color32,
) -> bool {
    let mut dirty = false;
    hig_card(ui, |ui| {
        ui.horizontal(|ui| {
            ui.label(
                RichText::new("Configure Columns & Cell Display Styles")
                    .font(FontId::proportional(14.0))
                    .strong()
                    .color(ui.visuals().strong_text_color()),
            );
            let _ = accent;
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.small_button("Reset Defaults").clicked() {
                    *columns = defaults_fn();
                    dirty = true;
                }
            });
        });
        ui.add_space(4.0);
        let len = columns.len();
        let mut move_up: Option<usize> = None;
        let mut move_down: Option<usize> = None;
        for (i, col) in columns.iter_mut().enumerate() {
            ui.horizontal(|ui| {
                if ui
                    .add_enabled(i > 0, egui::Button::new("↑").small())
                    .clicked()
                {
                    move_up = Some(i);
                }
                if ui
                    .add_enabled(i + 1 < len, egui::Button::new("↓").small())
                    .clicked()
                {
                    move_down = Some(i);
                }
                let mut visible = col.style != ColumnDisplayStyle::Hidden;
                if ui.checkbox(&mut visible, col.label.as_str()).changed() {
                    col.style = if visible {
                        ColumnDisplayStyle::TextOnly
                    } else {
                        ColumnDisplayStyle::Hidden
                    };
                    dirty = true;
                }
                egui::ComboBox::from_id_salt(format!("{salt}_{}", col.id))
                    .selected_text(col.style.label())
                    .show_ui(ui, |ui| {
                        for &st in ColumnDisplayStyle::allowed_for(col.is_percentage) {
                            if ui
                                .selectable_value(&mut col.style, st, st.label())
                                .changed()
                            {
                                dirty = true;
                            }
                        }
                    });
            });
        }
        if let Some(i) = move_up {
            columns.swap(i, i - 1);
            dirty = true;
        }
        if let Some(i) = move_down {
            columns.swap(i, i + 1);
            dirty = true;
        }
    });
    dirty
}

pub struct PlatformDataCache {
    pub services: Vec<ServiceEntry>,
    pub service_filter: String,
    pub service_state_filter: Option<ServiceState>,
    pub startup_items: Vec<StartupItem>,
    pub startup_filter: String,
    pub connections: Vec<NetworkConnectionEntry>,
    pub connection_filter: String,
    pub connection_projection: ConnectionProjection,
    pub connection_traffic: HashMap<String, ConnectionTrafficRecord>,
    pub wifi_report: Option<WirelessScanReport>,
    pub wifi_scanning: bool,
    pub lan_report: Option<LanRadarReport>,
    pub lan_scanning: bool,
    pub subnet_input: String,
    pub volumes: Vec<VolumeSpaceSummary>,
    pub hardware: Option<&'static SystemHardwareSummary>,
    pub users: Vec<UserSessionEntry>,
    pub installed_apps: Vec<InstalledAppEntry>,
    pub apps_slot: Arc<Mutex<Option<Vec<InstalledAppEntry>>>>,
    pub apps_size_slot: Arc<Mutex<Vec<(String, u64)>>>,
    pub apps_loading: bool,
    pub apps_filter: String,
    pub selected_app_idx: Option<usize>,
    pub selected_app_related: Vec<(RelatedFileEntry, bool)>,
    pub app_uninstall_status: String,
    pub drivers: Vec<DriverEntry>,
    pub drivers_slot: Arc<Mutex<Option<Vec<DriverEntry>>>>,
    pub drivers_loading: bool,
    pub drivers_filter: String,
    pub driver_refusal_status: String,
    pub health_tracker: SystemHealthTracker,
    pub health_trend_window: usize,
    pub health_trend_points: Vec<(i64, f64, f64, f64)>,
    pub health_trend_loaded_at: Option<Instant>,
    pub health_trend_loaded_window: usize,
    pub process_sparklines: HashMap<ProcessKey, ProcessSparklineHistory>,
    pub process_columns: Vec<TableColumnConfig>,
    pub process_columns_open: bool,
    pub app_columns: Vec<TableColumnConfig>,
    pub app_columns_open: bool,
    pub app_live_usage: HashMap<CompactString, AppLiveUsage>,
}

impl Default for PlatformDataCache {
    fn default() -> Self {
        Self::new()
    }
}

impl PlatformDataCache {
    pub fn new() -> Self {
        Self {
            services: Vec::new(),
            service_filter: String::new(),
            service_state_filter: None,
            startup_items: Vec::new(),
            startup_filter: String::new(),
            connections: Vec::new(),
            connection_filter: String::new(),
            connection_projection: ConnectionProjection::AllSockets,
            connection_traffic: HashMap::new(),
            wifi_report: None,
            wifi_scanning: false,
            lan_report: None,
            lan_scanning: false,
            subnet_input: detect_local_subnet(),
            volumes: Vec::new(),
            hardware: None,
            users: Vec::new(),
            installed_apps: Vec::new(),
            apps_slot: Arc::new(Mutex::new(None)),
            apps_size_slot: Arc::new(Mutex::new(Vec::new())),
            apps_loading: false,
            apps_filter: String::new(),
            selected_app_idx: None,
            selected_app_related: Vec::new(),
            app_uninstall_status: String::new(),
            drivers: Vec::new(),
            drivers_slot: Arc::new(Mutex::new(None)),
            drivers_loading: false,
            drivers_filter: String::new(),
            driver_refusal_status: String::new(),
            health_tracker: SystemHealthTracker::new(180),
            health_trend_window: 0,
            health_trend_points: Vec::new(),
            health_trend_loaded_at: None,
            health_trend_loaded_window: usize::MAX,
            process_sparklines: HashMap::new(),
            process_columns: default_process_columns(),
            process_columns_open: false,
            app_columns: default_app_columns(),
            app_columns_open: false,
            app_live_usage: HashMap::new(),
        }
    }

    pub fn update_app_live_usage(&mut self, snap: &SystemSnapshot) {
        if self.installed_apps.is_empty() {
            return;
        }
        let proc_samples: Vec<(String, f64, u64)> = snap
            .processes
            .iter()
            .map(|p| {
                let cpu = match p.cpu_percent.reading {
                    MetricReading::Measured { value, .. } if value.is_finite() => value.max(0.0),
                    _ => 0.0,
                };
                let mem = match p.phys_footprint_bytes.reading {
                    MetricReading::Measured { value, .. } if value.is_finite() => {
                        value.max(0.0) as u64
                    }
                    _ => 0,
                };
                (p.name.as_str().to_ascii_lowercase(), cpu, mem)
            })
            .collect();

        for app in &self.installed_apps {
            let stem = app
                .name
                .strip_suffix(".app")
                .or_else(|| app.name.strip_suffix(".exe"))
                .unwrap_or(app.name.as_str());
            let app_lower = stem.trim().to_ascii_lowercase();
            if app_lower.is_empty() {
                continue;
            }
            let first_word = app_lower
                .split_whitespace()
                .next()
                .unwrap_or(app_lower.as_str());
            let mut sum_cpu = 0.0_f64;
            let mut sum_mem = 0_u64;
            let mut matched = false;
            for (pname, pcpu, pmem) in &proc_samples {
                let p_clean = pname.strip_suffix(".exe").unwrap_or(pname.as_str());
                let is_match = p_clean == app_lower
                    || (app_lower.len() >= 3
                        && (p_clean.starts_with(&format!("{app_lower} "))
                            || p_clean.starts_with(&format!("{app_lower}helper"))
                            || p_clean.starts_with(&format!("{app_lower}-"))
                            || app_lower.starts_with(p_clean) && p_clean.len() >= 4))
                    || (first_word.len() >= 4 && p_clean == first_word);
                if is_match {
                    matched = true;
                    sum_cpu += *pcpu;
                    sum_mem = sum_mem.saturating_add(*pmem);
                }
            }
            let entry = self.app_live_usage.entry(app.path.clone()).or_default();
            entry.cpu_percent = if matched { sum_cpu } else { 0.0 };
            entry.memory_bytes = if matched { sum_mem } else { 0 };
            if entry.cpu_history.len() >= 30 {
                entry.cpu_history.pop_front();
            }
            entry.cpu_history.push_back(entry.cpu_percent);
        }
    }

    pub fn refresh_health_trends(&mut self) {
        let stale = self
            .health_trend_loaded_at
            .is_none_or(|t| t.elapsed() >= Duration::from_secs(10));
        if !stale && self.health_trend_loaded_window == self.health_trend_window {
            return;
        }
        let now_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(0);
        let lookback_ms: i64 = match self.health_trend_window {
            0 => 24 * 3600 * 1000,
            1 => 7 * 24 * 3600 * 1000,
            _ => 30 * 24 * 3600 * 1000,
        };
        let since_ms = now_ms.saturating_sub(lookback_ms);
        if let Ok(db) = MetricsHistoryStore::open_path(&default_telemetry_db_path())
            && let Ok(pts) = db.query_health_trend_points(since_ms, 60)
        {
            self.health_trend_points = pts;
        }
        self.health_trend_loaded_at = Some(Instant::now());
        self.health_trend_loaded_window = self.health_trend_window;
    }

    pub fn record_traffic(&mut self, conns: &[NetworkConnectionEntry]) {
        let now = Instant::now();
        for c in conns {
            let Some(rx) = c.rx_bytes else { continue };
            let tx = c.tx_bytes.unwrap_or(0);
            let key = format!(
                "{}:{}:{}->{}:{}",
                c.protocol.as_str(),
                c.local_address,
                c.local_port,
                c.remote_address,
                c.remote_port
            );
            let entry =
                self.connection_traffic
                    .entry(key)
                    .or_insert_with(|| ConnectionTrafficRecord {
                        prev_rx: rx,
                        prev_tx: tx,
                        last_sampled_at: now,
                        history_bps: VecDeque::with_capacity(60),
                    });
            let dt = now.duration_since(entry.last_sampled_at).as_secs_f64();
            if dt >= 0.05 {
                let d_rx = rx.saturating_sub(entry.prev_rx);
                let d_tx = tx.saturating_sub(entry.prev_tx);
                let bps = ((d_rx + d_tx) as f64) / dt;
                if entry.history_bps.len() >= 60 {
                    entry.history_bps.pop_front();
                }
                entry.history_bps.push_back(bps);
                entry.prev_rx = rx;
                entry.prev_tx = tx;
                entry.last_sampled_at = now;
            }
        }
    }

    pub fn poll_async_workers(&mut self) {
        if let Some(apps) = self
            .apps_slot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            self.installed_apps = apps;
            self.apps_loading = false;
            // On macOS, measure .app bundle sizes asynchronously. On Windows, rely on O(1) registry
            // EstimatedSize without walking C:\Program Files.
            if HostPlatform::current().is_macos() {
                let paths: Vec<String> = self
                    .installed_apps
                    .iter()
                    .filter(|a| a.size_bytes.is_none() && !a.path.is_empty())
                    .map(|a| a.path.to_string())
                    .collect();
                if !paths.is_empty() {
                    let size_slot = Arc::clone(&self.apps_size_slot);
                    std::thread::spawn(move || {
                        for p in paths {
                            let sz = measure_path_size_bytes(Path::new(&p));
                            size_slot
                                .lock()
                                .unwrap_or_else(|e| e.into_inner())
                                .push((p, sz));
                        }
                    });
                }
            }
        }
        let sizes: Vec<(String, u64)> = {
            let mut lock = self
                .apps_size_slot
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            std::mem::take(&mut *lock)
        };
        if !sizes.is_empty() {
            let map: HashMap<String, u64> = sizes.into_iter().collect();
            for app in &mut self.installed_apps {
                if let Some(&sz) = map.get(app.path.as_str()) {
                    app.size_bytes = Some(sz);
                }
            }
        }
        if let Some(drivers) = self
            .drivers_slot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            self.drivers = drivers;
            self.drivers_loading = false;
        }
    }

    pub fn trigger_apps_refresh(&mut self) {
        if self.apps_loading {
            return;
        }
        self.apps_loading = true;
        let slot = Arc::clone(&self.apps_slot);
        std::thread::spawn(move || {
            let apps = collect_installed_apps();
            *slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(apps);
        });
    }

    pub fn trigger_drivers_refresh(&mut self) {
        if self.drivers_loading {
            return;
        }
        self.drivers_loading = true;
        let slot = Arc::clone(&self.drivers_slot);
        std::thread::spawn(move || {
            let drivers = collect_drivers();
            *slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(drivers);
        });
    }

    pub fn ensure_loaded_sync_for_view(&mut self, view: GuiView) {
        self.poll_async_workers();
        match view {
            GuiView::Dest(Destination::Services) if self.services.is_empty() => {
                self.services = list_services();
            }
            GuiView::Dest(Destination::Startup) if self.startup_items.is_empty() => {
                self.startup_items = list_startup_items();
            }
            GuiView::Dest(Destination::Connections) if self.connections.is_empty() => {
                let conns = list_network_connections();
                self.record_traffic(&conns);
                self.connections = conns;
            }
            GuiView::Dest(Destination::DiskSpace) if self.volumes.is_empty() => {
                self.volumes = collect_mounted_volumes();
            }
            GuiView::Dest(Destination::SystemInfo) => {
                if self.hardware.is_none() {
                    self.hardware = Some(collect_system_hardware());
                }
                if self.volumes.is_empty() {
                    self.volumes = collect_mounted_volumes();
                }
                self.refresh_health_trends();
            }
            GuiView::Dest(Destination::Users) if self.users.is_empty() => {
                self.users = collect_users();
            }
            GuiView::Dest(Destination::InstalledApps) if self.installed_apps.is_empty() => {
                self.trigger_apps_refresh();
            }
            GuiView::Dest(Destination::Drivers) if self.drivers.is_empty() => {
                self.trigger_drivers_refresh();
            }
            _ => {}
        }
    }
}

pub fn trigger_wireless_scans(
    wifi_slot: Arc<Mutex<Option<WirelessScanReport>>>,
    lan_slot: Arc<Mutex<Option<LanRadarReport>>>,
    cache: &mut PlatformDataCache,
) {
    if !cache.wifi_scanning {
        cache.wifi_scanning = true;
        std::thread::spawn(move || {
            let rep = scan_wifi();
            *wifi_slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(rep);
        });
    }
    if !cache.lan_scanning {
        cache.lan_scanning = true;
        let cidr = cache.subnet_input.trim().to_string();
        std::thread::spawn(move || {
            let opt_cidr = if cidr.is_empty() {
                None
            } else {
                Some(cidr.as_str())
            };
            let rep = scan_lan_radar(opt_cidr, 120);
            *lan_slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(rep);
        });
    }
}

fn service_scope_label(svc: &ServiceEntry) -> &str {
    if !svc.user_account.is_empty() {
        return svc.user_account.as_str();
    }
    if svc.name.starts_with("com.apple.") {
        if HostPlatform::current().is_macos() {
            "Apple System"
        } else {
            "OS System"
        }
    } else if svc.binary_path.starts_with("/Library/") {
        "System Daemon"
    } else if svc.name.starts_with("application.") || svc.binary_path.contains("/Users/") {
        "User App / Agent"
    } else {
        "User Session"
    }
}

pub fn draw_services_view(
    ui: &mut egui::Ui,
    cache: &mut PlatformDataCache,
    status_out: &mut String,
    accent: Color32,
) {
    if cache.services.is_empty() {
        cache.services = list_services();
    }
    let plat = HostPlatform::current();
    hig_view_header(
        ui,
        match plat {
            HostPlatform::MacOs => "Background Services & Launch Daemons",
            HostPlatform::Windows => "Background Services & Windows SCM",
            HostPlatform::Linux => "Background Services & systemd Units",
        },
        match plat {
            HostPlatform::MacOs => {
                "Inspect active macOS launchd daemons and agents, filter by runtime state, and test lifecycle actions safely."
            }
            HostPlatform::Windows => {
                "Inspect registered Windows Service Control Manager (SCM) services, filter by runtime state, and test lifecycle actions safely."
            }
            HostPlatform::Linux => {
                "Inspect registered Linux systemd service units, filter by runtime state, and test lifecycle actions safely."
            }
        },
        accent,
    );

    let total_services = cache.services.len();
    let running_count = cache
        .services
        .iter()
        .filter(|s| s.state == ServiceState::Running)
        .count();
    let stopped_count = total_services.saturating_sub(running_count);
    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: "Total Services".to_string(),
                value: total_services.to_string(),
                detail: match plat {
                    HostPlatform::MacOs => "Registered launchd units".to_string(),
                    HostPlatform::Windows => "Registered Windows SCM services".to_string(),
                    HostPlatform::Linux => "Registered systemd units".to_string(),
                },
                color: accent,
            },
            HigKpiItem {
                label: "Running".to_string(),
                value: running_count.to_string(),
                detail: "Active with live PID".to_string(),
                color: hig_green(ui),
            },
            HigKpiItem {
                label: "Stopped / On-Demand".to_string(),
                value: stopped_count.to_string(),
                detail: "Loaded or idle until triggered".to_string(),
                color: hig_blue(ui),
            },
        ],
    );

    let query = cache.service_filter.trim().to_ascii_lowercase();
    let mut action_to_run: Option<(String, ServiceAction)> = None;
    let mut unsupported_test: Option<String> = None;

    hig_card(ui, |ui| {
        ui.set_min_width(ui.available_width());
        ui.horizontal_wrapped(|ui| {
            ui.label(RichText::new("Search:").strong());
            ui.add(
                egui::TextEdit::singleline(&mut cache.service_filter)
                    .hint_text("Filter by service label or binary path...")
                    .desired_width(220.0),
            );
            if ui.button("Refresh").clicked() {
                cache.services = list_services();
            }
            ui.separator();
            for (filter_opt, label) in [
                (None, "All"),
                (Some(ServiceState::Running), "Running"),
                (Some(ServiceState::Stopped), "Stopped / On-Demand"),
            ] {
                if ui
                    .selectable_label(cache.service_state_filter == filter_opt, label)
                    .clicked()
                {
                    cache.service_state_filter = filter_opt;
                }
            }
        });
        if plat.is_macos() {
            ui.add_space(4.0);
            ui.horizontal_wrapped(|ui| {
                ui.label(
                    RichText::new(
                        "macOS launchd StartType changes (enable/disable) report Unsupported unless edited via launchd plist or Login Items & Extensions.",
                    )
                    .font(FontId::proportional(11.5))
                    .color(ui.visuals().weak_text_color()),
                );
                if ui
                    .small_button("Open Login Items & Extensions")
                    .on_hover_text("Open macOS System Settings › General › Login Items & Extensions")
                    .clicked()
                {
                    open_macos_privacy_pane(
                        "x-apple.systempreferences:com.apple.LoginItems-Settings.extension",
                    );
                }
            });
        }
        ui.add_space(6.0);

        egui::ScrollArea::both()
            .id_salt("services_scroll")
            .max_height((ui.available_height() - 12.0).max(200.0))
            .show(ui, |ui| {
                egui::Grid::new("services_table")
                    .num_columns(6)
                    .striped(true)
                    .spacing([14.0, 6.0])
                    .show(ui, |ui| {
                        ui.label(RichText::new("Service Label").strong());
                        ui.label(RichText::new("Status").strong());
                        ui.label(RichText::new("Startup").strong());
                        ui.label(RichText::new("PID").strong());
                        ui.label(RichText::new("Scope").strong());
                        ui.label(RichText::new("Controls").strong());
                        ui.end_row();

                        for svc in cache
                            .services
                            .iter()
                            .filter(|s| {
                                (query.is_empty()
                                    || s.name.to_ascii_lowercase().contains(&query)
                                    || s.binary_path.to_ascii_lowercase().contains(&query))
                                    && cache.service_state_filter.is_none_or(|st| s.state == st)
                            })
                            .take(80)
                        {
                            let lbl_resp = ui.add(egui::Label::new(svc.name.as_str()).truncate());
                            if !svc.binary_path.is_empty() {
                                lbl_resp.on_hover_text(format!("Binary: {}", svc.binary_path));
                            }
                            let (badge_txt, badge_col) = match svc.state {
                                ServiceState::Running => ("Running", hig_green(ui)),
                                ServiceState::Stopped => match svc.start_type {
                                    ServiceStartType::Automatic
                                    | ServiceStartType::AutomaticDelayed
                                    | ServiceStartType::Manual => ("Loaded", hig_blue(ui)),
                                    _ => ("Stopped", hig_gray(ui)),
                                },
                                ServiceState::Paused
                                | ServiceState::StartPending
                                | ServiceState::StopPending => (svc.state.as_str(), hig_amber(ui)),
                                ServiceState::Unknown => ("Unknown", hig_gray(ui)),
                            };
                            hig_status_pill(ui, badge_txt, badge_col);
                            ui.label(svc.start_type.as_str());
                            ui.label(svc.pid.map_or_else(|| "—".to_string(), |p| p.to_string()));
                            ui.label(
                                RichText::new(service_scope_label(svc))
                                    .color(ui.visuals().weak_text_color()),
                            );
                            ui.horizontal(|ui| {
                                if ui
                                    .small_button("Start (Dry)")
                                    .on_hover_text("Simulate starting this launchd service without modifying system state.")
                                    .clicked()
                                {
                                    action_to_run =
                                        Some((svc.name.to_string(), ServiceAction::Start));
                                }
                                if ui
                                    .small_button("Stop (Dry)")
                                    .on_hover_text("Simulate stopping this launchd service without modifying system state.")
                                    .clicked()
                                {
                                    action_to_run =
                                        Some((svc.name.to_string(), ServiceAction::Stop));
                                }
                                if ui
                                    .small_button("Restart (Dry)")
                                    .on_hover_text("Simulate restarting this launchd service without modifying system state.")
                                    .clicked()
                                {
                                    action_to_run =
                                        Some((svc.name.to_string(), ServiceAction::Restart));
                                }
                                if ui
                                    .small_button("StartType")
                                    .on_hover_text("Test launchd StartType mutability (honestly reports Unsupported on macOS).")
                                    .clicked()
                                {
                                    unsupported_test = Some(svc.name.to_string());
                                }
                            });
                            ui.end_row();
                        }
                    });
            });
    });

    if let Some((name, act)) = action_to_run {
        let res = control_service(&name, act, true);
        *status_out = format!("Service {name} {act:?}: {res:?}");
    }
    if let Some(name) = unsupported_test {
        let res = set_service_start_type(&name, ServiceStartType::Manual);
        *status_out = format!("SetStartType({name}): {res:?}");
    }
}

fn startup_scope_label(item: &StartupItem) -> &'static str {
    let plat = HostPlatform::current();
    match item.item_type {
        StartupItemType::LaunchAgent if plat.is_macos() => {
            if item.user_writable {
                "User LaunchAgent"
            } else {
                "Global LaunchAgent"
            }
        }
        StartupItemType::LaunchDaemon if plat.is_macos() => "System LaunchDaemon",
        other => other.as_str_for(plat),
    }
}

pub fn draw_startup_view(
    ui: &mut egui::Ui,
    cache: &mut PlatformDataCache,
    status_out: &mut String,
    accent: Color32,
) {
    if cache.startup_items.is_empty() {
        cache.startup_items = list_startup_items();
    }
    let plat = HostPlatform::current();
    hig_view_header(
        ui,
        "Login Items & Startup Agents",
        match plat {
            HostPlatform::MacOs => {
                "Review LaunchAgents and LaunchDaemons configured to run automatically at login or system boot."
            }
            HostPlatform::Windows => {
                "Review Registry Run keys and Startup Folder tasks configured to launch automatically at user sign-in."
            }
            HostPlatform::Linux => {
                "Review XDG Autostart entries and user units configured to run automatically at login."
            }
        },
        accent,
    );

    let total = cache.startup_items.len();
    let enabled = cache.startup_items.iter().filter(|i| i.is_enabled).count();
    let user_writable = cache
        .startup_items
        .iter()
        .filter(|i| i.user_writable)
        .count();
    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: "Total Startup Items".to_string(),
                value: total.to_string(),
                detail: match plat {
                    HostPlatform::MacOs => "Discovered launch plists".to_string(),
                    HostPlatform::Windows => {
                        "Discovered Registry Run & Startup entries".to_string()
                    }
                    HostPlatform::Linux => "Discovered XDG autostart entries".to_string(),
                },
                color: accent,
            },
            HigKpiItem {
                label: "Enabled".to_string(),
                value: enabled.to_string(),
                detail: "Loaded at login or boot".to_string(),
                color: hig_green(ui),
            },
            HigKpiItem {
                label: "User-Manageable".to_string(),
                value: user_writable.to_string(),
                detail: match plat {
                    HostPlatform::MacOs => "Writable in ~/Library/LaunchAgents".to_string(),
                    HostPlatform::Windows => "Writable in HKCU\\...\\Run".to_string(),
                    HostPlatform::Linux => "Writable in ~/.config/autostart".to_string(),
                },
                color: hig_blue(ui),
            },
        ],
    );

    let query = cache.startup_filter.trim().to_ascii_lowercase();
    let mut toggle_req: Option<(StartupItem, bool)> = None;

    hig_card(ui, |ui| {
        ui.set_min_width(ui.available_width());
        ui.horizontal_wrapped(|ui| {
            ui.label(RichText::new("Search:").strong());
            ui.add(
                egui::TextEdit::singleline(&mut cache.startup_filter)
                    .hint_text("Filter by label or executable command...")
                    .desired_width(240.0),
            );
            if ui
                .button(if plat.is_macos() {
                    "Rescan Plists"
                } else {
                    "Rescan Startup Items"
                })
                .clicked()
            {
                cache.startup_items = list_startup_items();
            }
            if plat.is_macos()
                && ui
                    .button("Open Login Items Settings")
                    .on_hover_text(
                        "Open macOS System Settings › General › Login Items & Extensions to manage system-wide background items",
                    )
                    .clicked()
            {
                open_macos_privacy_pane(
                    "x-apple.systempreferences:com.apple.LoginItems-Settings.extension",
                );
            }
        });
        ui.add_space(6.0);

        egui::ScrollArea::vertical()
            .id_salt("startup_scroll")
            .max_height((ui.available_height() - 12.0).max(200.0))
            .show(ui, |ui| {
                egui::Grid::new("startup_grid")
                    .num_columns(6)
                    .striped(true)
                    .spacing([14.0, 6.0])
                    .show(ui, |ui| {
                        ui.label(RichText::new("Label").strong());
                        ui.label(RichText::new("Scope").strong());
                        ui.label(RichText::new("Status").strong());
                        ui.label(RichText::new("Executable / Command").strong());
                        ui.label(
                            RichText::new(if plat.is_macos() {
                                "Plist Location"
                            } else {
                                "Config Location"
                            })
                            .strong(),
                        );
                        ui.label(RichText::new("Action").strong());
                        ui.end_row();

                        for item in cache
                            .startup_items
                            .iter()
                            .filter(|i| {
                                query.is_empty()
                                    || i.name.to_ascii_lowercase().contains(&query)
                                    || i.command.to_ascii_lowercase().contains(&query)
                            })
                            .take(80)
                        {
                            ui.add(egui::Label::new(item.name.as_str()).truncate());
                            ui.label(
                                RichText::new(startup_scope_label(item))
                                    .color(ui.visuals().weak_text_color()),
                            );
                            if item.is_enabled {
                                hig_status_pill(ui, "Enabled", hig_green(ui));
                            } else {
                                hig_status_pill(ui, "Disabled", hig_gray(ui));
                            }
                            ui.add_sized(
                                [210.0, 18.0],
                                egui::Label::new(item.command.as_str()).truncate(),
                            )
                            .on_hover_text(item.command.as_str());
                            ui.add_sized(
                                [170.0, 18.0],
                                egui::Label::new(
                                    RichText::new(item.location.as_str())
                                        .color(ui.visuals().weak_text_color()),
                                )
                                .truncate(),
                            )
                            .on_hover_text(item.location.as_str());
                            if item.user_writable {
                                let (label, tip) = if item.is_enabled {
                                    (
                                        "Disable (Dry)",
                                        "Simulate disabling this user LaunchAgent without modifying your plist.",
                                    )
                                } else {
                                    (
                                        "Enable (Dry)",
                                        "Simulate enabling this user LaunchAgent without modifying your plist.",
                                    )
                                };
                                if ui.small_button(label).on_hover_text(tip).clicked() {
                                    toggle_req = Some((item.clone(), !item.is_enabled));
                                }
                            } else {
                                ui.label(
                                    RichText::new("System Read-Only")
                                        .font(FontId::proportional(11.5))
                                        .color(ui.visuals().weak_text_color()),
                                )
                                .on_hover_text(
                                    "Owned by macOS or root in /Library or /System/Library.",
                                );
                            }
                            ui.end_row();
                        }
                    });
            });
    });

    if let Some((item, en)) = toggle_req {
        let res = set_startup_item_enabled(&item, en, true);
        *status_out = format!("Startup toggle ({} to {en}): {res:?}", item.name);
    }
}

pub fn draw_connections_view(ui: &mut egui::Ui, cache: &mut PlatformDataCache, accent: Color32) {
    if cache.connections.is_empty() {
        let conns = list_network_connections();
        cache.record_traffic(&conns);
        cache.connections = conns;
    }
    hig_view_header(
        ui,
        "Network Connections & Active Sockets",
        "Monitor live TCP and UDP sockets, owning processes, and per-socket traffic rates.",
        accent,
    );

    let active_apps: HashSet<&str> = cache
        .connections
        .iter()
        .map(|c| c.process_name.as_str())
        .filter(|name| !name.is_empty())
        .collect();
    let established_count = cache
        .connections
        .iter()
        .filter(|c| c.state == TcpState::Established)
        .count();
    let listening_count = cache
        .connections
        .iter()
        .filter(|c| c.state == TcpState::Listen)
        .count();

    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: "Active Networked Apps".to_string(),
                value: active_apps.len().to_string(),
                detail: format!("Across {} open sockets", cache.connections.len()),
                color: accent,
            },
            HigKpiItem {
                label: "Established TCP Sockets".to_string(),
                value: established_count.to_string(),
                detail: "Live peer-to-peer streams".to_string(),
                color: hig_green(ui),
            },
            HigKpiItem {
                label: "Listening Ports".to_string(),
                value: listening_count.to_string(),
                detail: "Local server bindings".to_string(),
                color: hig_blue(ui),
            },
        ],
    );

    let query = cache.connection_filter.trim().to_ascii_lowercase();

    hig_card(ui, |ui| {
        ui.set_min_width(ui.available_width());
        ui.horizontal_wrapped(|ui| {
            ui.label(RichText::new("Search:").strong());
            ui.add(
                egui::TextEdit::singleline(&mut cache.connection_filter)
                    .hint_text("Process, IP address, or socket state...")
                    .desired_width(220.0),
            );
            if ui.button("Refresh Sockets").clicked() {
                let conns = list_network_connections();
                cache.record_traffic(&conns);
                cache.connections = conns;
            }
            ui.separator();
            ui.selectable_value(
                &mut cache.connection_projection,
                ConnectionProjection::AllSockets,
                "All Sockets",
            );
            ui.selectable_value(
                &mut cache.connection_projection,
                ConnectionProjection::ByProcess,
                "By Process",
            );
            ui.selectable_value(
                &mut cache.connection_projection,
                ConnectionProjection::ByRemoteHost,
                "By Remote Host",
            );
        });
        ui.add_space(4.0);
        ui.horizontal_wrapped(|ui| {
            ui.label(
                RichText::new("Standard privileges attribute PIDs for your user session's sockets. To resolve root-daemon socket PIDs, run:")
                    .font(FontId::proportional(13.0))
                    .color(ui.visuals().weak_text_color()),
            );
            draw_hig_command_box(ui, "sudo nexus-monitor", "connections_sudo");
        });
        ui.add_space(6.0);

        let filtered: Vec<&NetworkConnectionEntry> = cache
            .connections
            .iter()
            .filter(|c| {
                query.is_empty()
                    || c.process_name.to_ascii_lowercase().contains(&query)
                    || c.local_address.contains(&query)
                    || c.remote_address.contains(&query)
                    || c.state.as_str().to_ascii_lowercase().contains(&query)
            })
            .collect();

        match cache.connection_projection {
            ConnectionProjection::AllSockets => {
                egui::ScrollArea::both()
                    .id_salt("connections_all_scroll")
                    .max_height((ui.available_height() - 12.0).max(200.0))
                    .show(ui, |ui| {
                        egui::Grid::new("connections_grid")
                            .num_columns(8)
                            .striped(true)
                            .spacing([12.0, 5.0])
                            .show(ui, |ui| {
                                ui.label(RichText::new("Proto").strong());
                                ui.label(RichText::new("Local Address").strong());
                                ui.label(RichText::new("Remote Address").strong());
                                ui.label(RichText::new("State").strong());
                                ui.label(RichText::new("PID").strong());
                                ui.label(RichText::new("Process").strong());
                                ui.label(RichText::new("RX / TX").strong());
                                ui.label(RichText::new("Rate").strong());
                                ui.end_row();

                                for c in filtered.into_iter().take(120) {
                                    ui.label(c.protocol.as_str());
                                    ui.label(format!("{}:{}", c.local_address, c.local_port));
                                    ui.label(format!("{}:{}", c.remote_address, c.remote_port));
                                    let state_col = match c.state {
                                        TcpState::Established => hig_green(ui),
                                        TcpState::Listen => hig_blue(ui),
                                        TcpState::SynSent
                                        | TcpState::SynReceived
                                        | TcpState::CloseWait
                                        | TcpState::Closing
                                        | TcpState::FinWait1
                                        | TcpState::FinWait2
                                        | TcpState::LastAck => hig_amber(ui),
                                        TcpState::TimeWait
                                        | TcpState::Closed
                                        | TcpState::Unknown => hig_gray(ui),
                                    };
                                    hig_status_pill(ui, c.state.as_str(), state_col);
                                    ui.label(
                                        c.pid.map_or_else(|| "—".to_string(), |p| p.to_string()),
                                    );
                                    ui.label(c.process_name.as_str());
                                    let rx_tx = match (c.rx_bytes, c.tx_bytes) {
                                        (Some(r), Some(t)) => {
                                            format!("{}/{}", format_bytes(r), format_bytes(t))
                                        }
                                        _ => "—".to_string(),
                                    };
                                    ui.label(rx_tx);
                                    let key = format!(
                                        "{}:{}:{}->{}:{}",
                                        c.protocol.as_str(),
                                        c.local_address,
                                        c.local_port,
                                        c.remote_address,
                                        c.remote_port
                                    );
                                    let spark = cache
                                        .connection_traffic
                                        .get(&key)
                                        .map(|r| sparkline_text(&r.history_bps))
                                        .unwrap_or_else(|| "·".to_string());
                                    ui.label(RichText::new(spark).monospace().color(accent));
                                    ui.end_row();
                                }
                            });
                    });
            }
            ConnectionProjection::ByProcess | ConnectionProjection::ByRemoteHost => {
                let by_proc = cache.connection_projection == ConnectionProjection::ByProcess;
                let mut grouped: HashMap<String, (usize, u64, u64)> = HashMap::new();
                for c in filtered {
                    let gkey = if by_proc {
                        format!(
                            "{} (PID {})",
                            c.process_name,
                            c.pid.map_or_else(|| "—".to_string(), |p| p.to_string())
                        )
                    } else {
                        c.remote_address.to_string()
                    };
                    let e = grouped.entry(gkey).or_insert((0, 0, 0));
                    e.0 += 1;
                    e.1 = e.1.saturating_add(c.rx_bytes.unwrap_or(0));
                    e.2 = e.2.saturating_add(c.tx_bytes.unwrap_or(0));
                }
                let mut rows: Vec<(String, usize, u64, u64)> = grouped
                    .into_iter()
                    .map(|(k, (cnt, rx, tx))| (k, cnt, rx, tx))
                    .collect();
                rows.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| (b.2 + b.3).cmp(&(a.2 + a.3))));

                egui::ScrollArea::vertical()
                    .id_salt("connections_grouped_scroll")
                    .max_height((ui.available_height() - 12.0).max(200.0))
                    .show(ui, |ui| {
                        egui::Grid::new("connections_grouped_grid")
                            .num_columns(4)
                            .striped(true)
                            .spacing([14.0, 5.0])
                            .show(ui, |ui| {
                                ui.label(
                                    RichText::new(if by_proc { "Process" } else { "Remote Host" })
                                        .strong(),
                                );
                                ui.label(RichText::new("Sockets").strong());
                                ui.label(RichText::new("Total RX").strong());
                                ui.label(RichText::new("Total TX").strong());
                                ui.end_row();
                                for (label, cnt, rx, tx) in rows.into_iter().take(120) {
                                    ui.label(label);
                                    ui.label(cnt.to_string());
                                    ui.label(format_bytes(rx));
                                    ui.label(format_bytes(tx));
                                    ui.end_row();
                                }
                            });
                    });
            }
        }
    });
}

fn sparkline_text(samples: &VecDeque<f64>) -> String {
    if samples.is_empty() {
        return "·".to_string();
    }
    const BARS: [char; 8] = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
    let max_v = samples.iter().copied().fold(0.0_f64, f64::max).max(1.0);
    samples
        .iter()
        .rev()
        .take(16)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .map(|&v| {
            let idx = ((v / max_v).clamp(0.0, 1.0) * 7.0).round() as usize;
            BARS[idx.min(7)]
        })
        .collect()
}

fn wifi_signal_fraction(rssi: &Metric<f64>) -> Option<f32> {
    match rssi.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => {
            Some(((value + 95.0) / 55.0).clamp(0.05, 1.0) as f32)
        }
        _ => None,
    }
}

#[allow(clippy::too_many_arguments)]
pub fn draw_wireless_radar_view(
    ui: &mut egui::Ui,
    cache: &mut PlatformDataCache,
    wifi_slot: &Arc<Mutex<Option<WirelessScanReport>>>,
    lan_slot: &Arc<Mutex<Option<LanRadarReport>>>,
    accent: Color32,
) {
    if cache.wifi_report.is_none() && cache.lan_report.is_none() && !cache.wifi_scanning {
        trigger_wireless_scans(Arc::clone(wifi_slot), Arc::clone(lan_slot), cache);
    }

    hig_view_header(
        ui,
        "Wireless Spectrum & LAN Radar",
        "Inspect nearby Wi-Fi networks, RF signal-to-noise quality, and active hosts on your local subnet.",
        accent,
    );

    let (sig_noise_val, sig_noise_detail, sig_color) = if let Some(ref rep) = cache.wifi_report {
        if let Some(conn) = rep.networks.iter().find(|n| n.is_connected) {
            (
                format!(
                    "{} / {}",
                    conn.rssi_dbm.format_with_unit(0, "dBm"),
                    conn.noise_dbm.format_with_unit(0, "dBm")
                ),
                format!("{} · Ch {}", conn.band.as_str(), conn.channel),
                hig_green(ui),
            )
        } else {
            (
                "Not Associated".to_string(),
                format!("{} nearby networks", rep.networks.len()),
                hig_amber(ui),
            )
        }
    } else {
        (
            "Scanning...".to_string(),
            "Reading AirPort RF spectrum".to_string(),
            accent,
        )
    };

    let (conn_ssid_val, conn_ssid_detail) = if let Some(ref rep) = cache.wifi_report {
        (
            rep.connected_ssid
                .as_deref()
                .unwrap_or("Redacted / None")
                .to_string(),
            format!("Interface: {}", rep.interface),
        )
    } else {
        (
            "Scanning...".to_string(),
            "Detecting active Wi-Fi interface".to_string(),
        )
    };

    let (lan_val, lan_detail) = if let Some(ref lan) = cache.lan_report {
        (
            lan.hosts.len().to_string(),
            format!(
                "{} via {} ({} ms)",
                lan.subnet, lan.scanner_backend, lan.elapsed_ms
            ),
        )
    } else {
        (
            "Scanning...".to_string(),
            format!("Probing {}", cache.subnet_input),
        )
    };

    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: "Wi-Fi Signal & Noise dBm".to_string(),
                value: sig_noise_val,
                detail: sig_noise_detail,
                color: sig_color,
            },
            HigKpiItem {
                label: "Connected Network".to_string(),
                value: conn_ssid_val,
                detail: conn_ssid_detail,
                color: hig_blue(ui),
            },
            HigKpiItem {
                label: "Discovered LAN Devices".to_string(),
                value: lan_val,
                detail: lan_detail,
                color: accent,
            },
        ],
    );

    ui.horizontal_wrapped(|ui| {
        ui.label(RichText::new("Subnet CIDR:").strong());
        ui.add(egui::TextEdit::singleline(&mut cache.subnet_input).desired_width(150.0));
        if ui.button("Rescan Wi-Fi + LAN").clicked() {
            trigger_wireless_scans(Arc::clone(wifi_slot), Arc::clone(lan_slot), cache);
        }
    });
    ui.add_space(6.0);

    let wifi_redacted = HostPlatform::current().is_macos()
        && cache.wifi_report.as_ref().is_some_and(|rep| {
            rep.connected_ssid
                .as_deref()
                .is_none_or(|s| s.contains("redacted") || s.contains("Redacted") || s.is_empty())
                || rep
                    .networks
                    .iter()
                    .any(|n| n.ssid.contains("redacted") || n.ssid.contains("Redacted"))
        });
    if wifi_redacted {
        let _ = draw_permission_guidance_card(
            ui,
            "Location Services",
            hig_amber(ui),
            "Enable Location Services to show Wi-Fi network names",
            "macOS hides Wi-Fi SSID names (<redacted>) until Location Services is enabled for Nexus.",
            &[
                "Click Open Location Services Settings.",
                "Toggle Nexus System Monitor ON, then click Rescan Wi-Fi + LAN.",
            ],
            None,
            Some((
                "Open Location Services Settings",
                "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices",
            )),
            false,
            None,
        );
        ui.add_space(6.0);
    }

    ui.columns(2, |cols| {
        hig_card(&mut cols[0], |ui| {
            ui.set_min_width(ui.available_width());
            hig_card_header(
                ui,
                "Wi-Fi RF Spectrum",
                "Nearby access points with band classification, signal quality, and privacy redaction status.",
                accent,
            );
            if let Some(ref rep) = cache.wifi_report {
                if !rep.notes.is_empty() {
                    ui.label(
                        RichText::new(rep.notes.as_str())
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                    );
                    ui.add_space(4.0);
                }
                egui::ScrollArea::both()
                    .id_salt("wifi_scroll")
                    .max_height((ui.available_height() - 12.0).max(160.0))
                    .show(ui, |ui| {
                        egui::Grid::new("wifi_net_grid")
                            .num_columns(7)
                            .striped(true)
                            .spacing([8.0, 5.0])
                            .show(ui, |ui| {
                                ui.label(RichText::new("SSID").strong());
                                ui.label(RichText::new("Band").strong());
                                ui.label(RichText::new("Ch").strong());
                                ui.label(RichText::new("Quality").strong());
                                ui.label(RichText::new("RSSI").strong());
                                ui.label(RichText::new("Noise").strong());
                                ui.label(RichText::new("Security").strong());
                                ui.end_row();
                                for n in &rep.networks {
                                    ui.horizontal(|ui| {
                                        ui.add_sized(
                                            [110.0, 20.0],
                                            egui::Label::new(n.ssid.as_str()).truncate(),
                                        );
                                        if n.is_connected {
                                            hig_status_pill(ui, "Connected", hig_green(ui));
                                        }
                                    });
                                    let band_col = match n.band {
                                        WifiBand::Band6GHz => hig_green(ui),
                                        WifiBand::Band5GHz => hig_blue(ui),
                                        WifiBand::Band2_4GHz => hig_amber(ui),
                                        WifiBand::Unknown => hig_gray(ui),
                                    };
                                    hig_status_pill(ui, n.band.as_str(), band_col);
                                    ui.label(n.channel.to_string());
                                    if let Some(frac) = wifi_signal_fraction(&n.rssi_dbm) {
                                        let q_col = if frac > 0.65 {
                                            hig_green(ui)
                                        } else if frac > 0.35 {
                                            hig_amber(ui)
                                        } else {
                                            hig_red(ui)
                                        };
                                        draw_hig_progress_bar(ui, 44.0, 7.0, frac, q_col);
                                    } else {
                                        ui.label("—");
                                    }
                                    ui.label(n.rssi_dbm.format_with_unit(0, "dBm"));
                                    ui.label(n.noise_dbm.format_with_unit(0, "dBm"));
                                    ui.add_sized(
                                        [105.0, 20.0],
                                        egui::Label::new(n.security.as_str()).truncate(),
                                    );
                                    ui.end_row();
                                }
                            });
                    });
            } else {
                ui.label("Scanning Wi-Fi via system_profiler SPAirPortDataType in background...");
            }
        });

        hig_card(&mut cols[1], |ui| {
            ui.set_min_width(ui.available_width());
            hig_card_header(
                ui,
                "LAN Subnet Discovery",
                "Active hosts, MAC vendor hints, round-trip latency, and open TCP services.",
                accent,
            );
            if let Some(ref lan) = cache.lan_report {
                if !lan.notes.is_empty() {
                    ui.label(
                        RichText::new(lan.notes.as_str())
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                    );
                    ui.add_space(4.0);
                }
                egui::ScrollArea::both()
                    .id_salt("lan_scroll")
                    .max_height((ui.available_height() - 12.0).max(160.0))
                    .show(ui, |ui| {
                        egui::Grid::new("lan_hosts_grid")
                            .num_columns(5)
                            .striped(true)
                            .spacing([10.0, 5.0])
                            .show(ui, |ui| {
                                ui.label(RichText::new("IP Address").strong());
                                ui.label(RichText::new("Hostname").strong());
                                ui.label(RichText::new("MAC / Vendor").strong());
                                ui.label(RichText::new("Latency").strong());
                                ui.label(RichText::new("Open Ports").strong());
                                ui.end_row();
                                for h in &lan.hosts {
                                    ui.label(h.ip.as_str());
                                    ui.label(h.hostname.as_str());
                                    ui.label(format!(
                                        "{} {}",
                                        h.mac.as_deref().unwrap_or("—"),
                                        h.vendor.as_deref().unwrap_or("")
                                    ));
                                    ui.label(h.latency_ms.format_with_unit(1, "ms"));
                                    let ports: Vec<String> = h
                                        .open_ports
                                        .iter()
                                        .map(|p| format!("{}", p.port))
                                        .collect();
                                    ui.label(if ports.is_empty() {
                                        "—".to_string()
                                    } else {
                                        ports.join(", ")
                                    });
                                    ui.end_row();
                                }
                            });
                    });
            } else {
                ui.label("Probing local LAN subnet in background...");
            }
        });
    });
}

fn health_level_color(level: HealthLevel) -> Color32 {
    match level {
        HealthLevel::Excellent | HealthLevel::Good => Color32::from_rgb(48, 209, 88),
        HealthLevel::Fair => Color32::from_rgb(255, 214, 10),
        HealthLevel::Poor | HealthLevel::Critical => Color32::from_rgb(255, 69, 58),
    }
}

fn draw_health_ring_gauge(
    ui: &mut egui::Ui,
    score: f64,
    level: HealthLevel,
    trend: TrendDirection,
) {
    let size = Vec2::new(136.0, 136.0);
    let (rect, _) = ui.allocate_exact_size(size, egui::Sense::hover());
    let painter = ui.painter_at(rect);
    let center = rect.center();
    let radius = 52.0_f32;
    let ring_col = health_level_color(level);

    painter.circle_stroke(
        center,
        radius,
        Stroke::new(10.0, Color32::from_rgba_unmultiplied(120, 140, 165, 42)),
    );

    let frac = (score / 100.0).clamp(0.0, 1.0) as f32;
    let steps = ((frac * 64.0).ceil() as usize).max(2);
    let start_angle = -std::f32::consts::FRAC_PI_2;
    let total_sweep = std::f32::consts::TAU * frac;
    let mut arc_pts = Vec::with_capacity(steps + 1);
    for i in 0..=steps {
        let t = (i as f32) / (steps as f32);
        let a = start_angle + total_sweep * t;
        arc_pts.push(Pos2::new(
            center.x + a.cos() * radius,
            center.y + a.sin() * radius,
        ));
    }
    if frac > 0.005 {
        painter.add(egui::Shape::line(
            arc_pts.clone(),
            Stroke::new(
                14.0,
                Color32::from_rgba_unmultiplied(ring_col.r(), ring_col.g(), ring_col.b(), 36),
            ),
        ));
        painter.add(egui::Shape::line(arc_pts, Stroke::new(9.0, ring_col)));
    }

    painter.text(
        center + Vec2::new(0.0, -8.0),
        egui::Align2::CENTER_CENTER,
        format!("{score:.0}"),
        FontId::new(28.0, FontFamily::Name("led".into())),
        ring_col,
    );
    painter.text(
        center + Vec2::new(0.0, 13.0),
        egui::Align2::CENTER_CENTER,
        level.as_str(),
        FontId::proportional(12.0),
        ui.visuals().text_color(),
    );
    let trend_col = match trend {
        TrendDirection::Improving => Color32::from_rgb(48, 209, 88),
        TrendDirection::Stable => Color32::from_rgb(130, 180, 240),
        TrendDirection::Degrading => Color32::from_rgb(255, 69, 58),
    };
    painter.text(
        center + Vec2::new(0.0, 27.0),
        egui::Align2::CENTER_CENTER,
        format!("Trend: {}", trend.as_str()),
        FontId::proportional(11.5),
        trend_col,
    );
}

fn bucket_composite_health(cpu: f64, mem: f64, temp: f64) -> f64 {
    let cpu_s = HealthScoring::score_cpu(cpu);
    let mem_s = HealthScoring::score_memory(mem);
    let mut weighted = cpu_s * HealthScoring::CPU_WEIGHT + mem_s * HealthScoring::MEM_WEIGHT;
    let mut total_w = HealthScoring::CPU_WEIGHT + HealthScoring::MEM_WEIGHT;
    if temp > 0.0 && temp.is_finite() {
        weighted += HealthScoring::score_thermal(temp) * HealthScoring::THERMAL_WEIGHT;
        total_w += HealthScoring::THERMAL_WEIGHT;
    }
    (weighted / total_w).clamp(0.0, 100.0)
}

fn draw_health_trend_sparkline(
    ui: &mut egui::Ui,
    points: &[(i64, f64, f64, f64)],
    live_tracker: &SystemHealthTracker,
    accent: Color32,
) {
    let (rect, resp) = ui.allocate_exact_size(
        Vec2::new(ui.available_width().max(160.0), 96.0),
        egui::Sense::hover(),
    );
    let painter = ui.painter_at(rect);
    let canvas_bg = if ui.visuals().dark_mode {
        Color32::from_rgb(10, 14, 20)
    } else {
        Color32::from_rgb(244, 247, 252)
    };
    let canvas_border = if ui.visuals().dark_mode {
        Color32::from_rgba_unmultiplied(255, 255, 255, 26)
    } else {
        Color32::from_rgba_unmultiplied(15, 23, 42, 26)
    };
    let label_col = if ui.visuals().dark_mode {
        Color32::from_gray(210)
    } else {
        Color32::from_gray(55)
    };
    painter.rect_filled(rect, CornerRadius::same(6), canvas_bg);
    painter.rect_stroke(
        rect,
        CornerRadius::same(6),
        Stroke::new(1.0, canvas_border),
        StrokeKind::Inside,
    );

    // 70 and 90 health threshold reference lines
    for (ref_val, col) in [
        (70.0_f32, Color32::from_rgba_unmultiplied(255, 214, 10, 65)),
        (90.0_f32, Color32::from_rgba_unmultiplied(48, 209, 88, 65)),
    ] {
        let ry = rect.max.y - (ref_val / 100.0) * rect.height();
        painter.line_segment(
            [Pos2::new(rect.min.x, ry), Pos2::new(rect.max.x, ry)],
            Stroke::new(1.0, col),
        );
    }

    if points.len() >= 2 {
        let health_col = Color32::from_rgb(48, 209, 88);
        let cpu_col = Color32::from_rgb(255, 214, 10);
        let mem_col = Color32::from_rgb(10, 132, 255);
        let n = points.len();
        let mut health_pts = Vec::with_capacity(n);
        let mut cpu_pts = Vec::with_capacity(n);
        let mut mem_pts = Vec::with_capacity(n);
        for (idx, &(_ts, cpu, mem, temp)) in points.iter().enumerate() {
            let nx = (idx as f32) / ((n - 1) as f32);
            let x = rect.min.x + nx * rect.width();
            let hs = bucket_composite_health(cpu, mem, temp);
            health_pts.push(Pos2::new(
                x,
                rect.max.y - (hs as f32 / 100.0) * rect.height(),
            ));
            cpu_pts.push(Pos2::new(
                x,
                rect.max.y - (cpu.clamp(0.0, 100.0) as f32 / 100.0) * rect.height(),
            ));
            mem_pts.push(Pos2::new(
                x,
                rect.max.y - (mem.clamp(0.0, 100.0) as f32 / 100.0) * rect.height(),
            ));
        }
        painter.add(egui::Shape::line(mem_pts, Stroke::new(1.3, mem_col)));
        painter.add(egui::Shape::line(cpu_pts, Stroke::new(1.3, cpu_col)));
        painter.add(egui::Shape::line(health_pts, Stroke::new(2.0, health_col)));
        painter.text(
            rect.min + Vec2::new(6.0, 4.0),
            egui::Align2::LEFT_TOP,
            format!("SQLite Buckets: {n} (Green=Health, Yellow=CPU%, Blue=Mem% | Ref: 70/90)"),
            FontId::proportional(11.5),
            label_col,
        );
        if let Some(hover) = resp.hover_pos()
            && rect.contains(hover)
        {
            let nx = ((hover.x - rect.min.x) / rect.width().max(1.0)).clamp(0.0, 1.0);
            let idx = ((nx * ((n - 1) as f32)).round() as usize).min(n - 1);
            let (_ts, cpu, mem, temp) = points[idx];
            let hs = bucket_composite_health(cpu, mem, temp);
            let hx = rect.min.x + (idx as f32 / (n - 1) as f32) * rect.width();
            painter.line_segment(
                [Pos2::new(hx, rect.min.y), Pos2::new(hx, rect.max.y)],
                Stroke::new(1.0, Color32::from_rgba_unmultiplied(230, 240, 255, 110)),
            );
            resp.on_hover_text(format!(
                "Bucket #{}: Health Score {hs:.1}/100 | CPU {cpu:.1}% | Mem {mem:.1}% | Temp {temp:.1} C",
                idx + 1
            ));
        }
    } else if live_tracker.points().len() >= 2 {
        let live = live_tracker.points();
        let n = live.len();
        let mut pts = Vec::with_capacity(n);
        for (idx, &(_ts, score)) in live.iter().enumerate() {
            let nx = (idx as f32) / ((n - 1) as f32);
            pts.push(Pos2::new(
                rect.min.x + nx * rect.width(),
                rect.max.y - (score.clamp(0.0, 100.0) as f32 / 100.0) * rect.height(),
            ));
        }
        painter.add(egui::Shape::line(pts, Stroke::new(2.0, accent)));
        painter.text(
            rect.min + Vec2::new(6.0, 4.0),
            egui::Align2::LEFT_TOP,
            format!("Live Session Health Trajectory ({n} samples | Ref: 70/90)"),
            FontId::proportional(11.5),
            label_col,
        );
    } else {
        painter.text(
            rect.center(),
            egui::Align2::CENTER_CENTER,
            "Collecting telemetry trend samples...",
            FontId::proportional(12.0),
            label_col,
        );
    }
}

fn format_uptime_hms(secs: f64) -> String {
    if !secs.is_finite() || secs < 0.0 {
        return "Unavailable".to_string();
    }
    let total = secs as u64;
    let days = total / 86_400;
    let hours = (total % 86_400) / 3600;
    let mins = (total % 3600) / 60;
    if days > 0 {
        format!("{days}d {hours:02}h {mins:02}m")
    } else {
        format!("{hours:02}h {mins:02}m")
    }
}

fn measured_val_f64(m: &Metric<f64>) -> f64 {
    match m.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => value.max(0.0),
        _ => 0.0,
    }
}

pub fn draw_system_info_and_health_view(
    ui: &mut egui::Ui,
    snap: &SystemSnapshot,
    cache: &mut PlatformDataCache,
    storage_inspector: &[StorageInspectorEntry],
    theme: &ThemeEngine,
    accent: Color32,
) {
    if cache.volumes.is_empty() {
        cache.volumes = collect_mounted_volumes();
    }
    cache.refresh_health_trends();
    let hw = *cache.hardware.get_or_insert_with(collect_system_hardware);
    let health = HealthScoring::evaluate_snapshot(snap);
    let trend = cache.health_tracker.trend();

    hig_view_header(
        ui,
        "System Information & Health",
        "Hardware profile, live subsystem health diagnostics, unified memory composition, and telemetry trends.",
        accent,
    );

    let startup_disk_summary = if let Some(dev) = storage_inspector
        .iter()
        .find(|s| s.mount_point.as_deref() == Some("/") || s.is_internal)
        .or_else(|| storage_inspector.first())
    {
        format!(
            "{} ({} free)",
            format_bytes(dev.total_bytes),
            format_bytes(dev.available_bytes)
        )
    } else if let Some(vol) = cache
        .volumes
        .iter()
        .find(|v| v.mount_point == "/")
        .or_else(|| cache.volumes.first())
    {
        format!(
            "{} ({} free)",
            format_metric_bytes(&vol.total_bytes),
            format_metric_bytes(&vol.available_bytes)
        )
    } else {
        "Measuring...".to_string()
    };

    let uptime_str = match snap.system.uptime_seconds.reading {
        MetricReading::Measured { value, .. } => format_uptime_hms(value),
        _ => snap.system.uptime_seconds.format_with_unit(0, "s"),
    };

    egui::ScrollArea::vertical()
        .id_salt("system_info_health_scroll")
        .show(ui, |ui| {
                    // Hero "About This System" Hardware & Health Card
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                ui.columns(2, |cols| {
                    cols[0].vertical(|ui| {
                        let about_title = if cfg!(target_os = "macos") {
                            "About This Mac"
                        } else if cfg!(target_os = "linux") {
                            "About This Linux Host"
                        } else {
                            "About This Windows System"
                        };
                        let os_row_label = if cfg!(target_os = "macos") {
                            "macOS / Arch"
                        } else if cfg!(target_os = "linux") {
                            "Linux / Arch"
                        } else {
                            "Windows / Arch"
                        };
                        hig_card_header(
                            ui,
                            about_title,
                            &format!("{} · {} {}", hw.hostname, hw.os_name, hw.os_version),
                            accent,
                        );
                        egui::Grid::new("about_this_mac_grid")
                            .num_columns(2)
                            .spacing([16.0, 5.0])
                            .show(ui, |ui| {
                                ui.label(
                                    RichText::new("Processor").color(ui.visuals().weak_text_color()),
                                );
                                ui.label(
                                    RichText::new(if HostPlatform::current().is_macos() {
                                        format!(
                                            "{} ({} cores: {}P + {}E)",
                                            hw.chip_model, hw.physical_cores, hw.p_cores, hw.e_cores
                                        )
                                    } else {
                                        format!(
                                            "{} ({} physical / {} logical threads)",
                                            hw.chip_model, hw.physical_cores, hw.logical_cores
                                        )
                                    })
                                    .strong(),
                                );
                                ui.end_row();

                                ui.label(
                                    RichText::new("Memory").color(ui.visuals().weak_text_color()),
                                );
                                ui.label(
                                    RichText::new(format!(
                                        "{} {}",
                                        format_bytes(hw.total_ram_bytes),
                                        if cfg!(target_os = "macos") { "Unified" } else { "Physical RAM" }
                                    ))
                                    .strong(),
                                );
                                ui.end_row();

                                ui.label(
                                    RichText::new("Graphics").color(ui.visuals().weak_text_color()),
                                );
                                let gpu_label = if !snap.system.gpu_model.is_empty()
                                    && snap.system.gpu_model != "Windows Display Adapter"
                                {
                                    snap.system.gpu_model.as_str()
                                } else {
                                    hw.gpu_model.as_str()
                                };
                                ui.label(if let Some(cores) = hw.gpu_cores {
                                    format!("{gpu_label} ({cores} cores)")
                                } else {
                                    gpu_label.to_string()
                                });
                                ui.end_row();

                                ui.label(
                                    RichText::new(HostPlatform::current().system_drive_title())
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(&startup_disk_summary);
                                ui.end_row();

                                ui.label(
                                    RichText::new(os_row_label)
                                        .color(ui.visuals().weak_text_color()),
                                );
                                ui.label(format!(
                                    "{} {} ({})",
                                    hw.os_name, hw.os_version, hw.architecture
                                ));
                                ui.end_row();

                                ui.label(
                                    RichText::new("Uptime").color(ui.visuals().weak_text_color()),
                                );
                                ui.label(&uptime_str);
                                ui.end_row();
                            });
                    });

                    cols[1].horizontal(|ui| {
                        draw_health_ring_gauge(ui, health.overall_score, health.overall_level, trend);
                        ui.add_space(10.0);
                        ui.vertical(|ui| {
                            ui.label(
                                RichText::new(format!(
                                    "System Health: {:.0}/100 ({})",
                                    health.overall_score,
                                    health.overall_level.as_str()
                                ))
                                .font(FontId::proportional(15.0))
                                .strong()
                                .color(health_level_color(health.overall_level)),
                            );
                            ui.label(
                                RichText::new(format!(
                                    "Trend: {} · {} session samples",
                                    trend.as_str(),
                                    cache.health_tracker.points().len()
                                ))
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                            );
                            ui.add_space(6.0);
                            let (sev_label, sev_col) = match health.bottleneck.severity {
                                BottleneckSeverity::None => ("Healthy", hig_green(ui)),
                                BottleneckSeverity::Mild => ("Mild Load", hig_blue(ui)),
                                BottleneckSeverity::Moderate => ("Moderate Bottleneck", hig_amber(ui)),
                                BottleneckSeverity::Severe => ("Severe Bottleneck", hig_red(ui)),
                            };
                            ui.horizontal(|ui| {
                                ui.label(RichText::new("Diagnosis:").strong());
                                hig_status_pill(ui, sev_label, sev_col);
                            });
                            ui.label(RichText::new(health.bottleneck.headline.as_str()).strong());
                            ui.label(
                                RichText::new(health.bottleneck.explanation.as_str())
                                    .font(FontId::proportional(11.5))
                                    .color(ui.visuals().weak_text_color()),
                            );
                            if !health.bottleneck.upgrade_advice.is_empty() {
                                ui.label(
                                    RichText::new(format!(
                                        "Tip: {}",
                                        health.bottleneck.upgrade_advice
                                    ))
                                    .font(FontId::proportional(11.5))
                                    .color(accent),
                                );
                            }
                        });
                    });
                });
            });

            ui.add_space(10.0);
            // 5 Subsystem Health Cards
            ui.columns(5, |cols| {
                let subs = [
                    &health.cpu,
                    &health.memory,
                    &health.disk,
                    &health.gpu,
                    &health.thermal,
                ];
                for (i, sub) in subs.iter().enumerate() {
                    hig_card(&mut cols[i], |ui| {
                        ui.set_min_width(ui.available_width());
                        let badge = sub
                            .score
                            .map_or_else(|| "N/A".to_string(), |v| format!("{v:.0}/100"));
                        let lvl = (sub.score.unwrap_or(0.0) / 100.0).clamp(0.0, 1.0);
                        draw_single_vfd_bar(
                            ui,
                            sub.name.as_str(),
                            &badge,
                            lvl,
                            sub.score.is_some(),
                            accent,
                            theme,
                        );
                        ui.add_space(3.0);
                        ui.label(
                            RichText::new(sub.summary.as_str())
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                        );
                    });
                }
            });

            ui.add_space(10.0);
            ui.columns(2, |cols| {
                hig_card(&mut cols[0], |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Top Resource Consumers",
                        "Active processes ranked by combined CPU and memory impact.",
                        accent,
                    );
                    if health.top_consumers.is_empty() {
                        ui.label("No active process consumers in snapshot.");
                    } else {
                        egui::ScrollArea::horizontal()
                            .id_salt("health_top_consumers_scroll")
                            .show(ui, |ui| {
                                egui::Grid::new("health_top_consumers")
                                    .num_columns(5)
                                    .striped(true)
                                    .spacing([10.0, 5.0])
                                    .show(ui, |ui| {
                                        ui.label(RichText::new("Process").strong());
                                        ui.label(RichText::new("PID").strong());
                                        ui.label(RichText::new("CPU %").strong());
                                        ui.label(RichText::new("Memory").strong());
                                        ui.label(RichText::new("Impact").strong());
                                        ui.end_row();
                                        for c in health.top_consumers.iter().take(6) {
                                            let name: String = c.name.chars().take(20).collect();
                                            ui.label(name);
                                            ui.label(c.key.pid.to_string());
                                            ui.label(
                                                c.cpu_percent
                                                    .map_or_else(|| "n/a".into(), |v| format!("{v:.1}%")),
                                            );
                                            ui.label(
                                                c.memory_bytes
                                                    .map_or_else(|| "n/a".into(), |b| format_bytes(b as u64)),
                                            );
                                            ui.horizontal(|ui| {
                                                let frac = (c.impact_score / 100.0).clamp(0.0, 1.0) as f32;
                                                draw_hig_progress_bar(ui, 44.0, 7.0, frac, accent);
                                                ui.label(format!("{:.0}", c.impact_score));
                                            });
                                            ui.end_row();
                                        }
                                    });
                            });
                    }
                });

                hig_card(&mut cols[1], |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.horizontal(|ui| {
                        ui.vertical(|ui| {
                            hig_card_header(
                                ui,
                                "Health Trends",
                                "Composite health, CPU, and memory trajectory over time.",
                                accent,
                            );
                        });
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                            for (idx, lbl) in [(2usize, "30d"), (1, "7d"), (0, "24h")] {
                                if ui
                                    .selectable_label(cache.health_trend_window == idx, lbl)
                                    .clicked()
                                {
                                    cache.health_trend_window = idx;
                                    cache.refresh_health_trends();
                                }
                            }
                        });
                    });
                    draw_health_trend_sparkline(
                        ui,
                        &cache.health_trend_points,
                        &cache.health_tracker,
                        accent,
                    );
                });
            });

            ui.add_space(10.0);
            ui.columns(2, |cols| {
                hig_card(&mut cols[0], |ui| {
                    ui.set_min_width(ui.available_width());
                    let sp = &snap.hardware_telemetry.soc_power;
                    hig_card_header(
                        ui,
                        "Processor & Cache",
                        &format!(
                            "{} · {} physical ({}P + {}E) / {} logical",
                            hw.chip_model, hw.physical_cores, hw.p_cores, hw.e_cores, hw.logical_cores
                        ),
                        accent,
                    );
                    let l3_text =
                        if hw.l3_cache_kb == 0 && hw.architecture.eq_ignore_ascii_case("arm64") {
                            "System Level Cache (Unified)".to_string()
                        } else {
                            format!("{} KiB", hw.l3_cache_kb)
                        };
                    ui.label(format!(
                        "L2 Cache: {} KiB · L3 / SLC: {} · Firmware: {}",
                        hw.l2_cache_kb, l3_text, hw.firmware_version
                    ));
                    ui.label(format!(
                        "Load Average (1m / 5m / 15m): {} / {} / {}",
                        snap.system.load_avg_1m.format_with_unit(2, ""),
                        snap.system.load_avg_5m.format_with_unit(2, ""),
                        snap.system.load_avg_15m.format_with_unit(2, ""),
                    ));
                    ui.add_space(6.0);
                    draw_single_vfd_bar(
                        ui,
                        &format!(
                            "{} ({})",
                            if HostPlatform::current().is_macos() {
                                "P-Cluster"
                            } else {
                                "CPU Package"
                            },
                            sp.p_cluster_mhz.format_with_unit(0, "MHz")
                        ),
                        &sp.p_cluster_active_residency_percent.format_with_unit(1, "%"),
                        measured_val_f64(&sp.p_cluster_active_residency_percent) / 100.0,
                        true,
                        accent,
                        theme,
                    );
                    if HostPlatform::current().is_macos() || hw.e_cores > 0 {
                        ui.add_space(4.0);
                        draw_single_vfd_bar(
                            ui,
                            &format!("E-Cluster ({})", sp.e_cluster_mhz.format_with_unit(0, "MHz")),
                            &sp.e_cluster_active_residency_percent.format_with_unit(1, "%"),
                            measured_val_f64(&sp.e_cluster_active_residency_percent) / 100.0,
                            true,
                            accent,
                            theme,
                        );
                    }
                });

                hig_card(&mut cols[1], |ui| {
                    ui.set_min_width(ui.available_width());
                    let total_b = measured_val_f64(&snap.system.mem_total_bytes)
                        .max(hw.total_ram_bytes as f64)
                        .max(1.0);
                    let active_b = measured_val_f64(&snap.system.mem_active_bytes);
                    let wired_b = measured_val_f64(&snap.system.mem_wired_bytes);
                    let comp_b = measured_val_f64(&snap.system.mem_compressed_bytes);
                    let cached_b = measured_val_f64(&snap.system.mem_cached_files_bytes);
                    let free_b = measured_val_f64(&snap.system.mem_free_bytes);
                    let subtitle = if HostPlatform::current().is_macos() {
                        "Unified LPDDR memory pool shared across CPU, GPU, and Neural Engine."
                    } else {
                        "Physical RAM composition across active, wired, compressed, and cached pages."
                    };
                    hig_card_header(
                        ui,
                        if HostPlatform::current().is_macos() {
                            "Unified Memory Composition"
                        } else {
                            "Physical Memory Composition"
                        },
                        subtitle,
                        accent,
                    );
                    ui.label(format!(
                        "Total: {} ({} used) · Memory Pressure: {}",
                        format_bytes(total_b as u64),
                        snap.system.mem_used_percent.format_with_unit(1, "%"),
                        snap.system.system_pressure_percent.format_with_unit(1, "%"),
                    ));
                    ui.add_space(4.0);
                    let segs = [
                        ("Active", active_b, Color32::from_rgb(10, 132, 255)),
                        ("Wired", wired_b, Color32::from_rgb(255, 159, 10)),
                        ("Compressed", comp_b, Color32::from_rgb(191, 90, 242)),
                        ("Cached", cached_b, Color32::from_rgb(48, 209, 88)),
                        ("Free", free_b, Color32::from_rgb(72, 82, 96)),
                    ];
                    let sum_b: f64 = segs.iter().map(|s| s.1).sum::<f64>().max(total_b);
                    let (bar_rect, _) = ui.allocate_exact_size(
                        Vec2::new(ui.available_width().max(120.0), 16.0),
                        egui::Sense::hover(),
                    );
                    let painter = ui.painter_at(bar_rect);
                    let bar_bg = if ui.visuals().dark_mode {
                        Color32::from_rgb(14, 18, 26)
                    } else {
                        Color32::from_rgb(230, 234, 240)
                    };
                    painter.rect_filled(bar_rect, CornerRadius::same(4), bar_bg);
                    let mut x = bar_rect.min.x;
                    for (_name, bytes, col) in segs {
                        let w = ((bytes / sum_b) as f32) * bar_rect.width();
                        if w > 0.5 {
                            painter.rect_filled(
                                Rect::from_min_size(
                                    Pos2::new(x, bar_rect.min.y),
                                    Vec2::new(w, bar_rect.height()),
                                ),
                                CornerRadius::ZERO,
                                col,
                            );
                            x += w;
                        }
                    }
                    ui.add_space(4.0);
                    ui.horizontal_wrapped(|ui| {
                        for (name, bytes, col) in segs {
                            ui.colored_label(
                                col,
                                format!("* {name}: {}", format_bytes(bytes as u64)),
                            );
                        }
                    });
                    ui.label(format!(
                        "Swap Used: {} / {} ({})",
                        format_metric_bytes(&snap.system.swap_used_bytes),
                        format_metric_bytes(&snap.system.swap_total_bytes),
                        snap.system.swap_used_percent.format_with_unit(1, "%"),
                    ));
                });
            });

            ui.add_space(10.0);
            ui.columns(2, |cols| {
                hig_card(&mut cols[0], |ui| {
                    ui.set_min_width(ui.available_width());
                    let sp = &snap.hardware_telemetry.soc_power;
                    let gpu_label = if !snap.system.gpu_model.is_empty()
                        && snap.system.gpu_model != "Windows Display Adapter"
                    {
                        snap.system.gpu_model.as_str()
                    } else {
                        hw.gpu_model.as_str()
                    };
                    hig_card_header(
                        ui,
                        if HostPlatform::current().is_macos() {
                            "Graphics & Neural Engine"
                        } else {
                            "Graphics & Compute Accelerators"
                        },
                        &if let Some(cores) = hw.gpu_cores {
                            format!("{gpu_label} ({cores} GPU cores)")
                        } else {
                            gpu_label.to_string()
                        },
                        accent,
                    );
                    draw_single_vfd_bar(
                        ui,
                        &format!(
                            "GPU Utilization ({})",
                            sp.gpu_clock_mhz.format_with_unit(0, "MHz")
                        ),
                        &snap.system.gpu_utilization_percent.format_with_unit(1, "%"),
                        measured_val_f64(&snap.system.gpu_utilization_percent) / 100.0,
                        true,
                        accent,
                        theme,
                    );
                    ui.add_space(4.0);
                    ui.label(format!(
                        "GPU Memory In-Use: {} · Total / Allocated: {}",
                        format_metric_bytes(&snap.system.gpu_in_use_memory_bytes),
                        format_metric_bytes(&snap.system.gpu_alloc_memory_bytes),
                    ));
                    if HostPlatform::current().is_macos() {
                        ui.label(format!(
                            "ANE Active Residency: {} · ANE Power: {}",
                            sp.ane_active_residency_percent.format_with_unit(1, "%"),
                            sp.ane_watts.format_with_unit(2, "W"),
                        ));
                    } else {
                        ui.label(format!(
                            "GPU Board Power: {} · Thermal Max: {}",
                            sp.gpu_watts.format_with_unit(1, "W"),
                            snap.hardware_telemetry
                                .thermals
                                .max_die_celsius
                                .format_with_unit(1, "C"),
                        ));
                    }
                });

                hig_card(&mut cols[1], |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Storage Devices & SMART",
                        "Internal NVMe and external volumes with SMART health status.",
                        accent,
                    );
                    for dev in storage_inspector.iter().take(4) {
                        let total = dev.total_bytes.max(1) as f64;
                        let used = dev.total_bytes.saturating_sub(dev.available_bytes) as f64;
                        draw_single_vfd_bar(
                            ui,
                            &format!(
                                "{} [{}] ({}, SMART: {})",
                                dev.model_name, dev.bsd_name, dev.protocol, dev.smart_status
                            ),
                            &format!(
                                "{} free / {}",
                                format_bytes(dev.available_bytes),
                                format_bytes(dev.total_bytes)
                            ),
                            (used / total).clamp(0.0, 1.0),
                            true,
                            accent,
                            theme,
                        );
                        ui.add_space(3.0);
                    }
                    for v in cache.volumes.iter().take(4) {
                        ui.label(format!(
                            "Mount {} ({}): {} free / {}",
                            v.mount_point,
                            v.filesystem,
                            format_metric_bytes(&v.available_bytes),
                            format_metric_bytes(&v.total_bytes),
                        ));
                    }
                });
            });

            ui.add_space(10.0);
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                hig_card_header(
                    ui,
                    "Network Interfaces",
                    "Live throughput and cumulative byte counters across active network adapters.",
                    accent,
                );
                if snap.networks.is_empty() {
                    ui.label("No active network interface metrics available.");
                } else {
                    egui::Grid::new("sysinfo_net_grid")
                        .num_columns(4)
                        .striped(true)
                        .spacing([14.0, 5.0])
                        .show(ui, |ui| {
                            ui.label(RichText::new("Interface").strong());
                            ui.label(RichText::new("Rx / sec").strong());
                            ui.label(RichText::new("Tx / sec").strong());
                            ui.label(RichText::new("Total Rx / Tx").strong());
                            ui.end_row();
                            for net in snap.networks.iter().take(8) {
                                ui.label(net.interface.as_str());
                                ui.label(format!("{}/s", format_metric_bytes(&net.rx_bytes_per_sec)));
                                ui.label(format!("{}/s", format_metric_bytes(&net.tx_bytes_per_sec)));
                                ui.label(format!(
                                    "{} / {}",
                                    format_metric_bytes(&net.rx_bytes_total),
                                    format_metric_bytes(&net.tx_bytes_total)
                                ));
                                ui.end_row();
                            }
                        });
                }
            });
        });
}

pub fn draw_users_view(ui: &mut egui::Ui, cache: &mut PlatformDataCache, accent: Color32) {
    if cache.users.is_empty() {
        cache.users = collect_users();
    }
    let plat = HostPlatform::current();
    hig_view_header(
        ui,
        "User Sessions & Login Seats",
        match plat {
            HostPlatform::MacOs => {
                "Inspect active macOS console, graphical desktop, and terminal or SSH login sessions."
            }
            HostPlatform::Windows => {
                "Inspect active Windows desktop console and remote login sessions."
            }
            HostPlatform::Linux => {
                "Inspect active Linux console, graphical seat, and terminal or SSH login sessions."
            }
        },
        accent,
    );

    let total_sessions = cache.users.len();
    let console_count = cache.users.iter().filter(|u| u.tty == "console").count();
    let terminal_ssh_count = total_sessions.saturating_sub(console_count);
    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: "Logged-In Sessions".to_string(),
                value: total_sessions.to_string(),
                detail: "Total active login seats".to_string(),
                color: accent,
            },
            HigKpiItem {
                label: "Console Seat".to_string(),
                value: console_count.to_string(),
                detail: match plat {
                    HostPlatform::MacOs => "Local macOS WindowServer display".to_string(),
                    HostPlatform::Windows => "Local Windows desktop session".to_string(),
                    HostPlatform::Linux => "Local graphical / console display".to_string(),
                },
                color: hig_blue(ui),
            },
            HigKpiItem {
                label: "Terminal / SSH Sessions".to_string(),
                value: terminal_ssh_count.to_string(),
                detail: "Interactive PTY & remote shells".to_string(),
                color: hig_green(ui),
            },
        ],
    );

    hig_card(ui, |ui| {
        ui.set_min_width(ui.available_width());
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                hig_card_header(
                    ui,
                    "Active Sessions",
                    "Interactive login records reported by the system utmpx database.",
                    accent,
                );
            });
            ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                if ui.button("Refresh").clicked() {
                    cache.users = collect_users();
                }
            });
        });
        egui::Grid::new("users_grid")
            .num_columns(5)
            .striped(true)
            .spacing([18.0, 6.0])
            .show(ui, |ui| {
                ui.label(RichText::new("User").strong());
                ui.label(RichText::new("TTY / Seat").strong());
                ui.label(RichText::new("Host").strong());
                ui.label(RichText::new("Login Time").strong());
                ui.label(RichText::new("Status").strong());
                ui.end_row();
                for u in &cache.users {
                    ui.label(RichText::new(u.username.as_str()).strong());
                    ui.label(u.tty.as_str());
                    ui.label(if u.host.is_empty() {
                        "local"
                    } else {
                        u.host.as_str()
                    });
                    ui.label(u.login_time.as_str());
                    let (badge_txt, badge_col) = if u.tty == "console" {
                        ("Console", hig_blue(ui))
                    } else if !u.host.is_empty() && u.host != "local" && u.host != "-" {
                        ("Remote / SSH", hig_amber(ui))
                    } else {
                        (u.state.as_str(), hig_green(ui))
                    };
                    hig_status_pill(ui, badge_txt, badge_col);
                    ui.end_row();
                }
            });
    });
}

fn draw_power_rail_row(
    ui: &mut egui::Ui,
    label: &str,
    metric: &Metric<f64>,
    scale_max_watts: f64,
    fill: Color32,
) {
    let val = measured_val_f64(metric);
    let frac = (val / scale_max_watts.max(1.0)).clamp(0.0, 1.0) as f32;
    ui.horizontal(|ui| {
        ui.add_sized(
            [125.0, 18.0],
            egui::Label::new(RichText::new(label).strong()),
        );
        draw_hig_progress_bar(ui, 120.0, 8.0, frac, fill);
        ui.label(metric.format_with_unit(2, "W"));
    });
}

pub fn draw_installed_apps_view(
    ui: &mut egui::Ui,
    cache: &mut PlatformDataCache,
    accent: Color32,
) -> bool {
    let mut cols_dirty = false;
    cache.poll_async_workers();
    if cache.installed_apps.is_empty() && !cache.apps_loading {
        cache.trigger_apps_refresh();
    }

    let plat = HostPlatform::current();
    hig_view_header(
        ui,
        "Installed Applications & Storage Footprint",
        match plat {
            HostPlatform::MacOs => {
                "Inspect application bundle sizes, live processor and memory usage, and associated Library support files."
            }
            HostPlatform::Windows => {
                "Inspect installed Windows applications, estimated package sizes, and live processor and memory usage."
            }
            HostPlatform::Linux => {
                "Inspect installed desktop applications, package sizes, and live processor and memory usage."
            }
        },
        accent,
    );

    let total_apps = cache.installed_apps.len();
    let running_apps = cache
        .installed_apps
        .iter()
        .filter(|app| {
            cache
                .app_live_usage
                .get(&app.path)
                .is_some_and(|u| u.cpu_percent > 0.0 || u.memory_bytes > 0)
        })
        .count();
    let measured_count = cache
        .installed_apps
        .iter()
        .filter(|a| a.size_bytes.is_some())
        .count();
    let combined_bytes: u64 = cache
        .installed_apps
        .iter()
        .filter_map(|a| a.size_bytes)
        .sum();
    let footprint_val = if measured_count == 0 && cache.apps_loading {
        "Measuring...".to_string()
    } else {
        format_bytes(combined_bytes)
    };
    let footprint_detail = if measured_count < total_apps {
        format!("Measured {measured_count} of {total_apps} applications")
    } else {
        match plat {
            HostPlatform::MacOs => "Across /Applications & ~/Applications".to_string(),
            HostPlatform::Windows => "Across Windows Uninstall Registry".to_string(),
            HostPlatform::Linux => "Across /usr/share/applications".to_string(),
        }
    };

    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: "Installed Apps".to_string(),
                value: total_apps.to_string(),
                detail: match plat {
                    HostPlatform::MacOs => "Discovered .app bundles".to_string(),
                    HostPlatform::Windows => "Registered Windows applications".to_string(),
                    HostPlatform::Linux => "Discovered desktop applications".to_string(),
                },
                color: accent,
            },
            HigKpiItem {
                label: "Running Now".to_string(),
                value: running_apps.to_string(),
                detail: "Matched against live processes".to_string(),
                color: hig_green(ui),
            },
            HigKpiItem {
                label: "Combined Footprint".to_string(),
                value: footprint_val,
                detail: footprint_detail,
                color: hig_blue(ui),
            },
        ],
    );

    ui.horizontal_wrapped(|ui| {
        ui.label(RichText::new("Search:").strong());
        ui.add(
            egui::TextEdit::singleline(&mut cache.apps_filter)
                .hint_text("App name or identifier...")
                .desired_width(220.0),
        );
        if ui.button("Rescan").clicked() {
            cache.trigger_apps_refresh();
        }
        if ui
            .selectable_label(cache.app_columns_open, "Configure Columns...")
            .clicked()
        {
            cache.app_columns_open = !cache.app_columns_open;
        }
        if plat.is_macos() {
            if ui
                .button("Open Full Disk Access")
                .on_hover_text(
                    "Grant Full Disk Access in System Settings › Privacy & Security so Nexus can inspect protected ~/Library/Containers sizes",
                )
                .clicked()
            {
                open_macos_privacy_pane(
                    "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles",
                );
            }
            if ui
                .button("Open App Management")
                .on_hover_text(
                    "Grant App Management in System Settings › Privacy & Security so Nexus can move protected app bundles to Trash",
                )
                .clicked()
            {
                open_macos_privacy_pane(
                    "x-apple.systempreferences:com.apple.preference.security?Privacy_AppBundles",
                );
            }
        }
        if cache.apps_loading {
            hig_status_pill(ui, "Scanning...", hig_blue(ui));
        }
    });
    if cache.app_columns_open
        && draw_configure_columns_panel(
            ui,
            "apps_cols",
            &mut cache.app_columns,
            default_app_columns,
            accent,
        )
    {
        cols_dirty = true;
    }
    if !cache.app_uninstall_status.is_empty() {
        ui.add_space(4.0);
        ui.label(RichText::new(&cache.app_uninstall_status).color(accent));
    }
    ui.add_space(6.0);

    let q = cache.apps_filter.trim().to_ascii_lowercase();
    let mut inspect_idx: Option<usize> = None;
    let max_app_bytes = cache
        .installed_apps
        .iter()
        .filter_map(|a| a.size_bytes)
        .max()
        .unwrap_or(1)
        .max(1) as f64;
    let visible_cols: Vec<TableColumnConfig> = cache
        .app_columns
        .iter()
        .filter(|c| c.style != ColumnDisplayStyle::Hidden)
        .cloned()
        .collect();

    ui.columns(2, |cols| {
        hig_card(&mut cols[0], |ui| {
            ui.set_min_width(ui.available_width());
            let apps_card_sub = match plat {
                HostPlatform::MacOs => {
                    "Click any app to inspect its bundle footprint and ~/Library support files."
                }
                HostPlatform::Windows => {
                    "Click any application to inspect its installation path, registry size, and AppData support files."
                }
                HostPlatform::Linux => {
                    "Click any application to inspect its package path, footprint, and XDG config/cache files."
                }
            };
            hig_card_header(
                ui,
                "Applications",
                apps_card_sub,
                accent,
            );
            egui::ScrollArea::both()
                .id_salt("apps_list_scroll")
                .auto_shrink([false, false])
                .max_height((ui.available_height() - 16.0).max(240.0))
                .show(ui, |ui| {
                    egui::Grid::new("apps_grid")
                        .num_columns(visible_cols.len().max(1))
                        .striped(true)
                        .spacing([10.0, 5.0])
                        .show(ui, |ui| {
                            for c in &visible_cols {
                                ui.label(RichText::new(c.label.as_str()).strong());
                            }
                            ui.end_row();
                            for (idx, app) in cache
                                .installed_apps
                                .iter()
                                .enumerate()
                                .filter(|(_, a)| {
                                    q.is_empty()
                                        || a.name.to_ascii_lowercase().contains(&q)
                                        || a.bundle_id.to_ascii_lowercase().contains(&q)
                                })
                                .take(180)
                            {
                                let selected = cache.selected_app_idx == Some(idx);
                                let usage = cache.app_live_usage.get(&app.path);
                                let is_running = usage
                                    .is_some_and(|u| u.cpu_percent > 0.0 || u.memory_bytes > 0);
                                for c in &visible_cols {
                                    match c.id.as_str() {
                                        "name" => {
                                            ui.horizontal(|ui| {
                                                if ui
                                                    .selectable_label(selected, app.name.as_str())
                                                    .clicked()
                                                {
                                                    inspect_idx = Some(idx);
                                                }
                                                if is_running {
                                                    hig_status_pill(ui, "Running", hig_green(ui));
                                                }
                                            });
                                        }
                                        "version" => {
                                            ui.label(app.version.as_str());
                                        }
                                        "size" => {
                                            let sz_str = app.size_bytes.map(format_bytes).unwrap_or_else(|| {
                                                if plat.is_macos() {
                                                    "Measuring...".to_string()
                                                } else {
                                                    "Unknown".to_string()
                                                }
                                            });
                                            if c.style == ColumnDisplayStyle::HorizontalBar {
                                                let frac = (app.size_bytes.unwrap_or(0) as f64
                                                    / max_app_bytes)
                                                    as f32;
                                                draw_inline_hbar(ui, frac, &sz_str, accent);
                                            } else {
                                                ui.label(sz_str);
                                            }
                                        }
                                        "cpu" => {
                                            let cpu_pct = usage.map_or(0.0, |u| u.cpu_percent);
                                            let lbl = format!("{cpu_pct:.1}%");
                                            match c.style {
                                                ColumnDisplayStyle::LineChart => {
                                                    if let Some(u) = usage {
                                                        draw_inline_sparkline(
                                                            ui,
                                                            &u.cpu_history,
                                                            false,
                                                            &lbl,
                                                            accent,
                                                        );
                                                    } else {
                                                        ui.label(lbl);
                                                    }
                                                }
                                                ColumnDisplayStyle::LineChartScaled100 => {
                                                    if let Some(u) = usage {
                                                        draw_inline_sparkline(
                                                            ui,
                                                            &u.cpu_history,
                                                            true,
                                                            &lbl,
                                                            accent,
                                                        );
                                                    } else {
                                                        ui.label(lbl);
                                                    }
                                                }
                                                ColumnDisplayStyle::HorizontalBar => {
                                                    draw_inline_hbar(
                                                        ui,
                                                        (cpu_pct / 100.0) as f32,
                                                        &lbl,
                                                        accent,
                                                    );
                                                }
                                                _ => {
                                                    ui.label(lbl);
                                                }
                                            }
                                        }
                                        "mem" => {
                                            let mem_b = usage.map_or(0, |u| u.memory_bytes);
                                            let total_ram = cache.hardware.map_or(
                                                16_u64 * 1024 * 1024 * 1024,
                                                |h| h.total_ram_bytes.max(1),
                                            );
                                            let lbl = format_bytes(mem_b);
                                            if c.style == ColumnDisplayStyle::HorizontalBar {
                                                let frac =
                                                    (mem_b as f64 / total_ram as f64) as f32;
                                                draw_inline_hbar(ui, frac, &lbl, accent);
                                            } else {
                                                ui.label(lbl);
                                            }
                                        }
                                        "bundle_id" => {
                                            ui.label(
                                                RichText::new(app.bundle_id.as_str())
                                                    .color(ui.visuals().weak_text_color()),
                                            );
                                        }
                                        "inspect" => {
                                            if ui.small_button("Inspect").clicked() {
                                                inspect_idx = Some(idx);
                                            }
                                        }
                                        _ => {
                                            ui.label("—");
                                        }
                                    }
                                }
                                ui.end_row();
                            }
                        });
                });
        });

        hig_card(&mut cols[1], |ui| {
            ui.set_min_width(ui.available_width());
            let (uninst_title, uninst_sub) = match plat {
                HostPlatform::MacOs => (
                    "App Footprint & Safe Trash Uninstaller",
                    "Compare the .app bundle size against ~/Library caches and containers before moving items to Trash.",
                ),
                HostPlatform::Windows => (
                    "App Footprint & Uninstaller",
                    "Inspect the application install footprint and AppData support files before moving items to Recycle Bin.",
                ),
                HostPlatform::Linux => (
                    "App Footprint & Uninstaller",
                    "Inspect the application footprint and XDG config/cache files before moving items to Trash.",
                ),
            };
            hig_card_header(
                ui,
                uninst_title,
                uninst_sub,
                accent,
            );
            if let Some(idx) = cache.selected_app_idx
                && let Some(app) = cache.installed_apps.get(idx).cloned()
            {
                let bundle_bytes = app.size_bytes.unwrap_or(0);
                let selected_related_bytes: u64 = cache
                    .selected_app_related
                    .iter()
                    .filter(|(_, checked)| *checked)
                    .map(|(r, _)| r.size_bytes)
                    .sum();
                let total_footprint = bundle_bytes.saturating_add(selected_related_bytes);

                ui.label(
                    RichText::new(format!("{} (v{})", app.name, app.version))
                        .font(FontId::proportional(14.0))
                        .strong(),
                );
                ui.label(
                    RichText::new(format!("{} · {}", app.bundle_id, app.path))
                        .font(FontId::proportional(11.5))
                        .color(ui.visuals().weak_text_color()),
                );
                ui.add_space(6.0);

                // Visual stacked App vs Support bar
                let (bar_rect, _) = ui.allocate_exact_size(
                    Vec2::new(ui.available_width().max(120.0), 14.0),
                    egui::Sense::hover(),
                );
                let painter = ui.painter_at(bar_rect);
                let track = if ui.visuals().dark_mode {
                    Color32::from_rgba_unmultiplied(255, 255, 255, 22)
                } else {
                    Color32::from_rgba_unmultiplied(15, 23, 42, 18)
                };
                painter.rect_filled(bar_rect, CornerRadius::same(4), track);
                let denom = (total_footprint.max(1)) as f64;
                let bundle_w = ((bundle_bytes as f64 / denom) as f32) * bar_rect.width();
                let lib_w = ((selected_related_bytes as f64 / denom) as f32) * bar_rect.width();
                let bundle_col = hig_blue(ui);
                let lib_col = hig_amber(ui);
                if bundle_w > 0.5 {
                    painter.rect_filled(
                        Rect::from_min_size(bar_rect.min, Vec2::new(bundle_w, bar_rect.height())),
                        CornerRadius::same(3),
                        bundle_col,
                    );
                }
                if lib_w > 0.5 {
                    painter.rect_filled(
                        Rect::from_min_size(
                            Pos2::new(bar_rect.min.x + bundle_w, bar_rect.min.y),
                            Vec2::new(lib_w, bar_rect.height()),
                        ),
                        CornerRadius::same(3),
                        lib_col,
                    );
                }
                ui.add_space(4.0);
                let (bundle_lbl, support_lbl) = match plat {
                    HostPlatform::MacOs => ("* .app Bundle", "* ~/Library Support"),
                    HostPlatform::Windows => ("* Application Size", "* AppData Support"),
                    HostPlatform::Linux => ("* Package Size", "* XDG Config/Cache"),
                };
                ui.horizontal_wrapped(|ui| {
                    ui.colored_label(
                        bundle_col,
                        format!("{bundle_lbl}: {}", format_bytes(bundle_bytes)),
                    );
                    ui.colored_label(
                        lib_col,
                        format!(
                            "{support_lbl}: {}",
                            format_bytes(selected_related_bytes)
                        ),
                    );
                    ui.label(
                        RichText::new(format!(
                            "Total Reclaimable: {}",
                            format_bytes(total_footprint)
                        ))
                        .strong(),
                    );
                });
                ui.separator();

                egui::ScrollArea::vertical()
                    .id_salt("app_related_scroll")
                    .max_height((ui.available_height() - 56.0).max(120.0))
                    .show(ui, |ui| {
                        if cache.selected_app_related.is_empty() {
                            if plat.is_macos() {
                                ui.label("No matching ~/Library support, cache, or container items found.");
                                ui.label(
                                    RichText::new("Tip: If this app uses a sandboxed container under ~/Library/Containers, click 'Open Full Disk Access' above and add Nexus System Monitor so macOS allows reading container sizes.")
                                        .font(FontId::proportional(12.0))
                                        .color(ui.visuals().weak_text_color()),
                                );
                            } else {
                                ui.label("No matching user support or cache directories found.");
                            }
                        }
                        for (rel, checked) in &mut cache.selected_app_related {
                            ui.checkbox(
                                checked,
                                format!(
                                    "[{}] {} ({})",
                                    rel.category,
                                    rel.path,
                                    format_bytes(rel.size_bytes)
                                ),
                            );
                        }
                    });
                ui.separator();
                let move_btn_label = format!(
                    "Move App + {} Selected Related to {}",
                    cache.selected_app_related.iter().filter(|(_, c)| *c).count(),
                    plat.trash_noun()
                );
                let move_btn_tip = match plat {
                    HostPlatform::MacOs => {
                        "Moves the .app bundle and checked ~/Library support items to ~/.Trash (recoverable via Finder)."
                    }
                    HostPlatform::Windows => {
                        "Moves the selected application files to the Recycle Bin (recoverable via File Explorer)."
                    }
                    HostPlatform::Linux => {
                        "Moves the selected application files to the user Trash folder."
                    }
                };
                if ui
                    .button(move_btn_label)
                    .on_hover_text(move_btn_tip)
                    .clicked()
                {
                    let paths: Vec<PathBuf> = cache
                        .selected_app_related
                        .iter()
                        .filter(|(_, c)| *c)
                        .map(|(r, _)| PathBuf::from(r.path.as_str()))
                        .collect();
                    match uninstall_app_to_trash(&app, &paths) {
                        Ok(receipt) => {
                            cache.app_uninstall_status = format!(
                                "Moved {} item(s) for '{}' to {} ({} failed)",
                                receipt.trashed_paths.len(),
                                receipt.app_name,
                                plat.trash_noun(),
                                receipt.failed_paths.len()
                            );
                            cache.selected_app_idx = None;
                            cache.selected_app_related.clear();
                            cache.trigger_apps_refresh();
                        }
                        Err(err) => {
                            cache.app_uninstall_status = err;
                        }
                    }
                }
            } else {
                let empty_hint = match plat {
                    HostPlatform::MacOs => {
                        "Select an application from the list on the left to inspect its bundle size, ~/Library footprint, and Trash uninstall options."
                    }
                    HostPlatform::Windows => {
                        "Select an application from the list on the left to inspect its installation size, AppData footprint, and Recycle Bin options."
                    }
                    HostPlatform::Linux => {
                        "Select an application from the list on the left to inspect its package footprint and Trash options."
                    }
                };
                ui.label(empty_hint);
            }
        });
    });

    if let Some(idx) = inspect_idx
        && let Some(app) = cache.installed_apps.get(idx)
    {
        cache.selected_app_idx = Some(idx);
        let rel = resolve_related_files(app);
        cache.selected_app_related = rel.into_iter().map(|r| (r, true)).collect();
    }
    cols_dirty
}

pub fn draw_drivers_view(ui: &mut egui::Ui, cache: &mut PlatformDataCache, accent: Color32) {
    cache.poll_async_workers();
    if cache.drivers.is_empty() && !cache.drivers_loading {
        cache.trigger_drivers_refresh();
    }
    let plat = HostPlatform::current();

    hig_view_header(
        ui,
        match plat {
            HostPlatform::MacOs => "Kernel & DriverKit Extensions",
            HostPlatform::Windows => "Kernel Drivers & System Modules",
            HostPlatform::Linux => "Loaded Kernel Modules",
        },
        match plat {
            HostPlatform::MacOs => {
                "Inspect loaded macOS kernel extensions (.kext) and user-space DriverKit system extensions (.dext)."
            }
            HostPlatform::Windows => {
                "Inspect installed Windows kernel drivers (.sys) and system driver packages."
            }
            HostPlatform::Linux => {
                "Inspect loaded Linux kernel modules (/proc/modules and /sys/module)."
            }
        },
        accent,
    );

    let total_ext = cache.drivers.len();
    let apple_kexts = cache
        .drivers
        .iter()
        .filter(|d| d.scope == DriverScope::AppleSystem && d.kind == DriverKind::KernelExtension)
        .count();
    let third_party_or_dext = cache
        .drivers
        .iter()
        .filter(|d| d.scope == DriverScope::ThirdParty || d.kind == DriverKind::SystemExtension)
        .count();
    let sip_protected = cache
        .drivers
        .iter()
        .filter(|d| d.scope == DriverScope::AppleSystem)
        .count();

    draw_hig_kpi_strip(
        ui,
        &[
            HigKpiItem {
                label: if plat.is_macos() {
                    "Total Extensions".to_string()
                } else {
                    "Total Drivers".to_string()
                },
                value: total_ext.to_string(),
                detail: match plat {
                    HostPlatform::MacOs => "kextstat + DriverKit inventory".to_string(),
                    HostPlatform::Windows => "System32\\drivers inventory".to_string(),
                    HostPlatform::Linux => "/proc/modules inventory".to_string(),
                },
                color: accent,
            },
            HigKpiItem {
                label: match plat {
                    HostPlatform::MacOs => "Apple System Kexts".to_string(),
                    HostPlatform::Windows => "Windows System Drivers".to_string(),
                    HostPlatform::Linux => "Built-In Kernel Modules".to_string(),
                },
                value: apple_kexts.to_string(),
                detail: "Built-in OS kernel modules".to_string(),
                color: hig_blue(ui),
            },
            HigKpiItem {
                label: if plat.is_macos() {
                    "Third-Party / DriverKit".to_string()
                } else {
                    "Loaded / Third-Party".to_string()
                },
                value: third_party_or_dext.to_string(),
                detail: "Active kernel & vendor modules".to_string(),
                color: hig_amber(ui),
            },
            HigKpiItem {
                label: if plat.is_macos() {
                    "SIP Protected".to_string()
                } else {
                    "System Protected".to_string()
                },
                value: sip_protected.to_string(),
                detail: "Protected OS kernel components".to_string(),
                color: hig_green(ui),
            },
        ],
    );

    if plat.is_macos() {
        let _ = draw_permission_guidance_card(
            ui,
            "SIP & DriverKit Policy",
            hig_blue(ui),
            "Most macOS drivers cannot be unloaded while running",
            "Apple locks built-in kernel extensions with System Integrity Protection (SIP) and manages DriverKit extensions through System Settings.",
            &[
                "Click Open Login Items & Extensions to toggle user-space DriverKit extensions.",
                "To unload an eligible third-party kext with 0 active dependencies, run:",
            ],
            Some("sudo kextunload -b <BundleID>"),
            Some((
                "Open Login Items & Extensions",
                "x-apple.systempreferences:com.apple.LoginItems-Settings.extension",
            )),
            false,
            None,
        );
    }
    if !cache.driver_refusal_status.is_empty() {
        ui.add_space(4.0);
        hig_card(ui, |ui| {
            ui.set_min_width(ui.available_width());
            ui.label(
                RichText::new(&cache.driver_refusal_status)
                    .strong()
                    .color(hig_amber(ui)),
            );
        });
    }
    ui.add_space(8.0);

    let q = cache.drivers_filter.trim().to_ascii_lowercase();
    let mut check_refusal_idx: Option<usize> = None;

    hig_card(ui, |ui| {
        ui.set_min_width(ui.available_width());
        ui.horizontal_wrapped(|ui| {
            ui.label(RichText::new("Search:").strong());
            ui.add(
                egui::TextEdit::singleline(&mut cache.drivers_filter)
                    .hint_text("Bundle ID or driver state...")
                    .desired_width(220.0),
            );
            if ui.button("Refresh").clicked() {
                cache.trigger_drivers_refresh();
            }
            if cache.drivers_loading {
                hig_status_pill(ui, "Scanning...", hig_blue(ui));
            }
        });
        ui.add_space(6.0);

        egui::ScrollArea::both()
            .id_salt("drivers_scroll")
            .max_height((ui.available_height() - 12.0).max(200.0))
            .show(ui, |ui| {
                egui::Grid::new("drivers_grid")
                    .num_columns(7)
                    .striped(true)
                    .spacing([12.0, 5.0])
                    .show(ui, |ui| {
                        ui.label(RichText::new("Classification").strong());
                        ui.label(RichText::new("Protection / State").strong());
                        ui.label(RichText::new("Bundle / Driver ID").strong());
                        ui.label(RichText::new("Version").strong());
                        ui.label(RichText::new("Deps").strong());
                        ui.label(RichText::new("Size / Location").strong());
                        ui.label(RichText::new("Unload Check").strong());
                        ui.end_row();
                        for (idx, d) in cache
                            .drivers
                            .iter()
                            .enumerate()
                            .filter(|(_, d)| {
                                q.is_empty()
                                    || d.name.to_ascii_lowercase().contains(&q)
                                    || d.state.as_str().to_ascii_lowercase().contains(&q)
                            })
                            .take(200)
                        {
                            let (origin_label, origin_col) = match (d.scope, d.kind, plat) {
                                (
                                    DriverScope::AppleSystem,
                                    DriverKind::KernelExtension,
                                    HostPlatform::MacOs,
                                ) => ("Apple System Kext", hig_blue(ui)),
                                (
                                    DriverScope::AppleSystem,
                                    DriverKind::SystemExtension,
                                    HostPlatform::MacOs,
                                ) => ("Apple DriverKit", hig_blue(ui)),
                                (
                                    DriverScope::ThirdParty,
                                    DriverKind::SystemExtension,
                                    HostPlatform::MacOs,
                                ) => ("Third-Party DriverKit", hig_amber(ui)),
                                (
                                    DriverScope::ThirdParty,
                                    DriverKind::KernelExtension,
                                    HostPlatform::MacOs,
                                ) => ("Third-Party Kext", hig_amber(ui)),
                                (_, _, HostPlatform::Windows) => {
                                    ("Windows Kernel Driver (.sys)", hig_blue(ui))
                                }
                                (_, _, HostPlatform::Linux) => {
                                    ("Linux Kernel Module", hig_blue(ui))
                                }
                            };
                            hig_status_pill(ui, origin_label, origin_col);

                            let (state_label, state_col) = if d.scope == DriverScope::AppleSystem {
                                (
                                    if plat.is_macos() {
                                        "SIP Protected"
                                    } else {
                                        "OS Protected"
                                    },
                                    hig_blue(ui),
                                )
                            } else if d.state == DriverState::Loaded
                                || d.state == DriverState::ActivatedEnabled
                            {
                                (d.state.as_str(), hig_green(ui))
                            } else {
                                (d.state.as_str(), hig_gray(ui))
                            };
                            hig_status_pill(ui, state_label, state_col);

                            ui.add_sized([220.0, 20.0], egui::Label::new(d.name.as_str()).truncate())
                                .on_hover_text(d.name.as_str());
                            ui.label(d.version.as_str());
                            ui.label(if d.dependencies.is_empty() {
                                "—".to_string()
                            } else {
                                format!("{}", d.dependencies.len())
                            });
                            let loc_str = if d.kind == DriverKind::KernelExtension && d.loaded {
                                format!("{} ({})", format_bytes(d.size_bytes), d.address)
                            } else {
                                d.bundle_path
                                    .as_deref()
                                    .unwrap_or(d.address.as_str())
                                    .to_string()
                            };
                            ui.add_sized([180.0, 20.0], egui::Label::new(&loc_str).truncate())
                                .on_hover_text(loc_str);
                            if ui
                                .small_button("Inspect Unload")
                                .on_hover_text("Explain whether SIP, DriverKit ownership, or dependency links block unloading.")
                                .clicked()
                            {
                                check_refusal_idx = Some(idx);
                            }
                            ui.end_row();
                        }
                    });
            });
    });

    if let Some(idx) = check_refusal_idx
        && let Some(d) = cache.drivers.get(idx)
    {
        cache.driver_refusal_status = driver_unload_refusal(d).unwrap_or_else(|| {
            format!(
                "Eligible for privileged kextunload -b {} (third-party loaded kext with 0 dependencies).",
                d.name
            )
        });
    }
}

pub fn draw_power_and_freq_view(
    ui: &mut egui::Ui,
    snap: &SystemSnapshot,
    sleep_preventing: &mut bool,
    perf_profiles: &mut PerformanceProfileService,
    status_out: &mut String,
    accent: Color32,
) {
    let sp = &snap.hardware_telemetry.soc_power;
    let plat = HostPlatform::current();
    hig_view_header(
        ui,
        "Power Rails, Clocks & Energy Profiles",
        match plat {
            HostPlatform::MacOs => {
                "Real-time Apple Silicon power draw across CPU, GPU, Neural Engine, and DRAM rails, plus sleep prevention and performance profiles."
            }
            HostPlatform::Windows => {
                "Real-time GPU board power, processor and graphics clock frequencies, sleep prevention, and guided performance profiles."
            }
            HostPlatform::Linux => {
                "Real-time processor and GPU power rails, sysfs cpufreq clock frequencies, sleep prevention, and guided performance profiles."
            }
        },
        accent,
    );

    let max_rail_w = measured_val_f64(&sp.package_watts)
        .max(
            measured_val_f64(&sp.cpu_watts)
                + measured_val_f64(&sp.gpu_watts)
                + measured_val_f64(&sp.ane_watts)
                + measured_val_f64(&sp.dram_watts),
        )
        .max(measured_val_f64(&sp.system_power_in_watts))
        .max(20.0);

    ui.columns(2, |cols| {
        cols[0].vertical(|ui| {
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                hig_card_header(
                    ui,
                    match plat {
                        HostPlatform::MacOs => "Apple Silicon Power Rails & Clocks",
                        HostPlatform::Windows => "Processor & GPU Power Rails & Clocks",
                        HostPlatform::Linux => "System Power Rails & Clocks",
                    },
                    match plat {
                        HostPlatform::MacOs => {
                            "Live IOReport rail telemetry scaled against total package power draw."
                        }
                        HostPlatform::Windows => {
                            "Live NVML / Win32 power and clock telemetry scaled against active board power draw."
                        }
                        HostPlatform::Linux => {
                            "Live Linux hwmon and sysfs cpufreq telemetry scaled against system power draw."
                        }
                    },
                    accent,
                );
                draw_power_rail_row(ui, "CPU Rail", &sp.cpu_watts, max_rail_w, hig_blue(ui));
                draw_power_rail_row(ui, "GPU Rail", &sp.gpu_watts, max_rail_w, hig_green(ui));
                if plat.is_macos() {
                    draw_power_rail_row(
                        ui,
                        "Neural Engine (ANE)",
                        &sp.ane_watts,
                        max_rail_w,
                        hig_amber(ui),
                    );
                }
                draw_power_rail_row(ui, "DRAM Rail", &sp.dram_watts, max_rail_w, accent);
                if plat.is_macos() {
                    draw_power_rail_row(
                        ui,
                        "PMP / SoC Aux",
                        &sp.pmp_power_watts,
                        max_rail_w,
                        hig_gray(ui),
                    );
                }
                draw_power_rail_row(
                    ui,
                    "Package / Board Total",
                    &sp.package_watts,
                    max_rail_w,
                    hig_red(ui),
                );
                draw_power_rail_row(
                    ui,
                    "System Power In",
                    &sp.system_power_in_watts,
                    max_rail_w,
                    accent,
                );

                ui.add_space(8.0);
                ui.label(
                    RichText::new(if plat.is_macos() {
                        "DVFS Clocks & SoC Residency"
                    } else {
                        "Clock Frequencies & Active Provenance"
                    })
                    .strong(),
                );
                ui.add_space(4.0);
                ui.horizontal_wrapped(|ui| {
                    if plat.is_macos() {
                        hig_status_pill(
                            ui,
                            &format!("E-Cluster: {}", sp.e_cluster_mhz.format_with_unit(0, "MHz")),
                            hig_green(ui),
                        );
                        hig_status_pill(
                            ui,
                            &format!("P-Cluster: {}", sp.p_cluster_mhz.format_with_unit(0, "MHz")),
                            hig_blue(ui),
                        );
                    } else {
                        hig_status_pill(
                            ui,
                            &format!("CPU Clock: {}", sp.p_cluster_mhz.format_with_unit(0, "MHz")),
                            hig_blue(ui),
                        );
                    }
                    hig_status_pill(
                        ui,
                        &format!("GPU Clock: {}", sp.gpu_clock_mhz.format_with_unit(0, "MHz")),
                        accent,
                    );
                    if plat.is_macos() {
                        hig_status_pill(
                            ui,
                            &format!(
                                "SoC Floor: {}",
                                sp.soc_floor_residency_percent.format_with_unit(1, "%")
                            ),
                            hig_amber(ui),
                        );
                    } else {
                        hig_status_pill(ui, plat.power_telemetry_badge(), hig_amber(ui));
                    }
                });
                if !sp.cluster_clocks.is_empty() {
                    ui.add_space(6.0);
                    for cluster in &sp.cluster_clocks {
                        ui.label(format!(
                            "• {}: {} ({})",
                            cluster.cluster_name,
                            cluster.effective_mhz.format_with_unit(0, "MHz"),
                            cluster.active_residency_percent.format_with_unit(1, "%"),
                        ));
                    }
                }
            });

            ui.add_space(10.0);
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        hig_card_header(
                            ui,
                            if plat.is_macos() {
                                "Sleep Prevention (caffeinate -di)"
                            } else {
                                "System Sleep & Idle Prevention"
                            },
                            "Prevents your display and system from sleeping during long builds or recordings.",
                            accent,
                        );
                    });
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                        if *sleep_preventing {
                            hig_status_pill(ui, "Awake Locked", hig_green(ui));
                        } else {
                            hig_status_pill(ui, "Normal Sleep", hig_gray(ui));
                        }
                    });
                });
                ui.checkbox(
                    sleep_preventing,
                    match plat {
                        HostPlatform::MacOs => "Keep Mac Awake (caffeinate -di)",
                        HostPlatform::Windows => {
                            "Keep Windows PC Awake (SetThreadExecutionState)"
                        }
                        HostPlatform::Linux => "Keep Linux Host Awake (Inhibit Idle Sleep)",
                    },
                );
            });
        });

        hig_card(&mut cols[1], |ui| {
            ui.set_min_width(ui.available_width());
            hig_card_header(
                ui,
                "Guided Performance Profiles",
                "Apply reversible process scheduling presets or restore prior nice priorities.",
                accent,
            );
            ui.horizontal(|ui| {
                ui.label(RichText::new("Active Profile:").strong());
                if let Some(name) = perf_profiles.active_profile_name() {
                    hig_status_pill(ui, name, hig_green(ui));
                } else {
                    hig_status_pill(ui, "None", hig_gray(ui));
                }
                ui.label(
                    RichText::new(format!(
                        "Pending Restores: {}",
                        perf_profiles.pending_restores.len()
                    ))
                    .color(ui.visuals().weak_text_color()),
                );
            });
            ui.add_space(6.0);

            for idx in 0..perf_profiles.profiles.len() {
                let p_id = perf_profiles.profiles[idx].id.clone();
                let p_name = perf_profiles.profiles[idx].name.clone();
                let p_desc = perf_profiles.profiles[idx].description.clone();
                let is_active = perf_profiles
                    .active_profile_id
                    .as_ref()
                    .is_some_and(|cur| cur.as_str() == p_id.as_str());

                egui::Frame::new()
                    .fill(ui.visuals().faint_bg_color)
                    .stroke(Stroke::new(
                        1.0,
                        if is_active {
                            accent
                        } else {
                            ui.visuals().widgets.noninteractive.bg_stroke.color
                        },
                    ))
                    .corner_radius(CornerRadius::same(8))
                    .inner_margin(egui::Margin::symmetric(12, 10))
                    .show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(RichText::new(p_name.as_str()).strong());
                            if is_active {
                                hig_status_pill(ui, "Active", hig_green(ui));
                            }
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if is_active {
                                        if ui.button("Deactivate & Restore").clicked() {
                                            let res = perf_profiles.deactivate(snap);
                                            *status_out = format!(
                                                "Deactivated profile '{}': restored {} receipts",
                                                p_name,
                                                res.len()
                                            );
                                        }
                                    } else if ui.button("Activate").clicked() {
                                        match perf_profiles.activate(
                                            p_id.as_str(),
                                            snap,
                                            1_700_000_000_000,
                                        ) {
                                            Ok(evs) => {
                                                *status_out = format!(
                                                    "Activated profile '{}': {} process overrides queued",
                                                    p_name,
                                                    evs.len()
                                                );
                                            }
                                            Err(e) => {
                                                *status_out =
                                                    format!("Profile activation refused: {e}");
                                            }
                                        }
                                    }
                                },
                            );
                        });
                        ui.label(
                            RichText::new(p_desc.as_str())
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                        );
                    });
                ui.add_space(6.0);
            }
        });
    });
}

#[allow(clippy::too_many_arguments)]
pub fn draw_settings_view(
    ui: &mut egui::Ui,
    theme: &mut ThemeEngine,
    color_wheel: &mut ColorWheelState,
    saved_custom_presets: &mut Vec<ThemePresetMetadata>,
    preset_json_buf: &mut String,
    quiet_hours: &mut QuietHoursService,
    autobalance: &mut AutoBalanceService,
    idle_throttle: &mut IdleThrottleService,
    alerts_cpu_thresh: &mut f64,
    alerts_mem_thresh: &mut f64,
    alerts_temp_thresh: &mut f64,
    alerts_disk_thresh: &mut f64,
    alerts_webhook_buf: &mut String,
    runtime_state: &AutomationRuntimeState,
    remote_server: &mut Option<RemoteServerHandle>,
    remote_allow_cgnat: &mut bool,
    remote_advertise_bonjour: &mut bool,
    tokio_handle: &tokio::runtime::Handle,
    status_out: &mut String,
    accent: Color32,
) -> bool {
    let mut dirty = false;
    hig_view_header(
        ui,
        "Settings & Appearance",
        "Customize themes, accent colors, background automation, alert thresholds, and encrypted companion remote streaming.",
        accent,
    );

    egui::ScrollArea::vertical()
        .id_salt("settings_view_scroll")
        .show(ui, |ui| {
            ui.columns(2, |cols| {
                cols[0].vertical(|ui| {
                    // Card 1: Appearance & 20 Curated Themes
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        let lum = theme.luminance_analyzer.current_luminance;
                        let floor = theme.luminance_analyzer.current_opacity_floor;
                        hig_card_header(
                            ui,
                            &format!(
                                "Appearance & {} Curated Themes",
                                CrystalGlassPreset::ALL.len()
                            ),
                            &format!(
                                "Wallpaper luminance {:.3} · Adaptive glass floor {:.2}",
                                lum, floor
                            ),
                            accent,
                        );
                        if ui
                            .checkbox(
                                &mut theme.use_crystal_glass_backdrop,
                                "Enable Crystal-Glass Backdrop",
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .checkbox(
                                &mut theme.visual_bloom,
                                "Visual Bloom (VFD Phosphor & Trace Glow)",
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if theme.visual_bloom
                            && ui
                                .add(
                                    egui::Slider::new(&mut theme.visual_bloom_level, 1..=11)
                                        .text("Bloom Level"),
                                )
                                .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .add(
                                egui::Slider::new(&mut theme.display_saturation, 0.0..=1.5)
                                    .text("Display Saturation"),
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .checkbox(
                                &mut theme.smooth_graph_curves,
                                "Smooth Graph Curves (Cubic Bezier)",
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .checkbox(
                                &mut theme.high_frequency_visuals,
                                "High-Frequency Visuals (60 Hz Frame Pacing)",
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        ui.add_space(6.0);
                        ui.label(RichText::new("Curated Theme Presets").strong());
                        egui::ScrollArea::vertical()
                            .id_salt("presets_list")
                            .max_height(175.0)
                            .show(ui, |ui| {
                                for p in CrystalGlassPreset::ALL {
                                    let meta = p.metadata();
                                    let sw = srgb_to_color32(meta.accent_srgb);
                                    ui.horizontal(|ui| {
                                        let (r, _) = ui.allocate_exact_size(
                                            Vec2::new(12.0, 12.0),
                                            egui::Sense::hover(),
                                        );
                                        ui.painter().rect_filled(r, CornerRadius::same(3), sw);
                                        if ui
                                            .selectable_label(
                                                theme.glass_preset == p
                                                    && theme.custom_preset.is_none(),
                                                format!("{} ({})", meta.name, meta.accent_hex),
                                            )
                                            .clicked()
                                        {
                                            theme.glass_preset = p;
                                            theme.custom_preset = None;
                                            color_wheel.sync_from_meta(&meta);
                                            dirty = true;
                                        }
                                    });
                                }
                            });
                    });

                    ui.add_space(10.0);

                    // Card 2: Interactive Color Wheel & Custom Preset Studio + Collapsible JSON
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        if draw_hsv_color_wheel(
                            ui,
                            theme,
                            color_wheel,
                            saved_custom_presets,
                            preset_json_buf,
                            status_out,
                            accent,
                        ) {
                            dirty = true;
                        }
                        ui.add_space(6.0);
                        egui::CollapsingHeader::new("Advanced JSON Import / Export")
                            .default_open(false)
                            .show(ui, |ui| {
                                ui.horizontal(|ui| {
                                    if ui.button("Export Preset JSON").clicked()
                                        && let Ok(j) = theme.export_active_preset_json()
                                    {
                                        *preset_json_buf = j;
                                        *status_out =
                                            "Exported active theme preset to JSON box.".to_string();
                                    }
                                    if ui.button("Import Custom Preset JSON").clicked() {
                                        match theme.import_custom_preset_json(preset_json_buf) {
                                            Ok(m) => {
                                                color_wheel.sync_from_meta(&m);
                                                *status_out =
                                                    format!("Imported custom preset '{}'", m.name);
                                                dirty = true;
                                            }
                                            Err(e) => {
                                                *status_out = format!("Invalid preset JSON: {e}");
                                            }
                                        }
                                    }
                                });
                                ui.add(
                                    egui::TextEdit::multiline(preset_json_buf)
                                        .desired_rows(4)
                                        .desired_width(f32::INFINITY),
                                );
                            });
                    });
                });

                cols[1].vertical(|ui| {
                    // Card 3: Automation, Quiet Hours & Alert Thresholds
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        hig_card_header(
                            ui,
                            "Automation, Quiet Hours & Alert Thresholds",
                            "Control automatic background throttling, notification suppression windows, and alert triggers.",
                            accent,
                        );
                        ui.horizontal(|ui| {
                            if ui
                                .checkbox(&mut quiet_hours.enabled, "Enable Quiet Hours Window")
                                .changed()
                            {
                                dirty = true;
                            }
                            if quiet_hours.is_active() {
                                hig_status_pill(ui, "Quiet Active", hig_blue(ui));
                            } else {
                                hig_status_pill(ui, "Inactive", hig_gray(ui));
                            }
                        });
                        ui.horizontal(|ui| {
                            ui.label("Start (HH:MM):");
                            let mut s = quiet_hours.start_hhmm.to_string();
                            if ui
                                .add(egui::TextEdit::singleline(&mut s).desired_width(60.0))
                                .changed()
                            {
                                quiet_hours.start_hhmm = CompactString::from(s);
                                dirty = true;
                            }
                            ui.label("End (HH:MM):");
                            let mut e = quiet_hours.end_hhmm.to_string();
                            if ui
                                .add(egui::TextEdit::singleline(&mut e).desired_width(60.0))
                                .changed()
                            {
                                quiet_hours.end_hhmm = CompactString::from(e);
                                dirty = true;
                            }
                        });
                        ui.separator();
                        if ui
                            .checkbox(
                                &mut autobalance.enabled,
                                "Auto-Balance High-CPU Processes (>80% CPU lowered to nice +10)",
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .checkbox(
                                &mut idle_throttle.enabled,
                                "Throttle Idle Background Processes (nice +19)",
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        ui.separator();
                        if ui
                            .add(
                                egui::Slider::new(alerts_cpu_thresh, 20.0..=100.0)
                                    .text("CPU Alert %"),
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .add(
                                egui::Slider::new(alerts_mem_thresh, 20.0..=100.0)
                                    .text("Memory Alert %"),
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .add(
                                egui::Slider::new(alerts_temp_thresh, 40.0..=115.0)
                                    .text("Thermal Alert C"),
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        if ui
                            .add(
                                egui::Slider::new(alerts_disk_thresh, 50.0..=99.0)
                                    .text("Disk Alert %"),
                            )
                            .changed()
                        {
                            dirty = true;
                        }
                        ui.horizontal(|ui| {
                            ui.label("Webhook URL:");
                            if ui
                                .add(
                                    egui::TextEdit::singleline(alerts_webhook_buf)
                                        .hint_text("https://hooks.example.com/nexus"),
                                )
                                .changed()
                            {
                                dirty = true;
                            }
                        });
                        ui.separator();
                        ui.label(
                            RichText::new(format!(
                                "Recent Alerts: {} · Active Rules: {} · Stored History Rows: {}",
                                runtime_state.recent_alerts.len(),
                                runtime_state.active_rule_triggers.len(),
                                runtime_state.storage_rows_written
                            ))
                            .font(FontId::proportional(11.5))
                            .color(ui.visuals().weak_text_color()),
                        );
                        for al in runtime_state.recent_alerts.iter().rev().take(4) {
                            ui.label(format!(
                                "• {}: {:.1} >= {:.1} (Quiet Hours suppressed: {})",
                                al.metric_name,
                                al.measured_value,
                                al.threshold,
                                al.suppressed_by_quiet_hours
                            ));
                        }
                    });

                    ui.add_space(10.0);

                    // Card 4: Companion Remote Server (Bonjour & ChaCha20-Poly1305)
                    hig_card(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.vertical(|ui| {
                                hig_card_header(
                                    ui,
                                    "Companion Remote Server",
                                    &format!(
                                        "Bonjour ({BONJOUR_SERVICE_TYPE}, Port {DEFAULT_REMOTE_PORT}) · ChaCha20-Poly1305 encrypted telemetry"
                                    ),
                                    accent,
                                );
                            });
                            ui.with_layout(egui::Layout::right_to_left(egui::Align::TOP), |ui| {
                                if remote_server.is_some() {
                                    hig_status_pill(ui, "Active", hig_green(ui));
                                } else {
                                    hig_status_pill(ui, "Stopped", hig_gray(ui));
                                }
                            });
                        });
                        ui.horizontal_wrapped(|ui| {
                            if remote_server.is_none() {
                                if ui.button("Start Remote Server").clicked() {
                                    match RemoteServerHandle::start(
                                        tokio_handle,
                                        RemoteServerConfig {
                                            port: DEFAULT_REMOTE_PORT,
                                            allow_cgnat: *remote_allow_cgnat,
                                            advertise_bonjour: *remote_advertise_bonjour,
                                        },
                                    ) {
                                        Ok(handle) => {
                                            *remote_server = Some(handle);
                                            *status_out =
                                                "Companion remote server started.".to_string();
                                        }
                                        Err(err) => {
                                            *status_out =
                                                format!("Remote server start failed: {err}");
                                        }
                                    }
                                }
                            } else if ui.button("Stop Remote Server").clicked() {
                                if let Some(mut srv) = remote_server.take() {
                                    srv.stop();
                                }
                                *status_out = "Companion remote server stopped.".to_string();
                            }
                            if ui
                                .checkbox(
                                    remote_allow_cgnat,
                                    "Allow Tailscale / CGNAT peers (100.64.0.0/10)",
                                )
                                .changed()
                                && let Some(srv) = remote_server
                                && let Ok(mut st) = srv.status.lock()
                            {
                                st.allow_cgnat = *remote_allow_cgnat;
                            }
                            if ui
                                .checkbox(
                                    remote_advertise_bonjour,
                                    "Advertise via Bonjour (dns-sd -R)",
                                )
                                .changed()
                                && let Some(srv) = remote_server
                            {
                                match srv.set_bonjour_advertising(*remote_advertise_bonjour) {
                                    Ok(active) => {
                                        *status_out = if active {
                                            "Bonjour dns-sd advertising started.".to_string()
                                        } else {
                                            "Bonjour dns-sd advertising stopped.".to_string()
                                        };
                                    }
                                    Err(err) => {
                                        *status_out = format!("Bonjour toggle failed: {err}");
                                    }
                                }
                            }
                        });
                        ui.add_space(4.0);
                        if let Some(srv) = remote_server
                            && let Ok(st) = srv.status.lock()
                        {
                            ui.label(format!(
                                "Status: {} · Pairing Code: {} · Last SAS: {}",
                                st.status_message,
                                st.pairing_code,
                                st.last_sas_code.as_deref().unwrap_or("None")
                            ));
                            if st.connected_peers.is_empty() {
                                ui.label(
                                    RichText::new("Connected Peers: None")
                                        .color(ui.visuals().weak_text_color()),
                                );
                            } else {
                                for peer in &st.connected_peers {
                                    ui.label(format!(
                                        "• {} ({}) @ {} [SAS {}]",
                                        peer.device_name,
                                        peer.device_id,
                                        peer.peer_addr,
                                        peer.sas_code
                                    ));
                                }
                            }
                        } else {
                            ui.label(
                                RichText::new(
                                    "Server stopped. Private RFC1918/link-local policy active with ChaCha20-Poly1305 sealed frames.",
                                )
                                .font(FontId::proportional(11.5))
                                .color(ui.visuals().weak_text_color()),
                            );
                        }
                    });
                });
            });
        });

    dirty
}

#[allow(clippy::too_many_arguments)]
pub fn draw_disk_space_extra_tabs(
    ui: &mut egui::Ui,
    tab: DiskSubTab,
    disk_report: &DiskScanReport,
    treemap_rects: &mut Vec<TreemapRect>,
    treemap_last_size: &mut Option<Vec2>,
    duplicate_groups: &Arc<Mutex<Vec<DuplicateGroup>>>,
    dup_scanning: &mut bool,
    disk_store: &mut DiskSnapshotStore,
    disk_snapshots: &mut Vec<DiskSnapshotInfo>,
    selected_diff: &mut Option<DiskSnapshotDiff>,
    volumes: &[VolumeSpaceSummary],
    accent: Color32,
) {
    match tab {
        DiskSubTab::Visualizer => {
            ui.columns(2, |cols| {
                hig_card(&mut cols[0], |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Top Directories",
                        "Folders consuming the largest share of scanned storage.",
                        accent,
                    );
                    for d in disk_report.top_directories.iter().take(10) {
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(format!("{:>9}", format_bytes(d.size)))
                                    .monospace()
                                    .color(accent),
                            );
                            ui.add(egui::Label::new(d.full_path.as_str()).truncate());
                        });
                    }
                });
                hig_card(&mut cols[1], |ui| {
                    ui.set_min_width(ui.available_width());
                    hig_card_header(
                        ui,
                        "Largest Files",
                        "Individual files occupying the most disk space in this scan.",
                        accent,
                    );
                    for f in disk_report.largest_files.iter().take(10) {
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(format!("{:>9}", format_bytes(f.size)))
                                    .monospace()
                                    .color(accent),
                            );
                            ui.add(egui::Label::new(f.full_path.as_str()).truncate());
                        });
                    }
                });
            });
        }
        DiskSubTab::Treemap => {
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                hig_card_header(
                    ui,
                    "Storage Treemap",
                    "Proportional squarified map of folders and files colored by content category.",
                    accent,
                );
                let avail = Vec2::new(ui.available_width().max(240.0), 310.0);
                let (rect, resp) = ui.allocate_exact_size(avail, egui::Sense::hover());
                let dims_changed = treemap_last_size.is_some_and(|prev| {
                    (prev.x - rect.width()).abs() > 2.0 || (prev.y - rect.height()).abs() > 2.0
                });
                if dims_changed {
                    treemap_rects.clear();
                }
                if treemap_rects.is_empty() && !disk_report.root_node.children.is_empty() {
                    *treemap_rects = compute_squarified_treemap(
                        &disk_report.root_node.children,
                        RectBounds {
                            x: 0.0,
                            y: 0.0,
                            width: rect.width(),
                            height: rect.height(),
                        },
                    );
                    *treemap_last_size = Some(rect.size());
                }
                let painter = ui.painter_at(rect);
                let map_bg = if ui.visuals().dark_mode {
                    Color32::from_rgb(10, 14, 20)
                } else {
                    Color32::from_rgb(236, 240, 246)
                };
                painter.rect_filled(rect, CornerRadius::same(6), map_bg);
                let hover_pos = resp.hover_pos();
                let mut hovered_info: Option<String> = None;

                for tr in treemap_rects.iter() {
                    let r = Rect::from_min_size(
                        Pos2::new(rect.min.x + tr.x, rect.min.y + tr.y),
                        Vec2::new((tr.width - 1.0).max(1.0), (tr.height - 1.0).max(1.0)),
                    );
                    let col = srgb_to_color32(tr.category.default_color_srgb());
                    let fill = Color32::from_rgba_unmultiplied(
                        col.r(),
                        col.g(),
                        col.b(),
                        if tr.is_dir { 125 } else { 190 },
                    );
                    painter.rect_filled(r, CornerRadius::same(2), fill);
                    painter.rect_stroke(
                        r,
                        CornerRadius::same(2),
                        Stroke::new(0.8, map_bg),
                        StrokeKind::Inside,
                    );
                    if r.width() > 74.0 && r.height() > 21.0 {
                        painter.text(
                            r.min + Vec2::new(4.0, 3.0),
                            egui::Align2::LEFT_TOP,
                            tr.name.as_str(),
                            FontId::proportional(11.5),
                            Color32::WHITE,
                        );
                    }
                    if let Some(hp) = hover_pos
                        && r.contains(hp)
                    {
                        hovered_info = Some(format!(
                            "{} ({}) [{}]",
                            tr.full_path,
                            format_bytes(tr.size),
                            tr.category.label()
                        ));
                    }
                }
                if let Some(tip) = hovered_info {
                    ui.add_space(4.0);
                    ui.label(RichText::new(tip).color(accent));
                }
            });
        }
        DiskSubTab::Duplicates => {
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                hig_card_header(
                    ui,
                    "Duplicate File Finder",
                    "Runs a two-pass size and SHA-256 content check to locate exact duplicate files.",
                    accent,
                );
                ui.horizontal(|ui| {
                    if ui.button("Run 2-Pass SHA-256 Duplicate Scan").clicked() && !*dup_scanning {
                        *dup_scanning = true;
                        let root_clone = disk_report.root_node.clone();
                        let slot = Arc::clone(duplicate_groups);
                        std::thread::spawn(move || {
                            let res = find_duplicates(&root_clone);
                            *slot.lock().unwrap_or_else(|e| e.into_inner()) = res;
                        });
                    }
                    if *dup_scanning {
                        hig_status_pill(ui, "Hashing...", hig_blue(ui));
                    }
                });
                let groups = duplicate_groups.lock().unwrap_or_else(|e| e.into_inner());
                if *dup_scanning && !groups.is_empty() {
                    *dup_scanning = false;
                }
                ui.add_space(4.0);
                ui.label(
                    RichText::new(format!("Duplicate Groups Found: {}", groups.len())).strong(),
                );
                ui.add_space(4.0);
                egui::ScrollArea::vertical()
                    .id_salt("dup_groups_scroll")
                    .max_height(260.0)
                    .show(ui, |ui| {
                        for g in groups.iter().take(40) {
                            hig_card(ui, |ui| {
                                ui.set_min_width(ui.available_width());
                                ui.label(
                                    RichText::new(format!(
                                        "SHA256 {}.. · Size {} · Wasted {} ({} copies)",
                                        &g.hash_hex[..12.min(g.hash_hex.len())],
                                        format_bytes(g.file_size),
                                        format_bytes(g.wasted_bytes),
                                        g.paths.len()
                                    ))
                                    .strong(),
                                );
                                for p in &g.paths {
                                    ui.label(
                                        RichText::new(p.as_str())
                                            .font(FontId::proportional(11.5))
                                            .color(ui.visuals().weak_text_color()),
                                    );
                                }
                            });
                            ui.add_space(4.0);
                        }
                    });
            });
        }
        DiskSubTab::Snapshots => {
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                hig_card_header(
                    ui,
                    "Saved Disk Snapshots & Growth Diff",
                    "Save point-in-time directory trees to SQLite and compare disk usage changes.",
                    accent,
                );
                ui.horizontal(|ui| {
                    if ui.button("Save Disk Snapshot to SQLite").clicked() {
                        let _ = disk_store.save_disk_snapshot(disk_report, Some("GUI Disk Scan"));
                        *disk_snapshots = disk_store.list_disk_snapshots(None).unwrap_or_default();
                    }
                    if disk_snapshots.len() >= 2 && ui.button("Diff Latest 2 Snapshots").clicked() {
                        let base = disk_snapshots[1].id;
                        let targ = disk_snapshots[0].id;
                        *selected_diff = disk_store.diff_disk_snapshots(base, targ).ok();
                    }
                });
                ui.add_space(6.0);
                for snap in disk_snapshots.iter().take(8) {
                    ui.label(format!(
                        "#{} [{}] {} — {} ({} files)",
                        snap.id,
                        snap.label,
                        snap.root_path,
                        format_bytes(snap.total_bytes),
                        snap.total_files
                    ));
                }
                if let Some(diff) = selected_diff {
                    ui.separator();
                    ui.label(
                        RichText::new(format!(
                            "Diff #{} to #{}: deltaBytes={:+} bytes, deltaFiles={:+}, Added={}, Removed={}, Grown={}",
                            diff.baseline_id,
                            diff.target_id,
                            diff.total_delta_bytes,
                            diff.total_delta_files,
                            diff.added_paths.len(),
                            diff.removed_paths.len(),
                            diff.grown_paths.len()
                        ))
                        .color(accent),
                    );
                }
            });
        }
        DiskSubTab::Volumes => {
            hig_card(ui, |ui| {
                ui.set_min_width(ui.available_width());
                hig_card_header(
                    ui,
                    "Physical & Mounted Volumes",
                    if HostPlatform::current().is_macos() {
                        "Capacity, filesystem type, and available space across mounted APFS and external volumes."
                    } else {
                        "Capacity, filesystem type, and available space across mounted storage volumes."
                    },
                    accent,
                );
                egui::Grid::new("volumes_grid")
                    .num_columns(6)
                    .striped(true)
                    .spacing([14.0, 5.0])
                    .show(ui, |ui| {
                        ui.label(RichText::new("Mount Point").strong());
                        ui.label(RichText::new("Filesystem").strong());
                        ui.label(RichText::new("Total").strong());
                        ui.label(RichText::new("Used").strong());
                        ui.label(RichText::new("Available").strong());
                        ui.label(RichText::new("Used %").strong());
                        ui.end_row();

                        for v in volumes {
                            ui.label(v.mount_point.as_str());
                            ui.label(v.filesystem.as_str());
                            ui.label(format_metric_bytes(&v.total_bytes));
                            ui.label(format_metric_bytes(&v.used_bytes));
                            ui.label(format_metric_bytes(&v.available_bytes));
                            ui.label(v.used_percent.format_with_unit(1, "%"));
                            ui.end_row();
                        }
                    });
            });
        }
    }
}

#[allow(clippy::too_many_arguments)]
pub fn draw_single_vfd_bar(
    ui: &mut egui::Ui,
    title: &str,
    readout: &str,
    level: f64,
    positional: bool,
    accent: Color32,
    theme: &ThemeEngine,
) {
    ui.group(|ui| {
        ui.horizontal(|ui| {
            ui.label(
                RichText::new(title)
                    .font(FontId::new(11.0, FontFamily::Name("michroma".into())))
                    .color(accent),
            );
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                ui.label(
                    RichText::new(readout)
                        .font(FontId::new(15.0, FontFamily::Name("led".into())))
                        .color(accent),
                );
            });
        });

        let avail_w = ui.available_width().max(80.0);
        let (rect, _) = ui.allocate_exact_size(Vec2::new(avail_w, 18.0), egui::Sense::hover());
        let painter = ui.painter_at(rect);
        painter.rect_filled(rect, CornerRadius::same(2), Color32::from_rgb(8, 12, 16));

        let steps = constants::VFD_POSITIONAL_STEPS;
        let gap = 2.0_f32;
        let seg_w = ((rect.width() - gap * ((steps - 1) as f32)) / (steps as f32)).max(2.0);
        let lit_float = (level.clamp(0.0, 1.0) * (steps as f64)) as f32;

        let base_rgb = [
            f32::from(accent.r()) / 255.0,
            f32::from(accent.g()) / 255.0,
            f32::from(accent.b()) / 255.0,
        ];
        let ghost_rgb = vfd_dark_unlit_segment_srgb(base_rgb);

        for i in 0..steps {
            let x0 = rect.min.x + (i as f32) * (seg_w + gap);
            let seg_rect =
                Rect::from_min_size(Pos2::new(x0, rect.min.y + 3.0), Vec2::new(seg_w, 12.0));
            let lit_rgb = if positional {
                theme.apply_display_saturation(vfd_positional_lit_srgb(i))
            } else {
                base_rgb
            };
            let unlit_rgb = if positional {
                vfd_positional_unlit_srgb(i)
            } else {
                ghost_rgb
            };
            let frac = (lit_float - (i as f32)).clamp(0.0, 1.0);
            let lit = VfdLitAppearance::make(lit_rgb, theme.visual_bloom, theme.visual_bloom_level);

            if frac > 0.02 && theme.visual_bloom && lit.glow_alpha > 0.0 {
                let outer_expand = (1.5 * lit.glow_radius_multiplier * frac).max(0.6);
                let halo_outer = seg_rect.expand(outer_expand);
                let halo_alpha = (lit.glow_alpha * frac * 0.42).clamp(0.0, 1.0);
                painter.rect_filled(
                    halo_outer,
                    CornerRadius::same(2),
                    srgb_rgba_to_color32([
                        lit.glow_rgb[0],
                        lit.glow_rgb[1],
                        lit.glow_rgb[2],
                        halo_alpha,
                    ]),
                );
                let halo_inner = seg_rect.expand(outer_expand * 0.45);
                let inner_alpha = (lit.glow_alpha * frac * 0.65).clamp(0.0, 1.0);
                painter.rect_filled(
                    halo_inner,
                    CornerRadius::same(1),
                    srgb_rgba_to_color32([
                        lit.glow_rgb[0],
                        lit.glow_rgb[1],
                        lit.glow_rgb[2],
                        inner_alpha,
                    ]),
                );
            }

            let col = [
                unlit_rgb[0] + (lit.base_rgb[0] - unlit_rgb[0]) * frac,
                unlit_rgb[1] + (lit.base_rgb[1] - unlit_rgb[1]) * frac,
                unlit_rgb[2] + (lit.base_rgb[2] - unlit_rgb[2]) * frac,
            ];
            painter.rect_filled(seg_rect, CornerRadius::same(1), srgb_to_color32(col));

            if frac > 0.15 {
                let core_rect = seg_rect.shrink2(Vec2::new(0.8, 2.5));
                if core_rect.width() > 0.5 && core_rect.height() > 0.5 {
                    painter.rect_filled(
                        core_rect,
                        CornerRadius::same(1),
                        srgb_rgba_to_color32([
                            lit.inner_core_rgb[0],
                            lit.inner_core_rgb[1],
                            lit.inner_core_rgb[2],
                            (frac * 0.78).clamp(0.0, 1.0),
                        ]),
                    );
                }
                let spec_rect = Rect::from_min_size(
                    seg_rect.min,
                    Vec2::new(seg_rect.width(), (seg_rect.height() * 0.18).max(1.2)),
                );
                painter.rect_filled(
                    spec_rect,
                    CornerRadius::same(1),
                    srgb_rgba_to_color32([
                        lit.specular_rgb[0],
                        lit.specular_rgb[1],
                        lit.specular_rgb[2],
                        (frac * 0.65).clamp(0.0, 1.0),
                    ]),
                );
            }
        }
        let _ = VfdSegmentedMeterState::compute_geometry(f64::from(avail_w), 8.0, 2.0, 12.0);
    });
}

/// Renders one or more `CompressedHistoryGraph` series using actual cubic Bezier control points,
/// gradient `egui::epaint::Mesh` area fills, optional phosphor bloom stroke, optional top legend,
/// 4-band non-linear time compression, and combined multi-series hover scrub tooltip.
#[allow(clippy::too_many_arguments)]
pub fn draw_multi_series_history_canvas(
    ui: &mut egui::Ui,
    title: &str,
    series: &[(&str, Color32, &CompressedHistoryGraph)],
    _accent: Color32,
    height: f32,
    show_legend: bool,
    fill_opacity: u8,
    grid_lines: bool,
    smooth_curves: bool,
    visual_bloom: bool,
    bloom_level: u32,
    range_auto_y: bool,
    range_from: f64,
    range_to: f64,
    unit: &str,
) {
    if !title.is_empty() {
        ui.label(
            RichText::new(title)
                .font(FontId::proportional(14.5))
                .strong()
                .color(ui.visuals().strong_text_color()),
        );
    }
    if !series.is_empty() && (show_legend || height >= 80.0) {
        ui.horizontal_wrapped(|ui| {
            for &(lbl, col, graph) in series.iter().take(8) {
                let mut count = 0usize;
                let mut sum = 0.0_f64;
                let mut peak = f64::NEG_INFINITY;
                let mut latest: Option<f64> = None;
                for s in &graph.samples {
                    if let Some(v) = s.value.filter(|v| v.is_finite()) {
                        count += 1;
                        sum += v;
                        if v > peak {
                            peak = v;
                        }
                        latest = Some(v);
                    }
                }
                let name = if lbl.is_empty() { "Current" } else { lbl };
                let text = if count > 0
                    && let Some(cur) = latest
                {
                    let avg = sum / (count as f64);
                    format!("{name}: {cur:.1}{unit} (Avg {avg:.1}{unit} · Peak {peak:.1}{unit})")
                } else {
                    format!("{name}: No data")
                };
                let fill = Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), 28);
                let border = Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), 96);
                egui::Frame::new()
                    .fill(fill)
                    .stroke(Stroke::new(1.0, border))
                    .corner_radius(CornerRadius::same(6))
                    .inner_margin(egui::Margin::symmetric(8, 3))
                    .show(ui, |ui| {
                        ui.horizontal(|ui| {
                            let (dot_r, _) =
                                ui.allocate_exact_size(Vec2::new(8.0, 8.0), egui::Sense::hover());
                            ui.painter().circle_filled(dot_r.center(), 3.6, col);
                            ui.label(
                                RichText::new(text)
                                    .font(FontId::proportional(12.0))
                                    .color(ui.visuals().text_color()),
                            );
                        });
                    });
            }
        });
        ui.add_space(4.0);
    }

    let (rect, resp) = ui.allocate_exact_size(
        Vec2::new(ui.available_width().max(120.0), height),
        egui::Sense::hover(),
    );
    let painter = ui.painter_at(rect);
    let dark = ui.visuals().dark_mode;
    let canvas_bg = if dark {
        Color32::from_rgb(10, 14, 20)
    } else {
        Color32::from_rgb(244, 247, 252)
    };
    let canvas_border = if dark {
        Color32::from_rgba_unmultiplied(255, 255, 255, 28)
    } else {
        Color32::from_rgba_unmultiplied(15, 23, 42, 26)
    };
    let grid_col = if dark {
        Color32::from_rgba_unmultiplied(148, 163, 184, 30)
    } else {
        Color32::from_rgba_unmultiplied(100, 116, 139, 36)
    };
    painter.rect_filled(rect, CornerRadius::same(6), canvas_bg);
    painter.rect_stroke(
        rect,
        CornerRadius::same(6),
        Stroke::new(1.0, canvas_border),
        StrokeKind::Inside,
    );

    if grid_lines {
        let grid_stroke = Stroke::new(1.0, grid_col);
        for band in 1..4 {
            let frac = band as f32 * 0.25;
            let x = rect.min.x + rect.width() * frac;
            painter.line_segment(
                [Pos2::new(x, rect.min.y), Pos2::new(x, rect.max.y)],
                grid_stroke,
            );
            let y = rect.min.y + rect.height() * frac;
            painter.line_segment(
                [Pos2::new(rect.min.x, y), Pos2::new(rect.max.x, y)],
                grid_stroke,
            );
        }
    }

    let newest_t = series
        .iter()
        .filter_map(|(_, _, g)| g.samples.last().map(|s| s.sampled_at_sec))
        .fold(0.0_f64, f64::max);
    let oldest_t = series
        .iter()
        .filter_map(|(_, _, g)| g.samples.first().map(|s| s.sampled_at_sec))
        .fold(newest_t, f64::min);
    let window_sec = (newest_t - oldest_t).max(1.0);

    let (y_floor, y_ceiling) = if !range_auto_y && range_to > range_from {
        (range_from, range_to)
    } else if unit == "%" {
        let max_sample_val = series
            .iter()
            .flat_map(|(_, _, g)| {
                g.samples
                    .iter()
                    .filter_map(|s| s.value.filter(|v| v.is_finite()))
            })
            .fold(100.0_f64, f64::max);
        let ceil = if max_sample_val > 100.5 {
            (max_sample_val * 1.15).max(100.0)
        } else {
            100.0
        };
        (0.0, ceil)
    } else {
        let max_sample_val = series
            .iter()
            .flat_map(|(_, _, g)| {
                g.samples
                    .iter()
                    .filter_map(|s| s.value.filter(|v| v.is_finite()))
            })
            .fold(1.0_f64, f64::max);
        let ceil = (max_sample_val * 1.20).max(1.0);
        (0.0, ceil)
    };
    let y_span = (y_ceiling - y_floor).max(1e-6);

    if height >= 72.0 {
        let axis_col = if dark {
            Color32::from_rgba_unmultiplied(214, 224, 238, 195)
        } else {
            Color32::from_rgba_unmultiplied(51, 65, 85, 210)
        };
        painter.text(
            rect.min + Vec2::new(6.0, 4.0),
            egui::Align2::LEFT_TOP,
            format!("{y_ceiling:.0}{unit}"),
            FontId::proportional(11.5),
            axis_col,
        );
        painter.text(
            Pos2::new(rect.min.x + 6.0, rect.max.y - 4.0),
            egui::Align2::LEFT_BOTTOM,
            format!("{y_floor:.0}{unit}"),
            FontId::proportional(11.5),
            axis_col,
        );
    }

    let map_pt = |t_sec: f64, val: f64| -> Pos2 {
        let age = (newest_t - t_sec).max(0.0);
        let nx = CompressedHistoryGraph::map_age_to_normalized_x(age, window_sec, 15.0) as f32;
        let ny = ((val - y_floor) / y_span).clamp(0.0, 1.0) as f32;
        Pos2::new(
            rect.min.x + nx * rect.width(),
            rect.max.y - ny * rect.height(),
        )
    };

    let draw_live_dot = |pt: Pos2, col: Color32| {
        let dot_pos = Pos2::new(
            pt.x.clamp(rect.min.x + 4.0, rect.max.x - 4.0),
            pt.y.clamp(rect.min.y + 4.0, rect.max.y - 4.0),
        );
        if visual_bloom {
            painter.circle_filled(
                dot_pos,
                6.5,
                Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), 56),
            );
        }
        painter.circle_filled(dot_pos, 3.4, col);
        painter.circle_stroke(dot_pos, 4.4, Stroke::new(1.2, Color32::WHITE));
    };

    for &(_, col, graph) in series {
        let subpaths = graph.build_curve_subpaths();
        let top_fill = Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), fill_opacity);
        let bot_fill = Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), 4);
        let stroke = Stroke::new(1.8, col);
        let latest_val = graph
            .samples
            .iter()
            .rev()
            .find_map(|s| s.value.filter(|v| v.is_finite()));

        if subpaths.is_empty() || (newest_t - oldest_t) < 0.05 {
            if let Some(val) = latest_val {
                let ny = ((val - y_floor) / y_span).clamp(0.0, 1.0) as f32;
                let y = rect.max.y - ny * rect.height();
                let p0 = Pos2::new(rect.min.x, y);
                let p1 = Pos2::new(rect.max.x, y);
                let pts = vec![p0, p1];
                if fill_opacity > 0 {
                    let mut mesh = egui::Mesh::default();
                    mesh.colored_vertex(p0, top_fill);
                    mesh.colored_vertex(Pos2::new(p0.x, rect.max.y), bot_fill);
                    mesh.colored_vertex(p1, top_fill);
                    mesh.colored_vertex(Pos2::new(p1.x, rect.max.y), bot_fill);
                    mesh.add_triangle(0, 1, 2);
                    mesh.add_triangle(1, 3, 2);
                    painter.add(egui::Shape::mesh(mesh));
                }
                if visual_bloom {
                    let bloom_w = 3.4 + (bloom_level.clamp(1, 11) as f32) * 0.25;
                    let bloom_c = Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), 46);
                    painter.add(egui::Shape::line(
                        pts.clone(),
                        Stroke::new(bloom_w, bloom_c),
                    ));
                }
                painter.add(egui::Shape::line(pts, stroke));
                draw_live_dot(Pos2::new(rect.max.x - 4.0, y), col);
            }
            continue;
        }

        let mut latest_drawn_pt: Option<Pos2> = None;
        for segs in subpaths {
            let mut points: Vec<Pos2> = Vec::with_capacity(segs.len() * 8 + 1);
            for (idx, seg) in segs.iter().enumerate() {
                if seg.is_straight || !smooth_curves {
                    let p0 = map_pt(seg.p0[0], seg.p0[1]);
                    let p1 = map_pt(seg.p1[0], seg.p1[1]);
                    if idx == 0 {
                        points.push(p0);
                    }
                    points.push(p1);
                } else {
                    let steps = 8usize;
                    let start_k = usize::from(idx != 0);
                    for k in start_k..=steps {
                        let u = (k as f64) / (steps as f64);
                        let inv = 1.0 - u;
                        let w0 = inv * inv * inv;
                        let w1 = 3.0 * inv * inv * u;
                        let w2 = 3.0 * inv * u * u;
                        let w3 = u * u * u;
                        let tx = w0 * seg.p0[0] + w1 * seg.c1[0] + w2 * seg.c2[0] + w3 * seg.p1[0];
                        let vy = w0 * seg.p0[1] + w1 * seg.c1[1] + w2 * seg.c2[1] + w3 * seg.p1[1];
                        let mut pt = map_pt(tx, vy);
                        if let Some(prev) = points.last()
                            && pt.x < prev.x
                        {
                            pt.x = prev.x;
                        }
                        points.push(pt);
                    }
                }
            }
            if points.len() >= 2 {
                latest_drawn_pt = points.last().copied();
                if fill_opacity > 0 {
                    let mut mesh = egui::Mesh::default();
                    for p in &points {
                        mesh.colored_vertex(*p, top_fill);
                        mesh.colored_vertex(Pos2::new(p.x, rect.max.y), bot_fill);
                    }
                    for i in 0..(points.len() - 1) {
                        let v = (i * 2) as u32;
                        mesh.add_triangle(v, v + 1, v + 2);
                        mesh.add_triangle(v + 1, v + 3, v + 2);
                    }
                    painter.add(egui::Shape::mesh(mesh));
                }

                if visual_bloom {
                    let bloom_w = 3.4 + (bloom_level.clamp(1, 11) as f32) * 0.25;
                    let bloom_c = Color32::from_rgba_unmultiplied(col.r(), col.g(), col.b(), 46);
                    painter.add(egui::Shape::line(
                        points.clone(),
                        Stroke::new(bloom_w, bloom_c),
                    ));
                }
                painter.add(egui::Shape::line(points, stroke));
            }
        }
        if let Some(last_pt) = latest_drawn_pt {
            draw_live_dot(last_pt, col);
        }
    }

    // Combined multi-series hover scrub crosshair + dots + tooltip
    if let Some(hover) = resp.hover_pos()
        && rect.contains(hover)
    {
        let mut tip_lines = Vec::new();
        let mut scrub_x: Option<f32> = None;
        let mut scrub_age = 0.0_f64;

        for &(lbl, col, graph) in series {
            let mut best: Option<(f32, Pos2, f64, f64)> = None;
            for s in &graph.samples {
                if let Some(v) = s.value {
                    let p = map_pt(s.sampled_at_sec, v);
                    let dx = (p.x - hover.x).abs();
                    let age = (newest_t - s.sampled_at_sec).max(0.0);
                    if best.is_none_or(|(bd, ..)| dx < bd) {
                        best = Some((dx, p, v, age));
                    }
                }
            }
            if let Some((_, pt, val, age)) = best {
                if scrub_x.is_none() {
                    scrub_x = Some(pt.x);
                    scrub_age = age;
                }
                painter.circle_filled(pt, 3.8, col);
                painter.circle_stroke(pt, 5.0, Stroke::new(1.1, Color32::WHITE));
                let name = if lbl.is_empty() { "Sample" } else { lbl };
                tip_lines.push(format!("{name}: {val:.1}{unit}"));
            }
        }

        if let Some(sx) = scrub_x {
            let cross = Stroke::new(1.0, Color32::from_rgba_unmultiplied(230, 240, 255, 110));
            painter.line_segment(
                [Pos2::new(sx, rect.min.y), Pos2::new(sx, rect.max.y)],
                cross,
            );
            tip_lines.push(format!("({scrub_age:.1}s ago)"));
            resp.on_hover_text(tip_lines.join(" | "));
        }
    }
}

/// Renders a single `CompressedHistoryGraph` delegating to `draw_multi_series_history_canvas`.
#[allow(clippy::too_many_arguments)]
pub fn draw_compressed_history_canvas(
    ui: &mut egui::Ui,
    title: &str,
    graph: &CompressedHistoryGraph,
    accent: Color32,
    height: f32,
    smooth_curves: bool,
    visual_bloom: bool,
    bloom_level: u32,
) {
    let label = if title.is_empty() { "Sample" } else { title };
    let unit = if title.contains('%') { "%" } else { "" };
    draw_multi_series_history_canvas(
        ui,
        title,
        &[(label, accent, graph)],
        accent,
        height,
        true,
        64,
        true,
        smooth_curves,
        visual_bloom,
        bloom_level,
        true,
        0.0,
        100.0,
        unit,
    );
}

pub fn format_bytes(bytes: u64) -> String {
    const GIB: f64 = 1024.0 * 1024.0 * 1024.0;
    const MIB: f64 = 1024.0 * 1024.0;
    const KIB: f64 = 1024.0;
    let b = bytes as f64;
    if b >= GIB {
        format!("{:.2} GiB", b / GIB)
    } else if b >= MIB {
        format!("{:.1} MiB", b / MIB)
    } else if b >= KIB {
        format!("{:.1} KiB", b / KIB)
    } else {
        format!("{bytes} B")
    }
}

pub fn format_metric_bytes(m: &Metric<f64>) -> String {
    match m.reading {
        MetricReading::Measured { value, .. } if value.is_finite() => format_bytes(value as u64),
        _ => m.format_with_unit(1, "B"),
    }
}

pub fn srgb_to_color32(rgb: [f32; 3]) -> Color32 {
    Color32::from_rgb(
        (rgb[0].clamp(0.0, 1.0) * 255.0) as u8,
        (rgb[1].clamp(0.0, 1.0) * 255.0) as u8,
        (rgb[2].clamp(0.0, 1.0) * 255.0) as u8,
    )
}

pub fn srgb_rgba_to_color32(rgba: [f32; 4]) -> Color32 {
    Color32::from_rgba_unmultiplied(
        (rgba[0].clamp(0.0, 1.0) * 255.0) as u8,
        (rgba[1].clamp(0.0, 1.0) * 255.0) as u8,
        (rgba[2].clamp(0.0, 1.0) * 255.0) as u8,
        (rgba[3].clamp(0.0, 1.0) * 255.0) as u8,
    )
}

pub fn metric_mib(m: &Metric<f64>) -> f64 {
    match m.reading {
        MetricReading::Measured { value, .. } => value / (1024.0 * 1024.0),
        _ => 0.0,
    }
}
