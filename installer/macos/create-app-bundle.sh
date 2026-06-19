#!/usr/bin/env bash
# create-app-bundle.sh — Construct a macOS .app bundle from a flat dotnet publish output,
# then create a .dmg with drag-to-Applications layout and a portable .tar.gz.
#
# With plain net8.0 (no Microsoft.macOS workload), dotnet publish emits a flat directory
# of binaries. This script assembles the standard .app structure from that flat output.
#
# Usage:
#   bash installer/macos/create-app-bundle.sh <rid> <version> [output-dir]
#
#   rid         : osx-x64 or osx-arm64
#   version     : semver string, e.g. 0.5.0
#   output-dir  : directory for .dmg and .tar.gz (default: dist/)
#
# Requires (CI: macos-latest runner):
#   brew install create-dmg

set -euo pipefail

RID="${1:?Usage: $0 <rid> <version> [output-dir]}"
VERSION="${2:?Version is required}"
OUTDIR="${3:-dist}"

PUBLISH_DIR="src/NexusMonitor.UI/publish/${RID}"
APP_NAME="NexusMonitor"
BUNDLE_NAME="${APP_NAME}.app"

case "${RID}" in
  osx-arm64)  OS_LABEL="MacOS"        ;;
  osx-x64)    OS_LABEL="MacOS-Intel"  ;;
  *)          echo "Error: unsupported RID '${RID}'"; exit 1 ;;
esac

DMG_NAME="${APP_NAME}-${OS_LABEL}-${VERSION}.dmg"
DMG_PATH="${OUTDIR}/${DMG_NAME}"
TARBALL="${OUTDIR}/${APP_NAME}-${OS_LABEL}-Portable-${VERSION}.tar.gz"

# Working location for the .app we build (inside publish dir, gitignored by build outputs)
APP_DEST="${PUBLISH_DIR}/${BUNDLE_NAME}"

# ── Sanity-check the publish directory exists and has the executable ──────────
if [[ ! -f "${PUBLISH_DIR}/${APP_NAME}" ]]; then
  echo "Error: flat publish output not found at ${PUBLISH_DIR}/${APP_NAME}"
  echo "Run: dotnet publish src/NexusMonitor.UI/NexusMonitor.UI.csproj -p:PublishProfile=${RID} first."
  echo ""
  echo "Actual contents of ${PUBLISH_DIR} (up to 30 entries):"
  find "${PUBLISH_DIR}" -maxdepth 2 2>/dev/null | head -30 || echo "(directory not found)"
  exit 1
fi

mkdir -p "${OUTDIR}"

# ── Build .app structure from flat publish output ────────────────────────────
echo "→ Constructing ${BUNDLE_NAME} from flat publish output …"
rm -rf "${APP_DEST}"
mkdir -p "${APP_DEST}/Contents/MacOS"
mkdir -p "${APP_DEST}/Contents/Resources"

# Copy ALL files from the flat publish dir into Contents/MacOS/
# (executable, *.dylib, *.so, *.dll, runtimeconfig.json, deps.json, etc.)
# Exclude any .app that might already be there from a previous partial run.
find "${PUBLISH_DIR}" -maxdepth 1 -not -name "${BUNDLE_NAME}" -not -path "${PUBLISH_DIR}" \
  -exec cp -R {} "${APP_DEST}/Contents/MacOS/" \;

# Ensure the main executable has the executable bit set
chmod +x "${APP_DEST}/Contents/MacOS/${APP_NAME}"

echo "  ✓ Binaries copied to Contents/MacOS/"

# ── Install Info.plist with version substitution ──────────────────────────────
echo "→ Installing Info.plist (version: ${VERSION}) …"
cp "installer/macos/Info.plist" "${APP_DEST}/Contents/Info.plist"
sed -i '' "s/VERSION_PLACEHOLDER/${VERSION}/g" "${APP_DEST}/Contents/Info.plist"
echo "  ✓ Info.plist installed"

# ── Embed the app icon into the bundle resources ──────────────────────────────
echo "→ Embedding app icon …"
cp "src/NexusMonitor.UI/Assets/nexus-icon.icns" \
   "${APP_DEST}/Contents/Resources/nexus-icon.icns"
echo "  ✓ Icon embedded"

# ── Ad-hoc code sign the bundle ───────────────────────────────────────────────
# Apple Silicon requires ALL arm64 binaries to carry a code signature.
# Without any signature, macOS reports the app as "damaged and can't be opened".
# Ad-hoc signing (-) satisfies this requirement without an Apple Developer ID.
# Users will see an "unidentified developer" Gatekeeper prompt on first launch;
# they can bypass it via right-click → Open or System Settings → Privacy & Security.
echo "→ Ad-hoc signing bundle (required for arm64) …"
# CODESIGN_IDENTITY defaults to ad-hoc (-). Override with a real Developer ID
# for distribution signing (e.g. CODESIGN_IDENTITY="Developer ID Application: ...").
CODESIGN_IDENTITY="${CODESIGN_IDENTITY:--}"
ENTITLEMENTS="installer/macos/entitlements.plist"
if [[ "${CODESIGN_IDENTITY}" != "-" && -f "${ENTITLEMENTS}" ]]; then
  # Real identity: pass entitlements (required for hardened runtime)
  codesign --deep --force --sign "${CODESIGN_IDENTITY}" --timestamp \
    --options runtime --entitlements "${ENTITLEMENTS}" "${APP_DEST}"
else
  # Ad-hoc: entitlements are ignored by the kernel for ad-hoc signatures,
  # so skip --entitlements to avoid AMFI XML parse failures on comment nodes.
  codesign --deep --force --sign - --timestamp=none "${APP_DEST}"
fi
echo "  ✓ Signed (ad-hoc)"

# ── Create .dmg with drag-to-Applications layout ─────────────────────────────
echo "→ Creating ${DMG_PATH} ..."
rm -f "${DMG_PATH}"

create-dmg \
  --volname "Nexus System Monitor ${VERSION}" \
  --volicon "src/NexusMonitor.UI/Assets/nexus-icon.icns" \
  --window-pos 200 120 \
  --window-size 580 380 \
  --icon-size 100 \
  --icon "${BUNDLE_NAME}" 145 190 \
  --hide-extension "${BUNDLE_NAME}" \
  --app-drop-link 430 190 \
  "${DMG_PATH}" \
  "${APP_DEST}"

echo "  ✓ DMG created: ${DMG_PATH}"

# ── Create portable tar.gz of the .app bundle ────────────────────────────────
echo "→ Creating ${TARBALL} ..."
tar -czf "${TARBALL}" -C "$(dirname "${APP_DEST}")" "${BUNDLE_NAME}"
echo "  ✓ Portable archive: ${TARBALL}"
