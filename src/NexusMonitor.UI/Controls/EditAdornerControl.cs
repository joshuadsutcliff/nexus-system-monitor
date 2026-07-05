using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Edit-mode overlay for PageHostControl: draws per-tile chrome (outline, remove box,
/// resize grip), owns all edit pointer interaction, and drives drag/resize gestures with a
/// ghost preview. Rendering and hit-testing share the same geometry (PageGeometry) so they can
/// never diverge. Inactive → invisible and hit-test-transparent (enforced via IsHitTestVisible).
/// Implements <see cref="ICustomHitTest"/> because most of the chrome is stroke-only (the tile
/// outline and resize grip have no fill) — Avalonia's default hit-testing follows actual painted
/// geometry, so an unfilled outline/grip is NOT hit-testable on its own merits, and presses over
/// the tile body or grip would silently fall through to the real widget content underneath. Only
/// the filled remove-box happened to be hit-testable without this override.</summary>
public sealed class EditAdornerControl : Control, ICustomHitTest
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

    public EditAdornerControl()
    {
        IsHitTestVisible = false;
    }

    static EditAdornerControl()
    {
        AffectsRender<EditAdornerControl>(PageProperty, CellHeightProperty, CellGapProperty, IsActiveProperty);
        IsActiveProperty.Changed.AddClassHandler<EditAdornerControl>((c, e) => c.IsHitTestVisible = (bool?)e.NewValue ?? false);
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

    /// <summary>Raised once per drag/resize gesture, on release, with the committed target rect
    /// (pre-push-down — the engine resolves collisions on commit). Never raised mid-gesture: the
    /// ghost preview is adorner-drawn only, so the layout mutates once per gesture, not per pointer-move.</summary>
    public event Action<Guid, GridRect>? MoveCommitted;

    // ── Chrome constants ──────────────────────────────────────────────────────

    private const double RemoveBox = 20;
    private const double Grip = 16;

    // Hoisted render objects: all inputs to these pens/brushes/glyph are compile-time constants,
    // so allocating them per-frame in Render (as Task 5 originally did) was pure waste — every
    // pointer-move during a gesture calls InvalidateVisual, so this now matters. Hoisted per T5
    // review note.
    private static readonly Pen AccentPen = new(new SolidColorBrush(Color.FromArgb(0xEE, 0x4C, 0x8D, 0xFF)), 1.5);
    private static readonly SolidColorBrush DimBrush = new(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush GlyphBrush = new(Colors.White);
    private static readonly FormattedText RemoveGlyphText = new("✕", System.Globalization.CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Typeface.Default, 12, GlyphBrush);

    // Ghost tint: blue = clean drop onto empty cells, amber = will push other tiles down.
    // IsValidPlacement is used ADVISORILY here (tint only) — the commit always succeeds via
    // MoveWidget's push-down, regardless of what the ghost shows.
    private static readonly SolidColorBrush GhostCleanBrush = new(Color.FromArgb(0x33, 0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush GhostPushBrush = new(Color.FromArgb(0x33, 0xFF, 0x8D, 0x4C));
    private static readonly Pen GhostCleanPen = new(GhostCleanBrush, 2);
    private static readonly Pen GhostPushPen = new(GhostPushBrush, 2);

    // ── Gesture state ─────────────────────────────────────────────────────────
    // Reset together at the end of every gesture (release) so no state can leak into the next
    // press/session: _dragId back to null, _resizing false, _ghost null.
    //
    // Gesture identity is the widget's InstanceId, not its list index: the bound Page can be
    // swapped out from under a live gesture (Cancel via keyboard focus while pointer is
    // captured, Undo, a second pointer), reshuffling or shrinking Widgets. An index captured at
    // press would then point at the wrong tile — or past the end of the list. _dragId is
    // re-resolved against the *current* Page on every event via FindWidget, and any event that
    // can't resolve it aborts the gesture cleanly instead of indexing blind.

    private Guid? _dragId;
    private bool _resizing;
    private Point _pressOffset;      // pointer offset inside the tile at press (drag)
    private GridRect? _ghost;        // candidate cell rect under the pointer

    /// <summary>Pixel rect for a cell-grid rect, derived from Core's PageGeometry. Shared by
    /// <see cref="TileRect"/> and the gesture math (<see cref="CellRectForResize"/>) so chrome,
    /// hit-zones, and resize origins can never diverge.</summary>
    private Rect PixelRect(GridRect cell)
    {
        var px = PageGeometry.ToPixelRect(cell, Bounds.Width, Page!.GridColumns, CellHeight, CellGap);
        return new Rect(px.X, px.Y, px.Width, px.Height);
    }

    /// <summary>Pixel rect for tile <paramref name="i"/> by its current list index. Only safe to call
    /// with an index freshly derived from <c>Page.Widgets</c> in the same call (HitTest, Render, or the
    /// press handler) — gesture code that spans multiple events must resolve by InstanceId instead,
    /// since the Page (and therefore the list) can change between events.</summary>
    private Rect TileRect(int i) => PixelRect(Page!.Widgets[i].Rect);

    /// <summary><see cref="ICustomHitTest"/> root-cause fix: without this, Avalonia's default
    /// hit-testing for a plain <see cref="Control"/> follows actual painted geometry rather than
    /// the logical <see cref="Visual.Bounds"/> rect. <see cref="Render"/> draws the tile outline
    /// (stroke only, no fill) and the resize grip (stroke-only diagonal lines) — neither paints a
    /// filled region — so without this override, only the filled remove-box registers a hit;
    /// presses over the tile body or grip silently fall through to the real widget content
    /// underneath (PageHostControl's children), and drag/resize gestures can never begin. This
    /// makes the whole Bounds hit-testable while active, matching the doc'd intent that the
    /// adorner "owns all edit pointer interaction"; <see cref="OnPointerPressed"/> still resolves
    /// the specific zone (body/remove/grip) via <see cref="HitTest"/> below.</summary>
    bool ICustomHitTest.HitTest(Point point) => IsActive && new Rect(Bounds.Size).Contains(point);

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

    /// <summary>Cell width / pixel strides derived from Core's PageGeometry, shared by both gesture helpers
    /// so drag and resize math stay in lockstep with the render/hit-test geometry.</summary>
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
        var origin = PixelRect(w.Rect).TopLeft;
        var colSpan = Math.Max(1, (int)Math.Round((p.X - origin.X) / strideX));
        var rowSpan = Math.Max(1, (int)Math.Round((p.Y - origin.Y) / strideY));
        return new GridRect(w.Rect.Col, w.Rect.Row, colSpan, rowSpan).ClampTo(Page!.GridColumns);
    }

    /// <summary>Draws per-tile chrome: accent outline, remove box with a centered ✕, and a diagonal-hatch resize grip,
    /// then — mid-gesture — the candidate ghost rect tinted per <see cref="PageLayoutEngine.IsValidPlacement"/>.
    /// No-op when inactive or the page is unset.</summary>
    public override void Render(DrawingContext ctx)
    {
        if (!IsActive || Page is null) return;

        for (var i = 0; i < Page.Widgets.Count; i++)
        {
            var r = TileRect(i);
            ctx.DrawRectangle(null, AccentPen, r, 8, 8);

            var remove = new Rect(r.Right - RemoveBox, r.Y, RemoveBox, RemoveBox);
            ctx.DrawRectangle(DimBrush, null, remove, 4, 4);
            ctx.DrawText(RemoveGlyphText, new Point(remove.X + (RemoveBox - RemoveGlyphText.Width) / 2,
                remove.Y + (RemoveBox - RemoveGlyphText.Height) / 2));

            var grip = new Rect(r.Right - Grip, r.Bottom - Grip, Grip, Grip);
            for (var g = 4; g <= 12; g += 4)
                ctx.DrawLine(AccentPen, new Point(grip.Right - g, grip.Bottom), new Point(grip.Right, grip.Bottom - g));
        }

        if (_ghost is not null)
        {
            var widget = _dragId is { } id ? Page.FindWidget(id) : null;
            if (widget is null)
            {
                // The dragged widget no longer exists on this Page (swapped mid-gesture, e.g. by
                // Cancel/Undo while this control still held pointer capture) — abort instead of
                // drawing a ghost for a vanished tile or indexing into the wrong one.
                AbortGesture();
            }
            else
            {
                var px = PageGeometry.ToPixelRect(_ghost, Bounds.Width, Page.GridColumns, CellHeight, CellGap);
                var ghostRect = new Rect(px.X, px.Y, px.Width, px.Height);
                var legal = PageLayoutEngine.IsValidPlacement(Page, _ghost, ignoreInstanceId: widget.InstanceId);
                var (fill, pen) = legal ? (GhostCleanBrush, GhostCleanPen) : (GhostPushBrush, GhostPushPen);
                ctx.DrawRectangle(fill, pen, ghostRect, 8, 8);
            }
        }
    }

    /// <summary>Resets all gesture state — because the gesture ended normally, or because the bound
    /// Page changed mid-gesture and the dragged widget can no longer be resolved by InstanceId.
    /// Idempotent: safe to call even when no gesture is active.</summary>
    private void AbortGesture()
    {
        _dragId = null;
        _resizing = false;
        _ghost = null;
        InvalidateVisual();
    }

    /// <summary>Hit-tests the press point: a remove-box hit raises <see cref="RemoveRequested"/> and marks the
    /// event handled; a body/grip hit begins a drag/resize gesture and captures the pointer (capture happens
    /// only after the zone check succeeds — the ColorWheelControl idiom — so a miss never steals input from
    /// controls beneath the adorner). Any miss (inactive, no page, or outside all zones) leaves the event
    /// unhandled so scroll/pointer input continues to reach controls beneath the adorner.</summary>
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
        else
        {
            var index = hit.Value.Index;
            _dragId = Page!.Widgets[index].InstanceId;
            _resizing = hit.Value.Zone == 2;
            var r = TileRect(index);
            _pressOffset = e.GetPosition(this) - r.TopLeft;
            e.Pointer.Capture(this);   // capture ONLY on a valid zone (ColorWheel idiom)
            e.Handled = true;
        }
    }

    /// <summary>Mid-gesture: recomputes the candidate ghost cell rect under the pointer and redraws.
    /// No-op unless this control holds pointer capture from a body/grip press. Re-resolves the dragged
    /// widget by InstanceId every event (not by the index captured at press) because the bound Page can
    /// be swapped mid-gesture (Cancel, Undo, a second pointer); if it no longer resolves — or the adorner
    /// was deactivated mid-gesture — the gesture is aborted cleanly rather than indexing into a stale list.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured != this) return;
        if (!IsActive) { AbortGesture(); return; }

        if (Page is null || _dragId is null) { AbortGesture(); return; }
        var widget = Page.FindWidget(_dragId.Value);
        if (widget is null) { AbortGesture(); return; }

        var p = e.GetPosition(this);
        _ghost = _resizing ? CellRectForResize(widget, p) : CellRectForDrag(widget, p);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Ends the gesture: always uncaptures first, then — only if the dragged widget still
    /// resolves on the current Page and a candidate ghost was set — raises <see cref="MoveCommitted"/>
    /// once with the final rect. If the Page was swapped mid-gesture and the widget vanished, no commit
    /// fires (there is nothing sane to commit against). The layout itself is not touched here; the caller
    /// (DashboardView → DashboardViewModel.EditMove) owns the single engine mutation. Gesture state is
    /// always cleared, so nothing survives to the next press.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);       // ALWAYS release capture, unconditionally, first

        var widget = Page is not null && _dragId is { } id ? Page.FindWidget(id) : null;
        if (widget is not null && _ghost is not null)
            MoveCommitted?.Invoke(widget.InstanceId, _ghost);

        _dragId = null;
        _resizing = false;
        _ghost = null;
        InvalidateVisual();
    }
}
