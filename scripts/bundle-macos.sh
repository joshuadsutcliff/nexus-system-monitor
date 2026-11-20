#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUNDLE_DIR="${REPO_ROOT}/target/release/bundle/Nexus System Monitor.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"
SKINS_DIR="${RESOURCES_DIR}/Skins"
ICON_SRC="${REPO_ROOT}/crates/nexus-ui/assets/AppIcon.png"
ICONSET_DIR="${REPO_ROOT}/target/release/bundle/AppIcon.iconset"

echo "==> Building release binaries (nexus-monitor, nexus, nexus-cli)..."
cd "${REPO_ROOT}"
cargo build --release -p nexus-ui --bin nexus-monitor -p nexus-cli --bin nexus --bin nexus-cli

echo "==> Assembling macOS application bundle at: ${BUNDLE_DIR}"
rm -rf "${BUNDLE_DIR}" "${ICONSET_DIR}"
mkdir -p "${MACOS_DIR}" "${SKINS_DIR}" "${ICONSET_DIR}"

cp "${REPO_ROOT}/target/release/nexus-monitor" "${MACOS_DIR}/nexus-monitor"
cp "${REPO_ROOT}/target/release/nexus" "${MACOS_DIR}/nexus"
cp "${REPO_ROOT}/target/release/nexus-cli" "${MACOS_DIR}/nexus-cli"
chmod +x "${MACOS_DIR}/nexus-monitor" "${MACOS_DIR}/nexus" "${MACOS_DIR}/nexus-cli"

cp "${REPO_ROOT}/crates/nexus-ui/assets/fonts/Michroma-Regular.ttf" "${RESOURCES_DIR}/Michroma-Regular.ttf"
cp "${REPO_ROOT}/crates/nexus-ui/assets/fonts/LED Dot-Matrix.ttf" "${RESOURCES_DIR}/LED Dot-Matrix.ttf"
cp -R "${REPO_ROOT}/crates/nexus-ui/assets/skins/"* "${SKINS_DIR}/"

if [[ -f "${ICON_SRC}" ]]; then
    echo "==> Building AppIcon.icns from ${ICON_SRC}..."
    sips -z 16 16     "${ICON_SRC}" --out "${ICONSET_DIR}/icon_16x16.png" >/dev/null
    sips -z 32 32     "${ICON_SRC}" --out "${ICONSET_DIR}/icon_16x16@2x.png" >/dev/null
    sips -z 32 32     "${ICON_SRC}" --out "${ICONSET_DIR}/icon_32x32.png" >/dev/null
    sips -z 64 64     "${ICON_SRC}" --out "${ICONSET_DIR}/icon_32x32@2x.png" >/dev/null
    sips -z 128 128   "${ICON_SRC}" --out "${ICONSET_DIR}/icon_128x128.png" >/dev/null
    sips -z 256 256   "${ICON_SRC}" --out "${ICONSET_DIR}/icon_128x128@2x.png" >/dev/null
    sips -z 256 256   "${ICON_SRC}" --out "${ICONSET_DIR}/icon_256x256.png" >/dev/null
    sips -z 512 512   "${ICON_SRC}" --out "${ICONSET_DIR}/icon_256x256@2x.png" >/dev/null
    sips -z 512 512   "${ICON_SRC}" --out "${ICONSET_DIR}/icon_512x512.png" >/dev/null
    sips -z 1024 1024 "${ICON_SRC}" --out "${ICONSET_DIR}/icon_512x512@2x.png" >/dev/null
    iconutil -c icns "${ICONSET_DIR}" -o "${RESOURCES_DIR}/AppIcon.icns"
    rm -rf "${ICONSET_DIR}"
fi

cat > "${CONTENTS_DIR}/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>Nexus System Monitor</string>
    <key>CFBundleExecutable</key>
    <string>nexus-monitor</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleIdentifier</key>
    <string>com.joshsutcliff.nexusmonitor</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Nexus System Monitor</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>0.8.1</string>
    <key>CFBundleVersion</key>
    <string>0.8.1</string>
    <key>LSMinimumSystemVersion</key>
    <string>15.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSRequiresAquaSystemAppearance</key>
    <false/>
    <key>ATSApplicationFontsPath</key>
    <string>.</string>
</dict>
</plist>
EOF

echo "==> Clearing quarantine/extended attributes and ad-hoc signing bundle..."
xattr -cr "${BUNDLE_DIR}"
codesign --force -s - "${MACOS_DIR}/nexus-cli"
codesign --force -s - "${MACOS_DIR}/nexus"
codesign --force -s - "${MACOS_DIR}/nexus-monitor"
codesign --force --deep -s - "${BUNDLE_DIR}"
codesign --verify --deep --strict "${BUNDLE_DIR}"

echo "==> Bundle ready and verified: ${BUNDLE_DIR}"
ls -la "${CONTENTS_DIR}" "${MACOS_DIR}" "${RESOURCES_DIR}"
