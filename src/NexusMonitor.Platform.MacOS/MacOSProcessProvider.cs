using System.Diagnostics;
using System.Runtime.InteropServices;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Reactive;

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

    // uid -> account name, shared across every Snapshot() tick (see MacOSUidNameCache).
    private readonly MacOSUidNameCache _uidNameCache;

    // Efficiency Mode (Darwin background QoS clamp) state THIS app instance has itself set, keyed
    // by pid. See MacOSEfficiencyMode's doc comment for why this — not a live per-pid OS read —
    // is the honest read path for any pid other than our own. Guarded by _efficiencyModeLock
    // since SetEfficiencyModeAsync runs on a Task.Run background thread while Snapshot() may run
    // on the timer thread concurrently.
    private readonly Dictionary<int, bool> _efficiencyModeSetByUs = new();
    private readonly object _efficiencyModeLock = new();

    public MacOSProcessProvider()
    {
        _taskInfoSize = Marshal.SizeOf<proc_taskinfo>();
        _taskInfoPtr  = Marshal.AllocHGlobal(_taskInfoSize);
        _uidNameCache = new MacOSUidNameCache(ResolveUserNameViaGetpwuid);
    }

    // ── P/Invoke declarations ──────────────────────────────────────────────────
    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int setpriority(int which, uint who, int prio);

    // getpriority(2)'s REAL signature is `int getpriority(int which, id_t who)` — 2 params, not
    // 3. (An earlier throwaway probe with a bogus 3rd param happened to "work" on arm64 only
    // because unused register args are harmless there — fixed here to the correct signature.)
    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int getpriority(int which, uint who);

    [DllImport("libSystem.dylib")]
    private static extern int proc_listallpids(IntPtr buffer, int bufferSize);

    [DllImport("libSystem.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int bufferSize);

    [DllImport("libSystem.dylib")]
    private static extern int proc_name(int pid, byte[] buffer, uint bufferSize);

    // sysctl(3): int sysctl(int *name, u_int namelen, void *oldp, size_t *oldlenp, void *newp,
    // size_t newlen). Used here only for KERN_PROC/KERN_PROC_PID — see ResolveUid below.
    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int sysctl(int[] name, uint namelen, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    // getpwuid_r(3): int getpwuid_r(uid_t uid, struct passwd *pwd, char *buf, size_t buflen,
    // struct passwd **result). Reentrant/thread-safe variant — no shared static buffer to race on.
    [DllImport("libSystem.dylib", SetLastError = true)]
    private static extern int getpwuid_r(uint uid, out native_passwd pwd, byte[] buf, nuint buflen, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct native_passwd
    {
        public IntPtr pw_name;
        public IntPtr pw_passwd;
        public uint   pw_uid;
        public uint   pw_gid;
        public long   pw_change;
        public IntPtr pw_class;
        public IntPtr pw_gecos;
        public IntPtr pw_dir;
        public IntPtr pw_shell;
        public long   pw_expire;
    }

    // struct kinfo_proc (<sys/sysctl.h>) wraps struct extern_proc (kp_proc) and struct eproc
    // (kp_eproc, which embeds struct _ucred e_ucred containing cr_uid — the effective uid).
    // Both extern_proc and vmspace are opaque/kernel-internal types with no portable managed
    // layout, so rather than re-declare their full (and largely irrelevant) field lists, we
    // read cr_uid directly out of an unmanaged buffer at a fixed byte offset. Both constants
    // below were derived on THIS machine (Apple Silicon, macOS 26.5.2/Darwin, build 25F84) via
    // a `clang`-compiled `offsetof`/`sizeof` probe against the real
    // <sys/sysctl.h> struct kinfo_proc definition (Sym-1 Task 4 report has the exact probe +
    // output). This is the same struct/offset system tools (`ps`, `top`, Activity Monitor's
    // helper) rely on to show every process's owner unprivileged, which is why it has stayed
    // ABI-stable across macOS releases. Defensive guard: if a future OS ever returns a `len`
    // that doesn't match KinfoProcSize, ResolveUid refuses to trust CrUidOffset and reports
    // "unresolved" instead of reading a potentially-wrong offset.
    private const int KinfoProcSize = 648;
    private const int CrUidOffset   = 420;
    private const int CTL_KERN      = 1;
    private const int KERN_PROC     = 14;
    private const int KERN_PROC_PID = 1;

    private const int PROC_PIDTASKINFO    = 4;
    private const int PRIO_PROCESS        = 0;
    // PRIO_DARWIN_PROCESS is also declared as MacOSEfficiencyMode.PrioDarwinProcess (see that
    // file for the SDK header citation) — duplicated here as a plain const so the P/Invoke call
    // sites below read naturally without an extra type qualifier; both are the same source of
    // truth (4) and covered by MacOSEfficiencyModeTests.
    private const int PRIO_DARWIN_PROCESS = MacOSEfficiencyMode.PrioDarwinProcess;
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

    // ── UserName population (uid -> account name) ───────────────────────────────
    //
    // Route chosen: sysctl(CTL_KERN, KERN_PROC, KERN_PROC_PID, pid) -> struct kinfo_proc,
    // reading cr_uid at CrUidOffset. Verified empirically (Sym-1 Task 4 report) against the
    // alternative the brief offered, proc_pidinfo(PROC_PIDTBSDINFO) -> pbi_uid:
    //   - proc_pidinfo(PROC_PIDTBSDINFO) succeeds for the CALLING process's own pid, but fails
    //     with EPERM (errno 1) for ANY other-uid process — including root-owned non-pid-1
    //     daemons like logd (pid 90), not just pid 1. So it does NOT work cross-user unprivileged,
    //     despite reading like it should from the header alone.
    //   - sysctl(KERN_PROC_PID) DOES succeed cross-user unprivileged for every pid tested: self
    //     (uid 501), pid 1/launchd (uid 0), and pid 90/logd (uid 0). This is the same mechanism
    //     `ps`/`top`/Activity Monitor use to show every process's owner without elevation.
    // Hence sysctl is the one actually wired up below; proc_pidinfo(PROC_PIDTBSDINFO) is not used.
    //
    // Tries to resolve the uid that owns <paramref name="pid"/>. Returns false (never guesses)
    // if the process has exited, the kernel returns an unexpected buffer length (defensive
    // layout-mismatch guard — see the kinfo_proc constants above), or the syscall otherwise fails.
    private static bool TryResolveUid(int pid, out uint uid)
    {
        uid = 0;
        var mib = new[] { CTL_KERN, KERN_PROC, KERN_PROC_PID, pid };
        var len = (nuint)KinfoProcSize;
        var buf = Marshal.AllocHGlobal(KinfoProcSize);
        try
        {
            int ret = sysctl(mib, 4, buf, ref len, IntPtr.Zero, 0);
            if (ret != 0) return false;                  // e.g. process exited (ESRCH)
            if ((int)len != KinfoProcSize) return false;  // unexpected layout — don't trust the offset

            uid = unchecked((uint)Marshal.ReadInt32(buf, CrUidOffset));
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // Real getpwuid_r-backed resolver wired into _uidNameCache. Never throws — a failed/negative
    // lookup returns null so MacOSUidNameCache caches the honest-empty convention instead of
    // retrying every tick.
    private static string? ResolveUserNameViaGetpwuid(uint uid)
    {
        // _SC_GETPW_R_SIZE_MAX is typically a few hundred bytes; 4096 leaves generous headroom
        // for pw_gecos/pw_dir/pw_shell on any account without needing a retry-with-larger-buffer
        // loop (ERANGE handling), keeping this a single syscall per uncached uid.
        var buf = new byte[4096];
        try
        {
            int ret = getpwuid_r(uid, out var pwd, buf, (nuint)buf.Length, out var resultPtr);
            if (ret != 0 || resultPtr == IntPtr.Zero || pwd.pw_name == IntPtr.Zero)
                return null; // no matching account — honest "unknown", not a guess
            return Marshal.PtrToStringUTF8(pwd.pw_name);
        }
        catch
        {
            return null;
        }
    }

    // Resolves pid -> account name using the uid route above, sharing one cache across the whole
    // provider lifetime. Empty string (never null) on any failure — the honest "—/empty"
    // convention this file already uses for CPU%/AccessDenied.
    private string GetUserName(int pid) =>
        TryResolveUid(pid, out var uid) ? _uidNameCache.GetName(uid) : string.Empty;

    // Shared multicast observable. SelfHealingSharedStream wraps the timer+Publish() pattern
    // with RetryWithBackoff so a faulted Snapshot() call is logged and the timer is
    // recreated after a short backoff instead of permanently killing the multicast for
    // every subscriber.
    private SelfHealingSharedStream<IReadOnlyList<ProcessInfo>>? _shared;
    private TimeSpan _sharedInterval;
    private readonly object _sharedLock = new();

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
                _shared.Dispose();
                _shared = null;
            }
            if (_shared is null)
            {
                _sharedInterval = clampedInterval;
                _shared = new SelfHealingSharedStream<IReadOnlyList<ProcessInfo>>(
                    Snapshot, clampedInterval,
                    initialBackoff: TimeSpan.FromSeconds(1), maxBackoff: TimeSpan.FromSeconds(30));
            }
            return _shared.Stream;
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
                // Get process name — reuse shared buffer. proc_name(3)'s int return is the ACTUAL
                // name byte-length; the kernel does not zero-pad past it, so stale bytes from a
                // previous call can linger past the NUL. Decode only the reported length (see
                // MacOSProcName's doc comment for the live-verified "contactsd\0k" -> "contactsdk"
                // corruption this fixes) rather than the whole fixed buffer.
                Array.Clear(_nameBuffer, 0, _nameBuffer.Length);
                int nameLen = proc_name(pid, _nameBuffer, (uint)_nameBuffer.Length);
                var name = MacOSProcName.DecodeProcName(_nameBuffer, nameLen).Trim();
                if (string.IsNullOrEmpty(name))
                    name = $"pid{pid}";

                // Get task info (CPU + memory) — reuse shared native buffer
                ulong workingSet     = 0;
                ulong virtualSize    = 0;
                ulong totalUserNs    = 0;
                ulong totalSystemNs  = 0;
                int   threadCount    = 0;

                int ret = proc_pidinfo(pid, PROC_PIDTASKINFO, 0, _taskInfoPtr, _taskInfoSize);
                // proc_pidinfo(PROC_PIDTASKINFO) returns 0 (EPERM) for processes owned by
                // another uid unless we're root — confirmed empirically: root-owned daemons
                // (e.g. launchd pid 1, logd) fail with errno=1, while same-user processes
                // succeed and return the full struct (memory + CPU ns together, never split).
                // Track that failure explicitly so the UI can render "—" instead of a
                // false "0%" for processes we genuinely cannot read unprivileged.
                bool taskInfoOk = ret == _taskInfoSize;
                if (taskInfoOk)
                {
                    var info      = Marshal.PtrToStructure<proc_taskinfo>(_taskInfoPtr);
                    workingSet    = info.pti_resident_size;
                    virtualSize   = info.pti_virtual_size;
                    totalUserNs   = info.pti_total_user;
                    totalSystemNs = info.pti_total_system;
                    threadCount   = info.pti_threadnum;
                }

                // CPU% delta — macOS reports times in nanoseconds. Only sample/seed the
                // delta dictionary when the read actually succeeded, so an unreadable
                // process doesn't get a fabricated zero baseline.
                double cpuPercent = 0.0;
                if (taskInfoOk)
                {
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
                }
                else
                {
                    _cpuSamples.Remove(pid);
                }

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
                    UserName               = GetUserName(pid),
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
                    AccessDenied           = !taskInfoOk,
                    AffinityMask           = 0,
                    CurrentIoPriority      = IoPriority.Normal,
                    CurrentMemoryPriority  = MemoryPriority.Normal,
                    IsEfficiencyMode       = ReadEfficiencyMode(pid),
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

        // Evict stale Efficiency Mode tracking for pids that exited (avoids unbounded growth and
        // a reused pid inheriting a stale prior process's tracked state).
        lock (_efficiencyModeLock)
        {
            foreach (var key in _efficiencyModeSetByUs.Keys.Where(k => !activePids.Contains(k)).ToList())
                _efficiencyModeSetByUs.Remove(key);
        }

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
                        UserName               = GetUserName(pid),
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
                        IsEfficiencyMode       = ReadEfficiencyMode(pid),
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

    // NOTE (2026-07-04 capability-flag audit; Efficiency Mode carved out 2026-07-08 Sym-1 Task 4):
    // the four tuning no-ops below (affinity, I/O priority, memory priority, working-set trim)
    // are still correctly gated off via IPlatformCapabilities on macOS — SupportsCpuAffinity,
    // SupportsIoPriority, SupportsMemoryPriority and SupportsTrimMemory are all false in
    // MacOSPlatformCapabilities, and SetCpuSetsAsync isn't wired to any UI control at all
    // (Windows-only feature today). None of these four are reachable through the UI, so leaving
    // them as silent no-ops here is safe. Left as-is; do not flip any of the above flags to true
    // without a real implementation behind them. Efficiency Mode (below) now HAS a real
    // implementation (setpriority(PRIO_DARWIN_PROCESS, ...)) and SupportsEfficiencyMode is true.
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

    // ── Efficiency Mode (Darwin background QoS clamp) ───────────────────────────
    // setpriority(PRIO_DARWIN_PROCESS, pid, PRIO_DARWIN_BG) clamps an arbitrary same-user
    // process into the background scheduling/IO/network tier; prio 0 clears it. Verified
    // empirically to work for any same-user target pid (not just the caller's own children —
    // confirmed against an unrelated detached process too), mirroring what `taskpolicy -p pid -b`
    // does. EPERM on a process we don't own surfaces as an honest failure here, same as the
    // SetPriorityAsync/SetIoPriorityAsync boundary pattern elsewhere in this codebase.
    public Task SetEfficiencyModeAsync(int pid, bool enabled, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            int prio = MacOSEfficiencyMode.ToPrioValue(enabled);
            int ret  = setpriority(PRIO_DARWIN_PROCESS, (uint)pid, prio);
            if (ret != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Cannot set Efficiency Mode for process {pid} (errno {errno})");
            }

            lock (_efficiencyModeLock) { _efficiencyModeSetByUs[pid] = enabled; }
        }, ct);

    // See MacOSEfficiencyMode's doc comment for the full derivation. Only the CURRENT process's
    // read (who=0) is a genuinely truthful live getpriority(2) call — for every other pid we
    // report what this app instance has itself set (never fabricated for pids we haven't
    // touched), since macOS has no public unprivileged API to read another process's BG state.
    private bool ReadEfficiencyMode(int pid)
    {
        if (pid == s_currentPid)
        {
            int selfPrio = getpriority(PRIO_DARWIN_PROCESS, 0);
            return MacOSEfficiencyMode.FromPrioValue(selfPrio);
        }

        lock (_efficiencyModeLock)
        {
            return _efficiencyModeSetByUs.TryGetValue(pid, out var enabled) && enabled;
        }
    }

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

    // ── Process dump (sample-based) ─────────────────────────────────────────────
    // `/usr/bin/sample <pid> 3 -f <output>` produces a call-stack sampling report — a text
    // profile of which functions were executing, NOT a memory dump (macOS has no minidump
    // equivalent; see MacOSSampleCommand's doc comment). Own-user processes only: sampling
    // another user's/root's process without sudo fails (verified empirically against pid
    // 1/launchd: exit code 255, stderr explicitly names the missing privilege) — propagated
    // here as an honest failure, mirroring how WindowsProcessProvider surfaces a failed
    // MiniDumpWriteDump via its Win32 error text.
    public Task CreateDumpFileAsync(int pid, string outputPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(MacOSSampleCommand.BinaryPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            foreach (var arg in MacOSSampleCommand.BuildArguments(pid, outputPath))
                proc.StartInfo.ArgumentList.Add(arg);

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            // sample's own capture duration is MacOSSampleCommand.DurationSeconds (3s) plus
            // symbolication time — 20s covers that with headroom without hanging forever on a
            // stuck/huge process.
            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(); proc.WaitForExit(2_000); } catch { /* best-effort cleanup */ }
            }

            if (proc.ExitCode != 0)
            {
                var stderr = stderrTask.Result.Trim();
                throw new InvalidOperationException(
                    $"sample failed for process {pid} (exit code {proc.ExitCode})" +
                    (stderr.Length > 0 ? $": {stderr}" : string.Empty));
            }
            _ = stdoutTask.Result; // drain, discard — normal-path stdout is just status text
        }, ct);

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
        lock (_sharedLock) { _shared?.Dispose(); }
        _cpuSamples.Clear();
        if (_taskInfoPtr != IntPtr.Zero)
            Marshal.FreeHGlobal(_taskInfoPtr);
    }
}
