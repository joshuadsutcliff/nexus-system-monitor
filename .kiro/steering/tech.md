# Tech — Nexus System Monitor

## Stack

- **.NET 8**, **Avalonia** (XAML UI), **ReactiveUI** (MVVM). SQLite for metrics history.
- Solution layout: `src/NexusMonitor.Core` (services, models, automation), `src/NexusMonitor.Platform.{Windows,MacOS,Linux}` (per-OS providers behind shared interfaces like `IProcessProvider`, `ISystemMetricsProvider`), `src/NexusMonitor.UI` (Avalonia app), CLI project, `tests/` (xUnit — 850+ executed cases, CI green on all three OSes).
- Platform interop is direct P/Invoke — NtQueryInformationProcess on Windows; AppleSMC/IOKit/libproc on macOS; procfs/sysfs on Linux. No third-party monitoring libraries.

## Build constraints

- GUI builds/runs are verified per-OS; Windows validation happens on a separate Windows machine, macOS on Apple silicon, Linux on CachyOS.
- Six publish profiles (win/macOS arm+x64/linux x64+arm64); macOS .app bundling scripts live in `installer/macos/`.

## Conventions that matter for specs

- Platform capabilities differ; features must degrade **honestly** (explicit unavailable states), never fabricate values.
- Legacy feature names (ProBalance/IdleSaver/SmartTrim) are banned outside the settings-migration shim — current names are Auto-Balance, Idle Throttle, Memory Reclaim.
- New behavior needs tests; regression suite runs cross-OS in CI.
