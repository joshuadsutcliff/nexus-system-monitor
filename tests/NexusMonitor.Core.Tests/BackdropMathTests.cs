using FluentAssertions;
using NexusMonitor.Core.Backdrop;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="BackdropMath"/> — the pure per-OS/per-mode <see cref="BackdropLevel"/>
/// preference-chain selection and platform-rejection detection behind
/// <c>NexusMonitor.UI.Services.BackdropService</c> (Phase 8 Task 7, per-mode chains restored in the
/// gate-review fix pass). Lives in Core (with these tests) because the UI assembly has no test
/// project of its own — the same "Core-adjacent logic in a UI assembly" carve-out
/// <c>MotionMathTests</c> uses for <c>MotionMath</c>. Exercising all three <see cref="BackdropPlatform"/>
/// values here (rather than only whichever OS the CI runner happens to be) is exactly why the
/// platform is a parameter rather than an <c>OperatingSystem.IsXxx()</c> check baked into the pure
/// logic.
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

    [Fact]
    public void GetHintChain_UnrecognizedModeString_TreatedAsNone()
    {
        // Conductor ruling (Task 7 gate-review fix pass): an unrecognized mode string is treated
        // the same as "None" — [None] everywhere — the conservative choice for a value the
        // Settings UI never actually offers (BackdropModes is a closed 4-value list).
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "SomethingUnexpected")
            .Should().Equal(BackdropLevel.None);
    }

    // ── GetHintChain — "Blur" mode: [Blur, None] on macOS/Windows, [None] on Linux ─

    [Fact]
    public void GetHintChain_MacOS_Blur_ReturnsBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Blur")
            .Should().Equal(BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Windows_Blur_ReturnsBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.Windows, glassEnabled: true, mode: "Blur")
            .Should().Equal(BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Linux_Blur_ReturnsNoneOnly_NoInventedChain()
    {
        BackdropMath.GetHintChain(BackdropPlatform.Linux, glassEnabled: true, mode: "Blur")
            .Should().Equal(BackdropLevel.None);
    }

    // ── GetHintChain — "Acrylic" mode: [AcrylicBlur, Blur, None] on macOS/Windows, [None] on Linux ─

    [Fact]
    public void GetHintChain_MacOS_Acrylic_ReturnsAcrylicBlurThenBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Acrylic")
            .Should().Equal(BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Windows_Acrylic_ReturnsAcrylicBlurThenBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.Windows, glassEnabled: true, mode: "Acrylic")
            .Should().Equal(BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Linux_Acrylic_ReturnsNoneOnly_NoInventedChain()
    {
        // The task's hard rule: Linux never gets Blur/AcrylicBlur requested, since that behavior
        // can't be verified on the macOS dev machine this was built on.
        BackdropMath.GetHintChain(BackdropPlatform.Linux, glassEnabled: true, mode: "Acrylic")
            .Should().Equal(BackdropLevel.None);
    }

    // ── GetHintChain — "Mica" mode: Windows-only material, macOS falls to Acrylic's chain ─

    [Fact]
    public void GetHintChain_Windows_Mica_ReturnsMicaThenAcrylicBlurThenBlurThenNone()
    {
        BackdropMath.GetHintChain(BackdropPlatform.Windows, glassEnabled: true, mode: "Mica")
            .Should().Equal(BackdropLevel.Mica, BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_MacOS_Mica_FallsToAcrylicChain_NoMicaOnMacOS()
    {
        // Mica is Windows-only (Avalonia docs); macOS has no Mica material to request, so mode
        // "Mica" resolves to the same best-available chain as "Acrylic" rather than an invented
        // macOS Mica behavior.
        BackdropMath.GetHintChain(BackdropPlatform.MacOS, glassEnabled: true, mode: "Mica")
            .Should().Equal(BackdropLevel.AcrylicBlur, BackdropLevel.Blur, BackdropLevel.None);
    }

    [Fact]
    public void GetHintChain_Linux_Mica_ReturnsNoneOnly_NoInventedChain()
    {
        BackdropMath.GetHintChain(BackdropPlatform.Linux, glassEnabled: true, mode: "Mica")
            .Should().Equal(BackdropLevel.None);
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
