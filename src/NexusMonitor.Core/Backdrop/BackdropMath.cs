namespace NexusMonitor.Core.Backdrop;

/// <summary>
/// Platform-neutral mirror of Avalonia's <c>Avalonia.Controls.WindowTransparencyLevel</c> enum
/// (verified against the Avalonia 11.2.3 package referenced by NexusMonitor.UI.csproj — same five
/// members, same order: None, Transparent, Blur, AcrylicBlur, Mica. No "Vibrancy" member exists in
/// this Avalonia version; see <see cref="BackdropMath"/>'s class doc for how the macOS chain names
/// the closest real equivalent). Lives here — rather than referencing
/// <c>Avalonia.Controls.WindowTransparencyLevel</c> directly from this pure logic — because
/// NexusMonitor.Core has no Avalonia package reference and this file needs to be unit-testable
/// from NexusMonitor.Core.Tests (which likewise has no Avalonia reference). This is the identical
/// "Core-adjacent logic in a UI assembly" carve-out <c>NexusMonitor.Core.Motion.MotionMath</c>
/// already documents for <c>NexusMonitor.UI.Services.MotionSettingsService</c> — NexusMonitor.UI
/// has no test project of its own, so testable pure logic that backs one of its services lives in
/// Core instead. <c>NexusMonitor.UI.Services.BackdropService</c> maps 1:1 between this enum and
/// Avalonia's own; no chain-selection logic is duplicated there.
/// </summary>
public enum BackdropLevel
{
    /// <summary>Opaque window; nothing drawn shows through. Avalonia's least-capable/default level.</summary>
    None,
    /// <summary>Plain alpha transparency, no blur. Used only by the overlay widget's own
    /// corner-radius-clipping fallback (a pre-existing mechanism this task does not touch).</summary>
    Transparent,
    /// <summary>Blur-behind. On macOS, Avalonia.Native implements this via an NSVisualEffectView —
    /// i.e. this IS the platform's "vibrancy" material at this Avalonia version.</summary>
    Blur,
    /// <summary>Blur-behind with a higher blur radius; may fall back to <see cref="Blur"/> on
    /// platforms/compositors that don't distinguish the two.</summary>
    AcrylicBlur,
    /// <summary>Wallpaper-tint blur. Windows 11 only per the Avalonia XML docs.</summary>
    Mica,
}

/// <summary>Coarse OS family used to select a <see cref="BackdropMath"/> preference chain. Deliberately
/// not the same type as <see cref="System.Runtime.InteropServices.OSPlatform"/> — only the three
/// families Nexus ships on are represented, keeping the switch in <see cref="BackdropMath.GetHintChain"/>
/// exhaustive without a throwaway default arm.</summary>
public enum BackdropPlatform
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Pure per-OS <see cref="BackdropLevel"/> preference-chain selection and platform-rejection
/// detection backing <c>NexusMonitor.UI.Services.BackdropService</c> (Phase 8 Task 7 — acrylic
/// groundwork).
///
/// <para><b>Chains — exactly one per OS, chosen only by whether backdrop is enabled at all</b> (see
/// <see cref="GetHintChain"/>): the task brief specifies a single ordered preference list per
/// platform, not a matrix of the four legacy <c>AppSettings.BackdropBlurMode</c> string values
/// (<c>None</c>/<c>Blur</c>/<c>Acrylic</c>/<c>Mica</c>) crossed with OS. Enabling backdrop via ANY
/// non-<c>"None"</c> mode value now requests this OS's own best-available chain, letting Avalonia's
/// own per-platform capability negotiation (via <c>TransparencyLevelHint</c>'s documented fallback
/// order) pick the actual level — <c>BackdropBlurMode</c>'s <c>Blur</c>/<c>Mica</c> sub-values no
/// longer independently change the requested chain (both currently collapse to the same chain as
/// <c>Acrylic</c>). This is a deliberate simplification for this groundwork task, flagged in the
/// Task 7 report as a design choice for a hostile reviewer to weigh — not an oversight.</para>
///
/// <para><b>macOS:</b> <see cref="BackdropLevel.AcrylicBlur"/> → <see cref="BackdropLevel.Blur"/> →
/// <see cref="BackdropLevel.None"/>. There is no distinct "Vibrancy" <see cref="BackdropLevel"/> —
/// Avalonia 11.2.3 has no such enum member (confirmed against the shipped package's XML docs). Per
/// Apple/Avalonia terminology, <see cref="BackdropLevel.Blur"/> (an NSVisualEffectView-backed
/// blur-behind) IS the macOS vibrancy material at this Avalonia version, and
/// <see cref="BackdropLevel.AcrylicBlur"/> is documented to fall back to it on platforms/compositors
/// that don't distinguish the two — so this chain already expresses "try the strongest blur, then
/// plain vibrancy-blur, then give up" without inventing an enum value.</para>
///
/// <para><b>Windows:</b> <see cref="BackdropLevel.Mica"/> → <see cref="BackdropLevel.AcrylicBlur"/> →
/// <see cref="BackdropLevel.None"/>. Mica is Windows-11-only per Avalonia's docs; older Windows
/// falls through to AcrylicBlur, then None.</para>
///
/// <para><b>Linux:</b> <see cref="BackdropLevel.None"/> only — never <see cref="BackdropLevel.Blur"/>
/// or <see cref="BackdropLevel.AcrylicBlur"/>. Not because those levels are unreachable in every
/// conceivable Linux configuration, but because this task's evidence (screenshot verification) can
/// only be gathered on the macOS dev machine it was implemented on — requesting an unverified blur
/// level on Linux would be exactly the kind of assumed-not-verified behavior the task's gate
/// criteria call out. Verified, not assumed: Linux gets the one level nothing here needs to prove.</para>
/// </summary>
public static class BackdropMath
{
    /// <summary>
    /// Returns the ordered <see cref="BackdropLevel"/> preference chain to request for
    /// <paramref name="platform"/> given the current glass/backdrop settings. The first element is
    /// always the platform's best-effort level; the last is always <see cref="BackdropLevel.None"/>
    /// (a safe universal fallback). Returns <c>[None]</c> whenever backdrop is logically off —
    /// <paramref name="glassEnabled"/> is <see langword="false"/> or <paramref name="mode"/> is
    /// exactly the string <c>"None"</c> — regardless of platform.
    /// </summary>
    /// <param name="platform">The running OS family.</param>
    /// <param name="glassEnabled">Mirrors <c>AppSettings.IsGlassEnabled</c> — the master Crystal
    /// Glass on/off switch.</param>
    /// <param name="mode">Mirrors <c>AppSettings.BackdropBlurMode</c> (<c>"None"</c>|<c>"Blur"</c>|
    /// <c>"Acrylic"</c>|<c>"Mica"</c>). Only the <c>"None"</c> value is distinguished; any other
    /// value (including unrecognized strings) is treated as "backdrop enabled."</param>
    public static IReadOnlyList<BackdropLevel> GetHintChain(BackdropPlatform platform, bool glassEnabled, string mode)
    {
        if (!glassEnabled || mode == "None")
            return NoneChain;

        return platform switch
        {
            BackdropPlatform.MacOS   => MacChain,
            BackdropPlatform.Windows => WindowsChain,
            BackdropPlatform.Linux   => NoneChain,
            _                        => NoneChain,
        };
    }

    private static readonly IReadOnlyList<BackdropLevel> NoneChain =
        new[] { BackdropLevel.None };

    private static readonly IReadOnlyList<BackdropLevel> MacChain =
        new[] { BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None };

    private static readonly IReadOnlyList<BackdropLevel> WindowsChain =
        new[] { BackdropLevel.Mica, BackdropLevel.AcrylicBlur, BackdropLevel.None };

    /// <summary>
    /// Returns whether the platform rejected the requested chain — i.e. the caller asked for
    /// something other than <see cref="BackdropLevel.None"/> (the chain's first/most-preferred
    /// entry) but the platform actually granted <paramref name="actual"/> == <see
    /// cref="BackdropLevel.None"/>. A chain of <c>[None]</c> (backdrop logically off) is never
    /// "rejected" — <see cref="BackdropLevel.None"/> was exactly what was asked for.
    /// </summary>
    /// <param name="requestedChain">The chain most recently passed as <c>TransparencyLevelHint</c>
    /// (i.e. the return value of <see cref="GetHintChain"/>).</param>
    /// <param name="actual">The platform's achieved level (mirrors
    /// <c>TopLevel.ActualTransparencyLevel</c>).</param>
    public static bool IsRejected(IReadOnlyList<BackdropLevel> requestedChain, BackdropLevel actual)
    {
        if (requestedChain.Count == 0) return false;
        return requestedChain[0] != BackdropLevel.None && actual == BackdropLevel.None;
    }
}
