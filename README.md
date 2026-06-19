# Nexus System Monitor

<p align="center">
  <img src="Nexus System Monitor.png" width="128" alt="Nexus System Monitor">
</p>

<p align="center">
  <a href="https://github.com/brass458/nexus-system-monitor/releases/latest"><img src="https://img.shields.io/github/v/release/brass458/nexus-system-monitor?label=latest" alt="Latest Release"></a>
  <a href="https://github.com/brass458/nexus-system-monitor/releases/latest"><img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue" alt="Platforms"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT License"></a>
  <a href="https://github.com/sponsors/brass458"><img src="https://img.shields.io/badge/Sponsor-%E2%9D%A4-pink?logo=github" alt="Sponsor"></a>
</p>

**One tool. Every platform. Complete system visibility.**

Process Lasso is Windows-only. WizTree is Windows-only. Activity Monitor is macOS-only. htop is terminal-only. Nexus does what all of them do — on all three platforms, with a consistent interface you learn once.

> **Testing on macOS or Linux?** → [TESTING.md](TESTING.md) — step-by-step setup, what to test, and how to report issues.

---

## Screenshots

> Screenshots coming soon. See the [Releases](https://github.com/brass458/nexus-system-monitor/releases) page for download links.

<!-- TODO: Add screenshots of:
  - System Health Dashboard (health score, sparklines, bottleneck detection)
  - Process list (color-coded categories, Impact Score, rules indicators)
  - Crystal Glass theme dark + light
  - Theme customization panel
  - Side-by-side: Windows / macOS / Linux
-->

---

## Philosophy

Every operating system ships its own task manager, and power users inevitably turn to a patchwork of third-party tools — Process Lasso on Windows, Activity Monitor on macOS, htop on Linux, System Informer for deep inspection, and more. Each one has a different interface, different capabilities, and different learning curves.

Nexus System Monitor exists to end that fragmentation.

The goal is a **synonymous user experience across every desktop platform**. The same layout, the same depth of detail, the same workflows — regardless of whether you're sitting in front of a Windows workstation, a MacBook, or a Linux desktop. You learn the tool once, and it works everywhere.

This isn't a lowest-common-denominator approach. Nexus aims for the **union** of features found in the best system tools available today — the real-time metrics of Windows Task Manager, the process control of Process Lasso, the deep inspection of System Informer, and the hardware detail of system profilers — unified under a modern, visually consistent UI inspired by Apple's iOS 26, MacOS, Windows 11, and the freedom that is expected with Linux customization.

---

## Features

### Real-Time Performance Monitoring
- **CPU** — Overall utilization, per-core breakdown, frequency, temperature, cache info
- **Memory** — Physical and paged usage, speed, slot configuration, committed bytes
- **Disk** — Per-drive I/O rates, read/write throughput, queue depth, capacity and usage (NVMe/SSD/HDD detection)
- **Network** — Per-adapter throughput, active NIC detection, IPv4/IPv6 addresses, link speed
- **GPU** — Utilization, dedicated/shared memory, temperature, per-engine breakdown (3D, Copy, Video Decode/Encode)
- Task Manager-style sidebar with device selection and sparkline history charts

### Process Management
- Full process list with real-time CPU, memory, disk I/O, network, and GPU usage per process
- Color-coded process categories (system, service, user app, .NET managed, GPU-accelerated, suspicious, suspended)
- Set process priority (Idle through RealTime) and CPU affinity
- Set I/O priority, memory priority, and efficiency mode
- Kill, suspend, and resume processes
- Kill entire process trees
- Process dumps for debugging
- Module (DLL) enumeration with version info
- Thread enumeration with per-thread CPU times
- Environment variable inspection
- Open file location, search online, copy paths

### System Information
- Detailed hardware inventory — CPU model, cores, cache hierarchy, virtualization support
- Memory modules with speed, type, and slot info
- GPU details and driver versions
- Disk hardware identification
- Network adapter configuration
- Apple-style clean layout for at-a-glance system specs

### Services Manager
- Enumerate all system services with status and startup type
- Start, stop, and restart services
- Navigate directly to a service's host process

### Startup Items
- View and manage programs that launch at startup
- Enable and disable startup entries
- Open file locations and registry keys

### Network Connections
- Active TCP/UDP connections with state badges
- Local and remote endpoint details
- Navigate to the owning process

### Disk Analyzer
- Visual disk space breakdown by folder
- Multi-threaded directory scanning
- Identify space-hogging directories at a glance

### Alerts & Rules Engine
- Define performance alerts with CPU/RAM thresholds
- Create automation rules — automatically set priority, affinity, or terminate processes when conditions are met
- Watchdog monitoring with configurable duration triggers

### Gaming Mode
- One-click optimization profile for gaming sessions
- Suppress background process interference
- Prioritize game processes automatically

### ProBalance
- Automatic load balancing across running processes
- Prevent any single process from monopolizing system resources

### Optimization Recommendations
- Smart analysis of running processes
- Tiered impact ratings (Critical, High, Medium)
- One-click actions to resolve resource hogs

### Desktop Overlay Widget
- Floating always-on-top widget showing CPU, RAM, network, and GPU at a glance
- Draggable, transparent, and minimal (230 x 168 px)
- Toggle on/off from settings

### System Health Dashboard
- Composite health score (0–100) across CPU, Memory, Disk, GPU, and Thermal
- 4 subsystem cards with at-a-glance status and top-5 process consumers
- Bottleneck Detection: identifies the performance-limiting component for Gaming, Streaming, Video Editing, 3D Rendering, and CAD workloads
- Plain-English contextual recommendations

### LAN Scanner
- Nmap-based network scan for hosts, open ports, OS detection, and latency
- Real-time scan progress with host tree and port detail sidebar

### Appearance & Theming
- Crystal Glass theme with opt-in backdrop blur (configurable blur modes)
- 18 built-in theme presets (Nexus Default, Deep Dark, Neon, Dracula, Nord, Dark Sakura, and more)
- Dynamic surface swatch palettes: 8 curated color swatches per UI surface, per preset
- Custom accent and surface colors via color picker with live preview
- Font size multiplier: 0.8–1.5× slider scales all UI text
- Dark / Light / System theme mode (System follows OS preference at runtime)
- Grouped sidebar navigation (Pinned / Monitor / Tools / System) with drag-reorder within groups
- All theme changes apply instantly across the entire UI

---

## Platform Support

| Platform | Status | Detail Level |
|----------|--------|-------------|
| **Windows** | ✅ Full | P/Invoke, PDH counters, WMI, Win32 APIs |
| **macOS** | ✅ Full | sysctl, Mach APIs, ObjC runtime, launchctl, pmset |
| **Linux** | ✅ Full | procfs, sysfs, multi-init (systemd/SysVinit/OpenRC) |

All tabs show real data on all three platforms. Windows has the deepest detail level (WMI hardware info, PDH counters, GPU engines). macOS and Linux provide the same interface and equivalent data through native APIs.

**Platform-specific notes:**
- **System Info tab** shows full WMI hardware inventory on Windows; hostname, OS, architecture, uptime, and RAM on macOS/Linux
- **Gaming Mode** power plan switching on macOS may require `sudo` (pmset restriction). Process throttling works without elevation.
- **ProBalance** on Linux under Wayland without a compositor that supports `xdotool` will treat all background processes equally (no foreground window detection). Fully functional on X11 and macOS.

---

## Quick Start

**No .NET SDK required** — just download and run.

1. Go to [**Releases**](https://github.com/brass458/nexus-system-monitor/releases/latest)
2. Download the build for your platform (see table below)
3. Unzip / mount / install and run

| Platform | Download |
|----------|----------|
| **Windows x64** | `NexusMonitor-Windows-Installer-*.exe` (installer) or `NexusMonitor-Windows-Portable-*.zip` (portable) |
| **Windows ARM64** | `NexusMonitor-Windows-ARM-Installer-*.exe` |
| **macOS Apple Silicon** | `NexusMonitor-MacOS-*.dmg` |
| **macOS Intel** | `NexusMonitor-MacOS-Intel-*.dmg` |
| **Linux x64** | `NexusMonitor-Linux-*.AppImage` or `NexusMonitor-Linux-*.deb` |

**Platform notes:**
- **Windows:** SmartScreen may warn on first launch (the app is not yet code-signed). Click "More info" → "Run anyway."
- **macOS:** Gatekeeper will block an unsigned binary. Right-click → **Open** to bypass, or run the command below. **macOS Tahoe (macOS 26) users:** the standard quarantine removal is not sufficient — use `xattr -cr` instead (see note in Pre-Built Releases below).
- **Linux AppImage:** Requires FUSE. On Ubuntu 22.04+: `sudo apt install libfuse2`. Or run with `--appimage-extract-and-run`.
- **Elevated access:** Some features (IO priority, certain service operations) need admin/root. On Windows, run as Administrator. On macOS/Linux, launch with `sudo` if specific features don't work.

---

## Running Nexus System Monitor

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- **macOS:** macOS 12 Monterey or later (Intel or Apple Silicon)
- **Linux:** Any modern distribution — systemd, SysVinit (Fedora, Debian, etc.), or OpenRC (Gentoo, Alpine) are all supported. X11 recommended for ProBalance; Wayland works with reduced foreground-window detection.

### From Source

```bash
git clone https://github.com/brass458/nexus-system-monitor.git
cd nexus-system-monitor
dotnet build NexusMonitor.sln
```

#### Windows

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0-windows10.0.17763.0
```

#### macOS

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj
```

> **Gatekeeper:** If macOS blocks an unsigned binary, right-click → Open, or run:
> ```bash
> xattr -d com.apple.quarantine path/to/NexusMonitor
> ```
> **macOS Tahoe (macOS 26):** The quarantine flag alone is not enough — Gatekeeper applies additional security checks on Tahoe that block unlaunched unsigned apps even after quarantine removal. Clear all extended attributes recursively instead:
> ```bash
> xattr -cr path/to/NexusMonitor.app
> ```

> **Gaming Mode / power plans:** Switching power profiles uses `pmset`, which may require `sudo` on some machines. Process throttling (ProBalance, Gaming Mode process priority) works without elevation.

#### Linux

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0
```

The app auto-detects your init system at startup:
- **systemd** — uses `systemctl` (most distros)
- **SysVinit** — uses `/etc/init.d/` scripts and `service` (pre-systemd Fedora, Debian, etc.)
- **OpenRC** — uses `rc-status` and `rc-service` (Gentoo, Alpine, etc.)

> **Power plans:** Install `power-profiles-daemon` for Gaming Mode power plan switching (`powerprofilesctl`). Falls back to `/sys/devices/system/cpu/*/cpufreq/scaling_governor` if unavailable.

### Self-Contained Builds (for distribution)

Produce a portable folder that includes the .NET runtime — no SDK required on the target machine.

#### macOS (Apple Silicon)

```bash
dotnet publish src/NexusMonitor.UI /p:PublishProfile=osx-arm64
# Output: src/NexusMonitor.UI/publish/osx-arm64/
```

#### macOS (Intel)

```bash
dotnet publish src/NexusMonitor.UI /p:PublishProfile=osx-x64
# Output: src/NexusMonitor.UI/publish/osx-x64/
```

#### Linux x64

```bash
dotnet publish src/NexusMonitor.UI /p:PublishProfile=linux-x64
# Output: src/NexusMonitor.UI/publish/linux-x64/
# Distribute as a tarball:
tar -czf NexusMonitor-linux-x64.tar.gz -C src/NexusMonitor.UI/publish/linux-x64 .
```

#### Linux ARM64

```bash
dotnet publish src/NexusMonitor.UI /p:PublishProfile=linux-arm64
# Output: src/NexusMonitor.UI/publish/linux-arm64/
```

> **Cross-compiling from Windows:** All publish profiles work from a Windows host. The LINUX preprocessor define is set automatically by the publish profiles, so Linux-specific providers compile correctly even when building on Windows.

### Pre-Built Releases

Download the latest release for your platform from the [**Releases**](https://github.com/brass458/nexus-system-monitor/releases) page:

| Platform | Installer / Package | Portable |
|----------|---------------------|----------|
| Windows x64 | `NexusMonitor-Windows-Installer-*.exe` | `NexusMonitor-Windows-Portable-*.zip` |
| Windows ARM64 | `NexusMonitor-Windows-ARM-Installer-*.exe` | `NexusMonitor-Windows-ARM-Portable-*.zip` |
| macOS Apple Silicon | `NexusMonitor-MacOS-*.dmg` | `NexusMonitor-MacOS-Portable-*.tar.gz` |
| macOS Intel | `NexusMonitor-MacOS-Intel-*.dmg` | `NexusMonitor-MacOS-Intel-Portable-*.tar.gz` |
| Linux x64 | `NexusMonitor-Linux-*.AppImage` / `NexusMonitor-Linux-*.deb` | `NexusMonitor-Linux-Portable-*.tar.gz` |
| Linux ARM64 | — | `NexusMonitor-Linux-ARM-Portable-*.tar.gz` |

> **macOS note:** The app is unsigned. On first launch, right-click → **Open** to bypass Gatekeeper, or run:
> ```bash
> xattr -d com.apple.quarantine NexusMonitor.app
> ```
> **macOS Tahoe (macOS 26) — additional step required:** Gatekeeper on Tahoe enforces stricter checks that quarantine removal alone does not satisfy. After dragging the app from the DMG to `/Applications`, run:
> ```bash
> xattr -cr /Applications/NexusMonitor.app
> ```
> This clears all extended attributes recursively from the app bundle. The app will then launch normally.
>
> **Linux note:** AppImages require FUSE. If your distribution ships FUSE3 (Ubuntu 22.04+), install FUSE2:
> ```bash
> sudo apt install libfuse2
> ```
> Alternatively, run any AppImage without FUSE via `--appimage-extract-and-run`.

---

## NexusCLI — Terminal Interface

NexusCLI (`nexus`) is a headless companion to the GUI. It exposes the same monitoring engine as a terminal tool — useful for remote servers, SSH sessions, scripting, CI pipelines, or any situation where a graphical desktop isn't available or wanted.

| | NexusCLI | GUI |
|---|---|---|
| Graphical window | ✗ | ✓ |
| Real-time dashboard | ✓ (terminal) | ✓ |
| Process list & control | ✓ | ✓ |
| Services management | ✓ | ✓ |
| System health score | ✓ | ✓ |
| Alerts engine | ✓ | ✓ |
| Rules engine | ✓ | ✓ |
| Prometheus endpoint | ✓ | ✓ |
| CSV / JSON export | ✓ | — |
| Theming, overlays, LAN scanner | — | ✓ |
| SSH / headless server use | ✓ | — |

### Download

Go to [**Releases**](https://github.com/brass458/nexus-system-monitor/releases/latest) and grab the CLI archive for your platform:

| Platform | Archive |
|----------|---------|
| Windows x64 | `NexusCLI-Windows-*.zip` |
| Windows ARM64 | `NexusCLI-Windows-ARM-*.zip` |
| macOS Apple Silicon | `NexusCLI-MacOS-*.tar.gz` |
| macOS Intel | `NexusCLI-MacOS-Intel-*.tar.gz` |
| Linux x64 | `NexusCLI-Linux-*.tar.gz` |
| Linux ARM64 | `NexusCLI-Linux-ARM-*.tar.gz` |

Each archive is self-contained — no .NET installation required on the target machine.

### Launching the CLI

#### Windows

1. Extract `NexusCLI-Windows-*.zip` to a folder (e.g. `C:\Tools\nexus\`).
2. Open **PowerShell** or **Command Prompt** and navigate to that folder:

```powershell
cd C:\Tools\nexus
.\nexus.exe --help
```

To use `nexus` from any directory, add the folder to your PATH:

```powershell
$env:PATH += ";C:\Tools\nexus"
# Or permanently via System Properties → Environment Variables
```

> **SmartScreen:** Windows may warn on first run. Click **More info → Run anyway**. The binary is unsigned.

#### macOS

1. Extract the archive:

```bash
mkdir nexus && tar -xzf NexusCLI-MacOS-*.tar.gz -C nexus && cd nexus
```

2. Clear the Gatekeeper quarantine attribute (required for unsigned binaries downloaded from the internet):

```bash
xattr -d com.apple.quarantine nexus
```

3. Run:

```bash
./nexus --help
```

To make `nexus` available system-wide:

```bash
sudo mv nexus /usr/local/bin/nexus
nexus --help
```

> **Apple Silicon vs Intel:** Download `NexusCLI-MacOS-*.tar.gz` for M1/M2/M3/M4 Macs and `NexusCLI-MacOS-Intel-*.tar.gz` for Intel Macs. Running the wrong architecture will produce an error on launch.

#### Linux

1. Extract the archive:

```bash
mkdir nexus && tar -xzf NexusCLI-Linux-*.tar.gz -C nexus && cd nexus
```

2. Ensure the binary is executable (it should be already, but confirm):

```bash
chmod +x nexus
```

3. Run:

```bash
./nexus --help
```

To install system-wide:

```bash
sudo mv nexus /usr/local/bin/nexus
nexus --help
```

> **ARM64:** Use `NexusCLI-Linux-ARM-*.tar.gz` on Raspberry Pi 4/5, AWS Graviton, or any other 64-bit ARM board. The x64 build will not run on ARM.

> **Elevated access:** Some operations (service control, process priority changes) require root. Prefix with `sudo` when needed, e.g. `sudo nexus services --interactive`.

### Commands

```
nexus <command> [options]
```

| Command | Alias | Description |
|---------|-------|-------------|
| `dashboard` | — | Live-refreshing system dashboard (CPU, memory, disk, GPU, health, top processes). Press **Q** to exit. |
| `processes` | `ps` | List processes. Supports `--sort cpu\|mem\|name`, `--filter <name>`, `--top <n>`, `--live` (auto-refresh), `--interactive` (pick and act). |
| `services` | `svc` | List system services. Supports `--filter`, `--running`, `--stopped`, `--interactive`. |
| `health` | — | One-shot system health snapshot — composite score, subsystem breakdown, top CPU consumers, bottleneck. |
| `alerts list` | — | Show configured alert rules. |
| `alerts status` | — | Show whether the alerts engine is running. |
| `alerts watch` | — | Stream live alert events to the terminal. Press **Ctrl+C** to stop. |
| `rules list` | — | Show configured process rules. |
| `rules status` | — | Show rules engine status and rule count. |
| `settings show` | — | Dump all current application settings. |
| `settings get <key>` | — | Read a single setting value by property name. |
| `settings set <key> <value>` | — | Write a setting value and save. |
| `prometheus` | `prom` | Start a Prometheus `/metrics` HTTP endpoint. Supports `--port <n>`. Press **Ctrl+C** to stop. |
| `export` | — | Export historical metrics to CSV or JSON. Supports `--format csv\|json`, `--last <Nh>` (e.g. `24h`), `--output <file>`. |

**Examples:**

```bash
# Live dashboard
nexus dashboard

# Top 10 processes by memory
nexus ps --sort mem --top 10

# Filter running services and pick one interactively
nexus svc --running --interactive

# System health check (great for scripts / CI)
nexus health

# Export the last 24 hours of metrics to a file
nexus export --format json --last 24h --output metrics.json

# Prometheus endpoint on a custom port
nexus prometheus --port 9091

# Watch live alerts (leave running in a side terminal)
nexus alerts watch
```

---

## Performance

Nexus is designed to be a monitoring tool, not a monitoring problem. Measured on a mid-range Linux desktop (AMD Ryzen 5, 16 GB RAM):

| Metric | Idle | Active polling |
|--------|------|---------------|
| CPU usage | ~1–3% | 3–8% |
| RAM footprint | ~100 MB | 100–150 MB |
| Disk I/O | Negligible | Negligible |

**How it stays lean:**
- Metrics are polled on configurable intervals (default 1 s), not continuously
- SQLite WAL mode with tiered retention — hot metrics in memory, historical data batched to disk
- Platform providers cache hardware-invariant data (CPU model, memory slots) at startup
- UI rendering only updates changed values; sparklines use a ring buffer, not unbounded growth
- Semaphore-guarded tick loops prevent re-entrant polling spikes under slow API calls

> Baseline CPU was ~25% before the optimization pass (Phase 17). The current 3–8% figure reflects GC tuning, caching, and Rx subscription cleanup.

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Language | C# 12 / .NET 8 |
| UI Framework | [Avalonia UI](https://avaloniaui.net/) 11.2.3 |
| MVVM | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.3.2 |
| Reactive | [ReactiveUI](https://www.reactiveui.net/) + System.Reactive 6.0.1 |
| Charts | [LiveChartsCore](https://livecharts.dev/) 2.0.0-rc4 (SkiaSharp) |
| Graphics | SkiaSharp 2.88.9 |
| DI | Microsoft.Extensions.DependencyInjection 8.0.1 |

---

## Architecture

```
NexusMonitor.sln
├── src/
│   ├── NexusMonitor.Core/              # Abstractions, models, services (net8.0)
│   ├── NexusMonitor.Hosting/           # DI wiring shared by GUI and CLI
│   ├── NexusMonitor.UI/                # Avalonia desktop app (platform-conditional)
│   ├── NexusMonitor.CLI/               # NexusCLI terminal app (Spectre.Console)
│   ├── NexusMonitor.DiskAnalyzer/      # Disk analysis engine
│   ├── NexusMonitor.Platform.Windows/  # Windows API implementations
│   ├── NexusMonitor.Platform.MacOS/    # macOS implementations
│   └── NexusMonitor.Platform.Linux/    # Linux implementations
└── tests/
    └── NexusMonitor.Core.Tests/        # Unit tests
```

All UI and business logic lives in the platform-agnostic `Core` and `UI` projects. Platform-specific code is isolated behind five core interfaces:

- **ISystemMetricsProvider** — CPU, memory, disk, network, GPU metrics
- **IProcessProvider** — Process enumeration and control
- **INetworkConnectionsProvider** — TCP/UDP connection enumeration
- **IServicesProvider** — System service management
- **IStartupProvider** — Startup item management

Adding support for a new platform means implementing these interfaces against native APIs — the rest of the application works unchanged.

---

## Testing

Early-access testing on macOS and Linux is open. See **[TESTING.md](TESTING.md)** for a step-by-step setup guide, a per-tab test checklist, known limitations, and instructions for filing issues.

Report bugs and feedback at: https://github.com/brass458/nexus-system-monitor/issues

For detailed project documentation — feature inventory, architecture, gap analysis, and roadmap — see [`docs/`](docs/index.md).

---

## Sponsorship

Nexus System Monitor is free and open source. If you find it useful, consider sponsoring development:

| Tier | Monthly | Perks |
|------|---------|-------|
| Supporter | $3/mo | Name in the README supporters list |
| Backer | $10/mo | Name + priority responses on GitHub issues |
| Champion | $25/mo | Above + vote on the feature roadmap |

**[Sponsor on GitHub →](https://github.com/sponsors/brass458)**

---

## License

[MIT](LICENSE) — Copyright 2026 TheBlackSwordsman
