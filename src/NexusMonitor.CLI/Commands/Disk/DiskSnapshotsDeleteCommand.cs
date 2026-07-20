using System.ComponentModel;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NexusMonitor.CLI.Commands.Disk;

/// <summary>Deletes a stored disk snapshot by id.</summary>
internal sealed class DiskSnapshotsDeleteCommand : Command<DiskSnapshotsDeleteCommand.Settings>
{
    private readonly ISnapshotStore _store;
    public DiskSnapshotsDeleteCommand(ISnapshotStore store) { _store = store; }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Snapshot id to delete")]
        public long Id { get; init; }
    }

    public override int Execute(CommandContext context, Settings s)
    {
        var existing = _store.GetSnapshot(s.Id);
        if (existing == null)
        {
            AnsiConsole.MarkupLine($"[red]No snapshot found with id[/] {s.Id}.");
            return 1;
        }

        _store.Delete(s.Id);
        AnsiConsole.MarkupLine(
            $"[green]Deleted snapshot[/] #{s.Id} ({Markup.Escape(existing.RootPath)}, " +
            $"{existing.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}).");
        return 0;
    }
}
