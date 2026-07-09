using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MacOSUidNameCache"/> — pure caching policy with an injected resolver.
/// No P/Invoke, so this runs identically on every OS.
/// </summary>
public class MacOSUidNameCacheTests
{
    [Fact]
    public void GetName_FirstCall_InvokesResolver()
    {
        var cache = new MacOSUidNameCache(uid => "josh");

        var name = cache.GetName(501);

        name.Should().Be("josh");
        cache.ResolveCallCount.Should().Be(1);
    }

    [Fact]
    public void GetName_RepeatedCallsSameUid_ResolvesOnlyOnce()
    {
        var cache = new MacOSUidNameCache(uid => "josh");

        cache.GetName(501);
        cache.GetName(501);
        cache.GetName(501);

        cache.ResolveCallCount.Should().Be(1);
    }

    [Fact]
    public void GetName_DifferentUids_ResolvesEachIndependently()
    {
        var cache = new MacOSUidNameCache(uid => uid switch
        {
            0   => "root",
            501 => "josh",
            _   => null,
        });

        cache.GetName(0).Should().Be("root");
        cache.GetName(501).Should().Be("josh");
        cache.ResolveCallCount.Should().Be(2);

        // Re-fetching either uid must not call the resolver again.
        cache.GetName(0);
        cache.GetName(501);
        cache.ResolveCallCount.Should().Be(2);
    }

    [Fact]
    public void GetName_ResolverReturnsNull_CachesEmptyStringHonestly()
    {
        var cache = new MacOSUidNameCache(uid => null);

        var name = cache.GetName(999);

        name.Should().Be(string.Empty);
        cache.ResolveCallCount.Should().Be(1);

        // Still cached as empty — must not retry the resolver on every call.
        cache.GetName(999);
        cache.ResolveCallCount.Should().Be(1);
    }

    [Fact]
    public void GetName_ResolverThrowsForOneUidOnly_DoesNotPoisonOtherUids()
    {
        var cache = new MacOSUidNameCache(uid => uid == 501 ? "josh" : null);

        cache.GetName(501).Should().Be("josh");
        cache.GetName(0).Should().Be(string.Empty);
    }
}
