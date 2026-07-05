# Page Engine Phase 5 (Workspace Profiles: Layout + Theme Bundling, Export/Import) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Named workspace profiles that bundle page layouts + a theme reference, switchable at runtime without restart, exportable/importable as versioned `.nexusprofile` files — spec §7, adapted to what exists (pop-outs are Phase 6, so `PopOutStates` is schema-reserved but unused).

**Architecture (grounded in the theme recon):** A theme snapshot is exactly the existing 13-field `ThemePreset` + `SmartTintEnabled` — all already `AppSettings` fields with proven JSON round-trip. `ApplyAllVisuals()` (SettingsViewModel) is the atomic appearance-apply entry point; profile switching bulk-sets the appearance properties `ApplyPreset`-style then calls it once — no new theming machinery. New Core: `WorkspaceProfile` records (the name avoids the existing, unrelated `PerformanceProfile` domain) + `WorkspaceProfileStore` (profiles/ dir, one JSON per profile, `active-profile` pointer file, house atomic-write pattern, corrupt→.bak, and one-time migration of P3's `pages/dashboard.json` into a "Default" profile). Layout switching flows through a `WorkspaceProfileSwitchedMessage` → `DashboardViewModel` reloads `EnginePage` from the store.

**Tech Stack:** .NET 8, Avalonia 11.2.3 (StorageProvider file dialogs for export/import), existing Core.Pages + theming stack (PRs #12-#15).

## Global Constraints

- All standing rulings (FluentAssertions; usings-blank-namespace; XML docs on EVERY public member; TDD w/ captured RED for Core; ICustomHitTest rule for custom-drawn interactive controls — none planned here).
- **Classic-freeze policy (P4 M2, now binding): the classic Dashboard sections in DashboardView.axaml are FROZEN — any change there must be mirrored into the corresponding `Widgets/*.axaml`.** No task in this plan touches them.
- **Widget-Dispose invariant (P4 I1):** Task 3 adds the guard comment at the `RebuildChildren` disposal hook: a widget's `Dispose` may release only widget-owned resources, never its bound VM (shared singletons are load-bearing).
- OUT of this plan's scope (deliberate): per-widget `ConfigJson` channel + catalog `DefaultSize/MinSize/Category` (P4 I2/M3 — they belong to the sensor-widget/config phase and need their own design moment); title-bar profile dropdown (Settings-only this phase); single-page export granularity (full-profile + theme-only ship now); pop-out state (Phase 6).
- Naming: `WorkspaceProfile` everywhere — never bare `Profile` (collides with `PerformanceProfile`/`ActiveProfileId`, an unrelated Phase-17 domain).
- Persistence pattern (house standard, mirror `PageLayoutStore`): base dir `ApplicationData/NexusMonitor/profiles`, ctor `(string? baseDirectory = null)` for tests, 250 ms restart-on-call debounce under a lock, tmp+`File.Move(overwrite:true)`, `Dispose()` flush, broad `catch (Exception)` in background writers, corrupt file → `.bak` + safe fallback.
- Envelope: `{"schemaVersion":1,"profile":{...}}` camelCase via a `WorkspaceProfileSerializer` mirroring `PageLayoutSerializer` exactly (never-throws `TryDeserialize(string?, out, out)`, missing/newer schemaVersion rejected, null/whitespace guarded — Phase-1/3 hardening lessons pre-applied).
- Theme fields (the snapshot surface, byte-named from AppSettings): `ThemeMode, AccentColorHex, TextAccentColorHex, CustomWindowBgHex, CustomSurfaceBgHex, CustomSidebarBgHex, IsGlassEnabled, GlassOpacity, BackdropBlurMode, IsSpecularEnabled, SpecularIntensity, FontFamily, FontSizeMultiplier, SmartTintEnabled`.
- Build/test: `export DOTNET_ROOT=$HOME/.dotnet`; suite currently 472/472. Branch: `feat/page-engine-phase5-profiles` from main.
- Smoke facts: cliclick clicks+drags work (1:1); Escape via osascript key code 53; file dialogs may resist scripting — the smoke task has a headless fallback (drive export/import through the store API in a scratch console if dialogs can't be automated, with the dialog path left for owner eyeball).

---

### Task 1: Core records + serializer (`WorkspaceProfile`, `ThemeRef`, `WorkspaceProfileSerializer`)

**Files:**
- Create: `src/NexusMonitor.Core/Pages/WorkspaceProfile.cs`
- Create: `src/NexusMonitor.Core/Pages/WorkspaceProfileSerializer.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/WorkspaceProfileSerializerTests.cs`

**Interfaces (later tasks consume exactly these):**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>Appearance snapshot for a profile: either a preset reference or a full embedded
/// snapshot (the 13 ThemePreset fields + SmartTintEnabled). Exactly one of PresetId/Snapshot
/// should be set; when both are present, Snapshot wins.</summary>
public sealed record ThemeRef(string? PresetId = null, ThemeSnapshot? Snapshot = null);

/// <summary>Portable appearance state. Field names/types mirror the AppSettings appearance
/// surface byte-for-byte so capture/apply are trivial copies.</summary>
public sealed record ThemeSnapshot(
    string ThemeMode, string AccentColorHex, string TextAccentColorHex,
    string CustomWindowBgHex, string CustomSurfaceBgHex, string CustomSidebarBgHex,
    bool IsGlassEnabled, double GlassOpacity, string BackdropBlurMode,
    bool IsSpecularEnabled, double SpecularIntensity,
    string FontFamily, double FontSizeMultiplier, bool SmartTintEnabled);

/// <summary>A named workspace: page layouts + appearance. PopOutStates is schema-reserved for
/// Phase 6 (pop-out windows) and always empty today.</summary>
public sealed record WorkspaceProfile(
    string Name,
    IReadOnlyDictionary<string, PageLayout> Pages,   // key = pageId
    ThemeRef Theme,
    IReadOnlyList<PopOutState> PopOutStates);         // reserved, empty in Phase 5
```

`WorkspaceProfileSerializer`: `const int CurrentSchemaVersion = 1`; `Serialize(WorkspaceProfile)`; `TryDeserialize(string?, out WorkspaceProfile?, out string?)` — never throws; rejects null/whitespace input, missing `profile`, missing/null schemaVersion (int?), newer schemaVersion; camelCase, WriteIndented. (Copy `PageLayoutSerializer`'s post-hardening shape including the `[JsonIgnore]`-free model — these records have no computed properties.)

- [ ] **Step 1 (RED):** Tests: round-trip a profile with two pages (factory dashboard + a modified copy), a Snapshot ThemeRef, and empty PopOutStates — full structural equality via a local comparer (pages compared with the existing `PageLayoutComparer` approach); envelope shape test (`schemaVersion` + `profile` keys); hostile-input Theory reusing Phase-3's 8-row set adapted (`empty, garbage, "null", missing profile, null profile, future version, missing schemaVersion, null input`). Run filter → CS0246 RED captured.
- [ ] **Step 2:** Implement records + serializer per the shapes above.
- [ ] **Step 3:** Filter green; full suite (expect 472 + ~10 = 482ish — report exact).
- [ ] **Step 4:** Commit `feat(pages): WorkspaceProfile records and versioned serializer`

---

### Task 2: `WorkspaceProfileStore` (Core, TDD)

**Files:**
- Create: `src/NexusMonitor.Core/Pages/WorkspaceProfileStore.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/WorkspaceProfileStoreTests.cs`

**Interfaces (Task 4 consumes):** `sealed class WorkspaceProfileStore : IDisposable`, ctor `(string? baseDirectory = null, string? legacyPagesDirectory = null)` (second param for migration testing; production default = the P3 pages dir).
- `IReadOnlyList<string> ListProfiles()` (file-system scan, sorted, `.json` stems)
- `string ActiveProfileName { get; }` (from `active-profile` pointer file; falls back to "Default")
- `WorkspaceProfile LoadActive()` / `WorkspaceProfile? Load(string name)` (corrupt → `.bak` + null; LoadActive falls back to a factory Default: all `BuiltInPageLayouts` pages + `ThemeRef(PresetId: null)` meaning "leave appearance as-is")
- `void Save(WorkspaceProfile profile)` (debounced; file name = sanitized profile name — restrict to `[A-Za-z0-9 _-]`, reject others via `ArgumentException` documented as caller-validated)
- `void SetActive(string name)` (pointer write, immediate not debounced)
- `void Delete(string name)` (not the active one — throws InvalidOperationException; documented)
- `bool MigrateLegacyIfNeeded()` — when profiles dir has no profiles AND `{legacyPagesDirectory}/dashboard.json` exists: build "Default" profile from it (theme = `ThemeRef()` neutral), save synchronously, rename legacy file to `dashboard.json.migrated`, set active pointer. Returns whether migration ran.

- [ ] **Step 1 (RED):** Tests: save→dispose→reopen round-trip; active pointer persistence; LoadActive factory fallback when empty; corrupt profile → .bak + Load returns null; Delete guards active; migration happy path (seed a legacy pages/dashboard.json via PageLayoutSerializer, assert Default profile contains it + legacy renamed + active set) and migration no-op when profiles exist. Temp-dir per test (IDisposable pattern from PageLayoutStoreTests).
- [ ] **Step 2:** Implement per house pattern (single `_pending` is fine — saves are whole-profile; document like PageLayoutStore).
- [ ] **Step 3:** Filter green; full suite green (report count).
- [ ] **Step 4:** Commit `feat(pages): WorkspaceProfileStore with active pointer and legacy-pages migration`

---

### Task 3: Small hardening bundle (three unrelated one-pagers, one commit)

**Files:**
- Modify: `src/NexusMonitor.UI/Services/ThemePresetService.cs` (or wherever `SaveCurrentAsPreset` writes custom-themes.json — locate first)
- Modify: `src/NexusMonitor.UI/Controls/PageHostControl.cs` (comment only)

1. **custom-themes.json atomicity** (recon finding): replace the direct `File.WriteAllText` with the house tmp+move idiom + broad catch (no debounce needed — explicit user action). Keep behavior otherwise identical.
2. **Dispose-invariant guard comment** (P4 I1) above the RebuildChildren disposal loop: `// INVARIANT: a widget's Dispose may release only widget-owned resources — NEVER its bound` / `// VM. Widgets bind shared singleton VMs (card VMs, HealthTrendsViewModel); disposing those` / `// from a rebuild would corrupt live state app-wide.`
3. Verify build 0 warnings, suite green; commit `fix(theme): atomic custom-preset writes; docs: widget-dispose invariant at rebuild hook`

---

### Task 4: Profile coordinator — Settings UI, switch path, Dashboard reload

**Files:**
- Modify: `src/NexusMonitor.UI/Messages/NavigationMessages.cs` (`public record WorkspaceProfileSwitchedMessage(string Name);`)
- Modify: `src/NexusMonitor.UI/App.axaml.cs` (register `WorkspaceProfileStore` singleton; call `MigrateLegacyIfNeeded()` during startup before VMs resolve)
- Modify: `src/NexusMonitor.UI/ViewModels/SettingsViewModel.cs` + `Views/SettingsView.axaml` (a "Workspace profiles" card ABOVE the Experimental block: active-profile ComboBox over `ListProfiles()`, "Save current as…" (TextBox+Button; captures current theme via a `ThemeSnapshot` built from `_settings.Current` + the ACTIVE profile's pages from the store), "Delete" (disabled for active), Export/Import buttons wired in Task 5 — stub commands now)
- Modify: `src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs` (register for `WorkspaceProfileSwitchedMessage` → reload `EnginePage` from the profile store's active profile pages["dashboard"]; ALSO: `SaveEdit` now persists into the ACTIVE profile via the profile store instead of/in addition to PageLayoutStore — read the current wiring and route layout persistence through the profile store, keeping `PageLayoutStore` only if trivially removable is too risky this task: prefer routing DashboardViewModel's load+save fully to WorkspaceProfileStore and leaving PageLayoutStore unused-but-present for the migration source)

Switch semantics (SettingsViewModel command): `store.SetActive(name)` → load profile → if `Theme.Snapshot` present: bulk-set the 14 appearance VM properties with `_suppressApply` then `ApplyAllVisuals()` + `UpdateSurfaceSwatches()` (mirror `ApplyPreset`'s structure at ~SettingsViewModel.cs:909-942); if `PresetId` present: call the existing `ApplyPreset` path with that preset; if neutral (both null): appearance untouched. Then `WeakReferenceMessenger.Default.Send(new WorkspaceProfileSwitchedMessage(name))`. Restart-free.

- [ ] Steps: implement, build 0 warnings, suite green, manual-note where UI verification lands in Task 6. Commit `feat(pages): workspace profile switching — Settings card, theme apply, dashboard reload`

---

### Task 5: Export / import (.nexusprofile)

**Files:**
- Modify: `src/NexusMonitor.UI/ViewModels/SettingsViewModel.cs` (+ SettingsView.axaml buttons already stubbed)

- Export: `StorageProvider.SaveFilePickerAsync` (get TopLevel from the main window — find the codebase idiom or `App.Current.ApplicationLifetime` desktop MainWindow), default name `{profile}.nexusprofile`, content = `WorkspaceProfileSerializer.Serialize(...)`. Theme-only export: second button — serializes a profile named `{name} (theme)` with EMPTY Pages dict and the ThemeRef (documented convention; import handles pages-empty profiles by keeping current layouts).
- Import: `OpenFilePickerAsync` (filter *.nexusprofile;*.json) → read → `TryDeserialize` → on failure, toast the error; on success import-as-new: if name exists, suffix ` (2)`, ` (3)`…; NEVER overwrite; NEVER auto-activate — user switches explicitly. Unknown page WidgetTypeIds inside are inherently preserved (registry renders placeholders — spec §8 already handles it).
- [ ] Steps: implement, build 0 warnings, suite green. Commit `feat(pages): .nexusprofile export and import-as-new`

---

### Task 6: Smoke + changelog

- [ ] Flag ON. Baseline screenshot. Settings → save current as "Test B". Switch appearance (pick a visibly different preset, e.g. Arctic if current is dark), edit dashboard layout (move a widget), Save. Save current as… no — VERIFY semantics: "Test B" captured at save-time; now switch active back to "Default": theme AND layout revert (screenshots of both states). Switch to "Test B" again: both return. Restart: active profile + its look/layout persist.
- [ ] Export "Test B" (if the file dialog resists scripting: fall back to driving `WorkspaceProfileStore`+`WorkspaceProfileSerializer` through a scratch console app to produce/consume the file, and report the dialog path as owner-eyeball); delete "Test B"; import the file: appears as "Test B" (or suffixed), NOT active; switch to it: theme+layout intact.
- [ ] Migration check: pre-seed a legacy pages/dashboard.json (distinctive layout), clear profiles dir, launch: Default profile contains the legacy layout; legacy file renamed .migrated.
- [ ] Theme-only export/import: import a theme-only file → switching to it changes appearance but keeps current layouts.
- [ ] Cleanup: restore pristine (profiles dir cleared or left with only Default per owner preference — leave Default, delete test artifacts; flag false; no processes).
- [ ] CHANGELOG Unreleased/Added: `- Workspace profiles (experimental): save, switch, export and import named layout+theme bundles (.nexusprofile).` Commit with evidence note.

---

## Done means

Profiles round-trip (save/switch/restart) with restart-free theme+layout application; export/import as-new works with hostile-input safety; legacy pages migrate once; custom-themes writes atomically; Dispose invariant documented; suite ~482+/482+, 0 warnings; classic path untouched. Next: Performance-tab extraction, pop-outs (consuming PopOutStates), per-widget config design (P4 I2), title-bar profile dropdown.
