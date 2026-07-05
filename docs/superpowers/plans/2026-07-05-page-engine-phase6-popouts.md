# Page Engine Phase 6 (Pop-Out Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tear any dashboard widget into its own OS window (spec §6): the page keeps a reserved placeholder in its grid slot, the window's geometry persists per-widget and restores on launch (clamped on-screen), closing the window returns the widget to its slot, and a soft cap of 6 concurrent pop-outs shows a footprint notice. The existing Overlay feature is untouched.

**Architecture (grounded in recon):** Persistence rides ENTIRELY on the existing `WidgetInstance.PopOut` field (identity-correct since Phase 1, serialized inside profile Pages already). **Conductor rulings:** (1) `WorkspaceProfile.PopOutStates` stays in the schema exactly as-is but is documented deprecated-unused — removing it would churn ctor binding for zero gain; (2) a popped-out widget's grid slot shows a **placeholder tile** (the WidgetTile idiom: "Popped out — {Name}" / "Close its window to return it here") — the slot stays reserved per spec; (3) **switching workspace profiles first persists then closes all open pop-outs** (the outgoing profile keeps them; the incoming profile's own pop-outs restore). New pieces: a Core `SetPopOut` engine op + pure screen-clamp math (both TDD), a decorated plain `WidgetPopOutWindow`, and a `PopOutCoordinator` owned by DashboardViewModel tracking open windows by InstanceId.

**Tech Stack:** .NET 8, Avalonia 11.2.3 (Screens API for clamping), existing stack (PRs #12-#16).

## Global Constraints

- All standing rulings (FluentAssertions; usings/blank/namespace; XML docs everywhere public; TDD w/ RED for Core; classic-freeze mirror policy; widget-Dispose-never-touches-VM invariant; **ICustomHitTest rule** for any custom-drawn interactive control — the placeholder tile is NOT custom-drawn, no new exposure).
- Recon facts: `PopOutState(bool IsPoppedOut, int X, int Y, int Width, int Height, bool Topmost)` (Phase 1, complete); factory instantiates fresh controls per call over shared VMs (dual-instantiation already the pattern — a popped window hosting a second control instance is safe BUT the page shows a placeholder instead, so normally only one live instance exists); `PageHostControl` currently ignores PopOut (must branch); MainWindow's bounds save/restore is the nearest persistence precedent and has NO off-screen clamping (Phase 6 builds the clamp helper — pure math in Core); App shutdown choreography has no pop-out step (insert persist+close EARLY, before service teardown, since pop-outs host live widget bindings); OverlayWindow is never explicitly closed (do NOT copy that — pop-outs need explicit close-with-persist).
- No new NuGet deps. Suite baseline: whatever Phase 5 lands (507/507 + T6's count). Branch: `feat/page-engine-phase6-popouts` from post-P5 main.
- Smoke facts: cliclick clicks/drags/typing OK (1:1); Escape via osascript key code 53; secondary windows screenshot via full-screen capture or `screencapture -l <windowid>`.

---

### Task 1: Core — `SetPopOut` engine op + screen-clamp math (TDD)

**Files:** Modify `src/NexusMonitor.Core/Pages/PageLayoutEngine.cs` (one method); Create `src/NexusMonitor.Core/Pages/WindowGeometry.cs`; Tests in `tests/.../Pages/` (extend PageLayoutEngine tests file or sibling; new `WindowGeometryTests.cs`).

**Interfaces (Tasks 2-4 consume):**
- `static PageLayout PageLayoutEngine.SetPopOut(PageLayout page, Guid instanceId, PopOutState? popOut)` — replaces the widget's PopOut (null clears it); unknown id returns the same instance (no-op, matches Move/Remove semantics — the session/no-undo distinction doesn't apply: pop-out changes are NOT edit-session ops, they apply directly to the live page and persist immediately).
- `readonly record struct ScreenRect(int X, int Y, int Width, int Height)` + `static class WindowGeometry { static ScreenRect ClampToScreens(ScreenRect window, IReadOnlyList<ScreenRect> screens, ScreenRect fallback); }` — pure math: if `window` intersects any screen by at least 64px in both axes, shift it minimally so its title-bar region (top 32px) is fully on that screen; if it intersects nothing (monitor unplugged), center it inside `fallback` at min(window.size, fallback.size). Plain ints, zero Avalonia deps.
- Tests: SetPopOut set/clear/unknown-id-same-instance/round-trips-through-serializer; ClampToScreens on-screen-unchanged, partial-offscreen-shifted, fully-offscreen-centered-in-fallback, oversized-window-shrunk.

- [ ] RED (new test names fail/compile-error) → implement → green → full suite → commit `feat(pages): SetPopOut engine op and pure screen-clamp geometry`

### Task 2: `WidgetPopOutWindow` + `PopOutCoordinator`

**Files:** Create `src/NexusMonitor.UI/Windows/WidgetPopOutWindow.axaml`(+.cs); Create `src/NexusMonitor.UI/Services/PopOutCoordinator.cs`.

- Window: standard decorations (title = widget catalog Name), min size 240×160, content = card-chrome Border hosting `WidgetTileFactory.Create(widgetInstance)` — DataContext must be the DashboardViewModel (pass it in; the window sets its own DataContext so inherited bindings work exactly as on the page). NO Topmost in v1 (PopOutState.Topmost stays false; field reserved).
- Coordinator (plain class, owned by DashboardViewModel — not DI-registered; it needs the VM's page/save context): `IReadOnlyDictionary<Guid, WidgetPopOutWindow> Open`; `bool TryPopOut(WidgetInstance widget, Func<PixelRect-like current-screens> …)` — cap check (>= 6 open → toast footprint notice via the notification service, return false); creates window at PopOut geometry if set (clamped via WindowGeometry + live Screens list) else cascade default (240+32*n offset from main window); wires `window.Closing` → capture final geometry → callback `OnReturned(Guid, PopOutState cleared-but-geometry-kept)` so the VM clears IsPoppedOut but RETAINS X/Y/W/H for next time; `PersistAndCloseAll()` for shutdown/profile-switch (capture geometries → invoke persist callback per window with IsPoppedOut STILL TRUE → close without re-trigger via a suppression flag).
- [ ] Build 0 warnings; suite unchanged; commit `feat(pages): pop-out window and coordinator with cap and geometry capture`

### Task 3: Page placeholder + pop-out entry points + VM wiring

**Files:** Modify `WidgetTileFactory.cs` (placeholder branch), `PageHostControl.cs` (render placeholder for IsPoppedOut), `EditAdornerControl.cs` (zone 3: a pop-out glyph box in edit chrome, top-right beside ✕ — raises `PopOutRequested(Guid)`; ICustomHitTest already covers bounds), `DashboardView.axaml.cs` (wire event), `DashboardViewModel.cs` (own the coordinator; `PopOutWidget(Guid)` — SetPopOut(IsPoppedOut:true, geometry from prior state or default) → save active profile → TryPopOut; `OnPopOutReturned` → SetPopOut(cleared, geometry kept) → save; view-mode entry: ContextMenu/ContextFlyout on the widget tiles? RULING for v1: edit-chrome button ONLY — the view-mode context menu needs per-widget menu plumbing across nine controls; defer with a note. Spec lists both; scope-note it in the report).
- Placeholder: `WidgetTileFactory.Create` checks `widget.PopOut?.IsPoppedOut == true` FIRST → returns WidgetTile { Title = "{Name} (popped out)", Subtitle = "Close its window to return it here." } — before the TypeId switch, so it applies to every widget type uniformly.
- PageHostControl needs NO change if the factory handles it (children still 1:1 with Widgets — placeholder is just a different control). Verify and note; adorner chrome applies to the placeholder slot as normal (move/resize the reserved slot is legitimate).
- [ ] Build 0 warnings; suite unchanged; commit `feat(pages): placeholder tiles, edit-chrome pop-out button, VM coordination`

### Task 4: Restore-on-launch, shutdown persist, profile-switch rule

**Files:** `DashboardViewModel.cs` (after EnginePage loads — ctor and profile-switch reload — enumerate widgets with IsPoppedOut, TryPopOut each with clamping; BEFORE switching profiles: coordinator.PersistAndCloseAll + save outgoing), `App.axaml.cs` (ShutdownRequested: EARLY — before service teardown — DashboardViewModel exposes `PersistAndCloseAllPopOuts()`; call it via the resolved VM).
- Restore ordering: pop-outs restore only when UsePageEngine && flag-on && profile active — the same guard path as EnginePage.
- Profile-switch rule (conductor ruling): persist geometries into the OUTGOING profile (save), close all, then switch; incoming profile's IsPoppedOut widgets restore via the reload path. Document in the switch command.
- [ ] Build 0 warnings; suite green; commit `feat(pages): pop-out restore on launch, shutdown persistence, profile-switch close rule`

### Task 5: Smoke + changelog

- Flag on. Edit mode → pop out the CPU card via the new chrome button: window appears with LIVE data; page slot shows the placeholder (screenshots of both). Move the window somewhere distinctive. Quit (Cmd+Q) → relaunch: window restores at that position (screenshot). Close the window: widget returns to its slot live (screenshot), placeholder gone. Pop out 6 widgets → attempt a 7th: refused with footprint toast (screenshot). Profile switch with 2 pop-outs open: they close; switch back: they restore at their positions. Off-screen clamp: hand-edit the active profile's PopOut X to 20000, relaunch: window appears on-screen (clamped). Cleanup: return all, restore Default-only pristine state, flag off, no processes. CHANGELOG Unreleased/Added: `- Pop out any dashboard widget into its own window; positions persist per profile (experimental).` Commit CHANGELOG.md.

## Done means
Pop-out lifecycle (out → move → persist → restore → return) verified live with screenshots incl. clamp + cap + profile-switch semantics; suite green throughout; Overlay untouched; classic path untouched. Remaining after this phase: Performance-tab extraction; per-widget config channel design (P4 I2); view-mode context menus; title-bar profile dropdown; sensor-composable widgets (spec tier 2); flag removal (spec §12.7).
