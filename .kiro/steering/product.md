# Product — Nexus System Monitor

## What it is

Cross-platform desktop system monitor (Windows / macOS / Linux) built on .NET 8 + Avalonia + ReactiveUI. Tagline: **"One tool. Every platform. Complete system visibility."** It replaces the patchwork of single-OS tools (process tuner here, disk analyzer there, terminal monitor that can't click) with one app and one interface learned once.

Solo-developed by Josh Sutcliff. MIT-licensed, distributed via GitHub Releases. Currently **v0.7.0**, driving toward a 1.0 public launch (HN/Reddit).

## Feature set (shipped)

- **Real-time monitoring:** CPU/GPU/memory/disk/network with temperatures — including Apple-silicon CPU/GPU temps via AppleSMC/IOHID (a capability most cross-platform monitors lack).
- **Process management:** per-user visibility, live impact metrics, kill/tree-kill, priority control, efficiency mode.
- **Automation:** rules engine + named services — Auto-Balance (priority rebalancing), Idle Throttle, Memory Reclaim, Foreground Boost, CPU limiter, instance balancer, quiet hours, sleep prevention.
- **System surfaces:** services manager, startup items, network connections, LAN scanner, disk analyzer, system info.
- **Dashboard:** fully customizable (drag/resize widgets, gallery, pop-out windows), System Health dashboard, desktop overlay widget, gaming mode, optimization recommendations, alerts.
- **Theming:** Crystal Glass theme system (dark/light), theme customization panel, presets; quiet first-launch defaults with an accessibility runtime clamp.
- **NexusCLI:** terminal interface shipped alongside the GUI.
- **History:** metrics rollups (1m/5m/1h), health trends.

## Product principles

- **Honesty over spectacle:** metrics that can't be measured are shown as unavailable — never fabricated (no fake VRAM totals, no garbage temps). This is a hard rule with public claims audited against it.
- **Conservative automation:** never touch what you can't restore (e.g., process priorities are only changed when the original can be read back).
- **Quiet by default:** first launch is visually calm; showcase effects are opt-in.

## Current gaps on the road to 1.0

Sym-3 (service-write symmetry) and Sym-4 (cross-OS regression matrix), screenshot set, v0.6.0/v0.7.0 release narratives, macOS .app bundle polish, and two defer-or-build decisions: Snapshot & Compare, Remote Viewer.

## Open product questions (good targets for spec work)

- Who is the 1.0 user? (power users? gamers? sysadmins? cross-platform developers?) Positioning and launch copy depend on it.
- Which of Snapshot & Compare / Remote Viewer earns a place before 1.0, if either?
- What does the free/paid or donation story look like post-launch, if any?
