using System.Diagnostics;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

public sealed class MacOSServicesProvider : IServicesProvider
{
    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ServiceInfo>>(EnumerateServices, ct);

    private static IReadOnlyList<ServiceInfo> EnumerateServices()
    {
        var result = new List<ServiceInfo>();
        try
        {
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

                // `launchctl list` doesn't expose a Windows-SCM-style start type (Automatic/
                // Manual/Disabled) — that's a plist-level concept (RunAtLoad/KeepAlive) we can't
                // reliably infer from this output. Report Unknown rather than fabricating
                // Automatic for every service.
                var startType = ServiceStartType.Unknown;

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

    public Task SetStartTypeAsync(string name, ServiceStartType startType, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Changing a service's start type is not supported on macOS — launchd start type is controlled by the LaunchAgent/LaunchDaemon plist, not a runtime API.");

    private static string RunLaunchctl(string args)
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
