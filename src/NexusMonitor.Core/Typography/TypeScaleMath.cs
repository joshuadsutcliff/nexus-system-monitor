namespace NexusMonitor.Core.Typography;

/// <summary>
/// The four dynamic-type scale steps a widget tile's headline/value text can occupy, keyed to
/// the tile's pixel area (Phase 8 UI polish, Task 4). Ordinal order matters — <see
/// cref="TypeScaleMath.StepFor"/> walks this enum's declaration order one step at a time.
/// </summary>
public enum TypeScaleStep
{
    /// <summary>×0.85 — small tiles (e.g. a user-shrunk widget).</summary>
    Small,

    /// <summary>×1.0 — the baseline scale. Centered on the factory-default half-width card
    /// (CPU/Memory/Disk/GPU, 6×2 grid cells) — the most common tile in the factory layout, and the
    /// size the existing NxFont* tokens were already tuned against.</summary>
    Medium,

    /// <summary>×1.15 — large tiles (e.g. the factory Health Score and Predictions cards).</summary>
    Large,

    /// <summary>×1.3 — extra-large tiles (e.g. the factory Bottleneck, Top Consumers,
    /// Recommendations, and Health Trends cards).</summary>
    ExtraLarge
}

/// <summary>
/// Pure math for dynamic type scaling in widget tiles: maps a tile's pixel area (width × height,
/// as arranged by <see cref="NexusMonitor.Core.Pages.PageGeometry"/>) to one of four <see
/// cref="TypeScaleStep"/> steps, with hysteresis so a resize hovering near a threshold doesn't
/// visibly jitter between steps. No Avalonia dependency — <c>NexusMonitor.UI.Controls.DynamicTypeScale</c>
/// (the attached-property behavior that observes a tile's live bounds and drives the headline/value
/// TextBlocks) is the only consumer. Composes multiplicatively on top of
/// <c>AppSettings.FontSizeMultiplier</c>: the caller multiplies <see cref="ScaleFor"/>'s result by
/// whatever font-size the multiplier has already produced — this class knows nothing about
/// <c>FontSizeMultiplier</c> and never needs to.
///
/// <para>
/// <b>Threshold derivation.</b> Thresholds are derived from <c>dashboard.default.json</c>'s
/// factory tile geometry, run through <c>PageGeometry</c>'s cell math against the app's default
/// window: 1280×800 window, 210px sidebar (<c>MainWindow.axaml</c>'s <c>Grid ColumnDefinitions="210,*"</c>),
/// 20px+20px <c>ScrollViewer</c> side padding (<c>DashboardView.axaml</c>), 12-column grid, 72px
/// cell height, 12px gap (<c>PageMetrics.DefaultCellHeight/DefaultCellGap</c>) — giving an available
/// content width of 1280 − 210 − 20 − 20 = 1030px and a cell width of (1030 − 12×11) / 12 ≈ 74.83px.
/// The five distinct factory tile shapes (<c>dashboard.default.json</c>) land at:
/// </para>
/// <list type="table">
///   <item><description>6×2  (CPU/Memory/Disk/GPU cards)                  → 509 × 156  = 79,404 px² (Medium anchor)</description></item>
///   <item><description>12×2 (Health Score)                                → 1030 × 156 = 160,680 px² (Large band)</description></item>
///   <item><description>12×3 (Predictions)                                 → 1030 × 240 = 247,200 px² (Large band)</description></item>
///   <item><description>12×4 (Bottleneck / Top Consumers / Recommendations)→ 1030 × 324 = 333,720 px² (ExtraLarge band)</description></item>
///   <item><description>12×5 (Health Trends)                              → 1030 × 408 = 420,240 px² (ExtraLarge band)</description></item>
/// </list>
/// <para>
/// giving the three base boundaries <see cref="SmallMediumThreshold"/> (45,000 — below the
/// smallest factory tile, leaving room for compact user-shrunk tiles down toward the 1×1 minimum
/// tile size, ~5,388 px²), <see cref="MediumLargeThreshold"/> (120,000 — the rounded midpoint
/// between the 6×2 and 12×2 anchors, 120,042 exactly), and <see cref="LargeExtraLargeThreshold"/>
/// (290,000 — the rounded midpoint between the 12×3 and 12×4 anchors, 290,460 exactly). Each
/// boundary carries a <see cref="HysteresisFraction"/> (±5%, 10% total band width) so a resize
/// hovering right at a boundary doesn't oscillate between two steps every frame.
/// </para>
/// </summary>
public static class TypeScaleMath
{
    /// <summary>Area (px²) below which a tile is <see cref="TypeScaleStep.Small"/>, absent hysteresis.</summary>
    public const double SmallMediumThreshold = 45_000;

    /// <summary>Area (px²) at/above which a tile is <see cref="TypeScaleStep.Large"/>, absent hysteresis.</summary>
    public const double MediumLargeThreshold = 120_000;

    /// <summary>Area (px²) at/above which a tile is <see cref="TypeScaleStep.ExtraLarge"/>, absent hysteresis.</summary>
    public const double LargeExtraLargeThreshold = 290_000;

    /// <summary>Fraction of each threshold added (entering a higher step) or subtracted (falling
    /// back to a lower step) to form the hysteresis band — 5% each direction, 10% total band width.</summary>
    public const double HysteresisFraction = 0.05;

    /// <summary>The scale multiplier for a given step. Multiplies directly against whatever
    /// font-size <c>FontSizeMultiplier</c> has already produced — see the class doc.</summary>
    public static double ScaleFor(TypeScaleStep step) => step switch
    {
        TypeScaleStep.Small => 0.85,
        TypeScaleStep.Medium => 1.0,
        TypeScaleStep.Large => 1.15,
        TypeScaleStep.ExtraLarge => 1.3,
        _ => 1.0
    };

    /// <summary>
    /// Computes the step for a tile of the given pixel <paramref name="area"/>. When <paramref
    /// name="currentStep"/> is supplied, hysteresis is applied against it: moving to a higher step
    /// requires exceeding that step's boundary by <see cref="HysteresisFraction"/>, and falling
    /// back to a lower step requires dropping below that boundary by the same fraction — so a
    /// resize that settles exactly on a raw threshold does not flip back and forth. A single call
    /// walks as many steps as the area actually justifies (e.g. Small straight to ExtraLarge on a
    /// large one-shot resize), not just one step at a time.
    /// Omitting <paramref name="currentStep"/> (the default) classifies <paramref name="area"/>
    /// against the plain, non-hysteresis thresholds — appropriate for a tile's first computation
    /// (e.g. initial layout, before any prior step exists to hysteresis against).
    /// </summary>
    public static TypeScaleStep StepFor(double area, TypeScaleStep? currentStep = null)
    {
        if (currentStep is null) return PlainStepFor(area);

        var step = currentStep.Value;

        while (step < TypeScaleStep.ExtraLarge && area >= UpperBoundary(step) * (1 + HysteresisFraction))
            step++;

        while (step > TypeScaleStep.Small && area < LowerBoundary(step) * (1 - HysteresisFraction))
            step--;

        return step;
    }

    /// <summary>The boundary above <paramref name="step"/> (the area a tile must reach to advance
    /// to the next step up), or <see cref="double.PositiveInfinity"/> for <see
    /// cref="TypeScaleStep.ExtraLarge"/> (nothing above it).</summary>
    private static double UpperBoundary(TypeScaleStep step) => step switch
    {
        TypeScaleStep.Small => SmallMediumThreshold,
        TypeScaleStep.Medium => MediumLargeThreshold,
        TypeScaleStep.Large => LargeExtraLargeThreshold,
        _ => double.PositiveInfinity
    };

    /// <summary>The boundary below <paramref name="step"/> (the area a tile must drop under to
    /// fall back to the next step down), or <see cref="double.NegativeInfinity"/> for <see
    /// cref="TypeScaleStep.Small"/> (nothing below it).</summary>
    private static double LowerBoundary(TypeScaleStep step) => step switch
    {
        TypeScaleStep.Medium => SmallMediumThreshold,
        TypeScaleStep.Large => MediumLargeThreshold,
        TypeScaleStep.ExtraLarge => LargeExtraLargeThreshold,
        _ => double.NegativeInfinity
    };

    /// <summary>Classifies <paramref name="area"/> against the plain (non-hysteresis) thresholds.</summary>
    private static TypeScaleStep PlainStepFor(double area) =>
        area < SmallMediumThreshold ? TypeScaleStep.Small :
        area < MediumLargeThreshold ? TypeScaleStep.Medium :
        area < LargeExtraLargeThreshold ? TypeScaleStep.Large :
        TypeScaleStep.ExtraLarge;
}
