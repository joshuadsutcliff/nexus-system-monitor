using System.Diagnostics;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.Linux;

internal sealed class S6Backend : ILinuxInitBackend
{
    public InitSystem System => InitSystem.S6;

    public IReadOnlyList<ServiceInfo> EnumerateServices()
    {
        var result = new List<ServiceInfo>();
        try
        {
            // s6-rc -a list lists active services
            var activeOutput = RunCapture("s6-rc", "-a list");
            var activeSet    = new HashSet<string>(
                activeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            // s6-rc list lists all known services
            var allOutput = RunCapture("s6-rc", "list");
            foreach (var line in allOutput.Split('\n'))
            {
                var name = line.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new ServiceInfo
                {
                    Name        = name,
                    DisplayName = name,
                    Description = string.Empty,
                    State       = activeSet.Contains(name) ? ServiceState.Running : ServiceState.Stopped,
                    StartType   = ServiceStartType.Manual,
                    ServiceType = ServiceType.Unknown,
                    ProcessId   = 0,
                    BinaryPath  = string.Empty,
                    UserAccount = string.Empty,
                });
            }
        }
        catch { }
        return result;
    }

    public void Start(string name)   => Run("s6-rc", $"-u change {name}");
    public void Stop(string name)    => Run("s6-rc", $"-d change {name}");
    public void Restart(string name)
    {
        Run("s6-svc", $"-r /run/service/{name}");
    }

    public void SetStartType(string name, ServiceStartType startType)
    {
        // KNOWN GAP (flagged in 2026-07-04 capability-flag audit): s6-rc has no simple
        // persistent enable/disable primitive, unlike the other five ILinuxInitBackend
        // implementations (Systemd/OpenRc/Dinit/SysVinit/Runit), which all genuinely apply
        // the change. IPlatformCapabilities.SupportsServiceStartupType is a per-platform (not
        // per-init-backend) flag and is unconditionally true on Linux, so the Services tab's
        // startup-type submenu is shown even when s6-rc is the detected backend, where this
        // call silently does nothing. A correct fix needs a per-backend capability signal
        // rather than a single Linux-wide flag — left as a documented gap rather than
        // guessed at, since flipping the platform-wide flag would hide a working feature for
        // the systemd/OpenRC/Dinit/SysVinit/Runit majority.
    }

    private static void Run(string cmd, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(cmd, args)
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    private static string RunCapture(string cmd, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(cmd, args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } }
            return outputTask.Result;
        }
        catch { return string.Empty; }
    }
}
