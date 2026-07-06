namespace NexusMonitor.Core.Pages;

/// <summary>A plain-int screen-space rectangle (OS window or monitor bounds). No Avalonia
/// dependency so Core stays UI-framework-free.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height);

/// <summary>Pure math for keeping a pop-out window's title bar reachable on some connected
/// screen, or recovering it into a fallback rect when its former monitor is gone.</summary>
public static class WindowGeometry
{
    /// <summary>Minimum overlap (in each axis) with a screen for that screen to be considered
    /// the window's home, rather than treating the window as fully offscreen.</summary>
    private const int MinIntersectPx = 64;

    /// <summary>Height of the draggable title-bar region that must stay fully on-screen.</summary>
    private const int TitleBarHeightPx = 32;

    /// <summary>Returns <paramref name="window"/> adjusted to stay usable: if it intersects a
    /// screen in <paramref name="screens"/> by at least 64px in both axes, it is shifted
    /// (position only) so its top 32px title-bar region is fully on that screen. If it intersects
    /// no screen at all (its monitor was unplugged), it is centered inside <paramref name="fallback"/>,
    /// shrunk first if needed so neither dimension exceeds the fallback's.</summary>
    public static ScreenRect ClampToScreens(ScreenRect window, IReadOnlyList<ScreenRect> screens, ScreenRect fallback)
    {
        foreach (var screen in screens)
        {
            if (OverlapsAtLeast(window, screen, MinIntersectPx))
                return ShiftTitleBarOnto(window, screen);
        }
        return CenterInFallback(window, fallback);
    }

    /// <summary>True when two rects overlap by at least <paramref name="minPx"/> pixels along both axes.</summary>
    private static bool OverlapsAtLeast(ScreenRect a, ScreenRect b, int minPx)
    {
        var overlapWidth = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var overlapHeight = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return overlapWidth >= minPx && overlapHeight >= minPx;
    }

    /// <summary>Shifts (never resizes) <paramref name="window"/> so its title-bar region lands
    /// fully within <paramref name="screen"/>'s bounds.</summary>
    private static ScreenRect ShiftTitleBarOnto(ScreenRect window, ScreenRect screen)
    {
        var x = ClampAxis(window.X, window.Width, screen.X, screen.Width);
        var y = ClampAxis(window.Y, TitleBarHeightPx, screen.Y, screen.Height);
        return window with { X = x, Y = y };
    }

    /// <summary>Clamps a single-axis position so a span of <paramref name="size"/> starting at it
    /// lies fully within [<paramref name="screenPos"/>, <paramref name="screenPos"/> + <paramref name="screenSize"/>).
    /// If the span itself is at least as large as the screen, it is pinned to the screen's start.</summary>
    private static int ClampAxis(int pos, int size, int screenPos, int screenSize)
    {
        if (size >= screenSize) return screenPos;
        var min = screenPos;
        var max = screenPos + screenSize - size;
        return Math.Clamp(pos, min, max);
    }

    /// <summary>Centers <paramref name="window"/> inside <paramref name="fallback"/>, first shrinking
    /// each dimension to at most the fallback's corresponding dimension.</summary>
    private static ScreenRect CenterInFallback(ScreenRect window, ScreenRect fallback)
    {
        var width = Math.Min(window.Width, fallback.Width);
        var height = Math.Min(window.Height, fallback.Height);
        var x = fallback.X + (fallback.Width - width) / 2;
        var y = fallback.Y + (fallback.Height - height) / 2;
        return new ScreenRect(x, y, width, height);
    }
}
