using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class PathKeysTests
{
    [Fact]
    public void NormalizeDisplay_StripsTrailingSeparator()
    {
        var baseDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        PathKeys.NormalizeDisplay(baseDir + Path.DirectorySeparatorChar)
            .Should().Be(baseDir);
    }

    [Fact]
    public void NormalizeDisplay_KeepsRootSeparator()
    {
        // A filesystem root ("/" or "C:\") must not be stripped to empty.
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        PathKeys.NormalizeDisplay(root).Should().Be(root);
    }

    [Fact]
    public void ToRootKey_FoldsCase_OnCaseInsensitivePlatforms()
    {
        var a = PathKeys.ToRootKey(Path.Combine(Path.GetTempPath(), "MyData"));
        var b = PathKeys.ToRootKey(Path.Combine(Path.GetTempPath(), "mydata"));
        if (PathKeys.NamesAreCaseInsensitive) a.Should().Be(b);
        else                                  a.Should().NotBe(b);
    }

    [Fact]
    public void ToRootKey_SameForTrailingSlashVariants()
    {
        var p = Path.Combine(Path.GetTempPath(), "SnapRoot");
        PathKeys.ToRootKey(p + Path.DirectorySeparatorChar).Should().Be(PathKeys.ToRootKey(p));
    }

    [Fact]
    public void NameComparer_MatchesPlatformRule()
    {
        var equal = PathKeys.NameComparer.Equals("README.md", "readme.md");
        equal.Should().Be(PathKeys.NamesAreCaseInsensitive);
    }
}
