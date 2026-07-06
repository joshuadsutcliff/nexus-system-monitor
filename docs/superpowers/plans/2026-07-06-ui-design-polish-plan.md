# Phase 8 (UI Design Polish) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the professional look per spec `docs/superpowers/specs/2026-07-06-ui-design-polish-design.md`: elevation/depth tokens on all cards, a KDE-Plasma-6-style user-adjustable animation system with central duration tokens, widget-level dynamic type, an alignment+logo pass, and working per-OS acrylic groundwork.

**Architecture:** Token-first — new AppSettings fields feed a small `MotionSettingsService` (duration tokens) and `DepthIntensity` scaling; XAML consumes tokens only. All existing scattered Transitions migrate to tokens; per-effect gating via bool settings. No new pages; one new Settings section (ANIMATIONS) + two toggles in existing sections.

**Tech Stack:** unchanged (.NET 8, Avalonia 11.2.3). Branch: `feat/ui-design-polish` from post-P7 main.

## Global Constraints

- All standing rulings (FluentAssertions; usings above namespace + blank line; XML docs on every public member; TDD w/ RED for Core; TreatWarningsAsErrors; widget-Dispose-never-touches-VM; ICustomHitTest rule for custom-drawn interactive controls).
- New AppSettings fields (exact names/defaults from spec §2/§1/§3): `AnimationSpeed` double 1.0 (0.0–2.0), `AnimatePageTransitions`/`AnimateHoverEffects`/`AnimatePopOutMotion`/`AnimateEditChrome`/`AnimateValueChanges`/`AnimateSpecularShimmer` bool true, `DepthIntensity` double 0.5 (0–1), `ScaleTextWithWidgetSize` bool true. All must round-trip (Core serialization tests).
- Duration tokens: `MotionFast`=120ms, `MotionBase`=180ms, `MotionSlow`=280ms base values, divided by AnimationSpeed (speed 0 ⇒ zero-duration/disabled). Existing hardcoded transition durations (100/120/150/180/200/250/300ms sites, per recon) map: ≤120→Fast, 150–200→Base, ≥250→Slow.
- Elevation tokens: `ElevationRaised`/`ElevationFloating`/`ElevationModal` BoxShadows per theme variant, alphas scaled by DepthIntensity at apply time. Default look = subtle-Apple.
- Layout-parity guard: the P7 side-by-side baseline (factory default arrangement) must be visually unchanged except shadows/motion — final smoke re-shoots and compares.
- Suite baseline: post-P7 main. Every task: build 0 warnings + suite green.

---

### Task 1: Settings plumbing + MotionSettingsService (Core TDD + UI service)

**Files:** Modify `src/NexusMonitor.Core/Models/AppSettings.cs` (9 new fields, XML-documented, defaults above); Create `src/NexusMonitor.UI/Services/MotionSettingsService.cs`; Modify `src/NexusMonitor.UI/App.axaml.cs` (instantiate + apply at startup); Tests extend the existing AppSettings serialization test file.
**Produces:** `MotionSettingsService` — reads AppSettings, writes `MotionFast/Base/Slow` TimeSpan resources into Application resources, `void Apply(AppSettings)`, `event Action? MotionChanged`; static helper `bool EffectEnabled(AppSettings, MotionEffect effect)` w/ `MotionEffect` enum (PageTransitions, HoverEffects, PopOutMotion, EditChrome, ValueChanges, SpecularShimmer). AnimationSpeed=0 ⇒ zero durations AND EffectEnabled false for all.
- [ ] RED: serialization round-trip tests for all 9 fields (+ defaults test) fail/compile-error → implement fields → green → service + startup wiring → build 0 warnings → commit `feat(ui): animation/depth settings plumbing and motion token service`

### Task 2: Elevation tokens + card depth + hover lift

**Files:** Modify `Themes/Colors.axaml` (elevation BoxShadow tokens both variants), `Themes/Controls.axaml` + `Controls/WidgetTile.axaml` + `Widgets/*.axaml` chrome borders + settings-card styles + `CommandPaletteControl.axaml`/`NotificationToast.axaml` (align existing shadows to tokens); hover-lift style on widget tiles (view-mode only — bind gate on `!IsEditMode` via the tile's existing DataContext reach; transitions use MotionFast + HoverEffects gating from Task 1).
**DepthIntensity:** applied by a resource-rewrite in MotionSettingsService.Apply (scale shadow alphas) — extend the service, document.
- [ ] Implement → visual spot screenshot (tiles at rest + hover) → build 0 warnings, suite green → commit `feat(ui): elevation token system, card depth, hover lift`

### Task 3: Animation migration + ANIMATIONS settings section

**Files:** Migrate every recon-listed Transition site to tokens + per-effect gating: `MainWindow.axaml` (nav drag, page CrossFade→PageTransitions), `Themes/Controls.axaml` (buttons/datagrid/listbox→HoverEffects), `SettingsView.axaml` swatches, `NotificationToast.axaml`, `CommandPaletteControl.axaml`; pop-out open scale-in 0.97→1 + return (PopOutCoordinator/WidgetPopOutWindow → PopOutMotion); edit-chrome/gallery fades (EditChrome); shimmer timer → respects AnimateSpecularShimmer && AnimationSpeed>0 (MainWindow.axaml.cs timer gate; pause when disabled). New ANIMATIONS section in `SettingsView.axaml` after CRYSTAL GLASS + SettingsViewModel bindings (slider snap Off/0.5/1/1.5/2 + six toggles), persisting via existing settings save path, calling MotionSettingsService.Apply on change.
- [ ] Implement → manual check: speed slider visibly changes hover/page-transition speeds; 0 = everything instant, shimmer stopped → build 0 warnings, suite green → commit `feat(ui): central motion tokens everywhere, per-effect gating, Animations settings`

### Task 4: Dynamic type in widget tiles

**Files:** Create attached-property scaler (e.g. `Controls/DynamicTypeScale.cs`) computing step scale S/M/L/XL from tile bounds (thresholds by pixel area, hysteresis so resize doesn't jitter); apply to tile headline/value TextBlocks in `Widgets/*.axaml` chrome (value + title only, not body text); `ScaleTextWithWidgetSize` toggle in TYPOGRAPHY section; interacts multiplicatively with global FontSizeMultiplier.
- [ ] Implement → screenshot: same widget at 2 sizes shows scaled type; toggle off restores fixed sizes → build 0 warnings, suite green → commit `feat(ui): dynamic type scaling in widget tiles`

### Task 5: Alignment audit + logo treatment

**Files:** audit pass over `MainWindow.axaml` (titlebar/sidebar), `DashboardView.axaml` chrome, `SettingsView.axaml` rows, gallery: consistent gutter/padding tokens, header baselines, right-edge control alignment; logo: 28×28 (use nexus-icon-256 downscale — verify crispness @2x), spacing 10→12, optical alignment with sidebar edge. Produce fix list + BEFORE/AFTER screenshots (conductor reviews the logo shots personally).
- [ ] Implement → screenshots → build 0 warnings, suite green → commit `fix(ui): alignment audit and logo treatment`

### Task 6: Sidebar & controls — System Settings parity

**Files:** Modify `MainWindow.axaml` (sidebar structure/styles: row height, icon chips, selection pill, hover state, measured width — all values from the captured reference packet `p8-macos-ref.md` in the session scratchpad, cited per value), `Themes/Controls.axaml` (macOS-style switch restyle for ToggleSwitch/CheckBox-as-switch used in Settings — track/knob proportions + accent on-state + knob-travel transition on MotionFast, gated by HoverEffects/EditChrome as appropriate), `SettingsView.axaml` (row alignment grammar: label left / control right, grouped rounded sections, hairline separators — apply to 2-3 sections as the pattern-setter; full sweep only if mechanical).
**Reference-driven:** every changed metric cites the reference packet; where the packet lacks a value, match the nearest existing Nexus token rather than inventing.
- [ ] Implement → BEFORE/AFTER screenshots (sidebar + a settings section + switches) → build 0 warnings, suite green → commit `feat(ui): System Settings sidebar parity and macOS-style switches`

### Task 7: Acrylic groundwork

**Files:** Create `Services/BackdropService.cs` (per-OS TransparencyLevelHint chains: macOS AcrylicBlur→Vibrancy→None, Windows Mica→AcrylicBlur→None, Linux None; reads BackdropBlurMode; verifies `ActualTransparencyLevel` and adjusts GlassBg alpha compositing when rejected — document per-OS behavior); wire into MainWindow init + BackdropBlurMode settings change. Default behavior must render identical-to-today when mode="None"; "Acrylic" now actually does something on supported OSes.
- [ ] Implement → screenshots mode None vs Acrylic on macOS → build 0 warnings, suite green → commit `feat(ui): real per-OS backdrop acrylic behind BackdropBlurMode`

### Task 8: Smoke + changelog

- [ ] Live smoke: AnimationSpeed 0 ⇒ zero motion anywhere (incl shimmer); speed 2 visibly slow; DepthIntensity slider changes shadows live; dynamic type on resize; hover lift; acrylic toggle on/off; pop-out open/close motion; P7-baseline layout regression shot (factory default arrangement unchanged); settings round-trip after relaunch; cleanup pristine. CHANGELOG Unreleased/Added block. Commit `docs: changelog for UI polish`.

## Done means
All spec §7 acceptance items evidenced (conductor-verified screenshots incl. logo before/after); opus whole-branch final; PR; CI ×3 green; merge. Remaining backlog after: Performance-tab conversion, sensor tier, full Liquid Glass look, context menus, title-bar dropdown.
