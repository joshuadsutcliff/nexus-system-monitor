using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MacOSAccessibilitySignals"/>: the pure <c>defaults read</c> output parser
/// runs on every OS (no subprocess involved), and a real-host integration test (gated on
/// <see cref="OperatingSystem.IsMacOS"/>, a deliberate no-op elsewhere — same pattern as
/// <c>MacOSLaunchdIndexIntegrationTests</c>) exercises the actual subprocess path per the task
/// brief: "a macOS-gated integration test may assert the real signal reads without throwing."
/// </summary>
public class MacOSAccessibilitySignalsTests
{
    // ── ParseBoolPreferenceOutput — pure, any-OS ──────────────────────────────

    [Theory]
    [InlineData("1")]
    [InlineData("1\n")]
    [InlineData("  1  ")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("yes")]
    public void ParseBoolPreferenceOutput_TruthyForms_ReturnsTrue(string output)
    {
        MacOSAccessibilitySignals.ParseBoolPreferenceOutput(output).Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0\n")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseBoolPreferenceOutput_FalsyOrEmptyForms_ReturnsFalse(string? output)
    {
        MacOSAccessibilitySignals.ParseBoolPreferenceOutput(output).Should().BeFalse();
    }

    [Fact]
    public void ParseBoolPreferenceOutput_UnparsableGarbage_DegradesFalse_NeverThrows()
    {
        var act = () => MacOSAccessibilitySignals.ParseBoolPreferenceOutput("The domain/default pair does not exist");
        act.Should().NotThrow();
        MacOSAccessibilitySignals.ParseBoolPreferenceOutput("The domain/default pair does not exist").Should().BeFalse();
    }

    // ── ReadBoolPreference / constructor — real-host integration, macOS-gated ─

    [Fact]
    public void ReadBoolPreference_OnRealHost_NeverThrows_ReturnsABool()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var act = () => MacOSAccessibilitySignals.ReadBoolPreference("reduceMotion");
        act.Should().NotThrow();

        var act2 = () => MacOSAccessibilitySignals.ReadBoolPreference("reduceTransparency");
        act2.Should().NotThrow();
    }

    [Fact]
    public void ReadBoolPreference_UnknownKey_DegradesFalse_NeverThrows()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // A key that certainly doesn't exist in this domain — `defaults read` exits non-zero,
        // which must degrade to false rather than throw.
        MacOSAccessibilitySignals.ReadBoolPreference("thisKeyDefinitelyDoesNotExist12345")
            .Should().BeFalse();
    }

    [Fact]
    public void Constructor_OnRealHost_NeverThrows_ProducesBoolProperties()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var act = () => new MacOSAccessibilitySignals();
        act.Should().NotThrow();

        var signals = new MacOSAccessibilitySignals();
        // No assertion on the actual value — this host's real accessibility preferences are
        // whatever they are. The contract under test is "reads without throwing."
        _ = signals.ReduceMotion;
        _ = signals.ReduceTransparency;
    }
}
