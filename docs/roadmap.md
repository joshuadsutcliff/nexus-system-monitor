---
type: note
date: 2026-03-05
project: NexusSystemMonitor
tags: [nexus, roadmap, planning, deprecated]
status: deprecated
---

> [!warning] DEPRECATED — Reference Only
> This roadmap was written in early March 2026 and reflects planning through v0.1.7.
> It has been **superseded by [[v1.0-roadmap]]** (approved 2026-03-24), which is the authoritative plan for all work from v0.2.0 through v1.0.0.
> **Do not use this file to determine what work is left to do.** Use it only to understand decisions and priorities that shaped earlier versions.

# Roadmap

Forward-looking plan derived from [[gap-analysis|gap analysis]], prioritized by user value.

> [!important] Ship criteria (Apple Principle)
> Before any item ships it must be:
> 1. **Intuitive on first interaction** — no tooltip or manual needed
> 2. **Complete, not partial** — no placeholder UI, no broken platform
> 3. **Actually working** — not hidden behind a flag

Items are organized into three tiers. Within each tier, order reflects suggested priority.

---

## Completed in v0.1.7

- **Process search / filter bar** — `ProcessesViewModel.SearchText` + toolbar TextBox
- **Persistent process priorities and affinities** — `ProcessPreferenceStore` (SQLite) + RulesEngine integration + Settings UI
- **Performance Mode Profiles** — `PerformanceProfilesViewModel/View` with process rules + power plan switching + overlay indicator
- **Disk Analyzer UI integration** — `DiskAnalyzer` NavItem added to sidebar (was built but hidden)
- **MFT-based scanning** — `MftScanner.cs` was already implemented in DiskAnalyzer
- **Flat file list, duplicate detection, file type stats, scan progress** — all existed in DiskAnalyzer engine
- **Service startup type editing** — `SetStartupTypeCommand(string)` in `ServicesViewModel` → `SetStartTypeAsync`; context menu submenu: Automatic/AutomaticDelayed/Manual/Disabled

---

## Near-Term (next 1–3 releases)

### Process search and multi-select actions
**Gap:** Can't kill/set-priority on a selection of processes.
**Implementation:** `SelectedItems` collection in `ProcessesViewModel`, multi-select context menu with Kill All, Set Priority (submenu), and Suspend All actions.
**Ship criteria:** Shift-click / Ctrl-click selects multiple rows. Actions apply to all selected. Confirmation dialog for kill.

---

## Medium-Term (3–6 months)

### Column customization
**Gap:** Users can't hide irrelevant columns (e.g. GPU column on a machine without a GPU).
**Implementation:** `ColumnPreferences` in `AppSettings`. Context menu on column headers: "Hide column" / "Column settings." Persisted per-view.

### Idle Throttle (background process throttling)
**Gap:** PL's `IdleThrottle` — reclaim CPU from idle/background processes when system is under load.
**Implementation:** `IdleThrottleService` monitors total CPU % above threshold → applies BelowNormal to non-foreground, non-pinned processes.

### Working set trim / Memory Reclaim
**Gap:** PL's memory reclamation. Forces Windows to compress standby memory for selected processes.
**Implementation:** `SetProcessWorkingSet` / `EmptyWorkingSet` P/Invoke. UI: "Trim memory" button in process context menu + a batch "Trim all" action.

### Export disk scan to CSV
**Gap:** Power-user workflow: export list of large files for analysis.
**Implementation:** Add "Export CSV" button to DiskAnalyzerView, write FileInfo rows.

> [!tip] Differentiation opportunity
> Duplicate detection already ships (DuplicateFinder.cs). Next: surface it better in the UI with a dedicated "Duplicates" tab showing total wasted space and one-click cleanup.

### Historical disk growth tracking
**Gap:** No competitor does this.
**Implementation:** Store DiskAnalyzer scan results in SQLite (same DB as metrics). Show "growth since last scan" delta in treemap and flat list. Weekly auto-scan option.

---

## Long-Term (6+ months)

### Process token / security info
**Gap:** System Informer parity — show elevation level, token groups, privileges.
**Implementation:** `GetTokenInformation` with `TOKEN_USER`, `TOKEN_GROUPS`, `TOKEN_PRIVILEGES`. New "Security" section in process detail panel.

### Global hotkey support
**Gap:** Can't bring Nexus to the front from a game or full-screen app.
**Implementation:** `RegisterHotKey` P/Invoke (Windows), `NSEvent` global monitor (macOS), X11 keygrab (Linux). Configurable in Settings.

### GPU priorities (Windows)
**Gap:** Windows 11 exposes GPU scheduling priority via `D3DKMTSetProcessSchedulingPriorityClass`.
**Implementation:** Add GPU priority to process context menu. Windows-only, silently unavailable on other platforms.

### Geo-IP lookup for network connections
**Gap:** Can see remote IPs but not country/ISP.
**Implementation:** MaxMind GeoLite2 database (embedded, ~50 MB). Show country flag + ISP name in Network tab. Weekly DB update.

### CLI automation interface
**Gap:** No scriptable interface.
**Implementation:** `nexus` CLI tool that wraps the Core library. Basic commands: `nexus ps`, `nexus set-priority <exe> <level>`, `nexus kill <pid>`, `nexus disk scan <path>`.

### Command-line filtering in rules
**Gap:** Rules only match on executable name; can't target `python script.py` vs `python server.py`.
**Implementation:** Extend `ProcessRule` with `CommandLinePattern` (substring or regex). Match against full command line in `RulesEngine`.

---

## Design Philosophy Reminders

Each roadmap item must pass the Apple test before shipping:

> **"Would a first-time user understand what this does and how to use it without reading any documentation?"**

If the answer is no, redesign the interaction, not the feature. Nexus ships complete or not at all.
