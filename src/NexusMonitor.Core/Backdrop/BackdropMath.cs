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
/// families Nexus ships on are represented. The nested platform switches in
/// <see cref="BackdropMath.GetHintChain"/> still keep a defensive <c>_ => NoneChain</c> default arm:
/// the C# compiler cannot statically prove a switch over this enum's current three members is
/// exhaustive (and never enforces it against a future added member), so the default arm is a safety
/// net, not a sign the switch is missing a case today.</summary>
public enum BackdropPlatform
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Pure per-OS/per-mode <see cref="BackdropLevel"/> preference-chain selection and
/// platform-rejection detection backing <c>NexusMonitor.UI.Services.BackdropService</c> (Phase 8
/// Task 7 — acrylic groundwork; per-mode chains restored in the Task 7 gate-review fix pass).
///
/// <para><b>Chains — mode selects the preference tier, OS gates availability</b> (see
/// <see cref="GetHintChain"/>): the four legacy <c>AppSettings.BackdropBlurMode</c> string values
/// (<c>None</c>/<c>Blur</c>/<c>Acrylic</c>/<c>Mica</c>) each request a genuinely different ordered
/// chain — restoring the distinction the pre-Task-7 code honored and the Settings UI still
/// advertises (<c>SettingsViewModel.BackdropModes</c>). An earlier revision of this task
/// collapsed all non-<c>"None"</c> modes to one identical per-OS chain; that was a gate-review
/// finding (Important) and has been reverted here. Every enabled chain still ends in
/// <see cref="BackdropLevel.None"/> as a safe universal fallback, and Linux is unconditionally
/// <c>[None]</c> for every mode (see below).</para>
///
/// <para><b>"Blur":</b> <see cref="BackdropLevel.Blur"/> → <see cref="BackdropLevel.None"/> on macOS
/// and Windows.</para>
///
/// <para><b>"Acrylic":</b> <see cref="BackdropLevel.AcrylicBlur"/> → <see cref="BackdropLevel.Blur"/>
/// → <see cref="BackdropLevel.None"/> on macOS and Windows. There is no distinct "Vibrancy"
/// <see cref="BackdropLevel"/> — Avalonia 11.2.3 has no such enum member (confirmed against the
/// shipped package's XML docs). Per Apple/Avalonia terminology, <see cref="BackdropLevel.Blur"/> (an
/// NSVisualEffectView-backed blur-behind) IS the macOS vibrancy material at this Avalonia version,
/// and <see cref="BackdropLevel.AcrylicBlur"/> is documented to fall back to it on
/// platforms/compositors that don't distinguish the two.</para>
///
/// <para><b>"Mica":</b> Windows gets <see cref="BackdropLevel.Mica"/> → <see cref="BackdropLevel.AcrylicBlur"/>
/// → <see cref="BackdropLevel.Blur"/> → <see cref="BackdropLevel.None"/> (Mica is Windows-11-only per
/// Avalonia's docs; older Windows falls through the rest of the chain). macOS has no Mica material at
/// all, so mode <c>"Mica"</c> on macOS falls to the same chain as <c>"Acrylic"</c> (best available,
/// not an invented macOS Mica) — <see cref="BackdropLevel.AcrylicBlur"/> → <see cref="BackdropLevel.Blur"/>
/// → <see cref="BackdropLevel.None"/>.</para>
///
/// <para><b>"None" / unrecognized:</b> <see cref="BackdropLevel.None"/> only, on every platform —
/// mirrors the disabled (<c>glassEnabled == false</c>) case exactly.</para>
///
/// <para><b>Linux:</b> <see cref="BackdropLevel.None"/> only, for every mode — never
/// <see cref="BackdropLevel.Blur"/>, <see cref="BackdropLevel.AcrylicBlur"/>, or
/// <see cref="BackdropLevel.Mica"/>. Not because those levels are unreachable in every conceivable
/// Linux configuration, but because this task's evidence (screenshot verification) can only be
/// gathered on the macOS dev machine it was implemented on — requesting an unverified blur level on
/// Linux would be exactly the kind of assumed-not-verified behavior the task's gate criteria call
/// out. Verified, not assumed: Linux gets the one level nothing here needs to prove. Plan-mandated,
/// not a per-mode judgment call.</para>
/// </summary>
public static class BackdropMath
{
    /// <summary>
    /// Returns the ordered <see cref="BackdropLevel"/> preference chain to request for
    /// <paramref name="platform"/>/<paramref name="mode"/> given the current glass/backdrop
    /// settings. The first element is always the tier's best-effort level for that OS; the last is
    /// always <see cref="BackdropLevel.None"/> (a safe universal fallback). Returns <c>[None]</c>
    /// whenever backdrop is logically off — <paramref name="glassEnabled"/> is
    /// <see langword="false"/>, <paramref name="mode"/> is exactly the string <c>"None"</c>, or
    /// <paramref name="mode"/> is unrecognized — or whenever <paramref name="platform"/> is
    /// <see cref="BackdropPlatform.Linux"/>, regardless of mode.
    /// </summary>
    /// <param name="platform">The running OS family.</param>
    /// <param name="glassEnabled">Mirrors <c>AppSettings.IsGlassEnabled</c> — the master Crystal
    /// Glass on/off switch.</param>
    /// <param name="mode">Mirrors <c>AppSettings.BackdropBlurMode</c> (<c>"None"</c>|<c>"Blur"</c>|
    /// <c>"Acrylic"</c>|<c>"Mica"</c>). Each recognized non-<c>"None"</c> value selects its own
    /// preference tier (see class doc); an unrecognized string is treated the same as
    /// <c>"None"</c> — <c>[None]</c> — the conservative choice for a value the Settings UI never
    /// actually offers.</param>
    public static IReadOnlyList<BackdropLevel> GetHintChain(BackdropPlatform platform, bool glassEnabled, string mode)
    {
        if (!glassEnabled)
            return NoneChain;

        return mode switch
        {
            "Blur"    => platform switch
            {
                BackdropPlatform.MacOS   => BlurChain,
                BackdropPlatform.Windows => BlurChain,
                BackdropPlatform.Linux   => NoneChain,
                _                        => NoneChain,
            },
            "Acrylic" => platform switch
            {
                BackdropPlatform.MacOS   => AcrylicChain,
                BackdropPlatform.Windows => AcrylicChain,
                BackdropPlatform.Linux   => NoneChain,
                _                        => NoneChain,
            },
            "Mica"    => platform switch
            {
                BackdropPlatform.Windows => MicaChain,
                BackdropPlatform.MacOS   => AcrylicChain, // Mica is Windows-only; macOS's best available
                BackdropPlatform.Linux   => NoneChain,
                _                        => NoneChain,
            },
            _         => NoneChain, // "None" or any unrecognized mode string
        };
    }

    private static readonly IReadOnlyList<BackdropLevel> NoneChain =
        new[] { BackdropLevel.None };

    private static readonly IReadOnlyList<BackdropLevel> BlurChain =
        new[] { BackdropLevel.Blur, BackdropLevel.None };

    private static readonly IReadOnlyList<BackdropLevel> AcrylicChain =
        new[] { BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None };

    private static readonly IReadOnlyList<BackdropLevel> MicaChain =
        new[] { BackdropLevel.Mica, BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None };

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
