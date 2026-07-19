# Snapshot & Compare — Design Spec

**Date:** 2026-07-19 · **Status:** Approved-pending-review · **Owner:** Josh (TheBlackSwordsman)
**Feature:** Historical disk-scan snapshots with two-point comparison — "scan once a week, see how your storage changed" — extending the disk analyzer, plus the first `nexus disk` CLI commands.

## 1. Goals and non-goals

1. **Extend the differentiator** — the disk analyzer is already a headline feature; snapshots give it the "Nexus can do something WizTree can't" claim: historical disk-growth tracking.
2. **Zero new UX to accrue value** — every completed scan is saved automatically; history exists the second time the user scans, without them having learned anything.
3. **Honest diffs** — the compare surface states exactly what it can and cannot see (small-file aggregation threshold, renames reported as remove+add), consistent with the platform-honesty positioning.
4. **CLI composability** — `nexus disk scan --changed-since` falls out of the same store (per the 2026-07-19 Kiro probe synthesis).

**Non-goals (v1):** scheduled/background scans (user-initiated scans only); inline vs-last-scan badges in the normal scan view (possible fast-follow); rename/move detection via file IDs or inodes; hash-based content-change detection; snapshotting anything other than disk scans (no process/startup-state snapshots); remote anything.

## 2. Locked product decisions

| Decision | Choice |
|---|---|
| Snapshot scope | Disk scans only |
| Capture | Every completed scan auto-saved; no manual save step, no scheduler |
| Granularity | **Threshold hybrid** — all directories + every file ≥ threshold (default 1 MB, configurable); sub-threshold files roll into one per-directory "small files" aggregate |
| Compare UX | Dedicated two-snapshot diff-tree view (arbitrary pair, same root); no badges |
| Storage | Separate SQLite DB `disk-snapshots.db` following `MetricsDatabase` conventions |
| Retention | Per root: last 10 snapshots (configurable) + global DB size cap (default 500 MB) |

## 3. Architecture overview

New folder `src/NexusMonitor.DiskAnalyzer/Snapshots/`:

- `ISnapshotStore` — save / list / load / delete; registered via the existing DI extension pattern (`NexusServiceCollectionExtensions`).
- `SnapshotWriter` — consumes a completed `ScanResult` (post-scan; **scanner files are never touched** — see §9), applies the threshold, writes in batched transactions.
- `SnapshotReader` — loads snapshot metadata and node sets.
- `SnapshotDiffer` — pure logic, produces the `DiffNode` tree; unit-testable without a filesystem.

Storage is a **separate** SQLite file `disk-snapshots.db` in the existing per-platform app-data dir (`~/Library/Application Support/NexusMonitor/` on macOS, `%AppData%/NexusMonitor/` on Windows), on `Microsoft.Data.Sqlite` with WAL and batched writes, mirroring `MetricsDatabase`/`MetricsStore` conventions. Deliberately not inside `metrics.db`: retention, vacuum, and schema evolve independently, and a corrupt snapshot DB can never damage metrics history.

### 3.1 Schema

```sql
snapshots (
  id INTEGER PRIMARY KEY,
  root_path TEXT NOT NULL,
  created_at TEXT NOT NULL,        -- UTC ISO-8601
  scanner TEXT NOT NULL,           -- 'recursive' | 'mft'
  total_size INTEGER, total_files INTEGER, total_dirs INTEGER,
  threshold_bytes INTEGER NOT NULL,
  app_version TEXT,
  complete INTEGER NOT NULL DEFAULT 0
)

nodes (
  snapshot_id INTEGER NOT NULL,
  id INTEGER NOT NULL,             -- per-snapshot node id
  parent_id INTEGER,               -- NULL for root
  name TEXT NOT NULL,
  is_dir INTEGER NOT NULL,
  size INTEGER, allocated_size INTEGER,
  file_count INTEGER, folder_count INTEGER,
  last_modified TEXT,
  small_files_size INTEGER,        -- dirs only: sub-threshold rollup
  small_files_count INTEGER,
  PRIMARY KEY (snapshot_id, id)
)
CREATE INDEX idx_nodes_parent ON nodes (snapshot_id, parent_id);
```

Full paths are reconstructed by walking `parent_id` — no repeated path strings; that is most of the size win. Expected cost: ~10–30 MB per large (≈1M-file) volume snapshot at the default threshold, roughly 10–20% of full-fidelity row count.

## 4. Capture and retention

- On scan completion in `DiskAnalyzerViewModel` (and in `nexus disk scan`), the finished tree is handed to `SnapshotWriter` on a background task; the UI shows a subtle "saving snapshot…" indicator and stays responsive.
- Only **complete** scans are saved. Cancelled or partial scans never produce a snapshot. The `complete` flag is set last, in the final transaction; a startup sweep deletes any rows with `complete = 0` (crash-mid-write recovery).
- Retention runs after each successful write: per `root_path` keep the newest `retentionPerRoot` (default 10), then enforce the global size cap (default 500 MB) by deleting oldest-first across roots.
- The threshold is recorded **per snapshot**. Diffing two snapshots with different thresholds uses `max(thresholdA, thresholdB)` for file-level detail and says so in the UI/CLI output.
- Settings block (normal `SettingsService` JSON): `diskSnapshots: { enabled: true, thresholdBytes: 1048576, retentionPerRoot: 10, maxDbSizeMb: 500 }`.

## 5. Diff engine

`SnapshotDiffer.Diff(olderId, newerId)`:

- Loads both node sets into path-keyed dictionaries (tens of MB at hybrid row counts; no streaming in v1).
- Matches directories and files by name within parent; case-sensitivity follows the platform default.
- Output: a `DiffNode` tree — per node a `ChangeKind` (`Added | Removed | Grown | Shrunk | Unchanged`), before/after sizes, delta bytes; per directory additionally the small-files rollup delta, so mass small-file growth (caches, `node_modules`) surfaces as an aggregate delta even though no individual file is named.
- **Renames are reported as Removed + Added.** No move detection in v1; the UI states this rather than pretending.
- Same-`root_path` pairs only; the differ enforces it.
- "Now vs. last week" needs no special path: the current scan was auto-saved, so it is simply the two newest snapshots.

## 6. Compare UI

Inside the existing Disk Analyzer view, as a third mode following the Duplicates-toggle precedent (no new nav item):

- **Snapshots mode:** list of snapshots for the current root — date, total size, file count — with delete, store-wide disk usage, a link to the retention settings, and **Compare** on any two selected.
- **Diff view:** a `DataGrid` diff tree in the same visual style as the folder grid. Columns: Name · Change (color-coded kind) · Before → After · **Δ (default sort, by absolute delta)**; directories expandable.
- Honesty header on every diff: e.g. "Comparing Jul 12 → Jul 19 · files under 1 MB aggregated per folder · renames appear as removed + added."

## 7. CLI

New `disk` branch in `NexusMonitor.CLI` (first disk commands; same Spectre command pattern as `alerts`/`rules`):

- `nexus disk scan <path>` — scan, auto-save snapshot (same store and retention), print summary.
- `nexus disk scan <path> --changed-since <latest|id|date>` — scan, save, diff against the referenced snapshot, print changed paths with deltas; machine-friendly output; a footer states the active threshold. Resolution: `latest` = the newest snapshot that existed before this scan; a date resolves to the newest snapshot at or before that date (error if none).
- `nexus disk snapshots list [path]` · `nexus disk snapshots delete <id>`.
- `nexus disk diff <id-a> <id-b>` — diff two stored snapshots without scanning.

## 8. Error handling

- **Corrupt/unopenable DB:** rename aside to `disk-snapshots.db.corrupt-<date>`, recreate empty, surface a warning. Metrics are unaffected by construction.
- **Failure mid-write (incl. disk-full):** transaction rollback + delete the incomplete snapshot row; the in-memory scan result is never lost, only the snapshot. Crash cases are caught by the `complete`-flag startup sweep.
- **Retention pruning failure:** non-fatal; retried on the next write.
- **Vanished roots:** snapshots of a root that no longer exists remain listable, diffable, and deletable.
- Timestamps stored UTC, displayed local.

## 9. Platform-honesty-contract note

The feature consumes a completed `ScanResult`; `RecursiveScanner`/`MftScanner` are not modified. The pre-existing non-compliance in `RecursiveScanner` (`GetCompressedFileSizeW` returning bare `long` with a silent fallback) is therefore out of scope here and remains a known item for whichever change next touches those files (CONTRIBUTING.md "Platform Code Honesty Contract"). All Snapshot & Compare code is pure managed — no new P/Invoke surface.

## 10. Testing

These are the **first disk-analyzer tests in the suite**. All differ/writer logic is testable without a real filesystem: synthetic `DiskNode` trees built in code; SQLite against injected temp paths (pattern: `SettingsServiceTests`).

- Writer round-trip: tree → DB → reader → equivalent node set; threshold application (file at/above/below boundary; rollup sums correct).
- Differ: added/removed/grown/shrunk; threshold-crossing file (appears file-level as Added while the combined file+rollup delta stays net-consistent); small-files rollup delta; mismatched-threshold pairing uses the max and flags it; same-root enforcement.
- Retention: per-root count pruning; global size-cap pruning oldest-first.
- Recovery: corrupt-DB rename-and-recreate; `complete = 0` startup sweep.
- CLI: command tests following the existing CLI test precedent.
- All pure managed code — the full suite runs on all three CI OSes with no platform gating.
