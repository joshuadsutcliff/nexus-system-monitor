using System.Diagnostics;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

public sealed class MacOSProcessProvider : IProcessProvider, IDisposable
{
    private readonly Dictionary<int, (TimeSpan cpu, DateTime time)> _cpuSamples = new();
    private static readonly int s_processorCount = Math.Max(1, Environment.ProcessorCount);
    private static readonly int s_currentPid = Environment.ProcessId;

    // Reusable buffers — allocated once, shared across all processes per tick
    private readonly byte[]  _nameBuffer    = new byte[256];
    private readonly IntPtr  _taskInfoPtr;
    private readonly int     _taskInfoSize;
    private int _lastProcessCount = 200;

    private bool _disposed;

    // Serializes concurrent Snapshot() calls (timer tick vs GetProcessesAsync)
    private readonly object _snapshotLock = new();

    public MacOSProcessProvider()
    {
        _taskInfoSize = Marshal.SizeOf<proc_taskinfo>();
        _taskInfoPtr  = Marshal.AllocHGlobal(_taskInfoSize);
    }

    // ── P/Invoke declarations ──────────────────────────────────────────────────
    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int setpriority(int which, uint who, int prio);

    [DllImport("libSystem.dylib")]
    private static extern int proc_listallpids(IntPtr buffer, int bufferSize);

    [DllImport("libSystem.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int bufferSize);

    [DllImport("libSystem.dylib")]
    private static extern int proc_name(int pid, byte[] buffer, uint bufferSize);

    private const int PROC_PIDTASKINFO = 4;
    private const int PRIO_PROCESS     = 0;
    private const int SIGKILL          = 9;
    private const int SIGSTOP          = 17;
    private const int SIGCONT          = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct proc_taskinfo
    {
        public ulong pti_virtual_size;
        public ulong pti_resident_size;
        public ulong pti_total_user;
        public ulong pti_total_system;
        public ulong pti_threads_user;
        public ulong pti_threads_system;
        public int   pti_policy;
        public int   pti_faults;
        public int   pti_pageins;
        public int   pti_cow_faults;
        public int   pti_messages_sent;
        public int   pti_messages_received;
        public int   pti_syscalls_mach;
        public int   pti_syscalls_unix;
        public int   pti_csw;
        public int   pti_threadnum;
        public int   pti_numrunning;
        public int   pti_priority;
    }

    // Shared multicast observable
    private IObservable<IReadOnlyList<ProcessInfo>>? _shared;
    private TimeSpan _sharedInterval;
    private readonly object _sharedLock = new();
    private IDisposable? _connection;

    // ── Streaming ──────────────────────────────────────────────────────────────
    public IObservable<IReadOnlyList<ProcessInfo>> GetProcessStream(TimeSpan interval)
    {
        lock (_sharedLock)
        {
            var clampedInterval = interval < TimeSpan.FromSeconds(2)
                ? TimeSpan.FromSeconds(2) : interval;
            // Invalidate cached observable when interval changes
            if (_shared is not null && _sharedInterval != clampedInterval)
            {
                _connection?.Dispose();
                _shared = null;
            }
            if (_shared is null)
            {
                _sharedInterval = clampedInterval;
                var connectable = Observable.Timer(TimeSpan.Zero, clampedInterval)
                                            .Select(_ => (IReadOnlyList<ProcessInfo>)Snapshot())
                                            .Publish();
                _shared     = connectable;
                _connection = connectable.Connect();
            }
            return _shared;
        }
    }

    // ── Snapshot ───────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessInfo>>(() => Snapshot(), ct);

    private IReadOnlyList<ProcessInfo> Snapshot()
    {
        if (_disposed) return Array.Empty<ProcessInfo>();
        lock (_snapshotLock)
        {
        var now    = DateTime.UtcNow;
        var result = new List<ProcessInfo>(_lastProcessCount);

        // Get all PIDs using proc_listallpids
        int pidCount = proc_listallpids(IntPtr.Zero, 0);
        if (pidCount <= 0)
            return SnapshotFallback(now);

        var pidArray = new int[pidCount + 16]; // +16 for race margin
        var gcHandle = GCHandle.Alloc(pidArray, GCHandleType.Pinned);
        int actualCount;
        try
        {
            actualCount = proc_listallpids(gcHandle.AddrOfPinnedObject(), pidArray.Length * sizeof(int));
        }
        finally
        {
            gcHandle.Free();
        }

        if (actualCount <= 0)
            return SnapshotFallback(now);

        for (int i = 0; i < actualCount && i < pidArray.Length; i++)
        {
            int pid = pidArray[i];
            if (pid <= 0) continue;

            try
            {
                // Get process name — reuse shared buffer (zero-fill first to clear previous result)
                Array.Clear(_nameBuffer, 0, _nameBuffer.Length);
                proc_name(pid, _nameBuffer, (uint)_nameBuffer.Length);
                var name = System.Text.Encoding.UTF8.GetString(_nameBuffer)
                                  .TrimEnd('\0').Trim();
                if (string.IsNullOrEmpty(name))
                    name = $"pid{pid}";

                // Get task info (CPU + memory) — reuse shared native buffer
                ulong workingSet     = 0;
                ulong virtualSize    = 0;
                ulong totalUserNs    = 0;
                ulong totalSystemNs  = 0;
                int   threadCount    = 0;

                int ret = proc_pidinfo(pid, PROC_PIDTASKINFO, 0, _taskInfoPtr, _taskInfoSize);
                if (ret == _taskInfoSize)
                {
                    var info      = Marshal.PtrToStructure<proc_taskinfo>(_taskInfoPtr);
                    workingSet    = info.pti_resident_size;
                    virtualSize   = info.pti_virtual_size;
                    totalUserNs   = info.pti_total_user;
                    totalSystemNs = info.pti_total_system;
                    threadCount   = info.pti_threadnum;
                }

                // CPU% delta — macOS reports times in nanoseconds
                double cpuPercent = 0.0;
                var cpuNs = (long)(totalUserNs + totalSystemNs);
                var cpuTs = TimeSpan.FromTicks(cpuNs / 100); // 100ns per tick

                if (_cpuSamples.TryGetValue(pid, out var prev))
                {
                    var cpuDelta  = (cpuTs - prev.cpu).TotalSeconds;
                    var timeDelta = (now - prev.time).TotalSeconds;
                    if (timeDelta > 0 && cpuDelta >= 0)
                        cpuPercent = Math.Clamp(cpuDelta / (timeDelta * s_processorCount) * 100.0, 0, 100);
                }
                _cpuSamples[pid] = (cpuTs, now);

                // Classify
                var category = pid == s_currentPid
                    ? ProcessCategory.CurrentProcess
                    : ProcessCategory.UserApplication;

                result.Add(new ProcessInfo
                {
                    Pid                    = pid,
                    ParentPid              = 0,
                    Name                   = name,
                    Description            = string.Empty,
                    ImagePath              = string.Empty,
                    CommandLine            = string.Empty,
                    UserName               = string.Empty,
                    Category               = category,
                    State                  = ProcessState.Running,
                    StartTime              = DateTime.MinValue,
                    CpuPercent             = cpuPercent,
                    ThreadCount            = threadCount,
                    HandleCount            = 0,
                    WorkingSetBytes        = (long)workingSet,
                    PrivateBytesBytes      = 0,
                    PagedPoolBytes         = 0,
                    VirtualBytesBytes      = (long)virtualSize,
                    IoReadBytesPerSec      = 0,
                    IoWriteBytesPerSec     = 0,
                    GpuPercent             = 0,
                    NetworkSendBytesPerSec = 0,
                    NetworkRecvBytesPerSec = 0,
                    IsElevated             = false,
                    IsCritical             = false,
                    AccessDenied           = false,
                    AffinityMask           = 0,
                    CurrentIoPriority      = IoPriority.Normal,
                    CurrentMemoryPriority  = MemoryPriority.Normal,
                    IsEfficiencyMode       = false,
                });
            }
            catch
            {
                // Process exited or access denied — skip
            }
        }

        // Evict stale CPU samples
        var activePids = new HashSet<int>(result.Select(p => p.Pid));
        foreach (var key in _cpuSamples.Keys.Where(k => !activePids.Contains(k)).ToList())
            _cpuSamples.Remove(key);

        _lastProcessCount = Math.Max(result.Count, 64);
        return result;
        } // end lock (_snapshotLock)
    }

    // Fallback to System.Diagnostics.Process if proc_listallpids fails
    private IReadOnlyList<ProcessInfo> SnapshotFallback(DateTime now)
    {
        var result = new List<ProcessInfo>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                using (proc)
                {
                    var pid       = proc.Id;
                    var name      = proc.ProcessName;
                    var ws        = proc.WorkingSet64;
                    var virt      = proc.VirtualMemorySize64;
                    var threads   = 0;
                    var startTime = DateTime.MinValue;
                    var cpu       = TimeSpan.Zero;
                    var accessDenied = false;

                    try { threads   = proc.Threads.Count; }  catch { }
                    try { startTime = proc.StartTime.ToUniversalTime(); } catch { }
                    try { cpu       = proc.TotalProcessorTime; } catch { accessDenied = true; }

                    double cpuPercent = 0.0;
                    if (!accessDenied && _cpuSamples.TryGetValue(pid, out var prev))
                    {
                        var cpuDelta  = (cpu - prev.cpu).TotalSeconds;
                        var timeDelta = (now - prev.time).TotalSeconds;
                        if (timeDelta > 0 && cpuDelta >= 0)
                            cpuPercent = Math.Clamp(cpuDelta / (timeDelta * s_processorCount) * 100.0, 0, 100);
                    }
                    if (!accessDenied)
                        _cpuSamples[pid] = (cpu, now);

                    var category = pid == s_currentPid
                        ? ProcessCategory.CurrentProcess
                        : ProcessCategory.UserApplication;

                    result.Add(new ProcessInfo
                    {
                        Pid                    = pid,
                        ParentPid              = 0,
                        Name                   = name,
                        Description            = string.Empty,
                        ImagePath              = string.Empty,
                        CommandLine            = string.Empty,
                        UserName               = string.Empty,
                        Category               = category,
                        State                  = ProcessState.Running,
                        StartTime              = startTime,
                        CpuPercent             = cpuPercent,
                        ThreadCount            = threads,
                        HandleCount            = 0,
                        WorkingSetBytes        = ws,
                        PrivateBytesBytes      = 0,
                        PagedPoolBytes         = 0,
                        VirtualBytesBytes      = virt,
                        IoReadBytesPerSec      = 0,
                        IoWriteBytesPerSec     = 0,
                        GpuPercent             = 0,
                        NetworkSendBytesPerSec = 0,
                        NetworkRecvBytesPerSec = 0,
                        IsElevated             = false,
                        IsCritical             = false,
                        AccessDenied           = accessDenied,
                        AffinityMask           = 0,
                        CurrentIoPriority      = IoPriority.Normal,
                        CurrentMemoryPriority  = MemoryPriority.Normal,
                        IsEfficiencyMode       = false,
                    });
                }
            }
            catch { }
        }

        var activePids = new HashSet<int>(result.Select(p => p.Pid));
        foreach (var key in _cpuSamples.Keys.Where(k => !activePids.Contains(k)).ToList())
            _cpuSamples.Remove(key);

        return result;
    }

    // ── Process control ────────────────────────────────────────────────────────
    public Task KillProcessAsync(int pid, bool killTree = false, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (killTree)
            {
                // Best-effort: kill process group
                kill(-pid, SIGKILL);
            }
            kill(pid, SIGKILL);
        }, ct);

    public Task SuspendProcessAsync(int pid, CancellationToken ct = default) =>
        Task.Run(() => kill(pid, SIGSTOP), ct);

    public Task ResumeProcessAsync(int pid, CancellationToken ct = default) =>
        Task.Run(() => kill(pid, SIGCONT), ct);

    public Task SetPriorityAsync(int pid, ProcessPriority priority, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            int nice = priority switch
            {
                ProcessPriority.Idle        =>  19,
                ProcessPriority.BelowNormal =>  10,
                ProcessPriority.Normal      =>   0,
                ProcessPriority.AboveNormal =>  -5,
                ProcessPriority.High        => -10,
                ProcessPriority.RealTime    => -20,
                _                           =>   0,
            };
            setpriority(PRIO_PROCESS, (uint)pid, nice);
        }, ct);

    // NOTE (2026-07-04 capability-flag audit): the six tuning no-ops below (affinity, I/O
    // priority, memory priority, working-set trim, efficiency mode, CPU sets) are all
    // correctly gated off via IPlatformCapabilities on macOS — SupportsCpuAffinity,
    // SupportsIoPriority, SupportsMemoryPriority, SupportsTrimMemory and SupportsEfficiencyMode
    // are all false in MacOSPlatformCapabilities, and SetCpuSetsAsync isn't wired to any UI
    // control at all (Windows-only feature today). None of these are reachable through the
    // UI, so leaving them as silent no-ops here is safe. Left as-is; do not flip any of the
    // above flags to true without a real implementation behind them.
    // macOS does not support CPU affinity — no-op
    public Task SetAffinityAsync(int pid, long affinityMask, CancellationToken ct = default) =>
        Task.CompletedTask;

    // macOS does not expose I/O priority via public API — no-op
    public Task SetIoPriorityAsync(int pid, IoPriority priority, CancellationToken ct = default) =>
        Task.CompletedTask;

    // macOS does not expose memory priority via public API — no-op
    public Task SetMemoryPriorityAsync(int pid, MemoryPriority priority, CancellationToken ct = default) =>
        Task.CompletedTask;

    // macOS working set trim — no direct public API, no-op
    public Task TrimWorkingSetAsync(int pid, CancellationToken ct = default) =>
        Task.CompletedTask;

    // macOS efficiency mode — no direct equivalent, no-op
    public Task SetEfficiencyModeAsync(int pid, bool enabled, CancellationToken ct = default) =>
        Task.CompletedTask;

    // macOS CPU sets — no equivalent, no-op
    public Task SetCpuSetsAsync(int pid, uint[] cpuSetIds, CancellationToken ct = default) =>
        Task.CompletedTask;

    // ── Detail queries ─────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModuleInfo>>([]);

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ThreadInfo>>([]);

    public Task<IReadOnlyList<EnvironmentEntry>> GetEnvironmentAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EnvironmentEntry>>([]);

    public Task<IReadOnlyList<HandleInfo>> GetHandlesAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HandleInfo>>([]);

    public Task<IReadOnlyList<MemoryRegionInfo>> GetMemoryMapAsync(int pid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryRegionInfo>>([]);

    public Task CreateDumpFileAsync(int pid, string outputPath, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Process dump is not yet implemented on macOS.");

    public Task<(long ProcessMask, long SystemMask)> GetAffinityMasksAsync(int pid, CancellationToken ct = default)
    {
        // 5C: 1L << 64 is undefined behavior (wraps to 1). Use -1L for 64+ core systems
        //     which represents all-bits-set and is the correct "all cores" mask.
        long allCoresMask = Environment.ProcessorCount >= 64
            ? -1L
            : (1L << Environment.ProcessorCount) - 1;
        return Task.FromResult((allCoresMask, allCoresMask));
    }


    // ── IDisposable ────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sharedLock) { _connection?.Dispose(); }
        _cpuSamples.Clear();
        if (_taskInfoPtr != IntPtr.Zero)
            Marshal.FreeHGlobal(_taskInfoPtr);
    }
}
