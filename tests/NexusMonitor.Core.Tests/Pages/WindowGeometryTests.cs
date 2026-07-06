using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

/// <summary>Pure math tests for <see cref="WindowGeometry.ClampToScreens"/>: unchanged when fully
/// on-screen, shifted minimally when the title bar spills off an intersected screen, and centered
/// (shrinking if needed) into the fallback rect when no screen is intersected at all.</summary>
public class WindowGeometryTests
{
    private static readonly ScreenRect PrimaryScreen = new(0, 0, 1920, 1080);

    [Fact]
    public void ClampToScreens_FullyOnScreen_IsUnchanged()
    {
        var window = new ScreenRect(100, 100, 800, 600);

        var result = WindowGeometry.ClampToScreens(window, new[] { PrimaryScreen }, PrimaryScreen);

        result.Should().Be(window);
    }

    [Fact]
    public void ClampToScreens_PartiallyOffscreen_ShiftsTitleBarFullyOnScreen()
    {
        // Overlaps the primary screen by 120px horizontally / 80px vertically (>= 64px threshold).
        var window = new ScreenRect(1800, 1000, 800, 600);

        var result = WindowGeometry.ClampToScreens(window, new[] { PrimaryScreen }, PrimaryScreen);

        // X shifts left so the window's right edge lands on the screen's right edge (1920 - 800 = 1120).
        // Y is untouched: the 32px title bar at y=1000 already fits within the 1080px-tall screen.
        result.Should().Be(new ScreenRect(1120, 1000, 800, 600));
    }

    [Fact]
    public void ClampToScreens_FullyOffscreen_CentersInFallback()
    {
        var window = new ScreenRect(5000, 5000, 400, 300);

        var result = WindowGeometry.ClampToScreens(window, new[] { PrimaryScreen }, PrimaryScreen);

        result.Should().Be(new ScreenRect(760, 390, 400, 300));
    }

    [Fact]
    public void ClampToScreens_NoScreensAndOversizedWindow_ShrinksToFallbackSize()
    {
        var window = new ScreenRect(0, 0, 3000, 2000);
        var fallback = new ScreenRect(100, 50, 1920, 1080);

        var result = WindowGeometry.ClampToScreens(window, Array.Empty<ScreenRect>(), fallback);

        result.Should().Be(fallback);
    }
}
