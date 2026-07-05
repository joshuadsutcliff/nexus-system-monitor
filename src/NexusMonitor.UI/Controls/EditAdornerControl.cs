using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NexusMonitor.Core.Pages;

namespace NexusMonitor.UI.Controls;

/// <summary>Edit-mode overlay for PageHostControl: draws per-tile chrome (outline, remove box,
/// resize grip), owns all edit pointer interaction, and drives drag/resize gestures with a
/// ghost preview. Rendering and hit-testing share the same geometry (PageGeometry) so they can
/// never diverge. Inactive → invisible and hit-test-transparent (enforced via IsHitTestVisible).</summary>
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
    // press/session: _dragIndex back to -1, _resizing false, _ghost null.

    private int _dragIndex = -1;
    private bool _resizing;
    private Point _pressOffset;      // pointer offset inside the tile at press (drag)
    private GridRect? _ghost;        // candidate cell rect under the pointer

    /// <summary>Pixel rect for tile <paramref name="i"/>, derived from Core's PageGeometry.
    /// Shared by <see cref="Render"/> and <see cref="HitTest"/> so chrome and hit-zones can never diverge.</summary>
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
        var origin = TileRect(_dragIndex).TopLeft;
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
            var px = PageGeometry.ToPixelRect(_ghost, Bounds.Width, Page.GridColumns, CellHeight, CellGap);
            var ghostRect = new Rect(px.X, px.Y, px.Width, px.Height);
            var legal = PageLayoutEngine.IsValidPlacement(Page, _ghost,
                ignoreInstanceId: Page.Widgets[_dragIndex].InstanceId);
            var (fill, pen) = legal ? (GhostCleanBrush, GhostCleanPen) : (GhostPushBrush, GhostPushPen);
            ctx.DrawRectangle(fill, pen, ghostRect, 8, 8);
        }
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
            _dragIndex = hit.Value.Index;
            _resizing = hit.Value.Zone == 2;
            var r = TileRect(_dragIndex);
            _pressOffset = e.GetPosition(this) - r.TopLeft;
            e.Pointer.Capture(this);   // capture ONLY on a valid zone (ColorWheel idiom)
            e.Handled = true;
        }
    }

    /// <summary>Mid-gesture: recomputes the candidate ghost cell rect under the pointer and redraws.
    /// No-op unless this control holds pointer capture from a body/grip press.</summary>
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

    /// <summary>Ends the gesture: always uncaptures, then — if a candidate ghost was set — raises
    /// <see cref="MoveCommitted"/> once with the final rect. The layout itself is not touched here;
    /// the caller (DashboardView → DashboardViewModel.EditMove) owns the single engine mutation.
    /// Gesture state is always cleared, so nothing survives to the next press.</summary>
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
}
