---
type: note
date: 2026-03-05
project: NexusSystemMonitor
tags: [nexus, index, hub]
---

# Nexus System Monitor — Project Hub

**Repository:** `github.com/joshuadsutcliff/nexus-system-monitor`
**Current version:** v0.1.8.2
**Tech stack:** C# 12 / .NET 8 / Avalonia UI 11.2.3 / MVVM + ReactiveUI
**Status:** Phases 1–19 complete · v0.1.8.2 released · **v1.0 roadmap approved 2026-03-24**

---

## Design Philosophy

> [!important] The Apple Principle
> **If it doesn't immediately make sense to the user AND work, we don't ship it.**
>
> Every feature must be:
> - **Intuitive on first interaction** — no manual or tooltip required to understand what a control does
> - **Polished to completion** — no rough edges, no placeholder UI, no "good enough" layouts
> - **Actually functional** — not hidden behind a flag, not half-baked, not broken on any supported platform
>
> This is the bar Apple sets for its own tools. We hold Nexus to the same standard.

The goal is the **union** of features found in the best system tools — Process Lasso's process control, System Informer's deep inspection, WizTree's disk analysis — unified under a modern UI with Apple-quality polish and full cross-platform parity.

---

## Documentation Index

### Active Documents
| Document | Purpose |
|----------|---------|
| [[v1.0-roadmap]] | **Authoritative plan — v0.2.0 through v1.0.0 (approved 2026-03-24)** |
| [[feature-inventory]] | Every implemented feature by category with version introduced |
| [[release-history]] | Phase-to-release narrative, key decisions, session log links |
| [[architecture]] | Solution structure, key patterns, platform strategy |
| [[quick-reference]] | Build commands, paths, common tasks, gotchas |

### Reference Only (Deprecated)
> These documents are retained for historical context. They describe completed work or superseded plans. Do not use them to determine what work remains.

| Document | Why Deprecated |
|----------|---------------|
| [[roadmap]] | Superseded by [[v1.0-roadmap]] — reflects planning through v0.1.7 only |
| [[gap-analysis]] | Last updated v0.1.7; several "missing" items are now implemented; superseded by [[v1.0-roadmap]] |
| [[1.0-release-readiness]] | Written 2026-03-07 at v0.1.7; scorecard and tier classifications are outdated |
| [[plans/2026-02-26-phase3-polish-and-settings\|Phase 3 Plan]] | Fully executed; pre-v0.1.0 historical reference |
| [[plans/phase-11-metrics-persistence\|Phase 11 Plan]] | Fully executed; MetricsStore architecture reference |
| [[creation-history/README\|Creation History Archive]] | Phase 1–6 original design docs (pre-v0.1.0) |

---

## Quick Links

- **Source:** `Areas/Projects/NexusSystemMonitor/`
- **CHANGELOG:** [[../CHANGELOG|CHANGELOG]]
- **Session logs:** `CC-Session-Logs/` (12 sessions, Phases 7–16)
- **GitHub:** `github.com/joshuadsutcliff/nexus-system-monitor`

---

## Current Status

| Area | Status |
|------|--------|
| Phases 1–19 | ✅ Complete |
| Windows support | ✅ Full (P/Invoke, PDH, WMI) |
| macOS support | ✅ Full (sysctl, Mach, ObjC runtime) |
| Linux support | ✅ Full (procfs, sysfs, multi-init) |
| CI/CD releases | ✅ 12 artifacts across 6 RIDs |
| Structured logging | ✅ Serilog (Phase 19) |
| Latest release | v0.1.8.2 — CLI Spectre markup crash fix |
| Next focus | [[v1.0-roadmap]] — Phase 20: Test Suite → v0.2.0 |
