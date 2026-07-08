# Page Customization System — Design Spec

**Date:** 2026-07-04 · **Status:** Approved-pending-review · **Owner:** Josh (TheBlackSwordsman)
**Feature:** KDE-Plasma-style customization — windows, tiling, and pages — for every Nexus tab.

## 1. Goals and non-goals

Product goals this feature serves, in priority order:

1. **Multi-OS sameness** — identical UI and feature surface on Windows, macOS, and Linux; platform gaps expressed through capability gating, never through divergent UI.
2. **Not system-heavy** — view mode must cost no more than today's static tabs; all customization machinery is pay-only-when-used.
3. **Opt-in feature-rich** — defaults stay simple for basic users; the full power surface (sensor composition, feature widgets, profiles, theming) lives behind edit mode and the widget gallery.

**Non-goals (v1):** community gallery / hosted upload service for sharing profiles (deferred — file-based sharing only); UI test automation harness; converting every tab (v1 converts Dashboard + Performance; remaining tabs follow on the same engine in later releases).

## 2. Locked product decisions

| Decision | Choice |
|---|---|
| Scope model | Every tab eventually becomes an editable page; shipped layouts are factory defaults users can modify or reset |
| "Windows" | Both framed panels within pages AND pop-out OS windows per widget |
| Widget catalog | Two tiers: prebuilt panels + a sensor-composable custom widget |
| Persistence | Local, named profiles (layout + theme), export/import as files |
| V1 milestone | Full engine + edit mode + both widget tiers + pop-outs + profiles + export, delivered on Dashboard, Performance, and user-created pages |

## 3. Architecture overview

New Core namespace `NexusMonitor.Core.Pages` (model + engine + serialization + sensor catalog) and UI components in `NexusMonitor.UI` (PageHostControl, adorners, gallery, pop-out windows). No new dependencies. All layout logic is platform-neutral Core code; the UI layer only renders and forwards gestures.

### 3.1 Page model (Core, plain records)

- `PageLayout { PageId, Title, IconKey, GridColumns = 12, List<WidgetInstance> Widgets }`
- `WidgetInstance { InstanceId (guid), WidgetTypeId, GridRect (Col, Row, ColSpan, RowSpan), ConfigJson, PopOut: PopOutState? }`
- `PopOutState { IsPoppedOut, X, Y, Width, Height, Topmost }`

Rows are uniform-height cells (baseline cell height 72 px, scaled by the app font-size setting; final value tuned in phase 2 and then frozen as a styling constant); pages scroll vertically. Built-in pages ship as factory `PageLayout` JSON embedded as resources — the same format users edit. Reset-to-default reloads factory JSON. One rendering path for shipped and custom pages.

### 3.2 Layout engine (Core, pure logic, unit-tested)

`PageLayoutEngine` owns: placement validation, collision resolution (push-down, Plasma-style), vertical compaction, and drop-target hit-testing. Drag previews in the UI call the same engine methods that commit does — preview equals final behavior.

### 3.3 Rendering (UI)

`PageHostControl` (custom `Panel`) binds a `PageViewModel`, arranges children by `GridRect` over uniform cells, instantiates widget controls via the widget registry factory. View mode renders the same control count as today's static markup — the grid panel replaces fixed `Grid` XAML one-for-one.

### 3.4 Edit mode

Explicit per-page toggle (pencil button; `E` shortcut). View mode has zero edit overhead — adorner layer, gallery, and undo stack instantiate only when edit mode opens.

Edit chrome per widget: drag handle, corner+edge resize grips (snap to cells), remove, config gear. Page chrome: "Add widget" gallery flyout (searchable, categorized), undo stack (in-memory, per session), Save / Cancel (Cancel restores the pre-edit snapshot; Save commits to the active profile).

## 4. Widget system

### 4.1 Registry

Core: `WidgetDescriptor { TypeId, Name, Category, DefaultSize, MinSize, CapabilityRequirement (IPlatformCapabilities predicate or sensor availability), ConfigSchema }`. UI: factory map TypeId → (Control, ViewModel). Gallery lists only widgets whose capability requirement this platform satisfies.

Categories mirror the product: **Performance** (CPU/memory/disk/network/GPU charts), **Health** (score, trends, predictions, bottleneck), **Processes** (top-N, group summaries), **Features** (Alerts feed, Gaming Mode status, Disk Analyzer summary, Network Scanner results, Automation/Rules status, Auto-Balance activity, History sparklines, Diagnostics), **Info** (system info, uptime/clock, notes).

### 4.2 Tier 1 — prebuilt panels

Existing Dashboard/Performance sections extracted into self-contained widget UserControls reusing their current VMs (extraction, not rewrite). Zero configuration beyond placement.

### 4.3 Tier 2 — custom sensor widget

One widget type; config = `{ Visualization: line | area | bar | gauge | bignumber | table | textticker, SensorIds[], TimeWindowSeconds, Thresholds[] (value → color) }`.

### 4.4 Sensor catalog

`ISensorCatalog` (Core): browsable tree of `SensorDescriptor { SensorId (stable string path, e.g. "cpu/core3/usage"), DisplayName, Unit, Availability }`. Leaves adapt existing provider streams at the two canonical cadences (`MonitoringCadence.Fast/Normal`) — **no new sampling, no new platform code**. Sensor availability derives from platform capabilities + live provider data ("N/A" leaves are visible but unselectable-with-explanation, keeping the tree structurally identical across OSes).

## 5. Pages and navigation

Sidebar becomes a page registry: built-in pages + user-created pages (+ button → name, icon, blank or starter template). Reorder, rename, delete (built-ins: reset only, no delete). Page list persists in the active profile.

## 6. Pop-out windows

Any widget tears off into a plain Avalonia `Window` hosting the same control+VM (view mode: context menu; edit mode: chrome button). Page records `PopOutState`; pop-outs restore on launch with saved geometry. Closing returns the widget to its grid slot. Soft cap: 6 concurrent pop-outs, with a footprint notice beyond. The existing Overlay feature is unchanged and remains a special case.

## 7. Profiles, theming, sharing

- `Profile { Name, PageLayouts[], PopOutStates, ThemeRef }`. `ThemeRef` = preset name **or** embedded full theme snapshot (palette, glass, accent, font settings).
- **Preset palettes** (the shipped 19, from `BuiltInThemePresets.cs`, selectable via the existing Settings dropdown): Nexus Default, Clean Light, Neon, Cherry Blossom, Dark Sakura, Anime, Futuristic, Outer Space, Minimalist, Deep Dark, Magical, Techno, Ocean Depth, Sunset, Arctic, Dracula, Solarized Dark, Solarized Light, Nord.
- **Fonts:** not a fixed list — Settings enumerates every installed system font at runtime (SkiaSharp font manager) plus "(System Default)" and the bundled Inter, with a font-size multiplier. Theme snapshots embed the chosen family + multiplier; on import, a missing family falls back to (System Default) with a notice rather than failing.
- **Glass / transparency:** the existing Crystal Glass stack (glass + specular toggles, Smart Glass Tint with luminance-adaptive service, backdrop modes) is part of the theme snapshot and therefore travels with profiles. A richer **Liquid Glass** treatment (Apple-style translucent material design) is a desired future direction — explicitly noted here as wanted, design deferred to its own pass; nothing in this feature may preclude it (widget chrome and page backgrounds must stay material-agnostic).
- Switching profiles (title-bar dropdown + Settings) swaps layouts and theme in one action. Gaming Mode hook (optional setting): auto-switch to a chosen profile on activation, restore on exit.
- Export → single `.nexusprofile` JSON (schema-versioned); granularity: full profile / single page / theme only. Import validates schema, shows a preview, always adds as new entries (never overwrites); widgets unknown to this build/platform become placeholder tiles preserving slot + config (lossless round-trip).
- Sharing in v1 is file-based (any file channel). Hosted community gallery deferred (owner will evaluate free hosting later).

## 8. Persistence and resilience

- Profiles stored beside settings (`profiles/` dir, one JSON per profile + `active-profile` pointer), written debounced + atomic (temp-file + move, matching SettingsService), previous version kept as `.bak`.
- Corrupt/unreadable profile → fall back to factory defaults with a visible notice; never a blank app.
- Schema version field on every file; additive evolution preferred; migration hook required before any breaking change ships.
- Widget = bulkhead: a throwing widget VM or faulted sensor stream renders an inline error tile with retry; failures log via the existing crash-logging path; the page never crashes.

## 9. Footprint discipline (enforcement points)

- Widget VMs subscribe on page-activate, dispose on deactivate — via the existing `IActivatable` + window-visibility machinery. Non-visible pages hold zero subscriptions.
- Sensor widgets subscribe to shared cadence streams: N widgets add rendering cost only, zero sampling cost.
- Edit-mode machinery exists only while editing; view mode is structurally identical in cost to the pre-feature static tabs.
- Pop-outs are the single deliberate cost center (one compositor surface each) — hence the cap + notice.

## 10. Platform strategy

All engine/serialization/catalog code is portable Core. Widget capability gating reuses `IPlatformCapabilities` and sensor availability — same pages, same tree, honestly-gated leaves on every OS. Platform-authored profiles import cleanly elsewhere (placeholder tiles for unavailable widgets). No per-OS layout code.

## 11. Testing

Core xUnit (joins existing 400+ suite): layout engine (placement, collision push-down, compaction, hit-tests), serialization round-trips (unknown-widget preservation, schema versioning, corrupt-file fallback), sensor catalog gating, profile switching. UI: manual smoke checklist per OS (edit/drag/resize/gallery/pop-out/profile-switch/import-export); no UI automation harness this release.

## 12. Delivery phases (v1)

1. Core model + `PageLayoutEngine` + serialization + tests.
2. `PageHostControl` + view-mode rendering; Dashboard converted behind a feature flag (`Settings.EnablePageEngine`, default off).
3. Edit mode + adorners + gallery; prebuilt widget extraction (Dashboard, then Performance).
4. Sensor catalog + custom sensor widget.
5. Profiles + theme bundling + export/import.
6. Pop-out windows.
7. Flag removed; shipped defaults reproduce current layouts; docs + README update.

Each phase lands green on CI (3-OS matrix) before the next begins. Remaining tabs (Processes, Services, Alerts, …) convert in post-v1 releases on the same engine.

## 13. Risks

- **Custom Panel drag/resize UX polish** is the highest-effort UI work (adorner hit-testing, snapping feel). Mitigation: engine-driven previews keep logic testable; UX iterates behind the feature flag.
- **Widget extraction regressions** on Dashboard/Performance. Mitigation: factory defaults must visually reproduce current layouts before the flag is removed (side-by-side comparison during phase 7).
- **Profile schema lock-in** — export files live forever in the wild. Mitigation: schema version from day one; unknown-content preservation is a hard requirement, tested.
