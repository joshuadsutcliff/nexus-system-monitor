using System.Diagnostics;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

public sealed class MacOSServicesProvider : IServicesProvider
{
    // Sym-1 Task 3: one instance per app lifetime (DI registers this provider as a singleton),
    // so the plist index it lazily builds on first EnumerateServices() call is cached for the
    // life of the app rather than rebuilt on every Services-tab visit. See MacOSLaunchdIndex's
    // class doc for the full performance rationale.
    private readonly MacOSLaunchdIndex _launchdIndex = new();

    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ServiceInfo>>(EnumerateServices, ct);

    private IReadOnlyList<ServiceInfo> EnumerateServices()
    {
        var result = new List<ServiceInfo>();
        try
        {
            // Two per-refresh costs, deliberately NOT per-service: the plist index (cached,
            // mtime-invalidated — see MacOSLaunchdIndex) and the disabled-label set (2 cheap
            // `launchctl print-disabled` execs, intentionally re-fetched every refresh since
            // disabled state can change without any plist mtime changing).
            var plistIndex     = _launchdIndex.GetOrBuildIndex();
            var disabledLabels = _launchdIndex.GetDisabledLabels();

            // launchctl list: PID  Status  Label
            var output = RunLaunchctl("list");
            foreach (var line in output.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (parts[0].Equals("PID", StringComparison.OrdinalIgnoreCase)) continue; // header

                var pidStr  = parts[0].Trim();
                var label   = parts[2].Trim();
                if (string.IsNullOrEmpty(label)) continue;

                _ = int.TryParse(pidStr, out var pid);
                var state = pid > 0 ? ServiceState.Running : ServiceState.Stopped;

                // `launchctl list` doesn't expose a Windows-SCM-style start type directly — that's
                // a plist-level concept (RunAtLoad/KeepAlive) plus the separate print-disabled
                // override table. Derive it honestly from launchd metadata rather than the old
                // hardcoded Unknown; see LaunchdStartType.Map for the priority rules.
                var plistFound = plistIndex.TryGetFacts(label, out var facts);
                var isDisabled = disabledLabels.Contains(label);
                var startType  = LaunchdStartType.Map(plistFound, isDisabled, facts.RunAtLoad, facts.KeepAliveTruthy);

                result.Add(new ServiceInfo
                {
                    Name        = label,
                    DisplayName = label,
                    Description = string.Empty,
                    State       = state,
                    StartType   = startType,
                    ServiceType = ServiceType.Unknown,
                    ProcessId   = pid,
                    BinaryPath  = string.Empty,
                    UserAccount = string.Empty,
                });
            }
        }
        catch { }

        return result;
    }

    public Task StartServiceAsync(string name, CancellationToken ct = default) =>
        Task.Run(() => RunLaunchctl($"start {name}"), ct);

    public Task StopServiceAsync(string name, CancellationToken ct = default) =>
        Task.Run(() => RunLaunchctl($"stop {name}"), ct);

    public Task RestartServiceAsync(string name, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RunLaunchctl($"stop {name}");
            RunLaunchctl($"start {name}");
        }, ct);

    // Sym-1 Task 3 investigation finding: this does NOT shell out to launchctl today (unlike the
    // read paths above) — it unconditionally throws before ever touching a process. Changing a
    // launchd job's start policy means editing the LaunchAgent/LaunchDaemon plist's RunAtLoad/
    // KeepAlive keys (or the print-disabled override table) and re-bootstrapping the job; there's
    // no `launchctl <verb> <name>` runtime call that flips "start type" the way Windows SCM or
    // systemd enable/disable do. This task is read-only per its brief — the write path stays a
    // documented gap, not built here. See MacOSPlatformCapabilities.SupportsServiceStartupType for
    // why the flag gating the write-side UI stays false even now that read-side values are honest.
    public Task SetStartTypeAsync(string name, ServiceStartType startType, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Changing a service's start type is not supported on macOS — launchd start type is controlled by the LaunchAgent/LaunchDaemon plist, not a runtime API.");

    /// <summary>Internal so <see cref="MacOSLaunchdIndex"/> can reuse the same launchctl exec pattern
    /// (print-disabled calls) rather than duplicating process-start boilerplate.</summary>
    internal static string RunLaunchctl(string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("launchctl", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { } }
            return outputTask.Result;
        }
        catch
        {
            return string.Empty;
        }
    }
}
