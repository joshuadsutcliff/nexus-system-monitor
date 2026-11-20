use compact_str::CompactString;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::Path;
use thiserror::Error;

/// 7 global display modes (`main.DisplayMode` at `0x1007f0978`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DisplayMode {
    Color,
    Mono,
    Green,
    Amber,
    Blue,
    Red,
    Purple,
}

impl DisplayMode {
    /// Returns the UI title (`0x1000cf910`).
    #[inline]
    pub const fn title(self) -> &'static str {
        match self {
            Self::Color => "Color",
            Self::Mono => "Mono",
            Self::Green => "Green",
            Self::Amber => "Amber",
            Self::Blue => "Blue",
            Self::Red => "Red",
            Self::Purple => "Purple",
        }
    }

    /// Returns true if the mode forces dark appearance (`0x1000cfcec`).
    #[inline]
    pub const fn forces_dark_appearance(self) -> bool {
        matches!(
            self,
            Self::Green | Self::Amber | Self::Blue | Self::Red | Self::Purple
        )
    }

    /// Returns the `CIColor` monochrome tint sRGB (`0x100495618`), or `None` for `Color`/`Mono`.
    #[inline]
    pub const fn tint_srgb(self) -> Option<[f32; 3]> {
        match self {
            Self::Color | Self::Mono => None,
            Self::Green => Some([0.24, 1.0, 0.34]),
            Self::Amber => Some([1.0, 0.72, 0.16]),
            Self::Blue => Some([0.24, 0.544, 1.0]),
            Self::Red => Some([1.0, 0.34, 0.30]),
            Self::Purple => Some([0.82, 0.34, 1.0]),
        }
    }

    /// Returns the mode-specific opaque window background sRGB (`0x1000cfe54`), or `None` when system default applies.
    #[inline]
    pub const fn window_background_srgb(self) -> Option<[f32; 4]> {
        match self {
            Self::Color | Self::Mono => None,
            Self::Green => Some([0.0384, 0.16, 0.0544, 1.0]),
            Self::Amber => Some([0.16, 0.1152, 0.0256, 1.0]),
            Self::Blue => Some([0.03, 0.05, 0.09, 1.0]),
            Self::Red => Some([0.16, 0.0544, 0.048, 1.0]),
            Self::Purple => Some([0.1312, 0.0544, 0.16, 1.0]),
        }
    }
}

/// Primary digital readout and VFD bar color selector (`0x1007f0a80`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum VfdTextColor {
    Automatic,
    Amber,
    Green,
    Blue,
}

impl VfdTextColor {
    /// Resolves sRGB `[R, G, B, A]` for a given `DisplayMode` (`VFDTextColor.color(for:)` at `0x100224cc0`).
    #[inline]
    pub const fn resolve_srgb(self, mode: DisplayMode) -> [f32; 4] {
        match self {
            Self::Amber => [1.0, 0.65, 0.0, 1.0],
            Self::Green => [0.0, 1.0, 0.0, 1.0],
            Self::Blue => [0.0, 1.0, 1.0, 1.0],
            Self::Automatic => match mode {
                DisplayMode::Color | DisplayMode::Blue => [0.0, 1.0, 1.0, 1.0],
                DisplayMode::Mono => [1.0, 1.0, 1.0, 1.0],
                DisplayMode::Green => [0.0, 1.0, 0.0, 1.0],
                DisplayMode::Amber => [1.0, 0.65, 0.0, 1.0],
                DisplayMode::Red => [1.0, 0.337_255, 0.298_039, 1.0],
                DisplayMode::Purple => [0.819_608, 0.337_255, 1.0, 1.0],
            },
        }
    }
}

/// Popout Mini-Monitor window mode (`PopoutMode` at `0x1007f1418`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PopoutMode {
    Off,
    Desktop,
    Normal,
    TopMost,
}

pub mod constants {
    /// 6 discrete text size percentages.
    pub const TEXT_SIZE_PERCENTS: [u16; 6] = [100, 110, 125, 150, 175, 200];

    /// VFD phosphor ballistics (`0x100545204`, `0x100545218`).
    pub const VFD_ATTACK_SECONDS: f64 = 0.10;
    pub const VFD_DECAY_SECONDS: f64 = 0.22;

    /// Standard UI transition durations (`0x10018d974`, `0x100145884`, `0x100154f10`).
    pub const ANIM_FAST_EASE_OUT_S: f64 = 0.12;
    pub const ANIM_SETTINGS_EASE_IN_OUT_S: f64 = 0.25;
    pub const ANIM_SCROLL_EASE_IN_OUT_S: f64 = 0.30;

    /// Process row excitation and exited retention (`0x100544fa8..0x10054525c`).
    pub const RANK_GLOW_TINT_SRGB: [f32; 3] = [0.341_176, 1.0, 0.450_98];
    pub const RANK_GLOW_DURATION_S: f64 = 1.30;
    pub const RANK_GLOW_PEAK_ALPHA: f32 = 0.46;
    pub const RANK_GLOW_HEAD_COUNT: usize = 10;

    pub const EXITED_WASH_SRGB: [f32; 3] = [1.0, 0.341_176, 0.372_549];
    pub const EXITED_RETENTION_S: f64 = 5.0;
    pub const EXITED_PEAK_ALPHA: f32 = 0.30;
    pub const EXITED_TEXT_DIM: f32 = 0.55;

    /// 16-step positional VFD key stops (`kPositionalKeyBytes` at `0x10066ed20`).
    pub const VFD_POSITIONAL_STEPS: usize = 16;
    pub const VFD_STOP_GREEN_SRGB: [u8; 3] = [48, 209, 88];
    pub const VFD_STOP_YELLOW_SRGB: [u8; 3] = [255, 214, 10];
    pub const VFD_STOP_RED_SRGB: [u8; 3] = [255, 69, 58];

    /// `.nexusskin` security validation limits.
    pub const SKIN_FORMAT_VERSION: u32 = 1;
    pub const SKIN_MAX_DOCUMENT_BYTES: usize = 262_144;
    pub const SKIN_MAX_IMAGE_BYTES: usize = 8_388_608;
    pub const SKIN_MAX_FILES: usize = 64;
    pub const SKIN_MAX_IMAGE_EXTENT: f32 = 4096.0;
    pub const SKIN_MAX_IMAGE_PIXELS: u64 = 4_194_304;
    pub const SKIN_FONT_SIZE_MIN: f32 = 7.0;
    pub const SKIN_FONT_SIZE_MAX: f32 = 72.0;
    pub const SKIN_BINARY_FONT_CLAMP_MIN: f32 = 6.0;
    pub const SKIN_BINARY_FONT_CLAMP_MAX: f32 = 24.0;
}

/// Errors produced when validating or parsing a `.nexusskin` bundle or JSON document.
#[derive(Debug, Error, PartialEq)]
pub enum SkinValidationError {
    #[error("skin format version {0} is unsupported (expected 1)")]
    UnsupportedFormatVersion(u32),
    #[error("skin document size {0} bytes exceeds maximum {1} bytes")]
    DocumentTooLarge(usize, usize),
    #[error("skin image size {0} bytes exceeds maximum {1} bytes")]
    ImageTooLarge(usize, usize),
    #[error("skin bundle file count {0} exceeds maximum {1} files")]
    TooManyFiles(usize, usize),
    #[error("skin image dimensions {width}x{height} exceed maximum extent or pixel budget")]
    InvalidImageExtent { width: f32, height: f32 },
    #[error("unsafe or unsupported skin image filename: {0}")]
    UnsafeImageName(String),
    #[error("invalid skin JSON: {0}")]
    JsonError(String),
}

/// Validator 1: skin format version check (`version == 1`).
#[inline]
pub const fn validate_skin_format_version(version: u32) -> Result<(), SkinValidationError> {
    if version == constants::SKIN_FORMAT_VERSION {
        Ok(())
    } else {
        Err(SkinValidationError::UnsupportedFormatVersion(version))
    }
}

/// Validator 2: maximum document size check (256 KiB).
#[inline]
pub const fn validate_skin_document_bytes(len: usize) -> Result<(), SkinValidationError> {
    if len <= constants::SKIN_MAX_DOCUMENT_BYTES {
        Ok(())
    } else {
        Err(SkinValidationError::DocumentTooLarge(
            len,
            constants::SKIN_MAX_DOCUMENT_BYTES,
        ))
    }
}

/// Validator 3: maximum image payload size check (8 MiB).
#[inline]
pub const fn validate_skin_image_bytes(len: usize) -> Result<(), SkinValidationError> {
    if len <= constants::SKIN_MAX_IMAGE_BYTES {
        Ok(())
    } else {
        Err(SkinValidationError::ImageTooLarge(
            len,
            constants::SKIN_MAX_IMAGE_BYTES,
        ))
    }
}

/// Validator 4: maximum file count in a skin directory bundle (64 files).
#[inline]
pub const fn validate_skin_file_count(count: usize) -> Result<(), SkinValidationError> {
    if count <= constants::SKIN_MAX_FILES {
        Ok(())
    } else {
        Err(SkinValidationError::TooManyFiles(
            count,
            constants::SKIN_MAX_FILES,
        ))
    }
}

/// Validator 5: maximum image extent (`<= 4096 px`) and total pixel budget (`<= 4 MP`).
#[inline]
pub fn validate_skin_image_extent(width: f32, height: f32) -> Result<(), SkinValidationError> {
    if !width.is_finite()
        || !height.is_finite()
        || width < 1.0
        || height < 1.0
        || width > constants::SKIN_MAX_IMAGE_EXTENT
        || height > constants::SKIN_MAX_IMAGE_EXTENT
    {
        return Err(SkinValidationError::InvalidImageExtent { width, height });
    }
    let pixels = (width as f64) * (height as f64);
    if pixels > (constants::SKIN_MAX_IMAGE_PIXELS as f64) {
        return Err(SkinValidationError::InvalidImageExtent { width, height });
    }
    Ok(())
}

/// Validator 6a: clamps skin font sizes to `[7.0, 72.0]` pt (`SKIN_FONT_SIZE_MIN..=SKIN_FONT_SIZE_MAX`).
#[inline]
pub fn clamp_skin_font_size(size: f32) -> f32 {
    if !size.is_finite() {
        return 10.0;
    }
    size.clamp(constants::SKIN_FONT_SIZE_MIN, constants::SKIN_FONT_SIZE_MAX)
}

/// Validator 6b: clamps popout skin font size to `[6.0, 24.0]` pt.
#[inline]
pub fn clamp_popout_skin_font_size(size: f32) -> f32 {
    if !size.is_finite() {
        return 10.0;
    }
    size.clamp(
        constants::SKIN_BINARY_FONT_CLAMP_MIN,
        constants::SKIN_BINARY_FONT_CLAMP_MAX,
    )
}

/// Checks whether an image file extension is a supported portable raster format.
/// Accepts `png`, `jpg`, `jpeg`, `gif`, `tif`, `tiff`, `bmp` (case-insensitive, optional leading `.`).
pub fn skin_image_extension_is_portable(ext: &str) -> bool {
    let trimmed = ext.strip_prefix('.').unwrap_or(ext);
    matches!(
        trimmed.to_ascii_lowercase().as_str(),
        "png" | "jpg" | "jpeg" | "gif" | "tif" | "tiff" | "bmp"
    )
}

/// Checks whether a skin document extension is supported (`json` or `nexusskin`).
pub fn skin_document_extension_is_supported(ext: &str) -> bool {
    let trimmed = ext.strip_prefix('.').unwrap_or(ext);
    matches!(trimmed.to_ascii_lowercase().as_str(), "json" | "nexusskin")
}

/// Canonicalizes legacy or empty popout skin IDs to `"com.nexus.console"`.
pub fn canonical_popout_skin_id(id: &str) -> &str {
    if id.is_empty() || id == "com.nexus.console.two" {
        "com.nexus.console"
    } else {
        id
    }
}

/// Validator 7: enforces safe relative image filenames within a `.nexusskin` bundle.
///
/// Enforces:
/// - Byte length in `1..=128`
/// - Does not start with `.` and does not end with `' '` or `.`
/// - Rejects `..`, control bytes (`< 0x20` or `0x7f`), and reserved characters `"`, `*`, `/`, `:`, `<`, `>`, `?`, `\`, `|`
/// - Rejects Windows device stems (`con`, `prn`, `aux`, `nul`, `conin$`, `conout$`, `com1`..`com9`, `lpt1`..`lpt9`)
/// - Requires a portable image extension (`png`, `jpg`, `jpeg`, `gif`, `tif`, `tiff`, `bmp`)
pub fn skin_image_name_is_safe(name: &str) -> bool {
    let bytes = name.as_bytes();
    if bytes.is_empty() || bytes.len() > 128 {
        return false;
    }
    if bytes[0] == b'.' || bytes[bytes.len() - 1] == b' ' || bytes[bytes.len() - 1] == b'.' {
        return false;
    }
    if name.contains("..") {
        return false;
    }
    for &b in bytes {
        if b < 0x20 || b == 0x7f {
            return false;
        }
        if matches!(
            b,
            b'"' | b'*' | b'/' | b':' | b'<' | b'>' | b'?' | b'\\' | b'|'
        ) {
            return false;
        }
    }
    let first_stem = name.split('.').next().unwrap_or("");
    if first_stem.is_empty() || is_reserved_dos_stem(first_stem) {
        return false;
    }
    let Some((_, ext)) = name.rsplit_once('.') else {
        return false;
    };
    skin_image_extension_is_portable(ext)
}

fn is_reserved_dos_stem(stem: &str) -> bool {
    let lower = stem.to_ascii_lowercase();
    matches!(
        lower.as_str(),
        "con"
            | "prn"
            | "aux"
            | "nul"
            | "conin$"
            | "conout$"
            | "com1"
            | "com2"
            | "com3"
            | "com4"
            | "com5"
            | "com6"
            | "com7"
            | "com8"
            | "com9"
            | "lpt1"
            | "lpt2"
            | "lpt3"
            | "lpt4"
            | "lpt5"
            | "lpt6"
            | "lpt7"
            | "lpt8"
            | "lpt9"
    )
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum SurfaceFill {
    Solid(CompactString),
    Gradient { gradient: Vec<CompactString> },
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinMetrics {
    pub corner_radius: Option<f32>,
    pub border_width: Option<f32>,
    pub padding: Option<f32>,
    pub header_bottom_padding: Option<f32>,
    pub header_gap: Option<f32>,
    pub section_spacing: Option<f32>,
    pub tile_corner_radius: Option<f32>,
    pub tile_stroke_width: Option<f32>,
    pub badge_corner_radius: Option<f32>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinLogo {
    pub file: CompactString,
    pub width: f32,
    pub height: f32,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinHeader {
    pub title: Option<CompactString>,
    pub logo: Option<SkinLogo>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinSurfaces {
    pub window: Option<SurfaceFill>,
    pub window_stroke: Option<CompactString>,
    pub header: Option<SurfaceFill>,
    pub header_rule: Option<CompactString>,
    pub tile: Option<SurfaceFill>,
    pub tile_stroke: Option<CompactString>,
    pub tile_gloss: Option<SurfaceFill>,
    pub badge_stroke: Option<CompactString>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinFontSpec {
    pub size: f32,
    pub weight: Option<CompactString>,
    pub design: Option<CompactString>,
}

impl SkinFontSpec {
    #[inline]
    pub fn clamped(&self) -> Self {
        Self {
            size: clamp_skin_font_size(self.size),
            weight: self.weight.clone(),
            design: self.design.clone(),
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinTypography {
    pub title: Option<SkinFontSpec>,
    pub title_tracking: Option<f32>,
    pub heading: Option<SkinFontSpec>,
    pub label: Option<SkinFontSpec>,
    pub value: Option<SkinFontSpec>,
    pub row: Option<SkinFontSpec>,
    pub row_value: Option<SkinFontSpec>,
    pub aux_line: Option<SkinFontSpec>,
}

impl SkinTypography {
    pub fn clamp_all(&mut self) {
        if let Some(s) = &mut self.title {
            *s = s.clamped();
        }
        if let Some(s) = &mut self.heading {
            *s = s.clamped();
        }
        if let Some(s) = &mut self.label {
            *s = s.clamped();
        }
        if let Some(s) = &mut self.value {
            *s = s.clamped();
        }
        if let Some(s) = &mut self.row {
            *s = s.clamped();
        }
        if let Some(s) = &mut self.row_value {
            *s = s.clamped();
        }
        if let Some(s) = &mut self.aux_line {
            *s = s.clamped();
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinInk {
    pub title: Option<CompactString>,
    pub primary: Option<CompactString>,
    pub secondary: Option<CompactString>,
    pub control: Option<CompactString>,
    pub badge: Option<CompactString>,
    pub grip: Option<CompactString>,
    pub rule: Option<CompactString>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinGraphConfig {
    pub shows_grid: Option<bool>,
    pub height: Option<f32>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinInset {
    pub top: f32,
    pub bottom: f32,
    pub leading: f32,
    pub trailing: f32,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinChannelColors {
    pub accent: Option<CompactString>,
    pub lit: Option<CompactString>,
    pub unlit: Option<CompactString>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkinMeters {
    pub style: Option<CompactString>,
    pub columns: Option<u32>,
    pub auxiliary: Option<bool>,
    pub positional_palette: Option<bool>,
    pub ground: Option<CompactString>,
    pub flat: Option<bool>,
    pub split: Option<u32>,
    pub segments: Option<u32>,
    pub inset: Option<SkinInset>,
    pub graph: Option<SkinGraphConfig>,
    #[serde(default)]
    pub channels: HashMap<CompactString, SkinChannelColors>,
}

/// Complete `.nexusskin` v1 schema (`PopoutSkin`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PopoutSkin {
    pub format: u32,
    pub id: CompactString,
    pub name: CompactString,
    pub author: Option<CompactString>,
    pub description: Option<CompactString>,
    pub symbol: Option<CompactString>,
    #[serde(default)]
    pub quadrants: Vec<CompactString>,
    pub metrics: Option<SkinMetrics>,
    pub header: Option<SkinHeader>,
    pub surfaces: Option<SkinSurfaces>,
    pub typography: Option<SkinTypography>,
    pub ink: Option<SkinInk>,
    pub meters: Option<SkinMeters>,
}

impl PopoutSkin {
    /// Parses and validates a `.nexusskin` JSON document against all security rules.
    pub fn parse_and_validate(raw_json: &str) -> Result<Self, SkinValidationError> {
        validate_skin_document_bytes(raw_json.len())?;
        let mut skin: Self = serde_json::from_str(raw_json)
            .map_err(|e| SkinValidationError::JsonError(e.to_string()))?;
        validate_skin_format_version(skin.format)?;
        skin.id = CompactString::from(canonical_popout_skin_id(&skin.id));

        if let Some(header) = &skin.header
            && let Some(logo) = &header.logo
        {
            if !skin_image_name_is_safe(&logo.file) {
                return Err(SkinValidationError::UnsafeImageName(logo.file.to_string()));
            }
            validate_skin_image_extent(logo.width, logo.height)?;
        }
        if let Some(typo) = &mut skin.typography {
            typo.clamp_all();
        }
        Ok(skin)
    }

    /// Loads and validates either a standalone `.nexusskin` JSON file or a `.nexusskin/` directory bundle.
    pub fn load_from_path(path: &Path) -> Result<Self, SkinValidationError> {
        if path.is_dir() {
            let entries: Vec<_> = fs::read_dir(path)
                .map_err(|e| SkinValidationError::JsonError(e.to_string()))?
                .collect::<Result<Vec<_>, _>>()
                .map_err(|e| SkinValidationError::JsonError(e.to_string()))?;
            validate_skin_file_count(entries.len())?;

            let manifest_path = path.join("skin.json");
            let raw_bytes = fs::read(&manifest_path)
                .map_err(|e| SkinValidationError::JsonError(e.to_string()))?;
            validate_skin_document_bytes(raw_bytes.len())?;
            let raw_str = String::from_utf8(raw_bytes)
                .map_err(|e| SkinValidationError::JsonError(e.to_string()))?;
            let skin = Self::parse_and_validate(&raw_str)?;

            if let Some(header) = &skin.header
                && let Some(logo) = &header.logo
            {
                let img_path = path.join(logo.file.as_str());
                if let Ok(meta) = fs::metadata(&img_path) {
                    validate_skin_image_bytes(meta.len() as usize)?;
                }
            }
            Ok(skin)
        } else {
            let raw_bytes =
                fs::read(path).map_err(|e| SkinValidationError::JsonError(e.to_string()))?;
            validate_skin_document_bytes(raw_bytes.len())?;
            let raw_str = String::from_utf8(raw_bytes)
                .map_err(|e| SkinValidationError::JsonError(e.to_string()))?;
            Self::parse_and_validate(&raw_str)
        }
    }
}

/// Bundled fonts (`Michroma-Regular.ttf`, `LED Dot-Matrix.ttf`) and all 5 `.nexusskin` definitions.
pub mod bundled {
    use super::{PopoutSkin, SkinValidationError, validate_skin_image_bytes};

    pub const FONT_MICHROMA_REGULAR_TTF: &[u8] =
        include_bytes!("../assets/fonts/Michroma-Regular.ttf");
    pub const FONT_LED_DOT_MATRIX_TTF: &[u8] = include_bytes!("../assets/fonts/LED Dot-Matrix.ttf");

    pub const SKIN_CONSOLE_JSON: &str = include_str!("../assets/skins/Console.nexusskin");
    pub const SKIN_TRACES_JSON: &str = include_str!("../assets/skins/Traces.nexusskin");
    pub const SKIN_WINAMP_JSON: &str = include_str!("../assets/skins/Winamp.nexusskin");
    pub const SKIN_WMP_JSON: &str = include_str!("../assets/skins/Windows Media Player.nexusskin");
    pub const SKIN_WINTASKMAN_JSON: &str =
        include_str!("../assets/skins/Windows Task Manager.nexusskin/skin.json");
    pub const SKIN_WINTASKMAN_LOGO_PNG: &[u8] =
        include_bytes!("../assets/skins/Windows Task Manager.nexusskin/taskman.png");

    /// Loads and validates all 5 bundled `.nexusskin` definitions and their embedded image assets.
    pub fn load_bundled_skins() -> Result<Vec<PopoutSkin>, SkinValidationError> {
        validate_skin_image_bytes(SKIN_WINTASKMAN_LOGO_PNG.len())?;
        let raw_skins = [
            SKIN_CONSOLE_JSON,
            SKIN_TRACES_JSON,
            SKIN_WINAMP_JSON,
            SKIN_WMP_JSON,
            SKIN_WINTASKMAN_JSON,
        ];
        raw_skins
            .into_iter()
            .map(PopoutSkin::parse_and_validate)
            .collect()
    }
}
