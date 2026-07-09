using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MacOSLaunchdIndex.BuildLabelMaps"/> — pure, dependency-free label-map
/// construction logic factored out of <see cref="MacOSLaunchdIndex.GetOrBuildIndex"/> so it can be
/// unit-tested with plain fixture paths on every OS (no real launchd/plutil involved), mirroring
/// the LaunchdStartType pure/IO split.
///
/// Gate-review finding pinned here: the doc comment on <c>MacOSLaunchdIndex.s_plistDirs</c>
/// promises "first match for a filename-derived label wins" across the 5 launchd directories, but
/// the label maps used to be built by iterating <c>_byPath</c> directly (arbitrary
/// <see cref="Dictionary{TKey,TValue}"/> enumeration order, populated from a parallel
/// <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/> merge) — so which directory won
/// for a same-named plist in two directories was effectively random. <see cref="MacOSLaunchdIndex.BuildLabelMaps"/>
/// now walks the caller-supplied directory list in order instead, which these tests pin.
/// </summary>
public class MacOSLaunchdIndexTests
{
    private static readonly string[] OrderedDirs =
    [
        "/System/Library/LaunchDaemons",
        "/System/Library/LaunchAgents",
        "/Library/LaunchDaemons",
        "/Library/LaunchAgents",
        "/Users/tester/Library/LaunchAgents",
    ];

    [Fact]
    public void BuildLabelMaps_SameFilenameLabelInTwoDirs_EarlierDirectoryWins()
    {
        // "com.example.thing" exists under both LaunchAgents (later, index 1) and LaunchDaemons
        // (earlier, index 0), with different facts — the earlier directory (LaunchDaemons) must
        // win regardless of the order these entries are supplied in.
        var entries = new[]
        {
            ("/System/Library/LaunchAgents/com.example.thing.plist", false, false, (string?)null, false),
            ("/System/Library/LaunchDaemons/com.example.thing.plist", true, false, (string?)null, false),
        };

        var (byFilenameLabel, _) = MacOSLaunchdIndex.BuildLabelMaps(entries, OrderedDirs);

        byFilenameLabel.Should().ContainKey("com.example.thing");
        byFilenameLabel["com.example.thing"].RunAtLoad.Should().BeTrue(
            "LaunchDaemons precedes LaunchAgents in the declared directory order, so it should win");
    }

    [Fact]
    public void BuildLabelMaps_SameFilenameLabelInTwoDirs_WinnerIndependentOfInputOrder()
    {
        // Same fixture as above, but with the two entries supplied in the opposite order — the
        // winner must still be determined by directory precedence, not input/enumeration order.
        var entries = new[]
        {
            ("/System/Library/LaunchDaemons/com.example.thing.plist", true, false, (string?)null, false),
            ("/System/Library/LaunchAgents/com.example.thing.plist", false, false, (string?)null, false),
        };

        var (byFilenameLabel, _) = MacOSLaunchdIndex.BuildLabelMaps(entries, OrderedDirs);

        byFilenameLabel["com.example.thing"].RunAtLoad.Should().BeTrue();
    }

    [Fact]
    public void BuildLabelMaps_SameInternalLabelInTwoDirs_EarlierDirectoryWinsToo()
    {
        // The internal-Label-keyed map gets the same directory-order precedence treatment as the
        // filename-keyed map (the brief's "and any label-keyed map" instruction).
        var entries = new (string, bool, bool, string?, bool)[]
        {
            ("/Library/LaunchAgents/some-file-a.plist", false, false, "com.example.internal", false),
            ("/Library/LaunchDaemons/some-file-b.plist", true, false, "com.example.internal", false),
        };

        var (_, byInternalLabel) = MacOSLaunchdIndex.BuildLabelMaps(entries, OrderedDirs);

        byInternalLabel["com.example.internal"].RunAtLoad.Should().BeTrue(
            "LaunchDaemons precedes LaunchAgents in the declared directory order, so it should win");
    }

    [Fact]
    public void BuildLabelMaps_ParseFailedEntry_ContributesNoFactsToEitherMap()
    {
        // Negative-cache sentinel (finding 5): a ParseFailed entry must behave exactly like "no
        // plist found" — it must not pollute either label map with fabricated default-false facts,
        // and must not shadow a real entry for the same label from a lower-precedence directory.
        var entries = new (string, bool, bool, string?, bool)[]
        {
            ("/System/Library/LaunchDaemons/com.example.bad.plist", false, false, "com.example.bad", true),
        };

        var (byFilenameLabel, byInternalLabel) = MacOSLaunchdIndex.BuildLabelMaps(entries, OrderedDirs);

        byFilenameLabel.Should().NotContainKey("com.example.bad");
        byInternalLabel.Should().NotContainKey("com.example.bad");
    }

    [Fact]
    public void BuildLabelMaps_ParseFailedEntry_DoesNotBlockALowerPrecedenceGoodEntry()
    {
        // A known-bad plist in the higher-precedence directory must not shadow a good plist for
        // the same filename-derived label in a lower-precedence directory — it contributes nothing,
        // so the good entry from the later directory should still win.
        var entries = new[]
        {
            ("/System/Library/LaunchDaemons/com.example.thing.plist", false, false, (string?)null, true),
            ("/System/Library/LaunchAgents/com.example.thing.plist", true, false, (string?)null, false),
        };

        var (byFilenameLabel, _) = MacOSLaunchdIndex.BuildLabelMaps(entries, OrderedDirs);

        byFilenameLabel.Should().ContainKey("com.example.thing");
        byFilenameLabel["com.example.thing"].RunAtLoad.Should().BeTrue(
            "the LaunchDaemons entry failed to parse and contributes nothing, so the LaunchAgents entry should be used");
    }

    [Fact]
    public void BuildLabelMaps_NoDuplicates_BothMapsPopulatedNormally()
    {
        var entries = new[]
        {
            ("/System/Library/LaunchDaemons/com.apple.securityd.plist", true, false, "com.apple.securityd", false),
            ("/Library/LaunchAgents/com.example.other.plist", false, true, (string?)null, false),
        };

        var (byFilenameLabel, byInternalLabel) = MacOSLaunchdIndex.BuildLabelMaps(entries, OrderedDirs);

        byFilenameLabel.Should().HaveCount(2);
        byFilenameLabel["com.apple.securityd"].RunAtLoad.Should().BeTrue();
        byFilenameLabel["com.example.other"].KeepAliveTruthy.Should().BeTrue();
        byInternalLabel.Should().ContainKey("com.apple.securityd");
        byInternalLabel.Should().HaveCount(1, "only the first entry has a non-null internal Label");
    }
}
