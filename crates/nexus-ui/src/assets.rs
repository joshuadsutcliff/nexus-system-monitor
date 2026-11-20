use nexus_core::{PopoutSkin, SkinValidationError, validate_skin_image_bytes};
use std::fs;
use std::path::{Path, PathBuf};

/// Compile-time embedded `Michroma-Regular.ttf` inside `crates/nexus-ui/assets/fonts/`.
pub const UI_FONT_MICHROMA_REGULAR_TTF: &[u8] =
    include_bytes!("../assets/fonts/Michroma-Regular.ttf");

/// Compile-time embedded `LED Dot-Matrix.ttf` inside `crates/nexus-ui/assets/fonts/`.
pub const UI_FONT_LED_DOT_MATRIX_TTF: &[u8] = include_bytes!("../assets/fonts/LED Dot-Matrix.ttf");

/// Compile-time embedded `AppIcon.png` inside `crates/nexus-ui/assets/`.
pub const UI_APP_ICON_PNG: &[u8] = include_bytes!("../assets/AppIcon.png");

/// Compile-time embedded `.nexusskin` v1 JSON definitions inside `crates/nexus-ui/assets/skins/`.
pub const UI_SKIN_CONSOLE_JSON: &str = include_str!("../assets/skins/Console.nexusskin");
pub const UI_SKIN_TRACES_JSON: &str = include_str!("../assets/skins/Traces.nexusskin");
pub const UI_SKIN_WINAMP_JSON: &str = include_str!("../assets/skins/Winamp.nexusskin");
pub const UI_SKIN_WMP_JSON: &str = include_str!("../assets/skins/Windows Media Player.nexusskin");
pub const UI_SKIN_WINTASKMAN_JSON: &str =
    include_str!("../assets/skins/Windows Task Manager.nexusskin/skin.json");
pub const UI_SKIN_WINTASKMAN_LOGO_PNG: &[u8] =
    include_bytes!("../assets/skins/Windows Task Manager.nexusskin/taskman.png");

/// Resolves the macOS `.app` bundle `Contents/Resources` directory relative to `std::env::current_exe()`
/// when running inside `Nexus System Monitor.app/Contents/MacOS/nexus-monitor`.
pub fn resolve_bundle_resources_dir() -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let macos_dir = exe.parent()?;
    if macos_dir.file_name().and_then(|n| n.to_str()) != Some("MacOS") {
        return None;
    }
    let contents_dir = macos_dir.parent()?;
    let resources_dir = contents_dir.join("Resources");
    if resources_dir.is_dir() {
        Some(resources_dir)
    } else {
        None
    }
}

/// Loads `(michroma_ttf, led_dot_matrix_ttf)` from the `.app` bundle `Contents/Resources/` if available,
/// falling back to the compile-time bytes embedded directly in `crates/nexus-ui/assets/fonts/`.
pub fn load_standalone_fonts(override_resources_dir: Option<&Path>) -> (Vec<u8>, Vec<u8>) {
    let res_dir = override_resources_dir
        .map(Path::to_path_buf)
        .or_else(resolve_bundle_resources_dir);

    if let Some(dir) = res_dir {
        let michroma_path = dir.join("Michroma-Regular.ttf");
        let led_path = dir.join("LED Dot-Matrix.ttf");
        if let (Ok(m), Ok(l)) = (fs::read(&michroma_path), fs::read(&led_path))
            && !m.is_empty()
            && !l.is_empty()
        {
            return (m, l);
        }
    }
    (
        UI_FONT_MICHROMA_REGULAR_TTF.to_vec(),
        UI_FONT_LED_DOT_MATRIX_TTF.to_vec(),
    )
}

/// Loads and validates all 5 `.nexusskin` definitions from `Contents/Resources/Skins/` if running inside
/// a packaged `.app` bundle, falling back to the compile-time assets in `crates/nexus-ui/assets/skins/`.
pub fn load_standalone_skins(
    override_resources_dir: Option<&Path>,
) -> Result<Vec<PopoutSkin>, SkinValidationError> {
    let res_dir = override_resources_dir
        .map(Path::to_path_buf)
        .or_else(resolve_bundle_resources_dir);

    if let Some(dir) = res_dir {
        let skins_dir = dir.join("Skins");
        if skins_dir.is_dir() {
            let candidates = [
                skins_dir.join("Console.nexusskin"),
                skins_dir.join("Traces.nexusskin"),
                skins_dir.join("Winamp.nexusskin"),
                skins_dir.join("Windows Media Player.nexusskin"),
                skins_dir.join("Windows Task Manager.nexusskin"),
            ];
            if candidates.iter().all(|p| p.exists()) {
                let mut loaded = Vec::with_capacity(5);
                for path in &candidates {
                    loaded.push(PopoutSkin::load_from_path(path)?);
                }
                return Ok(loaded);
            }
        }
    }

    validate_skin_image_bytes(UI_SKIN_WINTASKMAN_LOGO_PNG.len())?;
    let raw_skins = [
        UI_SKIN_CONSOLE_JSON,
        UI_SKIN_TRACES_JSON,
        UI_SKIN_WINAMP_JSON,
        UI_SKIN_WMP_JSON,
        UI_SKIN_WINTASKMAN_JSON,
    ];
    raw_skins
        .into_iter()
        .map(PopoutSkin::parse_and_validate)
        .collect()
}
