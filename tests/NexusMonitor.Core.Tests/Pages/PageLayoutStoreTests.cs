using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public sealed class PageLayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-store-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PageLayout Modified()
    {
        var page = BuiltInPageLayouts.Load("dashboard");
        return PageLayoutEngine.RemoveWidget(page, page.Widgets[0].InstanceId);
    }

    [Fact]
    public void LoadOrDefault_NoFile_ReturnsFactoryDefault()
    {
        using var store = new PageLayoutStore(_dir);
        var page = store.LoadOrDefault("dashboard");
        page.Widgets.Count.Should().Be(BuiltInPageLayouts.Load("dashboard").Widgets.Count);
    }

    [Fact]
    public void SaveThenDispose_RoundTripsThroughDisk()
    {
        var modified = Modified();
        using (var store = new PageLayoutStore(_dir))
        {
            store.Save(modified);
        } // Dispose flushes the debounced write synchronously.

        using var reopened = new PageLayoutStore(_dir);
        reopened.LoadOrDefault("dashboard").Widgets.Count.Should().Be(modified.Widgets.Count);
    }

    [Fact]
    public void LoadOrDefault_CorruptFile_FallsBackToFactory_AndKeepsBak()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "dashboard.json"), "not json {{{");

        using var store = new PageLayoutStore(_dir);
        var page = store.LoadOrDefault("dashboard");

        page.Widgets.Count.Should().Be(BuiltInPageLayouts.Load("dashboard").Widgets.Count);
        File.Exists(Path.Combine(_dir, "dashboard.json.bak")).Should().BeTrue();
    }

    [Fact]
    public void Save_IsDebounced_FileAppearsAfterFlush()
    {
        using var store = new PageLayoutStore(_dir);
        store.Save(Modified());
        // Within the 250ms window the file may not exist yet; after Dispose it must.
        store.Dispose();
        File.Exists(Path.Combine(_dir, "dashboard.json")).Should().BeTrue();
    }
}
