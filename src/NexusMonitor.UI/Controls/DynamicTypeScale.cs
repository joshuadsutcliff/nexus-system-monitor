using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Typography;
using Serilog;

namespace NexusMonitor.UI.Controls;

/// <summary>
/// Attached behavior: dynamic type scaling for widget-tile headline/value text (Phase 8 UI
/// polish, Task 4; lifecycle-hardened in the Task 4 gate fix bundle — see
/// <c>task-4-report.md</c> "Gate fix bundle"). Two attached properties:
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
///   behavior through the logical tree). A host's very FIRST bounds push classifies via the plain
///   (non-hysteresis) <see cref="TypeScaleMath.StepFor(double, TypeScaleStep?)"/> overload rather
///   than hysteresis-walking from an assumed starting step — see <see cref="HostState.HasClassified"/>
///   and the gate-fix-bundle note below (Finding 2).</description></item>
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
///
/// <para>
/// <b>Gate fix bundle — Finding 1 (unbounded leak).</b> <see cref="TargetState"/> subscribes to
/// the STATIC <see cref="SettingsChanged"/> event. <c>PageHostControl.RebuildChildren</c> discards
/// and reconstructs every widget on every <c>EnginePage</c> reassignment (edit-mode moves, profile
/// switches, dashboard navigation) via <c>Children.Clear()</c>, which Avalonia propagates as
/// <c>DetachedFromVisualTree</c> down the ENTIRE subtree — including these nested TextBlocks — so
/// that event is the correct, always-fired hook to dispose a target's state and unsubscribe from
/// the static event; relying on the attached property's own Changed handler (the old behavior)
/// only fires on a VALUE change, never on a plain rebuild, so it never ran in practice and every
/// rebuilt TextBlock stayed permanently rooted in the static invocation list. <see
/// cref="EnsureLifecycleWired"/> wires <c>DetachedFromVisualTree</c>/<c>AttachedToVisualTree</c>
/// exactly once per TextBlock instance: detach disposes; attach recreates the state (reading the
/// attached property's current value) if it isn't already live — covering the (currently
/// speculative, not exercised by this app's own rebuild path) case of Avalonia reattaching the
/// SAME control instance rather than constructing a fresh one. <see cref="HostState"/>'s own
/// <c>BoundsSubscription</c> was checked for the same class of leak and found NOT to need this
/// treatment — see its doc comment — so it is intentionally left on its original, purely
/// property-Changed-driven lifecycle.
/// </para>
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
    /// small dedicated event keeps each concern self-contained. Every subscriber MUST unsubscribe
    /// on teardown (see <see cref="TargetState.Dispose"/> and <see cref="EnsureLifecycleWired"/>) —
    /// this is a permanent static field, so a stranded handler is a permanent leak.</summary>
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

        /// <summary>False until this host's first <c>Bounds</c> push has been classified.
        /// Gate-fix-bundle Finding 2: the very first classification for a freshly-created host must
        /// use the plain (non-hysteresis) thresholds — <c>TypeScaleMath.StepFor(area)</c>, i.e.
        /// <c>currentStep: null</c> — because a fresh host has no real prior step to hysteresis
        /// against. The old code always hysteresis-walked from <see cref="CurrentStep"/>'s field
        /// default (<c>Medium</c>), which stuck any tile whose first-ever measured area fell inside
        /// the hysteresis band around a boundary (e.g. ~44,000px², just under the 45,000 S/M line)
        /// at the WRONG step until a later resize pushed it past the retreat threshold. Only
        /// SUBSEQUENT pushes (once a genuine prior step exists) hysteresis-walk from
        /// <see cref="CurrentStep"/>.</summary>
        public bool HasClassified;

        public IDisposable? BoundsSubscription;

        /// <summary>Disposes the Bounds subscription. NOTE: unlike <see cref="TargetState"/>, this
        /// subscription is an INSTANCE-scoped Avalonia property observable
        /// (<c>host.GetObservable(Visual.BoundsProperty)</c>) stored on the host control's own
        /// property-changed infrastructure, not a static/long-lived event. When the host is
        /// discarded (e.g. by <c>PageHostControl.RebuildChildren</c>) and nothing else references
        /// it, the self-contained host+subscription reference cycle is ordinary GC-collectible —
        /// verified non-leaking during the gate fix bundle review — so it deliberately does NOT get
        /// the same explicit <c>DetachedFromVisualTree</c>-triggered disposal as
        /// <see cref="TargetState"/>; <c>OnIsHostChanged</c>'s existing property-Changed-driven
        /// disposal is sufficient and is left unrestructured.</summary>
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
            if (SettingsHandler is not null)
            {
                SettingsChanged -= SettingsHandler;
                SettingsHandler = null;
                LogSubscriberDelta(-1);
            }
        }
    }

    private static readonly ConditionalWeakTable<Control, HostState> HostStates = new();
    private static readonly ConditionalWeakTable<TextBlock, TargetState> TargetStates = new();

    /// <summary>Marks a TextBlock whose Attached/DetachedFromVisualTree lifecycle hooks are
    /// already wired (see <see cref="EnsureLifecycleWired"/>), so re-firing
    /// <see cref="FontSizeKeyProperty"/>'s Changed handler on the same instance (e.g. the key is
    /// reassigned at runtime, not just set once from XAML) never double-subscribes those instance
    /// events.</summary>
    private static readonly ConditionalWeakTable<TextBlock, object> TargetLifecycleWired = new();

    private static readonly object LifecycleWiredMarker = new();

    /// <summary>Cached <see cref="SettingsService"/> singleton (minor fix: previously re-resolved
    /// via DI on every single <see cref="Recompute"/> call). See <see cref="ResolveSettingsService"/>.</summary>
    private static SettingsService? _settingsService;

    /// <summary>Debug-instrumentation running count of live <see cref="SettingsChanged"/>
    /// subscribers, used to empirically verify the Finding-1 leak fix (task-4-report.md "Gate fix
    /// bundle"): balanced +1/-1 pairs across repeated widget rebuilds prove no stranded
    /// subscriptions remain. Logged at Verbose — below the app's default Information floor (see
    /// <c>LoggingBootstrap</c>) — so it costs nothing in normal operation; bump the minimum level
    /// locally to re-observe it.</summary>
    private static int _settingsSubscriberCount;

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

        CreateHostState(host);
    }

    private static void CreateHostState(Control host)
    {
        var state = new HostState();
        HostStates.Add(host, state);

        state.BoundsSubscription = host.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
        {
            var area = bounds.Width * bounds.Height;
            if (area <= 0) return;

            // Finding 2: first-ever classification uses the plain (no-hysteresis) thresholds;
            // only later pushes hysteresis-walk from the real prior step. See HasClassified's doc.
            var newStep = state.HasClassified
                ? TypeScaleMath.StepFor(area, state.CurrentStep)
                : TypeScaleMath.StepFor(area);
            state.HasClassified = true;

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
        DisposeTargetState(textBlock);

        if (string.IsNullOrEmpty(key)) return;

        CreateTargetState(textBlock, key);
        EnsureLifecycleWired(textBlock);
    }

    /// <summary>Wires <c>DetachedFromVisualTree</c>/<c>AttachedToVisualTree</c> exactly once per
    /// TextBlock instance — see the class doc's Finding 1 section for the full rationale. Detach
    /// disposes the live <see cref="TargetState"/> (unsubscribing from the static <see
    /// cref="SettingsChanged"/> event); attach recreates it — reading <see cref="FontSizeKeyProperty"/>'s
    /// current value — only if it isn't already live, so a genuine reattach of the same instance
    /// resumes scaling instead of silently sticking at whatever FontSize it last had.</summary>
    private static void EnsureLifecycleWired(TextBlock textBlock)
    {
        if (TargetLifecycleWired.TryGetValue(textBlock, out _)) return;
        TargetLifecycleWired.Add(textBlock, LifecycleWiredMarker);

        textBlock.DetachedFromVisualTree += (_, _) => DisposeTargetState(textBlock);
        textBlock.AttachedToVisualTree += (_, _) =>
        {
            if (TargetStates.TryGetValue(textBlock, out _)) return;
            if (GetFontSizeKey(textBlock) is { Length: > 0 } currentKey)
                CreateTargetState(textBlock, currentKey);
        };
    }

    private static void DisposeTargetState(TextBlock textBlock)
    {
        if (!TargetStates.TryGetValue(textBlock, out var old)) return;
        old.Dispose();
        TargetStates.Remove(textBlock);
    }

    private static void CreateTargetState(TextBlock textBlock, string key)
    {
        var state = new TargetState();
        TargetStates.Add(textBlock, state);

        state.ResourceSubscription = textBlock.GetResourceObservable(key).Subscribe(value =>
        {
            // GetResourceObservable can push Avalonia.AvaloniaProperty.UnsetValue (or null) when
            // the key isn't resolvable yet (e.g. the very first synchronous push, before the
            // TextBlock is attached to a visual tree that can walk up to Application.Current) —
            // neither is an IConvertible, so Convert.ToDouble would throw. Only a genuine double
            // updates BaseFontSize; anything else is a benign no-op push, not an error.
            if (value is double d)
            {
                state.BaseFontSize = d;
                Recompute(textBlock, state);
            }
            else
            {
                Log.Debug(
                    "DynamicTypeScale: non-double resource push for {Key} ({ValueType}) — ignored",
                    key, value?.GetType().Name ?? "null");
            }
        });

        state.SettingsHandler = () => Recompute(textBlock, state);
        SettingsChanged += state.SettingsHandler;
        LogSubscriberDelta(1);
    }

    private static void LogSubscriberDelta(int delta)
    {
        _settingsSubscriberCount += delta;
        Log.Verbose(
            "DynamicTypeScale: SettingsChanged subscribers {Delta} -> {Count}",
            delta > 0 ? $"+{delta}" : delta.ToString(), _settingsSubscriberCount);
    }

    private static void Recompute(TextBlock textBlock, TargetState state)
    {
        var scaleEnabled = ResolveSettingsService()?.Current.ScaleTextWithWidgetSize ?? true;
        var scale = scaleEnabled ? TypeScaleMath.ScaleFor(state.Step) : 1.0;
        textBlock.FontSize = state.BaseFontSize * scale;
    }

    /// <summary>Resolves and caches the <see cref="SettingsService"/> singleton once. If
    /// <c>App.Services</c> isn't ready yet, the next call retries rather than permanently caching
    /// a null — but never re-resolves once a real instance is found.</summary>
    private static SettingsService? ResolveSettingsService() =>
        _settingsService ??= App.Services?.GetService<SettingsService>();
}
