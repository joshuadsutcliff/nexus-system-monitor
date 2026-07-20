using System.ComponentModel;
using NexusMonitor.DiskAnalyzer.Models;
using NexusMonitor.DiskAnalyzer.Scanning;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NexusMonitor.CLI.Commands.Disk;

/// <summary>
/// Scans a directory, saves a snapshot, and optionally diffs it against a previous one.
/// v1 note: saves use <see cref="SnapshotOptions"/> defaults unless --threshold is passed —
/// GUI settings do not flow to the CLI in v1 (the CLI container does not load SettingsService).
/// </summary>
internal sealed class DiskScanCommand : AsyncCommand<DiskScanCommand.Settings>
{
    private readonly ISnapshotStore _store;
    public DiskScanCommand(ISnapshotStore store) { _store = store; }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Directory to scan")]
        public string Path { get; init; } = string.Empty;

        [CommandOption("--diff")]
        [Description("After scanning, diff against: 'latest', a snapshot id, or a date (newest at-or-before)")]
        public string? Diff { get; init; }

        [CommandOption("--format")]
        [Description("Output format: table or json (default: table)")]
        [DefaultValue("table")]
        public string Format { get; init; } = "table";

        [CommandOption("--top")]
        [Description("Limit diff output to the N largest changes by absolute delta")]
        public int? Top { get; init; }

        [CommandOption("--threshold")]
        [Description("Small-file aggregation threshold in bytes for THIS snapshot (default 1048576). " +
                      "v1 note: CLI saves always use SnapshotOptions defaults unless this is passed — " +
                      "GUI settings never flow to the CLI.")]
        public long? Threshold { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var format = s.Format.ToLowerInvariant();

        if (!Directory.Exists(s.Path))
        {
            AnsiConsole.MarkupLine($"[red]Path not found:[/] {Markup.Escape(s.Path)}");
            return 1;
        }

        // Resolve the diff reference BEFORE saving: 'latest' means the newest
        // snapshot that existed before this scan (spec §7). DO NOT move this below
        // Save — 'latest' would then resolve to the snapshot we just saved and
        // every --diff latest would be an empty self-diff.
        SnapshotInfo? baseline = null;
        if (s.Diff != null)
        {
            baseline = SnapshotRefs.Resolve(_store.ListSnapshots(s.Path), s.Diff);
            if (baseline == null)
            {
                AnsiConsole.MarkupLine($"[red]No snapshot matches[/] '{Markup.Escape(s.Diff)}' for this root.");
                return 1;
            }
        }

        var scanner = new MftScanner(); // falls back to RecursiveScanner off-Windows
        var result = await scanner.ScanAsync(s.Path, new ScanOptions(), progress: null, CancellationToken.None);

        var opts = s.Threshold is long t
            ? new SnapshotOptions(ThresholdBytes: t)
            : new SnapshotOptions();
        var id = _store.Save(result, opts,
            typeof(DiskScanCommand).Assembly.GetName().Version?.ToString());

        if (format != "json")
            AnsiConsole.MarkupLine(
                $"Scanned [bold]{result.TotalFiles:N0}[/] files, {DiskNode.FormatSize(result.TotalSize)} " +
                $"— saved snapshot [bold]#{id}[/].");

        if (baseline == null)
        {
            // No --diff: --format json must still emit a deliberate, stable,
            // machine-readable shape (spec §7's "machine-friendly text" requirement
            // isn't diff-only) — the same SnapshotInfo shape `disk snapshots list
            // --format json` uses, not silence.
            if (format == "json")
                Console.Write(SnapshotInfoJson.ToJson(_store.GetSnapshot(id)!));
            return 0;
        }

        var diff = SnapshotDiffer.Diff(_store, baseline.Id, id);
        Console.Write(format == "json"
            ? DiffFormatter.ToJson(diff, s.Top)
            : DiffFormatter.ToTable(diff, s.Top));
        return 0;
    }
}
