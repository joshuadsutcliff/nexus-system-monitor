using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Typography;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// Attached behavior: dynamic type scaling for widget-tile headline/value text (Phase 8 UI
/// polish, Task 4). Two attached properties:
/// <list type="bullet">
///   <item><description><see cref="IsHostProperty"/> — set <c>True</c> on a widget tile's root
///   chrome <c>Border</c> (the one carrying <c>Classes="nx-widget-chrome"</c>, which is arranged
///   at the tile's exact grid-cell pixel rect by <c>PageHostControl</c>). Observes that Border's
///   own <c>Bounds</c> on every layout pass, computes the tile's pixel area (width × height), and
///   — via the pure step math in <see cref="TypeScaleMath"/>, with hysteresis against the tile's
///   own previous step — recomputes its <see cref="TypeScaleStep"/> and pushes it directly onto
///   every descendant <c>TextBlock</c> that opted in via <see cref="FontSizeKeyProperty"/> (found
///   once per Bounds change via a visual-tree walk — deliberately NOT an inherited
///   AvaloniaProperty, so the update path is explicit and doesn't depend on inheritance-cascade
///   behavior through the logical tree).</description></item>
///   <item><description><see cref="FontSizeKeyProperty"/> — set on a headline/value
///   <c>TextBlock</c> INSTEAD OF a direct <c>FontSize="{DynamicResource NxFontNN}"</c> binding,
///   naming the resource key that binding used to reference. Composes multiplicatively on top of
///   <c>AppSettings.FontSizeMultiplier</c>: that resource key's value already reflects
///   <c>FontSizeMultiplier</c> (<c>SettingsViewModel.ApplyFont</c> rewrites the shared
///   <c>NxFont*</c>/<c>FontSize*</c> resource-dictionary entries in place — see its own doc
///   comment), so this behavior only ever multiplies the CURRENT resource value by the step's
///   scale factor; it never re-derives <c>FontSizeMultiplier</c> itself. The effective FontSize is
///   recomputed whenever: (1) the named resource's own value changes (theme swap or
///   <c>FontSizeMultiplier</c> edit — observed via <see cref="Avalonia.Controls.ResourceNodeExtensions.GetResourceObservable"/>,
///   the same mechanism Avalonia's own <c>{DynamicResource}</c> markup extension uses internally),
///   (2) the host tile pushes a new step (resize crossing a hysteresis boundary), or (3)
///   <c>AppSettings.ScaleTextWithWidgetSize</c> toggles — forwarded via <see
///   cref="NotifySettingsChanged"/> (see its own doc comment for why this needs its own small
///   notification hook rather than reusing <c>MotionSettingsService.MotionChanged</c>). When that
///   setting is off, FontSize is just the plain resource value (scale factor 1.0) — identical to
///   pre-Task-4 fixed-size behavior.</description></item>
/// </list>
/// </summary>
public static class DynamicTypeScale
{
    /// <summary>Set <c>True</c> on a widget tile's root chrome <c>Border</c> to make it the
    /// area-measurement source for every descendant opted into <see cref="FontSizeKeyProperty"/>.</summary>
    public static readonly AttachedProperty<bool> IsHostProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsHost", typeof(DynamicTypeScale));

    /// <summary>Set on a headline/value <c>TextBlock</c> to the resource key (e.g.
    /// <c>"NxFont20"</c>) that used to be bound directly via <c>FontSize="{DynamicResource ...}"</c>.</summary>
    public static readonly AttachedProperty<string?> FontSizeKeyProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("FontSizeKey", typeof(DynamicTypeScale));

    /// <summary>Raised whenever <c>AppSettings.ScaleTextWithWidgetSize</c> changes, so every live
    /// <see cref="FontSizeKeyProperty"/> target re-evaluates its gating immediately. This class has
    /// no DI access and widgets bind only to their card view-models (not <c>SettingsViewModel</c>
    /// or the settings service), so — mirroring how <c>MotionSettingsService.MotionChanged</c>
    /// already lets <c>DashboardViewModel</c>/<c>MainWindow</c> re-gate live — <see
    /// cref="NexusMonitor.UI.ViewModels.SettingsViewModel"/>'s own
    /// <c>OnScaleTextWithWidgetSizeChanged</c> partial method calls <see
    /// cref="NotifySettingsChanged"/> after persisting the toggle. It is intentionally NOT wired
    /// through <c>MotionSettingsService.MotionChanged</c>: that event's own <c>Apply()</c> writes
    /// only Motion-duration and Elevation-shadow resources, never touches
    /// <c>ScaleTextWithWidgetSize</c>, and conflating an unrelated Typography-domain setting onto
    /// the Motion event would be a correctness footgun for future readers of either class — a
    /// small dedicated event keeps each concern self-contained.</summary>
    public static event Action? SettingsChanged;

    /// <summary>Call after persisting a change to <c>AppSettings.ScaleTextWithWidgetSize</c> —
    /// see <see cref="SettingsChanged"/>.</summary>
    public static void NotifySettingsChanged() => SettingsChanged?.Invoke();

    public static void SetIsHost(Control control, bool value) => control.SetValue(IsHostProperty, value);
    public static bool GetIsHost(Control control) => control.GetValue(IsHostProperty);

    public static void SetFontSizeKey(TextBlock control, string? value) => control.SetValue(FontSizeKeyProperty, value);
    public static string? GetFontSizeKey(TextBlock control) => control.GetValue(FontSizeKeyProperty);

    /// <summary>Per-host state: tracked outside the control itself via a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> so a host that's later removed from the
    /// tree (e.g. an unknown-widget placeholder swap) never leaks a subscription.</summary>
    private sealed class HostState : IDisposable
    {
        public TypeScaleStep CurrentStep = TypeScaleStep.Medium;
        public IDisposable? BoundsSubscription;
        public void Dispose() => BoundsSubscription?.Dispose();
    }

    /// <summary>Per-target (headline/value TextBlock) state: the last resource-observed base font
    /// size and the last step pushed by its host, so either input changing alone triggers a
    /// correct recompute without needing the other.</summary>
    private sealed class TargetState : IDisposable
    {
        public double BaseFontSize = 15.0;
        public TypeScaleStep Step = TypeScaleStep.Medium;
        public IDisposable? ResourceSubscription;
        public Action? SettingsHandler;
        public void Dispose()
        {
            ResourceSubscription?.Dispose();
            if (SettingsHandler is not null) SettingsChanged -= SettingsHandler;
        }
    }

    private static readonly ConditionalWeakTable<Control, HostState> HostStates = new();
    private static readonly ConditionalWeakTable<TextBlock, TargetState> TargetStates = new();

    static DynamicTypeScale()
    {
        IsHostProperty.Changed.AddClassHandler<Control>((host, e) => OnIsHostChanged(host, (bool)(e.NewValue ?? false)));
        FontSizeKeyProperty.Changed.AddClassHandler<TextBlock>((tb, e) => OnFontSizeKeyChanged(tb, (string?)e.NewValue));
    }

    private static void OnIsHostChanged(Control host, bool isHost)
    {
        if (HostStates.TryGetValue(host, out var old))
        {
            old.Dispose();
            HostStates.Remove(host);
        }

        if (!isHost) return;

        var state = new HostState();
        HostStates.Add(host, state);

        state.BoundsSubscription = host.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
        {
            var area = bounds.Width * bounds.Height;
            if (area <= 0) return;
            var newStep = TypeScaleMath.StepFor(area, state.CurrentStep);
            if (newStep == state.CurrentStep) return;
            state.CurrentStep = newStep;
            PushStepToDescendants(host, newStep);
        });
    }

    private static void PushStepToDescendants(Control host, TypeScaleStep step)
    {
        foreach (var tb in host.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!TargetStates.TryGetValue(tb, out var target)) continue;
            target.Step = step;
            Recompute(tb, target);
        }
    }

    private static void OnFontSizeKeyChanged(TextBlock textBlock, string? key)
    {
        if (TargetStates.TryGetValue(textBlock, out var old))
        {
            old.Dispose();
            TargetStates.Remove(textBlock);
        }

        if (string.IsNullOrEmpty(key)) return;

        var state = new TargetState();
        TargetStates.Add(textBlock, state);

        state.ResourceSubscription = textBlock.GetResourceObservable(key).Subscribe(value =>
        {
            // GetResourceObservable can push Avalonia.AvaloniaProperty.UnsetValue (or null) when
            // the key isn't resolvable yet (e.g. the very first synchronous push, before the
            // TextBlock is attached to a visual tree that can walk up to Application.Current) —
            // neither is an IConvertible, so Convert.ToDouble would throw. Only a genuine double
            // updates BaseFontSize; anything else is a no-op push, not an error.
            if (value is double d)
            {
                state.BaseFontSize = d;
                Recompute(textBlock, state);
            }
        });

        state.SettingsHandler = () => Recompute(textBlock, state);
        SettingsChanged += state.SettingsHandler;
    }

    private static void Recompute(TextBlock textBlock, TargetState state)
    {
        var scaleEnabled = App.Services?.GetService<SettingsService>()?.Current.ScaleTextWithWidgetSize ?? true;
        var scale = scaleEnabled ? TypeScaleMath.ScaleFor(state.Step) : 1.0;
        textBlock.FontSize = state.BaseFontSize * scale;
    }
}
