using System.ComponentModel;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NexusMonitor.CLI.Commands.Disk;

/// <summary>Diffs two previously stored snapshots of the same root.</summary>
internal sealed class DiskDiffCommand : Command<DiskDiffCommand.Settings>
{
    private readonly ISnapshotStore _store;
    public DiskDiffCommand(ISnapshotStore store) { _store = store; }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<older-id>")]
        [Description("Id of the older (baseline) snapshot")]
        public long OlderId { get; init; }

        [CommandArgument(1, "<newer-id>")]
        [Description("Id of the newer snapshot")]
        public long NewerId { get; init; }

        [CommandOption("--format")]
        [Description("Output format: table or json (default: table)")]
        [DefaultValue("table")]
        public string Format { get; init; } = "table";

        [CommandOption("--top")]
        [Description("Limit diff output to the N largest changes by absolute delta")]
        public int? Top { get; init; }
    }

    public override int Execute(CommandContext context, Settings s)
    {
        DiffResult diff;
        try
        {
            diff = SnapshotDiffer.Diff(_store, s.OlderId, s.NewerId);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        Console.Write(s.Format.ToLowerInvariant() == "json"
            ? DiffFormatter.ToJson(diff, s.Top)
            : DiffFormatter.ToTable(diff, s.Top));
        return 0;
    }
}
