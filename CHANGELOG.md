# Changelog

All notable changes to Nexus System Monitor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.6.0] - 2026-07-09

### Added
- **Customizable dashboard:** the Dashboard is now a fully editable widget grid — enter edit
  mode to drag, resize, add, and remove widgets, with step-by-step Undo and a one-click Tidy
  to auto-compact the layout. Add widgets from a gallery covering every available card (health
  score, subsystem cards, bottleneck detection, top consumers, recommendations, predictions,
  health trends, and more). Stable and on by default.
- **Workspace profiles:** save named bundles of layout + full theme, switch between them
  instantly from Settings, and export/import them as `.nexusprofile` files — including a
  theme-only export for sharing just the look without the layout.
- **Pop-out widgets:** pop any dashboard widget into its own OS window; position and size
  persist per profile, with up to 6 pop-outs open simultaneously.
- **Motion and depth system:** a central Animation Speed control (`Settings → Appearance →
  Animations`) scales every UI transition — page switches, hover lift, edit-mode chrome fade,
  pop-out open/close, value-change ticks, and Crystal Glass specular shimmer — with a per-effect
  toggle for each, a 0 (off) to 2x (fastest) range, and a live Slower/Normal/Fast/Fastest label.
  A companion Depth Intensity slider scales card and pop-out elevation shadows from flat to full
  depth, independent of Crystal Glass.
- **Dynamic type in dashboard tiles:** widget headline and value text now scales with the
  tile's own size when resized in edit mode (`Settings → Typography → Scale text with widget
  size`), layered on top of the existing Font Size multiplier; can be turned off to keep fixed
  text sizes regardless of tile size.
- **Real per-OS acrylic backdrop:** the window Backdrop setting (`None` / `Blur` / `Acrylic` /
  `Mica`) now drives an actual native transparency material per platform, with an automatic
  fallback chain when a platform can't render the requested level and a defensive re-apply if
  the OS transiently rejects it on launch.
- **Linux: real AMD GPU temperature.** The GPU card now reports actual temperature readings for
  AMD GPUs on Linux, alongside the existing NVIDIA support, instead of a placeholder value.
- **Linux: I/O priority, process handles, and memory map.** The Processes tab's I/O priority
  control now works on Linux, and the Handles and Memory Map detail views — previously
  Windows-only — now show real data on Linux as well.
- **macOS: Services now show a real startup type.** The Services list reports actual
  Automatic / Manual / Disabled status (read from the system's launch service records) instead
  of always showing "Unknown".
- **macOS: Efficiency Mode.** Processes can now be switched into the OS's low-priority background
  mode on macOS, matching the existing Windows and Linux behavior.
- **macOS: Create Dump File.** Capturing a diagnostic dump for a process is now supported on
  macOS (a call-stack sample report rather than a full memory dump).
- **macOS: Processes tab shows the owning user.** The User column, previously blank on macOS,
  is now populated for every process.

### Changed
- **Dashboard layout-load failures** now fall back to the factory-default layout with an in-app
  notice instead of failing silently; a corrupt workspace-profile file is preserved alongside
  the original as a `.bak` copy rather than being overwritten or discarded.
- **System Settings page** now mirrors the macOS System Settings sidebar layout and uses
  macOS-style pill switches in place of the previous checkbox-style toggles.
- **Logo and titlebar:** the app logo and wordmark moved to the upper-right of the titlebar,
  vertically centered with the traffic-light window controls, keeping the rest of the titlebar
  clean; window-control centering was also normalized across platforms.
- **Clearer feature names:** the automation features are now Auto-Balance, Idle Throttle, and
  Memory Reclaim; existing settings migrate automatically.
- **Dashboard edit mode is easier to find:** the unlabeled pencil icon is now a labeled "Edit"
  button, and the Command Palette gains "Edit Dashboard" and "Add Widget…" entries.
- **Widget gallery redesigned:** adding a widget now opens a proper gallery — icon-tile cards
  grouped into Monitoring, Analysis, and Insights — instead of a plain dropdown list.
- **Command Palette redesigned:** now a single rounded card with a search icon and type labels,
  closer to a native Spotlight-style launcher.
- **Accent color now applies everywhere:** a handful of controls (including the Run Analysis
  button) stayed on the default blue when a custom accent color was set; they now follow the
  chosen accent like the rest of the app.
- **Fit-and-finish:** System Info no longer shows a cryptic internal model code for "Socket" on
  Mac, and shows a plain "Unified memory" note instead of an empty RAM-slot table; the Network
  tab's connection-state column no longer clips "ESTABLISHED"; Performance sub-navigation labels
  no longer collide with their metrics.

### Fixed
- **Crash on quit.** The app could crash when closing on macOS; the shutdown sequence is now
  handled correctly on every close path (window close, Cmd+Q, tray quit).
- **macOS Network tab:** IPv6 connections were silently missing and are now shown; process IDs
  in the connection list — previously often wrong — are now accurate.
- **Disk Analyzer no longer jitters during a scan:** the stats bar, Scan/Cancel button, and
  status text no longer shift position while a scan is running.
- **Honest status instead of silent failure:** Foreground Boost and part of Gaming Mode now
  disable themselves with an explanation on platforms that don't support them, instead of
  looking active while doing nothing; the health score no longer rates GPU health as
  "Excellent" when no real GPU data exists; unavailable readings (temperature, clock speed,
  cache size) now show "—" instead of a misleading zero; the LAN Scanner discloses when a scan
  ran without the permissions needed for complete results.
- **Theme switching fixed from the Command Palette:** switching themes there previously left
  glass effects, accent colors, and shadows out of sync with the new theme; it now applies
  fully. The Font Family selector no longer shows a blank box after switching away from the
  default, and the System Info Uptime field — previously blank on every platform — now displays
  correctly.
- **macOS Processes tab:** per-process CPU usage was blank for almost every process; it now
  shows a real percentage for processes the app can read, and "—" (rather than a false 0%) for
  processes it can't.
- **Diagnostics and Optimization pages now reflect real state:** the Diagnostics empty message
  no longer claims monitoring is active while it's paused, and the Optimization "all clear"
  checkmark no longer stays visible when there are recommendations waiting. The same underlying
  display bug is fixed in several other places it was hiding: an always-visible error banner in
  Rules, and stray banners in History, Alerts, and Gaming Mode.

### Removed
- **`EnablePageEngine` experimental flag:** the customizable dashboard is always on now — the
  Settings → Experimental toggle is gone. The `EnablePageEngine` field remains on old settings
  files purely so they still deserialize; nothing reads it anymore.

## [0.5.2] - 2026-06-18

*Reconstructed from git history 2026-07-04.*

### Fixed
- **macOS crash-on-close:** installed an Rx `DefaultExceptionHandler`, ordered the shutdown
  sequence, and corrected the release-build exit path so the app no longer crashes when closed.

## [0.5.1] - 2026-06-18

*Reconstructed from git history 2026-07-04.*

### Added
- **macOS data providers filled in:** CPU, memory, network, disk, and hardware-info stubs
  (left as placeholders by the v0.5.0 TFM retarget) now return real live data.
- **Honest "N/A" fields on macOS:** metrics with no macOS API equivalent (CPU/GPU temperature,
  GPU usage/engines/VRAM, handle count, memory pools) now display "N/A" instead of a
  misleading placeholder value.

## [0.5.0] - 2026-06-18

*Reconstructed from git history 2026-07-04.*

### Fixed
- **[Critical] macOS startup crash (SIGBUS):** `mach_task_self_` was being P/Invoked as a
  function instead of read as an exported data symbol — corrected, fixing a crash on every
  macOS launch.
- **macOS build retargeted from `net8.0-macos` to plain `net8.0`:** the `net8.0-macos` /
  `Microsoft.macOS` workload TFM's ObjC runtime was the root cause of the SIGBUS on macOS 26
  (Tahoe) and Apple Silicon; the `.app` bundle is now built from a flat `net8.0` publish output.
- Corrected signed/unsigned type mismatches in Mach P/Invoke signatures.
- Native traffic-light window buttons restored via `PreferSystemChrome` (regressed by the
  TFM retarget).
- `osascript` invocations now passed via `ArgumentList` instead of a single interpolated
  argument string, removing a quoting/injection hazard.
- Prediction depletion test now asserts against the fixed test clock instead of wall-clock
  time, removing test flakiness.

## [0.4.1] - 2026-04-13

*Reconstructed from git history 2026-07-04.*

### Added
- **Resource Health Trends & Predictions:** `HealthSnapshotPersistenceService` persists rolling
  health-score snapshots; a new `HealthTrendsView` is embedded in the Dashboard; `PredictionService`
  forecasts resource depletion from health-score trends and surfaces dismissible prediction cards
  (togglable in Settings).
- **Webhook notifications:** configurable webhook delivery for Alerts/Rules events, with a payload
  model and Settings UI toggle.
- **Real hardware temperatures:** LibreHardwareMonitor integrated on Windows for accurate CPU/GPU
  temperature readings, replacing estimated values.
- **HVCI dashboard card:** in-app fix action plus UX improvements for Hypervisor-Enforced Code
  Integrity warnings.

### Changed
- **Runtime footprint pass (4 batches):** working-set trimming, chart downsampling, buffer caps,
  lazy ViewModel initialization with Activate/Deactivate lifecycle, disabled chart animations,
  reduced SQLite cache (8 MB → 1 MB), `ConserveMemory=9`, disabled `TieredCompilation`, capped
  `ThreadPool`, and blocking GC before `EmptyWorkingSet` — together targeting lower idle CPU/RAM
  overhead.

### Fixed
- Four rounds of thread-safety and quality fixes across Rx subscriptions, `MetricsStore` restart
  handling, `AlertsService` race conditions, history locks, deadlocks, and silently-swallowed
  exceptions.
- Windows `Registry` calls guarded with `#if WINDOWS` so the solution builds cleanly on
  cross-platform CI runners.
- macOS Gatekeeper workaround documented in the README for the unsigned build on newer macOS
  versions.

## [0.4.0] - 2026-04-12

*Reconstructed from git history 2026-07-04.*

### Added
- **Command Palette (Ctrl+K):** glass overlay with fuzzy filtering by label/category, keyboard
  navigation, fade-in animation, scroll-to-selected, and focus restore.
- **Process Groups:** new `ProcessGroup` model with wildcard pattern matching, SQLite-backed
  `ProcessGroupStore`, a management UI (`ProcessGroupsView`/`ViewModel`), a colored Group badge
  column plus group summary strip in the Processes tab, and group-based targeting in the Rules
  Engine.
- **Quiet Hours:** time-window + day-of-week notification suppression, with a dedicated
  Automation card, a sidebar 🌙 badge when active, and settings wiring.

### Changed
- `WildcardMatcher` extracted from `ProcessRule` into a shared, reusable utility.
- `ProcessGroupStore`'s cache is now pre-sorted on mutation instead of re-sorting per lookup;
  `HexColorToBrushConverter` now caches brushes — both reduce per-tick UI overhead.

### Fixed
- Command palette startup visibility, redundant `SelectedIndex`, opacity/fade-in animation
  timing, focus restore, and item-click handling.
- Group-only rules now supported; `GroupSummaries` sync and the badge template's `DataType`
  corrected.
- `x:DataType` and null-store-get guards corrected in `ProcessGroupsView`.

## [0.3.0] - 2026-03-25

*Reconstructed from git history 2026-07-04.*

### Added
- Session persistence: window geometry and the active tab are now restored from the previous
  session on launch.

### Changed
- Docs: Disk Analyzer marked live, removing stale "currently disabled" language.

### Fixed
- CLI now reports its real assembly version instead of a hardcoded `0.1.8.2`.
- `MetricsStore` hard buffer cap prevents unbounded memory growth (OOM) if a flush stalls.
- CLI `Ctrl+C` / process-exit handling wired to all long-running commands; `GlobalCts` disposed
  safely in a `finally` block, guarded against `ObjectDisposedException`.
- Linux `Statvfs.Spare` field corrected from `ulong[]` to `int[]` to match the Linux ABI.
- Empty catch blocks in Linux providers replaced with real exception handling.

## [0.2.0] - 2026-03-24

*Reconstructed from git history 2026-07-04.*

### Added
- **Phase 20 unit test suite (255 tests):** coverage added for `RulesEngine`, `MetricsStore`,
  `SettingsService`, `AnomalyDetectionService`, `PerformanceProfileService`, health scoring,
  sliding-window stats, and `ProcessPreferenceStore`, plus supporting mock/Rx/DB test
  infrastructure.
- Docs: NexusCLI section with per-OS launch instructions; corrected release artifact names.

### Fixed
- Disallowed process rule now correctly sets `ruleMatched` so it can no longer be overridden by
  preference fallback.

## [0.1.8.2] - 2026-03-18

*Reconstructed from git history 2026-07-04.*

- Fixed CLI bar renderers crashing/mis-rendering when process names contained
  Spectre.Console markup-like brackets (`[`/`]`) — brackets now escaped before rendering.

## [0.1.8.1] - 2026-03-17

*Reconstructed from git history 2026-07-04.*

- Fixed macOS packaging: proper `Info.plist` metadata and human-readable release artifact
  names (previously version-only filenames).
- Fixed macOS ad-hoc code signing and icon embedding in the app bundle.
- Polished README and added `CONTRIBUTING.md` ahead of the public community launch.

## [0.1.8] - 2026-03-11

### Added (Phase 19 — Logging + Housekeeping)
- **Structured logging via Serilog:** Rolling file sink at `%AppData%\NexusMonitor\logs\nexus-.log`
  (daily roll, 10 MB cap, 7-file retention). `Microsoft.Extensions.Logging` wired into DI so all
  services receive typed `ILogger<T>` instances.
- **Runtime logging for key services:** `RulesEngine`, `GamingModeService`, `AutoBalanceService`,
  `PerformanceProfileService`, `MetricsStore`, and `SettingsService` now log warnings on
  recoverable failures rather than swallowing exceptions silently.
- **Startup/shutdown logging:** Unhandled exceptions in `AppDomain`, `TaskScheduler`, and the
  Avalonia dispatcher are now forwarded to Serilog in addition to `CrashLogger`.
- **Nmap target sanitization:** `NmapScannerService.BuildArgs` validates the target string against
  an IP/CIDR/hostname allowlist regex before appending it to nmap arguments, preventing flag
  injection via the Target field.

### Fixed (Bug Audit Round 3 — 24 bugs across 22 files)

**Batch 1 — Shutdown Symmetry**
- **`App.axaml.cs` shutdown handler** now calls `Stop()` on all 15+ background services in safe
  teardown order (automation → monitoring → data persistence → infrastructure). Previously only 6
  of the started services were stopped, leaving timers, threads, and modified process priorities
  dangling on exit.
- **`ServiceStarter.StopCoreServices` (CLI)** updated to mirror the same complete teardown.
- **`PerformanceProfileService.Dispose()`** now calls `DeactivateProfile()` first so boosted
  processes and the power plan are restored before the service is torn down.

**Batch 2 — Silent Rx Death + `async void` Crash Risk**
- **`ForegroundBoostService`, `CpuLimiterService`, `InstanceBalancerService`, `IdleThrottleService`,
  `SleepPreventionService`:** All rewritten — added `ILogger<T>` injection, Rx error handlers that
  set `_running = false` (so `Start()` can re-subscribe), `_running` guard in `Start()`, and full
  `try/catch` wrappers inside `async void OnTick` to prevent unhandled exceptions from reaching
  `SynchronizationContext` and crashing the process.
- **`AlertsService`:** Rx error handler now sets `_running = false` instead of silently discarding.
- **`RulesEngine`, `AutoBalanceService`:** Error handlers updated to set `_running = false`; `Start()`
  guarded against double-subscription.

**Batch 3 — Thread Safety**
- **`SemaphoreSlim _tickLock = new(1, 1)`** added to `ForegroundBoostService`, `CpuLimiterService`,
  `InstanceBalancerService`, `IdleThrottleService`, `AutoBalanceService`, and `RulesEngine`. `OnTick`
  skips with `WaitAsync(0)` if the previous tick is still in flight; `Stop()` drains the semaphore
  before calling `RestoreAllAsync` to prevent restoring while a tick holds modified state.
- **`GamingModeService`:** `_stateLock` SemaphoreSlim serialises `ThrottleBackgroundProcessesAsync`
  and `RestoreAllAsync` to prevent concurrent mutation of the `_throttled` dictionary.
- **`PerformanceProfileService`:** `_applyLock` SemaphoreSlim serialises `ApplyProfileAsync`
  (polling skip-if-busy) vs `RestoreProcessesAsync` (waits for in-flight apply).
- **`BottleneckDetector`:** Static `_smoothLock` wraps all 5 static `Queue<double>` mutations in
  `Analyse()`, preventing corruption if called from multiple threads.
- **`AnomalyDetectionService`:** `_cooldownLock` guards `IsCooldownElapsed` / `MarkCooldown`
  against concurrent access from 3 independent Rx subscriptions; added `_running` guard in `Start()`
  and `Stop()`.
- **`MemoryReclaimService`:** `ScheduleNextTrim()` now disposes the previous `_timerSubscription`
  before reassigning, closing a subscription leak that grew unbounded over time.

**Batch 4 — Platform Bugs**
- **`WindowsStartupProvider` (Critical):** `ReadApproved` was bit-testing the wrong way (treating
  odd byte = enabled). Corrected to even byte = enabled (`(bytes[0] & 1) == 0`). `SetEnabled`
  was writing 8-byte values with incorrect byte codes (`0x00`/`0x02`); corrected to 12-byte
  values with `0x02` (enabled) / `0x03` (disabled) per the Windows `StartupApproved` format.
- **`MacOSSystemMetricsProvider.ExtractBaseDiskName`:** `IndexOf('s')` returned index 1 (the 's'
  in "disk"), causing all I/O matching for partitioned devices to fail. Replaced with a right-to-
  left search for 's' flanked by digits, correctly stripping "disk3s5" → "disk3".
- **`MacOSProcessProvider.Snapshot()`:** Added `if (_disposed) return Array.Empty<ProcessInfo>()`
  guard at entry — timer callbacks firing after `Dispose()` could write to freed `_taskInfoPtr`
  memory.
- **`LinuxPowerPlanProvider.SetActivePlan`:** `_active = schemeGuid` moved to after the backend
  system call; previously a failed call would leave `GetActivePlan()` returning the wrong state.

**Batch 5 — UI Thread Safety**
- **`PerformanceProfilesViewModel`:** `ProfileChanged` subscription wrapped with
  `ObserveOn(RxApp.MainThreadScheduler)` — the observable fires from a background thread, and
  directly setting an `ObservableProperty` from it violated Avalonia's threading contract.
- **`LanScannerViewModel`:** Constructor `Task.Run` results now marshalled via
  `Dispatcher.UIThread.Post` before setting `NmapAvailable`, `PackageManagerName`, and
  `StatusText`.
- **`SettingsViewModel.OnLuminanceChanged`:** Event handler from `GlassAdaptiveService` (background
  thread) wrapped in `Dispatcher.UIThread.Post` to guard `ApplyGlass()` which writes to
  `Application.Current.Resources`.

### Fixed (housekeeping)
- **Version mismatch:** `Directory.Build.props` now correctly reflects version `0.1.8`
  (was `0.1.6` at start of v0.1.7 cycle; carried forward).

## [0.1.7] - 2026-03-06

### Added
- **Platform capability gating:** `IPlatformCapabilities` extended with `SupportsRegistry`,
  `SupportsEfficiencyMode`, `SupportsHandles`, `SupportsMemoryMap`, `SupportsPowerPlan`, and
  `OpenLocationMenuLabel`. All 3 platform implementations and `MockPlatformCapabilities` updated.
- **Cross-platform UI correctness (Apple Design Philosophy pass):** 14 UI elements now hidden or
  relabelled on platforms where they have no effect:
  - RulesView editor: CPU Set IDs, I/O Priority, Memory Priority, Efficiency Mode (EcoQoS),
    CPU Affinity Mask hidden on macOS/Linux
  - AutomationView: EcoQoS checkbox hidden on macOS/Linux; Memory Reclaim, CPU Limiter, and
    Instance Balancer cards hidden on platforms without affinity/trim support
  - ProcessesView: Handles and Memory Map detail sections hidden on macOS/Linux
  - PerformanceProfilesView: Power Plan rows and Efficiency Mode column hidden on macOS
  - StartupView: "Open Registry Key" menu item hidden on non-Windows
  - Context menus: "Open File Location" → "Reveal in Finder" (macOS) / "Show in Files" (Linux)
  - SettingsView: Removed Windows-specific backdrop blur mode hint text

### Fixed
- **[Critical] `MacOSSleepPreventionProvider`:** Replaced broken `IOPMAssertionCreateWithName`
  P/Invoke (which silently failed due to incorrect `CFStringRef` marshalling) with a
  `caffeinate -di` subprocess, matching the Linux `systemd-inhibit` pattern
- **`MacOSHardwareInfoProvider` storage size:** `spstorage_volume_size` now parses string values
  (`"499.96 GB"`) in addition to numeric byte values; storage capacity no longer shows 0 on
  modern macOS versions
- **`RunCommand` deadlock:** `ReadToEnd()` replaced with `ReadToEndAsync()` + `WaitForExit`
  timeout in `MacOSHardwareInfoProvider`, `MacOSSystemMetricsProvider`, and
  `LinuxSystemMetricsProvider` (nvidia-smi). Hung processes are now killed after timeout
- **Thread safety:** `_metricsLock` added to `MacOSSystemMetricsProvider` and
  `LinuxSystemMetricsProvider`; `BuildMetrics()` body wrapped in lock to prevent concurrent
  mutation of delta-tracking dictionaries from the `Observable.Timer` thread
- **Shell context menu process leak:** `MacOSShellContextMenuService` and
  `LinuxShellContextMenuService` now use `using var p = Process.Start(...)` for proper disposal
- **Path injection in shell helpers:** Both context menu services now use `ArgumentList` instead
  of string interpolation, eliminating potential injection via paths containing spaces or quotes
- **`IDisposable`** added to `MacOSSleepPreventionProvider` and `LinuxSleepPreventionProvider`
  so held processes are released on DI container teardown
- **`MftScanner` fallback:** Native failure now reports the exception type and message via
  `IProgress<ScanProgress>` before falling back to the recursive scanner
- **`RecursiveScanner` stack overflow:** Recursive `ScanDirectory` replaced with iterative DFS
  (`Stack<>` + post-order list) to prevent stack overflow on deep trees (e.g. `node_modules`)
- **Treemap hover highlight:** `TreemapControl` now tracks the hovered node and passes it to
  the draw operation for visual hit feedback

## [0.1.6] - 2026-03-05

### Added
- **Dynamic surface swatch palettes:** Background & Surface Colors section now shows 8 vibrant,
  theme-aware swatches for each UI surface (Window Chrome, Cards & Content, Sidebar & Navigation).
  Swatches update automatically when switching presets — e.g. cherry-blossom pinks for Dark Sakura,
  neon/cyberpunk colors for Neon, Dracula palette for Dracula, aurora colors for Nord, and
  Apple-style accent colors for base dark/custom mode. Light-theme palettes are unchanged.
- **18 built-in theme presets** with curated per-preset surface palettes covering all themes:
  Nexus Default, Deep Dark, Neon, Dark Sakura, Anime, Futuristic, Outer Space, Magical, Techno,
  Ocean Depth, Sunset, Dracula, Solarized Dark/Light, Nord, Cherry Blossom, Arctic, Minimalist.
- **`SwatchColor` model** (`NexusMonitor.Core.Models`) and **`SurfaceSwatchPalettes`** static
  lookup class (`NexusMonitor.Core.Themes`) for clean preset → palette resolution.

## [0.1.5.1] - 2026-03-04

### Added
- **GitHub Sponsors:** `FUNDING.yml` and README sponsor badge configured for `brass458`
- **Event-based bottleneck analysis:** `ResourceEvent` model, `EventMonitorService`, and
  `EventRepository` capture discrete resource threshold-crossing events; persisted to a new
  `resource_events` SQLite table; surfaced in the History tab as a **Resource Incidents**
  section showing event type, severity, value, and timestamp

### Fixed
- **Dashboard scroll:** `ScrollViewer` bottom spacer added so content is not clipped at the
  bottom of the tab on standard-height displays

## [0.1.5] - 2026-03-04

### Added
- **System Health Dashboard:** New default landing tab with an at-a-glance health score
  (0–100 composite weighted across CPU 30%, Memory 25%, Disk 20%, GPU 15%, Thermal 10%),
  4 subsystem cards, top-5 process consumers, and plain-English contextual recommendations
- **Bottleneck Detection:** Live bottleneck analysis card on the Dashboard identifies the
  performance-limiting component for Gaming, Streaming, Video Editing, 3D Rendering, and CAD
  workloads. Reports GPU-bound, CPU-bound, VRAM-bound, Memory-bound, Storage-bound, Thermal
  Throttle, or Balanced with explanations and actionable upgrade/tuning advice. Uses 5-tick
  smoothing to suppress single-frame false positives
- **Impact Score column** in Processes tab: composite 0–100 score per process visible alongside
  the new Rules indicator column (gear icon + tooltip)
- **Fluent icons:** `FluentSystemIcons-Regular.ttf` (MIT) registered as `NexusIcons` FontFamily;
  replaces emoji/text icons in the sidebar and Dashboard subsystem cards

### Changed
- **Sidebar navigation** reorganised into four named groups — Pinned, Monitor, Tools, System —
  with visual separators; pinned items are exempt from drag-reorder
- **Drag constraints:** items can only be reordered within their own group; separators and
  pinned items cannot be dragged
- **Dashboard** is now the first tab (eager-loaded); previously Processes was the landing page

### Fixed
- **Dashboard scroll:** `ScrollViewer` was missing `VerticalScrollBarVisibility="Auto"` — tab
  now scrolls correctly on smaller screens

## [0.1.4] - 2026-03-04

### Fixed
- **DataGrid sort persistence:** sort state (column + direction) now survives tab switches in
  Processes, Services, Startup, and Network views — Views are destroyed on tab switch but the
  sort is saved in the ViewModel (singleton) and restored on `OnLoaded`
- **Sort guard race:** `col.Sort()` posts `ProcessSort` via `Dispatcher.UIThread.Post`; the old
  `finally { _restoringSort = false }` block fired before those async callbacks. Fixed by
  resetting the guard via `Post` (same priority = FIFO), so it drops only after all sort events
  complete
- **Null `SortMemberPath`:** `DataGridTextColumn` without explicit `SortMemberPath` returns null
  in the `Sorting` handler — added explicit `SortMemberPath` to every column in all four views

## [0.1.3.1] - 2026-03-04

### Added
- **Shared color picker:** `SurfaceColorPickerWindow` removed; all 5 color pickers (primary
  accent, text accent, window chrome, cards, sidebar) now share a single `ColorPickerWindow`
  with live preview, editable hex input, and dynamic title via `ColorPickerTarget` enum
- **Nmap progress bar:** scan progress parsed from stderr (`"About X% done"`) and displayed
  as a live progress bar in LanScannerView
- **Metrics toggle:** new enable/disable control in Settings → Metrics & History; dynamically
  starts/stops `MetricsStore` and `MetricsRollupService`; HistoryView shows an informational
  banner when disabled

### Fixed
- **Color wheel TwoWay binding:** `ColorWheelControl.SelectedColorProperty` now uses TwoWay
  mode — dragging the wheel correctly pushes the selected color back to `PickerCurrentColor`
  (fixes "Apply does nothing" and "hex stays empty")
- **Sort state in ViewModel:** moved sort state (`SortMemberPath` / `SortDirection`) from
  `ProcessesView` fields to `ProcessesViewModel` properties so sort survives View recreation
- **Nmap latency:** was parsing `rttvar` (jitter); now parses `srtt` (actual RTT) from nmap XML
- **Nmap stderr surfacing:** all stderr output (privilege errors, scan phase banners) shown in
  UI; `HostsUp` guarded against sentinel `-1` values
- **Nmap install detection:** `IsAvailable()` now checks well-known install paths first and
  refreshes PATH from the Windows registry so newly installed nmap is found without restart;
  install output hidden by default with a "Show Details" toggle
- **Font size slider:** now scales all `NxFont*` / `FontSize*` resource tokens at runtime
- **Color wheel hue offset:** corrected +90°/−90° so the selector dot matches the visual colour
  under the cursor
- **Color picker height:** `ColorPickerWindow` increased 336 → 400 px so Apply/Cancel always
  visible
- **Light mode category labels:** `ProcessCategoryToBrushConverter` fallback changed from
  `Brushes.White` to `TextPrimaryBrush`; `ProcessesViewModel` re-evaluates on theme change
- **ComboBox widths:** all `ComboBox` controls in SettingsView changed from `Width=` to
  `MinWidth=` to prevent clipping at larger font-scale multipliers
- **Priority Level Guide:** moved to top of Optimization tab with expanded user-friendly
  descriptions for each priority level
- **Ctrl+Q:** now sets `_forceClose = true` before `Close()` so it always quits regardless
  of the "When Pressing Close Button" setting
- **Process sort feedback loop:** `RestoreSort` → `OnGridSorting` re-entrancy fixed with a
  guard flag

## [0.1.3] - 2026-03-03

### Added
- **Nmap LAN Scanner tab:** scan the local network for hosts, open ports, OS detection, and
  latency; host tree with detail sidebar and port table (`LanScannerView` / `LanScannerViewModel`)
- **Linux init backends:** Dinit, Runit, S6 service managers detected and managed alongside
  the existing Systemd, SysVinit, and OpenRC backends
- **Linux hardware info:** full CPU / RAM / GPU / storage / BIOS details from sysfs via
  `LinuxHardwareInfoProvider`
- **Linux temperature:** scans `/sys/class/hwmon` for `coretemp` / `k10temp` / `zenpower`
  before falling back to `thermal_zone*`
- **Font size multiplier:** 0.8–1.5× slider in Settings scales all UI text via
  `AppSettings.FontSizeMultiplier`; `NxFont*` tokens updated via `DynamicResource`
- **Text accent color:** swatches in Settings now wire to `SetTextAccentColorCommand`

### Changed
- **Defaults tightened:** `MetricsEnabled`, `AnomalyDetectionEnabled`, and OS notifications
  all default off; `AnomalySensitivity` defaults to Low; `MetricsDatabase` registration is
  lazy; detail sidebars (Processes / Network / Services) default collapsed
- **"Details" toggle** renamed to "Info Panel" throughout UI
- **Process category brushes** moved into `ThemeDictionaries` (dark + light variants) so they
  update on theme change without restart

### Fixed
- **Anomaly notifications:** re-check `AnomalyDetectionEnabled` at fire time to prevent
  notifications firing after the feature is disabled mid-session
- **Tray Quit on Linux:** `ForceQuitFromTray` flag allows clean exit from system tray
- **Duplicate window decorations on Linux:** `SystemDecorations.BorderOnly` set in `OnOpened`

## [0.1.2] - 2026-03-03

### Added
- **System theme detection:** New "System" option in Settings → Theme follows OS dark/light preference automatically at runtime; OS changes apply without restart
- `BgBaseOpaqueBrush` resource in both theme dictionaries for dialogs that must be fully opaque

### Changed
- **Theme selector:** Dark/Light toggle replaced with a three-option ComboBox (System / Dark / Light)
- **Crystal Glass:** renamed from "Liquid Glass" across all UI, code, and documentation
- **Specular rim lights:** wider mouse-tracking range — bright spot now reaches all four corners of the window (horizontal: 0–85%, vertical: 0–50%); tighter cone makes the highlight more focused
- **Desktop overlay widget** category labels (CPU / RAM / NET / GPU) now use `TextSecondaryBrush` instead of `TextTertiary` for legibility on bright wallpapers
- **Overlay background alpha floor** is theme-aware: 50% in dark mode, 63% in light mode; Smart Tint's `luminanceMinAlpha` also feeds into the overlay

### Fixed
- **Light mode text readability:** `TextTertiary` alpha raised from 50% (`#80`) to 72% (`#B8`) — description text in Settings is now clearly readable in light mode
- **Close dialog:** fully opaque (no wallpaper bleed-through), widened to 480 px so button text no longer truncates
- **Startup theme:** app previously always launched in Dark mode regardless of OS preference; now defaults to OS theme on first run

## [0.1.1] - 2026-03-03

### Added
- About dialog with version, build info, and links
- GC tuning via `runtimeconfig.template.json` for lower steady-state memory footprint

### Changed
- macOS metrics collection (disk I/O, mounts) uses sysctl/statvfs P/Invoke instead of spawning subprocesses — eliminates per-tick process overhead
- Linux process and network providers cache stable `/proc` data (30-second TTL) to eliminate redundant kernel reads
- Linux disk mount table cached (30-second TTL) to avoid re-parsing `/proc/mounts` every tick
- Linux network inode map refresh extended to 10-second cache with P/Invoke `readlink` replacing subprocess-based lookup

### Fixed
- **Performance:** CPU usage reduced from ~25% to ~3–8% on Linux; RAM reduced from ~300 MB to ~100–150 MB
- **Performance:** Eliminated stream-interval race across all 9 data providers that caused simultaneous tick bursts
- **Performance:** Fixed per-process `/proc/uptime` read on Linux (was reading the full file once per process per tick)
- **Performance:** Reduced per-tick allocations in AnomalyDetectionService, MetricsStore, and RulesEngine
- **Performance:** Reduced UI-thread ViewModel work in Network, Optimization, and Performance view models
- **Memory leak:** `SettingsViewModel` — `LuminanceChanged` event not unsubscribed on dispose
- **Memory leak:** `SettingsService` — debounce timer not stopped on dispose
- **Memory leak:** `FindWindowOverlay` — Process handle opened at 60 Hz without disposal
- **Memory leak:** `AnomalyDetectionService` — `_lastFired` dictionary grew without bound; now pruned on write
- **Theme switching:** 382 `{StaticResource}` → `{DynamicResource}` replacements across all AXAML files; Dark↔Light toggle now applies instantly without restart
- `.deb` package now includes correct 64 × 64 px icon

## [0.1.0] - 2026-03-01

### Added

#### Core Monitoring
- Real-time CPU metrics: overall usage, per-core breakdown, frequency, temperature (Windows)
- Memory metrics: total, used, available, commit, paged pool
- Disk metrics: usage per volume, read/write throughput, queue depth
- Network metrics: bytes sent/received per adapter, active connections
- GPU metrics: utilization, VRAM usage, temperature (Windows/NVIDIA)
- Process list: CPU, memory, disk I/O, network I/O, priority, affinity, status
- Services manager: start, stop, restart, enable, disable
- Startup items: enable/disable startup programs and services
- Network connections viewer: TCP/UDP connections with PID, state, send/recv throughput

#### System Intelligence
- Auto-Balance automatic load balancing: CPU priority management under load
- Gaming Mode: auto-optimize foreground game processes
- Rules Engine: define process rules that trigger on events
- Alerts: configurable threshold-based alerts with OS notifications

#### Persistence & Observability
- Metrics persistence: SQLite database with automatic data retention tiers
  - Raw data: 1 hour
  - 1-minute rollups: 7 days
  - 5-minute rollups: 30 days
  - 1-hour rollups: 1 year
- Historical Viewer: charts and event timeline from the metrics database
- Anomaly Detection: sliding-window statistical engine writing anomaly events
- Prometheus metrics exporter: `/metrics` endpoint on configurable port
- Telegraf configuration generator: in-app setup for Telegraf → InfluxDB/Prometheus
- Grafana integration guide: step-by-step setup built into the app

#### UI & Experience
- Avalonia UI 11.2.3 cross-platform desktop application
- Crystal Glass theme with configurable backdrop blur modes
- 8 accent color presets
- Custom title bar with window controls
- System tray icon with quick-access menu
- Desktop overlay widget for at-a-glance metrics
- Disk Analyzer tab with treemap visualization

#### Packaging & Distribution
- Installable packages: Windows setup EXE, macOS DMG, Linux AppImage and .deb
- Portable archives: ZIP (Windows), tar.gz (macOS, Linux) for all 6 RID targets
  - win-x64, win-arm64
  - osx-x64, osx-arm64
  - linux-x64, linux-arm64
- Automated release workflow via GitHub Actions (triggered on `v*` tags)

#### Platform Support
- **Windows 10/11 (x64, ARM64):** Full feature support. Requires administrator privileges.
- **macOS 12+ (Intel + Apple Silicon):** Full support. Unsigned — see README for Gatekeeper bypass.
- **Linux (x64, ARM64):** Full support. Best tested on Ubuntu 22.04+.

[Unreleased]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.5.2...v0.6.0
[0.5.2]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.8.2...v0.2.0
[0.1.8.2]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.8.1...v0.1.8.2
[0.1.8.1]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.8...v0.1.8.1
[0.1.8]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.7...v0.1.8
[0.1.7]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.6...v0.1.7
[0.1.6]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.5.1...v0.1.6
[0.1.5.1]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.5...v0.1.5.1
[0.1.5]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.3.1...v0.1.4
[0.1.3.1]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.3...v0.1.3.1
[0.1.3]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/joshuadsutcliff/nexus-system-monitor/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/joshuadsutcliff/nexus-system-monitor/releases/tag/v0.1.0
