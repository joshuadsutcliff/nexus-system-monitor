# Snapshot & Compare — Design Spec

**Date:** 2026-07-19 · **Status:** Approved-pending-review · **Owner:** Josh (TheBlackSwordsman)
**Feature:** Historical disk-scan snapshots with two-point comparison — "scan once a week, see how your storage changed" — extending the disk analyzer, plus the first `nexus disk` CLI commands.
**Rev 2:** amended 2026-07-19 after external (Kiro) review — 15 of 18 findings accepted; threshold-lowering finding rejected (directory rollups already preserve growth attribution at any depth).

## 1. Goals and non-goals

1. **Extend the differentiator** — the disk analyzer is already a headline feature; snapshots give it the "Nexus can do something WizTree can't" claim: historical disk-growth tracking.
2. **Zero new UX to accrue value** — every completed scan is saved automatically; history exists the second time the user scans, without them having learned anything.
3. **Honest diffs** — the compare surface states exactly what it can and cannot see (small-file aggregation threshold, renames reported as remove+add), consistent with the platform-honesty positioning.
4. **CLI composability** — `nexus disk scan --diff` (the synthesis's "`--changed-since`" capability, renamed) falls out of the same store (per the 2026-07-19 Kiro probe synthesis).

**Non-goals (v1):** scheduled/background scans (user-initiated scans only); inline vs-last-scan badges in the normal scan view (possible fast-follow); rename/move detection via file IDs or inodes; hash-based content-change detection; snapshotting anything other than disk scans (no process/startup-state snapshots); remote anything.

## 2. Locked product decisions

| Decision | Choice |
|---|---|
| Snapshot scope | Disk scans only |
| Capture | Every completed scan auto-saved; no manual save step, no scheduler |
| Granularity | **Threshold hybrid** — all directories + every file ≥ threshold (default 1 MB, configurable); sub-threshold files roll into one per-directory "small files" aggregate |
| Compare UX | Dedicated two-snapshot diff-tree view (arbitrary pair, same root); no badges |
| Storage | Separate SQLite DB `disk-snapshots.db` following `MetricsDatabase` conventions |
| Retention | Per root: last 26 snapshots (configurable; ≈6 months at weekly cadence) + global DB size cap (default 500 MB) |

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
  root_path TEXT NOT NULL,         -- normalized (see below); display casing preserved
  root_key TEXT NOT NULL,          -- normalized + case-folded grouping key
  created_at TEXT NOT NULL,        -- UTC ISO-8601
  scanner TEXT NOT NULL,           -- 'recursive' | 'mft'
  file_system TEXT,                -- from ScanResult.FileSystem (NTFS/APFS/ext4/FAT32…)
  volume_total INTEGER,            -- from ScanResult.VolumeTotal — free-space trend
  volume_free INTEGER,             -- from ScanResult.VolumeFree     cannot be re-captured
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
  created TEXT,                    -- nullable; stored for future features,
  last_accessed TEXT,              -- never diffed in v1 (see §5 note)
  small_files_size INTEGER,        -- dirs only: sub-threshold rollup
  small_files_count INTEGER,
  PRIMARY KEY (snapshot_id, id)
)
CREATE INDEX idx_nodes_parent ON nodes (snapshot_id, parent_id);
```

Full paths are reconstructed by walking `parent_id` — no repeated path strings; that is most of the size win. Expected cost: ~10–30 MB per large (≈1M-file) volume snapshot at the default threshold, roughly 10–20% of full-fidelity row count.

**Root-path normalization (grouping correctness).** Before storage, the scanned root is normalized: `Path.GetFullPath` (canonical absolute path), trailing directory separators stripped. `root_path` keeps the user's casing for display; `root_key` additionally case-folds (invariant) on case-insensitive platforms (Windows, macOS) and is what groups snapshots and gates same-root diffing. Without this, `C:\Data` vs `c:\data\` would silently split one root's history in two the first time the CLI and GUI disagree on casing.

`created`/`last_accessed` are stored nullable as schema insurance (old snapshots cannot be re-captured; a future "files not accessed in 6 months" feature needs them from snapshot #1). Honesty caveat: NTFS last-access updates are frequently disabled or coarse-grained, so v1 never diffs or displays claims based on them.

## 4. Capture and retention

- On scan completion in `DiskAnalyzerViewModel` (and in `nexus disk scan`), the finished tree is handed to `SnapshotWriter` on a background task; the UI shows a subtle "saving snapshot…" indicator and stays responsive.
- Only **complete** scans are saved. Cancelled or partial scans never produce a snapshot. The `complete` flag is set last, in the final transaction; a startup sweep deletes any rows with `complete = 0` (crash-mid-write recovery).
- Retention runs after each successful write: per `root_key` keep the newest `retentionPerRoot` (default 26 ≈ six months of weekly scans), then enforce the global size cap (default 500 MB) by repeatedly deleting the oldest snapshot **from whichever root currently holds the most snapshots** — a lone large root is not starved of history to protect many small ones. The retention behavior is described in the snapshot-settings UI.
- If a background snapshot save fails (§8), the UI surfaces a non-blocking warning toast — "Scan complete — snapshot could not be saved: <reason>" — so the user never silently believes a snapshot exists that doesn't.
- The threshold is recorded **per snapshot**. Diffing two snapshots with different thresholds uses `max(thresholdA, thresholdB)` for file-level detail and says so in the UI/CLI output (see §5/§7 for the exact disclosure).
- Settings block (normal `SettingsService` JSON): `diskSnapshots: { enabled: true, thresholdBytes: 1048576, retentionPerRoot: 26, maxDbSizeMb: 500 }`.

## 5. Diff engine

`SnapshotDiffer.Diff(olderId, newerId)`:

- Loads both node sets into path-keyed dictionaries (tens of MB at hybrid row counts; no streaming in v1).
- Matches directories and files by name within parent. **Explicit comparer rule:** ordinal-ignore-case on Windows and macOS, ordinal on Linux — and the active rule is disclosed in the diff honesty header ("names matched case-insensitively"). A case-sensitive-everywhere differ would fabricate remove+add pairs for simple case-renames on NTFS/APFS; that would be a fabricated-change honesty violation.
- Diffs compute on logical `size` only. `allocated_size` is stored but is **not** a diff dimension in v1 (see §9).
- Output: a `DiffNode` tree — per node a `ChangeKind` (`Added | Removed | Grown | Shrunk | Unchanged`), before/after sizes, delta bytes; per directory additionally the small-files rollup delta, so mass small-file growth (caches, `node_modules`) surfaces as an aggregate delta even though no individual file is named. Within each directory level, entries sort by absolute delta — so a rename's Removed and Added rows (same parent, same size) cluster adjacently instead of scattering.
- **Renames are reported as Removed + Added.** No move detection in v1; the UI states this rather than pretending.
- **Mismatched-threshold disclosure is explicit**, not just a stated number: "Snapshots used different thresholds (1 MB and 100 KB); diff uses 1 MB — files between 100 KB and 1 MB are aggregated."
- Same-`root_key` pairs only; the differ enforces it.
- "Now vs. last week" needs no special path: the current scan was auto-saved, so it is simply the two newest snapshots.

## 6. Compare UI

Inside the existing Disk Analyzer view, as a third mode following the Duplicates-toggle precedent (no new nav item):

- **Snapshots mode:** list of snapshots for the current root — date, total size, file count — with delete, store-wide disk usage, a link to the retention settings, and **Compare** on any two selected. A **"Compare with previous"** shortcut pre-selects the two newest snapshots for the root — the first-class entry point for the dominant "now vs. last time" case; the arbitrary two-snapshot picker remains for everything else.
- **Diff view:** a `DataGrid` diff tree in the same visual style as the folder grid. Columns: Name · Change (color-coded kind) · Before → After · **Δ (default sort, by absolute delta)**; directories expandable. The tree renders **collapsed to the top level by default** with row virtualization — the UI never materializes the fully-expanded tree, so a hundred-thousand-change diff stays responsive.
- Honesty header on every diff: e.g. "Comparing Jul 12 → Jul 19 · files under 1 MB aggregated per folder · renames appear as removed + added · names matched case-insensitively."

## 7. CLI

New `disk` branch in `NexusMonitor.CLI` (first disk commands; same Spectre command pattern as `alerts`/`rules`):

- `nexus disk scan <path>` — scan, auto-save snapshot (same store and retention), print summary.
- `nexus disk scan <path> --diff <latest|id|date>` — scan, save, diff against the referenced snapshot, print changed paths with deltas. Resolution: `latest` = the newest snapshot that existed before this scan; a date resolves to the newest snapshot at or before that date (error if none). (*Renamed from `--changed-since`, which fought the id/`latest` forms; `--diff` names the operation.*)
- `nexus disk snapshots list [path]` — with path omitted, lists **all** snapshots across all roots, grouped by root (the "show me everything I've snapshotted" default) · `nexus disk snapshots delete <id>`.
- `nexus disk diff <id-a> <id-b>` — diff two stored snapshots without scanning.
- **`--format table|json`** (table default) on `disk scan --diff`, `disk diff`, and `disk snapshots list` — the target segment scripts these (`jq`, cron reports, shipping deltas to a metrics stack); "machine-friendly text" without JSON isn't machine-friendly.
- **`--top N`** on `disk scan --diff` and `disk diff` — limit output to the N largest changes by absolute delta.
- Diff footers carry the same explicit threshold disclosure as the UI, including the mismatched-threshold sentence verbatim (§5).

## 8. Error handling

- **Corrupt/unopenable DB:** rename aside to `disk-snapshots.db.corrupt-<date>`, recreate empty, surface a warning. Metrics are unaffected by construction.
- **Failure mid-write (incl. disk-full):** transaction rollback + delete the incomplete snapshot row; the in-memory scan result is never lost, only the snapshot. Crash cases are caught by the `complete`-flag startup sweep.
- **Retention pruning failure:** non-fatal; retried on the next write.
- **Vanished roots:** snapshots of a root that no longer exists remain listable, diffable, and deletable.
- Timestamps stored UTC, displayed local.

## 9. Platform-honesty-contract note

The feature consumes a completed `ScanResult`; `RecursiveScanner`/`MftScanner` are not modified. The pre-existing non-compliance in `RecursiveScanner` (`GetCompressedFileSizeW` returning bare `long` with a silent fallback) is therefore out of scope here and remains a known item for whichever change next touches those files (CONTRIBUTING.md "Platform Code Honesty Contract"). All Snapshot & Compare code is pure managed — no new P/Invoke surface.

**Consequence for stored data:** because of that silent fallback, `allocated_size` may hold the uncompressed size for files on NTFS-compressed volumes. Snapshots store it anyway (it is correct in the common case and cannot be back-filled later), but v1 diffs neither compute on it nor display allocated-size deltas — presenting a known-potentially-wrong value in a diff would violate the honesty contract this feature is built around. It becomes a diff dimension only after the scanner-side fix lands.

## 10. Testing

These are the **first disk-analyzer tests in the suite**. All differ/writer logic is testable without a real filesystem: synthetic `DiskNode` trees built in code; SQLite against injected temp paths (pattern: `SettingsServiceTests`).

- Writer round-trip: tree → DB → reader → equivalent node set; threshold application (file at/above/below boundary; rollup sums correct); volume/filesystem metadata persisted.
- Root normalization: casing variants, trailing separators, and CLI-vs-GUI path spellings of the same root all group under one `root_key`.
- Differ: added/removed/grown/shrunk; threshold-crossing file (appears file-level as Added while the combined file+rollup delta stays net-consistent); small-files rollup delta; mismatched-threshold pairing uses the max and flags it; same-root enforcement; case-rule matrix (case-rename is Unchanged under ignore-case matching, Removed+Added under ordinal); within-directory |Δ| sort clusters rename pairs.
- Retention: per-root count pruning; global size-cap pruning takes from the root with the most snapshots.
- CLI: `--format json` output shape is stable and asserted; `--top N` truncation; omitted-path list grouping.
- Recovery: corrupt-DB rename-and-recreate; `complete = 0` startup sweep.
- CLI: command tests following the existing CLI test precedent.
- All pure managed code — the full suite runs on all three CI OSes with no platform gating.
