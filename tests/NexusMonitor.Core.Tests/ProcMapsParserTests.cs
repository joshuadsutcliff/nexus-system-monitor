using FluentAssertions;
using NexusMonitor.Platform.Linux;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="ProcMapsParser"/> and <see cref="ProcMapsClassifier"/> — pure
/// <c>/proc/&lt;pid&gt;/maps</c> line parsing and classification shared by module enumeration
/// and the Memory Map view. No file I/O, so these run identically on every OS.
/// </summary>
public class ProcMapsParserTests
{
    [Fact]
    public void ParseLine_FileBackedMapping_ParsesAllFields()
    {
        var entry = ProcMapsParser.ParseLine("00400000-00452000 r-xp 00000000 fc:00 1234 /usr/bin/foo");

        entry.Should().NotBeNull();
        entry!.Value.Start.Should().Be(0x00400000);
        entry.Value.End.Should().Be(0x00452000);
        entry.Value.Perms.Should().Be("r-xp");
        entry.Value.Offset.Should().Be(0);
        entry.Value.Dev.Should().Be("fc:00");
        entry.Value.Inode.Should().Be(1234);
        entry.Value.Path.Should().Be("/usr/bin/foo");
        entry.Value.HasPath.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_AnonymousMapping_NoPathField_HasEmptyPath()
    {
        // Anonymous mappings have exactly 5 whitespace-delimited fields — no trailing path token.
        var entry = ProcMapsParser.ParseLine("7f1234550000-7f1234560000 rw-p 00000000 00:00 0");

        entry.Should().NotBeNull();
        entry!.Value.Path.Should().BeEmpty();
        entry.Value.HasPath.Should().BeFalse();
    }

    [Theory]
    [InlineData("[heap]")]
    [InlineData("[stack]")]
    [InlineData("[stack:1234]")]
    [InlineData("[vdso]")]
    public void ParseLine_BracketedPseudoPath_KeptVerbatim(string bracketPath)
    {
        var entry = ProcMapsParser.ParseLine($"55d000-55e000 rw-p 00000000 00:00 0 {bracketPath}");

        entry!.Value.Path.Should().Be(bracketPath);
        entry.Value.HasPath.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_TooFewFields_ReturnsNull()
    {
        ProcMapsParser.ParseLine("garbage line").Should().BeNull();
    }

    [Fact]
    public void ParseLine_MalformedAddressRange_DegradesToZeroRatherThanRejectingLine()
    {
        // Matches the tolerant fallback ReadModules already had: an unparsable address must
        // not drop the whole entry, it just reports a zero base address.
        var entry = ProcMapsParser.ParseLine("not-an-address rw-p 00000000 00:00 0 /usr/bin/foo");

        entry.Should().NotBeNull();
        entry!.Value.Start.Should().Be(0);
        entry.Value.End.Should().Be(0);
        entry.Value.Path.Should().Be("/usr/bin/foo");
    }

    [Fact]
    public void ParseLine_NoDashInAddressField_DegradesToZero()
    {
        var entry = ProcMapsParser.ParseLine("nodash rw-p 00000000 00:00 0 /usr/bin/foo");

        entry.Should().NotBeNull();
        entry!.Value.Start.Should().Be(0);
        entry.Value.End.Should().Be(0);
    }

    [Fact]
    public void ParseLine_DeletedFileSuffix_KnownLimitationTruncatesToLastToken()
    {
        // Documented, inherited limitation: the kernel appends " (deleted)" (a literal space)
        // for unlinked backing files, and naive whitespace splitting means only the trailing
        // token is captured as Path. This test pins the (documented) behavior rather than
        // silently changing it.
        var entry = ProcMapsParser.ParseLine("00400000-00452000 r-xp 00000000 fc:00 1234 /usr/bin/foo (deleted)");

        entry!.Value.Path.Should().Be("(deleted)");
    }

    // ── ProcMapsClassifier ──────────────────────────────────────────────────────

    [Fact]
    public void ClassifyPath_EmptyPath_IsPrivateWithEmptyDescription()
    {
        var (regionType, description) = ProcMapsClassifier.ClassifyPath("", "rw-p");

        regionType.Should().Be("Private");
        description.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[heap]")]
    [InlineData("[stack]")]
    [InlineData("[vdso]")]
    public void ClassifyPath_BracketedPseudoPath_IsPrivateWithTagAsDescription(string bracketPath)
    {
        var (regionType, description) = ProcMapsClassifier.ClassifyPath(bracketPath, "rw-p");

        regionType.Should().Be("Private");
        description.Should().Be(bracketPath);
    }

    [Fact]
    public void ClassifyPath_FileBackedWithExecBit_IsImage()
    {
        var (regionType, description) = ProcMapsClassifier.ClassifyPath("/usr/bin/foo", "r-xp");

        regionType.Should().Be("Image");
        description.Should().Be("/usr/bin/foo");
    }

    [Fact]
    public void ClassifyPath_FileBackedWithoutExecBit_IsMapped()
    {
        var (regionType, description) = ProcMapsClassifier.ClassifyPath("/usr/bin/foo", "rw-p");

        regionType.Should().Be("Mapped");
        description.Should().Be("/usr/bin/foo");
    }

    [Fact]
    public void DecodeProtection_NoBitsSet_IsNoAccess()
    {
        ProcMapsClassifier.DecodeProtection("---p").Should().Be("No Access");
    }

    [Theory]
    [InlineData("r--p", "R")]
    [InlineData("rw-p", "RW")]
    [InlineData("r-xp", "RX")]
    [InlineData("rwxp", "RWX")]
    [InlineData("-w-p", "W")]
    [InlineData("--xp", "X")]
    public void DecodeProtection_DecodesEveryCombination(string perms, string expected)
    {
        ProcMapsClassifier.DecodeProtection(perms).Should().Be(expected);
    }
}
