using System.Diagnostics;
using System.Runtime.InteropServices;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Reactive;

namespace NexusMonitor.Platform.Linux;

public sealed class LinuxProcessProvider : IProcessProvider, IDisposable
{
    // ── P/Invoke declarations ──────────────────────────────────────────────────
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, uint who, int prio);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpriority(int which, uint who);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, IntPtr cpusetsize, out ulong mask);

    // syscall(2) — used to invoke ioprio_set, which has no dedicated libc wrapper. The real
    // libc syscall() is variadic; declaring the exact (long, int, int, int) shape used here is
    // the standard, well-established way to call a specific syscall from P/Invoke — the
    // arguments are passed in registers per the platform calling convention regardless of the
    // header's variadic declaration.
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall_ioprio_set(long number, int which, int who, int ioprio);

    [DllImport("libc", SetLastError = true)]
    private static extern int readlink(string path, byte[] buf, int bufsiz);

    private const int PRIO_PROCESS       = 0;
    private const int SIGKILL            = 9;
    private const int SIGSTOP            = 19;
    private const int SIGCONT            = 18;
    private const int IOPRIO_WHO_PROCESS = 1;
    private const int MaxHandles         = 500; // Windows parity — EnumHandles caps at 500 entries
    private const int MaxMemoryRegions   = 300; // Windows parity — EnumMemoryMap caps at 300 entries (result.Count < 300)

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly Dictionary<int, (long cpuTicks, DateTime time)> _cpuSamples = new();
    // Disk I/O delta tracking: pid → (cumReadBytes, cumWriteBytes, sampleTime)
    private readonly Dictionary<int, (long readBytes, long writeBytes, DateTime time)> _ioSamples = new();
    private static readonly int s_processorCount = Math.Max(1, Environment.ProcessorCount);
    private static readonly int s_currentPid     = Environment.ProcessId;
    private static readonly long s_clockTick      = GetClockTicksPerSecond();

    // username cache (uid → name)
    private readonly Dictionary<int, string> _uidCache = new();

    // Stable-field cache: exe, cmdline, username, category rarely change — refresh every 30 s
    private struct CachedProcessStatic
    {
        public string          ImagePath;
        public string          CommandLine;
        public string          UserName;
        public ProcessCategory Category;
        public DateTime        LastRefresh;
        public int             Uid;
    }
    private readonly Dictionary<int, CachedProcessStatic> _staticCache = new();
    private static readonly TimeSpan StaticCacheDuration = TimeSpan.FromSeconds(30);

    private bool _disposed;
    private int _lastProcessCount = 200;

    // Serializes concurrent Snapshot() calls (timer tick vs GetProcessesAsync)
    private readonly object _snapshotLock = new();

    private static long GetClockTicksPerSecond()
    {
        // sysconf(_SC_CLK_TCK) — typically 100 on Linux
        try
        {
            var output = RunFile("getconf", "CLK_TCK");
            if (long.TryParse(output.Trim(), out var v) && v > 0) return v;
        }
        catch (Exception ex) { /* ignored: getconf CLK_TCK not available, fallback to 100 */ _ = ex; }
        return 100;
    }

    private static string RunFile(string cmd, string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(cmd, args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            p.Start();
            var outputTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch (Exception ex) { /* ignored: process kill failed */ _ = ex; } }
            return outputTask.Result;
        }
        catch (Exception ex) { /* ignored: process execution failed, return empty */ _ = ex; return string.Empty; }
    }

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

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessInfo>>(() => Snapshot(), ct);

    // ── Snapshot ───────────────────────────────────────────────────────────────
    private IReadOnlyList<ProcessInfo> Snapshot()
    {
        lock (_snapshotLock)
        {
        var now    = DateTime.UtcNow;
        var result = new List<ProcessInfo>(_lastProcessCount);

        // Read /proc/uptime once per snapshot — not once per process
        var bootTime = DateTime.MinValue;
        try
        {
            var uptimeText = File.ReadAllText("/proc/uptime");
            if (double.TryParse(uptimeText.Split(' ')[0],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var uptimeSec))
                bootTime = now - TimeSpan.FromSeconds(uptimeSec);
        }
        catch (Exception ex) { /* ignored: /proc/uptime read failed */ _ = ex; }

        string[] pidDirs;
        try
        {
            pidDirs = Directory.GetDirectories("/proc");
        }
        catch (Exception ex) { /* ignored: /proc directory enumeration failed */ _ = ex; return result; }

        foreach (var dir in pidDirs)
        {
            var dirName = Path.GetFileName(dir);
            if (!int.TryParse(dirName, out var pid)) continue;

            try
            {
                var info = ReadProcessInfo(pid, dir, now, bootTime);
                if (info != null) result.Add(info);
            }
            catch (Exception ex) { /* ignored: process info read failed for this PID */ _ = ex; }
        }

        // Evict stale CPU, I/O, and static-field samples for dead PIDs
        var activePids = new HashSet<int>(result.Select(p => p.Pid));
        foreach (var key in _cpuSamples.Keys.Where(k => !activePids.Contains(k)).ToList())
            _cpuSamples.Remove(key);
        foreach (var key in _ioSamples.Keys.Where(k => !activePids.Contains(k)).ToList())
            _ioSamples.Remove(key);
        foreach (var key in _staticCache.Keys.Where(k => !activePids.Contains(k)).ToList())
            _staticCache.Remove(key);

        _lastProcessCount = Math.Max(result.Count, 64);
        return result;
        } // end lock (_snapshotLock)
    }

    private ProcessInfo? ReadProcessInfo(int pid, string procDir, DateTime now, DateTime bootTime)
    {
        var statPath   = Path.Combine(procDir, "stat");
        var statusPath = Path.Combine(procDir, "status");

        if (!File.Exists(statPath) || !File.Exists(statusPath))
            return null;

        var statLine   = File.ReadAllText(statPath);
        var statusText = File.ReadAllText(statusPath);

        // Parse /proc/[pid]/stat
        // Format: pid (name) state ppid ... utime stime ...
        // Field 2 (name) is wrapped in parens and may contain spaces
        var nameStart = statLine.IndexOf('(');
        var nameEnd   = statLine.LastIndexOf(')');
        if (nameStart < 0 || nameEnd < 0 || nameEnd <= nameStart) return null;

        var name  = statLine[(nameStart + 1)..nameEnd];
        var rest  = statLine[(nameEnd + 2)..]; // skip ") "
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // parts[0] = state, parts[1] = ppid, ... parts[11]=utime, parts[12]=stime, parts[16]=nice, parts[19]=starttime
        if (parts.Length < 20) return null;

        var stateChar = parts[0].Length > 0 ? parts[0][0] : 'S';
        _ = int.TryParse(parts[1],  out var ppid);
        _ = long.TryParse(parts[11], out var utime);
        _ = long.TryParse(parts[12], out var stime);
        _ = int.TryParse(parts[16],  out var niceValue);
        _ = long.TryParse(parts[19], out var starttime);

        // CPU% delta
        var totalTicks = utime + stime;
        double cpuPercent = 0.0;
        if (_cpuSamples.TryGetValue(pid, out var prev))
        {
            var tickDelta = totalTicks - prev.cpuTicks;
            var timeDelta = (now - prev.time).TotalSeconds;
            if (timeDelta > 0 && tickDelta >= 0)
                cpuPercent = Math.Clamp(
                    tickDelta / (double)s_clockTick / timeDelta / s_processorCount * 100.0,
                    0, 100);
        }
        _cpuSamples[pid] = (totalTicks, now);

        // Parse /proc/[pid]/status
        long   vmRss     = ParseStatusLong(statusText, "VmRSS:");
        long   vmSwap    = ParseStatusLong(statusText, "VmSwap:");
        int    threads   = (int)ParseStatusLong(statusText, "Threads:");
        int    uid       = (int)ParseStatusLong(statusText, "Uid:");

        // Start time (seconds since boot) — bootTime was computed once per Snapshot()
        DateTime startDt = DateTime.MinValue;
        if (bootTime != DateTime.MinValue)
        {
            var procStartSec = starttime / (double)s_clockTick;
            startDt = bootTime + TimeSpan.FromSeconds(procStartSec);
        }

        // Stable fields (exe, cmdline, username, category) — cached for 30 s
        string imagePath, cmdLine, userName;
        ProcessCategory category;
        if (_staticCache.TryGetValue(pid, out var cached)
            && (now - cached.LastRefresh) < StaticCacheDuration
            && cached.Uid == uid)
        {
            imagePath = cached.ImagePath;
            cmdLine   = cached.CommandLine;
            userName  = cached.UserName;
            category  = cached.Category;
        }
        else
        {
            imagePath = string.Empty;
            try { imagePath = new FileInfo(Path.Combine(procDir, "exe")).LinkTarget ?? string.Empty; }
            catch (Exception ex) { /* ignored: symlink read failed */ _ = ex; }

            cmdLine = string.Empty;
            try
            {
                var rawCmd = File.ReadAllText(Path.Combine(procDir, "cmdline"));
                cmdLine = rawCmd.Replace('\0', ' ').Trim();
            }
            catch (Exception ex) { /* ignored: cmdline read failed */ _ = ex; }

            userName = LookupUsername(uid);
            category = pid == s_currentPid
                ? ProcessCategory.CurrentProcess
                : ClassifyLinuxProcess(uid, imagePath);

            _staticCache[pid] = new CachedProcessStatic
            {
                ImagePath   = imagePath,
                CommandLine = cmdLine,
                UserName    = userName,
                Category    = category,
                LastRefresh = now,
                Uid         = uid,
            };
        }

        // Per-process disk I/O from /proc/[pid]/io (may fail with EPERM for other users' processes)
        long ioReadRate = 0, ioWriteRate = 0;
        try
        {
            var ioPath = Path.Combine(procDir, "io");
            if (File.Exists(ioPath))
            {
                long cumRead = 0, cumWrite = 0;
                foreach (var line in File.ReadAllLines(ioPath))
                {
                    if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                    {
                        var val = line.AsSpan(11).Trim();
                        long.TryParse(val, out cumRead);
                    }
                    else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                    {
                        var val = line.AsSpan(12).Trim();
                        long.TryParse(val, out cumWrite);
                    }
                }
                if (_ioSamples.TryGetValue(pid, out var prevIo))
                {
                    var elapsed = (now - prevIo.time).TotalSeconds;
                    if (elapsed > 0)
                    {
                        ioReadRate  = (long)Math.Max(0, (cumRead  - prevIo.readBytes)  / elapsed);
                        ioWriteRate = (long)Math.Max(0, (cumWrite - prevIo.writeBytes) / elapsed);
                    }
                }
                _ioSamples[pid] = (cumRead, cumWrite, now);
            }
        }
        catch { /* EPERM for processes owned by other users — silently skip */ }

        // Process state
        var procState = stateChar switch
        {
            'R' or 'S' => ProcessState.Running,
            'T'        => ProcessState.Suspended,
            'Z'        => ProcessState.Zombie,
            _          => ProcessState.Unknown,
        };

        return new ProcessInfo
        {
            Pid                    = pid,
            ParentPid              = ppid,
            Name                   = name,
            Description            = string.Empty,
            ImagePath              = imagePath,
            CommandLine            = cmdLine,
            UserName               = userName,
            Category               = category,
            State                  = procState,
            StartTime              = startDt,
            CpuPercent             = cpuPercent,
            ThreadCount            = threads,
            HandleCount            = 0,
            WorkingSetBytes        = vmRss * 1024L,
            PrivateBytesBytes      = 0,
            PagedPoolBytes         = vmSwap * 1024L,
            VirtualBytesBytes      = 0,
            IoReadBytesPerSec      = ioReadRate,
            IoWriteBytesPerSec     = ioWriteRate,
            GpuPercent             = 0,
            NetworkSendBytesPerSec = 0,
            NetworkRecvBytesPerSec = 0,
            IsElevated             = uid == 0,
            IsCritical             = false,
            AccessDenied           = false,
            AffinityMask           = 0,
            CurrentIoPriority      = IoPriority.Normal,
            CurrentMemoryPriority  = MemoryPriority.Normal,
            IsEfficiencyMode       = false,
            BasePriority           = niceValue,
        };
    }

    private static long ParseStatusLong(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var rest = text[(idx + key.Length)..].TrimStart();
        // value may be followed by " kB" or newline
        var end  = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '-'))
            end++;
        return long.TryParse(rest[..end], out var v) ? v : 0;
    }

    private string LookupUsername(int uid)
    {
        if (_uidCache.TryGetValue(uid, out var cached)) return cached;
        try
        {
            foreach (var line in File.ReadAllLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length >= 3 && int.TryParse(parts[2], out var fileUid) && fileUid == uid)
                {
                    _uidCache[uid] = parts[0];
                    return parts[0];
                }
            }
        }
        catch (Exception ex) { /* ignored: /etc/passwd read failed, fallback to UID string */ _ = ex; }
        _uidCache[uid] = uid.ToString();
        return uid.ToString();
    }

    private static ProcessCategory ClassifyLinuxProcess(int uid, string imagePath)
    {
        if (uid == 0) return ProcessCategory.SystemKernel;
        if (!string.IsNullOrEmpty(imagePath))
        {
            if (imagePath.StartsWith("/usr/lib/systemd/", StringComparison.Ordinal)
             || imagePath.StartsWith("/lib/systemd/",     StringComparison.Ordinal)
             || imagePath.StartsWith("/usr/sbin/",        StringComparison.Ordinal)
             || imagePath.StartsWith("/sbin/",            StringComparison.Ordinal))
                return ProcessCategory.WindowsService;
        }
        return ProcessCategory.UserApplication;
    }

    // ── Process control ────────────────────────────────────────────────────────
    public Task KillProcessAsync(int pid, bool killTree = false, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (killTree) kill(-pid, SIGKILL);
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

    // getpriority(2) legitimately returns negative values on success, so -1 alone doesn't mean
    // failure — we also need errno. DllImport(SetLastError = true) captures errno right after
    // the native call returns (before any managed code can perturb it), which is the standard
    // portable way to disambiguate a genuine nice value of -1 from a real ESRCH/EPERM failure.
    public Task<ProcessPriority?> GetPriorityAsync(int pid, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            int nice = getpriority(PRIO_PROCESS, (uint)pid);
            if (nice == -1)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno != 0) return (ProcessPriority?)null;
            }

            return nice switch
            {
                <= -15          => ProcessPriority.RealTime,
                >= -14 and <= -8 => ProcessPriority.High,
                >= -7 and <= -1  => ProcessPriority.AboveNormal,
                0                => ProcessPriority.Normal,
                >= 1 and <= 14   => ProcessPriority.BelowNormal,
                _                => ProcessPriority.Idle, // >= 15
            };
        }, cancellationToken);

    public Task SetAffinityAsync(int pid, long affinityMask, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var mask = (ulong)affinityMask;
            sched_setaffinity(pid, new IntPtr(sizeof(ulong)), ref mask);
        }, ct);

    // Linux I/O priority via ioprio_set. Syscall number is architecture-specific (251 on
    // x86_64, 30 on aarch64 — see LinuxIoPriority.GetSyscallNumber); the (class, data) value
    // mapping is LinuxIoPriority.ComputeIoprioValue, a pure/testable function.
    public Task SetIoPriorityAsync(int pid, IoPriority priority, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var syscallNumber = LinuxIoPriority.GetSyscallNumber(RuntimeInformation.ProcessArchitecture);
            if (syscallNumber is null)
                // Unsupported architecture — no known-correct ioprio_set syscall number to call
                // (see LinuxIoPriority.GetSyscallNumber). Every call site (RulesEngine.cs) wraps
                // this in try/catch-and-log, so throwing here degrades to a logged warning
                // rather than a silent no-op that would misleadingly imply the priority was set.
                throw new PlatformNotSupportedException(
                    $"IO priority is not supported on this architecture ({RuntimeInformation.ProcessArchitecture}) — ioprio_set has no known syscall number here.");

            var ioprio = LinuxIoPriority.ComputeIoprioValue(priority);
            long result = syscall_ioprio_set(syscallNumber.Value, IOPRIO_WHO_PROCESS, pid, ioprio);
            if (result < 0)
            {
                // Mirrors WindowsProcessProvider.SetIoPriorityAsync, which throws when it
                // can't even obtain a handle to the target process (e.g. another user's
                // process without permission) — here the single ioprio_set syscall itself
                // is both the "open" and the "set", and EPERM/ESRCH surface the same way.
                var errno = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Cannot set IO priority for process {pid} (errno {errno})");
            }
        }, ct);

    public Task SetMemoryPriorityAsync(int pid, MemoryPriority priority, CancellationToken ct = default) =>
        Task.CompletedTask; // No direct Linux equivalent

    public Task TrimWorkingSetAsync(int pid, CancellationToken ct = default) =>
        Task.CompletedTask; // madvise(MADV_DONTNEED) requires per-mapping addresses

    public Task SetEfficiencyModeAsync(int pid, bool enabled, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (enabled)
                setpriority(PRIO_PROCESS, (uint)pid, 10); // Nice +10
            // No direct equivalent for "efficiency mode" on Linux
        }, ct);

    // ── Detail queries ─────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(int pid, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ModuleInfo>>(() => ReadModules(pid), ct);

    private static IReadOnlyList<ModuleInfo> ReadModules(int pid)
    {
        var result  = new List<ModuleInfo>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var mapsPath = $"/proc/{pid}/maps";
        if (!File.Exists(mapsPath)) return result;

        try
        {
            foreach (var line in File.ReadAllLines(mapsPath))
            {
                var entry = ProcMapsParser.ParseLine(line);
                if (entry is null || !entry.Value.HasPath) continue;

                var path = entry.Value.Path;
                if (!path.StartsWith('/') || !seen.Add(path)) continue;

                result.Add(new ModuleInfo(Path.GetFileName(path), path, (long)entry.Value.Start));
            }
        }
        catch (Exception ex) { /* ignored: /proc maps read failed */ _ = ex; }

        return result;
    }

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(int pid, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ThreadInfo>>(() => ReadThreads(pid), ct);

    private static IReadOnlyList<ThreadInfo> ReadThreads(int pid)
    {
        var result   = new List<ThreadInfo>();
        var taskPath = $"/proc/{pid}/task";
        if (!Directory.Exists(taskPath)) return result;

        try
        {
            foreach (var tidDir in Directory.GetDirectories(taskPath))
            {
                if (!int.TryParse(Path.GetFileName(tidDir), out var tid)) continue;
                var statPath = Path.Combine(tidDir, "stat");
                if (!File.Exists(statPath)) continue;

                try
                {
                    var line      = File.ReadAllText(statPath);
                    var nameEnd   = line.LastIndexOf(')');
                    var rest      = nameEnd >= 0 ? line[(nameEnd + 2)..] : line;
                    var parts     = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // parts[15] = priority (field 18 overall, 0-indexed from after name)
                    int priority  = parts.Length > 15 && int.TryParse(parts[15], out var pr) ? pr : 0;

                    result.Add(new ThreadInfo(tid, pid, priority));
                }
                catch (Exception ex) { /* ignored: thread stat read failed */ _ = ex; }
            }
        }
        catch (Exception ex) { /* ignored: task directory enumeration failed */ _ = ex; }

        return result;
    }

    public Task<IReadOnlyList<EnvironmentEntry>> GetEnvironmentAsync(int pid, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<EnvironmentEntry>>(() => ReadEnvironment(pid), ct);

    private static IReadOnlyList<EnvironmentEntry> ReadEnvironment(int pid)
    {
        var result   = new List<EnvironmentEntry>();
        var envPath  = $"/proc/{pid}/environ";
        if (!File.Exists(envPath)) return result;

        try
        {
            var raw   = File.ReadAllBytes(envPath);
            var text  = System.Text.Encoding.UTF8.GetString(raw);
            foreach (var entry in text.Split('\0'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var eq = entry.IndexOf('=');
                if (eq < 0) continue;
                result.Add(new EnvironmentEntry(entry[..eq], entry[(eq + 1)..]));
            }
        }
        catch (Exception ex) { /* ignored: environ read failed */ _ = ex; }

        return result;
    }

    public Task<IReadOnlyList<HandleInfo>> GetHandlesAsync(int pid, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<HandleInfo>>(() => ReadHandles(pid), ct);

    private static IReadOnlyList<HandleInfo> ReadHandles(int pid)
    {
        var result = new List<HandleInfo>();
        var fdDir  = $"/proc/{pid}/fd";

        string[] fdPaths;
        try
        {
            fdPaths = Directory.GetFileSystemEntries(fdDir);
        }
        catch (Exception ex)
        {
            // EACCES for other users' processes, ENOENT if it exited mid-read — honest empty
            // result rather than a partial/fabricated one.
            _ = ex;
            return result;
        }

        // Sized to Linux's PATH_MAX so a genuinely long absolute file path never silently
        // truncates (readlink has no truncation signal — a too-small buffer just fills
        // silently, which would otherwise surface as subtly wrong data rather than an honest
        // failure). Socket/pipe/anon_inode targets are a few dozen bytes at most.
        var readlinkBuf = new byte[4096];

        foreach (var fdPath in fdPaths)
        {
            if (result.Count >= MaxHandles) break;

            var fdName = Path.GetFileName(fdPath);
            if (!ulong.TryParse(fdName, out var fdNumber)) continue;

            string target;
            try
            {
                int len = readlink(fdPath, readlinkBuf, readlinkBuf.Length);
                if (len <= 0) continue; // unresolvable (raced with process exit, etc.) — skip, don't fabricate
                target = System.Text.Encoding.UTF8.GetString(readlinkBuf, 0, len);
            }
            catch (Exception ex) { /* ignored: readlink failed for this one fd */ _ = ex; continue; }

            var typeName = ProcFdClassifier.ClassifyTypeName(target);
            // AccessDisplay has no Linux equivalent of a Windows ACCESS_MASK for an fd — honest
            // "unknown" convention is the empty string (matches ProcessInfo.Description's use
            // of string.Empty above for fields with no data, rather than fabricating a value).
            result.Add(new HandleInfo(fdNumber, typeName, target, string.Empty));
        }

        // Sort by TypeName, then ObjectName — matches WindowsProcessProvider.EnumHandles.
        result.Sort((a, b) =>
        {
            int c = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.ObjectName, b.ObjectName, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    public Task<IReadOnlyList<MemoryRegionInfo>> GetMemoryMapAsync(int pid, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<MemoryRegionInfo>>(() => ReadMemoryMap(pid), ct);

    private static IReadOnlyList<MemoryRegionInfo> ReadMemoryMap(int pid)
    {
        var result   = new List<MemoryRegionInfo>();
        var mapsPath = $"/proc/{pid}/maps";
        if (!File.Exists(mapsPath)) return result;

        try
        {
            foreach (var line in File.ReadAllLines(mapsPath))
            {
                if (result.Count >= MaxMemoryRegions) break;

                var entry = ProcMapsParser.ParseLine(line);
                if (entry is null) continue;
                result.Add(ToMemoryRegionInfo(entry.Value));
            }
        }
        catch (Exception ex)
        {
            // EACCES for other users' processes, ENOENT if it exited mid-read — honest empty
            // result rather than a partial/fabricated one.
            _ = ex;
        }

        return result;
    }

    private static MemoryRegionInfo ToMemoryRegionInfo(ProcMapsEntry entry)
    {
        var (regionType, description) = ProcMapsClassifier.ClassifyPath(entry.Path, entry.Perms);
        var protection = ProcMapsClassifier.DecodeProtection(entry.Perms);

        return new MemoryRegionInfo(
            entry.Start,
            entry.End - entry.Start,
            // /proc/<pid>/maps only ever lists VMAs that are actually mapped — Linux has no
            // "reserved but not committed" state visible at this layer the way Windows
            // VirtualQueryEx does, so every region here is honestly "Committed".
            "Committed",
            regionType,
            protection,
            description);
    }

    public Task CreateDumpFileAsync(int pid, string outputPath, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Process dump is not yet implemented on Linux.");

    public Task<(long ProcessMask, long SystemMask)> GetAffinityMasksAsync(int pid, CancellationToken ct = default)
    {
        long allCores = Environment.ProcessorCount >= 64 ? -1L : (1L << Environment.ProcessorCount) - 1;
        long procMask = allCores;
        if (sched_getaffinity(pid, new IntPtr(sizeof(ulong)), out ulong raw) == 0)
            procMask = (long)raw;
        return Task.FromResult((procMask, allCores));
    }

    // Linux CPU sets — no direct equivalent API, no-op
    public Task SetCpuSetsAsync(int pid, uint[] cpuSetIds, CancellationToken ct = default) =>
        Task.CompletedTask;


    // ── IDisposable ────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sharedLock) { _shared?.Dispose(); }
        _cpuSamples.Clear();
        _ioSamples.Clear();
        _uidCache.Clear();
        _staticCache.Clear();
    }
}
