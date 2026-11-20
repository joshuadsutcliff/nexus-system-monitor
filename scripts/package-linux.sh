#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCH="$(uname -m)"
TARGET_DIR="${CARGO_TARGET_DIR:-${REPO_ROOT}/target}"
BUNDLE_ROOT="${TARGET_DIR}/release/bundle"
PKG_NAME="nexus-system-monitor-linux-${ARCH}"
APPDIR="${BUNDLE_ROOT}/${PKG_NAME}"
TARBALL="${BUNDLE_ROOT}/${PKG_NAME}.tar.gz"

echo "==> Building release binaries for Linux (${ARCH})..."
cd "${REPO_ROOT}"
cargo build --release -p nexus-ui --bin nexus-monitor --bin nexus-gui -p nexus-cli --bin nexus --bin nexus-cli

echo "==> Assembling Linux AppDir layout at: ${APPDIR}"
rm -rf "${APPDIR}" "${TARBALL}"
mkdir -p \
    "${APPDIR}/usr/bin" \
    "${APPDIR}/usr/share/applications" \
    "${APPDIR}/usr/share/icons/hicolor/512x512/apps" \
    "${APPDIR}/usr/share/nexus-system-monitor/Resources/Skins"

cp "${TARGET_DIR}/release/nexus-monitor" "${APPDIR}/usr/bin/nexus-monitor"
cp "${TARGET_DIR}/release/nexus-gui"     "${APPDIR}/usr/bin/nexus-gui"
cp "${TARGET_DIR}/release/nexus"         "${APPDIR}/usr/bin/nexus"
cp "${TARGET_DIR}/release/nexus-cli"     "${APPDIR}/usr/bin/nexus-cli"
chmod +x "${APPDIR}/usr/bin/nexus-monitor" "${APPDIR}/usr/bin/nexus-gui" "${APPDIR}/usr/bin/nexus" "${APPDIR}/usr/bin/nexus-cli"

cp "${REPO_ROOT}/crates/nexus-ui/assets/AppIcon.png" \
   "${APPDIR}/usr/share/icons/hicolor/512x512/apps/nexus-monitor.png"
cp "${REPO_ROOT}/crates/nexus-ui/assets/fonts/Michroma-Regular.ttf" \
   "${APPDIR}/usr/share/nexus-system-monitor/Resources/Michroma-Regular.ttf"
cp "${REPO_ROOT}/crates/nexus-ui/assets/fonts/LED Dot-Matrix.ttf" \
   "${APPDIR}/usr/share/nexus-system-monitor/Resources/LED Dot-Matrix.ttf"
cp -R "${REPO_ROOT}/crates/nexus-ui/assets/skins/"* \
   "${APPDIR}/usr/share/nexus-system-monitor/Resources/Skins/"

if [[ -f "${REPO_ROOT}/README.md" ]]; then
    cp "${REPO_ROOT}/README.md" "${APPDIR}/README.md"
fi
if [[ -f "${REPO_ROOT}/LICENSE" ]]; then
    cp "${REPO_ROOT}/LICENSE" "${APPDIR}/LICENSE"
fi

cat > "${APPDIR}/usr/share/applications/com.joshsutcliff.nexusmonitor.desktop" << 'EOF'
[Desktop Entry]
Type=Application
Name=Nexus System Monitor
GenericName=System Monitor
Comment=Unified real-time telemetry, process management, disk analyzer, and VFD popout monitor
Exec=nexus-monitor
Icon=nexus-monitor
Terminal=false
Categories=System;Monitor;Utility;
Keywords=system;process;task;cpu;memory;gpu;disk;network;monitor;
StartupWMClass=nexus-monitor
EOF

echo "==> Creating portable Linux release tarball: ${TARBALL}"
tar -C "${BUNDLE_ROOT}" -czf "${TARBALL}" "${PKG_NAME}"
ls -lh "${TARBALL}"
