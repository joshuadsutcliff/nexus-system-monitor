# Phase 8 — UI Design Polish (depth, motion, type, alignment)

**Goal (Josh, 2026-07-06):** the app should *look* professional, not just clean — modern Apple-style appearance: 3D-style depth on UI elements, animations (user-adjustable, KDE Plasma 6 style), dynamic font sizing, alignment fixes, logo placement fixed. Nothing may preclude the Liquid Glass ambition; this phase starts building toward it.

**Grounding:** the codebase already has theme token dictionaries (Colors/Typography/Controls.axaml), a 16-field ThemePreset (incl. GlassOpacity, SpecularIntensity, FontSizeMultiplier 0.8–1.5), GlassAdaptiveService (wallpaper-luminance→alpha), a 3-layer specular composite w/ 50ms shimmer timer, scattered Avalonia Transitions (100–300ms, no central control), 13 bundled fonts, BoxShadows on MainWindow/toasts only, `TransparencyLevelHint=None` (BackdropBlurMode setting currently cosmetic), zero reduced-motion handling. Phase 8 systematizes and exposes; it does not rebuild.

## 1. Depth & elevation system

- **Elevation tokens** in Themes: `ElevationFlat` (none), `ElevationRaised` (cards at rest — soft ambient shadow, Apple-subtle), `ElevationFloating` (hover/drag/overlays — larger blur + slight y-offset), `ElevationModal` (dialogs/pop-out chrome). Both theme variants (dark shadows lighter alpha; light theme slightly stronger).
- Applied to: widget tiles (WidgetTile + all Widgets/* card chrome), settings cards, gallery, command palette, toasts (align existing ad-hoc shadows to tokens), pop-out window inner chrome.
- **Hover lift micro-motion** on widget tiles in view mode: elevation Raised→Floating + translateY(-1px) + scale(1.005), duration token, honors animation settings. No lift in edit mode (adorner owns gestures).
- **Default intensity: subtle-Apple** (System Settings-like). `DepthIntensity` slider (0–1, default 0.5) in Appearance settings scales all shadow alphas/blurs — Josh's "bolder vs subtle" question resolved by making it adjustable with a subtle default.

## 2. Animation system (KDE Plasma 6 model)

- **AppSettings additions:** `AnimationSpeed` (double 0.0–2.0, default 1.0; 0 = all animations off; UI slider with snap points Off/0.5×/1×/1.5×/2× — KDE's global animation-speed control) + per-effect bools: `AnimatePageTransitions`, `AnimateHoverEffects`, `AnimatePopOutMotion`, `AnimateEditChrome`, `AnimateValueChanges`, `AnimateSpecularShimmer` (all default true).
- **Central duration tokens:** `MotionFast` (120ms), `MotionBase` (180ms), `MotionSlow` (280ms) resources, written at startup and on settings change as `base / AnimationSpeed` (0 → all transitions removed/zero-duration). A small `MotionSettingsService` owns the math and raises change events; every XAML Transition migrates from hardcoded 100–300ms values to the tokens.
- **Effects inventory (migrate + gate):** page CrossFade → PageTransitions toggle; button/list/datagrid hovers → HoverEffects; pop-out open (subtle scale-in 0.97→1) and return → PopOutMotion (new); edit-mode chrome fade-in + gallery slide → EditChrome (new); health-score/number eases → ValueChanges (new, cheap opacity/translate only — no layout thrash); prismatic shimmer timer → respects AnimateSpecularShimmer AND AnimationSpeed=0, and migrates off the 50ms DispatcherTimer to a paused-when-hidden animation loop.
- **Reduced motion:** no reliable cross-platform OS signal in Avalonia 11 — honest limit. `AnimationSpeed=0` is the accessibility path; Settings copy says so ("Turn off all animations").
- **Settings UI:** new "ANIMATIONS" section directly after CRYSTAL GLASS: global slider + the six effect toggles, Plasma-6-style.

## 3. Dynamic type

- Keep global `FontSizeMultiplier` (exists). Add **widget-level dynamic scaling**: headline values in widget tiles (score number, % values, card titles) scale with tile size — computed step scale (S/M/L/XL by tile pixel area, clamped, no continuous jitter) applied via attached property or DataTemplate trigger in the tile chrome. Off switch: `ScaleTextWithWidgetSize` (default true) in TYPOGRAPHY section.
- Alignment of the NxFont token scale: audit the ~12 sizes for consistent hierarchy; no new fonts (13 bundled already; the spec's custom-fonts list is shipped).

## 4. Alignment & logo pass

- **Spacing audit** (dashboard, settings, sidebar): consistent gutters (grid gap tokens), consistent card padding (one token), baseline alignment of section headers, right-edge alignment of controls in settings rows. Deliverable = fix list with before/after screenshots, executed as one mechanical task.
- **Logo:** titlebar 24×24 nexus-icon + "Nexus Monitor" text. Default treatment (Josh to veto/adjust on PR screenshots): optical centering fix in the titlebar row, bump to 28×28 @2x-crisp asset, spacing 10→12, remove any off-by-pixel margin vs the sidebar edge. Before/after screenshot required in PR.

## 5. Acrylic groundwork (Liquid Glass path)

- Make `BackdropBlurMode` real: "Acrylic" sets `TransparencyLevelHint` per-OS (macOS → AcrylicBlur/Vibrancy fallback chain; Windows → Mica→AcrylicBlur fallback; Linux → None) with the existing GlassOpacity/GlassAdaptiveService alpha pipeline compositing over it. "None" = today's opaque behavior. Per-OS gotchas isolated in one service; app must render correctly when the hint is rejected (Avalonia falls back silently — verify ActualTransparencyLevel and adapt GlassBg alpha).
- Explicitly experimental: default stays whatever renders identical-to-today; the toggle just starts working. Full Liquid Glass look = later phase.

## 6. Non-goals (this phase)

No new widgets/layout features; no theme-preset changes beyond new fields' defaults; no Performance-tab work; no icon redesign; no font additions.

## 7. Delivery & acceptance

Subagent-driven, per-task review gates, opus final, PR w/ before/after screenshots (conductor-verified), merge on green. New AppSettings fields round-trip (Core tests, TDD). All animations verifiably off at AnimationSpeed=0 (smoke). Elevation/motion/type changes must not regress the side-by-side default layout (spot screenshot vs P7 baseline). CI ×3 OS green; suite green throughout.

**Open items for Josh (defaults chosen, veto anytime):** (1) logo — exact gripe unknown; default treatment above ships behind before/after shots. (2) depth default = subtle w/ DepthIntensity slider.
