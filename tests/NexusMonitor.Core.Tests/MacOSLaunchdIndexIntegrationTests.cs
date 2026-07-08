using System.Diagnostics;
using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Platform.MacOS;
using Xunit;
using Xunit.Abstractions;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Live integration coverage for the Sym-1 Task 3 macOS launchd StartType work. These exercise
/// the real provider path against whatever launchd state exists on the host — genuinely runs on
/// macOS CI runners (the workflow matrix includes macos-latest) and on a macOS dev machine; a
/// deliberate no-op everywhere else so it never fails CI on Windows/Linux runners.
///
/// Per the brief: reports index build time, per-bucket StartType counts, and (via the
/// <see cref="ITestOutputHelper"/> writes) 3 spot-checked services — captured into the Task 3
/// report by running this test and reading its output.
/// </summary>
public class MacOSLaunchdIndexIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MacOSLaunchdIndexIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GetOrBuildIndex_OnRealHost_FindsPlistsAndCompletesQuickly()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var index = new MacOSLaunchdIndex();
        var sw    = Stopwatch.StartNew();
        var snapshot = index.GetOrBuildIndex();
        sw.Stop();

        _output.WriteLine($"launchd plist index cold build time: {sw.ElapsedMilliseconds} ms (parallelized plutil spawns, {snapshot.PlistCount} plists)");
        snapshot.PlistCount.Should().BeGreaterThan(0);

        // Warm-cache claim: a second call with nothing changed on disk must not re-spawn plutil
        // for any file — only cheap directory enumeration + mtime comparisons. This is the crux
        // of the "cache the label→StartType index" performance requirement in the brief.
        var sw2 = Stopwatch.StartNew();
        var snapshot2 = index.GetOrBuildIndex();
        sw2.Stop();
        _output.WriteLine($"launchd plist index warm (cached) rebuild time: {sw2.ElapsedMilliseconds} ms");
        snapshot2.PlistCount.Should().Be(snapshot.PlistCount);
        sw2.ElapsedMilliseconds.Should().BeLessThan(sw.ElapsedMilliseconds / 2,
            "a warm rebuild with no on-disk changes should skip nearly all plutil spawns and be far faster than the cold build");

        // A handful of real macOS system daemons/agents are effectively guaranteed to exist and
        // be resolvable via the filename-fast-path across supported macOS versions.
        var knownRunAtLoadCandidates = new[] { "com.apple.securityd", "com.apple.cfprefsd.daemon" };
        var resolvedAny = knownRunAtLoadCandidates.Any(label => snapshot.TryGetFacts(label, out _));
        resolvedAny.Should().BeTrue("at least one well-known system daemon plist should be indexed");
    }

    [Fact]
    public void GetDisabledLabels_OnRealHost_ReturnsWithoutThrowing()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var index    = new MacOSLaunchdIndex();
        var disabled = index.GetDisabledLabels();

        _output.WriteLine($"disabled-set size: {disabled.Count}");
        disabled.Should().NotBeNull();
    }

    [Fact]
    public async Task MacOSServicesProvider_OnRealHost_ProducesHonestBucketedStartTypes()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var provider = new MacOSServicesProvider();
        var sw       = Stopwatch.StartNew();
        var services = await provider.GetServicesAsync();
        sw.Stop();

        _output.WriteLine($"GetServicesAsync total time (includes index build): {sw.ElapsedMilliseconds} ms");
        services.Should().NotBeEmpty();

        var buckets = services
            .GroupBy(s => s.StartType)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var startType in Enum.GetValues<ServiceStartType>())
            buckets.TryAdd(startType, 0);

        foreach (var (startType, count) in buckets)
            _output.WriteLine($"StartType.{startType}: {count}");

        // Honest-UI floor: we must not report every single service as Unknown — that would mean
        // the plist index silently failed to resolve anything, indistinguishable from the old
        // hardcoded-Unknown behavior this task replaces.
        buckets[ServiceStartType.Unknown].Should().BeLessThan(services.Count,
            "at least some services should resolve to a real StartType, not just Unknown");

        // Spot-check 1: a well-known RunAtLoad system daemon → Automatic.
        var securityd = services.FirstOrDefault(s => s.Name == "com.apple.securityd");
        if (securityd is not null)
        {
            _output.WriteLine($"spot-check com.apple.securityd => {securityd.StartType}");
            securityd.StartType.Should().Be(ServiceStartType.Automatic);
        }

        // Spot-check 2: something present in `launchctl print-disabled` → Disabled.
        var index    = new MacOSLaunchdIndex();
        var disabled = index.GetDisabledLabels();
        var disabledLabel = disabled.FirstOrDefault();
        if (disabledLabel is not null)
        {
            var svc = services.FirstOrDefault(s => s.Name == disabledLabel);
            if (svc is not null)
            {
                _output.WriteLine($"spot-check {disabledLabel} (in print-disabled) => {svc.StartType}");
                svc.StartType.Should().Be(ServiceStartType.Disabled);
            }
        }

        // Spot-check 3: a dynamically-submitted job with no on-disk plist → Unknown (honest,
        // not a bug). We can't name one reliably across hosts, so just surface whichever Unknown
        // entries exist for the report rather than asserting a specific label.
        var unknownExample = services.FirstOrDefault(s => s.StartType == ServiceStartType.Unknown);
        if (unknownExample is not null)
            _output.WriteLine($"spot-check (dynamic/no-plist example) {unknownExample.Name} => Unknown");
    }
}
