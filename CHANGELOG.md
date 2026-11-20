# Changelog

All notable changes to **Nexus System Monitor** are documented in this file.

## [v0.8.1] - 2026-11-20

### Highlights
- **Windows & Linux CPU/RAM Footprint Remediation:** Eliminated the unthrottled 60 FPS idle redraw loop and startup full-drive `C:\` / `C:\Program Files` recursive scan storms, cutting idle CPU load to `< 2%` and physical memory usage to `< 80 MiB`.
- **Native Windows Dedicated GPU Telemetry (NVIDIA NVML + DXGI/PDH Fallback):** Added dynamic runtime `nvml.dll` FFI for NVIDIA GPUs (`windows.nvml.gpu`) and universal DXGI + PDH GPU Engine / VRAM fallback (`windows.d3dkmt.gpu`) for AMD, Intel, and NVIDIA adapters.
- **Native Windows Disk & Network I/O Throughput:** Added live physical/logical volume read/write `MB/s` and `IOPS` via `DeviceIoControl(IOCTL_DISK_PERFORMANCE)` + PDH fallback (`windows.ioctl.disk_performance`), per-process `GetProcessIoCounters`, and hardware NIC `Mbps` throughput via `GetIfTable2` (`windows.iphlpapi.if_table2`).
- **Native Windows & Linux Thermal Sensors, Clocks & Package Power:** Added `CallNtPowerInformation(ProcessorInformation)` CPU clock frequency, PDH ACPI thermal zone sensors (`windows.wmi.thermal`) with static-stub rejection, NVML GPU core temperature and board power, and Linux `/sys/class/drm` + `hwmon` GPU load, VRAM, and wattage (`linux.sysfs.drm`, `linux.sysfs.hwmon`).
- **Cross-Platform Terminology & 4-Card Hardware Architecture View:** Eliminated all leaked macOS/Apple strings on Windows and Linux and added the 4-card `"Processor, GPU & Thermal Architecture"` view (`CPU Core & Thread Topology`, `Dedicated & Integrated GPU Telemetry`, `Compute & Media Engines`, and `Hardware Thermal Sensors`).

### Fixed
- **Event Loop Pacing (`crates/nexus-ui/src/desktop.rs`):** Replaced unconditional per-frame `window.request_redraw()` with cadence-paced `ControlFlow::WaitUntil` deadlines and `StartCause::ResumeTimeReached` wakeups, running at 60 Hz only when an active WGSL shader view (`DiskSpace::Visualizer`, `F12` screensaver, or custom shader tile) or streaming disk scan is active.
- **Lazy Disk Space Scanning (`crates/nexus-ui/src/desktop.rs`, `crates/nexus-disk/src/lib.rs`):** Removed unconditional startup disk scanning from `GuiApp::new` so directory tree walks only start when the user opens the `Disk Space` view. Capped retained directory children per node to `64` and bounded parallel scanning to a 4-thread Rayon pool.
- **O(1) Windows Installed Apps Registry Enumeration (`crates/nexus-collectors-macos/src/system_ops.rs`):** Replaced recursive `C:\Program Files` directory walking with `HKLM` (`KEY_WOW64_64KEY` and `KEY_WOW64_32KEY`) and `HKCU` `...\CurrentVersion\Uninstall` registry reads (`DisplayName`, `DisplayVersion`, `Publisher`, `InstallLocation`, `EstimatedSize` KiB -> bytes).
- **Windows Process Working Set & Handle Hygiene (`crates/nexus-collectors-windows/src/lib.rs`):** Consolidated process sampling to a single `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` handle per PID, using `K32GetProcessMemoryInfo` (`PROCESS_MEMORY_COUNTERS_EX` `WorkingSetSize` / `PrivateUsage`) and `GetProcessIoCounters`.
- **Hardware NIC Filtering (`crates/nexus-collectors-windows/src/lib.rs`):** Fixed `should_include_windows_if_row` to test `InterfaceAndOperStatusFlags` bit 1 (`0x02`, `FilterInterface`) and bit 0 (`0x01`, `HardwareInterface`) so physical Ethernet and Wi-Fi adapters (`0x05`, `HardwareInterface | ConnectorPresent`) are retained while NDIS lightweight filter rows (`0x02`, `0x03`), loopback (`24`), and tunnels (`131`) are excluded.
- **8-Byte Aligned PDH Counter Buffer (`crates/nexus-collectors-windows/src/lib.rs`):** Backed `PdhSession::scratch_buf` with `Vec<u64>` to guarantee 8-byte alignment for `PDH_FMT_COUNTERVALUE_ITEM_W`.

### Added
- **`HostPlatform` Cross-Platform UI Abstraction (`crates/nexus-core/src/platform.rs`, `crates/nexus-ui/`, `crates/nexus-cli/`):** Platform-aware headings, action labels (`Show in Explorer` / `Move to Recycle Bin` on Windows, `Show in File Manager` / `Move to Trash` on Linux, `Reveal in Finder` / `Move to Trash` on macOS), service/startup/driver terminology (`Windows SCM` / `systemd` / `launchd`), and sensor provenance descriptions.
- **Cross-Platform Parity Regression Tests (`crates/nexus-ui/src/lib.rs`, `crates/nexus-collectors-windows/src/lib.rs`):** Unit tests verifying NVML FFI struct layouts, `GetIfTable2` hardware NIC bitmasks, monotonic Disk/Network I/O delta calculations, PDH GPU Engine aggregation, and zero Apple/macOS strings when rendering Windows and Linux UI views.

**Full Changelog**: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.8.0...v0.8.1

---

## [v0.8.0] - 2026-11-20

### Highlights
- **Complete 8-Crate Native Rust Rewrite (`v0.8.0`):** Rebuilt Nexus System Monitor as an 8-crate Rust 2024 workspace (`nexus-core`, `nexus-collectors-macos`, `nexus-collectors-linux`, `nexus-collectors-windows`, `nexus-disk`, `nexus-automation`, `nexus-ui`, `nexus-cli`) powered by `winit` + `wgpu` + `egui` + `ratatui`.
- **18-View Apple HIG Desktop Application & 8-Pane TUI:** Delivered progressive-disclosure cards, guided permission unlock cards, 20 Crystal-Glass themes, an interactive HSV color wheel studio, 3 custom `naga`-validated WGSL shaders (`Ocean Metaballs`, `48x26 Defrag Blocks`, `Tempest Vector Tunnel`), and an independent native `.nexusskin` desktop popout mini-monitor (`Console`, `Traces`, `Winamp`, `Media Player`, `Win2000 Task Manager`).
- **Honesty & Provenance Engine (`Metric<T>`):** Typed 8-state `ValueState` and 4-tier `MetricQuality` envelope with interned `SourceId` provenance and automatic cross-sensor coherence validation (`repair_snapshot_coherence`).
- **PID-Reuse-Safe Automation & Encrypted Companion Remote:** Double-verified `ProcessKey(pid, creation_token_us, uid)` process controls, crash-journaled `GamingModeService`, wildcard `RulesEngine`, `AutoBalanceService`, SQLite `SnapshotDatabase` & `.nexustrace` flight recorder, 4-suite hardware benchmark studio, Prometheus `/metrics` exporter, and RFC-verified X25519 + HKDF-SHA256 + ChaCha20-Poly1305 companion remote server.

**Full Changelog**: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.7.0...v0.8.0

---

## [v0.7.0] - 2026-07-14

### What's Changed
- **Platform Symmetry (Sym-2):** Added macOS CPU/GPU die temperatures (`AppleSMC`) and `IOAccelerator` GPU load telemetry.
- **Process Name & Lifecycle Fixes:** Fixed macOS process name resolution and reliable application shutdown cleanup.

**Full Changelog**: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.6.0...v0.7.0

---

## [v0.6.0] - 2026-07-13

### What's Changed
- **Cross-Platform Collector Foundations:** Added multi-platform process, memory, storage, and service monitoring across Windows, macOS, and Linux.
- **Disk Space Analyzer & Duplicate Finder:** Added directory tree scanning, category breakdown, and SHA-256 duplicate file detection.

**Full Changelog**: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.5.2...v0.6.0

---

## [v0.5.2] - 2026-06-25

### What's Changed
- **Initial Public Multi-Platform Release:** Core system telemetry dashboard, process table, performance graphs, and desktop installer packaging.
