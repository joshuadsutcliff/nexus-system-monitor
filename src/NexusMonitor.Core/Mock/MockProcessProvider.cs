using System.Reactive.Linq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Mock;

public sealed class MockProcessProvider : IProcessProvider
{
    private static readonly Random _rng = new(42);
    private static readonly ProcessInfo[] _baseProcesses = BuildProcessList();

    public IObservable<IReadOnlyList<ProcessInfo>> GetProcessStream(TimeSpan interval)
        => Observable.Interval(interval)
                     .Select(_ => (IReadOnlyList<ProcessInfo>)GetAnimatedSnapshot())
                     .StartWith(GetAnimatedSnapshot());

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<ProcessInfo>)GetAnimatedSnapshot());

    public Task KillProcessAsync(int pid, bool killTree = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SuspendProcessAsync(int pid, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ResumeProcessAsync(int pid, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetPriorityAsync(int pid, ProcessPriority priority, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<ProcessPriority?> GetPriorityAsync(int pid, CancellationToken cancellationToken = default)
        => Task.FromResult<ProcessPriority?>(ProcessPriority.Normal);

    public Task SetAffinityAsync(int pid, long affinityMask, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetIoPriorityAsync(int pid, IoPriority priority, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetMemoryPriorityAsync(int pid, MemoryPriority priority, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task TrimWorkingSetAsync(int pid, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetEfficiencyModeAsync(int pid, bool enabled, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(int pid, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModuleInfo>>(new List<ModuleInfo>
        {
            new("ntdll.dll",      @"C:\Windows\System32\ntdll.dll",      0x7FF800000000),
            new("kernel32.dll",   @"C:\Windows\System32\kernel32.dll",   0x7FF7E0000000),
            new("kernelbase.dll", @"C:\Windows\System32\KernelBase.dll", 0x7FF7C0000000),
        });

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(int pid, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadInfo>>(new List<ThreadInfo>
        {
            new(1001, pid, 8),
            new(1002, pid, 8),
            new(1003, pid, 2),
        });

    public Task<IReadOnlyList<EnvironmentEntry>> GetEnvironmentAsync(int pid, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EnvironmentEntry>>(new List<EnvironmentEntry>
        {
            new("PATH",     @"C:\Windows\system32;C:\Windows"),
            new("TEMP",     @"C:\Users\User\AppData\Local\Temp"),
            new("USERNAME", "User"),
        });

    public Task<IReadOnlyList<HandleInfo>> GetHandlesAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HandleInfo>>([]);

    public Task<IReadOnlyList<MemoryRegionInfo>> GetMemoryMapAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryRegionInfo>>([]);

    public Task CreateDumpFileAsync(int pid, string outputPath, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<(long ProcessMask, long SystemMask)> GetAffinityMasksAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult((0xFFL, 0xFFL));

    public Task SetCpuSetsAsync(int pid, uint[] cpuSetIds, CancellationToken ct = default) =>
        Task.CompletedTask;


    private static ProcessInfo[] GetAnimatedSnapshot()
    {
        var t = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        return _baseProcesses.Select((p, i) =>
        {
            var cpu = p.Category switch
            {
                ProcessCategory.SystemKernel => 0.2 + 0.1 * Math.Sin(t * 0.5 + i),
                ProcessCategory.UserApplication => 1.5 + 3.0 * Math.Abs(Math.Sin(t * 0.3 + i * 0.7)),
                _ => 0.05 + 0.1 * _rng.NextDouble()
            };
            var mem = p.WorkingSetBytes + (long)(_rng.NextDouble() * 1_024_000 - 512_000);
            return p with
            {
                CpuPercent = Math.Max(0, Math.Round(cpu, 1)),
                WorkingSetBytes = Math.Max(4_096, mem),
                IoReadBytesPerSec = _rng.Next(0, 50_000),
                IoWriteBytesPerSec = _rng.Next(0, 20_000)
            };
        }).ToArray();
    }

    private static ProcessInfo[] BuildProcessList() =>
    [
        Mock(4,    0,   "System",              "NT Kernel & System",       "",                                   ProcessCategory.SystemKernel,   "SYSTEM",  40_960,    0),
        Mock(88,   4,   "Registry",            "NT Registry",              "",                                   ProcessCategory.SystemKernel,   "SYSTEM",  81_920,    0),
        Mock(576,  4,   "smss.exe",            "Session Manager",          @"C:\Windows\System32\smss.exe",      ProcessCategory.SystemKernel,   "SYSTEM",  1_200_000, 0),
        Mock(720,  576, "csrss.exe",           "Client Server Runtime",    @"C:\Windows\System32\csrss.exe",     ProcessCategory.SystemKernel,   "SYSTEM",  4_096_000, 0),
        Mock(832,  576, "wininit.exe",         "Windows Init",             @"C:\Windows\System32\wininit.exe",   ProcessCategory.SystemKernel,   "SYSTEM",  5_120_000, 0),
        Mock(848,  832, "services.exe",        "Services and Controller",  @"C:\Windows\System32\services.exe",  ProcessCategory.SystemKernel,   "SYSTEM",  6_144_000, 0),
        Mock(856,  832, "lsass.exe",           "Local Security Authority", @"C:\Windows\System32\lsass.exe",     ProcessCategory.SystemKernel,   "SYSTEM",  12_288_000,0),
        Mock(1200, 848, "svchost.exe",         "Host Process (netsvcs)",   @"C:\Windows\System32\svchost.exe",   ProcessCategory.WindowsService, "SYSTEM",  18_432_000,0),
        Mock(1320, 848, "svchost.exe",         "Host Process (LocalSvc)",  @"C:\Windows\System32\svchost.exe",   ProcessCategory.WindowsService, "SYSTEM",  9_000_000, 0),
        Mock(1580, 848, "svchost.exe",         "Host Process (NetworkSvc)",@"C:\Windows\System32\svchost.exe",   ProcessCategory.WindowsService, "SYSTEM",  22_000_000,0),
        Mock(2040, 848, "spoolsv.exe",         "Print Spooler",            @"C:\Windows\System32\spoolsv.exe",   ProcessCategory.WindowsService, "SYSTEM",  7_680_000, 0),
        Mock(2200, 848, "MsMpEng.exe",         "Windows Defender",         @"C:\Program Files\Windows Defender\MsMpEng.exe", ProcessCategory.WindowsService, "SYSTEM", 102_400_000, 0),
        Mock(3200, 848, "WmiPrvSE.exe",        "WMI Provider Host",        @"C:\Windows\System32\wbem\WmiPrvSE.exe", ProcessCategory.WindowsService, "SYSTEM", 8_000_000, 0),
        Mock(4520, 1,   "explorer.exe",        "Windows Explorer",         @"C:\Windows\explorer.exe",           ProcessCategory.UserApplication,"User",    92_000_000,0),
        Mock(5000, 4520,"chrome.exe",          "Google Chrome",            @"C:\Program Files\Google\Chrome\Application\chrome.exe", ProcessCategory.UserApplication,"User", 350_000_000, 0),
        Mock(5100, 5000,"chrome.exe",          "Google Chrome (Renderer)", @"C:\Program Files\Google\Chrome\Application\chrome.exe", ProcessCategory.UserApplication,"User", 120_000_000, 0),
        Mock(5200, 5000,"chrome.exe",          "Google Chrome (GPU)",      @"C:\Program Files\Google\Chrome\Application\chrome.exe", ProcessCategory.GpuAccelerated, "User", 80_000_000,  0),
        Mock(5800, 4520,"code.exe",            "Visual Studio Code",       @"C:\Users\User\AppData\Local\Programs\Microsoft VS Code\Code.exe", ProcessCategory.DotNetManaged,"User", 280_000_000, 0),
        Mock(5900, 5800,"code.exe",            "VS Code (Extension Host)", @"C:\Users\User\AppData\Local\Programs\Microsoft VS Code\Code.exe", ProcessCategory.DotNetManaged,"User", 95_000_000,  0),
        Mock(6400, 4520,"Spotify.exe",         "Spotify",                  @"C:\Users\User\AppData\Roaming\Spotify\Spotify.exe", ProcessCategory.UserApplication,"User", 185_000_000, 0),
        Mock(6800, 4520,"Discord.exe",         "Discord",                  @"C:\Users\User\AppData\Local\Discord\app-1.0.9164\Discord.exe", ProcessCategory.UserApplication,"User", 210_000_000, 0),
        Mock(7100, 4520,"WindowsTerminal.exe", "Windows Terminal",         @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", ProcessCategory.DotNetManaged,"User", 64_000_000, 0),
        Mock(7400, 4520,"notepad.exe",         "Notepad",                  @"C:\Windows\notepad.exe",            ProcessCategory.UserApplication,"User",    18_000_000,0),
        Mock(8100, 4520,"NexusMonitor.exe",    "Nexus Monitor",            @"O:\Github\Nexus System Monitor\src\NexusMonitor.UI\bin\Debug\NexusMonitor.exe", ProcessCategory.CurrentProcess,"User", 45_000_000, 0),
        Mock(9000, 848, "taskmgr.exe",         "Task Manager",             @"C:\Windows\System32\Taskmgr.exe",   ProcessCategory.UserApplication,"User",    35_000_000,0),
        Mock(9500, 848, "SearchIndexer.exe",   "Windows Search",           @"C:\Windows\system32\SearchIndexer.exe", ProcessCategory.WindowsService,"SYSTEM", 28_000_000,0),
        Mock(9900, 4520,"mspaint.exe",         "Paint",                    @"C:\Windows\system32\mspaint.exe",   ProcessCategory.Suspended,      "User",    14_000_000,0),
    ];

    private static ProcessInfo Mock(int pid, int ppid, string name, string desc, string path,
        ProcessCategory category, string user, long ws, double cpu) => new()
    {
        Pid = pid,
        ParentPid = ppid,
        Name = name,
        Description = desc,
        ImagePath = path,
        CommandLine = path.Length > 0 ? $"\"{path}\"" : "",
        UserName = user,
        Category = category,
        State = category == ProcessCategory.Suspended ? ProcessState.Suspended : ProcessState.Running,
        StartTime = DateTime.Now.AddMinutes(-_rng.Next(1, 300)),
        CpuPercent = cpu,
        ThreadCount = _rng.Next(2, 32),
        HandleCount = _rng.Next(50, 500),
        WorkingSetBytes = ws,
        PrivateBytesBytes = (long)(ws * 0.7),
        IsElevated = user == "SYSTEM",
        IsCritical = category == ProcessCategory.SystemKernel
    };
}
