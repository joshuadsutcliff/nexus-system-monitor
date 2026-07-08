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
/// report by running this test and reading its output. Gate-review finding: 2 of these 3
/// spot-checks were silently no-oping (never actually asserting anything) on the verification
/// machine because they only tried a single hardcoded/arbitrary label instead of scanning a
/// candidate list; each spot-check below now explicitly logs "SKIPPED — no live example on this
/// host" when it genuinely finds nothing to check, so a no-op is visible in the test output
/// instead of silently indistinguishable from "checked and passed".
/// </summary>
public class MacOSLaunchdIndexIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MacOSLaunchdIndexIntegrationTests(ITestOutputHelper output) => _output = output;

    // A handful of real macOS system daemons/agents are effectively guaranteed to exist and be
    // resolvable via the filename-fast-path across supported macOS versions. Shared by both the
    // index-level test below and the provider-level spot-check in
    // MacOSServicesProvider_OnRealHost_ProducesHonestBucketedStartTypes so a single candidate list
    // backs both checks (gate-review finding: the provider-level RunAtLoad spot-check used to be a
    // single hardcoded label instead of iterating candidates like this one already did).
    private static readonly string[] s_knownRunAtLoadCandidates = { "com.apple.securityd", "com.apple.cfprefsd.daemon" };

    [Fact]
    public void GetOrBuildIndex_OnRealHost_FindsPlistsAndCompletesQuickly()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Force a genuine cold build for this measurement: a prior run of this test (or the
        // MacOSServicesProvider_OnRealHost_ProducesHonestBucketedStartTypes test, which also builds
        // an index) may have already persisted a disk cache at this path, which would otherwise
        // make the "cold" build below indistinguishable from an already-warm one.
        try { File.Delete(MacOSLaunchdIndex.CachePathForTesting); } catch { }

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
        _output.WriteLine($"launchd plist index warm (in-process cache) rebuild time: {sw2.ElapsedMilliseconds} ms");
        snapshot2.PlistCount.Should().Be(snapshot.PlistCount);
        sw2.ElapsedMilliseconds.Should().BeLessThan(sw.ElapsedMilliseconds / 2,
            "a warm rebuild with no on-disk changes should skip nearly all plutil spawns and be far faster than the cold build");

        // Disk-persisted cache claim (gate-review finding 1): a brand-new MacOSLaunchdIndex
        // instance — simulating a fresh process — should pick up the cache this instance just
        // wrote to disk and need far less time than the cold build above, without needing a real
        // second process.
        var freshProcessIndex = new MacOSLaunchdIndex();
        var sw3 = Stopwatch.StartNew();
        var snapshot3 = freshProcessIndex.GetOrBuildIndex();
        sw3.Stop();
        _output.WriteLine($"launchd plist index warm (disk-cache, fresh-instance) build time: {sw3.ElapsedMilliseconds} ms ({snapshot3.PlistCount} plists)");
        snapshot3.PlistCount.Should().Be(snapshot.PlistCount);
        sw3.ElapsedMilliseconds.Should().BeLessThan(sw.ElapsedMilliseconds / 2,
            "a fresh instance that picks up the on-disk cache should skip nearly all plutil spawns, just like the in-process warm rebuild");

        var resolvedAny = s_knownRunAtLoadCandidates.Any(label => snapshot.TryGetFacts(label, out _));
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

        // Spot-check 1: a well-known RunAtLoad system daemon → Automatic. Gate-review finding:
        // this used to check a single hardcoded label ("com.apple.securityd"), which silently
        // no-oped (never fired) on hosts where that exact label wasn't present under the label
        // launchctl reports it under. Iterate the same candidate list
        // GetOrBuildIndex_OnRealHost_FindsPlistsAndCompletesQuickly uses instead, so the check
        // fires as long as ANY known-RunAtLoad candidate is present.
        var runAtLoadExample = s_knownRunAtLoadCandidates
            .Select(label => services.FirstOrDefault(s => s.Name == label))
            .FirstOrDefault(s => s is not null);
        if (runAtLoadExample is not null)
        {
            _output.WriteLine($"spot-check (RunAtLoad candidate) {runAtLoadExample.Name} => {runAtLoadExample.StartType}");
            runAtLoadExample.StartType.Should().Be(ServiceStartType.Automatic);
        }
        else
        {
            _output.WriteLine("spot-check (RunAtLoad candidate): SKIPPED — no live example on this host");
        }

        // Spot-check 2: something present in `launchctl print-disabled` → Disabled. Gate-review
        // finding: `disabled.FirstOrDefault()` picked an arbitrary (HashSet-ordering-dependent)
        // label from the full disabled set with no guarantee it was even present in the
        // `launchctl list` service snapshot — on this machine that combination never fired.
        // Iterate the FULL disabled set looking for any label that IS present in the service
        // list, instead of grabbing one and hoping.
        var index    = new MacOSLaunchdIndex();
        var disabled = index.GetDisabledLabels();
        var disabledExample = disabled
            .Select(label => services.FirstOrDefault(s => s.Name == label))
            .FirstOrDefault(s => s is not null);
        if (disabledExample is not null)
        {
            _output.WriteLine($"spot-check (disabled candidate, {disabled.Count} disabled labels scanned) {disabledExample.Name} => {disabledExample.StartType}");
            disabledExample.StartType.Should().Be(ServiceStartType.Disabled);
        }
        else
        {
            _output.WriteLine($"spot-check (disabled candidate, {disabled.Count} disabled labels scanned): SKIPPED — no live example on this host");
        }

        // Spot-check 3: a dynamically-submitted job with no on-disk plist → Unknown (honest,
        // not a bug). We can't name one reliably across hosts, so just surface whichever Unknown
        // entries exist for the report rather than asserting a specific label.
        var unknownExample = services.FirstOrDefault(s => s.StartType == ServiceStartType.Unknown);
        if (unknownExample is not null)
            _output.WriteLine($"spot-check (dynamic/no-plist example) {unknownExample.Name} => Unknown");
        else
            _output.WriteLine("spot-check (dynamic/no-plist example): SKIPPED — no live example on this host");
    }
}
