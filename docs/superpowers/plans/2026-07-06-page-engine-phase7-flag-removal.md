# Page Engine Phase 7 (Flag Removal & Ship) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The page engine becomes the Dashboard, unconditionally (spec §12.7): the `EnablePageEngine` flag and its Settings toggle disappear, the frozen classic Dashboard XAML/members are deleted, shipped factory defaults visually reproduce the classic layout (verified BEFORE deletion), and README/CHANGELOG present customization as a stable feature.

**Architecture:** Pure removal + polish — no new subsystems. The engine path (Phases 1–6) is already the complete feature; Phase 7 deletes the classic fallback and the branch points. `AppSettings.EnablePageEngine` follows the `IsDarkTheme` precedent (AppSettings.cs:9): kept as an inert migration-only property so old settings.json files deserialize cleanly, but nothing reads it. Startup failure handling changes meaning: with no classic path left, a profile/layout load failure falls back to the FACTORY DEFAULT engine page + an error toast (previously `usePageEngine=false`).

**Tech Stack:** unchanged (.NET 8, Avalonia 11.2.3). Branch: `feat/page-engine-phase7-flag-removal` from post-P6 main.

## Global Constraints

- All standing rulings (FluentAssertions; usings above namespace + blank line; XML docs on every public member; TreatWarningsAsErrors; TDD w/ RED for any Core change; widget-Dispose-never-touches-VM).
- **The classic-freeze mirror policy RETIRES with this phase** — after T3 there is no classic copy to mirror into; note the retirement in the PR body and ledger.
- **Deletion gate:** classic XAML may not be deleted until T1's side-by-side evidence is captured and conductor-approved.
- Recon anchors (from 2026-07-05 recon; **re-verify all line numbers on the post-P6 tree before editing**): flag refs = AppSettings.cs:164, DashboardViewModel.cs:75/94, SettingsViewModel.cs:213/406/773/775, SettingsView.axaml:1489-1491 (Experimental CheckBox). Classic-only content = DashboardView.axaml classic ScrollViewer + sections (~72–656 pre-P6). KEEP: all engine members, edit chrome (pencil/toolbar), EditAdornerControl, Widgets/*, WidgetCatalog, messenger registrations, App.axaml.cs migration-before-VM ordering.
- Suite baseline: whatever Phase 6 lands (520/520 + any T5 additions). Factory defaults: `src/NexusMonitor.Core/Pages/Defaults/dashboard.default.json` via BuiltInPageLayouts manifest-resource loading.

---

### Task 1: Side-by-side equivalence evidence (GATE — no deletion until passed)

**Files:** none modified (screenshot/smoke task).

- [ ] Launch with flag OFF → screenshot classic Dashboard (full window). Launch with flag ON, pristine Default profile (move any user profile dir aside first, restore after) → screenshot engine Dashboard. Same window size both runs.
- [ ] Compare: 10 widgets present, same order/proportions, live data in all cards. Deliver both screenshots + a per-section checklist to the conductor for approval. Any visual drift → fix `dashboard.default.json` (Core change, TDD: adjust BuiltInPageLayouts test expectations) before proceeding.
- [ ] Restore user profile dir; commit nothing (evidence-only task) or commit default-json fixes if any: `fix(pages): factory default parity with classic layout`

### Task 2: Flag removal — engine unconditional, settings inert

**Files:** Modify `src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs`, `src/NexusMonitor.UI/App.axaml.cs` (only if it reads the flag), `src/NexusMonitor.Core/Models/AppSettings.cs` (doc-only), `src/NexusMonitor.UI/ViewModels/SettingsViewModel.cs`, `src/NexusMonitor.UI/Views/SettingsView.axaml`.

**Interfaces:** `DashboardViewModel.UsePageEngine` property stays (XAML binds to it) but becomes constant-true, documented for removal in a later cleanup; the ctor's flag read is deleted.

- [ ] DashboardViewModel: delete `settings.EnablePageEngine` read; `UsePageEngine` → always true. REPLACE the old catch-fallback (`usePageEngine=false` on load failure) with: build the factory-default engine page (`BuiltInPageLayouts`), show an error toast ("Your saved layout could not be loaded — showing defaults. Your profile file was preserved.") via the notification service, do NOT overwrite the user's profile file on disk (they may fix it by hand or a later load may succeed).
- [ ] AppSettings.cs: `EnablePageEngine` gets the `IsDarkTheme` treatment — XML doc "kept for settings-file migration only; not read anywhere" (property stays, default irrelevant).
- [ ] SettingsViewModel: delete `_enablePageEngine` ObservableProperty, init, OnChanged handler, persist line. SettingsView.axaml: delete the Experimental CheckBox block (and the Experimental section header if now empty).
- [ ] Build 0 warnings; suite green; commit `feat(pages): page engine unconditional — EnablePageEngine retired to migration-only`

### Task 3: Classic Dashboard deletion

**Files:** Modify `src/NexusMonitor.UI/Views/DashboardView.axaml` (delete classic ScrollViewer + all classic sections; the engine host + edit chrome remain), `src/NexusMonitor.UI/Views/DashboardView.axaml.cs` (remove classic-only wiring if any), `src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs` (delete members ONLY the classic XAML consumed — verify each candidate by grepping the post-T2 XAML before deleting; anything the engine widgets bind via WidgetTileFactory stays).

- [ ] Enumerate candidate classic-only VM members by cross-referencing the deleted XAML's bindings against Widgets/* bindings (the same VM properties feed both in many cases — DELETE ONLY what has zero remaining consumers). Produce the kept/deleted list in the report.
- [ ] Delete classic XAML; delete orphaned VM members; grep for now-unused usings/styles/converters referenced only by deleted XAML.
- [ ] Full launch smoke: Dashboard renders engine page, edit mode works, no binding errors in console output.
- [ ] Build 0 warnings; suite green; commit `feat(pages): remove classic dashboard — page engine is the dashboard`

### Task 4: Docs, defaults polish, release notes

**Files:** Modify `README.md`, `CHANGELOG.md`, `docs/feature-inventory.md`.

- [ ] README: new "Customizable dashboard" section (edit mode, widget gallery, drag/resize, workspace profiles bundling layout+theme, export/import, pop-out windows; screenshots optional/deferred). Update any stale dashboard description (lines ~26/122 pre-P6).
- [ ] CHANGELOG Unreleased: rewrite the experimental page-engine entries as a single stable-feature block (Added: customizable dashboard w/ profiles + pop-outs; Removed: `EnablePageEngine` experimental flag — now always on; Changed: layout-load failure falls back to defaults with a notice).
- [ ] feature-inventory.md: dashboard entry reflects customization.
- [ ] Final whole-branch smoke: fresh-profile launch, migration launch (pre-existing settings.json WITH `"enablePageEngine": false` — engine must load anyway, no crash), edit/save/profile-switch/pop-out spot checks.
- [ ] Commit `docs: customizable dashboard is stable — README, changelog, inventory`

## Done means

Engine renders unconditionally with factory parity proven by the T1 evidence; flag inert-but-deserializable (old settings files load); classic XAML/members gone; docs current; suite green ×3-OS CI. Remaining post-v1 (unchanged backlog): Performance-tab conversion, sensor-composable widget tier, per-widget ConfigJson channel, view-mode context menus, title-bar profile dropdown, import preview, Gaming Mode auto-switch hook.
