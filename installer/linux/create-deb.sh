#!/usr/bin/env bash
# create-deb.sh — Build a Debian/Ubuntu .deb package from Rust release output
#
# Usage:
#   bash installer/linux/create-deb.sh <version> [output-dir] [cli-publish-dir] [ui-publish-dir]

set -euo pipefail

VERSION="${1:?Usage: $0 <version> [output-dir] [cli-publish-dir] [ui-publish-dir]}"
OUTDIR="${2:-dist}"
CLI_PUBLISH_DIR="${3:-dist/stage/linux-x64-cli}"
PUBLISH_DIR="${4:-dist/stage/linux-x64-ui}"

PKG_NAME="nexus-monitor"
PKG_DIR="${OUTDIR}/${PKG_NAME}_${VERSION}_amd64"
DEB_OUT="${OUTDIR}/NexusMonitor-Linux-${VERSION}.deb"
INSTALL_DIR="${PKG_DIR}/usr/lib/nexus-monitor"
CLI_INSTALL_DIR="${INSTALL_DIR}/cli"

if [[ ! -f "${PUBLISH_DIR}/nexus-monitor" ]]; then
  echo "Error: release binary not found at ${PUBLISH_DIR}/nexus-monitor"
  exit 1
fi

mkdir -p "${OUTDIR}"

echo "→ Building .deb package structure at ${PKG_DIR} ..."
rm -rf "${PKG_DIR}"

mkdir -p "${PKG_DIR}/DEBIAN"
mkdir -p "${INSTALL_DIR}"
mkdir -p "${PKG_DIR}/usr/bin"
mkdir -p "${PKG_DIR}/usr/share/applications"
for RES in 16 32 48 64 128 256 512; do
  mkdir -p "${PKG_DIR}/usr/share/icons/hicolor/${RES}x${RES}/apps"
done

cp -R "${PUBLISH_DIR}/." "${INSTALL_DIR}/"
chmod +x "${INSTALL_DIR}/nexus-monitor"
if [[ ! -e "${INSTALL_DIR}/NexusMonitor" ]]; then
  ln -s "nexus-monitor" "${INSTALL_DIR}/NexusMonitor"
fi

ln -s "/usr/lib/nexus-monitor/nexus-monitor" "${PKG_DIR}/usr/bin/nexus-monitor"

if [[ -n "${CLI_PUBLISH_DIR}" && -f "${CLI_PUBLISH_DIR}/nexus" ]]; then
  echo "→ Bundling CLI from ${CLI_PUBLISH_DIR} ..."
  mkdir -p "${CLI_INSTALL_DIR}"
  cp -R "${CLI_PUBLISH_DIR}/." "${CLI_INSTALL_DIR}/"
  chmod +x "${CLI_INSTALL_DIR}/nexus"
  if [[ -f "${CLI_INSTALL_DIR}/nexus-cli" ]]; then
    chmod +x "${CLI_INSTALL_DIR}/nexus-cli"
  fi
  ln -s "/usr/lib/nexus-monitor/cli/nexus" "${PKG_DIR}/usr/bin/nexus"
  echo "  ✓ CLI bundled: /usr/lib/nexus-monitor/cli/nexus (symlinked as /usr/bin/nexus)"
fi

cat > "${PKG_DIR}/usr/share/applications/nexus-monitor.desktop" << 'EOF'
[Desktop Entry]
Version=1.0
Type=Application
Name=Nexus System Monitor
GenericName=System Monitor
Comment=Cross-platform system monitoring and process management
Exec=/usr/lib/nexus-monitor/nexus-monitor
Icon=nexus-monitor
Terminal=false
Categories=System;Monitor;Utility;
Keywords=system;monitor;process;cpu;memory;disk;network;gpu;
StartupWMClass=nexus-monitor
StartupNotify=true
EOF

for RES in 16 32 48 64 128 256 512; do
  SRC="crates/nexus-ui/assets/nexus-icon-${RES}.png"
  DST="${PKG_DIR}/usr/share/icons/hicolor/${RES}x${RES}/apps/nexus-monitor.png"
  if [[ -f "${SRC}" ]]; then
    cp "${SRC}" "${DST}"
  fi
done

INSTALLED_SIZE=$(du -sk "${INSTALL_DIR}" | cut -f1)

cat > "${PKG_DIR}/DEBIAN/control" << EOF
Package: ${PKG_NAME}
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: ${INSTALLED_SIZE}
Depends: libc6, libx11-6, libxkbcommon0, libxkbcommon-x11-0, libwayland-client0, libvulkan1
Maintainer: Josh Sutcliff <joshuadsutcliff@users.noreply.github.com>
Homepage: https://github.com/joshuadsutcliff/nexus-system-monitor
Description: Cross-platform system monitor
 Nexus System Monitor provides real-time system insight across
 Windows, macOS, and Linux. Features include CPU, memory, disk,
 network and GPU metrics, process management, service control,
 startup management, disk space visualization, hardware benchmarks,
 flight recorder traces, Prometheus metrics export, and a VFD popout monitor.
EOF

cat > "${PKG_DIR}/DEBIAN/postinst" << 'EOF'
#!/bin/sh
set -e
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t /usr/share/icons/hicolor || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi
EOF
chmod 755 "${PKG_DIR}/DEBIAN/postinst"

cat > "${PKG_DIR}/DEBIAN/postrm" << 'EOF'
#!/bin/sh
set -e
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t /usr/share/icons/hicolor || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi
EOF
chmod 755 "${PKG_DIR}/DEBIAN/postrm"

echo "→ Building ${DEB_OUT} ..."
rm -f "${DEB_OUT}"
dpkg-deb --build --root-owner-group "${PKG_DIR}" "${DEB_OUT}"
echo "  ✓ .deb: ${DEB_OUT}"

rm -rf "${PKG_DIR}"
