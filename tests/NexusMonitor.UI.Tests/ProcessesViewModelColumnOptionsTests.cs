using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Mock;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using NexusMonitor.UI.ViewModels;
using Xunit;

namespace NexusMonitor.UI.Tests;

/// <summary>
/// Tests for the Processes-grid column show/hide customization (v1, non-visual half — see
/// ProcessesViewModel.ColumnOptions / ResetColumnsCommand and AppSettings.ProcessColumnsHidden).
/// Task 2 (not covered here) wires the XAML header context menu + per-column IsVisible bindings.
///
/// Uses the real MockProcessProvider (parameterless, no Moq dependency in this test project —
/// see DiskAnalyzerViewModelRunDiffTests's header comment for why no Avalonia bootstrap is
/// needed: column-option logic never touches Dispatcher.UIThread or Application.Current) and a
/// real SettingsService bound to a throwaway temp path per test instance, mirroring
/// SettingsServiceTests's INVARIANT — never touch the real per-user settings file.
/// </summary>
public class ProcessesViewModelColumnOptionsTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _settingsPath;

    public ProcessesViewModelColumnOptionsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "NexusMonitorUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _settingsPath = Path.Combine(_testDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
    }

    private SettingsService CreateSettingsService() =>
        new(NullLogger<SettingsService>.Instance, _settingsPath);

    /// <summary>
    /// Builds a ProcessesViewModel wired the same way production DI wires it (see
    /// NexusServiceCollectionExtensions: AppSettings is registered as
    /// `sp.GetRequiredService&lt;SettingsService&gt;().Current` — the SAME object, not a copy) so
    /// mutating _appSettings inside the VM and calling _settingsService.Save() actually
    /// round-trips through the SAME SettingsService the test observes.
    /// </summary>
    private static ProcessesViewModel CreateVm(SettingsService settingsService) =>
        new(new MockProcessProvider(), settingsService.Current, settingsService: settingsService);

    private static readonly string[] ExpectedKeysInOrder =
        ["pid", "cpu", "memory", "leak", "io", "impact", "rules", "group", "priority", "threads", "handles", "user"];

    /// <summary>Polls the settings file for up to ~2s (50 ms steps) until <paramref name="ready"/>
    /// is satisfied, mirroring SettingsServiceTests's debounced-write polling idiom (a fixed
    /// sleep would either be flaky under load or needlessly slow) — proves Save() actually armed
    /// and fired the 250 ms debounce timer, not just that AppSettings.Current was mutated
    /// in-memory (Current is a plain field; nothing but a real Save() ever touches disk).</summary>
    private string WaitForSettingsFile(Func<string, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_settingsPath))
            {
                var text = File.ReadAllText(_settingsPath);
                if (ready(text)) return text;
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException($"Settings file at {_settingsPath} never reached the expected state.");
    }

    [Fact]
    public void ColumnOptions_Defaults_AllTwelveVisibleInDataGridOrder()
    {
        using var settings = CreateSettingsService();
        using var vm = CreateVm(settings);

        vm.ColumnOptions.Select(o => o.Key).Should().Equal(ExpectedKeysInOrder);
        vm.ColumnOptions.Should().OnlyContain(o => o.IsVisible);
    }

    [Fact]
    public void ColumnOptions_Headers_MatchDataGridColumnTextExactly()
    {
        using var settings = CreateSettingsService();
        using var vm = CreateVm(settings);

        var headersByKey = vm.ColumnOptions.ToDictionary(o => o.Key, o => o.Header);
        headersByKey["pid"].Should().Be("PID");
        headersByKey["cpu"].Should().Be("CPU");
        headersByKey["memory"].Should().Be("Memory");
        headersByKey["leak"].Should().Be("Leak");
        headersByKey["io"].Should().Be("I/O");
        headersByKey["impact"].Should().Be("Impact");
        headersByKey["rules"].Should().Be("Rules");
        headersByKey["group"].Should().Be("Group");
        headersByKey["priority"].Should().Be("Priority");
        headersByKey["threads"].Should().Be("Threads");
        headersByKey["handles"].Should().Be("Handles");
        headersByKey["user"].Should().Be("User");
    }

    [Fact]
    public void ToggleOff_KeyLandsInHiddenListAndIsPersisted()
    {
        using var settings = CreateSettingsService();
        using var vm = CreateVm(settings);

        var pid = vm.ColumnOptions.Single(o => o.Key == "pid");
        pid.IsVisible = false;

        settings.Current.ProcessColumnsHidden.Should().ContainSingle().Which.Should().Be("pid");

        // Discriminating: only a real _settingsService.Save() call ever writes this file —
        // proves the toggle handler actually persisted, not merely mutated Current in memory.
        var written = WaitForSettingsFile(text => text.Contains("\"pid\""));
        written.Should().Contain("ProcessColumnsHidden");
    }

    [Fact]
    public void ToggleOff_ThenToggleOn_KeyIsRemovedFromHiddenList()
    {
        using var settings = CreateSettingsService();
        using var vm = CreateVm(settings);

        var cpu = vm.ColumnOptions.Single(o => o.Key == "cpu");
        cpu.IsVisible = false;
        settings.Current.ProcessColumnsHidden.Should().Contain("cpu");

        cpu.IsVisible = true;
        settings.Current.ProcessColumnsHidden.Should().NotContain("cpu");
    }

    [Fact]
    public void ResetColumns_WithThreeHidden_AllVisibleListEmptyAndPersisted()
    {
        using var settings = CreateSettingsService();
        using var vm = CreateVm(settings);

        foreach (var key in new[] { "pid", "cpu", "memory" })
            vm.ColumnOptions.Single(o => o.Key == key).IsVisible = false;
        settings.Current.ProcessColumnsHidden.Should().HaveCount(3);

        vm.ResetColumnsCommand.Execute(null);

        vm.ColumnOptions.Should().OnlyContain(o => o.IsVisible);
        settings.Current.ProcessColumnsHidden.Should().BeEmpty();

        var written = WaitForSettingsFile(text => text.Contains("\"ProcessColumnsHidden\": []"));
        written.Should().NotBeNull();
    }

    [Fact]
    public void UnknownHiddenKey_IsIgnored_NoThrowAndAllColumnsVisible()
    {
        using var settings = CreateSettingsService();
        settings.Current.ProcessColumnsHidden.Add("not-a-real-column");

        var act = () =>
        {
            using var vm = CreateVm(settings);
            vm.ColumnOptions.Should().OnlyContain(o => o.IsVisible);
        };

        act.Should().NotThrow();
        // The unrecognized entry is left untouched — nothing in ColumnOptions matches it, so
        // there's nothing to clean it up on construction.
        settings.Current.ProcessColumnsHidden.Should().ContainSingle().Which.Should().Be("not-a-real-column");
    }
}
