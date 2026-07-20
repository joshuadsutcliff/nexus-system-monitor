using NexusMonitor.DiskAnalyzer.Scanning;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

/// <summary>
/// Pins the MFT path-resolution contract: a scan target must fully resolve through the
/// parent/child index or resolution FAILS — it must never fall back to the deepest
/// ancestor that did resolve. (The pre-fix walk broke out of the loop on a missing
/// component and scanned that ancestor — worst case the volume root — while stamping
/// the requested path on the result: silently wrong totals.)
/// The raw-MFT read itself needs admin + NTFS and is covered by manual live passes.
/// </summary>
public class MftScannerPathResolutionTests
{
    private const ulong Root = 5;

    private static (Dictionary<ulong, MftScanner.MftEntry> frnMap, Dictionary<ulong, List<ulong>> childrenOf)
        Index(params MftScanner.MftEntry[] entries)
    {
        var frnMap = new Dictionary<ulong, MftScanner.MftEntry>();
        var childrenOf = new Dictionary<ulong, List<ulong>>();
        foreach (var e in entries)
        {
            frnMap[e.Frn] = e;
            if (!childrenOf.TryGetValue(e.ParentFrn, out var list))
                childrenOf[e.ParentFrn] = list = new List<ulong>();
            list.Add(e.Frn);
        }
        return (frnMap, childrenOf);
    }

    private static MftScanner.MftEntry Dir(ulong frn, ulong parent, string name) =>
        new(frn, parent, name, 0, 0, default, IsDirectory: true, IsSystem: false, IsHidden: false);

    private static MftScanner.MftEntry File(ulong frn, ulong parent, string name, long size = 1) =>
        new(frn, parent, name, size, size, default, IsDirectory: false, IsSystem: false, IsHidden: false);

    [Fact]
    public void FullyResolvablePath_ReturnsTrue_WithDeepestFrn()
    {
        var (frnMap, childrenOf) = Index(
            Dir(100, Root, "Temp"),
            Dir(200, 100, "nexus-lp"),
            File(300, 200, "big1.bin"));

        var ok = MftScanner.TryResolvePathFrn(frnMap, childrenOf, new[] { "Temp", "nexus-lp" }, out var frn);

        Assert.True(ok);
        Assert.Equal(200ul, frn);
    }

    [Fact]
    public void MissingLeafComponent_Fails_AndDoesNotReturnAncestorFrn()
    {
        // "Temp" resolves; "nexus-lp" does not exist in the index (e.g. a directory
        // created after the on-disk MFT state was captured). The pre-fix behavior
        // returned FRN 100 (the ancestor) here — the exact silent-volume-scan bug.
        var (frnMap, childrenOf) = Index(
            Dir(100, Root, "Temp"),
            Dir(110, 100, "other"));

        var ok = MftScanner.TryResolvePathFrn(frnMap, childrenOf, new[] { "Temp", "nexus-lp" }, out var frn);

        Assert.False(ok);
        Assert.NotEqual(100ul, frn);
        Assert.Equal(Root, frn); // reset, not left at the partial ancestor
    }

    [Fact]
    public void MissingMiddleComponent_Fails()
    {
        var (frnMap, childrenOf) = Index(
            Dir(100, Root, "Temp"));

        var ok = MftScanner.TryResolvePathFrn(frnMap, childrenOf, new[] { "Temp", "missing", "deeper" }, out _);

        Assert.False(ok);
    }

    [Fact]
    public void ComponentMatchingAFileNotADirectory_Fails()
    {
        // A FILE named like the component must not satisfy directory resolution.
        var (frnMap, childrenOf) = Index(
            Dir(100, Root, "Temp"),
            File(200, 100, "nexus-lp"));

        var ok = MftScanner.TryResolvePathFrn(frnMap, childrenOf, new[] { "Temp", "nexus-lp" }, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Resolution_IsCaseInsensitive()
    {
        var (frnMap, childrenOf) = Index(
            Dir(100, Root, "Temp"),
            Dir(200, 100, "Nexus-LP"));

        var ok = MftScanner.TryResolvePathFrn(frnMap, childrenOf, new[] { "temp", "NEXUS-lp" }, out var frn);

        Assert.True(ok);
        Assert.Equal(200ul, frn);
    }

    [Fact]
    public void EmptyParts_ResolvesToVolumeRoot()
    {
        var (frnMap, childrenOf) = Index(Dir(100, Root, "Temp"));

        var ok = MftScanner.TryResolvePathFrn(frnMap, childrenOf, Array.Empty<string>(), out var frn);

        Assert.True(ok);
        Assert.Equal(Root, frn);
    }
}
