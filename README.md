# Nexus System Monitor

<p align="center">
  <img src="Nexus System Monitor.png" width="128" alt="Nexus System Monitor">
</p>

<p align="center">
  <a href="https://github.com/joshuadsutcliff/nexus-system-monitor/releases/latest"><img src="https://img.shields.io/github/v/release/joshuadsutcliff/nexus-system-monitor?label=latest" alt="Latest Release"></a>
  <a href="https://github.com/joshuadsutcliff/nexus-system-monitor/actions"><img src="https://img.shields.io/badge/platform-macOS%20%7C%20Linux%20%7C%20Windows-0A84FF" alt="Platforms"></a>
  <a href="https://www.rust-lang.org"><img src="https://img.shields.io/badge/rust-2024%20edition-orange" alt="Rust 2024"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT License"></a>
</p>

**One system monitor. Every desktop platform. Authoritative telemetry.**

Nexus System Monitor is an 8-crate native Rust (`wgpu` + `egui` + `winit` + `ratatui`) system monitor, process controller, disk space analyzer, wireless and LAN radar, flight recorder, hardware benchmark suite, and skinnable desktop mini-monitor for **macOS**, **Linux**, and **Windows**.

> **Testing or building from source?** Read [TESTING.md](TESTING.md) for platform prerequisites, build commands, and CLI/GUI verification harnesses.

---

## Multi-Platform Screenshot Gallery

### macOS (`aarch64-apple-darwin` · Metal)

*Dashboard: Composite health ring, plain-English workload diagnosis, 4 environment pillars, and customizable Pro telemetry tiles*
![Dashboard on macOS](docs/screenshots/dashboard-macos.png)

*Performance Telemetry Studio: Real-time Total/User/System CPU curves with 4-band non-linear time compression and per-core Efficiency/Performance sparklines*
![Performance on macOS](docs/screenshots/performance-macos.png)

*Processes & Applications: Human-readable impact badges, inline CPU sparklines, physical footprint bars, and double-verified ProcessKey safety*
![Processes on macOS](docs/screenshots/processes-macos.png)

*Storage & Disk Space Analyzer: Guided TCC Full Disk Access card, 3D WGSL Ocean Metaballs / 48x26 Defrag Blocks / Tempest Vector Tunnel, squarified treemap, 2-pass SHA-256 duplicate finder, and SQLite snapshot diffs*
![Disk Space on macOS](docs/screenshots/disk-space-macos.png)

*Apple Silicon Architecture & Thermals: Live libIOReport E-cluster and P-cluster residency, Apple GPU power, Neural Engine state, Media Engine blocks, and AppleSMC die temperature sensors*
![Apple Silicon on macOS](docs/screenshots/apple-silicon-macos.png)

*System Information & Health: Hardware specification sheet, 5 subsystem VFD health cards, and SQLite health trend trajectory*
![System Info on macOS](docs/screenshots/system-info-macos.png)

*Wireless Spectrum & LAN Radar: Wi-Fi RF spectrum table, CoreWLAN Location Services privacy unlock guidance, and concurrent TCP/ARP LAN subnet discovery*
![Wireless & LAN Radar on macOS](docs/screenshots/wireless-macos.png)

*Background Services & Launch Daemons: Live service inventory (`launchd` / `systemd` / Windows SCM) with runtime state filters and dry-run lifecycle controls*
![Services on macOS](docs/screenshots/services-macos.png)

*Kernel Extensions & System Drivers: SIP & DriverKit unload policy guidance card, interactive CLI copy box, and live `kmutil` + `systemextensionsctl` inventory*
![Drivers on macOS](docs/screenshots/drivers-macos.png)

*Hardware Benchmark Studio: 4-suite hardware benchmark (SplitMix64 CPU, offscreen Metal/wgpu Compute + Fragment GPU, uncached F_NOCACHE Disk I/O, and HTTPS transfer) anchored to a 100-point reference baseline*
![Benchmarks on macOS](docs/screenshots/benchmarks-macos.png)

*Measurement Honesty & Sensor Provenance: Real-time audit of every sensor ValueState, quality tier, collector source identifier, and platform permission unlock action*
![Honesty & Provenance on macOS](docs/screenshots/honesty-macos.png)

*Settings & Appearance: 20 curated themes, interactive HSV color wheel studio, quiet hours, watchdog automation, and ChaCha20-Poly1305 companion remote streaming*
![Settings on macOS](docs/screenshots/settings-macos.png)

### Linux (`aarch64-unknown-linux-gnu` · Vulkan)

*Linux Dashboard, System Info, and Processes (`/proc` + `/sys` + `statvfs` + `systemd`)*
![Dashboard on Linux](docs/screenshots/dashboard-linux.png)
![System Info on Linux](docs/screenshots/system-info-linux.png)
![Processes on Linux](docs/screenshots/processes-linux.png)

### Windows (`x86_64-pc-windows-gnu` · Win32 + Vulkan)

*Windows Dashboard, System Info, and Independent Native `.nexusskin` Desktop Popout Window*
![Dashboard on Windows](docs/screenshots/dashboard-windows.png)
![System Info on Windows](docs/screenshots/system-info-windows.png)
![Winamp Popout on Windows](docs/screenshots/popout-winamp-windows.png)

---

## Design Philosophy & Architecture Decisions

### 1. Apple Human Interface Guidelines (HIG) Progressive Disclosure
All 18 views follow an inset grouped card hierarchy (`Card Title` -> `1-line plain-English subtitle` -> `Visual Meter / Graph` -> `Action / Detail`). Every screen leads with a human-readable workload summary and color-coded status pills, while low-level engineering data (`CreationToken` integers, `libIOReport` energy channels, `AppleSMC` die sensors, and 20-column tables) remains accessible on demand inside Inspector drawers and column pickers.

### 2. Guided Permission & Feature Unlock Cards
Whenever OS security boundaries restrict a sensor or directory (macOS Full Disk Access for protected containers, Location Services for Wi-Fi SSID names, App Management for safe Trash uninstallation, Linux `CAP_NET_ADMIN` / `sudo`, or Windows Administrator elevation), Nexus renders an inline guidance card with step-by-step instructions and a one-click button that opens the exact System Settings pane.

### 3. Honesty & Provenance Engine (`nexus-core`)
Every metric in Nexus carries a typed `Metric<T>` envelope with `ValueState` (`Valid`, `Zero`, `NotYetAvailable`, `Stale`, `Unsupported`, `AccessDenied`, `TransientError`), `MetricQuality` (`Authoritative`, `Derived`, `Estimated`, `Unknown`), sampling window duration, and an interned `SourceId` (for example `darwin.mach.host_processor_info`, `darwin.ioreport.energy_model`, `linux.proc.stat`, or `windows.win32.system_times`). Nexus never fabricates zeros when a sensor requires elevated privileges or a warm delta window.

### 4. PID-Reuse-Safe `ProcessKey` & Double Verification (`nexus-automation`)
Every process is identified by `ProcessKey { pid, creation_token_us, uid }`. Before `ActionCoordinator` dispatches any signal (`SIGTERM`, `SIGKILL`, `renice`, or `taskkill`), it re-queries the live OS kernel twice around the operation and aborts with `PidReusedOrExited` if the creation timestamp token changed.

### 5. Independent Native `.nexusskin` Desktop Popout Window (`nexus-ui`)
Launching the Popout Mini-Monitor creates an independent borderless `winit` desktop window sharing the primary `wgpu::Device` and `wgpu::Queue` (`WgpuSetup::Existing`). Users can drag the mini-monitor anywhere on the desktop, minimize or close the main window while the popout stays live at 60 Hz, switch between 5 bundled `.nexusskin` designs (`Console`, `Traces`, `Winamp`, `Windows Media Player`, `Windows 2000 Task Manager`), and pin window level (`TopMost`, `Normal`, `Desktop`).

---

## Workspace Architecture (8 Rust Crates)

| Crate | Responsibility | Safety Policy |
| :--- | :--- | :--- |
| [`crates/nexus-core`](crates/nexus-core) | `SystemSnapshot`, `Metric<T>` honesty engine, `CadenceCoordinator`, `HealthScoring`, 4-suite benchmark math, Prometheus `/metrics`, Grafana/Telegraf generators, and RFC-verified ChaCha20-Poly1305 + X25519 companion remote protocol | `#![forbid(unsafe_code)]` |
| [`crates/nexus-collectors-macos`](crates/nexus-collectors-macos) | Authoritative macOS collector (`mach_host_self`, `proc_pid_rusage`, `libIOReport.dylib` energy/residency, `AppleSMC`, `launchctl`, `diskutil`, `CoreWLAN` / `system_profiler`, `kmutil`) and cross-platform `system_ops` dispatch facade | OS FFI boundary |
| [`crates/nexus-collectors-linux`](crates/nexus-collectors-linux) | Native Linux collector (`/proc/stat`, `/proc/meminfo`, `/proc/pressure/memory`, `/proc/diskstats`, `/proc/net/dev`, `/proc/[pid]/stat` + `io` + `fd` socket inode attribution, `/sys/class/thermal`, `systemctl`, XDG autostart) | OS FFI boundary |
| [`crates/nexus-collectors-windows`](crates/nexus-collectors-windows) | Native Windows collector (`GetSystemTimes`, `NtQuerySystemInformation`, `GlobalMemoryStatusEx`, `GetTickCount64`, `GetDiskFreeSpaceExW`, `CreateToolhelp32Snapshot`, `GetProcessTimes`, `K32GetProcessMemoryInfo`, Registry SCM & Run keys) | OS FFI boundary |
| [`crates/nexus-disk`](crates/nexus-disk) | Multi-threaded streaming disk scanner, 7-category file classifier, squarified treemap layout, 2-pass FIPS 180-4 SHA-256 duplicate finder, SQLite snapshot & telemetry stores, and `.nexustrace` flight recorder | `#![forbid(unsafe_code)]` |
| [`crates/nexus-automation`](crates/nexus-automation) | `ActionCoordinator`, wildcard `RulesEngine`, `AutoBalanceService`, `IdleThrottleService`, crash-journaled `GamingModeService`, `QuietHoursService`, `SleepPreventionService`, and webhook `AlertsService` | `#![forbid(unsafe_code)]` |
| [`crates/nexus-ui`](crates/nexus-ui) | Native `winit` + `wgpu` + `egui` desktop application (`nexus-monitor`), 3 custom WGSL shaders (`naga`-validated), VFD ballistics engine, 20 Crystal-Glass themes, HSV color wheel studio, and independent `.nexusskin` popout window | `#![forbid(unsafe_code)]` |
| [`crates/nexus-cli`](crates/nexus-cli) | Unified `nexus` / `nexus-cli` command-line interface and full-screen 8-pane `ratatui` + `crossterm` terminal dashboard (`nexus tui`) | `#![forbid(unsafe_code)]` |

---

## Building & Packaging

### macOS (`aarch64-apple-darwin` / `x86_64-apple-darwin`)
```bash
# Run test suite and lint checks
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings

# Build and sign the native macOS application bundle
./scripts/bundle-macos.sh
open "target/release/bundle/Nexus System Monitor.app"
```

### Linux (`x86_64-unknown-linux-gnu` / `aarch64-unknown-linux-gnu`)
```bash
# Ubuntu / Debian dependencies
sudo apt-get install -y build-essential pkg-config libxkbcommon-dev libxkbcommon-x11-dev \
    libxcb-xkb-dev libwayland-dev libx11-dev libxcursor-dev libxrandr-dev libxi-dev libvulkan-dev

# Build portable Linux AppDir + tarball
./scripts/package-linux.sh
```

### Windows (`x86_64-pc-windows-msvc` / `x86_64-pc-windows-gnu`)
```bash
# Native Windows build or cross-compilation via MinGW-w64
./scripts/package-windows.sh
```

---

## Unified CLI & Terminal UI (`nexus`)

The `nexus` binary provides complete command-line and terminal access to every subsystem:

```bash
nexus tui                                     # Interactive 8-pane ratatui terminal dashboard
nexus status                                  # Live telemetry snapshot with provenance & quality tiers
nexus processes --sort cpu -n 20              # Top processes with CreationToken & physical footprint
nexus processes --terminate <PID> --dry-run   # Double ProcessKey-verified process action
nexus services --running                      # OS services (launchd / systemd / Windows SCM)
nexus startup                                 # Login items, LaunchAgents, XDG autostart, Registry Run keys
nexus connections --protocol tcp4             # Active sockets with ProcessKey attribution
nexus wireless                                # Wi-Fi RF spectrum + concurrent LAN subnet radar
nexus disk-usage --path . --max-depth 4       # 7-category parallel disk scan + GPU shader blob prep
nexus disk duplicates .                       # 2-pass SHA-256 exact duplicate finder
nexus health                                  # Composite 0-100 system health score & bottleneck diagnosis
nexus alerts status                           # Evaluate threshold alerts against live telemetry
nexus rules status                            # Evaluate process watchdog & auto-balance rules
nexus benchmark --suite all --duration-secs 5 # CPU, headless wgpu GPU, uncached Disk, and HTTP transfer suite
nexus trace record --seconds 5                # Record portable .nexustrace flight capture
nexus prometheus --port 9182                  # Serve honesty-preserving Prometheus /metrics endpoint
nexus remote --port 51843                     # Run ChaCha20-Poly1305 encrypted companion remote server
nexus doctor                                  # Full honesty, provenance, WGSL shader, and .nexusskin audit
```

---

## License

Released under the [MIT License](LICENSE).
