using System.ComponentModel;
using System.Text.Json;
using NexusMonitor.DiskAnalyzer.Models;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NexusMonitor.CLI.Commands.Disk;

/// <summary>Lists stored disk snapshots. With no path, lists all roots, grouped.</summary>
internal sealed class DiskSnapshotsListCommand : Command<DiskSnapshotsListCommand.Settings>
{
    private readonly ISnapshotStore _store;
    public DiskSnapshotsListCommand(ISnapshotStore store) { _store = store; }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[path]")]
        [Description("Root path to filter by. Omit to list snapshots for all roots, grouped by root.")]
        public string? Path { get; init; }

        [CommandOption("--format")]
        [Description("Output format: table or json (default: table)")]
        [DefaultValue("table")]
        public string Format { get; init; } = "table";
    }

    public override int Execute(CommandContext context, Settings s)
    {
        var snapshots = _store.ListSnapshots(s.Path);
        bool json = s.Format.ToLowerInvariant() == "json";

        if (json)
        {
            Console.Write(ToJson(snapshots));
            return 0;
        }

        if (snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No snapshots found.[/]");
            return 0;
        }

        if (s.Path != null)
        {
            AnsiConsole.Write(BuildTable(snapshots));
            return 0;
        }

        // path omitted: all roots, grouped — rows ordered by root then newest-first
        // (ListSnapshots(null) is already globally newest-first, so grouping preserves
        // newest-first order within each group).
        foreach (var group in snapshots.GroupBy(x => x.RootPath).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[bold underline]{Markup.Escape(group.Key)}[/]");
            AnsiConsole.Write(BuildTable(group.ToList()));
            AnsiConsole.WriteLine();
        }
        return 0;
    }

    private static Table BuildTable(IReadOnlyList<SnapshotInfo> snapshots)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Id[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Created[/]"))
            .AddColumn(new TableColumn("[bold]Root[/]"))
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Files[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Threshold[/]").RightAligned());

        foreach (var snap in snapshots)
        {
            table.AddRow(
                snap.Id.ToString(),
                snap.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Markup.Escape(snap.RootPath),
                DiskNode.FormatSize(snap.TotalSize),
                snap.TotalFiles.ToString("N0"),
                DiskNode.FormatSize(snap.ThresholdBytes));
        }
        return table;
    }

    private static string ToJson(IReadOnlyList<SnapshotInfo> snapshots)
    {
        var payload = snapshots.Select(s => new
        {
            id = s.Id,
            rootPath = s.RootPath,
            createdAt = s.CreatedAtUtc.ToString("o"),
            totalSize = s.TotalSize,
            totalFiles = s.TotalFiles,
            thresholdBytes = s.ThresholdBytes,
            fileSystem = s.FileSystem,
            volumeTotal = s.VolumeTotal,
            volumeFree = s.VolumeFree,
        });
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
