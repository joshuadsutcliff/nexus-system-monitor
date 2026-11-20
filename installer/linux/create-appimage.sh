#!/usr/bin/env bash
# create-appimage.sh — Build a Linux x64 AppImage from Rust release output
#
# Usage:
#   bash installer/linux/create-appimage.sh <version> [output-dir] [publish-dir]

set -euo pipefail

VERSION="${1:?Usage: $0 <version> [output-dir] [publish-dir]}"
OUTDIR="${2:-dist}"
PUBLISH_DIR="${3:-dist/stage/linux-x64-ui}"

APPDIR="${OUTDIR}/NexusMonitor.AppDir"
APPIMAGE_OUT="${OUTDIR}/NexusMonitor-Linux-${VERSION}.AppImage"
APPIMAGETOOL_URL="https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
APPIMAGETOOL="${OUTDIR}/appimagetool-x86_64.AppImage"

if [[ ! -f "${PUBLISH_DIR}/nexus-monitor" ]]; then
  echo "Error: release binary not found at ${PUBLISH_DIR}/nexus-monitor"
  exit 1
fi

mkdir -p "${OUTDIR}"

if [[ ! -f "${APPIMAGETOOL}" ]]; then
  echo "→ Downloading appimagetool ..."
  curl -fsSL -o "${APPIMAGETOOL}" "${APPIMAGETOOL_URL}"
  chmod +x "${APPIMAGETOOL}"
fi

export APPIMAGE_EXTRACT_AND_RUN=1

echo "→ Building AppDir at ${APPDIR} ..."
rm -rf "${APPDIR}"
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/256x256/apps"
mkdir -p "${APPDIR}/usr/share/nexus-system-monitor/Resources/Skins"

cp -R "${PUBLISH_DIR}/." "${APPDIR}/usr/bin/"
chmod +x "${APPDIR}/usr/bin/nexus-monitor"
if [[ -f "${APPDIR}/usr/bin/NexusMonitor" ]]; then
  chmod +x "${APPDIR}/usr/bin/NexusMonitor"
else
  ln -s "nexus-monitor" "${APPDIR}/usr/bin/NexusMonitor"
fi

cp crates/nexus-ui/assets/fonts/Michroma-Regular.ttf \
   "${APPDIR}/usr/share/nexus-system-monitor/Resources/Michroma-Regular.ttf"
cp "crates/nexus-ui/assets/fonts/LED Dot-Matrix.ttf" \
   "${APPDIR}/usr/share/nexus-system-monitor/Resources/LED Dot-Matrix.ttf"
cp -R crates/nexus-ui/assets/skins/* \
   "${APPDIR}/usr/share/nexus-system-monitor/Resources/Skins/"

cp installer/linux/AppRun "${APPDIR}/AppRun"
chmod +x "${APPDIR}/AppRun"

cp installer/linux/NexusMonitor.desktop "${APPDIR}/NexusMonitor.desktop"
cp installer/linux/NexusMonitor.desktop "${APPDIR}/usr/share/applications/"

cp crates/nexus-ui/assets/nexus-icon-256.png "${APPDIR}/nexus-monitor.png"
cp crates/nexus-ui/assets/nexus-icon-256.png \
  "${APPDIR}/usr/share/icons/hicolor/256x256/apps/nexus-monitor.png"

echo "→ Building ${APPIMAGE_OUT} ..."
rm -f "${APPIMAGE_OUT}"
ARCH=x86_64 "${APPIMAGETOOL}" "${APPDIR}" "${APPIMAGE_OUT}"
chmod +x "${APPIMAGE_OUT}"

echo "  ✓ AppImage: ${APPIMAGE_OUT}"

rm -rf "${APPDIR}"
rm -f "${APPIMAGETOOL}"
