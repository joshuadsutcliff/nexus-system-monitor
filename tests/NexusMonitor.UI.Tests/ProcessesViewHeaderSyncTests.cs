using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Mock;
using NexusMonitor.Core.Services;
using NexusMonitor.UI.ViewModels;
using NexusMonitor.UI.Views;
using Xunit;

namespace NexusMonitor.UI.Tests;

/// <summary>
/// Guards the three-way header sync the Processes-grid column customization feature depends on:
/// <see cref="ProcessesViewModel"/>'s hideable-column headers (exercised via
/// <see cref="ProcessesViewModel.ColumnOptions"/>), <see cref="ProcessesView"/>'s code-behind
/// <c>_headerByColumnKey</c> lookup table, and the actual <c>Header="..."</c> literals in
/// ProcessesView.axaml. <see cref="ProcessesViewModelColumnOptionsTests.ColumnOptions_Headers_MatchDataGridColumnTextExactly"/>
/// already guards VM-vs-expected-literal; this file adds the two legs that test didn't cover:
/// code-behind-vs-VM (<see cref="HeaderByColumnKey_MatchesViewModel_KeysAndHeadersExactly"/>) and
/// code-behind-vs-XAML (<see cref="HeaderByColumnKey_EveryHeaderExistsAsADataGridColumnHeaderInXaml"/>).
///
/// Why this matters: ProcessesView.axaml.cs resolves each hideable DataGridColumn at runtime by
/// matching its static Header text against _headerByColumnKey (DataGridColumn is never in the
/// visual tree, so it can't be looked up by x:Name or bound directly — see that file's header
/// comment). If a column's Header="..." literal is ever renamed in the XAML alone (a copy edit,
/// nothing more), that lookup silently stops resolving the affected column — no compiler error,
/// since Header is just a string. Before this file existed that drift only surfaced as an
/// InvalidOperationException crash in OnLoaded the next time the Processes tab loaded (the
/// lookup used `.First(...)`, since fixed to `.FirstOrDefault(...)` + skip + log — see
/// ProcessesView.axaml.cs's BuildHideableColumnsByKey). These tests catch the drift at build
/// time instead, in either direction.
/// </summary>
public class ProcessesViewHeaderSyncTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _settingsPath;

    public ProcessesViewHeaderSyncTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "NexusMonitorUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _settingsPath = Path.Combine(_testDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
    }

    private static ProcessesViewModel CreateVm(SettingsService settingsService) =>
        new(new MockProcessProvider(), settingsService.Current, settingsService: settingsService);

    /// <summary>
    /// Resolves ProcessesView.axaml next to this test's own source file (via
    /// <see cref="CallerFilePath"/>) rather than via the test assembly's bin/ output directory —
    /// that output layout differs per-OS TFM (net8.0 vs net8.0-windows10.0.17763.0) and the XAML
    /// file isn't copied into it anyway (it's compiled into the UI assembly as a resource, not
    /// readable back out as source text). Walking up from the .cs file to the repo root and back
    /// down to src/ is stable across all three CI OSes and a local worktree checkout alike.
    /// </summary>
    private static string GetProcessesViewAxamlPath([CallerFilePath] string thisFilePath = "")
    {
        var testsDir = Path.GetDirectoryName(thisFilePath)!; // .../tests/NexusMonitor.UI.Tests
        var repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
        return Path.Combine(repoRoot, "src", "NexusMonitor.UI", "Views", "ProcessesView.axaml");
    }

    [Fact]
    public void HeaderByColumnKey_MatchesViewModel_KeysAndHeadersExactly()
    {
        using var settings = CreateSettingsService();
        using var vm = CreateVm(settings);

        var vmHeadersByKey = vm.ColumnOptions.ToDictionary(o => o.Key, o => o.Header);

        // Same key set...
        vmHeadersByKey.Keys.Should().BeEquivalentTo(ProcessesView._headerByColumnKey.Keys);

        // ...and the same header text for every key — a rename on either side alone fails this.
        foreach (var (key, header) in ProcessesView._headerByColumnKey)
            vmHeadersByKey[key].Should().Be(header,
                $"ProcessesViewModel.ColumnOptions and ProcessesView._headerByColumnKey must agree on the '{key}' header");
    }

    [Fact]
    public void HeaderByColumnKey_EveryHeaderExistsAsADataGridColumnHeaderInXaml()
    {
        var xaml = File.ReadAllText(GetProcessesViewAxamlPath());

        foreach (var (key, header) in ProcessesView._headerByColumnKey)
        {
            // Mirrors the runtime lookup in ProcessesView.BuildHideableColumnsByKey():
            // a DataGridTextColumn/DataGridTemplateColumn element whose Header attribute equals
            // this exact string. Header is always the element's first attribute in this file
            // (confirmed by inspection), so an anchored match is safe and doesn't need to parse
            // the whole XML tree.
            var pattern = $@"<DataGrid(?:Text|Template)Column\s+Header=""{Regex.Escape(header)}""";
            Regex.IsMatch(xaml, pattern).Should().BeTrue(
                $"ProcessesView.axaml should declare a DataGridColumn with Header=\"{header}\" " +
                $"for key \"{key}\" (see ProcessesView._headerByColumnKey) — if this fails, the " +
                "XAML Header literal drifted from the code-behind dictionary and the column's " +
                "show/hide toggle would silently no-op at runtime");
        }
    }

    private SettingsService CreateSettingsService() =>
        new(NullLogger<SettingsService>.Instance, _settingsPath);
}
