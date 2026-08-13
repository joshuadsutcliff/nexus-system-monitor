# Nexus System Monitor

Cross-platform (Windows/macOS/Linux) system monitor and process manager, built as an honest, automation-first alternative to Process Lasso, with Prometheus/Grafana export.

## Stack
- C# 12 / .NET 8
- Avalonia UI 11.2.3 (cross-platform GUI)
- Rx.NET, LiveChartsCore (Skia), Spectre.Console CLI

## Key paths
- `src/NexusMonitor.Core/`: platform-agnostic engine (sensors, services, settings)
- `installer/macos/`: .app bundle scaffolding (Info.plist, entitlements)
- `~/Github/nexus-system-monitor` (sigmamini clone, this repo)
- `~/Code/nexus-system-monitor` (CachyOS clone, holds unpushed `feature/composable-dashboard`, parked)

## Current state
v0.7.0 shipped 2026-07-11; all three 1.0-gate features (tooltips, orientation overlay, batch actions) plus Snapshot & Compare shipped. Windows, Linux, and macOS GUI live passes all COMPLETE. Open to 1.0: **#38** duplicate-instance-on-relaunch (blocker), **#42** crash-log written on every Linux exit, **Sym-3** (service writes) not started, **PR #44** (Arch/CachyOS SDK Split-compat fix) awaiting CI x3 + Josh's merge word. Full detail: vault ledger `Nexus-Status-Ledger.md`.

## Agents working this repo
- Claude Code (Fable 5): primary
- Sonnet/Opus workers: platform live passes (UIA, xdotool)

## Gotchas
- Repo lives ONLY under `github.com/joshuadsutcliff/nexus-system-monitor` (transferred from brass458 2026-07-03). brass458 must NEVER touch this repo, for any reason. Every gh op and https push: switch to the joshuadsutcliff account first, operate, switch back.
- Bitsum-era names (ProBalance/IdleSaver/SmartTrim) are banned in new code; legacy literals only in the `SettingsService` shim.
- macOS code signing deferred on finances (Apple dev account ready, payment pending).
