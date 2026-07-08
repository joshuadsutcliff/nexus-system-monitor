---
type: note
date: 2026-03-05
project: NexusSystemMonitor
tags: [nexus, gap-analysis, roadmap, process-lasso, wiztree, deprecated]
status: deprecated
---

> [!warning] DEPRECATED — Reference Only
> This gap analysis was last updated at v0.1.7. Several items it lists as missing have since been implemented or reclassified:
> - **Idle Throttle** and **Memory Reclaim** exist in the codebase (`IdleThrottleService.cs`, `MemoryReclaimService.cs`)
> - **Disk Analyzer** is being enabled in v0.3.0 (no longer "currently disabled")
> - The approved v1.0 plan adds 10 new features not in this document
>
> **Do not use this file to determine what features are missing.** Use it only as a historical reference for the competitive analysis that shaped the original feature set. See [[v1.0-roadmap]] for the current plan.

# Gap Analysis

Feature-by-feature comparison of Nexus System Monitor against **Process Lasso** (~51 features) and **WizTree** (~20 features).

**Summary:** 44 of 51 Process Lasso features implemented · 18 of 20 WizTree features implemented · Updated v0.1.7

> [!tip] Priority legend
> `P1` = High (core user value, implement next) · `P2` = Medium · `P3` = Low / niche

---

## Process Lasso Comparison

### A. Core Process Control

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Set process priority | ✅ Implemented | — | Full Idle→RealTime |
| Set CPU affinity | ✅ Implemented | — | Core mask UI |
| Persistent process priorities (survive reboot) | ✅ Implemented | — | ProcessPreferenceStore — SQLite-backed, applied by RulesEngine on each launch (v0.1.7) |
| Persistent CPU affinities | ✅ Implemented | — | Stored in ProcessPreference.AffinityMask (v0.1.7) |
| CPU Sets (modern Windows API) | ❌ Missing | P2 | `SetProcessDefaultCpuSets` — softer than affinity |
| I/O priority | ✅ Implemented | — | |
| Memory priority | ✅ Implemented | — | |
| Efficiency Mode | ✅ Implemented | — | |
| Kill process | ✅ Implemented | — | |
| Kill process tree | ✅ Implemented | — | |
| Suspend / resume | ✅ Implemented | — | |

### B. Auto-Balance & Automation

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Auto-Balance (automatic load balancing) | ✅ Implemented | — | |
| Gaming Mode (foreground optimization) | ✅ Implemented | — | |
| Rules Engine (auto actions on events) | ✅ Implemented | — | |
| Watchdog monitoring | ✅ Implemented | — | CPU/RAM threshold triggers |
| Instance count limits | ❌ Missing | P2 | Kill/alert when process spawns N+ copies |
| Bitness-based rules | ❌ Missing | P3 | 32-bit vs. 64-bit process targeting |
| Command-line filtering in rules | ❌ Missing | P2 | Match on argv, not just exe name |
| Schedule-based rules | ❌ Missing | P3 | Apply only during business hours, etc. |

### C. Performance Mode Profiles

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Performance Mode (manual profile activate) | ✅ Implemented | — | PerformanceProfilesView — named profiles with process rules and power plan switching (v0.1.7) |
| Idle Throttle (throttle inactive processes) | ❌ Missing | P2 | Background CPU reclamation |
| Memory Reclaim (proactive memory trimming) | ❌ Missing | P2 | Reduce standby memory usage |
| Process Working Set trim | ✅ Implemented | — | `IProcessProvider.TrimWorkingSetAsync` → `EmptyWorkingSet` in `WindowsProcessProvider` (v0.1.7) |
| Gaming profile (full) | ✅ Partial | P2 | Exists; less featureful than PL's implementation |

### D. Monitoring & Alerts

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Real-time performance graphs | ✅ Implemented | — | Per-device sparklines |
| Configurable alerts | ✅ Implemented | — | CPU/RAM threshold notifications |
| Event log viewer | ❌ Missing | P2 | Windows Event Log integration |
| System tray graphs | ❌ Missing | P3 | Animated tray icon showing CPU% |
| Historical graphs | ✅ Implemented | — | SQLite + Historical Viewer tab |
| Anomaly detection | ✅ Implemented | — | |

### E. System Information

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Hardware inventory | ✅ Implemented | — | Full on Windows |
| CPU info with topology | ✅ Implemented | — | |
| Memory info with slot details | ✅ Implemented | — | |
| GPU info | ✅ Implemented | — | |
| Timer resolution reporting | ❌ Missing | P3 | `NtQueryTimerResolution` — niche but PL-exclusive |
| Responsible process for timer resolution | ❌ Missing | P3 | |

### F. Process Details

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Module list (DLLs) | ✅ Implemented | — | |
| Thread list | ✅ Implemented | — | |
| Environment variables | ✅ Implemented | — | |
| Token / security info | ❌ Missing | P2 | Elevation level, token groups (System Informer parity) |
| Heap/stack explorer | ❌ Missing | P3 | Deep inspection, complex |
| String search in process memory | ❌ Missing | P3 | Power user / security feature |
| Network connections per process | ✅ Implemented | — | Via Network tab cross-nav |

### G. Services

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Service list with status | ✅ Implemented | — | |
| Start / stop / restart | ✅ Implemented | — | |
| Change startup type | ✅ Implemented | — | `SetStartupTypeCommand(string)` in `ServicesViewModel` → `SetStartTypeAsync`; submenu: Automatic/AutomaticDelayed/Manual/Disabled (v0.1.7) |
| Navigate to host process | ✅ Implemented | — | |

### H. UI & Workflow

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Column customization | ❌ Missing | P2 | Show/hide columns per user preference |
| Process search / filter bar | ✅ Implemented | — | `ProcessesViewModel.SearchText` + search TextBox in toolbar (v0.1.7) |
| Process color coding | ✅ Implemented | — | 7 category colors |
| Highlight new / changed processes | ❌ Missing | P2 | Flash row on process appear/disappear |
| Process tree view | ✅ Implemented | — | |
| Flat / tree toggle | ❌ Missing | P2 | |
| Multi-select process actions | ❌ Missing | P2 | Kill or set priority on selection |
| Sticky column sort | ✅ Implemented (v0.1.4) | — | Survives tab switch |
| Global hotkey | ❌ Missing | P2 | Bring window to front from anywhere |
| System tray with graphs | ❌ Missing | P3 | |
| Desktop overlay | ✅ Implemented | — | |

### I. Networking

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Network connections | ✅ Implemented | — | |
| Per-connection throughput | ✅ Implemented | — | Windows EStats (auto-hides on incompatible NICs) |
| LAN scanner | ✅ Implemented | — | Nmap-based (v0.1.3) |
| Geo-IP lookup | ❌ Missing | P3 | Country/ISP for remote endpoints |

### J. Extras

| PL Feature | Nexus Status | Priority | Notes |
|------------|-------------|---------|-------|
| Startup items | ✅ Implemented | — | |
| Change service startup type | ✅ Implemented | — | See row G above (v0.1.7) |
| CLI automation interface | ❌ Missing | P3 | `nexus.exe --set-priority notepad.exe High` |
| Scripting API | ❌ Missing | P3 | Plugin/script extensibility |
| Portable mode | ✅ Partial | P2 | Self-contained publish exists; no config portability |

---

## WizTree Comparison

### Core Features

| WizTree Feature | Nexus Status | Priority | Notes |
|-----------------|-------------|---------|-------|
| Visual disk space treemap | ✅ Implemented | — | DiskAnalyzer project |
| Multi-threaded directory scan | ✅ Implemented | — | |
| MFT-based scanning (instant scan) | ✅ Implemented | — | `MftScanner.cs` in NexusMonitor.DiskAnalyzer (v0.1.7) |
| Flat file list sorted by size | ✅ Implemented | — | File View tab in `DiskAnalyzerView.axaml` (v0.1.7) |
| File extension summary | ✅ Implemented | — | `FileTypeStats` in `DiskAnalyzerViewModel` + `FileTypeClassifier` (v0.1.7) |
| Export to CSV | ❌ Missing | P2 | Standard power-user workflow |
| Free space visualization | ❌ Missing | P2 | Show unallocated space in treemap |
| Jump to file in Explorer | ✅ Implemented | — | `OpenFileLocation` command in `DiskAnalyzerViewModel` (v0.1.7) |
| Delete files from UI | ❌ Missing | P2 | Send to Recycle Bin or permanent delete |
| Compare scans / delta view | ❌ Missing | P3 | Diff between two scan snapshots |
| Content hashing (duplicate detection) | ✅ Implemented | — | `DuplicateFinder.cs` (SHA-256) in DiskAnalyzer (v0.1.7) |
| Historical scan comparison | ❌ Missing | P1 | **Differentiation opportunity** — growth tracking over time |
| Cleanup recommendations | ❌ Missing | P2 | **Differentiation opportunity** — AI-driven suggestions |
| Cross-platform disk analysis | ✅ Partial | — | DiskAnalyzer engine is cross-platform; WizTree is Windows-only |
| Filter by file type | ❌ Missing | P2 | Show only .psd, .mp4, etc. |
| Progress indication during scan | ✅ Implemented | — | `ScanProgress` model + progress bar in DiskAnalyzer toolbar (v0.1.7) |
| Rescan subdirectory | ❌ Missing | P3 | Refresh a subtree without full rescan |
| Bookmarks / favorites | ❌ Missing | P3 | Save frequently checked paths |
| Network share scanning | ❌ Missing | P3 | UNC paths |
| Command-line scan | ❌ Missing | P3 | `nexus-disk --scan C: --output result.csv` |

---

## Differentiation Opportunities

> [!important] Where Nexus can beat both competitors
>
> 1. **Cross-platform disk analysis** — WizTree is Windows-only. Nexus DiskAnalyzer works on macOS and Linux too.
> 2. **Duplicate file detection** — Neither WizTree nor Process Lasso does content hashing. Nexus could be the first integrated tool to show "these 3 files are identical, saving X GB would free Y MB."
> 3. **Historical disk growth tracking** — Scan once a week, see how your disk usage changed. No competitor does this.
> 4. **Integrated process + disk workflow** — See which *running process* owns the biggest files. Unique to an integrated tool.
> 5. **Observability pipeline** — Prometheus/Telegraf/Grafana integration is genuinely unique in this category.
