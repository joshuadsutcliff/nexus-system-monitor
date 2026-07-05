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

    // MoveCommitted (Action&lt;Guid, GridRect&gt;?) is NOT declared here: this task never raises it
    // (body/grip zones are hit-tested but not yet acted on), and a field-like event that's never
    // invoked trips CS0067 under this repo's TreatWarningsAsErrors. Task 6 adds the declaration
    // alongside the drag/resize gesture that actually raises it.

    private const double RemoveBox = 20;
    private const double Grip = 16;

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

    /// <summary>Draws per-tile chrome: accent outline, remove box with a centered ✕, and a diagonal-hatch resize grip.
    /// No-op when inactive or the page is unset.</summary>
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

    /// <summary>Hit-tests the press point: a remove-box hit raises <see cref="RemoveRequested"/> and marks the
    /// event handled; body/grip hits are recorded by <see cref="HitTest"/> but not yet acted on (gesture task).
    /// Any miss (inactive, no page, or outside all zones) leaves the event unhandled so scroll/pointer input
    /// continues to reach controls beneath the adorner.</summary>
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
