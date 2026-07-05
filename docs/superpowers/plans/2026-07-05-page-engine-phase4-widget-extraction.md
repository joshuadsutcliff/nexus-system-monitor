# Page Engine Phase 4 (Widget Extraction: Real Live Dashboard Widgets) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the placeholder tiles with the real Dashboard: ten live widgets (health ring, four subsystem cards, bottleneck, top consumers, recommendations, predictions, health trends) extracted from the classic view, rendered and editable through the page engine — full visual equivalence with the classic Dashboard at the factory default layout.

**Architecture (grounded in recon):** The data layer is ALREADY shared — every classic section binds directly to `DashboardViewModel` properties fed by one funnel (`ApplySnapshot`), and Avalonia DataContext inheritance flows into `PageHostControl`'s children. Widgets are therefore pure view lifts: each section's XAML moves verbatim into a `Widgets/*.axaml` UserControl that binds the same properties through the inherited DataContext. No new subscriptions, no widget VMs, no teardown problem — the P3 review's "flag-ON pays both VM costs" concern is closed by design (the VM cost was never duplicated; only the hidden classic view remains, which Avalonia skips at `IsVisible=false`). `WidgetTileFactory` becomes a real TypeId→control registry (placeholder `WidgetTile` retained for unknown TypeIds per spec §8); `RebuildChildren` gains the generic disposal hook the P2/P3 reviews mandated (trivially satisfied today, load-bearing later).

**Tech Stack:** .NET 8, Avalonia 11.2.3, existing Core.Pages + edit-mode stack (PRs #12-#14).

## Global Constraints

- `TreatWarningsAsErrors=true`, `Nullable=enable`, ImplicitUsings — zero warnings. No new NuGet dependencies.
- Owner rulings (all phases): FluentAssertions idiom; test files = usings, blank, file-scoped namespace; XML `///` docs on EVERY public member; TDD with captured RED for Core code.
- **NEW standing rule (P3 lesson, twice-hit bug class): any custom-drawn interactive control MUST declare its hit-test geometry explicitly via `ICustomHitTest` — never rely on painted-geometry defaults.** (No new custom-drawn controls are planned this phase; the rule guards drift.)
- Verification environment facts: cliclick clicks AND drags are proven working (display is 1:1 point:pixel); Escape must be sent via `osascript -e 'tell application "System Events" to key code 53'` — cliclick's `kp:esc` is broken system-wide. Screenshots required for UI verification.
- Extraction fidelity: lifted XAML sections move VERBATIM (same tokens, same bindings) except for the explicitly listed scoping changes. The classic path stays fully intact and byte-identical apart from nothing at all — this phase does NOT touch the classic ScrollViewer's content.
- Source-of-truth line ranges (recon 2026-07-05, DashboardView.axaml): health ring 72-112; subsystem 2×2 grid 114-253; bottleneck 256-469; top consumers 471-541; recommendations 543-585; predictions 587-644; HealthTrendsView embed 647. Re-verify ranges before cutting (file may drift a few lines).
- TypeId set (canonical, this phase): `nexus.widget.healthScore`, `nexus.widget.cpuCard`, `nexus.widget.memoryCard`, `nexus.widget.diskCard`, `nexus.widget.gpuCard`, `nexus.widget.bottleneck`, `nexus.widget.topConsumers`, `nexus.widget.recommendations`, `nexus.widget.predictions`, `nexus.widget.healthTrends`. Legacy aliases (Phase-1 factory JSON + any saved layouts): `nexus.widget.cpuChart` → cpuCard, `nexus.widget.memoryChart` → memoryCard — resolved in the factory, preserving old layouts (spec §8 spirit).
- Build/test: `export DOTNET_ROOT=$HOME/.dotnet`; suite currently 470/470. Execution branch: `feat/page-engine-phase4-widgets` from current `main`.

---

### Task 1: Catalog + factory-default layout (Core resource + UI catalog)

**Files:**
- Modify: `src/NexusMonitor.Core/Pages/Defaults/dashboard.default.json` (full 10-widget layout)
- Modify: `src/NexusMonitor.UI/Controls/WidgetCatalog.cs` (10 entries)
- Modify: `tests/NexusMonitor.Core.Tests/Pages/BuiltInPageLayoutsTests.cs` (only if its assertions pin counts — the existing test asserts `>= 3` widgets + validity invariants, which must still pass; update the count floor to `>= 10`)

**Interfaces:**
- Produces: the canonical TypeId strings above (Tasks 2-5 must match byte-exactly); factory default rects:
  healthScore (0,0,4,4) · cpuCard (4,0,4,2) · memoryCard (8,0,4,2) · diskCard (4,2,4,2) · gpuCard (8,2,4,2) · bottleneck (0,4,12,4) · topConsumers (0,8,6,4) · recommendations (6,8,6,4) · predictions (0,12,12,3) · healthTrends (0,15,12,5).
  InstanceIds: fixed GUIDs `0b5e0002-0000-4000-8000-0000000000NN` (01-10 in the order above). Envelope schemaVersion 1, camelCase, field name `rect`.

- [ ] **Step 1:** Rewrite `dashboard.default.json` with the 10 widgets at the rects above (same envelope shape as the current file — copy its structure, replace the widgets array). Validity invariants (in-grid, no overlaps) must hold — the rects above are constructed overlap-free; the existing `BuiltInPageLayoutsTests` pairwise check is the enforcement.
- [ ] **Step 2:** `WidgetCatalog.Entries` → 10 entries, Names: "Health Score", "CPU", "Memory", "Disk", "GPU", "Bottleneck Analysis", "Top Consumers", "Recommendations", "Predictions", "Health Trends"; Descriptions: one line each describing the live content (e.g. "Overall system health ring with trend."). Keep XML docs.
- [ ] **Step 3:** Run `--filter "FullyQualifiedName~BuiltInPageLayoutsTests"` (update the `>= 3` floor to `>= 10` if present) → green; full suite → 470/470 (no other Core change).
- [ ] **Step 4:** Commit: `feat(pages): full-dashboard factory layout and 10-entry widget catalog`

---

### Task 2: Subsystem card widget (one control, four catalog uses)

**Files:**
- Create: `src/NexusMonitor.UI/Widgets/SubsystemCardWidget.axaml` + `.axaml.cs`

**Interfaces:**
- Produces (Task 5 consumes): `SubsystemCardWidget : UserControl` whose DataContext the FACTORY sets to the specific card VM via a binding: `widgetControl.Bind(DataContextProperty, new Binding("CpuCard"))` (or MemoryCard/DiskCard/GpuCard). Inside, the control binds `SubsystemCardViewModel` members (.Icon/.Name/.TrendArrow/.LevelBrush/.Level/.Summary).

- [ ] **Step 1:** Lift ONE card cell from the classic 2×2 grid (DashboardView.axaml ~114-253 — each cell is a `Border`→`StackPanel` binding `CpuCard.Icon` etc.) into `SubsystemCardWidget.axaml`, changing bindings from `CpuCard.X` to plain `X` (the DataContext IS the card VM). Root keeps the cell's own card-chrome Border; stretch to fill (`HorizontalAlignment/VerticalAlignment Stretch`).
- [ ] **Step 2:** Minimal code-behind (InitializeComponent only + class XML doc). Build → 0 warnings.
- [ ] **Step 3:** Commit: `feat(pages): SubsystemCardWidget — parameterized live subsystem card`

---

### Task 3: Health ring, top consumers, recommendations, predictions widgets

**Files:**
- Create: `src/NexusMonitor.UI/Widgets/HealthScoreWidget.axaml` (+.cs), `TopConsumersWidget.axaml` (+.cs), `RecommendationsWidget.axaml` (+.cs), `PredictionsWidget.axaml` (+.cs)

**Interfaces:** all four bind the inherited `DashboardViewModel` DataContext directly (no factory binding needed). Verbatim lifts of their line ranges with these specific notes: HealthScore binds `HealthRingBrush/OverallScore/TrendArrow/OverallLabel/OverallDescription/AutomationStatus`; TopConsumers keeps `TopConsumers` ItemsControl + `RefreshConsumersCommand`; Recommendations keeps `Recommendations`; Predictions: the classic section's outer `IsVisible="{Binding HasPredictions}"` moves OFF the widget root (a widget placed by the user should show an empty-state line "No active predictions" when `HasPredictions` is false, not vanish) — inner content gated on `HasPredictions`, plus a fallback TextBlock gated on `!HasPredictions` (NxFont12/TextSecondaryBrush).

- [ ] **Step 1-4:** One lift per control per the notes; each root keeps its section's card Border; stretch-fill. Build after each → 0 warnings.
- [ ] **Step 5:** Commit: `feat(pages): health ring, top consumers, recommendations, predictions widgets`

---

### Task 4: Bottleneck + health trends widgets (the two non-trivial lifts)

**Files:**
- Create: `src/NexusMonitor.UI/Widgets/BottleneckWidget.axaml` (+.cs), `HealthTrendsWidget.axaml` (+.cs)

**Interfaces & mandated scoping fixes:**
- Bottleneck (classic 256-469): the classic root Border sets `DataContext="{Binding BottleneckCard}"` and one button escapes via `$parent[UserControl].DataContext.RunAnalysisCommand`. In the widget: the UserControl root does NOT set DataContext (stays DashboardViewModel); an inner Border carries `DataContext="{Binding BottleneckCard}"` wrapping the lifted content; the RunAnalysis button's `$parent[UserControl].DataContext.RunAnalysisCommand` now correctly resolves to the widget's inherited DashboardViewModel — same binding string, now structurally sound. Verify `DisableMemoryIntegrityCommand` (on BottleneckCardViewModel) still resolves through the inner scope.
- HealthTrends: thin wrapper — root card Border hosting `<views:HealthTrendsView DataContext="{Binding HealthTrendsViewModel}"/>` exactly as the classic embed (line 647) does. The VM is a DI singleton with its own lifecycle (recon: disposal ownership uncertain but PRE-EXISTING — do not change it; note in report).

- [ ] **Steps:** lift, scope-fix, build → 0 warnings each, commit: `feat(pages): bottleneck and health-trends widgets`

---

### Task 5: Factory registry rewrite + RebuildChildren disposal hook

**Files:**
- Modify: `src/NexusMonitor.UI/Controls/WidgetTileFactory.cs`
- Modify: `src/NexusMonitor.UI/Controls/PageHostControl.cs`

**Interfaces:**
- Factory contract (unchanged signature): `static Control Create(WidgetInstance widget)` — never null, never throws. Resolution: alias map first (cpuChart→cpuCard, memoryChart→memoryCard), then TypeId switch: healthScore→`HealthScoreWidget`, cpuCard/memoryCard/diskCard/gpuCard→`SubsystemCardWidget` with `Bind(DataContextProperty, new Binding("CpuCard"))` etc., bottleneck→`BottleneckWidget`, topConsumers→`TopConsumersWidget`, recommendations→`RecommendationsWidget`, predictions→`PredictionsWidget`, healthTrends→`HealthTrendsWidget`; UNKNOWN → existing `WidgetTile` placeholder path byte-identical (spec §8). KnownTitles dictionary is superseded by the switch — remove it or repurpose for the placeholder titles; keep `StringComparer.Ordinal` explicitness if a dictionary remains (P3 triage nit).
- Disposal hook (P2/P3 carried requirement): `RebuildChildren` disposes outgoing children before `Children.Clear()`:
```csharp
        foreach (var child in Children)
            (child as IDisposable)?.Dispose();
        Children.Clear();
```
Today no widget is IDisposable (pure bindings) — the hook is the guard for when one becomes so. Add the same sweep to a `DetachedFromVisualTree` override? NO — out of scope, note only.

- [ ] **Steps:** implement, build → 0 warnings, full suite 470/470 (Core untouched), commit: `feat(pages): live widget registry with legacy aliases and child-disposal hook`

---

### Task 6: Tidy button (wires CompactPage) + Core test gaps

**Files:**
- Modify: `src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs` (TidyLayoutCommand)
- Modify: `src/NexusMonitor.UI/Views/DashboardView.axaml` (toolbar button between Add widget and Undo: `<Button Classes="nx-btn" Command="{Binding TidyLayoutCommand}"><TextBlock Text="Tidy" FontSize="{DynamicResource NxFont12}"/></Button>`)
- Modify: `tests/NexusMonitor.Core.Tests/Pages/PageEditSessionTests.cs` (two new tests)

**Interfaces:** `[RelayCommand] private void TidyLayout()` → `if (_editSession is null) return; _editSession.CompactPage(); AfterEdit();` with XML doc. Core tests (TDD — run RED first for the NEW test names): `Remove_UnknownId_IsNoOp_NoUndoEntry` (mirror of the Move version) and `CompactPage_ClosesGaps_AndPushesUndoEntry` (build a page, remove a top widget leaving a gap, CompactPage, assert rows pulled up + CanUndo).

- [ ] **Steps:** tests RED → implement/wire → focused green → full suite (expect 472/472) → 0 warnings → commit: `feat(pages): Tidy action wiring CompactPage + session test gaps closed`

---

### Task 7: Visual equivalence smoke + changelog

**Files:** `CHANGELOG.md`

- [ ] **Step 1:** Publish; flag ON; launch; screenshot the engine dashboard at factory default. Then flag OFF, relaunch, screenshot classic. INSPECT both (Read): the engine layout must present the same ten content areas with live data (ring shows a real score, cards show real levels, consumers list real processes, trends chart renders). Cosmetic layout differences (grid rects vs classic stacking) are expected; missing/blank/broken widgets are failures. If the 72px-row proportions make any widget unusable (clipped ring, squashed chart), adjust ONLY the factory JSON rects (not PageMetrics) and note.
- [ ] **Step 2:** Edit-mode gesture pass on REAL widgets: drag the bottleneck widget, resize trends, remove predictions + undo, add a second CPU card via gallery (Escape check via osascript key code 53), Tidy closes a gap you create, Save → relaunch persistence. Screenshots at each step; report what you SEE.
- [ ] **Step 3:** Alias check: hand-write a layout file containing legacy `nexus.widget.cpuChart`, launch flag-on: renders the CPU card (not "Unknown widget"). Then delete the layouts file, restore flag false, kill processes, confirm pristine.
- [ ] **Step 4:** CHANGELOG Unreleased/Added: `- Page engine now renders the real Dashboard: all ten sections are live, draggable widgets (experimental flag).` Commit: `docs: changelog for live dashboard widgets; phase-4 equivalence evidence`

---

## Done means

10 live widgets rendering real data through the engine; factory default visually equivalent to classic; gestures/Tidy/gallery/persistence verified on real widgets with screenshots; legacy TypeIds alias cleanly; disposal hook in place; 472/472, 0 warnings; classic path untouched. Next plans: Performance-tab extraction on the same registry, then Phase 5 (profiles + theme bundling + export).
