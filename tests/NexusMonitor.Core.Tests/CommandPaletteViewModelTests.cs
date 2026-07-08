using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.ViewModels;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class CommandPaletteItemTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var item = new CommandPaletteItem("Dashboard", "\uF119", "Navigate", () => { });

        item.Label.Should().Be("Dashboard");
        item.Icon.Should().Be("\uF119");
        item.Category.Should().Be("Navigate");
        item.StateLabel.Should().BeNull();
        item.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithStateLabel_SetsStateLabel()
    {
        var item = new CommandPaletteItem("Gaming Mode", "\uF451", "Toggle", () => { }, "ON");
        item.StateLabel.Should().Be("ON");
    }

    [Fact]
    public void Execute_RunsAction()
    {
        bool executed = false;
        var item = new CommandPaletteItem("Test", "", "Navigate", () => executed = true);
        item.Execute();
        executed.Should().BeTrue();
    }

    [Fact]
    public void IsSelected_PropertyChanged_Fires()
    {
        var item = new CommandPaletteItem("Test", "", "Navigate", () => { });
        var fired = false;
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.IsSelected)) fired = true; };

        item.IsSelected = true;

        fired.Should().BeTrue();
        item.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void StateLabel_PropertyChanged_Fires()
    {
        var item = new CommandPaletteItem("Gaming Mode", "", "Toggle", () => { }, "OFF");
        var fired = false;
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.StateLabel)) fired = true; };

        item.StateLabel = "ON";

        fired.Should().BeTrue();
        item.StateLabel.Should().Be("ON");
    }
}

public class CommandPaletteViewModelTests
{
    private static CommandPaletteViewModel CreateVm(int itemCount = 3)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(i => new CommandPaletteItem($"Item {i}", "", "Navigate", () => { }))
            .ToList();
        return new CommandPaletteViewModel(items);
    }

    [Fact]
    public void Open_SetsIsOpenTrue()
    {
        var vm = CreateVm();
        vm.Open();
        vm.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Close_SetsIsOpenFalse()
    {
        var vm = CreateVm();
        vm.Open();
        vm.Close();
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Toggle_WhenClosed_Opens()
    {
        var vm = CreateVm();
        vm.Toggle();
        vm.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Toggle_WhenOpen_Closes()
    {
        var vm = CreateVm();
        vm.Open();
        vm.Toggle();
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Open_ClearsSearchText()
    {
        var vm = CreateVm();
        vm.SearchText = "something";
        vm.Open();
        vm.SearchText.Should().Be(string.Empty);
    }

    [Fact]
    public void Open_PopulatesFilteredItemsWithAllItems()
    {
        var vm = CreateVm(itemCount: 5);
        vm.Open();
        vm.FilteredItems.Should().HaveCount(5);
    }

    [Fact]
    public void Open_ResetsSelectedIndexToZero()
    {
        var vm = CreateVm();
        vm.SelectedIndex = 2;
        vm.Open();
        vm.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Filter_EmptyString_ReturnsAllItems()
    {
        var vm = CreateVm(3);
        vm.Open(); // SearchText is "", all items
        vm.FilteredItems.Should().HaveCount(3);
    }

    [Fact]
    public void Filter_ByLabel_ReturnsMatchingItems()
    {
        var items = new[]
        {
            new CommandPaletteItem("Dashboard", "", "Navigate", () => { }),
            new CommandPaletteItem("Network", "", "Navigate", () => { }),
            new CommandPaletteItem("Processes", "", "Navigate", () => { }),
        };
        var vm = new CommandPaletteViewModel(items);
        vm.Open();

        vm.SearchText = "dash";

        vm.FilteredItems.Should().HaveCount(1);
        vm.FilteredItems[0].Label.Should().Be("Dashboard");
    }

    [Fact]
    public void Filter_ByCategory_ReturnsMatchingItems()
    {
        var items = new[]
        {
            new CommandPaletteItem("Dashboard", "", "Navigate", () => { }),
            new CommandPaletteItem("Gaming Mode", "", "Toggle", () => { }),
        };
        var vm = new CommandPaletteViewModel(items);
        vm.Open();

        vm.SearchText = "toggle";

        vm.FilteredItems.Should().HaveCount(1);
        vm.FilteredItems[0].Label.Should().Be("Gaming Mode");
    }

    [Fact]
    public void Filter_CaseInsensitive()
    {
        var items = new[] { new CommandPaletteItem("Dashboard", "", "Navigate", () => { }) };
        var vm = new CommandPaletteViewModel(items);
        vm.Open();

        vm.SearchText = "DASH";

        vm.FilteredItems.Should().HaveCount(1);
    }

    [Fact]
    public void Filter_NoMatch_ReturnsEmpty()
    {
        var items = new[] { new CommandPaletteItem("Dashboard", "", "Navigate", () => { }) };
        var vm = new CommandPaletteViewModel(items);
        vm.Open();

        vm.SearchText = "xyz_no_match";

        vm.FilteredItems.Should().BeEmpty();
    }

    [Fact]
    public void Filter_ResetsSelectedIndexToZero()
    {
        var vm = CreateVm(3);
        vm.Open();
        vm.SelectedIndex = 2;

        vm.SearchText = "Item";

        vm.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Filter_PartialMatch_MultipleResults()
    {
        var items = new[]
        {
            new CommandPaletteItem("Dashboard", "", "Navigate", () => { }),
            new CommandPaletteItem("Dark Theme", "", "Theme", () => { }),
            new CommandPaletteItem("Network", "", "Navigate", () => { }),
        };
        var vm = new CommandPaletteViewModel(items);
        vm.Open();

        vm.SearchText = "d";

        vm.FilteredItems.Should().HaveCount(2);
    }

    [Fact]
    public void MoveSelection_Down_IncrementsSelectedIndex()
    {
        var vm = CreateVm(3);
        vm.Open(); // SelectedIndex = 0
        vm.MoveSelection(1);
        vm.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void MoveSelection_Up_DecrementsSelectedIndex()
    {
        var vm = CreateVm(3);
        vm.Open();
        vm.SelectedIndex = 2;
        vm.MoveSelection(-1);
        vm.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void MoveSelection_AtBottom_ClampsToLastItem()
    {
        var vm = CreateVm(3);
        vm.Open(); // 3 items, indices 0-2
        vm.SelectedIndex = 2;
        vm.MoveSelection(1); // would be 3, clamped to 2
        vm.SelectedIndex.Should().Be(2);
    }

    [Fact]
    public void MoveSelection_AtTop_ClampsToZero()
    {
        var vm = CreateVm(3);
        vm.Open(); // SelectedIndex = 0
        vm.MoveSelection(-1); // would be -1, clamped to 0
        vm.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void MoveSelection_EmptyList_DoesNotThrow()
    {
        var vm = new CommandPaletteViewModel(Array.Empty<CommandPaletteItem>());
        vm.Open();
        var act = () => vm.MoveSelection(1);
        act.Should().NotThrow();
    }

    [Fact]
    public void ExecuteSelected_RunsActionAndCloses()
    {
        bool executed = false;
        var items = new[] { new CommandPaletteItem("Test", "", "Navigate", () => executed = true) };
        var vm = new CommandPaletteViewModel(items);
        vm.Open();
        vm.ExecuteSelected();
        executed.Should().BeTrue();
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void ExecuteSelected_OutOfRange_DoesNotThrow()
    {
        var vm = new CommandPaletteViewModel(Array.Empty<CommandPaletteItem>());
        vm.Open();
        var act = () => vm.ExecuteSelected();
        act.Should().NotThrow();
    }

    [Fact]
    public void MoveSelection_UpdatesIsSelectedOnItems()
    {
        var vm = CreateVm(3);
        vm.Open(); // item 0 selected
        vm.FilteredItems[0].IsSelected.Should().BeTrue();

        vm.MoveSelection(1); // move to item 1
        vm.FilteredItems[0].IsSelected.Should().BeFalse();
        vm.FilteredItems[1].IsSelected.Should().BeTrue();
    }
}

public class CommandPaletteToggleThemeTests
{
    /// <summary>
    /// Creates a ViewModel with settings wired in plus two toggle items and three theme items.
    /// Returns the VM and the AppSettings so tests can inspect/mutate state directly.
    /// </summary>
    private static (CommandPaletteViewModel vm, AppSettings settings) CreateVmWithToggles(
        string initialTheme = "Dark",
        Action? onSave = null,
        Action<string>? onThemeChanged = null)
    {
        var appSettings = new AppSettings { GamingModeEnabled = false, AutoBalanceEnabled = false, ThemeMode = initialTheme };
        var vm = new CommandPaletteViewModel(
            Array.Empty<CommandPaletteItem>(),
            settings: appSettings,
            onSave: onSave,
            onThemeChanged: onThemeChanged);

        // Build toggle + theme items via the helpers and add them to a new VM that holds them
        var gamingToggle      = vm.MakeToggle("Gaming Mode",   "\uF451", () => appSettings.GamingModeEnabled,  v => appSettings.GamingModeEnabled  = v);
        var autoBalanceToggle = vm.MakeToggle("Auto-Balance",  "\uF1C0", () => appSettings.AutoBalanceEnabled,  v => appSettings.AutoBalanceEnabled   = v);
        var darkTheme         = vm.MakeTheme("Dark Theme",     "\uF468", "Dark");
        var lightTheme        = vm.MakeTheme("Light Theme",    "\uF07C", "Light");
        var systemTheme       = vm.MakeTheme("System Theme",   "\uF108", "System");

        var items = new CommandPaletteItem[] { gamingToggle, autoBalanceToggle, darkTheme, lightTheme, systemTheme };
        var finalVm = new CommandPaletteViewModel(items, appSettings, onSave, onThemeChanged);
        return (finalVm, appSettings);
    }

    [Fact]
    public void ToggleItems_HaveStateBadges_ON_or_OFF()
    {
        var (vm, settings) = CreateVmWithToggles();
        vm.Open();

        // GamingMode and AutoBalance both start false → "OFF"
        var gamingItem = vm.FilteredItems.First(i => i.Label == "Gaming Mode");
        var autoBalanceItem = vm.FilteredItems.First(i => i.Label == "Auto-Balance");

        gamingItem.StateLabel.Should().Be("OFF");
        autoBalanceItem.StateLabel.Should().Be("OFF");

        // Flip gaming mode on directly, re-open to refresh
        settings.GamingModeEnabled = true;
        vm.Open();

        gamingItem = vm.FilteredItems.First(i => i.Label == "Gaming Mode");
        gamingItem.StateLabel.Should().Be("ON");
    }

    [Fact]
    public void Open_RefreshesToggleStates()
    {
        var (vm, settings) = CreateVmWithToggles();
        vm.Open();

        // Externally flip AutoBalance
        settings.AutoBalanceEnabled = true;

        // Re-open — RefreshToggleStates should pick up the change
        vm.Open();

        var autoBalanceItem = vm.FilteredItems.First(i => i.Label == "Auto-Balance");
        autoBalanceItem.StateLabel.Should().Be("ON");
    }

    [Fact]
    public void ThemeItems_ShowACTIVE_OnCurrentTheme()
    {
        // Initial theme = "Dark"
        var (vm, _) = CreateVmWithToggles(initialTheme: "Dark");
        vm.Open();

        var darkItem   = vm.FilteredItems.First(i => i.Label == "Dark Theme");
        var lightItem  = vm.FilteredItems.First(i => i.Label == "Light Theme");
        var systemItem = vm.FilteredItems.First(i => i.Label == "System Theme");

        darkItem.StateLabel.Should().Be("ACTIVE");
        lightItem.StateLabel.Should().BeNull();
        systemItem.StateLabel.Should().BeNull();
    }

    [Fact]
    public void ToggleExecute_FlipsSettingAndUpdatesState()
    {
        bool saveCalled = false;
        var (vm, settings) = CreateVmWithToggles(onSave: () => saveCalled = true);
        vm.Open();

        // Gaming Mode starts OFF; find and execute it
        var gamingItem = vm.FilteredItems.First(i => i.Label == "Gaming Mode");
        gamingItem.Execute();

        // Setting should be flipped
        settings.GamingModeEnabled.Should().BeTrue();
        saveCalled.Should().BeTrue();

        // Re-open to refresh badges
        vm.Open();
        var refreshedItem = vm.FilteredItems.First(i => i.Label == "Gaming Mode");
        refreshedItem.StateLabel.Should().Be("ON");
    }

    [Fact]
    public void ThemeExecute_CallsOnThemeChangedWithCorrectMode()
    {
        string? themeChangedValue = null;
        var (vm, _) = CreateVmWithToggles(onThemeChanged: mode => themeChangedValue = mode);
        vm.Open();

        var darkItem = vm.FilteredItems.First(i => i.Label == "Dark Theme");
        darkItem.Execute();
        themeChangedValue.Should().Be("Dark");

        themeChangedValue = null;
        var lightItem = vm.FilteredItems.First(i => i.Label == "Light Theme");
        lightItem.Execute();
        themeChangedValue.Should().Be("Light");

        themeChangedValue = null;
        var systemItem = vm.FilteredItems.First(i => i.Label == "System Theme");
        systemItem.Execute();
        themeChangedValue.Should().Be("System");
    }
}
