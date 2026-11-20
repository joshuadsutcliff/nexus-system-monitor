#!/usr/bin/env bash
# create-app-bundle.sh — Construct a macOS .app bundle from a Rust target release directory,
# then create a .dmg with drag-to-Applications layout and a portable .tar.gz.
#
# Usage:
#   bash installer/macos/create-app-bundle.sh <rid> <version> [output-dir] [cli-binary-path] [release-bin-dir]
#
#   rid              : osx-x64 or osx-arm64
#   version          : semver string, e.g. 0.8.0
#   output-dir       : directory for .dmg and .tar.gz (default: dist/)
#   cli-binary-path  : optional path to the published "nexus" CLI binary
#   release-bin-dir  : path to the Rust release directory containing nexus-monitor

set -euo pipefail

RID="${1:?Usage: $0 <rid> <version> [output-dir] [cli-binary-path] [release-bin-dir]}"
VERSION="${2:?Version is required}"
OUTDIR="${3:-dist}"
CLI_BINARY="${4:-}"
RELEASE_DIR="${5:-}"

case "${RID}" in
  osx-arm64)
    OS_LABEL="MacOS"
    DEFAULT_RELEASE_DIR="target/aarch64-apple-darwin/release"
    ;;
  osx-x64)
    OS_LABEL="MacOS-Intel"
    DEFAULT_RELEASE_DIR="target/x86_64-apple-darwin/release"
    ;;
  *)
    echo "Error: unsupported RID '${RID}'"
    exit 1
    ;;
esac

if [[ -z "${RELEASE_DIR}" ]]; then
  if [[ -f "${DEFAULT_RELEASE_DIR}/nexus-monitor" ]]; then
    RELEASE_DIR="${DEFAULT_RELEASE_DIR}"
  else
    RELEASE_DIR="target/release"
  fi
fi

if [[ -z "${CLI_BINARY}" && -f "${RELEASE_DIR}/nexus" ]]; then
  CLI_BINARY="${RELEASE_DIR}/nexus"
fi

APP_NAME="NexusMonitor"
BUNDLE_NAME="Nexus System Monitor.app"

DMG_NAME="${APP_NAME}-${OS_LABEL}-${VERSION}.dmg"
DMG_PATH="${OUTDIR}/${DMG_NAME}"
TARBALL="${OUTDIR}/${APP_NAME}-${OS_LABEL}-Portable-${VERSION}.tar.gz"

STAGE_ROOT="${OUTDIR}/stage-${RID}"
APP_DEST="${STAGE_ROOT}/${BUNDLE_NAME}"

if [[ ! -f "${RELEASE_DIR}/nexus-monitor" ]]; then
  echo "Error: release binary not found at ${RELEASE_DIR}/nexus-monitor"
  exit 1
fi

mkdir -p "${OUTDIR}"
rm -rf "${STAGE_ROOT}"
mkdir -p "${APP_DEST}/Contents/MacOS"
mkdir -p "${APP_DEST}/Contents/Resources/Skins"

echo "→ Constructing ${BUNDLE_NAME} for ${RID} ..."
cp "${RELEASE_DIR}/nexus-monitor" "${APP_DEST}/Contents/MacOS/nexus-monitor"
chmod +x "${APP_DEST}/Contents/MacOS/nexus-monitor"
ln -s "nexus-monitor" "${APP_DEST}/Contents/MacOS/NexusMonitor"

if [[ -f "${RELEASE_DIR}/nexus" ]]; then
  cp "${RELEASE_DIR}/nexus" "${APP_DEST}/Contents/MacOS/nexus"
  chmod +x "${APP_DEST}/Contents/MacOS/nexus"
fi
if [[ -f "${RELEASE_DIR}/nexus-cli" ]]; then
  cp "${RELEASE_DIR}/nexus-cli" "${APP_DEST}/Contents/MacOS/nexus-cli"
  chmod +x "${APP_DEST}/Contents/MacOS/nexus-cli"
fi

cp "installer/macos/Info.plist" "${APP_DEST}/Contents/Info.plist"
sed -i '' "s/VERSION_PLACEHOLDER/${VERSION}/g" "${APP_DEST}/Contents/Info.plist"

cp "crates/nexus-ui/assets/nexus-icon.icns" "${APP_DEST}/Contents/Resources/AppIcon.icns"
cp "crates/nexus-ui/assets/nexus-icon.icns" "${APP_DEST}/Contents/Resources/nexus-icon.icns"
cp "crates/nexus-ui/assets/fonts/Michroma-Regular.ttf" "${APP_DEST}/Contents/Resources/Michroma-Regular.ttf"
cp "crates/nexus-ui/assets/fonts/LED Dot-Matrix.ttf" "${APP_DEST}/Contents/Resources/LED Dot-Matrix.ttf"
cp -R crates/nexus-ui/assets/skins/* "${APP_DEST}/Contents/Resources/Skins/"

echo "→ Signing bundle ..."
xattr -cr "${APP_DEST}" 2>/dev/null || true
CODESIGN_IDENTITY="${CODESIGN_IDENTITY:--}"
ENTITLEMENTS="installer/macos/entitlements.plist"
if [[ "${CODESIGN_IDENTITY}" != "-" && -f "${ENTITLEMENTS}" ]]; then
  for bin in "${APP_DEST}/Contents/MacOS/"*; do
    if [[ -f "${bin}" && ! -L "${bin}" ]]; then
      codesign --force --sign "${CODESIGN_IDENTITY}" --timestamp --options runtime --entitlements "${ENTITLEMENTS}" "${bin}"
    fi
  done
  codesign --deep --force --sign "${CODESIGN_IDENTITY}" --timestamp \
    --options runtime --entitlements "${ENTITLEMENTS}" "${APP_DEST}"
else
  for bin in "${APP_DEST}/Contents/MacOS/"*; do
    if [[ -f "${bin}" && ! -L "${bin}" ]]; then
      codesign --force --sign - --timestamp=none "${bin}"
    fi
  done
  codesign --deep --force --sign - --timestamp=none "${APP_DEST}"
fi
echo "  ✓ Signed"

echo "→ Creating ${DMG_PATH} ..."
rm -f "${DMG_PATH}"

DMG_STAGING="${STAGE_ROOT}/dmg-staging"
mkdir -p "${DMG_STAGING}"
cp -R "${APP_DEST}" "${DMG_STAGING}/${BUNDLE_NAME}"

if [[ -n "${CLI_BINARY}" && -f "${CLI_BINARY}" ]]; then
  cp "${CLI_BINARY}" "${DMG_STAGING}/nexus"
  chmod +x "${DMG_STAGING}/nexus"
  cat > "${DMG_STAGING}/INSTALL-CLI.txt" << 'EOF'
Nexus System Monitor — CLI installation
========================================

This disk image includes the "nexus" command-line tool alongside the app.

To install it on your PATH, open Terminal, cd into this mounted volume,
and run:

    sudo cp nexus /usr/local/bin/

Then verify it is available:

    nexus --version
EOF
fi

if command -v create-dmg >/dev/null 2>&1; then
  create-dmg \
    --volname "Nexus System Monitor ${VERSION}" \
    --volicon "crates/nexus-ui/assets/nexus-icon.icns" \
    --window-pos 200 120 \
    --window-size 580 380 \
    --icon-size 100 \
    --icon "${BUNDLE_NAME}" 145 190 \
    --hide-extension "${BUNDLE_NAME}" \
    --app-drop-link 430 190 \
    "${DMG_PATH}" \
    "${DMG_STAGING}" || hdiutil create -volname "Nexus System Monitor ${VERSION}" -srcfolder "${DMG_STAGING}" -ov -format UDZO "${DMG_PATH}"
else
  ln -s /Applications "${DMG_STAGING}/Applications"
  hdiutil create -volname "Nexus System Monitor ${VERSION}" -srcfolder "${DMG_STAGING}" -ov -format UDZO "${DMG_PATH}"
fi

echo "  ✓ DMG created: ${DMG_PATH}"

echo "→ Creating ${TARBALL} ..."
tar -czf "${TARBALL}" -C "${STAGE_ROOT}" "${BUNDLE_NAME}"
echo "  ✓ Portable archive: ${TARBALL}"

rm -rf "${STAGE_ROOT}"
