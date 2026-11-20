# Plan: Process-grid column customization (show/hide)

**Date:** 2026-07-20 · **Scope decided by Josh (quick-scope):** Processes grid only · show/hide only (no reorder/widths) · header right-click context menu · persisted per grid with "Reset to Default" · existing users see zero change until they customize.

## Global Constraints

1. **Column identity = stable string keys**, not header text: `pid, cpu, memory, leak, io, impact, rules, group, priority, threads, handles, user`. The **Name column is NOT hideable** (anchor column, like Task Manager) and has no key.
2. **Persistence shape:** `AppSettings.ProcessColumnsHidden` — `List<string>` of hidden column keys, default **empty** (= all visible). Absent key in older settings JSON deserializes to empty ⇒ migration-safe by construction (existing users unaffected). Unknown keys in the list are ignored harmlessly. Reset = clear the list + save.
3. **Visibility mechanism:** bind/assign `DataGridColumn.IsVisible` (Avalonia 11.2.3 — exists since 11.1). Do not remove/re-add columns from the Columns collection.
4. Persist via the existing `SettingsService` save path the VM already uses (`ProcessesViewModel` already receives `AppSettings`); follow whatever save-call idiom neighboring VM settings writes use.
5. TDD with discriminating tests; VM tests go in `tests/NexusMonitor.UI.Tests` (exists since PR #33, plain-VM style, no Avalonia bootstrap); settings round-trip/migration tests go wherever `AppSettings` serialization tests already live (find them; follow suit).
6. No new Bitsum-style names; match repo idiom/comment density. All suites green before commit.

## Task 1 — Settings + ViewModel state (`ProcessColumnsHidden`, options collection, reset)

**Files:** `src/NexusMonitor.Core/Models/AppSettings.cs`, `src/NexusMonitor.UI/ViewModels/ProcessesViewModel.cs` (+ a small `ProcessColumnOption` type where the repo keeps row/option models for this VM), tests.

1. Add `ProcessColumnsHidden` (`List<string>`, default empty) to `AppSettings` next to similar list-typed settings; mirror their JSON attributes/idiom.
2. New `ProcessColumnOption` observable model: `Key` (string), `Header` (display string), `IsVisible` (bool, observable). 
3. `ProcessesViewModel` gains `ColumnOptions` (ordered collection of the 12 hideable columns, headers exactly matching the XAML: PID, CPU, Memory, Leak, I/O, Impact, Rules, Group, Priority, Threads, Handles, User), initialized from settings (key in hidden-list ⇒ IsVisible false). Toggling an option updates `ProcessColumnsHidden` and saves settings. `ResetColumnsCommand` sets all visible, clears the list, saves.
4. **Tests (discriminating):** (a) settings: JSON WITHOUT the new key → empty list (migration guard); round-trip preserves entries. (b) VM: defaults all-visible; toggle off → key lands in settings list + save invoked; toggle back on → key removed; reset with 3 hidden → all visible, list empty, save invoked; unknown key in stored list → ignored, no throw. Prove at least the migration-guard and toggle tests bite (neuter-proof: temporarily break, observe failure, restore, note in report).

## Task 2 — XAML wiring (IsVisible bindings + header context menu)

**Files:** `src/NexusMonitor.UI/Views/ProcessesView.axaml` (+ code-behind only if a binding helper is genuinely required).

1. Bind each hideable column's `IsVisible` to its `ProcessColumnOption.IsVisible`. Columns are not in the visual tree's DataContext chain — use whatever the repo's cleanest working pattern is (e.g. `x:Name`d columns assigned in code-behind from `ColumnOptions`, or compiled bindings with a source reference). Correctness over cleverness; a small code-behind subscription is acceptable and testable.
2. Add a context menu on the **column headers** (Style selector `DataGridColumnHeader` ContextMenu, or equivalent working approach): one checkable `MenuItem` per option (header text, `IsChecked` two-way to `IsVisible`), separator, **Reset to Default** wired to `ResetColumnsCommand`. The Name column header shows the same menu (menu lists only the 12 hideable columns).
3. Keep the existing row `DataGrid.ContextMenu` untouched.
4. Build must be clean; add any VM-level test that becomes possible (e.g. option ordering matches XAML column order). Visual verification is deferred to the standing GUI live pass — note that in the report.
