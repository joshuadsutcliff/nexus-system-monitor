# Page Engine Phase 1 (Core Model + Layout Engine + Serialization) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the platform-neutral core of the page-customization system — the page/widget model, the tiling layout engine (placement, push-down collision, compaction), and versioned serialization — fully unit-tested, with zero UI dependencies.

**Architecture:** New `NexusMonitor.Core.Pages` namespace containing immutable records (`PageLayout`, `WidgetInstance`, `GridRect`, `PopOutState`), a pure static `PageLayoutEngine` (all tiling math), and a `PageLayoutSerializer` (schema-versioned JSON envelope, lossless `ConfigJson` passthrough). This is Phase 1 of 7 from the spec `docs/superpowers/specs/2026-07-04-tab-customization-design.md`; later phases (rendering, edit mode, widgets, profiles, pop-outs) each get their own plan written against this landed API.

**Tech Stack:** .NET 8, C# 12, System.Text.Json (already used in Core), xUnit in `tests/NexusMonitor.Core.Tests`.

## Global Constraints

- `TreatWarningsAsErrors=true` and `Nullable=enable` repo-wide — zero warnings allowed.
- No new NuGet dependencies. Serialization uses System.Text.Json only.
- All Phase-1 code goes in `src/NexusMonitor.Core/Pages/` — no references to Avalonia, UI, Hosting, or platform projects.
- Match existing Core style: file-scoped namespaces, `sealed` records, XML doc comments on public members.
- Grid semantics (from spec §3.1): 12 columns default, uniform-height rows, page grows downward (no row limit), `Col`/`Row` are 0-based.
- Build/test commands on this machine (macOS, SDK not on PATH):
  `export DOTNET_ROOT=$HOME/.dotnet` then `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` and `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~<TestClass>"`.
- In C#, TDD "red" for a not-yet-written type is a **compile error** (e.g. CS0246) — that counts as the failing state for "verify it fails" steps.
- **Doc-comment ruling (review precedent, Tasks 1-2):** XML doc comments go on EVERY public member — methods, properties, and consts included — even where a brief's sample code omits them. Add a one-line `/// <summary>` when the sample lacks one.
- **Test style ruling (owner decision, 2026-07-04):** the repo's test convention is FluentAssertions (`.Should()`), and it governs. The briefs' test code blocks are written with raw xUnit `Assert.*` — translate assertions to FluentAssertions idiom when writing the actual test files (structure, test names, and case values stay exactly as specified). Also: `using` directives go above the `namespace` declaration, matching existing test files.
- Commit after every task (not every step) unless a step says otherwise; commit messages given per task.

---

### Task 1: GridRect geometry

**Files:**
- Create: `src/NexusMonitor.Core/Pages/GridRect.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/GridRectTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `GridRect(int Col, int Row, int ColSpan, int RowSpan)` record with `int Right` (exclusive), `int Bottom` (exclusive), `bool Intersects(GridRect other)`, `bool FitsWithinColumns(int gridColumns)`, `GridRect ClampTo(int gridColumns)`. All later tasks use these exact names.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using NexusMonitor.Core.Pages;
using Xunit;

public class GridRectTests
{
    [Fact]
    public void RightAndBottom_AreExclusiveEdges()
    {
        var r = new GridRect(Col: 2, Row: 1, ColSpan: 3, RowSpan: 2);
        Assert.Equal(5, r.Right);
        Assert.Equal(3, r.Bottom);
    }

    [Theory]
    [InlineData(0, 0, 2, 2, 1, 1, 2, 2, true)]   // overlapping corner
    [InlineData(0, 0, 2, 2, 2, 0, 2, 2, false)]  // touching edges don't intersect
    [InlineData(0, 0, 2, 2, 0, 2, 2, 2, false)]  // stacked, touching rows
    [InlineData(0, 0, 4, 1, 1, 0, 1, 1, true)]   // containment
    public void Intersects_UsesExclusiveEdges(
        int c1, int r1, int cs1, int rs1, int c2, int r2, int cs2, int rs2, bool expected)
    {
        var a = new GridRect(c1, r1, cs1, rs1);
        var b = new GridRect(c2, r2, cs2, rs2);
        Assert.Equal(expected, a.Intersects(b));
        Assert.Equal(expected, b.Intersects(a));
    }

    [Theory]
    [InlineData(10, 0, 3, 1, 12, false)] // spills past column 12
    [InlineData(9, 0, 3, 1, 12, true)]   // exactly reaches edge
    [InlineData(-1, 0, 2, 1, 12, false)] // negative col
    [InlineData(0, -1, 2, 1, 12, false)] // negative row
    public void FitsWithinColumns_ValidatesBounds(int col, int row, int cs, int rs, int cols, bool expected)
    {
        Assert.Equal(expected, new GridRect(col, row, cs, rs).FitsWithinColumns(cols));
    }

    [Fact]
    public void ClampTo_MovesAndShrinksIntoGrid()
    {
        Assert.Equal(new GridRect(9, 0, 3, 1), new GridRect(10, 0, 3, 1).ClampTo(12));   // shift left
        Assert.Equal(new GridRect(0, 0, 12, 1), new GridRect(0, 0, 15, 1).ClampTo(12));  // shrink span
        Assert.Equal(new GridRect(0, 0, 2, 1), new GridRect(-2, -1, 2, 1).ClampTo(12));  // negative → origin
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~GridRectTests"`
Expected: build FAILS with CS0246 (`GridRect` not found).

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>A cell-aligned rectangle on a page grid. Col/Row are 0-based; Right/Bottom are exclusive.</summary>
public sealed record GridRect(int Col, int Row, int ColSpan, int RowSpan)
{
    public int Right => Col + ColSpan;
    public int Bottom => Row + RowSpan;

    /// <summary>True when the rectangles share at least one cell. Touching edges do not intersect.</summary>
    public bool Intersects(GridRect other) =>
        Col < other.Right && other.Col < Right &&
        Row < other.Bottom && other.Row < Bottom;

    /// <summary>True when the rect lies fully inside a grid of the given column count (rows are unbounded downward).</summary>
    public bool FitsWithinColumns(int gridColumns) =>
        Col >= 0 && Row >= 0 && ColSpan >= 1 && RowSpan >= 1 && Right <= gridColumns;

    /// <summary>Returns the nearest valid rect inside the grid: origin clamped to 0, span capped to the column count, then shifted left if it spills.</summary>
    public GridRect ClampTo(int gridColumns)
    {
        var colSpan = Math.Clamp(ColSpan, 1, gridColumns);
        var rowSpan = Math.Max(1, RowSpan);
        var col = Math.Max(0, Col);
        var row = Math.Max(0, Row);
        if (col + colSpan > gridColumns) col = gridColumns - colSpan;
        return new GridRect(col, row, colSpan, rowSpan);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~GridRectTests"`
Expected: PASS (9 test cases).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/GridRect.cs tests/NexusMonitor.Core.Tests/Pages/GridRectTests.cs
git commit -m "feat(pages): GridRect cell geometry with exclusive-edge intersection"
```

---

### Task 2: Page model records

**Files:**
- Create: `src/NexusMonitor.Core/Pages/PageModel.cs` (PopOutState, WidgetInstance, PageLayout — small, cohesive, change together)
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageModelTests.cs`

**Interfaces:**
- Consumes: `GridRect` (Task 1).
- Produces (exact shapes used by every later task):
  - `PopOutState(bool IsPoppedOut, int X, int Y, int Width, int Height, bool Topmost)`
  - `WidgetInstance(Guid InstanceId, string WidgetTypeId, GridRect Rect, string? ConfigJson = null, PopOutState? PopOut = null)`
  - `PageLayout(string PageId, string Title, string IconKey, int GridColumns, IReadOnlyList<WidgetInstance> Widgets)` with `const int DefaultGridColumns = 12`, `PageLayout WithWidgets(IReadOnlyList<WidgetInstance> widgets)`, and `WidgetInstance? FindWidget(Guid instanceId)`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using NexusMonitor.Core.Pages;
using Xunit;

public class PageModelTests
{
    private static WidgetInstance W(Guid id, int col, int row) =>
        new(id, "nexus.test", new GridRect(col, row, 2, 2));

    [Fact]
    public void DefaultGridColumns_Is12()
    {
        Assert.Equal(12, PageLayout.DefaultGridColumns);
    }

    [Fact]
    public void FindWidget_ReturnsMatchOrNull()
    {
        var id = Guid.NewGuid();
        var page = new PageLayout("dash", "Dashboard", "icon.dashboard",
            PageLayout.DefaultGridColumns, new[] { W(id, 0, 0) });

        Assert.NotNull(page.FindWidget(id));
        Assert.Null(page.FindWidget(Guid.NewGuid()));
    }

    [Fact]
    public void WithWidgets_ReplacesListOnly()
    {
        var page = new PageLayout("dash", "Dashboard", "icon.dashboard",
            PageLayout.DefaultGridColumns, Array.Empty<WidgetInstance>());
        var updated = page.WithWidgets(new[] { W(Guid.NewGuid(), 0, 0) });

        Assert.Single(updated.Widgets);
        Assert.Empty(page.Widgets);            // original untouched (immutability)
        Assert.Equal("dash", updated.PageId);  // identity fields preserved
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageModelTests"`
Expected: build FAILS with CS0246 (`PageLayout` not found).

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>Persisted geometry/state of a widget torn off into its own OS window.</summary>
public sealed record PopOutState(bool IsPoppedOut, int X, int Y, int Width, int Height, bool Topmost);

/// <summary>One placed widget on a page. ConfigJson is opaque raw JSON owned by the widget type (preserved verbatim by serialization).</summary>
public sealed record WidgetInstance(
    Guid InstanceId,
    string WidgetTypeId,
    GridRect Rect,
    string? ConfigJson = null,
    PopOutState? PopOut = null);

/// <summary>A page's complete layout. Immutable; engine operations return new instances.</summary>
public sealed record PageLayout(
    string PageId,
    string Title,
    string IconKey,
    int GridColumns,
    IReadOnlyList<WidgetInstance> Widgets)
{
    public const int DefaultGridColumns = 12;

    public PageLayout WithWidgets(IReadOnlyList<WidgetInstance> widgets) => this with { Widgets = widgets };

    public WidgetInstance? FindWidget(Guid instanceId)
    {
        foreach (var w in Widgets)
            if (w.InstanceId == instanceId) return w;
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageModelTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageModel.cs tests/NexusMonitor.Core.Tests/Pages/PageModelTests.cs
git commit -m "feat(pages): PageLayout/WidgetInstance/PopOutState model records"
```

---

### Task 3: Engine — placement validation

**Files:**
- Create: `src/NexusMonitor.Core/Pages/PageLayoutEngine.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutEngineValidationTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2 types.
- Produces: `static class PageLayoutEngine` with `static bool IsValidPlacement(PageLayout page, GridRect rect, Guid? ignoreInstanceId = null)` — true when `rect` fits the grid and overlaps no widget (optionally ignoring one instance, used later for move-in-place). Tasks 4–6 add more members to this same class.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using NexusMonitor.Core.Pages;
using Xunit;

public class PageLayoutEngineValidationTests
{
    private static readonly Guid IdA = Guid.NewGuid();

    private static PageLayout PageWithOneWidget() =>
        new("p", "P", "icon.p", 12,
            new[] { new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)) });

    [Fact]
    public void ValidPlacement_EmptyArea_IsAccepted()
    {
        Assert.True(PageLayoutEngine.IsValidPlacement(PageWithOneWidget(), new GridRect(4, 0, 4, 2)));
    }

    [Fact]
    public void OverlappingPlacement_IsRejected()
    {
        Assert.False(PageLayoutEngine.IsValidPlacement(PageWithOneWidget(), new GridRect(2, 1, 4, 2)));
    }

    [Fact]
    public void OutOfBounds_IsRejected()
    {
        Assert.False(PageLayoutEngine.IsValidPlacement(PageWithOneWidget(), new GridRect(10, 0, 4, 1)));
    }

    [Fact]
    public void OverlapWithIgnoredInstance_IsAccepted()
    {
        // Moving A onto its own footprint must be legal.
        Assert.True(PageLayoutEngine.IsValidPlacement(
            PageWithOneWidget(), new GridRect(1, 0, 4, 2), ignoreInstanceId: IdA));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEngineValidationTests"`
Expected: build FAILS with CS0246 (`PageLayoutEngine` not found).

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

/// <summary>Pure tiling logic for page layouts: validation, placement with push-down, movement, compaction.
/// UI drag previews and commits both call these methods, so preview always equals final behavior.</summary>
public static class PageLayoutEngine
{
    /// <summary>True when <paramref name="rect"/> fits the grid and overlaps no widget
    /// (optionally ignoring one instance — pass the instance being moved).</summary>
    public static bool IsValidPlacement(PageLayout page, GridRect rect, Guid? ignoreInstanceId = null)
    {
        if (!rect.FitsWithinColumns(page.GridColumns)) return false;
        foreach (var w in page.Widgets)
        {
            if (w.InstanceId == ignoreInstanceId) continue;
            if (w.Rect.Intersects(rect)) return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEngineValidationTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageLayoutEngine.cs tests/NexusMonitor.Core.Tests/Pages/PageLayoutEngineValidationTests.cs
git commit -m "feat(pages): PageLayoutEngine placement validation"
```

---

### Task 4: Engine — place with push-down collision

**Files:**
- Modify: `src/NexusMonitor.Core/Pages/PageLayoutEngine.cs` (add methods to the class from Task 3)
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutEnginePlacementTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `static PageLayout PlaceWidget(PageLayout page, WidgetInstance widget)` — clamps the widget's rect to the grid, adds it, and pushes any overlapped widgets straight down (cascading) until nothing overlaps. Deterministic: colliders are processed in (Row, Col) order. Task 5's `MoveWidget` reuses the internal push-down helper `ResolveCollisions`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using System.Linq;
using NexusMonitor.Core.Pages;
using Xunit;

public class PageLayoutEnginePlacementTests
{
    private static WidgetInstance W(string tag, int col, int row, int cs = 4, int rs = 2) =>
        new(GuidFrom(tag), "nexus.test", new GridRect(col, row, cs, rs));

    // Stable ids so tests can find widgets after engine ops.
    private static Guid GuidFrom(string tag) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(tag)));

    private static GridRect RectOf(PageLayout page, string tag) => page.FindWidget(GuidFrom(tag))!.Rect;

    private static PageLayout Empty() =>
        new("p", "P", "icon.p", 12, System.Array.Empty<WidgetInstance>());

    [Fact]
    public void Place_OnEmptyArea_AddsWithoutMovingAnything()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 0, 0));
        Assert.Single(page.Widgets);
        Assert.Equal(new GridRect(0, 0, 4, 2), RectOf(page, "a"));
    }

    [Fact]
    public void Place_OntoOccupiedCells_PushesOccupantDown()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 0, 0));
        page = PageLayoutEngine.PlaceWidget(page, W("b", 0, 0)); // lands on top of a

        Assert.Equal(new GridRect(0, 0, 4, 2), RectOf(page, "b")); // newcomer keeps its spot
        Assert.Equal(new GridRect(0, 2, 4, 2), RectOf(page, "a")); // occupant pushed below newcomer
    }

    [Fact]
    public void Place_PushDown_Cascades()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 0, 0));
        page = PageLayoutEngine.PlaceWidget(page, W("b", 0, 2));
        page = PageLayoutEngine.PlaceWidget(page, W("c", 0, 0)); // pushes a, which must push b

        Assert.Equal(new GridRect(0, 0, 4, 2), RectOf(page, "c"));
        Assert.Equal(new GridRect(0, 2, 4, 2), RectOf(page, "a"));
        Assert.Equal(new GridRect(0, 4, 4, 2), RectOf(page, "b"));
    }

    [Fact]
    public void Place_OutOfBoundsRect_IsClampedIntoGrid()
    {
        var page = PageLayoutEngine.PlaceWidget(Empty(), W("a", 11, 0)); // 4-wide at col 11 spills
        Assert.Equal(new GridRect(8, 0, 4, 2), RectOf(page, "a"));
    }

    [Fact]
    public void Place_NeverLeavesOverlaps()
    {
        var page = Empty();
        foreach (var tag in new[] { "a", "b", "c", "d", "e" })
            page = PageLayoutEngine.PlaceWidget(page, W(tag, 0, 0, 6, 2)); // all dropped at origin

        var rects = page.Widgets.Select(w => w.Rect).ToList();
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++)
                Assert.False(rects[i].Intersects(rects[j]), $"widgets {i} and {j} overlap");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEnginePlacementTests"`
Expected: build FAILS with CS0117 (`PageLayoutEngine` has no `PlaceWidget`).

- [ ] **Step 3: Write the implementation** (add inside `PageLayoutEngine`)

```csharp
    /// <summary>Adds a widget at its rect (clamped into the grid), pushing overlapped widgets
    /// straight down — cascading — until no overlaps remain. The placed widget keeps its spot.</summary>
    public static PageLayout PlaceWidget(PageLayout page, WidgetInstance widget)
    {
        var clamped = widget with { Rect = widget.Rect.ClampTo(page.GridColumns) };
        var widgets = new List<WidgetInstance>(page.Widgets) { clamped };
        return page.WithWidgets(ResolveCollisions(widgets, pinnedId: clamped.InstanceId));
    }

    /// <summary>Push-down resolution: while any widget overlaps the pinned/settled set, move the
    /// topmost-leftmost offender down to just below whatever it overlaps. Deterministic and terminating
    /// (rows only ever increase; the grid is unbounded downward).</summary>
    private static IReadOnlyList<WidgetInstance> ResolveCollisions(List<WidgetInstance> widgets, Guid pinnedId)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mover in widgets
                         .Where(w => w.InstanceId != pinnedId)
                         .OrderBy(w => w.Rect.Row).ThenBy(w => w.Rect.Col).ToList())
            {
                var collidesWith = widgets
                    .Where(o => o.InstanceId != mover.InstanceId && o.Rect.Intersects(mover.Rect))
                    .ToList();
                if (collidesWith.Count == 0) continue;

                var newRow = collidesWith.Max(o => o.Rect.Bottom);
                var idx = widgets.FindIndex(w => w.InstanceId == mover.InstanceId);
                widgets[idx] = mover with { Rect = mover.Rect with { Row = newRow } };
                changed = true;
            }
        }
        return widgets;
    }
```

Also add `using System.Linq;` is unnecessary (file-scoped namespace + implicit usings are enabled in this repo — verify `ImplicitUsings` in `NexusMonitor.Core.csproj`; if disabled, add `using System.Collections.Generic; using System.Linq;` at the top).

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEnginePlacementTests"`
Expected: PASS (5 tests). Also re-run Task 3's filter — still PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageLayoutEngine.cs tests/NexusMonitor.Core.Tests/Pages/PageLayoutEnginePlacementTests.cs
git commit -m "feat(pages): PlaceWidget with cascading push-down collision resolution"
```

---

### Task 5: Engine — move and remove

**Files:**
- Modify: `src/NexusMonitor.Core/Pages/PageLayoutEngine.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutEngineMoveTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4 (`ResolveCollisions` reused internally).
- Produces: `static PageLayout MoveWidget(PageLayout page, Guid instanceId, GridRect target)` (returns page unchanged if the id is unknown; target clamped; mover is pinned, others push down) and `static PageLayout RemoveWidget(PageLayout page, Guid instanceId)`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using NexusMonitor.Core.Pages;
using Xunit;

public class PageLayoutEngineMoveTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();

    private static PageLayout TwoWidgets() =>
        new("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)),
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 0, 4, 2)),
        });

    [Fact]
    public void Move_ToEmptySpace_JustMoves()
    {
        var page = PageLayoutEngine.MoveWidget(TwoWidgets(), IdA, new GridRect(8, 0, 4, 2));
        Assert.Equal(new GridRect(8, 0, 4, 2), page.FindWidget(IdA)!.Rect);
        Assert.Equal(new GridRect(4, 0, 4, 2), page.FindWidget(IdB)!.Rect);
    }

    [Fact]
    public void Move_OntoOtherWidget_PushesItDown()
    {
        var page = PageLayoutEngine.MoveWidget(TwoWidgets(), IdA, new GridRect(4, 0, 4, 2));
        Assert.Equal(new GridRect(4, 0, 4, 2), page.FindWidget(IdA)!.Rect);
        Assert.Equal(new GridRect(4, 2, 4, 2), page.FindWidget(IdB)!.Rect);
    }

    [Fact]
    public void Move_UnknownId_ReturnsPageUnchanged()
    {
        var page = TwoWidgets();
        Assert.Same(page, PageLayoutEngine.MoveWidget(page, Guid.NewGuid(), new GridRect(8, 0, 4, 2)));
    }

    [Fact]
    public void Remove_DeletesOnlyThatWidget()
    {
        var page = PageLayoutEngine.RemoveWidget(TwoWidgets(), IdA);
        Assert.Null(page.FindWidget(IdA));
        Assert.NotNull(page.FindWidget(IdB));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEngineMoveTests"`
Expected: build FAILS with CS0117 (`MoveWidget` not found).

- [ ] **Step 3: Write the implementation** (add inside `PageLayoutEngine`)

```csharp
    /// <summary>Moves a widget to the target rect (clamped). The mover is pinned; anything it
    /// now overlaps pushes down. Unknown ids return the page unchanged (same instance).</summary>
    public static PageLayout MoveWidget(PageLayout page, Guid instanceId, GridRect target)
    {
        var existing = page.FindWidget(instanceId);
        if (existing is null) return page;

        var moved = existing with { Rect = target.ClampTo(page.GridColumns) };
        var widgets = page.Widgets
            .Select(w => w.InstanceId == instanceId ? moved : w)
            .ToList();
        return page.WithWidgets(ResolveCollisions(widgets, pinnedId: instanceId));
    }

    /// <summary>Removes a widget. Remaining widgets keep their positions (compaction is separate/explicit).</summary>
    public static PageLayout RemoveWidget(PageLayout page, Guid instanceId) =>
        page.FindWidget(instanceId) is null
            ? page
            : page.WithWidgets(page.Widgets.Where(w => w.InstanceId != instanceId).ToList());
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEngineMoveTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageLayoutEngine.cs tests/NexusMonitor.Core.Tests/Pages/PageLayoutEngineMoveTests.cs
git commit -m "feat(pages): MoveWidget (pinned push-down) and RemoveWidget"
```

---

### Task 6: Engine — vertical compaction

**Files:**
- Modify: `src/NexusMonitor.Core/Pages/PageLayoutEngine.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutEngineCompactTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces: `static PageLayout Compact(PageLayout page)` — processes widgets in (Row, Col) order, moving each up to the lowest row where it fits without overlap. Edit mode calls this after remove/move (Plasma behavior: gaps close upward).

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using NexusMonitor.Core.Pages;
using Xunit;

public class PageLayoutEngineCompactTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();
    private static readonly Guid IdC = Guid.NewGuid();

    [Fact]
    public void Compact_PullsWidgetsUpIntoGaps()
    {
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 3, 4, 2)),  // floating with gap above
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 5, 4, 1)),  // floating deeper
        });

        var compacted = PageLayoutEngine.Compact(page);

        Assert.Equal(new GridRect(0, 0, 4, 2), compacted.FindWidget(IdA)!.Rect);
        Assert.Equal(new GridRect(4, 0, 4, 1), compacted.FindWidget(IdB)!.Rect); // different columns → row 0 too
    }

    [Fact]
    public void Compact_StopsAtBlockers_SameColumns()
    {
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)),
            new WidgetInstance(IdB, "nexus.test", new GridRect(0, 5, 4, 2)),  // must land at row 2, not 0
        });

        var compacted = PageLayoutEngine.Compact(page);

        Assert.Equal(new GridRect(0, 0, 4, 2), compacted.FindWidget(IdA)!.Rect);
        Assert.Equal(new GridRect(0, 2, 4, 2), compacted.FindWidget(IdB)!.Rect);
    }

    [Fact]
    public void Compact_AlreadyCompact_IsStable()
    {
        var page = new PageLayout("p", "P", "icon.p", 12, new[]
        {
            new WidgetInstance(IdA, "nexus.test", new GridRect(0, 0, 4, 2)),
            new WidgetInstance(IdB, "nexus.test", new GridRect(4, 0, 4, 2)),
            new WidgetInstance(IdC, "nexus.test", new GridRect(0, 2, 8, 1)),
        });

        var compacted = PageLayoutEngine.Compact(page);

        Assert.Equal(page.FindWidget(IdA)!.Rect, compacted.FindWidget(IdA)!.Rect);
        Assert.Equal(page.FindWidget(IdB)!.Rect, compacted.FindWidget(IdB)!.Rect);
        Assert.Equal(page.FindWidget(IdC)!.Rect, compacted.FindWidget(IdC)!.Rect);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEngineCompactTests"`
Expected: build FAILS with CS0117 (`Compact` not found).

- [ ] **Step 3: Write the implementation** (add inside `PageLayoutEngine`)

```csharp
    /// <summary>Closes vertical gaps: in (Row, Col) order, each widget moves up to the lowest
    /// row where it fits without overlapping already-settled widgets.</summary>
    public static PageLayout Compact(PageLayout page)
    {
        var settled = new List<WidgetInstance>();
        foreach (var w in page.Widgets.OrderBy(x => x.Rect.Row).ThenBy(x => x.Rect.Col))
        {
            var row = 0;
            while (true)
            {
                var candidate = w.Rect with { Row = row };
                if (!settled.Any(s => s.Rect.Intersects(candidate)))
                {
                    settled.Add(w with { Rect = candidate });
                    break;
                }
                row++;
            }
        }
        // Preserve original list order (stable for serialization diffs).
        var byId = settled.ToDictionary(s => s.InstanceId);
        return page.WithWidgets(page.Widgets.Select(w => byId[w.InstanceId]).ToList());
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutEngineCompactTests"`
Expected: PASS (3 tests). Re-run all engine filters (`FullyQualifiedName~PageLayoutEngine`) — all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageLayoutEngine.cs tests/NexusMonitor.Core.Tests/Pages/PageLayoutEngineCompactTests.cs
git commit -m "feat(pages): vertical compaction closing layout gaps upward"
```

---

### Task 7: Serialization — versioned envelope, lossless round-trip

**Files:**
- Create: `src/NexusMonitor.Core/Pages/PageLayoutSerializer.cs`
- Test: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutSerializerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2 model types.
- Produces: `static class PageLayoutSerializer` with `const int CurrentSchemaVersion = 1`, `static string Serialize(PageLayout page)`, `static bool TryDeserialize(string json, out PageLayout? page, out string? error)`. Envelope shape (spec §8): `{"schemaVersion":1,"page":{...}}`. `ConfigJson` strings pass through verbatim (they are opaque widget-owned JSON *stored as a JSON string value* — no re-parsing, no normalization). Phase 5's profile serializer wraps this same envelope pattern.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using System.Text.Json;
using NexusMonitor.Core.Pages;
using Xunit;

public class PageLayoutSerializerTests
{
    private static PageLayout SamplePage()
    {
        var id = new Guid("11111111-2222-3333-4444-555555555555");
        return new PageLayout("dash", "Dashboard", "icon.dashboard", 12, new[]
        {
            new WidgetInstance(id, "nexus.cpu.chart", new GridRect(0, 0, 6, 3),
                ConfigJson: """{"timeWindowSeconds":60,"someFutureKey":[1,2,3]}""",
                PopOut: new PopOutState(true, 100, 200, 640, 360, Topmost: false)),
        });
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var json = PageLayoutSerializer.Serialize(SamplePage());
        Assert.True(PageLayoutSerializer.TryDeserialize(json, out var page, out var error), error);

        Assert.Equal(SamplePage(), page, PageLayoutComparer.Instance);
    }

    [Fact]
    public void Serialize_WritesVersionEnvelope()
    {
        using var doc = JsonDocument.Parse(PageLayoutSerializer.Serialize(SamplePage()));
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("page", out _));
    }

    [Fact]
    public void ConfigJson_SurvivesVerbatim_IncludingUnknownKeys()
    {
        var json = PageLayoutSerializer.Serialize(SamplePage());
        PageLayoutSerializer.TryDeserialize(json, out var page, out _);

        var config = JsonDocument.Parse(page!.Widgets[0].ConfigJson!);
        Assert.Equal(3, config.RootElement.GetProperty("someFutureKey").GetArrayLength());
    }
}

/// <summary>Structural equality for pages whose Widgets lists are IReadOnlyList (record Equals is reference-based for lists).</summary>
public sealed class PageLayoutComparer : IEqualityComparer<PageLayout>
{
    public static readonly PageLayoutComparer Instance = new();

    public bool Equals(PageLayout? x, PageLayout? y)
    {
        if (x is null || y is null) return ReferenceEquals(x, y);
        return x.PageId == y.PageId && x.Title == y.Title && x.IconKey == y.IconKey
            && x.GridColumns == y.GridColumns
            && x.Widgets.SequenceEqual(y.Widgets);
    }

    public int GetHashCode(PageLayout obj) => obj.PageId.GetHashCode();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutSerializerTests"`
Expected: build FAILS with CS0246 (`PageLayoutSerializer` not found).

- [ ] **Step 3: Write the implementation**

```csharp
namespace NexusMonitor.Core.Pages;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Schema-versioned JSON persistence for a single page layout.
/// Envelope: {"schemaVersion":N,"page":{...}}. ConfigJson is carried as an opaque string value.</summary>
public static class PageLayoutSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private sealed record Envelope(int SchemaVersion, PageLayout Page);

    public static string Serialize(PageLayout page) =>
        JsonSerializer.Serialize(new Envelope(CurrentSchemaVersion, page), Options);

    /// <summary>Never throws. False + error for malformed JSON, missing envelope fields,
    /// or a schemaVersion newer than this build understands.</summary>
    public static bool TryDeserialize(string json, out PageLayout? page, out string? error)
    {
        page = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
            if (envelope?.Page is null)
            {
                error = "Missing 'page' object in layout file.";
                return false;
            }
            if (envelope.SchemaVersion > CurrentSchemaVersion)
            {
                error = $"Layout schema version {envelope.SchemaVersion} is newer than supported ({CurrentSchemaVersion}).";
                return false;
            }
            page = envelope.Page;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid layout JSON: {ex.Message}";
            return false;
        }
    }
}
```

Note: records with positional constructors deserialize via constructor binding in System.Text.Json on .NET 8 — no attributes needed. `GridRect`'s computed `Right`/`Bottom` properties are get-only expression bodies, so they are serialized but harmlessly ignored on read (no setters); if `TreatWarningsAsErrors` surfaces nothing, leave as-is — the round-trip test is the arbiter.

- [ ] **Step 4: Run tests to verify they pass**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutSerializerTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/PageLayoutSerializer.cs tests/NexusMonitor.Core.Tests/Pages/PageLayoutSerializerTests.cs
git commit -m "feat(pages): versioned page-layout serialization with lossless ConfigJson"
```

---

### Task 8: Serialization — corrupt and hostile input handling

**Files:**
- Modify: `tests/NexusMonitor.Core.Tests/Pages/PageLayoutSerializerTests.cs` (append tests; implementation from Task 7 should already satisfy most — this task hardens gaps it exposes)
- Modify (only if a test fails): `src/NexusMonitor.Core/Pages/PageLayoutSerializer.cs`

**Interfaces:**
- Consumes: Task 7.
- Produces: guarantee relied on by Phase 5's profile store: `TryDeserialize` NEVER throws and always yields a human-readable `error` on failure.

- [ ] **Step 1: Write the (possibly already-passing) tests**

```csharp
    // Append inside PageLayoutSerializerTests:

    [Theory]
    [InlineData("")]                                      // empty
    [InlineData("not json at all {{{")]                   // garbage
    [InlineData("null")]                                  // JSON null
    [InlineData("""{"schemaVersion":1}""")]               // missing page
    [InlineData("""{"page":null,"schemaVersion":1}""")]   // null page
    [InlineData("""{"schemaVersion":999,"page":{"pageId":"x","title":"X","iconKey":"i","gridColumns":12,"widgets":[]}}""")] // future version
    public void TryDeserialize_HostileInput_ReturnsFalseWithError_NeverThrows(string json)
    {
        var ok = PageLayoutSerializer.TryDeserialize(json, out var page, out var error);
        Assert.False(ok);
        Assert.Null(page);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
```

- [ ] **Step 2: Run tests**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~PageLayoutSerializerTests"`
Expected: the `""` and `"null"` cases likely FAIL (empty string throws `JsonException` → caught → fine; `"null"` deserializes to a null envelope → `envelope?.Page is null` → fine). If all 6 PASS immediately, skip Step 3.

- [ ] **Step 3: Fix any failures (only if Step 2 failed)**

The one known gap: `JsonSerializer.Deserialize<Envelope>("")` throws `JsonException` — already caught. If an `ArgumentNullException` or other exception type surfaces for any input, widen the catch:

```csharp
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            error = $"Invalid layout JSON: {ex.Message}";
            return false;
        }
```

- [ ] **Step 4: Run full Pages suite**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~NexusMonitor.Core.Tests.Pages"`
Expected: PASS — all tasks' tests (≈27 cases).

- [ ] **Step 5: Commit**

```bash
git add tests/NexusMonitor.Core.Tests/Pages/PageLayoutSerializerTests.cs src/NexusMonitor.Core/Pages/PageLayoutSerializer.cs
git commit -m "test(pages): hostile-input guarantees for layout deserialization"
```

---

### Task 9: Factory default page — embedded resource + loader

**Files:**
- Create: `src/NexusMonitor.Core/Pages/Defaults/dashboard.default.json` (embedded resource)
- Create: `src/NexusMonitor.Core/Pages/BuiltInPageLayouts.cs`
- Modify: `src/NexusMonitor.Core/NexusMonitor.Core.csproj` (embed the resource)
- Test: `tests/NexusMonitor.Core.Tests/Pages/BuiltInPageLayoutsTests.cs`

**Interfaces:**
- Consumes: Task 7 serializer.
- Produces: `static class BuiltInPageLayouts` with `static PageLayout Load(string pageId)` (throws `InvalidOperationException` for unknown ids — factory resources are build-time artifacts; a missing one is a packaging bug, not user error) and `static IReadOnlyList<string> BuiltInPageIds` (currently `["dashboard"]`). Phase 2 renders this page behind the feature flag; phase 3 widget TypeIds referenced here (`nexus.widget.cpuChart`, `nexus.widget.memoryChart`, `nexus.widget.healthScore`) become real registry entries then — until then they're just strings, which the model explicitly supports (unknown-type preservation, spec §8).

- [ ] **Step 1: Write the failing test**

```csharp
namespace NexusMonitor.Core.Tests.Pages;

using NexusMonitor.Core.Pages;
using Xunit;

public class BuiltInPageLayoutsTests
{
    [Fact]
    public void Dashboard_LoadsFromEmbeddedResource()
    {
        var page = BuiltInPageLayouts.Load("dashboard");

        Assert.Equal("dashboard", page.PageId);
        Assert.Equal(12, page.GridColumns);
        Assert.True(page.Widgets.Count >= 3);
        // Factory layouts must themselves be valid: no overlaps, everything in-grid.
        for (var i = 0; i < page.Widgets.Count; i++)
        {
            Assert.True(page.Widgets[i].Rect.FitsWithinColumns(page.GridColumns));
            for (var j = i + 1; j < page.Widgets.Count; j++)
                Assert.False(page.Widgets[i].Rect.Intersects(page.Widgets[j].Rect));
        }
    }

    [Fact]
    public void UnknownPageId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => BuiltInPageLayouts.Load("nope"));
    }

    [Fact]
    public void BuiltInPageIds_ListsDashboard()
    {
        Assert.Contains("dashboard", BuiltInPageLayouts.BuiltInPageIds);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~BuiltInPageLayoutsTests"`
Expected: build FAILS with CS0246 (`BuiltInPageLayouts` not found).

- [ ] **Step 3: Create the resource, csproj entry, and loader**

`src/NexusMonitor.Core/Pages/Defaults/dashboard.default.json`:

```json
{
  "schemaVersion": 1,
  "page": {
    "pageId": "dashboard",
    "title": "Dashboard",
    "iconKey": "icon.dashboard",
    "gridColumns": 12,
    "widgets": [
      {
        "instanceId": "0b5e0001-0000-4000-8000-000000000001",
        "widgetTypeId": "nexus.widget.healthScore",
        "rect": { "col": 0, "row": 0, "colSpan": 4, "rowSpan": 2 }
      },
      {
        "instanceId": "0b5e0001-0000-4000-8000-000000000002",
        "widgetTypeId": "nexus.widget.cpuChart",
        "rect": { "col": 4, "row": 0, "colSpan": 8, "rowSpan": 2 }
      },
      {
        "instanceId": "0b5e0001-0000-4000-8000-000000000003",
        "widgetTypeId": "nexus.widget.memoryChart",
        "rect": { "col": 0, "row": 2, "colSpan": 12, "rowSpan": 2 }
      }
    ]
  }
}
```

(This is a Phase-1 skeleton proving the load path; Phase 3 replaces it with the full current-Dashboard-equivalent layout when the real widget TypeIds exist.)

In `NexusMonitor.Core.csproj`, inside an `<ItemGroup>`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Pages\Defaults\*.json" />
  </ItemGroup>
```

`src/NexusMonitor.Core/Pages/BuiltInPageLayouts.cs`:

```csharp
namespace NexusMonitor.Core.Pages;

using System.Reflection;

/// <summary>Factory-default page layouts, embedded as resources. These are the same serialized
/// format users edit — "reset to default" simply reloads these. A missing/invalid resource is a
/// packaging bug and throws; it is never a user-facing error path.</summary>
public static class BuiltInPageLayouts
{
    public static IReadOnlyList<string> BuiltInPageIds { get; } = new[] { "dashboard" };

    public static PageLayout Load(string pageId)
    {
        var resourceName = $"NexusMonitor.Core.Pages.Defaults.{pageId}.default.json";
        using var stream = typeof(BuiltInPageLayouts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No built-in page layout '{pageId}' (resource '{resourceName}' not found).");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        if (!PageLayoutSerializer.TryDeserialize(json, out var page, out var error))
            throw new InvalidOperationException($"Built-in page layout '{pageId}' is invalid: {error}");
        return page!;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass, then the full suite**

Run: `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release --filter "FullyQualifiedName~BuiltInPageLayoutsTests"`
Expected: PASS (3 tests).
Then: `/Users/josh/.dotnet/dotnet build NexusMonitor.sln -c Release` → 0 warnings, and `/Users/josh/.dotnet/dotnet test tests/NexusMonitor.Core.Tests -c Release` → full suite green (408 pre-existing + ≈30 new).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.Core/Pages/Defaults/dashboard.default.json src/NexusMonitor.Core/Pages/BuiltInPageLayouts.cs src/NexusMonitor.Core/NexusMonitor.Core.csproj tests/NexusMonitor.Core.Tests/Pages/BuiltInPageLayoutsTests.cs
git commit -m "feat(pages): embedded factory-default layouts with validated load path"
```

---

## Done means

All Pages tests green, full solution builds with 0 warnings, full Core suite green, every task committed. Phase 2 (PageHostControl + flagged Dashboard rendering) gets its own plan written against these exact APIs.
