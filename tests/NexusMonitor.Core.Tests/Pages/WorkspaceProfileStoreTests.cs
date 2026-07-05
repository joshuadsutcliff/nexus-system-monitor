using FluentAssertions;
using NexusMonitor.Core.Pages;
using Xunit;

namespace NexusMonitor.Core.Tests.Pages;

public sealed class WorkspaceProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-profile-store-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _legacyDir = Path.Combine(Path.GetTempPath(), "nexus-profile-store-legacy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        if (Directory.Exists(_legacyDir)) Directory.Delete(_legacyDir, recursive: true);
    }

    private static WorkspaceProfile SampleProfile(string name = "Test Profile")
    {
        var dashboard = BuiltInPageLayouts.Load("dashboard");
        var pages = new Dictionary<string, PageLayout> { [dashboard.PageId] = dashboard };
        return new WorkspaceProfile(name, pages, new ThemeRef(Snapshot: SampleSnapshot()), Array.Empty<PopOutState>());
    }

    private static ThemeSnapshot SampleSnapshot() => new(
        ThemeMode: "Dark",
        AccentColorHex: "#FF00FF",
        TextAccentColorHex: "#00FFFF",
        CustomWindowBgHex: "#111111",
        CustomSurfaceBgHex: "#222222",
        CustomSidebarBgHex: "#333333",
        IsGlassEnabled: true,
        GlassOpacity: 0.65,
        BackdropBlurMode: "Acrylic",
        IsSpecularEnabled: true,
        SpecularIntensity: 0.3,
        FontFamily: "Segoe UI",
        FontSizeMultiplier: 1.1,
        SmartTintEnabled: true);

    [Fact]
    public void SaveThenDispose_RoundTripsThroughDisk()
    {
        var profile = SampleProfile();
        using (var store = new WorkspaceProfileStore(_dir))
        {
            store.Save(profile);
        } // Dispose flushes the debounced write synchronously.

        using var reopened = new WorkspaceProfileStore(_dir);
        var loaded = reopened.Load(profile.Name);

        loaded.Should().NotBeNull();
        WorkspaceProfileComparer.Instance.Equals(profile, loaded).Should().BeTrue();
    }

    [Fact]
    public void SetActive_PersistsAcrossInstances()
    {
        using (var store = new WorkspaceProfileStore(_dir))
        {
            store.SetActive("Work");
        }

        using var reopened = new WorkspaceProfileStore(_dir);
        reopened.ActiveProfileName.Should().Be("Work");
    }

    [Fact]
    public void ActiveProfileName_NoPointerFile_FallsBackToDefault()
    {
        using var store = new WorkspaceProfileStore(_dir);
        store.ActiveProfileName.Should().Be("Default");
    }

    [Fact]
    public void LoadActive_NoSavedProfiles_ReturnsFactoryDefault()
    {
        using var store = new WorkspaceProfileStore(_dir);
        var profile = store.LoadActive();

        profile.Name.Should().Be("Default");
        profile.Pages.Keys.Should().BeEquivalentTo(BuiltInPageLayouts.BuiltInPageIds);
        profile.Theme.PresetId.Should().BeNull();
        profile.Theme.Snapshot.Should().BeNull();
        profile.PopOutStates.Should().BeEmpty();
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull_AndKeepsBak()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Default.json"), "not json {{{");

        using var store = new WorkspaceProfileStore(_dir);
        var profile = store.Load("Default");

        profile.Should().BeNull();
        File.Exists(Path.Combine(_dir, "Default.json.bak")).Should().BeTrue();
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        using var store = new WorkspaceProfileStore(_dir);
        store.Load("NoSuchProfile").Should().BeNull();
    }

    [Fact]
    public void Delete_ActiveProfile_Throws()
    {
        using var store = new WorkspaceProfileStore(_dir);
        store.SetActive("Test Profile");

        var act = () => store.Delete("Test Profile");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Delete_NonActiveProfile_RemovesFile()
    {
        using var store = new WorkspaceProfileStore(_dir);
        store.Save(SampleProfile("Other"));
        store.Dispose();

        using var reopened = new WorkspaceProfileStore(_dir);
        reopened.Delete("Other");

        File.Exists(Path.Combine(_dir, "Other.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("bad:name")]
    [InlineData("")]
    public void Save_InvalidName_ThrowsArgumentException(string invalidName)
    {
        using var store = new WorkspaceProfileStore(_dir);
        var profile = SampleProfile(invalidName);

        var act = () => store.Save(profile);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("")]
    public void SetActive_InvalidName_ThrowsArgumentException(string invalidName)
    {
        using var store = new WorkspaceProfileStore(_dir);
        var act = () => store.SetActive(invalidName);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("")]
    public void Delete_InvalidName_ThrowsArgumentException(string invalidName)
    {
        using var store = new WorkspaceProfileStore(_dir);
        var act = () => store.Delete(invalidName);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MigrateLegacyIfNeeded_NoProfilesButLegacyDashboardExists_MigratesAndActivates()
    {
        Directory.CreateDirectory(_legacyDir);
        var legacyPage = BuiltInPageLayouts.Load("dashboard") with { Title = "Legacy Dashboard" };
        File.WriteAllText(Path.Combine(_legacyDir, "dashboard.json"), PageLayoutSerializer.Serialize(legacyPage));

        using var store = new WorkspaceProfileStore(_dir, _legacyDir);
        var migrated = store.MigrateLegacyIfNeeded();

        migrated.Should().BeTrue();
        File.Exists(Path.Combine(_legacyDir, "dashboard.json.migrated")).Should().BeTrue();
        File.Exists(Path.Combine(_legacyDir, "dashboard.json")).Should().BeFalse();
        store.ActiveProfileName.Should().Be("Default");

        var defaultProfile = store.Load("Default");
        defaultProfile.Should().NotBeNull();
        defaultProfile!.Pages.Should().ContainKey("dashboard");
        PageLayoutComparer.Instance.Equals(defaultProfile.Pages["dashboard"], legacyPage).Should().BeTrue();
        defaultProfile.Theme.PresetId.Should().BeNull();
        defaultProfile.Theme.Snapshot.Should().BeNull();
    }

    [Fact]
    public void MigrateLegacyIfNeeded_ProfilesAlreadyExist_DoesNothing()
    {
        Directory.CreateDirectory(_legacyDir);
        File.WriteAllText(Path.Combine(_legacyDir, "dashboard.json"),
            PageLayoutSerializer.Serialize(BuiltInPageLayouts.Load("dashboard")));

        var store = new WorkspaceProfileStore(_dir, _legacyDir);
        store.Save(SampleProfile());
        store.Dispose();

        using var reopened = new WorkspaceProfileStore(_dir, _legacyDir);
        var migrated = reopened.MigrateLegacyIfNeeded();

        migrated.Should().BeFalse();
        File.Exists(Path.Combine(_legacyDir, "dashboard.json")).Should().BeTrue();
        File.Exists(Path.Combine(_legacyDir, "dashboard.json.migrated")).Should().BeFalse();
    }

    [Fact]
    public void MigrateLegacyIfNeeded_NoLegacyFile_ReturnsFalse()
    {
        using var store = new WorkspaceProfileStore(_dir, _legacyDir);
        store.MigrateLegacyIfNeeded().Should().BeFalse();
    }

    [Fact]
    public void ListProfiles_ReturnsSortedJsonStems()
    {
        var store = new WorkspaceProfileStore(_dir);
        store.Save(SampleProfile("Zeta"));
        store.Dispose();

        using var reopened = new WorkspaceProfileStore(_dir);
        reopened.Save(SampleProfile("Alpha"));
        reopened.Dispose();

        using var third = new WorkspaceProfileStore(_dir);
        third.ListProfiles().Should().Equal("Alpha", "Zeta");
    }

    [Fact]
    public void Load_TraversalName_ReturnsNull_AndTouchesNothing()
    {
        // The store's directory is a subdirectory of _dir; "../decoy" resolves to a file
        // living directly under _dir — a stand-in for anything outside the profiles dir
        // that an attacker-controlled name could otherwise reach.
        var storeDir = Path.Combine(_dir, "store");
        Directory.CreateDirectory(storeDir);
        var decoyPath = Path.Combine(_dir, "decoy.json");
        File.WriteAllText(decoyPath, "not json {{{");

        using var store = new WorkspaceProfileStore(storeDir);
        var profile = store.Load("../decoy");

        profile.Should().BeNull();
        // The real proof: pre-fix, Load's corrupt-file branch renames whatever the
        // traversal resolved to onto ".bak" — an attacker-chosen rename outside the
        // store directory. Post-fix, an invalid name must never touch the file system.
        File.Exists(decoyPath).Should().BeTrue();
        File.Exists(decoyPath + ".bak").Should().BeFalse();
    }

    [Fact]
    public void ActiveProfileName_TamperedPointer_FallsBackToDefault()
    {
        var storeDir = Path.Combine(_dir, "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "active-profile"), "../evil");

        // Stand-in for a file living outside the store dir that a traversal name inside
        // the pointer could otherwise reach via Load.
        var decoyPath = Path.Combine(_dir, "evil.json");
        File.WriteAllText(decoyPath, "not json {{{");

        using var store = new WorkspaceProfileStore(storeDir);

        store.ActiveProfileName.Should().Be("Default");

        var loaded = store.LoadActive();
        loaded.Name.Should().Be("Default");
        loaded.Pages.Keys.Should().BeEquivalentTo(BuiltInPageLayouts.BuiltInPageIds);
        loaded.Theme.PresetId.Should().BeNull();
        loaded.Theme.Snapshot.Should().BeNull();

        File.Exists(decoyPath).Should().BeTrue();
        File.Exists(decoyPath + ".bak").Should().BeFalse();
    }

    [Fact]
    public void MigrateLegacy_CorruptLegacyJson_ReturnsFalse_LeavesFileUntouched()
    {
        Directory.CreateDirectory(_legacyDir);
        var legacyPath = Path.Combine(_legacyDir, "dashboard.json");
        File.WriteAllText(legacyPath, "not json {{{");

        using var store = new WorkspaceProfileStore(_dir, _legacyDir);
        var migrated = store.MigrateLegacyIfNeeded();

        migrated.Should().BeFalse();
        File.Exists(legacyPath).Should().BeTrue();
        File.Exists(legacyPath + ".migrated").Should().BeFalse();
        store.ListProfiles().Should().BeEmpty();
    }
}
