using FluentAssertions;
using NexusMonitor.Core.Backdrop;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="BackdropMath"/> — the pure per-OS <see cref="BackdropLevel"/>
/// preference-chain selection and platform-rejection detection behind
/// <c>NexusMonitor.UI.Services.BackdropService</c> (Phase 8 Task 7). Lives in Core (with these
/// tests) because the UI assembly has no test project of its own — the same "Core-adjacent logic
/// in a UI assembly" carve-out <c>MotionMathTests</c> uses for <c>MotionMath</c>. Exercising all
/// three <see cref="BackdropPlatform"/> values here (rather than only whichever OS the CI runner
/// happens to be) is exactly why the platform is a parameter rather than an
/// <c>OperatingSystem.IsXxx()</c> check baked into the pure logic.
/// </summary>
public class BackdropMathTests
{
    // ── GetHintChain — disabled cases (glassEnabled=false or mode="None") ──────

    [Theory]
    [InlineData(BackdropPlatform.MacOS)]
    [InlineData(BackdropPlatform.Windows)]
    [InlineData(BackdropPlatform.Linux)]
    public void GetHintChain_GlassDisabled_ReturnsNoneOnly_RegardlessOfPlatform(BackdropPlatform platform)
    {
        BackdropMath.GetHintChain(platform, glassEnabled: false, mode: "Acrylic")
            .Should().Equal(BackdropLevel.None);
    }

    [Theory]
    [InlineData(BackdropPlatform.MacOS)]
    [InlineData(BackdropPlatform.Windows)]
    [InlineData(BackdropPlatform.Linux)]
    public void GetHintChain_ModeNone_ReturnsNoneOnly_RegardlessOfPlatform(BackdropPlatform platform)
    {
        BackdropMath.GetHintChain(platform, glassEnabled: true, mode: "None")
            .Should().Equal(BackdropLevel.None);
    }

    // ── GetHintChain — enabled cases, exact per-OS chains from the task brief ──

    [Fact]
    public void GetHintChain_MacOS_Enabled_ReturnsAcrylicBlurThenBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Acrylic")
            .Should().Equal(BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Windows_Enabled_ReturnsMicaThenAcrylicBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.Windows, glassEnabled: true, mode: "Acrylic")
            .Should().Equal(BackdropLevel.Mica, BackdropLevel.AcrylicBlur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Linux_Enabled_ReturnsNoneOnly_NoInventedChain()
    {
        // The task's hard rule: Linux never gets Blur/AcrylicBlur requested, since that behavior
        // can't be verified on the macOS dev machine this was built on.
        BackdropMath.GetHintChain(BackdropPlatform.Linux, glassEnabled: true, mode: "Acrylic")
            .Should().Equal(BackdropLevel.None);
    }

    [Theory]
    [InlineData("Blur")]
    [InlineData("Mica")]
    public void GetHintChain_MacOS_AnyNonNoneMode_CollapsesToTheSameMacChain(string mode)
    {
        // Documented simplification (see BackdropMath's class doc): the legacy Blur/Mica sub-modes
        // no longer diverge from Acrylic's chain — only "None" vs "not None" is distinguished.
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: mode)
            .Should().Equal(BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_UnrecognizedModeString_TreatedAsEnabled()
    {
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "SomethingUnexpected")
            .Should().Equal(BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    // ── IsRejected ───────────────────────────────────────────────────────────

    [Fact]
    public void IsRejected_RequestedAcrylicButActualNone_ReturnsTrue()
    {
        var chain = BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Acrylic");
        BackdropMath.IsRejected(chain, BackdropLevel.None).Should().BeTrue();
    }

    [Fact]
    public void IsRejected_RequestedAcrylicAndActualAcrylic_ReturnsFalse()
    {
        var chain = BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Acrylic");
        BackdropMath.IsRejected(chain, BackdropLevel.AcrylicBlur).Should().BeFalse();
    }

    [Fact]
    public void IsRejected_RequestedAcrylicButActualFallsBackToBlur_ReturnsFalse()
    {
        // Falling back to a lesser-but-still-non-None level in the same chain is the *intended*
        // fallback mechanism, not a rejection.
        var chain = BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Acrylic");
        BackdropMath.IsRejected(chain, BackdropLevel.Blur).Should().BeFalse();
    }

    [Fact]
    public void IsRejected_RequestedNoneAndActualNone_ReturnsFalse()
    {
        // None was explicitly asked for — never "rejected."
        var chain = BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: false, mode: "None");
        BackdropMath.IsRejected(chain, BackdropLevel.None).Should().BeFalse();
    }

    [Fact]
    public void IsRejected_LinuxAlwaysRequestsNone_NeverRejected()
    {
        var chain = BackdropMath.GetHintChain(BackdropPlatform.Linux, glassEnabled: true, mode: "Acrylic");
        BackdropMath.IsRejected(chain, BackdropLevel.None).Should().BeFalse();
    }

    [Fact]
    public void IsRejected_EmptyChain_ReturnsFalse()
    {
        BackdropMath.IsRejected(Array.Empty<BackdropLevel>(), BackdropLevel.None).Should().BeFalse();
    }
}
