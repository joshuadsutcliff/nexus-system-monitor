using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Backdrop;

namespace NexusMonitor.UI.Services;

/// <summary>
/// Phase 8 Task 7 (acrylic groundwork): applies <c>AppSettings.BackdropBlurMode</c> to the main
/// window's <see cref="Window.TransparencyLevelHint"/> using the per-OS/per-mode preference chain
/// defined in <see cref="BackdropMath"/> (each of "Blur"/"Acrylic"/"Mica" requests its own ordered
/// chain, gated per OS, with Linux always <c>[None]</c> — see that class's doc for the full matrix),
/// then observes
/// <see cref="TopLevel.ActualTransparencyLevel"/> to detect when the platform rejected the request
/// (granted <see cref="BackdropLevel.None"/> instead of the preferred level) and raises
/// <see cref="RejectionChanged"/> so <c>SettingsViewModel</c> can widen the Crystal Glass alpha
/// floors (<c>ApplyGlass</c>'s <c>backdropRejected</c> parameter) — otherwise a translucent
/// <c>GlassBgBrush</c>/<c>BgBaseBrush</c> would blend against Avalonia's
/// <see cref="TopLevel.TransparencyBackgroundFallback"/> (a solid WHITE brush by default, per the
/// Avalonia XML docs — Nexus never overrides it) instead of real desktop blur, washing out the Dark
/// theme's near-black palette.
///
/// Scope: only the MAIN window's chain is OS-aware. The overlay widget window's
/// <c>TransparencyLevelHint</c> (which always needs <see cref="BackdropLevel.Transparent"/> — never
/// <see cref="BackdropLevel.None"/> — as its last-resort fallback so its <c>CornerRadius</c> clips
/// correctly) is a pre-existing, unrelated mechanism and is left untouched in
/// <c>SettingsViewModel.ApplyBackdropMode</c>.
///
/// Mirrors the <c>MotionSettingsService</c> pattern: a thin Avalonia-facing sealed class that calls
/// straight through to Core's pure, unit-tested <see cref="BackdropMath"/> — no chain-selection or
/// rejection-detection logic is duplicated here.
///
/// <para><b>Defensive re-Apply on <see cref="TopLevel.Opened"/>/<see cref="WindowBase.Activated"/>
/// (Task 7 gate-review fix pass):</b> <c>MainWindow</c>'s constructor calls <see cref="Apply"/>
/// before the window has a native platform handle, so the very first
/// <c>TransparencyLevelHint</c> assignment can be negotiated against a handle that doesn't exist
/// yet. Separately, on macOS the blur materials (<see cref="BackdropLevel.Blur"/>/
/// <see cref="BackdropLevel.AcrylicBlur"/>) are implemented via an <c>NSVisualEffectView</c>, which
/// per Apple/Avalonia's own behavior may only actually start compositing once the window has become
/// key (i.e. <see cref="WindowBase.Activated"/> fires) — not merely once it's been shown
/// (<see cref="TopLevel.Opened"/>). This service re-asserts the last-requested chain on both events
/// so a construction-time request that silently resolved to <see cref="BackdropLevel.None"/> gets a
/// second (and, on first activation, a third) chance to be honored once the native handle exists and
/// the window is key — without the user having to touch Settings. See <see cref="DefensiveReapply"/>
/// for the redundant-churn guard and detach timing.</para>
/// </summary>
public sealed class BackdropService
{
    private readonly IAccessibilitySignals _accessibilitySignals;

    public BackdropService(IAccessibilitySignals accessibilitySignals)
    {
        _accessibilitySignals = accessibilitySignals;
    }

    /// <summary>
    /// Raised whenever the observed window's <see cref="TopLevel.ActualTransparencyLevel"/> changes
    /// in a way that flips the rejection state computed by <see cref="BackdropMath.IsRejected"/>.
    /// Argument is the new rejection state. Always raised on the UI thread (the property-changed
    /// notification Avalonia raises this from is already UI-thread in practice, but callers that
    /// touch <c>Application.Current.Resources</c> from this event should not assume that — see
    /// <c>SettingsViewModel.OnBackdropRejectionChanged</c>, which posts via
    /// <see cref="Dispatcher.UIThread"/> defensively).
    /// </summary>
    public event Action<bool>? RejectionChanged;

    /// <summary>The most recently computed rejection state (see <see cref="RejectionChanged"/>).
    /// <see langword="false"/> until the first <see cref="Apply"/> call.</summary>
    public bool IsRejected { get; private set; }

    private Window? _observedWindow;
    private IReadOnlyList<BackdropLevel> _lastRequestedChain = Array.Empty<BackdropLevel>();

    /// <summary>Whether the first-activation defensive re-Apply has already run for the currently
    /// observed window (see <see cref="OnWindowActivated"/>). Reset whenever <see cref="Apply"/>
    /// starts observing a different window.</summary>
    private bool _firstActivationReapplyDone;

    /// <summary>
    /// Detects the running OS family as a <see cref="BackdropPlatform"/>. A thin wrapper over
    /// <see cref="OperatingSystem"/>'s per-platform checks so <see cref="BackdropMath.GetHintChain"/>
    /// itself never depends on the actual runtime OS — see that class's doc for why the platform is
    /// a parameter rather than baked-in checks (keeps it testable for all three OSes from one CI run).
    /// Falls back to <see cref="BackdropPlatform.Linux"/> (the most conservative, None-only chain)
    /// for any OS Nexus doesn't explicitly ship on.
    /// </summary>
    public static BackdropPlatform DetectPlatform()
    {
        if (OperatingSystem.IsMacOS())   return BackdropPlatform.MacOS;
        if (OperatingSystem.IsWindows()) return BackdropPlatform.Windows;
        return BackdropPlatform.Linux;
    }

    /// <summary>
    /// Computes this OS's preference chain for <paramref name="glassEnabled"/>/<paramref name="mode"/>
    /// and sets it as <paramref name="window"/>'s <see cref="Window.TransparencyLevelHint"/>. Begins
    /// (or continues) observing <see cref="TopLevel.ActualTransparencyLevel"/> on
    /// <paramref name="window"/> so <see cref="RejectionChanged"/> fires once the platform has
    /// resolved the request — which may happen synchronously (the common case for an already-shown
    /// window reacting to a live settings change) or on a later property-changed notification (the
    /// window's native handle may not exist yet on the very first call made from a constructor,
    /// before the window is shown).
    /// </summary>
    public void Apply(Window window, bool glassEnabled, string mode)
    {
        // OS "Reduce Transparency" runtime clamp (task brief, 2026-07-11): forces the chain to
        // [None] while the signal is active, WITHOUT touching AppSettings.BackdropBlurMode —
        // see IAccessibilitySignals' class doc for the non-mutation contract.
        var chain = BackdropMath.GetHintChain(DetectPlatform(), glassEnabled, mode, _accessibilitySignals.ReduceTransparency);
        _lastRequestedChain = chain;
        window.TransparencyLevelHint = chain.Select(ToAvalonia).ToArray();

        if (!ReferenceEquals(_observedWindow, window))
        {
            if (_observedWindow is not null)
            {
                _observedWindow.PropertyChanged -= OnWindowPropertyChanged;
                _observedWindow.Opened -= OnWindowOpened;
                _observedWindow.Activated -= OnWindowActivated;
            }
            _observedWindow = window;
            _observedWindow.PropertyChanged += OnWindowPropertyChanged;
            _observedWindow.Opened += OnWindowOpened;
            _observedWindow.Activated += OnWindowActivated;
            _firstActivationReapplyDone = false;
        }

        EvaluateRejection(window);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TopLevel.ActualTransparencyLevelProperty) return;
        if (sender is Window w) EvaluateRejection(w);
    }

    /// <summary>
    /// Defensive re-Apply, hook 1 of 2: fires once the window's native platform handle exists
    /// (<see cref="TopLevel.Opened"/>). Covers <c>MainWindow</c>'s constructor calling
    /// <see cref="Apply"/> before that handle exists, per this class's doc.
    /// </summary>
    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (sender is Window w) DefensiveReapply(w);
    }

    /// <summary>
    /// Defensive re-Apply, hook 2 of 2: fires when the window becomes key/foreground
    /// (<see cref="WindowBase.Activated"/>). Covers macOS's <c>NSVisualEffectView</c>-backed blur
    /// materials, which per this class's doc may only start compositing once the window is key.
    /// Detaches itself after the first activation (whether or not that activation actually needed a
    /// re-Apply) — see this class's doc for why later activations shouldn't keep re-negotiating the
    /// backdrop.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;

        DefensiveReapply(w);

        if (!_firstActivationReapplyDone)
        {
            _firstActivationReapplyDone = true;
            w.Activated -= OnWindowActivated;
        }
    }

    /// <summary>
    /// Re-asserts <see cref="_lastRequestedChain"/> as <paramref name="window"/>'s
    /// <see cref="Window.TransparencyLevelHint"/> — the SAME chain already computed by the most
    /// recent <see cref="Apply"/> call, not a new decision (no <see cref="BackdropMath.GetHintChain"/>
    /// re-evaluation). Guards against redundant native churn: if <see cref="TopLevel.ActualTransparencyLevel"/>
    /// already matches the chain's top (most-preferred) entry, the request already succeeded and
    /// there is nothing to correct, so this is a no-op beyond the (idempotent) rejection check.
    /// </summary>
    private void DefensiveReapply(Window window)
    {
        var actual = ToCore(window.ActualTransparencyLevel);
        if (_lastRequestedChain.Count > 0 && actual == _lastRequestedChain[0])
        {
            EvaluateRejection(window);
            return;
        }

        window.TransparencyLevelHint = _lastRequestedChain.Select(ToAvalonia).ToArray();
        EvaluateRejection(window);
    }

    private void EvaluateRejection(Window window)
    {
        var actual = ToCore(window.ActualTransparencyLevel);
        var rejected = BackdropMath.IsRejected(_lastRequestedChain, actual);
        if (rejected == IsRejected) return;

        IsRejected = rejected;
        RejectionChanged?.Invoke(rejected);
    }

    private static WindowTransparencyLevel ToAvalonia(BackdropLevel level) => level switch
    {
        BackdropLevel.None        => WindowTransparencyLevel.None,
        BackdropLevel.Transparent => WindowTransparencyLevel.Transparent,
        BackdropLevel.Blur        => WindowTransparencyLevel.Blur,
        BackdropLevel.AcrylicBlur => WindowTransparencyLevel.AcrylicBlur,
        BackdropLevel.Mica        => WindowTransparencyLevel.Mica,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown BackdropLevel."),
    };

    /// <summary>
    /// Avalonia 11.2.3's <see cref="WindowTransparencyLevel"/> is a <see langword="readonly"/>
    /// struct exposing its five levels as static properties (confirmed via reflection:
    /// <c>typeof(WindowTransparencyLevel).IsEnum == false</c>) — NOT a plain C# <see langword="enum"/>
    /// like earlier Avalonia versions. That means it can't be used as a <see langword="switch"/>
    /// constant pattern (the compiler rejects <c>case WindowTransparencyLevel.None:</c> with
    /// CS9135), so this mapping uses <c>==</c> comparisons instead — the struct does implement
    /// value equality (<c>op_Equality</c>, also confirmed via reflection).
    /// </summary>
    private static BackdropLevel ToCore(WindowTransparencyLevel level)
    {
        if (level == WindowTransparencyLevel.None)        return BackdropLevel.None;
        if (level == WindowTransparencyLevel.Transparent) return BackdropLevel.Transparent;
        if (level == WindowTransparencyLevel.Blur)        return BackdropLevel.Blur;
        if (level == WindowTransparencyLevel.AcrylicBlur) return BackdropLevel.AcrylicBlur;
        if (level == WindowTransparencyLevel.Mica)        return BackdropLevel.Mica;
        return BackdropLevel.None;
    }
}
