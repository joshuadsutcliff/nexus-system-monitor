using FluentAssertions;
using NexusMonitor.Core.Accessibility;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="AccessibilityClamp"/> — the pure precedence logic behind the OS "Reduce
/// Transparency" runtime clamp on Crystal Glass (task brief, 2026-07-11). Lives in Core (with
/// these tests) for the same "Core-adjacent logic in a UI assembly" reason as
/// <c>MotionMathTests</c>/<c>BackdropMathTests</c>.
/// </summary>
public class AccessibilityClampTests
{
    [Fact]
    public void EffectiveGlassEnabled_UserOn_SignalOff_ReturnsTrue()
    {
        AccessibilityClamp.EffectiveGlassEnabled(glassEnabled: true, reduceTransparency: false)
            .Should().BeTrue();
    }

    [Fact]
    public void EffectiveGlassEnabled_UserOn_SignalOn_ReturnsFalse()
    {
        // The clamp's core precedence claim: the OS signal wins even though the user's own
        // setting is on. Callers must not feed this back into the stored setting.
        AccessibilityClamp.EffectiveGlassEnabled(glassEnabled: true, reduceTransparency: true)
            .Should().BeFalse();
    }

    [Fact]
    public void EffectiveGlassEnabled_UserOff_SignalOff_ReturnsFalse()
    {
        AccessibilityClamp.EffectiveGlassEnabled(glassEnabled: false, reduceTransparency: false)
            .Should().BeFalse();
    }

    [Fact]
    public void EffectiveGlassEnabled_UserOff_SignalOn_ReturnsFalse()
    {
        AccessibilityClamp.EffectiveGlassEnabled(glassEnabled: false, reduceTransparency: true)
            .Should().BeFalse();
    }

    [Fact]
    public void EffectiveGlassEnabled_SignalClears_ReturnsToUsersOwnSetting()
    {
        // "Signal clears -> rendered on again" precedence: the same glassEnabled=true input
        // flips from off to on purely based on the signal — proving this function alone (not
        // some hidden mutation of glassEnabled) drives the rendered state.
        AccessibilityClamp.EffectiveGlassEnabled(glassEnabled: true, reduceTransparency: true)
            .Should().BeFalse("the signal was active");
        AccessibilityClamp.EffectiveGlassEnabled(glassEnabled: true, reduceTransparency: false)
            .Should().BeTrue("the signal cleared and the user's own setting was never touched");
    }
}
