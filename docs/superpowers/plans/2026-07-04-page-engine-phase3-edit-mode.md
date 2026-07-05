# Page Engine Phase 3 (Edit Mode: Store, Undo Session, Adorner Gestures, Gallery) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the flag-gated Dashboard page editable: enter edit mode, drag/resize tiles with ghost preview, remove/add widgets via an overlay gallery, undo, cancel, and save — persisting the layout to disk so it survives restarts. All on the existing placeholder tiles; real-widget extraction is the NEXT plan.

**Architecture:** Two new Core pieces, both TDD'd: `PageLayoutStore` (per-page JSON persistence mirroring SettingsService's debounce+atomic-write shape; load-or-factory-default with corrupt-file fallback) and `PageEditSession` (an undo stack over the immutable `PageLayout`, every mutation routed through `PageLayoutEngine`). UI side: `DashboardViewModel` gains observable edit state + commands; the header grows an edit toolbar; a new custom-drawn `EditAdornerControl` overlays `PageHostControl` in edit mode, owning all pointer interaction (ColorWheelControl's capture idiom, shared-geometry hit-testing) and drawing chrome + drag/resize ghosts; the add-widget gallery is an overlay panel per the CommandPaletteControl pattern (this codebase uses no Flyout/Popup).

**Tech Stack:** .NET 8, Avalonia 11.2.3, Core.Pages API (Phases 1-2, merged PRs #12/#13).

## Global Constraints

- `TreatWarningsAsErrors=true`, `Nullable=enable`, ImplicitUsings — zero warnings. No new NuGet dependencies.
- Owner rulings: FluentAssertions idiom; test files = usings, blank line, file-scoped namespace; XML `///` docs on EVERY public member; TDD with captured RED for all Core code.
- Exact existing API facts: `WidgetInstance(Guid InstanceId, string WidgetTypeId, GridRect Rect, string? ConfigJson = null, PopOutState? PopOut = null)` — field is `Rect`; `PageLayoutEngine.MoveWidget(page, id, target)` clamps + pins + push-down, returns SAME instance for unknown id; `RemoveWidget` no compaction; `PlaceWidget(page, widget)` clamps + push-down; `Compact(page)` closes gaps; `IsValidPlacement` is ADVISORY (preview legality) — place/move always commit. `PageLayoutSerializer.Serialize/TryDeserialize(string?, out PageLayout?, out string?)` never throws. `BuiltInPageLayouts.Load("dashboard")`. `PageGeometry.ToPixelRect(rect, availableWidth, gridColumns, cellHeight, gap)`, `CellWidth(...)`, `PixelRect(double X,Y,Width,Height)`. `PageMetrics.DefaultCellHeight=72`, `DefaultCellGap=12`.
- UI idioms (from recon, follow exactly): pointer gestures per `Controls/ColorWheelControl.cs:65-91` — hit-zone check BEFORE `e.Pointer.Capture(this)`, moves gated on `e.Pointer.Captured == this`, release always uncaptures; shared geometry helpers between hit-test and render; overlay panels per `Controls/CommandPaletteControl` (backdrop Border with PointerPressed-close + centered card with `e.Handled=true`, IsVisible-bound, no Popup); icon buttons `Classes="nx-btn-icon"`, ghost buttons `nx-btn-ghost`, action `nx-btn`/`nx-btn-accent` (Themes/Controls.axaml); messages = positional records in `Messages/NavigationMessages.cs`, `WeakReferenceMessenger.Default.Send/Register`, `UnregisterAll` in Dispose.
- Persistence mirror of `Core/Services/SettingsService.cs`: base dir `Environment.SpecialFolder.ApplicationData` + "NexusMonitor"; 250 ms single-shot restart-on-call debounce `Timer` under a lock; write `.tmp` then `File.Move(overwrite: true)`; `Dispose()` kills timer + synchronous flush; catch-log-never-throw on IO.
- Phase-3 notes from prior reviews: placeholder tiles are GC-safe to discard on rebuild (no disposal needed THIS plan; becomes mandatory when real widgets arrive next plan); classic-VM teardown when engine owns the tab is NEXT plan's concern; `Page` mutations happen only on the UI thread.
- Build/test: `export DOTNET_ROOT=$HOME/.dotnet`; `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release`; `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release`. Suite currently 459/459. UI tasks: build gate + reviewer + the Task 8 screenshot smoke (Screen Recording permission now granted — screenshots are REQUIRED evidence this plan).
- Execution branch: `feat/page-engine-phase3-edit` from current `main`.

---

### Task 1: Core `PageLayoutStore`

**Files:**
- Create: `src/NexusMonitor.Core/Pages/PageLayoutStore.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutStoreTests.cs`

**Interfaces:**
- Consumes: `PageLayoutSerializer`, `BuiltInPageLayouts`.
- Produces (Task 3 consumes): `sealed class PageLayoutStore : IDisposable` with ctor `(string? baseDirectory = null)` (null → `ApplicationData/NexusMonitor/pages`; tests pass a temp dir), `PageLayout LoadOrDefault(string pageId)`, `void Save(PageLayout page)` (debounced 250 ms), `void Dispose()` (flush). File per page: `{baseDirectory}/{pageId}.json`. Corrupt file → renamed to `{pageId}.json.bak` (overwrite existing .bak) and factory default returned.

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public sealed class PageLayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-store-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PageLayout Modified()
    {
        var page = BuiltInPageLayouts.Load("dashboard");
        return PageLayoutEngine.RemoveWidget(page, page.Widgets[0].InstanceId);
    }

    [Fact]
    public void LoadOrDefault_NoFile_ReturnsFactoryDefault()
    {
        using var store = new PageLayoutStore(_dir);
        var page = store.LoadOrDefault("dashboard");
        page.Widgets.Count.Should().Be(BuiltInPageLayouts.Load("dashboard").Widgets.Count);
    }

    [Fact]
    public void SaveThenDispose_RoundTripsThroughDisk()
    {
        var modified = Modified();
        using (var store = new PageLayoutStore(_dir))
        {
            store.Save(modified);
        } // Dispose flushes the debounced write synchronously.

        using var reopened = new PageLayoutStore(_dir);
        reopened.LoadOrDefault("dashboard").Widgets.Count.Should().Be(modified.Widgets.Count);
    }

    [Fact]
    public void LoadOrDefault_CorruptFile_FallsBackToFactory_AndKeepsBak()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "dashboard.json"), "not json {{{");

        using var store = new PageLayoutStore(_dir);
        var page = store.LoadOrDefault("dashboard");

        page.Widgets.Count.Should().Be(BuiltInPageLayouts.Load("dashboard").Widgets.Count);
        File.Exists(Path.Combine(_dir, "dashboard.json.bak")).Should().BeTrue();
    }

    [Fact]
    public void Save_IsDebounced_FileAppearsAfterFlush()
    {
        using var store = new PageLayoutStore(_dir);
        store.Save(Modified());
        // Within the 250ms window the file may not exist yet; after Dispose it must.
        store.Dispose();
        File.Exists(Path.Combine(_dir, "dashboard.json")).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutStoreTests"`
Expected: build FAILS (CS0246 `PageLayoutStore`). Capture output.

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>Per-page layout persistence. Mirrors SettingsService's shape: debounced (250 ms)
/// atomic writes (tmp + move), synchronous flush on dispose, IO failures logged-by-silence
/// (never thrown). A corrupt page file is renamed to .bak and the factory default returned —
/// never a blank page (spec §8).</summary>
public sealed class PageLayoutStore : IDisposable
{
    private readonly string _dir;
    private readonly object _lock = new();
    private Timer? _debounce;
    private PageLayout? _pending;

    /// <summary>Creates a store rooted at <paramref name="baseDirectory"/> (tests) or the
    /// per-user app-data pages directory (production, when null).</summary>
    public PageLayoutStore(string? baseDirectory = null)
    {
        _dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusMonitor", "pages");
    }

    /// <summary>Loads the saved layout for a page, falling back to the factory default when no
    /// file exists or the file is corrupt (corrupt files are preserved as .bak).</summary>
    public PageLayout LoadOrDefault(string pageId)
    {
        var path = PathFor(pageId);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (PageLayoutSerializer.TryDeserialize(json, out var page, out _))
                    return page!;
                File.Move(path, path + ".bak", overwrite: true);
            }
        }
        catch (IOException) { /* fall through to factory default */ }
        catch (UnauthorizedAccessException) { /* fall through to factory default */ }
        return BuiltInPageLayouts.Load(pageId);
    }

    /// <summary>Queues a debounced save (250 ms, restart-on-call). Dispose flushes synchronously.</summary>
    public void Save(PageLayout page)
    {
        lock (_lock)
        {
            _pending = page;
            _debounce?.Dispose();
            _debounce = new Timer(_ => WriteToDisk(), null,
                dueTime: TimeSpan.FromMilliseconds(250), period: Timeout.InfiniteTimeSpan);
        }
    }

    private void WriteToDisk()
    {
        try
        {
            PageLayout? page;
            lock (_lock) { page = _pending; }
            if (page is null) return;

            Directory.CreateDirectory(_dir);
            var path = PathFor(page.PageId);
            File.WriteAllText(path + ".tmp", PageLayoutSerializer.Serialize(page));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException) { /* never throw from background write */ }
        catch (UnauthorizedAccessException) { /* never throw */ }
    }

    private string PathFor(string pageId) => Path.Combine(_dir, pageId + ".json");

    /// <summary>Stops the debounce timer and flushes any pending save synchronously.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        WriteToDisk();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the Step 2 filter → PASS (4 tests). Full suite → 463/463.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageLayoutStore.cs tests/NexusMonitor.Core.Tests/Pages/PageLayoutStoreTests.cs
git commit -m "feat(pages): PageLayoutStore — debounced atomic per-page persistence with corrupt-file fallback"
```

---

### Task 2: Core `PageEditSession`

**Files:**
- Create: `src/NexusMonitor.Core/Pages/PageEditSession.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageEditSessionTests.cs`

**Interfaces:**
- Consumes: `PageLayoutEngine`, model records.
- Produces (Tasks 3/5/6/7 consume): `sealed class PageEditSession` — ctor `(PageLayout original)`; `PageLayout Current { get; }`; `bool CanUndo { get; }`; `bool IsDirty { get; }` (Current not ReferenceEquals original); `void Move(Guid instanceId, GridRect target)`; `void Remove(Guid instanceId)`; `void Add(WidgetInstance widget)`; `void CompactPage()`; `void Undo()`; `PageLayout Cancel()` (returns original); `PageLayout Commit()` (returns Current). Every mutation pushes the prior `Current` onto an internal stack; no-op engine results (same instance back) push NOTHING (undo stays clean).

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageEditSessionTests
{
    private static PageLayout Factory() => BuiltInPageLayouts.Load("dashboard");

    [Fact]
    public void NewSession_CleanState()
    {
        var s = new PageEditSession(Factory());
        s.CanUndo.Should().BeFalse();
        s.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Move_ChangesCurrent_AndEnablesUndo()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);

        s.Move(id, new GridRect(0, 10, 4, 2));

        s.Current.FindWidget(id)!.Rect.Row.Should().Be(10);
        s.CanUndo.Should().BeTrue();
        s.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Move_UnknownId_IsNoOp_NoUndoEntry()
    {
        var s = new PageEditSession(Factory());
        s.Move(Guid.NewGuid(), new GridRect(0, 10, 4, 2));
        s.CanUndo.Should().BeFalse();
        s.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Undo_RestoresPreviousState_StepByStep()
    {
        var page = Factory();
        var id = page.Widgets[0].InstanceId;
        var s = new PageEditSession(page);

        s.Move(id, new GridRect(0, 10, 4, 2));
        s.Remove(page.Widgets[1].InstanceId);
        s.Undo(); // undo the remove

        s.Current.Widgets.Count.Should().Be(page.Widgets.Count);
        s.Current.FindWidget(id)!.Rect.Row.Should().Be(10);

        s.Undo(); // undo the move
        s.Current.FindWidget(id)!.Rect.Should().Be(page.Widgets[0].Rect);
        s.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Cancel_ReturnsOriginal_RegardlessOfEdits()
    {
        var page = Factory();
        var s = new PageEditSession(page);
        s.Remove(page.Widgets[0].InstanceId);
        s.Cancel().Should().BeSameAs(page);
    }

    [Fact]
    public void Commit_ReturnsCurrent()
    {
        var page = Factory();
        var s = new PageEditSession(page);
        s.Remove(page.Widgets[0].InstanceId);
        s.Commit().Widgets.Count.Should().Be(page.Widgets.Count - 1);
    }

    [Fact]
    public void Add_PlacesWithPushDown()
    {
        var page = Factory();
        var s = new PageEditSession(page);
        var widget = new WidgetInstance(Guid.NewGuid(), "nexus.widget.cpuChart", new GridRect(0, 0, 6, 2));

        s.Add(widget);

        s.Current.Widgets.Count.Should().Be(page.Widgets.Count + 1);
        s.Current.FindWidget(widget.InstanceId)!.Rect.Row.Should().Be(0); // newcomer keeps its spot
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Filter `FullyQualifiedName~PageEditSessionTests` → build FAILS (CS0246). Capture.

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>An in-memory editing transaction over an immutable PageLayout. Every mutation runs
/// through PageLayoutEngine and pushes the prior state onto an undo stack; Cancel returns the
/// untouched original, Commit the final state. Engine no-ops (unknown ids) record no undo entry.</summary>
public sealed class PageEditSession
{
    private readonly PageLayout _original;
    private readonly Stack<PageLayout> _undo = new();

    /// <summary>Begins a session over the given layout.</summary>
    public PageEditSession(PageLayout original)
    {
        _original = original;
        Current = original;
    }

    /// <summary>The working layout including all applied edits.</summary>
    public PageLayout Current { get; private set; }

    /// <summary>True when at least one undoable edit has been applied.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when Current differs from the original (by reference — engine ops return new instances).</summary>
    public bool IsDirty => !ReferenceEquals(Current, _original);

    /// <summary>Moves a widget (engine semantics: clamp, pin, push-down). Unknown ids are no-ops.</summary>
    public void Move(Guid instanceId, GridRect target) =>
        Apply(PageLayoutEngine.MoveWidget(Current, instanceId, target));

    /// <summary>Removes a widget. Unknown ids are no-ops.</summary>
    public void Remove(Guid instanceId) =>
        Apply(PageLayoutEngine.RemoveWidget(Current, instanceId));

    /// <summary>Adds a widget via engine placement (clamp + push-down).</summary>
    public void Add(WidgetInstance widget) =>
        Apply(PageLayoutEngine.PlaceWidget(Current, widget));

    /// <summary>Closes vertical gaps (engine Compact).</summary>
    public void CompactPage() => Apply(PageLayoutEngine.Compact(Current));

    /// <summary>Reverts the most recent edit.</summary>
    public void Undo()
    {
        if (_undo.Count > 0) Current = _undo.Pop();
    }

    /// <summary>Abandons all edits and returns the original layout.</summary>
    public PageLayout Cancel() => _original;

    /// <summary>Finalizes the session and returns the edited layout.</summary>
    public PageLayout Commit() => Current;

    private void Apply(PageLayout next)
    {
        if (ReferenceEquals(next, Current)) return; // engine no-op → no undo entry
        _undo.Push(Current);
        Current = next;
    }
}
```

Note: `Compact` always returns a new instance even when positions are unchanged (it rebuilds the list) — that is acceptable (an undo entry for a visually-identical compact is harmless); do not "optimize" with structural equality.

- [ ] **Step 4: Run tests to verify they pass**

Filter → PASS (7 tests). Full suite → 470/470.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageEditSession.cs tests/NexusMonitor.Core.Tests/Pages/PageEditSessionTests.cs
git commit -m "feat(pages): PageEditSession undo-stack editing transaction over the layout engine"
```

---

### Task 3: DashboardViewModel edit state + store wiring

**Files:**
- Modify: `src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs`
- Modify: `src/NexusMonitor.UI/App.axaml.cs` (register `PageLayoutStore` singleton)
- Modify: `src/NexusMonitor.UI/Messages/NavigationMessages.cs` (one record)

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces (Tasks 4-7 consume): on `DashboardViewModel` — `[ObservableProperty] PageLayout? _enginePage` (REPLACES the Phase-2 get-only `EnginePage`; `UsePageEngine` stays get-only), `[ObservableProperty] bool _isEditMode`, `[ObservableProperty] bool _isGalleryOpen`, `[ObservableProperty] bool _canUndoEdit`; commands `EnterEditModeCommand`, `SaveEditCommand`, `CancelEditCommand`, `UndoEditCommand`, `OpenGalleryCommand`, `AddWidgetCommand(string typeId)`; methods for the adorner: `void EditMove(Guid id, GridRect target)`, `void EditRemove(Guid id)`. New message record: `public record PageEditModeChangedMessage(bool IsEditMode);`

- [ ] **Step 1: Implement**

In `NavigationMessages.cs` append:

```csharp
/// <summary>Broadcast when the Dashboard page enters/leaves edit mode (page engine).</summary>
public record PageEditModeChangedMessage(bool IsEditMode);
```

In `App.axaml.cs` `BuildServices()`, with the other Core singletons: `services.AddSingleton<PageLayoutStore>();` (the DI container disposes IDisposable singletons on shutdown — flush is automatic; add `using NexusMonitor.Core.Pages;` if absent).

In `DashboardViewModel`:
- ctor gains `PageLayoutStore? layoutStore = null` trailing optional parameter (nullable so existing tests/design-time construction stay valid); store it in `private readonly PageLayoutStore? _layoutStore;`.
- Replace the Phase-2 load block: keep `UsePageEngine` get-only exactly as-is, but `EnginePage` becomes `[ObservableProperty] private PageLayout? _enginePage;` and the ctor assigns via `EnginePage = _layoutStore?.LoadOrDefault("dashboard") ?? TryFactoryLoad();` inside the same `if (usePageEngine)` + try/catch fallback structure as today (`TryFactoryLoad` is the existing `BuiltInPageLayouts.Load` call — keep the InvalidOperationException fallback that flips usePageEngine false).
- Add the edit block (place after the Phase-2 region, matching `// ── ... ──` banner style):

```csharp
    // ── Page engine editing (Phase 3) ──────────────────────────────────────
    private PageEditSession? _editSession;

    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _isGalleryOpen;
    [ObservableProperty] private bool _canUndoEdit;

    /// <summary>Enters edit mode over the current page.</summary>
    [RelayCommand]
    private void EnterEditMode()
    {
        if (!UsePageEngine || EnginePage is null || IsEditMode) return;
        _editSession = new PageEditSession(EnginePage);
        IsEditMode = true;
        CanUndoEdit = false;
        WeakReferenceMessenger.Default.Send(new PageEditModeChangedMessage(true));
    }

    /// <summary>Commits edits, persists, leaves edit mode.</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (_editSession is null) return;
        EnginePage = _editSession.Commit();
        _layoutStore?.Save(EnginePage);
        ExitEdit();
    }

    /// <summary>Abandons edits, restores the pre-edit layout, leaves edit mode.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        if (_editSession is null) return;
        EnginePage = _editSession.Cancel();
        ExitEdit();
    }

    /// <summary>Reverts the most recent edit.</summary>
    [RelayCommand]
    private void UndoEdit()
    {
        if (_editSession is null) return;
        _editSession.Undo();
        EnginePage = _editSession.Current;
        CanUndoEdit = _editSession.CanUndo;
    }

    /// <summary>Opens the add-widget gallery overlay.</summary>
    [RelayCommand]
    private void OpenGallery() { if (IsEditMode) IsGalleryOpen = true; }

    /// <summary>Adds a widget of the given type at the top of the page (engine push-down applies).</summary>
    [RelayCommand]
    private void AddWidget(string typeId)
    {
        if (_editSession is null) return;
        _editSession.Add(new WidgetInstance(Guid.NewGuid(), typeId, new GridRect(0, 0, 4, 2)));
        AfterEdit();
        IsGalleryOpen = false;
    }

    /// <summary>Adorner callback: commit a drag/resize result.</summary>
    public void EditMove(Guid id, GridRect target)
    {
        if (_editSession is null) return;
        _editSession.Move(id, target);
        AfterEdit();
    }

    /// <summary>Adorner callback: remove a widget.</summary>
    public void EditRemove(Guid id)
    {
        if (_editSession is null) return;
        _editSession.Remove(id);
        AfterEdit();
    }

    private void AfterEdit()
    {
        EnginePage = _editSession!.Current;
        CanUndoEdit = _editSession.CanUndo;
    }

    private void ExitEdit()
    {
        _editSession = null;
        IsEditMode = false;
        IsGalleryOpen = false;
        CanUndoEdit = false;
        WeakReferenceMessenger.Default.Send(new PageEditModeChangedMessage(false));
    }
```

(`RelayCommand`/`ObservableProperty` come from CommunityToolkit.Mvvm already used throughout this VM; usings exist.)

- [ ] **Step 2: Verify** — full build 0 warnings; full Core suite 470/470 (VM has no tests; Core unaffected).

- [ ] **Step 3: Commit**

```bash
git add src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs src/NexusMonitor.UI/App.axaml.cs src/NexusMonitor.UI/Messages/NavigationMessages.cs
git commit -m "feat(pages): Dashboard edit-mode state, commands, and layout-store persistence"
```

---

### Task 4: Header edit toolbar

**Files:**
- Modify: `src/NexusMonitor.UI/Views/DashboardView.axaml` (header Border, Grid ColumnDefinitions="Auto,*,Auto" at ~lines 13-39)

**Interfaces:** consumes Task 3's properties/commands only.

- [ ] **Step 1: Implement**

In the header Grid's column 2 (before the existing "Data stale" badge, inside a horizontal StackPanel wrapping both — create the StackPanel if the badge is currently the lone element; keep the badge markup byte-identical):

```xml
        <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
          <!-- View-mode: single pencil toggle (page engine only) -->
          <Button Classes="nx-btn-ghost"
                  IsVisible="{Binding UsePageEngine}"
                  Command="{Binding EnterEditModeCommand}"
                  ToolTip.Tip="Edit layout">
            <TextBlock Text="&#xF303;" FontFamily="{StaticResource NexusIcons}" FontSize="{DynamicResource NxFont14}"/>
          </Button>
          <!-- Edit-mode toolbar -->
          <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding IsEditMode}">
            <Button Classes="nx-btn" Command="{Binding OpenGalleryCommand}">
              <TextBlock Text="＋ Add widget" FontSize="{DynamicResource NxFont12}"/>
            </Button>
            <Button Classes="nx-btn" Command="{Binding UndoEditCommand}" IsEnabled="{Binding CanUndoEdit}">
              <TextBlock Text="Undo" FontSize="{DynamicResource NxFont12}"/>
            </Button>
            <Button Classes="nx-btn" Command="{Binding CancelEditCommand}">
              <TextBlock Text="Cancel" FontSize="{DynamicResource NxFont12}"/>
            </Button>
            <Button Classes="nx-btn-accent" Command="{Binding SaveEditCommand}">
              <TextBlock Text="Save" FontSize="{DynamicResource NxFont12}"/>
            </Button>
          </StackPanel>
          <!-- existing Data stale badge moves here unchanged -->
        </StackPanel>
```

Adjustments the implementer must make against reality: (a) the pencil glyph `&#xF303;` is a GUESS — grep `NexusIcons` usages for an existing edit/pencil glyph and reuse one that renders (any reasonable editing glyph is fine; note the choice); (b) hide the pencil while editing: change its IsVisible to a MultiBinding if the codebase has a precedent, otherwise simplest is `IsVisible="{Binding !IsEditMode}"` on the pencil *inside* an outer `StackPanel IsVisible="{Binding UsePageEngine}"` wrapper — pick the structure that stays readable; (c) the existing badge markup relocates verbatim.

- [ ] **Step 2: Verify + Commit** — build 0 warnings.

```bash
git add src/NexusMonitor.UI/Views/DashboardView.axaml
git commit -m "feat(pages): Dashboard header edit toolbar (pencil, add, undo, cancel, save)"
```

---

### Task 5: `EditAdornerControl` — chrome + remove hit-zone (no gestures yet)

**Files:**
- Create: `src/NexusMonitor.UI/Controls/EditAdornerControl.cs`
- Modify: `src/NexusMonitor.UI/Views/DashboardView.axaml` (overlay the adorner on the PageHostControl)

**Interfaces:**
- Consumes: `PageGeometry`, `PageLayout`, DashboardViewModel's `EditRemove`.
- Produces (Task 6 extends this class): `sealed class EditAdornerControl : Control` with StyledProperties `Page (PageLayout?)`, `CellHeight`, `CellGap` (same defaults as PageHostControl), `IsActive (bool)`; CLR event-free design — instead two `public` delegate properties set from code-behind or bindings are NOT used; the adorner finds its callbacks via `RemoveRequested`/`MoveCommitted` `public event Action<Guid>?` / `public event Action<Guid, GridRect>?`. Shared geometry: a private `TileRect(int i, Rect bounds)` helper used by BOTH Render and hit-testing (ColorWheel idiom). Renders per tile (when `IsActive`): accent outline, a ✕ remove box (top-right, 20×20 px), a resize grip (bottom-right, 16×16 px diagonal hatch). `OnPointerPressed`: hit-test ✕ → raise `RemoveRequested(id)`; grip/body zones are recorded but gesture handling arrives in Task 6.

- [ ] **Step 1: Implement the control**

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Edit-mode overlay for PageHostControl: draws per-tile chrome (outline, remove box,
/// resize grip) and owns all edit pointer interaction. Rendering and hit-testing share the same
/// geometry (PageGeometry) so they can never diverge. Inactive → invisible and hit-test-transparent.</summary>
public sealed class EditAdornerControl : Control
{
    /// <summary>The page whose tiles are adorned (same instance PageHostControl renders).</summary>
    public static readonly StyledProperty<PageLayout?> PageProperty =
        AvaloniaProperty.Register<EditAdornerControl, PageLayout?>(nameof(Page));

    /// <summary>Row height in pixels (must match the host panel).</summary>
    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<EditAdornerControl, double>(nameof(CellHeight), PageMetrics.DefaultCellHeight);

    /// <summary>Cell gap in pixels (must match the host panel).</summary>
    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<EditAdornerControl, double>(nameof(CellGap), PageMetrics.DefaultCellGap);

    /// <summary>True while edit mode is on; gates rendering and hit-testing.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<EditAdornerControl, bool>(nameof(IsActive));

    static EditAdornerControl()
    {
        AffectsRender<EditAdornerControl>(PageProperty, CellHeightProperty, CellGapProperty, IsActiveProperty);
    }

    /// <summary>The page whose tiles are adorned.</summary>
    public PageLayout? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    /// <summary>Row height in pixels.</summary>
    public double CellHeight { get => GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }
    /// <summary>Cell gap in pixels.</summary>
    public double CellGap { get => GetValue(CellGapProperty); set => SetValue(CellGapProperty, value); }
    /// <summary>True while edit mode is on.</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Raised when the user clicks a tile's remove box.</summary>
    public event Action<Guid>? RemoveRequested;

    /// <summary>Raised when a drag/resize gesture commits (Task 6 wires the gesture).</summary>
    public event Action<Guid, GridRect>? MoveCommitted;

    private const double RemoveBox = 20;
    private const double Grip = 16;

    private Rect TileRect(int i)
    {
        var page = Page!;
        var px = PageGeometry.ToPixelRect(page.Widgets[i].Rect, Bounds.Width, page.GridColumns, CellHeight, CellGap);
        return new Rect(px.X, px.Y, px.Width, px.Height);
    }

    /// <summary>Zone hit for a point: (tile index, zone) or null. Zones: 0=body, 1=remove, 2=grip.</summary>
    private (int Index, int Zone)? HitTest(Point p)
    {
        if (!IsActive || Page is null) return null;
        for (var i = Page.Widgets.Count - 1; i >= 0; i--)
        {
            var r = TileRect(i);
            if (!r.Contains(p)) continue;
            if (new Rect(r.Right - RemoveBox, r.Y, RemoveBox, RemoveBox).Contains(p)) return (i, 1);
            if (new Rect(r.Right - Grip, r.Bottom - Grip, Grip, Grip).Contains(p)) return (i, 2);
            return (i, 0);
        }
        return null;
    }

    public override void Render(DrawingContext ctx)
    {
        if (!IsActive || Page is null) return;
        var accent = new Pen(new SolidColorBrush(Color.FromArgb(0xEE, 0x4C, 0x8D, 0xFF)), 1.5);
        var dim = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        var glyph = new SolidColorBrush(Colors.White);

        for (var i = 0; i < Page.Widgets.Count; i++)
        {
            var r = TileRect(i);
            ctx.DrawRectangle(null, accent, r, 8, 8);

            var remove = new Rect(r.Right - RemoveBox, r.Y, RemoveBox, RemoveBox);
            ctx.DrawRectangle(dim, null, remove, 4, 4);
            var t = new FormattedText("✕", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, glyph);
            ctx.DrawText(t, new Point(remove.X + (RemoveBox - t.Width) / 2, remove.Y + (RemoveBox - t.Height) / 2));

            var grip = new Rect(r.Right - Grip, r.Bottom - Grip, Grip, Grip);
            for (var g = 4; g <= 12; g += 4)
                ctx.DrawLine(accent, new Point(grip.Right - g, grip.Bottom), new Point(grip.Right, grip.Bottom - g));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var hit = HitTest(e.GetPosition(this));
        if (hit is null) return;

        if (hit.Value.Zone == 1)
        {
            RemoveRequested?.Invoke(Page!.Widgets[hit.Value.Index].InstanceId);
            e.Handled = true;
        }
        // Zones 0 (drag) and 2 (resize) are wired in the gesture task.
    }
}
```

Implementer notes: `FormattedText` ctor signature varies across Avalonia 11 minors — if the shown overload doesn't compile, use the current one (foreground last or via `.SetForegroundBrush`); the behavior contract is a centered ✕. `MoveCommitted` is declared now (Task 6 raises it) so Task 6's diff stays gesture-only; suppress the unused-event warning by referencing it in a doc comment only if the compiler complains — if CS0067 fires under warnings-as-errors, add the event in Task 6 instead and note it.

- [ ] **Step 2: Overlay in DashboardView.axaml**

Replace the engine ScrollViewer's single child with a Panel stacking host + adorner (adorner LAST = on top):

```xml
      <Panel>
        <controls:PageHostControl Page="{Binding EnginePage}"/>
        <controls:EditAdornerControl x:Name="EditAdorner"
                                     Page="{Binding EnginePage}"
                                     IsActive="{Binding IsEditMode}"/>
      </Panel>
```

In `DashboardView.axaml.cs` (currently a bare InitializeComponent code-behind), wire the events:

```csharp
        EditAdorner.RemoveRequested += id => (DataContext as DashboardViewModel)?.EditRemove(id);
        EditAdorner.MoveCommitted += (id, rect) => (DataContext as DashboardViewModel)?.EditMove(id, rect);
```

(Add the usings the compiler asks for; keep the code-behind minimal.)

Note: `EditAdornerControl` is a `Control`, not a `Panel` child of PageHostControl — the stacking `Panel` gives both the same Bounds, so `Bounds.Width` in TileRect matches the host panel's arrange width. The adorner must NOT block scroll: it only `e.Handled = true` on actual zone hits.

- [ ] **Step 3: Verify + Commit** — build 0 warnings; Core suite 470/470.

```bash
git add src/NexusMonitor.UI/Controls/EditAdornerControl.cs src/NexusMonitor.UI/Views/DashboardView.axaml src/NexusMonitor.UI/Views/DashboardView.axaml.cs
git commit -m "feat(pages): edit-mode adorner chrome with remove hit-zone"
```

---

### Task 6: Drag + resize gestures with ghost preview

**Files:**
- Modify: `src/NexusMonitor.UI/Controls/EditAdornerControl.cs`

**Interfaces:** consumes Task 5's zones; raises `MoveCommitted(Guid, GridRect)` on release. Ghost preview = adorner-drawn only; the layout mutates ONCE per gesture (on release), so PageHostControl rebuilds once per gesture, not per pointer-move.

- [ ] **Step 1: Implement the gesture state machine** (add to EditAdornerControl)

```csharp
    private int _dragIndex = -1;
    private bool _resizing;
    private Point _pressOffset;      // pointer offset inside the tile at press (drag)
    private GridRect? _ghost;        // candidate cell rect under the pointer

    // In OnPointerPressed, replace the trailing comment with:
        else
        {
            _dragIndex = hit.Value.Index;
            _resizing = hit.Value.Zone == 2;
            var r = TileRect(_dragIndex);
            _pressOffset = e.GetPosition(this) - r.TopLeft;
            e.Pointer.Capture(this);   // capture ONLY on a valid zone (ColorWheel idiom)
            e.Handled = true;
        }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured != this || _dragIndex < 0 || Page is null) return;

        var p = e.GetPosition(this);
        var w = Page.Widgets[_dragIndex];
        _ghost = _resizing ? CellRectForResize(w, p) : CellRectForDrag(w, p);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);       // ALWAYS release capture
        if (_dragIndex >= 0 && _ghost is not null && Page is not null)
            MoveCommitted?.Invoke(Page.Widgets[_dragIndex].InstanceId, _ghost);
        _dragIndex = -1;
        _resizing = false;
        _ghost = null;
        InvalidateVisual();
    }

    private (double CellW, double StrideX, double StrideY) Strides()
    {
        var cellW = PageGeometry.CellWidth(Bounds.Width, Page!.GridColumns, CellGap);
        return (cellW, cellW + CellGap, CellHeight + CellGap);
    }

    /// <summary>Pixel point → cell rect for a body drag: tile origin follows the pointer minus press offset, span unchanged.</summary>
    private GridRect CellRectForDrag(WidgetInstance w, Point p)
    {
        var (_, strideX, strideY) = Strides();
        var col = (int)Math.Round((p.X - _pressOffset.X) / strideX);
        var row = (int)Math.Round((p.Y - _pressOffset.Y) / strideY);
        return new GridRect(col, Math.Max(0, row), w.Rect.ColSpan, w.Rect.RowSpan)
            .ClampTo(Page!.GridColumns);
    }

    /// <summary>Pixel point → cell rect for a resize: origin fixed, spans track the pointer (min 1×1).</summary>
    private GridRect CellRectForResize(WidgetInstance w, Point p)
    {
        var (_, strideX, strideY) = Strides();
        var origin = TileRect(_dragIndex).TopLeft;
        var colSpan = Math.Max(1, (int)Math.Round((p.X - origin.X) / strideX));
        var rowSpan = Math.Max(1, (int)Math.Round((p.Y - origin.Y) / strideY));
        return new GridRect(w.Rect.Col, w.Rect.Row, colSpan, rowSpan).ClampTo(Page!.GridColumns);
    }

    // In Render, after the per-tile loop, draw the ghost:
        if (_ghost is not null)
        {
            var px = PageGeometry.ToPixelRect(_ghost, Bounds.Width, Page.GridColumns, CellHeight, CellGap);
            var ghostRect = new Rect(px.X, px.Y, px.Width, px.Height);
            var legal = PageLayoutEngine.IsValidPlacement(Page, _ghost,
                ignoreInstanceId: Page.Widgets[_dragIndex].InstanceId);
            var fill = new SolidColorBrush(Color.FromArgb(0x33, legal ? (byte)0x4C : (byte)0xFF,
                legal ? (byte)0x8D : (byte)0x8D, legal ? (byte)0xFF : (byte)0x4C));
            ctx.DrawRectangle(fill, new Pen(fill, 2), ghostRect, 8, 8);
        }
```

Semantics locked by this task: the ghost tints blue when the drop lands on empty cells and amber when it will push others down (`IsValidPlacement` used ADVISORILY, exactly per its contract — the commit itself always succeeds via `MoveWidget`'s push-down). `_ghost` is a *candidate*, not a preview of the final push-down state — Plasma behaves the same way. Integrate these snippets cleanly into the Task 5 file (single coherent class, not appended fragments); resolve name/scope collisions sensibly.

- [ ] **Step 2: Verify + Commit** — build 0 warnings; Core suite 470/470.

```bash
git add src/NexusMonitor.UI/Controls/EditAdornerControl.cs
git commit -m "feat(pages): drag and resize gestures with advisory ghost preview"
```

---

### Task 7: Add-widget gallery overlay

**Files:**
- Create: `src/NexusMonitor.UI/Controls/WidgetGalleryControl.axaml` + `.axaml.cs`
- Create: `src/NexusMonitor.UI/Controls/WidgetCatalog.cs`
- Modify: `src/NexusMonitor.UI/Views/DashboardView.axaml` (overlay at the view root)

**Interfaces:**
- Produces: `static class WidgetCatalog` with `record WidgetCatalogEntry(string TypeId, string Name, string Description)` and `static IReadOnlyList<WidgetCatalogEntry> Entries` — the three known TypeIds (Health Score / CPU / Memory, descriptions "Placeholder tile — live content arrives with widget extraction"). Real-widget extraction (next plan) grows this list; the gallery reads ONLY the catalog.
- Gallery control: CommandPalette pattern — root Panel, backdrop `Border` (semi-transparent, PointerPressed closes via `IsGalleryOpen=false` binding path: give the control a `CloseRequested` event wired in code-behind like Task 5, or bind backdrop click through a command — pick the CommandPalette-consistent route and note it), centered card listing `WidgetCatalog.Entries` in an ItemsControl; each entry a `nx-btn` styled row whose click invokes `AddWidgetCommand` with the TypeId (`Command="{Binding $parent[UserControl].DataContext.AddWidgetCommand}" CommandParameter="{Binding TypeId}"` — the `$parent[UserControl].DataContext` idiom is used at PerformanceProfilesView.axaml:283-289). Whole control `IsVisible="{Binding IsGalleryOpen}"`, card stops click-through with `e.Handled=true`.

- [ ] **Step 1: Implement** (catalog record + control per the pattern; keep the card ≤ 480px wide, title "Add widget", entries as Name + Description rows).
- [ ] **Step 2: Overlay `<controls:WidgetGalleryControl/>` as the LAST child of DashboardView's root DockPanel-wrapping element — it must cover the whole view. If the root is the DockPanel itself, wrap the DockPanel in a Panel and add the gallery as the second child (mirror how MainWindow hosts CommandPaletteControl — check and follow that structure).
- [ ] **Step 3: Verify + Commit** — build 0 warnings; Core 470/470.

```bash
git add src/NexusMonitor.UI/Controls/WidgetGalleryControl.axaml src/NexusMonitor.UI/Controls/WidgetGalleryControl.axaml.cs src/NexusMonitor.UI/Controls/WidgetCatalog.cs src/NexusMonitor.UI/Views/DashboardView.axaml
git commit -m "feat(pages): add-widget gallery overlay backed by WidgetCatalog"
```

---

### Task 8: Visual smoke (screenshots required) + changelog

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Publish + enable flag** (settings at `~/Library/Application Support/NexusMonitor/settings.json`, key `EnablePageEngine`, PascalCase; restore false when done).
- [ ] **Step 2: Scripted visual walkthrough** — launch, screenshot each state to the session scratchpad (`p3-1..p3-7.png`), inspecting each with the Read tool and reporting what is SEEN:
  1. Flag-on view mode: pencil visible in header, three tiles, NO edit chrome.
  2. Enter edit mode (the pencil is clickable via `cliclick` if installed — check `which cliclick`; if absent, report and drive clicks via AppleScript `osascript -e 'tell application "System Events" to click at {x,y}'` computed from the screenshot; if neither works, report BLOCKED with what's missing): outlines + ✕ + grips visible, toolbar shows Add/Undo/Cancel/Save.
  3. Drag the CPU tile below Memory (press-drag-release via cliclick/AppleScript): ghost visible mid-drag (bonus screenshot), layout committed after release (CPU now on its own row).
  4. Remove Health Score via its ✕ → tile gone; Undo → tile back.
  5. Add widget via gallery: overlay appears, click CPU entry → new 4×2 tile pushed in at top.
  6. Save → exit edit mode → verify `~/Library/Application Support/NexusMonitor/pages/dashboard.json` exists and TryDeserialize-parses (a `python3 -c "import json..."` sanity parse is fine).
  7. Quit, relaunch, screenshot: edited layout PERSISTED.
  Then Cancel-path check: re-enter edit, remove a tile, Cancel → layout unchanged.
- [ ] **Step 3: Classic regression** — flag off, launch, screenshot: classic dashboard unchanged, NO pencil button visible (UsePageEngine false).
- [ ] **Step 4: CHANGELOG** under Unreleased/Added:

```markdown
- Page engine edit mode (experimental): rearrange, resize, add, and remove Dashboard
  widgets with drag-and-drop, undo, and persistent layouts (requires "Enable page engine").
```

- [ ] **Step 5: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: changelog for page-engine edit mode; phase-3 visual walkthrough evidence"
```

If pointer scripting proves impossible (no cliclick, AppleScript blocked by TCC), report DONE_WITH_CONCERNS listing exactly which walkthrough steps were verified (view-mode + classic screenshots at minimum) and which need the owner's hands — do NOT silently skip the walkthrough.

---

## Done means

Core suite 470/470 (11 new tests), 0 warnings; every task committed on `feat/page-engine-phase3-edit`; visual walkthrough evidence captured (or explicitly reported as owner-handoff); classic path regression-checked. Next plan: real-widget extraction (Dashboard sections → live widget controls), which also picks up: RebuildChildren child disposal, classic-VM teardown via IActivatable, WidgetCatalog expansion.
