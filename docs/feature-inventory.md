---
type: note
date: 2026-03-05
project: NexusSystemMonitor
tags: [nexus, features, inventory]
---

# Feature Inventory

Exhaustive checklist of every implemented feature, organized by category. Version column shows when the feature first shipped.

> [!tip] Reading this list
> `(v0.1.0)` = shipped in initial public release. Earlier work (Phases 1–6, pre-release) is marked `(pre-release)`.

---

## 1. Core Monitoring

- [x] CPU overall utilization with animated sparkline (pre-release)
- [x] Per-core CPU breakdown with heat-mapped core cells (pre-release)
- [x] CPU frequency and temperature — Windows WMI (pre-release)
- [x] CPU cache hierarchy info — L1/L2/L3 (pre-release)
- [x] Memory: total, used, available, committed bytes, paged pool (pre-release)
- [x] Memory slot configuration and speed info (pre-release)
- [x] Disk: per-volume I/O rates, read/write throughput, queue depth (pre-release)
- [x] Disk: NVMe/SSD/HDD hardware identification (pre-release)
- [x] Network: per-adapter bytes sent/received with sparkline (pre-release)
- [x] Network: IPv4/IPv6 addresses, link speed, active NIC detection (pre-release)
- [x] GPU: utilization, dedicated VRAM, shared memory, temperature (pre-release)
- [x] GPU: per-engine breakdown — 3D, Copy, Video Decode, Video Encode (pre-release)
- [x] Task Manager-style device sidebar with selection and sparkline history (pre-release)
- [x] TransitioningContentControl cross-fade between device detail panels (pre-release)

---

## 2. Process Management

- [x] Full process list with real-time CPU, memory, disk I/O, network, GPU per process (pre-release)
- [x] Color-coded process categories: system, service, user app, .NET managed, GPU-accelerated, suspicious, suspended (pre-release)
- [x] Set process priority: Idle → RealTime (pre-release)
- [x] Set CPU affinity (core mask) (pre-release)
- [x] Set I/O priority (pre-release)
- [x] Set memory priority (pre-release)
- [x] Efficiency mode toggle (pre-release)
- [x] Kill process (pre-release)
- [x] Kill entire process tree (children) (pre-release)
- [x] Suspend / resume process (pre-release)
- [x] Process tree visualization (parent-child hierarchy) (pre-release)
- [x] Module (DLL) enumeration with path, version info (pre-release)
- [x] Thread enumeration with per-thread CPU times and context switches (pre-release)
- [x] Environment variable inspection (pre-release)
- [x] Open file location in Explorer (pre-release)
- [x] Search process name online (pre-release)
- [x] Copy process path to clipboard (pre-release)
- [x] Process dumps for debugging (v0.1.0)
- [x] Impact Score column: composite 0–100 score per process (v0.1.5)
- [x] Rules indicator column: gear icon + tooltip showing active rules (v0.1.5)
- [x] DataGrid sort persistence across tab switches (v0.1.4)
- [x] Process category brushes update on Dark↔Light theme switch (v0.1.1)

---

## 3. System Intelligence

- [x] ProBalance: automatic CPU priority management under load (v0.1.0)
- [x] Gaming Mode: one-click optimization for foreground game processes (v0.1.0)
- [x] Rules Engine: define process rules with conditions and auto-actions (v0.1.0)
- [x] Watchdog monitoring: CPU/RAM threshold triggers over configurable duration (v0.1.0)
- [x] Alerts: configurable threshold-based alerts with OS notifications (v0.1.0)
- [x] Anomaly Detection: sliding-window statistical engine (v0.1.0)
  - [x] Anomaly events written to SQLite `resource_events` table (v0.1.5.1)
  - [x] Sensitivity control: Low/Medium/High (v0.1.3)
  - [x] Re-checks `AnomalyDetectionEnabled` at fire time (v0.1.3)
- [x] Optimization Recommendations: tiered impact ratings Critical/High/Medium (v0.1.0)
- [x] One-click optimization actions (v0.1.0)
- [x] Priority Level Guide in Optimization tab (v0.1.3.1)
- [x] System Health Score: 0–100 composite (CPU 30%, Memory 25%, Disk 20%, GPU 15%, Thermal 10%) (v0.1.5)
- [x] Bottleneck Detection: Gaming, Streaming, Video Editing, 3D Rendering, CAD workloads (v0.1.5)
  - Reports: GPU-bound, CPU-bound, VRAM-bound, Memory-bound, Storage-bound, Thermal Throttle, Balanced
  - 5-tick smoothing to suppress false positives

---

## 4. Persistence & Observability

- [x] SQLite metrics database (WAL mode, cross-platform) (v0.1.0)
  - Raw data: 1 hour retention
  - 1-minute rollups: 7 days
  - 5-minute rollups: 30 days
  - 1-hour rollups: 1 year
  - Top-15 process snapshots per tick
  - Batch writes: buffer 30 ticks, flush in single transaction
- [x] Historical Viewer tab: charts + event timeline from database (v0.1.0)
- [x] Resource Incidents section: event type, severity, value, timestamp (v0.1.5.1)
- [x] Prometheus `/metrics` endpoint on configurable port (v0.1.0)
- [x] Telegraf configuration generator: in-app setup UI (v0.1.0)
- [x] Grafana integration guide: step-by-step setup built into app (v0.1.0)
- [x] Metrics enable/disable toggle: dynamically starts/stops MetricsStore (v0.1.3.1)
- [x] HistoryView informational banner when metrics disabled (v0.1.3.1)

---

## 5. Network

- [x] Active TCP/UDP connections with state badges (pre-release)
- [x] Local and remote endpoint details (pre-release)
- [x] Navigate to owning process (cross-tab navigation) (pre-release)
- [x] Copy local/remote address to clipboard (pre-release)
- [x] Per-connection throughput via EStats (Windows only) (v0.1.0)
  - Auto-hides on hardware (TSO/RSC NICs) that return error 1784
- [x] Nmap LAN Scanner tab (v0.1.3)
  - Scan local network for hosts, open ports, OS detection, latency
  - Host tree with detail sidebar and port table
  - Real-time scan progress bar from stderr parsing
  - nmap install detection (checks well-known paths + registry refresh)
  - Latency from `srtt` attribute (actual RTT, not jitter)

---

## 6. Services & Startup

- [x] Enumerate all system services with status and startup type (pre-release)
- [x] Start, stop, restart services (pre-release)
- [x] Navigate to service host process (pre-release)
- [x] Open service file location (pre-release)
- [x] View and manage startup programs (pre-release)
- [x] Enable/disable startup entries (pre-release)
- [x] Open file location and registry keys for startup items (pre-release)
- [x] Linux: multi-init detection and management (v0.1.0)
  - systemd (`systemctl`), SysVinit (`/etc/init.d/`), OpenRC (`rc-service`)
  - Dinit, Runit, S6 (v0.1.3)
- [x] DataGrid sort persistence for Services, Startup, Network views (v0.1.4)

---

## 7. Disk Analyzer

- [x] Visual disk space breakdown by folder (treemap) (v0.1.0)
- [x] Multi-threaded directory scanning engine (`NexusMonitor.DiskAnalyzer` project) (v0.1.0)
- [x] Identify space-hogging directories at a glance (v0.1.0)

---

## 8. System Information

- [x] Detailed hardware inventory — CPU model, cores, cache hierarchy, virtualization (pre-release)
- [x] Memory modules: speed, type, slot info (pre-release)
- [x] GPU details and driver versions (pre-release)
- [x] Disk hardware identification (pre-release)
- [x] Network adapter configuration (pre-release)
- [x] Linux: full CPU/RAM/GPU/storage/BIOS details from sysfs (v0.1.3)
  - `LinuxHardwareInfoProvider` reads sysfs/proc
- [x] macOS: hostname, OS, architecture, uptime, RAM (v0.1.0)
- [x] Apple-style clean layout for at-a-glance system specs (pre-release)

---

## 9. UI & Theming

- [x] Crystal Glass (formerly Liquid Glass) theme with configurable backdrop blur (v0.1.2)
  - Opt-in via Settings → Backdrop Blur Mode
  - Specular shimmer + prismatic layers (intentional opt-in feature)
- [x] 18 built-in theme presets (v0.1.6)
  - Nexus Default, Deep Dark, Neon, Dark Sakura, Anime, Futuristic, Outer Space, Magical, Techno, Ocean Depth, Sunset, Dracula, Solarized Dark, Solarized Light, Nord, Cherry Blossom, Arctic, Minimalist
- [x] Dynamic surface swatch palettes per preset (v0.1.6)
  - 8 swatches each for Window Chrome, Cards & Content, Sidebar & Navigation
  - Theme-aware: dark and light palettes curated per preset
- [x] 8 accent color presets with runtime switching (pre-release)
- [x] Custom accent color via color picker (v0.1.3.1)
- [x] Text accent color picker (v0.1.3)
- [x] Window Chrome / Cards / Sidebar custom surface colors (v0.1.3.1)
- [x] Shared color picker window with live preview and editable hex (v0.1.3.1)
- [x] Dark/Light/System theme mode (v0.1.2)
  - System option follows OS preference at runtime without restart
- [x] Font size multiplier: 0.8–1.5× slider scales all UI text (v0.1.3)
- [x] 382 DynamicResource replacements — instant Dark↔Light toggle (v0.1.1)
- [x] Grouped sidebar navigation (Pinned / Monitor / Tools / System) (v0.1.5)
  - Visual separators between groups
  - Drag-reorder constrained within groups
  - Pinned items exempt from drag
- [x] Fluent System Icons font (`NexusIcons`) (v0.1.5)
- [x] Desktop overlay widget: CPU, RAM, NET, GPU (pre-release)
  - Always-on-top, draggable, transparent (230×168 px)
  - Theme-aware background alpha
- [x] System tray icon with quick-access menu (v0.1.0)
- [x] Custom title bar with window controls (pre-release)
- [x] About dialog with version, build info, links (v0.1.1)
- [x] Close behavior setting (minimize to tray vs. quit) (v0.1.0)
- [x] Ctrl+Q force quit (v0.1.3.1)
- [x] Smart Tint luminance-based overlay alpha (v0.1.2)
- [x] System Health Dashboard as default landing tab (v0.1.5)
  - Health score widget, 4 subsystem cards, top-5 process consumers
  - Plain-English contextual recommendations
- [x] Customizable dashboard: page-engine widget grid replaces the fixed layout (Unreleased)
  - Edit mode: drag to reposition, resize by edges, add/remove widgets, step-by-step Undo, Tidy auto-compact
  - Widget gallery covering every available dashboard widget
  - Workspace profiles: named layout + full-theme bundles, switchable from Settings, export/import as `.nexusprofile` (theme-only export supported)
  - Pop-out windows: any widget → its own OS window, position/size persisted per profile, up to 6 simultaneous
  - `EnablePageEngine` experimental flag retired — always on; layout-load failures fall back to factory defaults with an in-app notice, corrupt profile files preserved as `.bak`

---

## 10. Cross-Platform

- [x] Windows 10/11 (x64, ARM64): full P/Invoke, PDH, WMI implementation (pre-release)
- [x] macOS 12+ (Intel + Apple Silicon): sysctl, Mach APIs, ObjC runtime (v0.1.0)
  - Per-core CPU via `host_processor_info` P/Invoke
  - Disk I/O via `ioreg -c IOBlockStorageDriver`
  - Foreground window via ObjC P/Invoke into `libobjc.A.dylib`
  - Power plans via `pmset`
- [x] Linux (x64, ARM64): procfs, sysfs, multi-init (v0.1.0)
  - Network PIDs via `/proc/[pid]/fd/` socket inode matching
  - Temperature from `/sys/class/hwmon`
  - Power plans via `powerprofilesctl` or `scaling_governor`
- [x] `LINUX` define in both `.csproj` and all `linux-*.pubxml` profiles (v0.1.0)
- [x] Platform feature detection at runtime (e.g. `SupportsPerConnectionThroughput`) (v0.1.0)

---

## 11. Packaging & Distribution

- [x] GitHub Actions release workflow triggered on `v*` tags (v0.1.0)
- [x] Fan-out/fan-in CI: build-test → parallel platform builds → create-release (v0.1.0)
- [x] 12 release artifacts across 6 RIDs (v0.1.0)
  - win-x64/arm64: ZIP + Inno Setup `.exe`
  - osx-x64/arm64: tar.gz + DMG (via `create-dmg`)
  - linux-x64/arm64: tar.gz + AppImage (x64) + .deb (x64)
- [x] Version source of truth: `Directory.Build.props` `<Version>` (v0.1.0)
- [x] Self-contained publish profiles for all 6 RIDs (v0.1.0)
- [x] GC tuning via `runtimeconfig.template.json` (v0.1.1)
- [x] GitHub Sponsors: `FUNDING.yml` + README badge (v0.1.5.1)

---

## 12. Performance Optimizations

- [x] CPU usage reduced from ~25% to ~3–8% on Linux (v0.1.1)
- [x] RAM reduced from ~300 MB to ~100–150 MB on Linux (v0.1.1)
- [x] Eliminated stream-interval race across all 9 data providers (v0.1.1)
- [x] macOS metrics via sysctl/statvfs P/Invoke (eliminates subprocess per tick) (v0.1.1)
- [x] Linux `/proc` stable data cached (30s TTL) (v0.1.1)
- [x] Linux network inode map cached (10s TTL) (v0.1.1)
- [x] 4 memory leaks fixed: SettingsViewModel, SettingsService, FindWindowOverlay, AnomalyDetectionService (v0.1.1)
- [x] `_procStats` LRU eviction in AnomalyDetectionService (v0.1.1)
