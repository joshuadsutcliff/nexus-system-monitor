using System.ComponentModel;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NexusMonitor.CLI.Commands;

/// <summary>
/// Lists and manages system services.
/// </summary>
internal sealed class ServicesCommand : AsyncCommand<ServicesCommand.Settings>
{
    private readonly IServicesProvider _servicesProvider;

    public ServicesCommand(IServicesProvider servicesProvider)
    {
        _servicesProvider = servicesProvider;
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--filter")]
        [Description("Filter services by name (case-insensitive substring)")]
        public string? Filter { get; init; }

        [CommandOption("--running")]
        [Description("Show only running services")]
        [DefaultValue(false)]
        public bool Running { get; init; }

        [CommandOption("--stopped")]
        [Description("Show only stopped services")]
        [DefaultValue(false)]
        public bool Stopped { get; init; }

        [CommandOption("--interactive")]
        [Description("Pick a service interactively and perform an action")]
        [DefaultValue(false)]
        public bool Interactive { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!Environment.IsPrivilegedProcess)
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Not running as administrator — some service operations may fail.");

        var services = await _servicesProvider.GetServicesAsync();
        var filtered = ApplyFilter(services, settings);

        if (settings.Interactive)
            return await RunInteractiveAsync(filtered);

        PrintTable(filtered);
        return 0;
    }

    private static void PrintTable(IReadOnlyList<ServiceInfo> services)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Name[/]"))
            .AddColumn(new TableColumn("[bold]Display Name[/]"))
            .AddColumn(new TableColumn("[bold]Status[/]"))
            .AddColumn(new TableColumn("[bold]Start Type[/]"));

        foreach (var svc in services)
        {
            string stateColor = svc.State switch
            {
                ServiceState.Running => "green",
                ServiceState.Stopped => "red",
                ServiceState.Paused  => "yellow",
                _                    => "grey",
            };

            table.AddRow(
                Markup.Escape(svc.Name),
                Markup.Escape(svc.DisplayName),
                $"[{stateColor}]{svc.State}[/]",
                svc.StartType.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{services.Count} service(s) shown.[/]");
    }

    private async Task<int> RunInteractiveAsync(IReadOnlyList<ServiceInfo> services)
    {
        if (services.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No services found.[/]");
            return 0;
        }

        var choices = services
            .Select(s => $"{s.Name,-40}  [{s.State}]  {s.StartType}")
            .ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a service:")
                .PageSize(20)
                .AddChoices(choices));

        var svc = services[choices.IndexOf(selected)];

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Action for [cyan]{Markup.Escape(svc.Name)}[/]:")
                .AddChoices("Start", "Stop", "Restart",
                             "Set Startup: Automatic", "Set Startup: AutomaticDelayed",
                             "Set Startup: Manual", "Set Startup: Disabled",
                             "Cancel"));

        // Providers throw PlatformNotSupportedException for operations the OS has no API for
        // (e.g. SetStartTypeAsync on macOS, where launchd start type lives in the plist) — the
        // CLI menu is not capability-gated, so surface those as a friendly message here instead
        // of letting them bubble to the top-level handler as a generic crash.
        try
        {
            switch (action)
            {
                case "Start":
                    await _servicesProvider.StartServiceAsync(svc.Name);
                    AnsiConsole.MarkupLine($"[green]Started '{Markup.Escape(svc.Name)}'.[/]");
                    break;
                case "Stop":
                    await _servicesProvider.StopServiceAsync(svc.Name);
                    AnsiConsole.MarkupLine($"[green]Stopped '{Markup.Escape(svc.Name)}'.[/]");
                    break;
                case "Restart":
                    await _servicesProvider.RestartServiceAsync(svc.Name);
                    AnsiConsole.MarkupLine($"[green]Restarted '{Markup.Escape(svc.Name)}'.[/]");
                    break;
                case "Set Startup: Automatic":
                    await _servicesProvider.SetStartTypeAsync(svc.Name, ServiceStartType.Automatic);
                    AnsiConsole.MarkupLine($"[green]Set start type to Automatic.[/]");
                    break;
                case "Set Startup: AutomaticDelayed":
                    await _servicesProvider.SetStartTypeAsync(svc.Name, ServiceStartType.AutomaticDelayed);
                    AnsiConsole.MarkupLine($"[green]Set start type to AutomaticDelayed.[/]");
                    break;
                case "Set Startup: Manual":
                    await _servicesProvider.SetStartTypeAsync(svc.Name, ServiceStartType.Manual);
                    AnsiConsole.MarkupLine($"[green]Set start type to Manual.[/]");
                    break;
                case "Set Startup: Disabled":
                    await _servicesProvider.SetStartTypeAsync(svc.Name, ServiceStartType.Disabled);
                    AnsiConsole.MarkupLine($"[green]Set start type to Disabled.[/]");
                    break;
                default:
                    AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                    break;
            }
        }
        catch (PlatformNotSupportedException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
        }

        return 0;
    }

    private static IReadOnlyList<ServiceInfo> ApplyFilter(
        IReadOnlyList<ServiceInfo> services, Settings settings)
    {
        IEnumerable<ServiceInfo> q = services;

        if (!string.IsNullOrEmpty(settings.Filter))
            q = q.Where(s =>
                s.Name.Contains(settings.Filter, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(settings.Filter, StringComparison.OrdinalIgnoreCase));

        if (settings.Running)
            q = q.Where(s => s.State == ServiceState.Running);
        else if (settings.Stopped)
            q = q.Where(s => s.State == ServiceState.Stopped);

        return q.OrderBy(s => s.DisplayName).ToList();
    }
}
