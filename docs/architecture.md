---
type: note
date: 2026-07-04
project: NexusSystemMonitor
tags: [nexus, architecture, patterns]
---

# Architecture

Current architecture of Nexus System Monitor as of **v0.5.2 (2026-07-04)**.

---

## Solution Structure

9 projects total — 8 under `src/` plus one standalone tool:

```
NexusMonitor.sln
├── Directory.Build.props          # Version source of truth: <Version>0.5.2</Version>
├── src/
│   ├── NexusMonitor.Core/              # Abstractions, models, services (net8.0 — fully portable)
│   ├── NexusMonitor.Hosting/           # Shared DI/service bootstrap consumed by UI + CLI (platform-conditional TFM)
│   ├── NexusMonitor.UI/                # Avalonia desktop app (platform-conditional TFM)
│   ├── NexusMonitor.CLI/               # Headless CLI front-end (platform-conditional TFM)
│   ├── NexusMonitor.DiskAnalyzer/      # Disk analysis engine (treemap, scanning) (net8.0)
│   ├── NexusMonitor.Platform.Windows/  # Windows API implementations (net8.0-windows10.0.17763.0 on Windows, net8.0 stub elsewhere)
│   ├── NexusMonitor.Platform.MacOS/    # macOS implementations (net8.0, unconditional — see Platform layers below)
│   └── NexusMonitor.Platform.Linux/    # Linux implementations (net8.0, unconditional)
├── tools/
│   └── IconGenerator/              # SkiaSharp-based icon generation CLI (net8.0, all 3 platforms)
└── tests/
    └── NexusMonitor.Core.Tests/    # Unit tests (mock provider tests)
```

**Assembly name:** `NexusMonitor` (not `NexusMonitor.UI`)
All `avares://` URIs: `avares://NexusMonitor/Assets/...`

---

## Platform Layers

Platform selection happens **at build time**, not at runtime, via MSBuild OS conditionals — there is no single cross-compiled binary that dynamically loads a platform plugin.

- **Windows** (`Platform.Windows.csproj`): `TargetFramework` is conditional —
  `net8.0-windows10.0.17763.0` when `$([MSBuild]::IsOSPlatform('windows'))`, else plain `net8.0`.
  On non-Windows build hosts, all real implementation files are excluded
  (`<Compile Remove="**/*.cs" />`) and only an empty `Placeholder.cs` compiles, so
  `dotnet build NexusMonitor.sln` succeeds on any CI runner without pulling in
  Windows-only APIs it can't target.
- **Linux** (`Platform.Linux.csproj`): unconditional plain `net8.0` — no native TFM exists for
  Linux, so there's nothing to switch on; all functionality is P/Invoke into libc / `/proc`
  /`/sys` reads and process spawning, all of which cross-compile cleanly.
- **macOS** (`Platform.MacOS.csproj`): also unconditional plain `net8.0`, **deliberately avoiding
  the `net8.0-macos` TFM.** The project has no managed ObjC bindings — everything is pure
  P/Invoke against `libSystem`/`sysctl`/Mach APIs — so there's no reason to pull in the
  `Microsoft.macOS` workload TFM, whose ObjC runtime SIGBUSes at startup on macOS 26 (Tahoe) /
  Apple Silicon. Same rationale as Linux: plain `net8.0` builds and runs everywhere without a
  workload-specific runtime in the way.
- **UI / CLI / Hosting**: all three follow the same pattern as `Platform.Windows` — conditional
  `net8.0-windows10.0.17763.0` vs `net8.0` — because they reference `Platform.Windows` directly
  and need matching TFM compatibility rules (`IsTargetFrameworkCompatible`) to consume it.

---

## Core Interfaces

Five platform-agnostic interfaces isolate all platform-specific code:

| Interface | Responsibility |
|-----------|----------------|
| `ISystemMetricsProvider` | CPU, memory, disk, network, GPU metrics |
| `IProcessProvider` | Process enumeration and control |
| `INetworkConnectionsProvider` | TCP/UDP connection enumeration + EStats |
| `IServicesProvider` | System service management |
| `IStartupProvider` | Startup item management |

Adding a new platform means implementing these 5 interfaces. The rest of the application works unchanged.

---

## Platform Provider Strategy

Each platform project implements the 5 interfaces using native APIs:

**Windows** (`Platform.Windows/`):
- Process metrics: `TotalProcessorTime` delta + `GetProcessIoCounters` (P/Invoke)
- System metrics: PDH counters (`_diskCounters`, `_netCounters`, `_gpuCopyCounters`, etc.)
- Parent PID: `NtQueryInformationProcess`
- Elevation: `GetTokenInformation`
- Services: `EnumServicesStatusExW`
- Modules/Threads: ToolHelp32 (`CreateToolhelp32Snapshot`)
- GPU engines: PDH engine-type counter init via `InitEngineCounters`
- EStats (per-connection throughput): `GetPerTcpConnectionEStats` — errors on TSO/RSC NICs → auto-hides

**macOS** (`Platform.MacOS/`):
- Per-core CPU: `host_processor_info(host, PROCESSOR_CPU_LOAD_INFO=2)` — flat `uint[cpuCount*4]` (user/sys/idle/nice per core); free with `vm_deallocate`
- Disk I/O: `ioreg -c IOBlockStorageDriver -r -k Statistics` → `"Bytes (Read)"/"Bytes (Write)"` per driver
- Foreground window: ObjC P/Invoke into `libobjc.A.dylib` — separate `objc_msgSend_int` signature for `int` returns
- Power plans: `pmset -a lowpowermode` for Power Saver/Balanced/High Performance

**Linux** (`Platform.Linux/`):
- Network PIDs: scan `/proc/[pid]/fd/` symlinks for `socket:[inode]`, cache 2s; column 10 in `/proc/net/tcp` is the inode
- Temperature: scans `/sys/class/hwmon` for `coretemp`/`k10temp`/`zenpower` then `thermal_zone*`
- Init system: detect via `/proc/1/comm` → `/run/systemd/system` → `/run/openrc/softlevel` → `/etc/init.d` fallback
- Init backends: `SystemdBackend`, `SysVinitBackend`, `OpenRcBackend`, `DinitBackend`, `RunitBackend`, `S6Backend`
- Power plans: `powerprofilesctl` first, fallback to `/sys/.../scaling_governor`

**Platform conditional compilation:**
```csharp
#if WINDOWS
    // Windows-only code
#elif MACOS
    // macOS-only code
#elif LINUX
    // Linux-only code
#endif
```
`LINUX` define must appear in both `.csproj` AND all `linux-*.pubxml` publish profiles.

---

## Dependency Injection

All ViewModels registered as `AddSingleton` in `App.axaml.cs`. The DI container auto-disposes `IDisposable` singletons on shutdown — `(Services as IDisposable)?.Dispose()` calls all registered disposables. No manual wiring needed.

`OverlayWindow` is created in `OnFrameworkInitializationCompleted` and wired to `SettingsViewModel.OverlayWindow`.

---

## MVVM + ReactiveUI Patterns

```csharp
// Providers emit observable streams; always marshal to UI thread
_processProvider
  .GetProcessStream(TimeSpan.FromSeconds(2))
  .ObserveOn(RxApp.MainThreadScheduler)    // required
  .Subscribe(processes => UpdateUI(processes));
// Do NOT add inner Dispatcher.UIThread.Post — redundant inside ObserveOn
```

Cross-tab navigation uses `WeakReferenceMessenger`:
```csharp
// Send from ServicesViewModel
WeakReferenceMessenger.Default.Send(new NavigateToProcessMessage(pid));

// Receive in MainViewModel
WeakReferenceMessenger.Default.Register<NavigateToProcessMessage>(this, (r, m) => SwitchToProcessTab(m.Pid));
```

Commands follow CommunityToolkit.Mvvm pattern:
```csharp
[RelayCommand]
private async Task KillProcess() =>
    await _processProvider.KillProcessAsync(SelectedProcess.Pid);
```

---

## Theming System

**Rule:** `{DynamicResource}` for any brush that must update when the theme changes. `{StaticResource}` only for truly static values (corner radii, accent colors that don't change with theme).

382 `StaticResource` → `DynamicResource` replacements were made in v0.1.1 across all 21 AXAML files.

Theme structure in `App.axaml`:
```xml
<ResourceDictionary>
    <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Dark">  <!-- dark brushes -->
        <ResourceDictionary x:Key="Light"> <!-- light brushes -->
    </ResourceDictionary.ThemeDictionaries>
    <!-- Theme-independent resources (accent colors) -->
</ResourceDictionary>
```

Runtime color changes propagate instantly:
```csharp
Application.Current.Resources["AccentBlueBrush"] = new SolidColorBrush(color);
```

18 theme presets + custom. `SurfaceSwatchPalettes.GetPalette(presetId, isDark)` returns `SwatchColor[8]` per surface. Swatch `Background="{Binding Hex}"` uses Avalonia's string→Brush type converter.

**Fonts:**
- `FluentSystemIcons-Regular.ttf` at `Assets/Fonts/` — registered as `NexusIcons` FontFamily in `Typography.axaml`
- Icon codepoints from `FluentSystemIcons-Regular.json` (key=name, value=decimal codepoint)

---

## Storage Layer

`NexusMonitor.Core/Storage/MetricsStore.cs`:
- SQLite WAL mode via `Microsoft.Data.Sqlite`
- Tiered retention: raw 1h → 1m rollups 7d → 5m rollups 30d → 1h rollups 1yr
- Top-15 process snapshots per tick (by CPU + memory, deduped)
- Batch writes: buffer 30 ticks, flush in single transaction
- `resource_events` table for anomaly/bottleneck events

Estimated steady-state DB size: ~50–100 MB.

---

## Observability Pipeline

```
MetricsStore (SQLite) → HistoricalViewer (in-app)
                      → PrometheusExporter (/metrics endpoint)
                      → Telegraf config generator (Settings UI)
                      → Grafana dashboard (in-app guide)
AnomalyDetectionService → resource_events table → History tab incidents
EventMonitorService     → resource_events table (threshold crossings)
```

---

## Avalonia-Specific Gotchas

| Problem | Solution |
|---------|----------|
| `x:Name` on `DataGridTextColumn` does not generate code-behind field | Iterate `DataGrid.Columns` by header string; put `x:Name` on the `DataGrid` element |
| `col.Sort()` posts async via `Dispatcher.UIThread.Post` | Reset sort guard also via `Post` (FIFO) — not in `finally` |
| `DataGridTextColumn` without explicit `SortMemberPath` returns null in sort handler | Always set `SortMemberPath` explicitly |
| `RelativeSource AncestorType=UserControl` in nested DataTemplate | Requires `x:CompileBindings="False"` |
| `{StaticResource}` does not update on theme change | Must use `{DynamicResource}` for any brush in `ThemeDictionaries` |
| Inline hex colors: only 3, 4, 6, or 8 hex digits valid | Never use 10-digit strings like `#CCE0FFFFFF` — Avalonia crashes with `FormatException` |
| `TransparencyLevelHint` set in XAML causes type conversion error | Set in code-behind only |

---

## Key File Locations

| Area | Path |
|------|------|
| Core models | `src/NexusMonitor.Core/Models/` |
| Core abstractions | `src/NexusMonitor.Core/Abstractions/` |
| Core services | `src/NexusMonitor.Core/Services/` (RulesEngine, SettingsService, AnomalyDetection) |
| Core storage | `src/NexusMonitor.Core/Storage/` (MetricsStore, EventRepository) |
| Core themes | `src/NexusMonitor.Core/Themes/` (SurfaceSwatchPalettes) |
| Core network | `src/NexusMonitor.Core/Network/` (NmapScannerService, NmapXmlParser) |
| Windows native | `src/NexusMonitor.Platform.Windows/Native/` (Kernel32, NtDll, PsApi, AdvApi32) |
| UI ViewModels | `src/NexusMonitor.UI/ViewModels/` |
| UI Views | `src/NexusMonitor.UI/Views/` |
| UI Resources | `src/NexusMonitor.UI/Assets/` + `Styles/` |
| DI setup | `src/NexusMonitor.UI/App.axaml.cs` |
| Version | `Directory.Build.props` |
| CI workflow | `.github/workflows/release.yml` |
| Windows installer | `installer/windows/NexusMonitor.iss` |
