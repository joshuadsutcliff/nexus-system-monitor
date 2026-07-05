# Page Engine Phase 2 (PageHostControl + Flagged Dashboard Rendering) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render a serialized `PageLayout` through a new Avalonia `PageHostControl` custom Panel, shown on the Dashboard tab behind a new `EnablePageEngine` setting (default off), with placeholder widget tiles standing in for real widgets until Phase 3.

**Architecture:** Pure pixel math lives in Core (`PageGeometry`, unit-tested). The UI gets three new pieces: `WidgetTile` (a small UserControl with the standard card chrome), `WidgetTileFactory` (TypeId → tile; unknown TypeIds get the same placeholder path — spec §8's placeholder-tile behavior starts here), and `PageHostControl : Panel` (the repo's first Panel subclass; Measure/Arrange delegates to `PageGeometry`). `DashboardView` hosts both the legacy layout and the engine view as flag-gated siblings — no changes to nav routing or MainWindow DataTemplates.

**Tech Stack:** .NET 8, Avalonia 11.2.3, existing Core.Pages API from Phase 1 (merged in PR #12).

## Global Constraints

- `TreatWarningsAsErrors=true`, `Nullable=enable`, ImplicitUsings enabled — zero warnings. No new NuGet dependencies.
- Owner rulings (carried from Phase 1): FluentAssertions idiom in tests (never raw `Assert.*`); test files start with `using` directives, blank line, then file-scoped namespace; XML `///` doc comments on EVERY public member even where sample code omits them; TDD — actually run and capture the RED step for Core code.
- Core additions go in `src/NexusMonitor.Core/Pages/` with zero UI references. UI additions: `src/NexusMonitor.UI/Controls/` (controls) + existing view/VM files.
- Model facts (Phase 1, exact): `WidgetInstance.Rect` (field name is `Rect`, type `GridRect(int Col, int Row, int ColSpan, int RowSpan)` with exclusive `Right`/`Bottom`); `PageLayout.GridColumns`; `BuiltInPageLayouts.Load("dashboard")` throws `InvalidOperationException` only on packaging bugs.
- **Phase-2 pre-work notes (from Phase 1 final review):** `IsValidPlacement` is ADVISORY (drop-preview legality only) — Phase 2 does not call engine mutation methods at all (view-mode only); do NOT touch the dead `AppSettings.DashboardEnabled` flag (unused pre-existing cruft — leave for a cleanup pass); `BuiltInPageIds` hardening waits for the phase where the sidebar consumes it.
- Styling: widget-tile chrome reuses the Dashboard card tokens exactly: `BgElevatedBrush`, `GlassBorderBrush`, `CornerMD`, `TextPrimaryBrush`/`TextSecondaryBrush`, `NxFont12`/`NxFont14` (all `DynamicResource` except Corner*/NxFont* which are `StaticResource` — copy usage from `DashboardView.axaml`).
- Layout constants: baseline cell height **72 px**, cell gap **12 px** (spec §3.1: tuned during this phase's smoke test, then frozen). They live in `PageMetrics` (UI) as the control's property defaults; `PageGeometry` (Core) takes explicit parameters and owns no constants.
- Build/test on this machine: `export DOTNET_ROOT=$HOME/.dotnet`, `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` / `... test tests/NexusMonitor.Core.Tests -c Release`. UI work is verified by clean build + the Task 6 manual smoke (no UI test harness exists — confirmed).
- Feature-flag semantics: `EnablePageEngine` is read ONCE at DashboardViewModel construction — the toggle takes effect on restart (documented in the toggle text). Live-swap is out of scope.
- Execution happens on a new branch `feat/page-engine-phase2` from current `main`.

---

### Task 1: Core pixel geometry (`PageGeometry`)

**Files:**
- Create: `src/NexusMonitor.Core/Pages/PageGeometry.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageGeometryTests.cs`

**Interfaces:**
- Consumes: `GridRect` (Phase 1).
- Produces (Task 5 depends on): `readonly record struct PixelRect(double X, double Y, double Width, double Height)`; `static class PageGeometry` with
  `static double CellWidth(double availableWidth, int gridColumns, double gap)` and
  `static PixelRect ToPixelRect(GridRect rect, double availableWidth, int gridColumns, double cellHeight, double gap)` and
  `static double TotalHeight(int rowCount, double cellHeight, double gap)`.
  Semantics: N columns share `availableWidth` minus `(N-1)` gaps; a widget spanning k columns is k cells + (k-1) interior gaps wide; same shape vertically with `cellHeight`; `TotalHeight(0,...)` is 0.

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public class PageGeometryTests
{
    // 12 columns, width 1212, gap 12 → 11 gaps = 132; 1080/12 = 90 per cell. Clean numbers on purpose.
    private const double Width = 1212;
    private const int Cols = 12;
    private const double Gap = 12;
    private const double CellH = 72;

    [Fact]
    public void CellWidth_DividesRemainingSpaceEvenly()
    {
        PageGeometry.CellWidth(Width, Cols, Gap).Should().Be(90);
    }

    [Fact]
    public void ToPixelRect_OriginCell_SitsAtZero()
    {
        var px = PageGeometry.ToPixelRect(new GridRect(0, 0, 1, 1), Width, Cols, CellH, Gap);
        px.Should().Be(new PixelRect(0, 0, 90, 72));
    }

    [Fact]
    public void ToPixelRect_OffsetAndSpan_IncludeInteriorGaps()
    {
        // Col 4, span 8: X = 4*(90+12) = 408; W = 8*90 + 7*12 = 804.
        // Row 2, span 2: Y = 2*(72+12) = 168; H = 2*72 + 1*12 = 156.
        var px = PageGeometry.ToPixelRect(new GridRect(4, 2, 8, 2), Width, Cols, CellH, Gap);
        px.Should().Be(new PixelRect(408, 168, 804, 156));
    }

    [Fact]
    public void ToPixelRect_FullWidthRow_SpansExactlyAvailableWidth()
    {
        var px = PageGeometry.ToPixelRect(new GridRect(0, 0, 12, 1), Width, Cols, CellH, Gap);
        px.Width.Should().Be(Width);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 72)]
    [InlineData(4, 4 * 72 + 3 * 12)]
    public void TotalHeight_SumsRowsAndInteriorGaps(int rows, double expected)
    {
        PageGeometry.TotalHeight(rows, CellH, Gap).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageGeometryTests"`
Expected: build FAILS (CS0246/CS0103 — `PageGeometry`/`PixelRect` unknown). Capture the output.

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>A device-independent pixel rectangle. Plain doubles so Core stays UI-framework-free.</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height);

/// <summary>Pure math mapping grid cells to pixels. The UI panel owns the inputs
/// (available width, cell height, gap); this class owns no constants.</summary>
public static class PageGeometry
{
    /// <summary>Width of one cell: the available width minus interior gaps, divided evenly.</summary>
    public static double CellWidth(double availableWidth, int gridColumns, double gap) =>
        (availableWidth - gap * (gridColumns - 1)) / gridColumns;

    /// <summary>Pixel rect for a grid rect: spans cover their cells plus interior gaps.</summary>
    public static PixelRect ToPixelRect(GridRect rect, double availableWidth, int gridColumns, double cellHeight, double gap)
    {
        var cellWidth = CellWidth(availableWidth, gridColumns, gap);
        var x = rect.Col * (cellWidth + gap);
        var y = rect.Row * (cellHeight + gap);
        var width = rect.ColSpan * cellWidth + (rect.ColSpan - 1) * gap;
        var height = rect.RowSpan * cellHeight + (rect.RowSpan - 1) * gap;
        return new PixelRect(x, y, width, height);
    }

    /// <summary>Total content height for a page of the given row count (0 rows → 0).</summary>
    public static double TotalHeight(int rowCount, double cellHeight, double gap) =>
        rowCount <= 0 ? 0 : rowCount * cellHeight + (rowCount - 1) * gap;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageGeometryTests"`
Expected: PASS (7 cases). Then full suite once: 452 pre-existing + 7 = 459/459.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageGeometry.cs tests/NexusMonitor.Core.Tests/Pages/PageGeometryTests.cs
git commit -m "feat(pages): PageGeometry grid-to-pixel math"
```

---

### Task 2: `EnablePageEngine` setting + toggle

**Files:**
- Modify: `src/NexusMonitor.Core/Models/AppSettings.cs` (one property, near the other feature bools)
- Modify: `src/NexusMonitor.UI/ViewModels/SettingsViewModel.cs` (ObservableProperty + init + change hook)
- Modify: `src/NexusMonitor.UI/Views/SettingsView.axaml` (CheckBox in/near the Updates card added recently, as a small "Experimental" block)

**Interfaces:**
- Consumes: existing settings plumbing (`_settings.Current`, debounced save).
- Produces (Task 5 depends on): `AppSettings.EnablePageEngine` (bool, default false).

- [ ] **Step 1: Add the setting**

In `AppSettings.cs`, alongside the other feature flags (do NOT touch the unused `DashboardEnabled` at its line ~84):

```csharp
    /// <summary>Renders the Dashboard through the page engine (Phase 2, experimental). Read at startup; takes effect after restart.</summary>
    public bool EnablePageEngine { get; set; } = false;
```

- [ ] **Step 2: Wire the ViewModel**

In `SettingsViewModel.cs`, following the exact `MetricsEnabled` pattern (declaration ~line 205, init ~line 391, hook ~line 729):

```csharp
    [ObservableProperty] private bool _enablePageEngine;
```
Init in the constructor block that copies from `settings.Current`:
```csharp
        _enablePageEngine = settings.Current.EnablePageEngine;
```
Change hook with the sibling hooks:
```csharp
    partial void OnEnablePageEngineChanged(bool value)
    {
        _settings.Current.EnablePageEngine = value;
        _settings.Save();
    }
```
(Match the neighboring hooks' exact save-call idiom — if siblings call `_settings.Save()` implicitly via another path, copy that instead. No messenger broadcast: the flag is restart-scoped.)

- [ ] **Step 3: Add the CheckBox**

In `SettingsView.axaml`, directly under the Updates card (added with the update checker), a minimal block copying the card chrome around it and the `WebhookAlerts` CheckBox idiom (~line 1279):

```xml
        <!-- Experimental -->
        <TextBlock Text="Experimental" FontSize="{DynamicResource NxFont14}" Foreground="{DynamicResource TextSecondaryBrush}" Margin="0,16,0,4"/>
        <CheckBox IsChecked="{Binding EnablePageEngine}"
                  Content="Enable page engine — Dashboard renders via the new layout engine (takes effect after restart)"
                  FontSize="{DynamicResource NxFont12}"/>
```
(If the Updates card sits inside a bordered container, place this block as its sibling at the same indentation level; match surrounding margins.)

- [ ] **Step 4: Verify**

Run: `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` → 0 warnings. Full Core suite once → all green (settings round-trip is covered by existing SettingsService tests; the new property serializes via the same plain-property path).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Models/AppSettings.cs src/NexusMonitor.UI/ViewModels/SettingsViewModel.cs src/NexusMonitor.UI/Views/SettingsView.axaml
git commit -m "feat(pages): EnablePageEngine setting with experimental toggle"
```

---

### Task 3: `WidgetTile` control + `WidgetTileFactory`

**Files:**
- Create: `src/NexusMonitor.UI/Controls/WidgetTile.axaml` + `src/NexusMonitor.UI/Controls/WidgetTile.axaml.cs`
- Create: `src/NexusMonitor.UI/Controls/WidgetTileFactory.cs`

**Interfaces:**
- Consumes: `WidgetInstance` (Core.Pages).
- Produces (Task 4 depends on): `static Control WidgetTileFactory.Create(WidgetInstance widget)` — always returns a tile; known Phase-1 TypeIds get friendly titles, unknown TypeIds get the placeholder path with the raw TypeId shown (spec §8 unknown-widget behavior).

- [ ] **Step 1: WidgetTile.axaml** (card chrome per Global Constraints tokens)

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="NexusMonitor.UI.Controls.WidgetTile">
  <Border Background="{DynamicResource BgElevatedBrush}"
          BorderBrush="{DynamicResource GlassBorderBrush}"
          BorderThickness="1"
          CornerRadius="{StaticResource CornerMD}"
          Padding="16,12">
    <StackPanel Spacing="6">
      <TextBlock x:Name="TitleText"
                 FontSize="{DynamicResource NxFont14}"
                 Foreground="{DynamicResource TextPrimaryBrush}"/>
      <TextBlock x:Name="SubtitleText"
                 FontSize="{DynamicResource NxFont12}"
                 Foreground="{DynamicResource TextSecondaryBrush}"
                 TextWrapping="Wrap"/>
    </StackPanel>
  </Border>
</UserControl>
```

- [ ] **Step 2: WidgetTile.axaml.cs**

```csharp
using Avalonia;
using Avalonia.Controls;

namespace NexusMonitor.UI.Controls;

/// <summary>Standard chrome for one widget on a page. Phase 2 renders title + subtitle only;
/// Phase 3 replaces the subtitle body with real widget content.</summary>
public partial class WidgetTile : UserControl
{
    /// <summary>The tile's heading.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WidgetTile, string?>(nameof(Title));

    /// <summary>Secondary line under the heading.</summary>
    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<WidgetTile, string?>(nameof(Subtitle));

    /// <summary>The tile's heading.</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>Secondary line under the heading.</summary>
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>Initializes the tile and wires property changes to the text blocks.</summary>
    public WidgetTile()
    {
        InitializeComponent();
        this.GetObservable(TitleProperty).Subscribe(t => TitleText.Text = t);
        this.GetObservable(SubtitleProperty).Subscribe(s => SubtitleText.Text = s);
    }
}
```
(If `x:Name` field generation or `GetObservable`/`Subscribe` needs `using System;` for `IObserver` adapters under this Avalonia version, follow the compiler; an equivalent acceptable variant is overriding `OnPropertyChanged` and assigning the text there — pick whichever compiles clean with zero warnings and note the choice in the report.)

- [ ] **Step 3: WidgetTileFactory.cs**

```csharp
using Avalonia.Controls;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Maps a WidgetInstance to its rendered control. Phase 2: every TypeId renders as a
/// placeholder WidgetTile; Phase 3 replaces this with the real widget registry. Unknown TypeIds
/// intentionally render the same placeholder path (spec §8: never a broken/blank tile).</summary>
public static class WidgetTileFactory
{
    private static readonly Dictionary<string, string> KnownTitles = new()
    {
        ["nexus.widget.healthScore"] = "Health Score",
        ["nexus.widget.cpuChart"] = "CPU",
        ["nexus.widget.memoryChart"] = "Memory",
    };

    /// <summary>Creates the control for one widget instance. Never returns null and never throws.</summary>
    public static Control Create(WidgetInstance widget)
    {
        var known = KnownTitles.TryGetValue(widget.WidgetTypeId, out var title);
        return new WidgetTile
        {
            Title = known ? title : "Unknown widget",
            Subtitle = known
                ? "Widget content arrives in a future update."
                : $"'{widget.WidgetTypeId}' isn't available in this version. Its place and settings are preserved.",
        };
    }
}
```

- [ ] **Step 4: Verify + Commit**

Run: `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` → 0 warnings.

```bash
git add src/NexusMonitor.UI/Controls/WidgetTile.axaml src/NexusMonitor.UI/Controls/WidgetTile.axaml.cs src/NexusMonitor.UI/Controls/WidgetTileFactory.cs
git commit -m "feat(pages): WidgetTile placeholder control and factory"
```

---

### Task 4: `PageHostControl` custom Panel

**Files:**
- Create: `src/NexusMonitor.UI/Controls/PageHostControl.cs`
- Create: `src/NexusMonitor.UI/Controls/PageMetrics.cs`

**Interfaces:**
- Consumes: `PageGeometry`/`PixelRect` (Task 1), `WidgetTileFactory` (Task 3), `PageLayout`.
- Produces (Task 5 depends on): `PageHostControl : Panel` with StyledProperties `Page (PageLayout?)`, `CellHeight (double, default PageMetrics.DefaultCellHeight)`, `CellGap (double, default PageMetrics.DefaultCellGap)`. Children are rebuilt from `Page.Widgets` (index-aligned) whenever `Page` changes.

- [ ] **Step 1: PageMetrics.cs**

```csharp
namespace NexusMonitor.UI.Controls;

/// <summary>Frozen layout constants for the page engine (spec §3.1: tuned during Phase 2 smoke, then fixed).</summary>
public static class PageMetrics
{
    /// <summary>Baseline height of one grid row in pixels (before any font-scale multiplier, applied in a later phase).</summary>
    public const double DefaultCellHeight = 72;

    /// <summary>Gap between cells in pixels, both axes.</summary>
    public const double DefaultCellGap = 12;
}
```

- [ ] **Step 2: PageHostControl.cs**

```csharp
using Avalonia;
using Avalonia.Controls;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Arranges widget tiles on the page grid. Pure view-mode renderer: layout math is
/// delegated to Core's PageGeometry, children come from WidgetTileFactory, and nothing here
/// mutates the PageLayout (edit mode arrives in Phase 3).</summary>
public sealed class PageHostControl : Panel
{
    /// <summary>The page to render. Reassigning rebuilds all children.</summary>
    public static readonly StyledProperty<PageLayout?> PageProperty =
        AvaloniaProperty.Register<PageHostControl, PageLayout?>(nameof(Page));

    /// <summary>Height of one grid row in pixels.</summary>
    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<PageHostControl, double>(nameof(CellHeight), PageMetrics.DefaultCellHeight);

    /// <summary>Gap between cells in pixels.</summary>
    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<PageHostControl, double>(nameof(CellGap), PageMetrics.DefaultCellGap);

    static PageHostControl()
    {
        PageProperty.Changed.AddClassHandler<PageHostControl>((c, _) => c.RebuildChildren());
        AffectsMeasure<PageHostControl>(PageProperty, CellHeightProperty, CellGapProperty);
    }

    /// <summary>The page to render.</summary>
    public PageLayout? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>Height of one grid row in pixels.</summary>
    public double CellHeight { get => GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }

    /// <summary>Gap between cells in pixels.</summary>
    public double CellGap { get => GetValue(CellGapProperty); set => SetValue(CellGapProperty, value); }

    private void RebuildChildren()
    {
        Children.Clear();
        if (Page is null) return;
        foreach (var widget in Page.Widgets)
            Children.Add(WidgetTileFactory.Create(widget));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Page is null || Page.Widgets.Count == 0 || Children.Count != Page.Widgets.Count)
            return new Size(0, 0);

        // Width must be finite (the hosting ScrollViewer disables horizontal scrolling).
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;

        var rows = 0;
        for (var i = 0; i < Page.Widgets.Count; i++)
        {
            var px = PageGeometry.ToPixelRect(Page.Widgets[i].Rect, width, Page.GridColumns, CellHeight, CellGap);
            Children[i].Measure(new Size(px.Width, px.Height));
            if (Page.Widgets[i].Rect.Bottom > rows) rows = Page.Widgets[i].Rect.Bottom;
        }
        return new Size(width, PageGeometry.TotalHeight(rows, CellHeight, CellGap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Page is null || Children.Count != Page.Widgets.Count)
            return finalSize;

        for (var i = 0; i < Page.Widgets.Count; i++)
        {
            var px = PageGeometry.ToPixelRect(Page.Widgets[i].Rect, finalSize.Width, Page.GridColumns, CellHeight, CellGap);
            Children[i].Arrange(new Rect(px.X, px.Y, px.Width, px.Height));
        }
        return finalSize;
    }
}
```

- [ ] **Step 3: Verify + Commit**

Run: `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` → 0 warnings. Full Core suite once → 459/459 (no Core changes; regression guard).

```bash
git add src/NexusMonitor.UI/Controls/PageHostControl.cs src/NexusMonitor.UI/Controls/PageMetrics.cs
git commit -m "feat(pages): PageHostControl panel rendering PageLayout via PageGeometry"
```

---

### Task 5: Flag-gated Dashboard wiring

**Files:**
- Modify: `src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs` (two properties + load)
- Modify: `src/NexusMonitor.UI/Views/DashboardView.axaml` (xmlns + sibling visibility gating)

**Interfaces:**
- Consumes: `AppSettings.EnablePageEngine` (Task 2), `PageHostControl` (Task 4), `BuiltInPageLayouts` (Phase 1).
- Produces: `DashboardViewModel.UsePageEngine` (bool, read-once) and `DashboardViewModel.EnginePage` (PageLayout?).

- [ ] **Step 1: ViewModel additions**

`DashboardViewModel`'s constructor already receives `AppSettings` (recon: ctor deps at DashboardViewModel.cs:72). Add plain get-only properties (no change notification needed — set once in ctor before binding):

```csharp
    /// <summary>True when the Dashboard renders through the page engine (EnablePageEngine at startup).</summary>
    public bool UsePageEngine { get; }

    /// <summary>The page rendered by the engine path; null when the flag is off or the factory layout failed to load.</summary>
    public PageLayout? EnginePage { get; }
```
In the constructor, after existing assignments (add `using NexusMonitor.Core.Pages;`):
```csharp
        UsePageEngine = settings.EnablePageEngine;
        if (UsePageEngine)
        {
            try
            {
                EnginePage = BuiltInPageLayouts.Load("dashboard");
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogError(ex, "Page engine factory layout failed to load; falling back to classic dashboard");
                UsePageEngine = false;
            }
        }
```
IMPORTANT: match the actual field names — the ctor parameter may be `AppSettings settings` or a wrapper like `settings.Current`; and use the class's existing logger field if one exists (check; if the VM has no ILogger, drop the log line and set `UsePageEngine = false;` silently with a `// packaging bug — never a user path` comment). `UsePageEngine` must be assignable in ctor — declare `public bool UsePageEngine { get; }` and assign only in ctor (init-once), which requires converting the try/catch to compute a local first:
```csharp
        var usePageEngine = settings.EnablePageEngine;
        PageLayout? enginePage = null;
        if (usePageEngine)
        {
            try { enginePage = BuiltInPageLayouts.Load("dashboard"); }
            catch (InvalidOperationException) { usePageEngine = false; }
        }
        UsePageEngine = usePageEngine;
        EnginePage = enginePage;
```
Use this second form — it compiles cleanly with get-only properties.

- [ ] **Step 2: View wiring**

In `DashboardView.axaml`: add `xmlns:controls="using:NexusMonitor.UI.Controls"` to the root element. Then gate the existing content `ScrollViewer` (recon: line ~41) with `IsVisible="{Binding !UsePageEngine}"`, and add directly after it (same parent DockPanel):

```xml
    <ScrollViewer IsVisible="{Binding UsePageEngine}"
                  HorizontalScrollBarVisibility="Disabled"
                  VerticalScrollBarVisibility="Auto"
                  Padding="20,0,20,20">
      <controls:PageHostControl Page="{Binding EnginePage}"/>
    </ScrollViewer>
```
(`x:CompileBindings` is False in this file, so the `!` negation binding works as elsewhere in the codebase; the header Border above both ScrollViewers stays shared.)

- [ ] **Step 3: Verify + Commit**

Run: `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` → 0 warnings.

```bash
git add src/NexusMonitor.UI/ViewModels/DashboardViewModel.cs src/NexusMonitor.UI/Views/DashboardView.axaml
git commit -m "feat(pages): Dashboard renders via PageHostControl behind EnablePageEngine flag"
```

---

### Task 6: Manual smoke + constant freeze + changelog

**Files:**
- Possibly modify: `src/NexusMonitor.UI/Controls/PageMetrics.cs` (only if smoke says the constants look wrong)
- Modify: `CHANGELOG.md` (Unreleased section)

- [ ] **Step 1: Smoke with the flag OFF (default)**

Publish and run ~15s exactly as the Phase 1 audit tester did (`dotnet publish src/NexusMonitor.UI -c Release -p:PublishProfile=osx-arm64`, run the binary via nohup, sample `ps`, kill). Expected: identical classic dashboard, no crash, no console errors.

- [ ] **Step 2: Smoke with the flag ON**

Locate the settings file path from `SettingsService` source (Core/Services/SettingsService.cs — the path idiom near its Save/Load), set `"enablePageEngine": true` (verify the JSON casing the serializer actually writes by toggling the new Settings checkbox once, or by checking SettingsService's JsonSerializerOptions), run again ~20s. Expected: app runs clean; Dashboard shows three placeholder tiles (Health Score 4-wide, CPU 8-wide on row 0; Memory full-width below). If a windowed session allows, capture `screencapture` for the report; if not, "ran 20s, no crash, no errors in log" is the acceptance bar. Restore the setting to false afterwards.

- [ ] **Step 3: Constant check**

If the screenshot/visual clearly shows the 72/12 constants producing broken proportions, adjust `PageMetrics` once and re-smoke; otherwise leave frozen. Record the decision.

- [ ] **Step 4: CHANGELOG**

Under `## [Unreleased]`, add:

```markdown
### Added
- Page engine (experimental, off by default): the Dashboard can render through the new
  grid layout engine (`Settings → Experimental → Enable page engine`). Phase 2 of the
  page-customization system — placeholder tiles now, real widgets and edit mode next.
```

- [ ] **Step 5: Commit**

```bash
git add CHANGELOG.md src/NexusMonitor.UI/Controls/PageMetrics.cs
git commit -m "docs: changelog entry for experimental page engine; freeze phase-2 layout constants"
```
(Omit PageMetrics.cs from the add if unchanged.)

---

## Done means

Solution builds 0 warnings; Core suite 459/459; both smoke runs clean (flag off = unchanged dashboard, flag on = three placeholder tiles, no crash); constants frozen; changelog updated; every task committed on `feat/page-engine-phase2`. Phase 3 (edit mode + real widget extraction) plans against this.
