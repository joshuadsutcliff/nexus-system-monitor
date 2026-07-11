using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Platform.Windows;

/// <summary>
/// Windows implementation of <see cref="IAccessibilitySignals"/>.
///
/// <para><b>Reduce Motion</b> — <c>SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)</c> reads
/// whether Windows' "Show animations in Windows" setting (Settings &gt; Accessibility &gt; Visual
/// effects &gt; Animation effects) is currently on; this class reports the logical inverse (no
/// animations == reduce motion). No other class in this codebase P/Invokes
/// <c>user32.dll!SystemParametersInfoW</c> yet — this follows the same
/// <c>[DllImport]</c>/guarded-try-catch conventions <see cref="WindowsPowerPlanProvider"/> uses
/// for its own (different) Win32 API surface.</para>
///
/// <para><b>Reduce Transparency</b> — reads the per-user personalization registry value
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency</c>
/// (DWORD; 0 = "Transparency effects" toggle is OFF in Settings &gt; Personalization &gt; Colors),
/// the same <c>Registry.CurrentUser.OpenSubKey</c> pattern <see cref="WindowsWallpaperService"/>
/// already uses for a different personalization key.</para>
///
/// <para><b>Read live on every property access, not cached at construction</b> (contrast
/// <c>MacOSAccessibilitySignals</c>, which reads once at startup because its read is a subprocess
/// spawn): both reads here are a single in-process P/Invoke call or a single registry-key open —
/// microsecond-cost, no subprocess, no file I/O beyond the registry — so re-reading on every
/// access is the CHEAPEST possible way to make this signal reflect the OS's current state without
/// building any polling/notification infrastructure, satisfying the task brief's "live
/// change-listening only if a cheap per-platform hook exists" clause for free.</para>
///
/// <para>Reads degrade: any P/Invoke failure or registry-access exception returns
/// <see langword="false"/> (assume no clamp) rather than throwing.</para>
/// </summary>
public sealed class WindowsAccessibilitySignals : IAccessibilitySignals
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    public bool ReduceMotion
    {
        get
        {
            try
            {
                bool animationsEnabled = true; // Windows ships with client-area animation ON by default
                if (!SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref animationsEnabled, 0))
                    return false; // call failed — degrade honest, assume no clamp
                return !animationsEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool ReduceTransparency
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("EnableTransparency");
                if (value is int enabled) return enabled == 0;
                return false; // key/value absent — Windows' shipped default is transparency ON, so no clamp
            }
            catch
            {
                return false;
            }
        }
    }
}
