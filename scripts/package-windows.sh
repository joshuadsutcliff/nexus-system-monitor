#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET_TRIPLE="${1:-x86_64-pc-windows-gnu}"
TARGET_DIR="${CARGO_TARGET_DIR:-${REPO_ROOT}/target}"
RELEASE_DIR="${TARGET_DIR}/${TARGET_TRIPLE}/release"
BUNDLE_ROOT="${TARGET_DIR}/release/bundle"
PKG_NAME="nexus-system-monitor-windows-x86_64"
STAGE_DIR="${BUNDLE_ROOT}/${PKG_NAME}"
ZIP_OUT="${BUNDLE_ROOT}/${PKG_NAME}.zip"

echo "==> Building Windows release binaries (${TARGET_TRIPLE})..."
cd "${REPO_ROOT}"
cargo build --release --target "${TARGET_TRIPLE}" -p nexus-ui --bin nexus-monitor --bin nexus-gui -p nexus-cli --bin nexus --bin nexus-cli

echo "==> Assembling Windows portable package at: ${STAGE_DIR}"
rm -rf "${STAGE_DIR}" "${ZIP_OUT}"
mkdir -p "${STAGE_DIR}/Resources/Skins"

cp "${RELEASE_DIR}/nexus-monitor.exe" "${STAGE_DIR}/nexus-monitor.exe"
cp "${RELEASE_DIR}/nexus-gui.exe"     "${STAGE_DIR}/nexus-gui.exe"
cp "${RELEASE_DIR}/nexus.exe"         "${STAGE_DIR}/nexus.exe"
cp "${RELEASE_DIR}/nexus-cli.exe"     "${STAGE_DIR}/nexus-cli.exe"

cp "${REPO_ROOT}/crates/nexus-ui/assets/AppIcon.png" "${STAGE_DIR}/Resources/AppIcon.png"
cp "${REPO_ROOT}/crates/nexus-ui/assets/fonts/Michroma-Regular.ttf" "${STAGE_DIR}/Resources/Michroma-Regular.ttf"
cp "${REPO_ROOT}/crates/nexus-ui/assets/fonts/LED Dot-Matrix.ttf" "${STAGE_DIR}/Resources/LED Dot-Matrix.ttf"
cp -R "${REPO_ROOT}/crates/nexus-ui/assets/skins/"* "${STAGE_DIR}/Resources/Skins/"

if [[ -f "${REPO_ROOT}/README.md" ]]; then
    cp "${REPO_ROOT}/README.md" "${STAGE_DIR}/README.md"
fi
if [[ -f "${REPO_ROOT}/LICENSE" ]]; then
    cp "${REPO_ROOT}/LICENSE" "${STAGE_DIR}/LICENSE"
fi

echo "==> Creating Windows release archive: ${ZIP_OUT}"
(cd "${BUNDLE_ROOT}" && /usr/bin/zip -rq "${ZIP_OUT}" "${PKG_NAME}")
ls -lh "${ZIP_OUT}"
