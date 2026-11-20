use crate::assets::{load_standalone_fonts, load_standalone_skins};
use compact_str::CompactString;
use nexus_core::{
    DisplayMode, PopoutMode, PopoutSkin, SkinChannelColors, SurfaceFill, VfdTextColor,
    canonical_popout_skin_id, constants,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Exact 19-double VFD segment style table.
pub const VFD_SEGMENT_STYLE_TABLE: [f64; 19] = [
    0.52, 0.12, 0.03, 0.25, 0.50, 0.60, 1.10, 1.20, 0.22, 0.50, 0.275, 0.14, 0.45, 0.20, 0.075,
    0.05, 0.45, 0.75, 0.01,
];

/// Light mode instrument face `#E8EBEE`.
pub const VFD_LIGHT_INSTRUMENT_FACE_SRGB: [f32; 3] = [232.0 / 255.0, 235.0 / 255.0, 238.0 / 255.0];

/// Light mode unlit segment `#969696`.
pub const VFD_LIGHT_UNLIT_SEGMENT_SRGB: [f32; 3] = [150.0 / 255.0, 150.0 / 255.0, 150.0 / 255.0];

/// Dark mode unlit segment ghost (`max(lit * 0.14, 0.055)`).
#[inline]
pub fn vfd_dark_unlit_segment_srgb(lit_rgb: [f32; 3]) -> [f32; 3] {
    [
        (lit_rgb[0] * 0.14).max(0.055),
        (lit_rgb[1] * 0.14).max(0.055),
        (lit_rgb[2] * 0.14).max(0.055),
    ]
}

/// Parses `#RRGGBB` or `#RRGGBBAA` into normalized `[r, g, b, a]` sRGB floats.
pub fn parse_hex_rgba(hex: &str) -> Option<[f32; 4]> {
    let clean = hex.trim().strip_prefix('#').unwrap_or(hex.trim());
    if clean.len() == 6 {
        let r = u8::from_str_radix(&clean[0..2], 16).ok()?;
        let g = u8::from_str_radix(&clean[2..4], 16).ok()?;
        let b = u8::from_str_radix(&clean[4..6], 16).ok()?;
        Some([
            f32::from(r) / 255.0,
            f32::from(g) / 255.0,
            f32::from(b) / 255.0,
            1.0,
        ])
    } else if clean.len() == 8 {
        let r = u8::from_str_radix(&clean[0..2], 16).ok()?;
        let g = u8::from_str_radix(&clean[2..4], 16).ok()?;
        let b = u8::from_str_radix(&clean[4..6], 16).ok()?;
        let a = u8::from_str_radix(&clean[6..8], 16).ok()?;
        Some([
            f32::from(r) / 255.0,
            f32::from(g) / 255.0,
            f32::from(b) / 255.0,
            f32::from(a) / 255.0,
        ])
    } else {
        None
    }
}

/// 14-double Lit Phosphor Appearance (`MakeLitAppearance`).
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct VfdLitAppearance {
    pub base_rgb: [f32; 3],
    pub inner_core_rgb: [f32; 3],
    pub specular_rgb: [f32; 3],
    pub glow_rgb: [f32; 3],
    pub glow_alpha: f32,
    pub glow_radius_multiplier: f32,
}

impl VfdLitAppearance {
    pub fn make(rgb: [f32; 3], bloom_enabled: bool, bloom_level: u32) -> Self {
        let clamped_level = bloom_level.clamp(1, 11) as f32;
        let base_rgb = [
            rgb[0].clamp(0.0, 1.0),
            rgb[1].clamp(0.0, 1.0),
            rgb[2].clamp(0.0, 1.0),
        ];
        let max_c = base_rgb[0].max(base_rgb[1]).max(base_rgb[2]).max(1e-5);
        let vivid_rgb = [
            (base_rgb[0] / max_c).clamp(0.0, 1.0),
            (base_rgb[1] / max_c).clamp(0.0, 1.0),
            (base_rgb[2] / max_c).clamp(0.0, 1.0),
        ];
        let inner_core_rgb = [
            (base_rgb[0] * 0.72 + 0.28).clamp(0.0, 1.0),
            (base_rgb[1] * 0.72 + 0.28).clamp(0.0, 1.0),
            (base_rgb[2] * 0.72 + 0.28).clamp(0.0, 1.0),
        ];
        let specular_rgb = [
            (vivid_rgb[0] * 0.65 + 0.35).clamp(0.0, 1.0),
            (vivid_rgb[1] * 0.65 + 0.35).clamp(0.0, 1.0),
            (vivid_rgb[2] * 0.65 + 0.35).clamp(0.0, 1.0),
        ];
        let (glow_alpha, glow_radius_multiplier) = if bloom_enabled {
            let norm = (clamped_level - 1.0) / 10.0;
            (0.18 + 0.42 * norm, 0.85 + 0.90 * norm)
        } else {
            (0.0, 0.0)
        };
        Self {
            base_rgb,
            inner_core_rgb,
            specular_rgb,
            glow_rgb: vivid_rgb,
            glow_alpha,
            glow_radius_multiplier,
        }
    }
}

/// Maps normalized `[0.0, 1.0]` to VFD positional stop index `0..=15`.
#[inline]
pub fn vfd_positional_step(normalized_value: f64) -> usize {
    if !normalized_value.is_finite() || normalized_value <= 0.0 {
        return 0;
    }
    let clamped = normalized_value.min(1.0);
    let idx = (clamped * 16.0) as usize;
    idx.min(15)
}

/// Interpolates across Stop 0 (`t = 0.00`, `#30D158`), Stop 1 (`t = 0.60`, `#FFD60A`), and Stop 2 (`t = 0.85..1.00`, `#FF453A`).
pub fn vfd_positional_lit_srgb(step: usize) -> [f32; 3] {
    let idx = step.min(15);
    let t = (idx as f64) / 15.0;
    let s0 = constants::VFD_STOP_GREEN_SRGB.map(f64::from);
    let s1 = constants::VFD_STOP_YELLOW_SRGB.map(f64::from);
    let s2 = constants::VFD_STOP_RED_SRGB.map(f64::from);

    let (a, b, local_t) = if t >= 0.85 {
        (s2, s2, 0.0)
    } else if t >= 0.60 {
        (s1, s2, (t - 0.60) * 4.0)
    } else {
        (s0, s1, t / 0.60)
    };

    let r = ((a[0] + local_t * (b[0] - a[0])) as i64 as f64) / 255.0;
    let g = ((a[1] + local_t * (b[1] - a[1])) as i64 as f64) / 255.0;
    let bl = ((a[2] + local_t * (b[2] - a[2])) as i64 as f64) / 255.0;
    [r as f32, g as f32, bl as f32]
}

/// Scales each positional stop by `0.19` for dark unlit ghost segments.
pub fn vfd_positional_unlit_srgb(step: usize) -> [f32; 3] {
    let lit = vfd_positional_lit_srgb(step);
    let r_byte = ((f64::from(lit[0]) * 255.0).clamp(0.0, 255.0) as u64) as f64;
    let g_byte = ((f64::from(lit[1]) * 255.0).clamp(0.0, 255.0) as u64) as f64;
    let b_byte = ((f64::from(lit[2]) * 255.0).clamp(0.0, 255.0) as u64) as f64;
    [
        (((r_byte * 0.19) as i64 as f64) / 255.0) as f32,
        (((g_byte * 0.19) as i64 as f64) / 255.0) as f32,
        (((b_byte * 0.19) as i64 as f64) / 255.0) as f32,
    ]
}

/// Complete theme preset metadata mirroring Nexus Crystal-Glass palette tokens.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ThemePresetMetadata {
    pub id: CompactString,
    pub name: CompactString,
    pub is_dark: bool,
    pub accent_hex: CompactString,
    pub accent_srgb: [f32; 3],
    pub text_accent_hex: CompactString,
    pub text_accent_srgb: [f32; 3],
    pub window_bg_hex: CompactString,
    pub window_bg_srgb: [f32; 3],
    pub surface_bg_hex: CompactString,
    pub surface_bg_srgb: [f32; 3],
    pub sidebar_bg_hex: CompactString,
    pub sidebar_bg_srgb: [f32; 3],
    pub glass_opacity_floor: f32,
    pub glass_opacity_ceiling: f32,
    pub is_glass_enabled: bool,
    pub is_specular_enabled: bool,
    pub specular_intensity: f32,
    pub prismatic_shimmer: bool,
    pub font_family: CompactString,
    pub font_size_multiplier: f32,
}

impl ThemePresetMetadata {
    pub fn to_json_pretty(&self) -> Result<String, serde_json::Error> {
        serde_json::to_string_pretty(self)
    }

    pub fn from_json(raw: &str) -> Result<Self, serde_json::Error> {
        serde_json::from_str(raw)
    }
}

/// Converts normalized sRGB `[0.0, 1.0]` to `(hue_deg [0, 360), sat [0, 1], val [0, 1])`.
pub fn rgb_to_hsv(r: f32, g: f32, b: f32) -> (f32, f32, f32) {
    let r = r.clamp(0.0, 1.0);
    let g = g.clamp(0.0, 1.0);
    let b = b.clamp(0.0, 1.0);
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let delta = max - min;

    let h = if delta <= 1e-6 {
        0.0
    } else if (max - r).abs() <= 1e-6 {
        60.0 * (((g - b) / delta).rem_euclid(6.0))
    } else if (max - g).abs() <= 1e-6 {
        60.0 * (((b - r) / delta) + 2.0)
    } else {
        60.0 * (((r - g) / delta) + 4.0)
    };
    let s = if max <= 1e-6 { 0.0 } else { delta / max };
    (h.rem_euclid(360.0), s.clamp(0.0, 1.0), max)
}

/// Converts `(hue_deg, sat [0, 1], val [0, 1])` to normalized sRGB `[r, g, b]`.
pub fn hsv_to_rgb(h: f32, s: f32, v: f32) -> [f32; 3] {
    let h = h.rem_euclid(360.0);
    let s = s.clamp(0.0, 1.0);
    let v = v.clamp(0.0, 1.0);
    let c = v * s;
    let x = c * (1.0 - (((h / 60.0) % 2.0) - 1.0).abs());
    let m = v - c;
    let (r1, g1, b1) = if h < 60.0 {
        (c, x, 0.0)
    } else if h < 120.0 {
        (x, c, 0.0)
    } else if h < 180.0 {
        (0.0, c, x)
    } else if h < 240.0 {
        (0.0, x, c)
    } else if h < 300.0 {
        (x, 0.0, c)
    } else {
        (c, 0.0, x)
    };
    [
        (r1 + m).clamp(0.0, 1.0),
        (g1 + m).clamp(0.0, 1.0),
        (b1 + m).clamp(0.0, 1.0),
    ]
}

/// Formats normalized sRGB `[r, g, b]` into `#RRGGBB`.
pub fn rgb_to_hex(rgb: [f32; 3]) -> String {
    let r = (rgb[0].clamp(0.0, 1.0) * 255.0).round() as u8;
    let g = (rgb[1].clamp(0.0, 1.0) * 255.0).round() as u8;
    let b = (rgb[2].clamp(0.0, 1.0) * 255.0).round() as u8;
    format!("#{r:02X}{g:02X}{b:02X}")
}

/// Exact 20 Nexus Crystal-Glass theme presets matching `BuiltInThemePresets.cs`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum CrystalGlassPreset {
    #[serde(alias = "CobaltDeep")]
    NexusDefault,
    #[serde(alias = "ObsidianGlass")]
    CrystalGlass,
    #[serde(alias = "DaylightClear")]
    CleanLight,
    #[serde(alias = "NeonSynth")]
    Neon,
    CherryBlossom,
    #[serde(alias = "RoseQuartz")]
    DarkSakura,
    #[serde(alias = "CrimsonPulse")]
    Anime,
    #[serde(alias = "CyberCyan")]
    Futuristic,
    #[serde(alias = "VioletNebula")]
    OuterSpace,
    Minimalist,
    #[serde(alias = "VolcanicAsh")]
    DeepDark,
    #[serde(alias = "SolarGold")]
    Magical,
    #[serde(alias = "EmeraldMatrix", alias = "IcebergMint")]
    Techno,
    #[serde(alias = "JadeHorizon")]
    OceanDepth,
    #[serde(alias = "AmberPhosphor", alias = "CopperForge")]
    Sunset,
    #[serde(alias = "ArcticFrost")]
    Arctic,
    #[serde(alias = "MonolithDark")]
    Dracula,
    #[serde(alias = "MidnightSapphire")]
    SolarizedDark,
    SolarizedLight,
    #[serde(alias = "SlateTitanium")]
    Nord,
}

fn hex_to_rgb3(hex: &str) -> [f32; 3] {
    let rgba = parse_hex_rgba(hex).unwrap_or([0.5, 0.5, 0.5, 1.0]);
    [rgba[0], rgba[1], rgba[2]]
}

#[allow(clippy::too_many_arguments)]
fn build_meta(
    id: &str,
    name: &str,
    is_dark: bool,
    accent_hex: &str,
    text_accent_hex: &str,
    window_bg_hex: &str,
    surface_bg_hex: &str,
    sidebar_bg_hex: &str,
    glass_floor: f32,
    glass_ceil: f32,
    is_glass: bool,
    is_specular: bool,
    specular_intensity: f32,
    prismatic_shimmer: bool,
    font_family: &str,
    font_size_multiplier: f32,
) -> ThemePresetMetadata {
    ThemePresetMetadata {
        id: CompactString::from(id),
        name: CompactString::from(name),
        is_dark,
        accent_hex: CompactString::from(accent_hex),
        accent_srgb: hex_to_rgb3(accent_hex),
        text_accent_hex: CompactString::from(text_accent_hex),
        text_accent_srgb: hex_to_rgb3(text_accent_hex),
        window_bg_hex: CompactString::from(window_bg_hex),
        window_bg_srgb: hex_to_rgb3(window_bg_hex),
        surface_bg_hex: CompactString::from(surface_bg_hex),
        surface_bg_srgb: hex_to_rgb3(surface_bg_hex),
        sidebar_bg_hex: CompactString::from(sidebar_bg_hex),
        sidebar_bg_srgb: hex_to_rgb3(sidebar_bg_hex),
        glass_opacity_floor: glass_floor,
        glass_opacity_ceiling: glass_ceil,
        is_glass_enabled: is_glass,
        is_specular_enabled: is_specular,
        specular_intensity,
        prismatic_shimmer,
        font_family: CompactString::from(font_family),
        font_size_multiplier,
    }
}

impl CrystalGlassPreset {
    pub const ALL: [Self; 20] = [
        Self::NexusDefault,
        Self::CrystalGlass,
        Self::CleanLight,
        Self::Neon,
        Self::CherryBlossom,
        Self::DarkSakura,
        Self::Anime,
        Self::Futuristic,
        Self::OuterSpace,
        Self::Minimalist,
        Self::DeepDark,
        Self::Magical,
        Self::Techno,
        Self::OceanDepth,
        Self::Sunset,
        Self::Arctic,
        Self::Dracula,
        Self::SolarizedDark,
        Self::SolarizedLight,
        Self::Nord,
    ];

    pub const fn accent_srgb(self) -> [f32; 3] {
        match self {
            Self::NexusDefault => [10.0 / 255.0, 132.0 / 255.0, 1.0],
            Self::CrystalGlass => [10.0 / 255.0, 132.0 / 255.0, 1.0],
            Self::CleanLight => [0.0, 122.0 / 255.0, 1.0],
            Self::Neon => [57.0 / 255.0, 1.0, 20.0 / 255.0],
            Self::CherryBlossom => [232.0 / 255.0, 67.0 / 255.0, 147.0 / 255.0],
            Self::DarkSakura => [1.0, 107.0 / 255.0, 157.0 / 255.0],
            Self::Anime => [1.0, 107.0 / 255.0, 129.0 / 255.0],
            Self::Futuristic => [0.0, 212.0 / 255.0, 1.0],
            Self::OuterSpace => [124.0 / 255.0, 92.0 / 255.0, 252.0 / 255.0],
            Self::Minimalist => [51.0 / 255.0, 51.0 / 255.0, 51.0 / 255.0],
            Self::DeepDark => [187.0 / 255.0, 134.0 / 255.0, 252.0 / 255.0],
            Self::Magical => [224.0 / 255.0, 64.0 / 255.0, 251.0 / 255.0],
            Self::Techno => [0.0, 1.0, 156.0 / 255.0],
            Self::OceanDepth => [0.0, 188.0 / 255.0, 212.0 / 255.0],
            Self::Sunset => [1.0, 112.0 / 255.0, 67.0 / 255.0],
            Self::Arctic => [2.0 / 255.0, 136.0 / 255.0, 209.0 / 255.0],
            Self::Dracula => [189.0 / 255.0, 147.0 / 255.0, 249.0 / 255.0],
            Self::SolarizedDark => [38.0 / 255.0, 139.0 / 255.0, 210.0 / 255.0],
            Self::SolarizedLight => [38.0 / 255.0, 139.0 / 255.0, 210.0 / 255.0],
            Self::Nord => [136.0 / 255.0, 192.0 / 255.0, 208.0 / 255.0],
        }
    }

    pub fn metadata(self) -> ThemePresetMetadata {
        match self {
            Self::NexusDefault => Self::nexus_default_metadata(true),
            Self::CrystalGlass => build_meta(
                "crystal-glass",
                "Crystal Glass",
                true,
                "#0A84FF",
                "#60A5FA",
                "#090D14",
                "#111722",
                "#0C111A",
                0.55,
                0.85,
                true,
                true,
                0.55,
                true,
                "Michroma",
                1.0,
            ),
            Self::CleanLight => build_meta(
                "clean-light",
                "Clean Light",
                false,
                "#007AFF",
                "#0051B3",
                "#F5F7FA",
                "#FFFFFF",
                "#EBEEF5",
                0.80,
                0.96,
                false,
                false,
                0.20,
                false,
                "Michroma",
                1.0,
            ),
            Self::Neon => build_meta(
                "neon", "Neon", true, "#39FF14", "#00FFFF", "#0A0014", "#0F0020", "#0D001A", 0.50,
                0.80, true, true, 0.70, true, "Michroma", 1.0,
            ),
            Self::CherryBlossom => build_meta(
                "cherry-blossom",
                "Cherry Blossom",
                false,
                "#E84393",
                "#D63384",
                "#FFF0F5",
                "#FFFFFF",
                "#FFF5F8",
                0.80,
                0.96,
                false,
                false,
                0.30,
                false,
                "Michroma",
                1.0,
            ),
            Self::DarkSakura => build_meta(
                "dark-sakura",
                "Dark Sakura",
                true,
                "#FF6B9D",
                "#FFB0CC",
                "#1A0F15",
                "#231820",
                "#150B12",
                0.58,
                0.86,
                true,
                true,
                0.40,
                true,
                "Michroma",
                1.0,
            ),
            Self::Anime => build_meta(
                "anime", "Anime", true, "#FF6B81", "#FFD93D", "#1A0A1E", "#221428", "#1E0F24",
                0.56, 0.87, true, true, 0.60, true, "Michroma", 1.0,
            ),
            Self::Futuristic => build_meta(
                "futuristic",
                "Futuristic",
                true,
                "#00D4FF",
                "#00FFE0",
                "#020D18",
                "#081824",
                "#061420",
                0.50,
                0.82,
                true,
                true,
                0.80,
                true,
                "Michroma",
                1.0,
            ),
            Self::OuterSpace => build_meta(
                "outer-space",
                "Outer Space",
                true,
                "#7C5CFC",
                "#B8A9FF",
                "#05050F",
                "#0C0C1A",
                "#080818",
                0.52,
                0.84,
                true,
                true,
                0.65,
                true,
                "Michroma",
                1.0,
            ),
            Self::Minimalist => build_meta(
                "minimalist",
                "Minimalist",
                false,
                "#333333",
                "#555555",
                "#FAFAFA",
                "#FFFFFF",
                "#F5F5F5",
                0.80,
                0.96,
                false,
                false,
                0.20,
                false,
                "Michroma",
                1.0,
            ),
            Self::DeepDark => build_meta(
                "deep-dark",
                "Deep Dark",
                true,
                "#BB86FC",
                "#D7B8FF",
                "#000000",
                "#0A0A0A",
                "#050505",
                0.75,
                0.96,
                false,
                false,
                0.00,
                false,
                "Michroma",
                1.0,
            ),
            Self::Magical => build_meta(
                "magical", "Magical", true, "#E040FB", "#FFD740", "#140A20", "#1A1030", "#160C28",
                0.54, 0.85, true, true, 0.75, true, "Michroma", 1.0,
            ),
            Self::Techno => build_meta(
                "techno", "Techno", true, "#00FF9C", "#CCFF00", "#0A0F0A", "#101A10", "#0D150D",
                0.52, 0.82, true, true, 0.60, true, "Michroma", 1.0,
            ),
            Self::OceanDepth => build_meta(
                "ocean-depth",
                "Ocean Depth",
                true,
                "#00BCD4",
                "#4DD0E1",
                "#0A1620",
                "#0F1E2C",
                "#0C1A26",
                0.56,
                0.85,
                true,
                true,
                0.50,
                false,
                "Michroma",
                1.0,
            ),
            Self::Sunset => build_meta(
                "sunset", "Sunset", true, "#FF7043", "#FFB74D", "#1A0F0A", "#241510", "#1E120D",
                0.55, 0.84, true, true, 0.45, false, "Michroma", 1.0,
            ),
            Self::Arctic => build_meta(
                "arctic", "Arctic", false, "#0288D1", "#0277BD", "#F0F8FF", "#FFFFFF", "#E8F4FD",
                0.78, 0.92, false, false, 0.25, false, "Michroma", 1.0,
            ),
            Self::Dracula => build_meta(
                "dracula", "Dracula", true, "#BD93F9", "#FF79C6", "#282A36", "#2D2F3D", "#21222C",
                0.75, 0.95, false, false, 0.00, false, "Michroma", 1.0,
            ),
            Self::SolarizedDark => build_meta(
                "solarized-dark",
                "Solarized Dark",
                true,
                "#268BD2",
                "#2AA198",
                "#002B36",
                "#073642",
                "#002028",
                0.72,
                0.92,
                false,
                false,
                0.00,
                false,
                "Michroma",
                1.0,
            ),
            Self::SolarizedLight => build_meta(
                "solarized-light",
                "Solarized Light",
                false,
                "#268BD2",
                "#2AA198",
                "#FDF6E3",
                "#EEE8D5",
                "#FDF6E3",
                0.80,
                0.96,
                false,
                false,
                0.00,
                false,
                "Michroma",
                1.0,
            ),
            Self::Nord => build_meta(
                "nord", "Nord", true, "#88C0D0", "#81A1C1", "#2E3440", "#3B4252", "#2E3440", 0.72,
                0.90, false, false, 0.00, false, "Michroma", 1.0,
            ),
        }
    }

    pub fn nexus_default_metadata(system_is_dark: bool) -> ThemePresetMetadata {
        if system_is_dark {
            build_meta(
                "nexus-default",
                "Nexus Default",
                true,
                "#0A84FF",
                "#60A5FA",
                "#090D14",
                "#111722",
                "#0C111A",
                0.70,
                0.92,
                false,
                false,
                0.0,
                false,
                "Michroma",
                1.0,
            )
        } else {
            build_meta(
                "nexus-default",
                "Nexus Default",
                false,
                "#007AFF",
                "#0051B3",
                "#F5F7FA",
                "#FFFFFF",
                "#EBEEF5",
                0.80,
                0.96,
                false,
                false,
                0.0,
                false,
                "Michroma",
                1.0,
            )
        }
    }
}

/// Queries macOS global dark mode preference (`AppleInterfaceStyle == "Dark"`).
pub fn detect_system_dark_mode() -> bool {
    std::process::Command::new("/usr/bin/defaults")
        .args(["read", "-g", "AppleInterfaceStyle"])
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).contains("Dark"))
        .unwrap_or(true)
}

/// Nexus BT.601 `WallpaperLuminanceAnalyzer` ($Y = 0.299R + 0.587G + 0.114B$)
/// computing adaptive glass surface opacity in `[0.50, 0.87]` with `0.15s` cubic adaptation.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct WallpaperLuminanceAnalyzer {
    pub current_luminance: f64,
    pub current_opacity_floor: f64,
    pub target_opacity_floor: f64,
}

impl Default for WallpaperLuminanceAnalyzer {
    fn default() -> Self {
        Self::new()
    }
}

impl WallpaperLuminanceAnalyzer {
    pub const MIN_OPACITY: f64 = 0.50;
    pub const MAX_OPACITY: f64 = 0.87;
    pub const ADAPTATION_SECONDS: f64 = 0.15;

    pub const fn new() -> Self {
        Self {
            current_luminance: 0.15,
            current_opacity_floor: 0.56,
            target_opacity_floor: 0.56,
        }
    }

    pub fn analyze_rgb8(&mut self, rgb_bytes: &[u8]) -> f64 {
        let pixel_count = rgb_bytes.len() / 3;
        if pixel_count == 0 {
            return self.current_luminance;
        }
        let mut sum_y = 0.0_f64;
        for chunk in rgb_bytes.as_chunks::<3>().0 {
            let r = f64::from(chunk[0]) / 255.0;
            let g = f64::from(chunk[1]) / 255.0;
            let b = f64::from(chunk[2]) / 255.0;
            sum_y += 0.299 * r + 0.587 * g + 0.114 * b;
        }
        let y = (sum_y / (pixel_count as f64)).clamp(0.0, 1.0);
        self.current_luminance = y;
        self.target_opacity_floor = (Self::MIN_OPACITY
            + y * (Self::MAX_OPACITY - Self::MIN_OPACITY))
            .clamp(Self::MIN_OPACITY, Self::MAX_OPACITY);
        y
    }

    pub fn step(&mut self, dt_seconds: f64) -> f64 {
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return self.current_opacity_floor;
        }
        let alpha = (dt_seconds / Self::ADAPTATION_SECONDS).clamp(0.0, 1.0);
        let inv = 1.0 - alpha;
        let eased = 1.0 - inv * inv * inv;
        self.current_opacity_floor +=
            (self.target_opacity_floor - self.current_opacity_floor) * eased;
        self.current_opacity_floor
    }
}

/// Resolved Popout Mini-Monitor layout, skin surfaces, typography, and channel colors from a `.nexusskin`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ResolvedPopoutView {
    pub skin_id: CompactString,
    pub skin_name: CompactString,
    pub mode: PopoutMode,
    pub width: f32,
    pub height: f32,
    pub effective_opacity: f32,
    pub flat_segments: bool,
    pub positional_palette: bool,
    pub quadrants: Vec<CompactString>,
    pub meter_style: CompactString,
    pub columns: u32,
    pub corner_radius: f32,
    pub window_bg_rgba: [f32; 4],
    pub window_stroke_rgba: [f32; 4],
    pub header_bg_rgba: [f32; 4],
    pub tile_bg_rgba: [f32; 4],
    pub tile_stroke_rgba: [f32; 4],
    pub title_ink_rgba: [f32; 4],
    pub primary_ink_rgba: [f32; 4],
    pub secondary_ink_rgba: [f32; 4],
    pub ground_rgba: [f32; 4],
    pub monospaced: bool,
    pub channel_colors: HashMap<CompactString, SkinChannelColors>,
}

fn fill_primary_rgba(fill: Option<&SurfaceFill>, fallback: [f32; 4]) -> [f32; 4] {
    match fill {
        Some(SurfaceFill::Solid(hex)) => parse_hex_rgba(hex).unwrap_or(fallback),
        Some(SurfaceFill::Gradient { gradient }) => gradient
            .first()
            .and_then(|h| parse_hex_rgba(h))
            .unwrap_or(fallback),
        None => fallback,
    }
}

/// Unified `ThemeEngine` combining 7 `DisplayMode` phosphor palettes, 4 `VfdTextColor`s,
/// 16-step positional VFD ramp, 19 Crystal-Glass presets + custom preset import/export +
/// BT.601 luminance analyzer, `cosmic-text` font database (`Michroma-Regular.ttf` + `LED Dot-Matrix.ttf`),
/// and 5 `.nexusskin` v1 definitions.
pub struct ThemeEngine {
    pub display_mode: DisplayMode,
    pub vfd_text_color: VfdTextColor,
    pub glass_preset: CrystalGlassPreset,
    pub custom_preset: Option<ThemePresetMetadata>,
    pub use_crystal_glass_backdrop: bool,
    pub display_saturation: f32,
    pub visual_bloom: bool,
    pub visual_bloom_level: u32,
    pub smooth_graph_curves: bool,
    pub high_frequency_visuals: bool,
    pub text_size_percent: u16,
    pub system_is_dark: bool,
    pub popout_mode: PopoutMode,
    pub popout_opacity: f32,
    pub popout_opaque_on_hover: bool,
    pub active_skin_id: CompactString,
    pub luminance_analyzer: WallpaperLuminanceAnalyzer,
    pub skins: Vec<PopoutSkin>,
    pub font_system: cosmic_text::FontSystem,
}

impl Default for ThemeEngine {
    fn default() -> Self {
        Self::new()
    }
}

impl ThemeEngine {
    pub fn new() -> Self {
        let (michroma_bytes, led_bytes) = load_standalone_fonts(None);
        let mut db = cosmic_text::fontdb::Database::new();
        db.load_font_data(michroma_bytes);
        db.load_font_data(led_bytes);
        let font_system = cosmic_text::FontSystem::new_with_locale_and_db("en-US".to_string(), db);

        let skins = load_standalone_skins(None).unwrap_or_default();

        Self {
            display_mode: DisplayMode::Color,
            vfd_text_color: VfdTextColor::Automatic,
            glass_preset: CrystalGlassPreset::CrystalGlass,
            custom_preset: None,
            use_crystal_glass_backdrop: true,
            display_saturation: 1.0,
            visual_bloom: true,
            visual_bloom_level: 7,
            smooth_graph_curves: true,
            high_frequency_visuals: true,
            text_size_percent: 100,
            system_is_dark: detect_system_dark_mode(),
            popout_mode: PopoutMode::Off,
            popout_opacity: 1.0,
            popout_opaque_on_hover: true,
            active_skin_id: CompactString::const_new("com.nexus.console"),
            luminance_analyzer: WallpaperLuminanceAnalyzer::new(),
            skins,
            font_system,
        }
    }

    pub fn active_preset_metadata(&self) -> ThemePresetMetadata {
        if let Some(custom) = &self.custom_preset {
            custom.clone()
        } else if self.glass_preset == CrystalGlassPreset::NexusDefault {
            CrystalGlassPreset::nexus_default_metadata(self.system_is_dark)
        } else {
            self.glass_preset.metadata()
        }
    }

    pub fn export_active_preset_json(&self) -> Result<String, serde_json::Error> {
        self.active_preset_metadata().to_json_pretty()
    }

    pub fn import_custom_preset_json(
        &mut self,
        raw_json: &str,
    ) -> Result<ThemePresetMetadata, serde_json::Error> {
        let parsed = ThemePresetMetadata::from_json(raw_json)?;
        self.use_crystal_glass_backdrop = parsed.is_glass_enabled;
        self.custom_preset = Some(parsed.clone());
        Ok(parsed)
    }

    /// Applies `display_saturation` (clamped `0.0..=1.5`, forced `0.0` in `DisplayMode::Mono`).
    pub fn apply_display_saturation(&self, rgb: [f32; 3]) -> [f32; 3] {
        let factor = if self.display_mode == DisplayMode::Mono {
            0.0
        } else {
            self.display_saturation.clamp(0.0, 1.5)
        };
        if (factor - 1.0).abs() <= 1e-4 {
            return [
                rgb[0].clamp(0.0, 1.0),
                rgb[1].clamp(0.0, 1.0),
                rgb[2].clamp(0.0, 1.0),
            ];
        }
        let (h, s, v) = rgb_to_hsv(rgb[0], rgb[1], rgb[2]);
        hsv_to_rgb(h, (s * factor).clamp(0.0, 1.0), v)
    }

    #[inline]
    pub fn primary_vfd_srgb(&self) -> [f32; 4] {
        let base = self.vfd_text_color.resolve_srgb(self.display_mode);
        let sat = self.apply_display_saturation([base[0], base[1], base[2]]);
        [sat[0], sat[1], sat[2], base[3]]
    }

    pub fn active_vfd_lit_appearance(&self) -> VfdLitAppearance {
        let rgba = self.primary_vfd_srgb();
        VfdLitAppearance::make(
            [rgba[0], rgba[1], rgba[2]],
            self.visual_bloom,
            self.visual_bloom_level,
        )
    }

    pub fn increase_text_size(&mut self) -> u16 {
        for &step in &constants::TEXT_SIZE_PERCENTS {
            if step > self.text_size_percent {
                self.text_size_percent = step;
                return step;
            }
        }
        self.text_size_percent = 200;
        200
    }

    pub fn decrease_text_size(&mut self) -> u16 {
        for &step in constants::TEXT_SIZE_PERCENTS.iter().rev() {
            if step < self.text_size_percent {
                self.text_size_percent = step;
                return step;
            }
        }
        self.text_size_percent = 100;
        100
    }

    /// Resolves the Popout Mini-Monitor state & styling tokens from the active `.nexusskin`.
    pub fn resolve_popout(&self, cursor_hovered: bool, force_flat: bool) -> ResolvedPopoutView {
        let canonical = canonical_popout_skin_id(&self.active_skin_id);
        let skin = self
            .skins
            .iter()
            .find(|s| s.id.as_str() == canonical)
            .or_else(|| self.skins.first());

        let effective_opacity = if cursor_hovered && self.popout_opaque_on_hover {
            1.0
        } else {
            self.popout_opacity.clamp(0.2, 1.0)
        };

        let def_win = [0.06, 0.08, 0.12, 0.94];
        let def_tile = [0.04, 0.05, 0.08, 1.0];
        let def_stroke = [0.35, 0.42, 0.52, 0.75];
        let def_hdr = [0.08, 0.11, 0.17, 1.0];

        if let Some(s) = skin {
            let flat = force_flat || s.meters.as_ref().and_then(|m| m.flat).unwrap_or(false);
            let pos = s
                .meters
                .as_ref()
                .and_then(|m| m.positional_palette)
                .unwrap_or(false);
            let quads = if s.quadrants.is_empty() {
                vec![
                    CompactString::const_new("cpu"),
                    CompactString::const_new("gpu"),
                    CompactString::const_new("memory"),
                    CompactString::const_new("thermal"),
                ]
            } else {
                s.quadrants.clone()
            };
            let surf = s.surfaces.as_ref();
            let ink = s.ink.as_ref();
            let metrics = s.metrics.as_ref();
            let meters = s.meters.as_ref();
            let title_ink = ink
                .and_then(|i| i.title.as_deref())
                .and_then(parse_hex_rgba)
                .unwrap_or([0.92, 0.96, 1.0, 1.0]);
            let primary_ink = ink
                .and_then(|i| i.primary.as_deref())
                .and_then(parse_hex_rgba)
                .unwrap_or([0.90, 0.94, 0.98, 1.0]);
            let secondary_ink = ink
                .and_then(|i| i.secondary.as_deref())
                .and_then(parse_hex_rgba)
                .unwrap_or([0.62, 0.68, 0.76, 1.0]);
            let ground_rgba = meters
                .and_then(|m| m.ground.as_deref())
                .and_then(parse_hex_rgba)
                .unwrap_or([0.03, 0.04, 0.06, 1.0]);
            let monospaced = s
                .typography
                .as_ref()
                .and_then(|t| t.value.as_ref().or(t.label.as_ref()))
                .and_then(|f| f.design.as_deref())
                .is_some_and(|d| d.eq_ignore_ascii_case("monospaced"));

            ResolvedPopoutView {
                skin_id: s.id.clone(),
                skin_name: s.name.clone(),
                mode: self.popout_mode,
                width: 289.0,
                height: 263.0,
                effective_opacity,
                flat_segments: flat,
                positional_palette: pos,
                quadrants: quads,
                meter_style: meters
                    .and_then(|m| m.style.clone())
                    .unwrap_or_else(|| CompactString::const_new("rows")),
                columns: meters.and_then(|m| m.columns).unwrap_or(2).clamp(1, 3),
                corner_radius: metrics.and_then(|m| m.corner_radius).unwrap_or(8.0),
                window_bg_rgba: fill_primary_rgba(surf.and_then(|x| x.window.as_ref()), def_win),
                window_stroke_rgba: surf
                    .and_then(|x| x.window_stroke.as_deref())
                    .and_then(parse_hex_rgba)
                    .unwrap_or(def_stroke),
                header_bg_rgba: fill_primary_rgba(surf.and_then(|x| x.header.as_ref()), def_hdr),
                tile_bg_rgba: fill_primary_rgba(surf.and_then(|x| x.tile.as_ref()), def_tile),
                tile_stroke_rgba: surf
                    .and_then(|x| x.tile_stroke.as_deref())
                    .and_then(parse_hex_rgba)
                    .unwrap_or(def_stroke),
                title_ink_rgba: title_ink,
                primary_ink_rgba: primary_ink,
                secondary_ink_rgba: secondary_ink,
                ground_rgba,
                monospaced,
                channel_colors: meters.map(|m| m.channels.clone()).unwrap_or_default(),
            }
        } else {
            ResolvedPopoutView {
                skin_id: CompactString::const_new("com.nexus.console"),
                skin_name: CompactString::const_new("Default"),
                mode: self.popout_mode,
                width: 289.0,
                height: 263.0,
                effective_opacity,
                flat_segments: force_flat,
                positional_palette: false,
                quadrants: vec![CompactString::const_new("cpu")],
                meter_style: CompactString::const_new("rows"),
                columns: 2,
                corner_radius: 8.0,
                window_bg_rgba: def_win,
                window_stroke_rgba: def_stroke,
                header_bg_rgba: def_hdr,
                tile_bg_rgba: def_tile,
                tile_stroke_rgba: def_stroke,
                title_ink_rgba: [1.0; 4],
                primary_ink_rgba: [0.9, 0.94, 0.98, 1.0],
                secondary_ink_rgba: [0.65; 4],
                ground_rgba: [0.03, 0.04, 0.06, 1.0],
                monospaced: true,
                channel_colors: HashMap::new(),
            }
        }
    }
}
