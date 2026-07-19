using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.CLI.Commands;
using NexusMonitor.CLI.Commands.Disk;
using NexusMonitor.CLI.Infrastructure;
using NexusMonitor.Core.Services;
using NexusMonitor.Hosting;
using Serilog;
using Serilog.Events;
using Spectre.Console.Cli;
using System.Reflection;

// ── Logging ──────────────────────────────────────────────────────────────────

var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "NexusMonitor", "logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        path:                 Path.Combine(logDirectory, "nexus-cli-.log"),
        rollingInterval:      RollingInterval.Day,
        fileSizeLimitBytes:   10 * 1024 * 1024,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("Nexus CLI starting");

// Global cancellation: CTRL+C or process exit cancels all long-running commands.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;      // prevent abrupt termination; let commands clean up
    Program.GlobalCts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { Program.GlobalCts.Cancel(); } catch (ObjectDisposedException) { }
};

int exitCode = 0;
try
{
    // ── DI container ─────────────────────────────────────────────────────────
    var services = new ServiceCollection();
    services.AddNexusPlatformProviders();
    services.AddNexusCoreServices();

    // Register all CLI commands so they can receive DI constructor injection
    services.AddSingleton<DashboardCommand>();
    services.AddSingleton<ProcessesCommand>();
    services.AddSingleton<ServicesCommand>();
    services.AddSingleton<HealthCommand>();
    services.AddSingleton<AlertsCommand>();
    services.AddSingleton<AlertsListCommand>();
    services.AddSingleton<AlertsStatusCommand>();
    services.AddSingleton<AlertsWatchCommand>();
    services.AddSingleton<RulesCommand>();
    services.AddSingleton<RulesListCommand>();
    services.AddSingleton<RulesStatusCommand>();
    services.AddSingleton<SettingsCommand>();
    services.AddSingleton<SettingsShowCommand>();
    services.AddSingleton<SettingsGetCommand>();
    services.AddSingleton<SettingsSetCommand>();
    services.AddSingleton<PrometheusCommand>();
    services.AddSingleton<ExportCommand>();

    var snapshotDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusMonitor", "disk-snapshots.db");
    services.AddSingleton<NexusMonitor.DiskAnalyzer.Snapshots.ISnapshotStore>(_ =>
    {
        var store = new NexusMonitor.DiskAnalyzer.Snapshots.SnapshotStore(snapshotDbPath);
        store.SweepIncomplete(); // spec §4: crash-recovery sweep at startup
        return store;
    });
    services.AddSingleton<DiskScanCommand>();
    services.AddSingleton<DiskSnapshotsListCommand>();
    services.AddSingleton<DiskSnapshotsDeleteCommand>();
    services.AddSingleton<DiskDiffCommand>();

    var registrar = new TypeRegistrar(services);

    // ── Spectre.Console.Cli app ───────────────────────────────────────────────
    var app = new CommandApp(registrar);
    app.Configure(config =>
    {
        config.SetApplicationName("nexus");
        config.SetApplicationVersion(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

        config.AddCommand<DashboardCommand>("dashboard")
            .WithDescription("Show a live system dashboard (default command)");

        config.AddCommand<ProcessesCommand>("processes")
            .WithAlias("ps")
            .WithDescription("List and manage processes");

        config.AddCommand<ServicesCommand>("services")
            .WithAlias("svc")
            .WithDescription("List and manage system services");

        config.AddCommand<HealthCommand>("health")
            .WithDescription("Show system health snapshot");

        config.AddBranch("alerts", branch =>
        {
            branch.SetDescription("Alert rules management");
            branch.AddCommand<AlertsListCommand>("list")
                .WithDescription("List configured alert rules");
            branch.AddCommand<AlertsStatusCommand>("status")
                .WithDescription("Show alerts engine status");
            branch.AddCommand<AlertsWatchCommand>("watch")
                .WithDescription("Stream live alert events");
        });

        config.AddBranch("rules", branch =>
        {
            branch.SetDescription("Process rules management");
            branch.AddCommand<RulesListCommand>("list")
                .WithDescription("List configured process rules");
            branch.AddCommand<RulesStatusCommand>("status")
                .WithDescription("Show rules engine status");
        });

        config.AddBranch("settings", branch =>
        {
            branch.SetDescription("Application settings");
            branch.AddCommand<SettingsShowCommand>("show")
                .WithDescription("Show all settings");
            branch.AddCommand<SettingsGetCommand>("get")
                .WithDescription("Get a single setting value");
            branch.AddCommand<SettingsSetCommand>("set")
                .WithDescription("Set a setting value");
        });

        config.AddCommand<PrometheusCommand>("prometheus")
            .WithAlias("prom")
            .WithDescription("Start the Prometheus metrics endpoint");

        config.AddCommand<ExportCommand>("export")
            .WithDescription("Export historical metrics to CSV or JSON");

        config.AddBranch("disk", disk =>
        {
            disk.SetDescription("Disk scanning, snapshots, and snapshot diffs");
            disk.AddCommand<DiskScanCommand>("scan")
                .WithDescription("Scan a directory, save a snapshot, optionally diff against a previous one. " +
                    "v1 note: saves use SnapshotOptions defaults unless --threshold is passed — " +
                    "GUI settings do not flow to the CLI.");
            disk.AddBranch("snapshots", snaps =>
            {
                snaps.SetDescription("Manage stored disk snapshots");
                snaps.AddCommand<DiskSnapshotsListCommand>("list")
                    .WithDescription("List snapshots (all roots when no path given)");
                snaps.AddCommand<DiskSnapshotsDeleteCommand>("delete")
                    .WithDescription("Delete a snapshot by id");
            });
            disk.AddCommand<DiskDiffCommand>("diff")
                .WithDescription("Diff two stored snapshots of the same root");
        });
    });

    exitCode = await app.RunAsync(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Nexus CLI terminated unexpectedly");
    exitCode = 1;
}
finally
{
    Log.CloseAndFlush();
    GlobalCts.Cancel();  // ensure cancellation is requested before dispose
    GlobalCts.Dispose();
}

return exitCode;

// Partial class declaration so commands can access GlobalCts as Program.GlobalCts.
internal static partial class Program
{
    internal static readonly CancellationTokenSource GlobalCts = new();
}
