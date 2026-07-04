using System.Diagnostics;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Text.Json;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

// P/Invoke wrapper for libSystem — must be partial for [LibraryImport] source generation
internal static partial class LibSystem
{
    // Cache mach_host_self() — each call leaks a Mach port; 65536 limit would crash at 2s intervals
    public static readonly uint HostSelf = mach_host_self();

    // mach_task_self_ is a DATA export (the cached task-self Mach port), NOT a function —
    // `mach_task_self()` is a C macro that reads this global. P/Invoking it as a function
    // and calling it jumps into a data address and bus-errors (EXC_BAD_ACCESS /
    // KERN_PROTECTION_FAILURE). Read the symbol's value once and cache it (it never
    // changes for the lifetime of the process).
    public static readonly uint TaskSelf = ReadTaskSelfPort();

    private static uint ReadTaskSelfPort()
    {
        var handle = NativeLibrary.Load("libSystem.B.dylib");
        var addr   = NativeLibrary.GetExport(handle, "mach_task_self_");
        return (uint)Marshal.ReadInt32(addr);   // mach_port_t is a 32-bit unsigned int
    }

    [LibraryImport("libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sysctlbyname(
        string name, [Out] byte[] oldp, ref nuint oldlenp, nint newp, nuint newlen);

    // mach_port_t / natural_t / mach_msg_type_number_t are all 32-bit *unsigned* int.
    // vm_address_t / vm_size_t are pointer-width unsigned (nuint) on LP64 macOS.
    [DllImport("libSystem.B.dylib")]
    public static extern uint mach_host_self();

    [DllImport("libSystem.B.dylib")]
    public static extern int host_processor_info(
        uint host,
        int flavor,
        out uint processorCount,
        out nint processorInfo,
        out uint processorInfoCount);

    [DllImport("libSystem.B.dylib")]
    public static extern int vm_deallocate(uint task, nuint address, nuint size);

    // host_statistics64(HOST_VM_INFO64) — the stable, version-independent way to read page
    // counts (active/inactive/wired/compressed/purgeable). The equivalent sysctl OIDs
    // (vm.page_active_count, vm.page_wire_count, vm.page_inactive_count,
    // vm.compressor_page_count) do not exist on this OS (confirmed via `sysctl`: "unknown
    // oid") — sysctlbyname fails and SysctlInt() silently returns 0 for all four, so a
    // formula built on them always saw wired/active/inactive/compressed as 0.
    [DllImport("libSystem.B.dylib")]
    public static extern int host_statistics64(
        uint host, int flavor, out VmStatistics64 hostInfo, ref uint hostInfoCount);

}

// Mirrors XNU's vm_statistics64_data_t (mach/vm_statistics.h). Field order/widths matter —
// natural_t is a 32-bit uint, the counters below it are 64-bit; this layout is part of the
// stable Mach host_statistics64() ABI used by vm_stat/Activity Monitor/top.
[StructLayout(LayoutKind.Sequential)]
internal struct VmStatistics64
{
    public uint  FreeCount;
    public uint  ActiveCount;
    public uint  InactiveCount;
    public uint  WireCount;
    public ulong ZeroFillCount;
    public ulong Reactivations;
    public ulong Pageins;
    public ulong Pageouts;
    public ulong Faults;
    public ulong CowFaults;
    public ulong Lookups;
    public ulong Hits;
    public ulong Purges;
    public uint  PurgeableCount;
    public uint  SpeculativeCount;
    public ulong Decompressions;
    public ulong Compressions;
    public ulong Swapins;
    public ulong Swapouts;
    public uint  CompressorPageCount;
    public uint  ThrottledCount;
    public uint  ExternalPageCount;
    public uint  InternalPageCount;
    public ulong TotalUncompressedPagesInCompressor;
}

public sealed class MacOSSystemMetricsProvider : ISystemMetricsProvider, IDisposable
{
    // PROCESSOR_CPU_LOAD_INFO flavor, CPU_STATE_MAX slots per CPU
    private const int ProcessorCpuLoadInfo = 2;
    private const int CpuStateMax          = 4;
    private const int CpuStateUser         = 0;
    private const int CpuStateSys          = 1;
    private const int CpuStateIdle         = 2;
    private const int CpuStateNice         = 3;

    // HOST_VM_INFO64 flavor for host_statistics64() (mach/host_info.h)
    private const int HostVmInfo64 = 4;

    // ── sysctl helpers ─────────────────────────────────────────────────────────
    private static string SysctlString(string name)
    {
        nuint size = 256;
        var buf = new byte[size];
        return LibSystem.sysctlbyname(name, buf, ref size, nint.Zero, 0) == 0
            ? System.Text.Encoding.UTF8.GetString(buf, 0, (int)size).TrimEnd('\0')
            : string.Empty;
    }

    private static int SysctlInt(string name)
    {
        nuint size = 4;
        var buf = new byte[4];
        return LibSystem.sysctlbyname(name, buf, ref size, nint.Zero, 0) == 0
            ? BitConverter.ToInt32(buf, 0) : 0;
    }

    private static long SysctlLong(string name)
    {
        nuint size = 8;
        var buf = new byte[8];
        return LibSystem.sysctlbyname(name, buf, ref size, nint.Zero, 0) == 0
            ? BitConverter.ToInt64(buf, 0) : 0L;
    }

    // ── Subprocess helper ──────────────────────────────────────────────────────
    private static string RunCommand(string cmd, string args)
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
            if (!proc.WaitForExit(3000)) { proc.Kill(); return string.Empty; }
            return outputTask.Result;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Cached static data (read once at startup) ──────────────────────────────
    private readonly string _cpuModel;
    private readonly int    _physicalCores;
    private readonly int    _logicalCores;
    private readonly long   _totalMemBytes;
    private readonly int    _pageSize;
    // GPU identity from system_profiler — cached at startup; utilization unavailable on macOS
    private readonly IReadOnlyList<GpuMetrics> _staticGpuInfo;

    // ── Delta tracking for CPU, disk, and network rates ───────────────────────
    private long[]   _prevCpuTimes = [];
    private uint[][] _prevPerCoreTicks = [];   // [coreIndex][CpuStateMax]
    private DateTime _prevCpuTime = DateTime.MinValue;

    private readonly Dictionary<string, (long rxBytes, long txBytes)> _prevNetBytes = new();
    private DateTime _prevNetTime = DateTime.MinValue;

    // disk: { diskName -> (cumulativeReadBytes, cumulativeWriteBytes) }
    private readonly Dictionary<string, (long readBytes, long writeBytes)> _prevDiskBytes = new();
    private DateTime _prevDiskTime = DateTime.MinValue;

    // ioreg disk stats cache — ioreg is expensive; refresh every 10 seconds
    private Dictionary<string, (long readBytes, long writeBytes)> _cachedIoregStats = new();
    private DateTime _ioregCacheTime = DateTime.MinValue;
    private static readonly TimeSpan IoregCacheDuration = TimeSpan.FromSeconds(10);

    // df -k cache — real per-volume "used" bytes (see ReadDisks()); refresh every 10 seconds
    private Dictionary<string, long> _cachedDfUsedBytes = new(StringComparer.Ordinal);
    private DateTime _dfCacheTime = DateTime.MinValue;
    private static readonly TimeSpan DfCacheDuration = TimeSpan.FromSeconds(10);

    public MacOSSystemMetricsProvider()
    {
        _cpuModel      = SysctlString("machdep.cpu.brand_string");
        _physicalCores = SysctlInt("hw.physicalcpu");
        _logicalCores  = Environment.ProcessorCount;
        _totalMemBytes = SysctlLong("hw.memsize");
        _pageSize      = SysctlInt("hw.pagesize");

        if (_physicalCores <= 0) _physicalCores = _logicalCores;
        if (_pageSize <= 0)      _pageSize      = 4096;

        _staticGpuInfo = ReadGpuIdentityFromSystemProfiler();
    }

    /// <summary>
    /// Reads GPU name and VRAM from system_profiler at startup.
    /// Utilization is not available via any public macOS API.
    /// </summary>
    private static IReadOnlyList<GpuMetrics> ReadGpuIdentityFromSystemProfiler()
    {
        var result = new List<GpuMetrics>();
        try
        {
            var json = RunCommand("system_profiler", "SPDisplaysDataType -json");
            if (string.IsNullOrEmpty(json)) return result;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var displays))
                return result;

            foreach (var gpu in displays.EnumerateArray())
            {
                var name    = gpu.TryGetProperty("sppci_model",  out var n) ? n.GetString() ?? "" : "";
                var vramStr = gpu.TryGetProperty("sppci_vram",   out var v) ? v.GetString() ?? "" : "";
                long vramBytes = ParseVramString(vramStr);

                result.Add(new GpuMetrics
                {
                    Name                      = name,
                    UsagePercent              = 0,   // no public API on macOS
                    DedicatedMemoryUsedBytes  = 0,
                    DedicatedMemoryTotalBytes = vramBytes,
                    TemperatureCelsius        = 0,
                });
            }
        }
        catch { }
        return result;
    }

    private static long ParseVramString(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;
        // Use double.Parse so "499.96 GB" is not truncated to 499 GB (integer parse bug fix)
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var value)) return 0;
        return parts[1].Equals("TB", StringComparison.OrdinalIgnoreCase) ? (long)Math.Round(value * 1_099_511_627_776.0)
             : parts[1].Equals("GB", StringComparison.OrdinalIgnoreCase) ? (long)Math.Round(value * 1_073_741_824.0)
             : parts[1].Equals("MB", StringComparison.OrdinalIgnoreCase) ? (long)Math.Round(value * 1_048_576.0)
             : 0;
    }

    // Shared multicast observable
    private IObservable<SystemMetrics>? _shared;
    private TimeSpan _sharedInterval;
    private readonly object _sharedLock = new();
    private readonly object _metricsLock = new();
    private IDisposable? _connection;
    private bool _disposed;

    // ── ISystemMetricsProvider ─────────────────────────────────────────────────
    public IObservable<SystemMetrics> GetMetricsStream(TimeSpan interval)
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
                                            .Select(_ => BuildMetrics())
                                            .Publish();
                _shared     = connectable;
                _connection = connectable.Connect();
            }
            return _shared;
        }
    }

    public Task<SystemMetrics> GetMetricsAsync(CancellationToken ct = default) =>
        Task.FromResult(BuildMetrics());

    public void Dispose()
    {
        lock (_sharedLock)
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
        }
    }

    // ── Snapshot ───────────────────────────────────────────────────────────────
    private SystemMetrics BuildMetrics()
    {
        lock (_metricsLock)
        {
            var memory  = ReadMemory();
            var cpu     = ReadCpu();
            var disks   = ReadDisks();
            var nets    = ReadNetwork();

            return new SystemMetrics
            {
                Cpu             = cpu,
                Memory          = memory,
                Disks           = disks,
                NetworkAdapters = nets,
                Gpus            = _staticGpuInfo,
                Timestamp       = DateTime.UtcNow,
            };
        }
    }

    // ── Memory via sysctl (no subprocess) ─────────────────────────────────────

    // RAM slot info (speed, slots, form factor) never changes at runtime — read once.
    // Populated lazily on first ReadMemory() call.  On Apple Silicon system_profiler
    // SPMemoryDataType may return empty; fields stay at defaults (0 / "") if so.
    private int    _cachedMemSpeedMhz;
    private int    _cachedMemSlotsUsed;
    private string _cachedMemFormFactor = string.Empty;
    private bool   _ramSlotCachePopulated;

    private void EnsureRamSlotCache()
    {
        if (_ramSlotCachePopulated) return;
        _ramSlotCachePopulated = true;
        try
        {
            var json = RunCommand("system_profiler", "SPMemoryDataType -json");
            if (string.IsNullOrEmpty(json)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPMemoryDataType", out var memArray)) return;

            int slotsFound = 0;
            foreach (var bank in memArray.EnumerateArray())
            {
                var itemsProp = bank.TryGetProperty("_items", out var it)  ? it
                              : bank.TryGetProperty("Items",  out var it2) ? it2
                              : default;
                if (itemsProp.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

                foreach (var slot in itemsProp.EnumerateArray())
                {
                    slotsFound++;
                    if (_cachedMemSpeedMhz == 0)
                    {
                        var speedStr = slot.TryGetProperty("dimm_speed", out var sp) ? sp.GetString() ?? "" : "";
                        int.TryParse(speedStr.Replace(" MHz", "").Trim(), out _cachedMemSpeedMhz);
                    }
                    if (string.IsNullOrEmpty(_cachedMemFormFactor))
                        _cachedMemFormFactor = slot.TryGetProperty("dimm_type", out var t) ? t.GetString() ?? "" : "";
                }
            }
            _cachedMemSlotsUsed = slotsFound;
        }
        catch { /* leave at defaults */ }
    }

    private MemoryMetrics ReadMemory()
    {
        // Page counts via host_statistics64(HOST_VM_INFO64) — see the P/Invoke declaration
        // above for why sysctl can't be used for these four counters on this OS.
        var vmStats = ReadVmStatistics64();

        var activePages     = (long)vmStats.ActiveCount;
        var inactivePages   = (long)vmStats.InactiveCount;
        var wiredPages      = (long)vmStats.WireCount;
        var purgeablePages  = (long)vmStats.PurgeableCount;
        var compressorPages = (long)vmStats.CompressorPageCount;

        // Activity-Monitor-style "memory used": active + wired + compressed, minus
        // purgeable (purgeable pages are reclaimable on demand — the API surfaces them
        // separately, so they're excluded rather than counted as "used"). Cleanly
        // isolating file-backed pages further would require an active/inactive-scoped
        // internal/external split that HOST_VM_INFO64 doesn't provide (its
        // internal/external counts span active+inactive+wired together), so that
        // refinement isn't applied.
        var usedBytes = Math.Max(0L, (activePages + wiredPages + compressorPages - purgeablePages) * _pageSize);
        if (_totalMemBytes > 0 && usedBytes > _totalMemBytes) usedBytes = _totalMemBytes;

        // Available = total − used, so the two stay internally consistent.
        var availableBytes = Math.Max(0L, _totalMemBytes - usedBytes);
        var cachedBytes     = inactivePages * _pageSize;
        var compressedBytes = compressorPages * _pageSize;

        // CommitTotal approximation: wired + active + inactive pages.
        // CommitLimit: total installed RAM (hw.memsize).
        // macOS has no Windows-style commit charge; these are approximations only.
        // (Previously always 0 — wired/active/inactive were read via the nonexistent
        // sysctl OIDs above and silently defaulted to 0.)
        var commitTotalBytes = (wiredPages + activePages + inactivePages) * _pageSize;
        var commitLimitBytes = _totalMemBytes;

        // Populate RAM slot cache once (system_profiler SPMemoryDataType).
        // On Apple Silicon this may return empty — fields stay at defaults.
        EnsureRamSlotCache();

        return new MemoryMetrics
        {
            TotalBytes          = _totalMemBytes,
            AvailableBytes      = availableBytes,
            UsedBytes           = usedBytes,
            CachedBytes         = cachedBytes,
            PagedPoolBytes      = 0,   // no macOS equivalent
            NonPagedPoolBytes   = 0,   // no macOS equivalent
            CommitTotalBytes    = commitTotalBytes,  // approximation: wired+active+inactive pages
            CommitLimitBytes    = commitLimitBytes,  // approximation: total installed RAM
            CompressedBytes     = compressedBytes,   // compressor_page_count × pageSize
            SpeedMhz            = _cachedMemSpeedMhz,
            SlotsUsed           = _cachedMemSlotsUsed,
            FormFactor          = _cachedMemFormFactor,
        };
    }

    /// <summary>
    /// Reads current VM page counts via host_statistics64(HOST_VM_INFO64). Returns a
    /// zeroed struct if the Mach call fails (mirrors the old sysctl-failure fallback of 0).
    /// </summary>
    private static VmStatistics64 ReadVmStatistics64()
    {
        var count = (uint)(Marshal.SizeOf<VmStatistics64>() / sizeof(uint));
        var result = LibSystem.host_statistics64(LibSystem.HostSelf, HostVmInfo64, out var stats, ref count);
        return result == 0 ? stats : default;
    }

    // ── CPU via host_processor_info (per-core) + top for total ────────────────
    private CpuMetrics ReadCpu()
    {
        double totalPercent  = 0;
        double freqMhz       = 0;
        double[] corePercents = [];

        // Frequency via sysctl
        var freqHz = SysctlLong("hw.cpufrequency");
        if (freqHz == 0) freqHz = SysctlLong("hw.cpufrequency_max");
        if (freqHz > 0)  freqMhz = freqHz / 1_000_000.0;

        // Per-core CPU via Mach host_processor_info
        corePercents = ReadPerCoreCpu(out totalPercent);

        // If Mach API returned nothing, fall back to top -l 1
        if (corePercents.Length == 0)
            totalPercent = ReadTotalCpuFromTop();

        // L1/L2/L3 cache sizes via sysctl.  hw.l3cachesize is absent on Apple Silicon — returns 0.
        var l1CacheBytes = SysctlLong("hw.l1icachesize");
        var l2CacheBytes = SysctlLong("hw.l2cachesize");
        var l3CacheBytes = SysctlLong("hw.l3cachesize");   // 0 on Apple Silicon (key absent)

        // kern.num_threads: system-wide thread count.  Returns 0 if sysctl key unavailable.
        var threadCount = SysctlInt("kern.num_threads");

        return new CpuMetrics
        {
            TotalPercent       = totalPercent,
            CorePercents       = corePercents,
            FrequencyMhz       = freqMhz,
            TemperatureCelsius = 0,   // no public macOS API
            LogicalCores       = _logicalCores,
            PhysicalCores      = _physicalCores,
            ModelName          = _cpuModel,
            Sockets            = 1,   // always one package on Mac
            L1CacheBytes       = l1CacheBytes,
            L2CacheBytes       = l2CacheBytes,
            L3CacheBytes       = l3CacheBytes,
            ProcessCount       = System.Diagnostics.Process.GetProcesses().Length,
            ThreadCount        = threadCount,
        };
    }

    private double[] ReadPerCoreCpu(out double totalPercent)
    {
        totalPercent = 0;
        try
        {
            var host   = LibSystem.HostSelf;
            var result = LibSystem.host_processor_info(
                host,
                ProcessorCpuLoadInfo,
                out uint cpuCountRaw,
                out nint infoPtr,
                out uint infoCnt);

            if (result != 0 || infoPtr == nint.Zero || cpuCountRaw == 0)
                return [];

            int cpuCount = (int)cpuCountRaw;

            // infoCnt = cpuCount * CpuStateMax (4 uint32 per CPU)
            var corePercents = new double[cpuCount];
            long totalUser = 0, totalSys = 0, totalIdle = 0, totalNice = 0;

            // Resize/reset previous tick buffer if CPU count changed
            if (_prevPerCoreTicks.Length != cpuCount)
            {
                _prevPerCoreTicks = new uint[cpuCount][];
                for (int i = 0; i < cpuCount; i++)
                    _prevPerCoreTicks[i] = new uint[CpuStateMax];
            }

            for (int i = 0; i < cpuCount; i++)
            {
                var offset   = i * CpuStateMax;
                var user     = (uint)Marshal.ReadInt32(infoPtr, (offset + CpuStateUser) * 4);
                var sys      = (uint)Marshal.ReadInt32(infoPtr, (offset + CpuStateSys)  * 4);
                var idle     = (uint)Marshal.ReadInt32(infoPtr, (offset + CpuStateIdle) * 4);
                var nice     = (uint)Marshal.ReadInt32(infoPtr, (offset + CpuStateNice) * 4);

                var prevUser = _prevPerCoreTicks[i][CpuStateUser];
                var prevSys  = _prevPerCoreTicks[i][CpuStateSys];
                var prevIdle = _prevPerCoreTicks[i][CpuStateIdle];
                var prevNice = _prevPerCoreTicks[i][CpuStateNice];

                var dUser = user - prevUser;
                var dSys  = sys  - prevSys;
                var dIdle = idle - prevIdle;
                var dNice = nice - prevNice;
                var dTotal = (double)(dUser + dSys + dIdle + dNice);

                corePercents[i] = dTotal > 0
                    ? Math.Clamp((dUser + dSys + dNice) / dTotal * 100.0, 0, 100)
                    : 0;

                _prevPerCoreTicks[i][CpuStateUser] = user;
                _prevPerCoreTicks[i][CpuStateSys]  = sys;
                _prevPerCoreTicks[i][CpuStateIdle] = idle;
                _prevPerCoreTicks[i][CpuStateNice] = nice;

                totalUser += dUser;
                totalSys  += dSys;
                totalIdle += dIdle;
                totalNice += dNice;
            }

            // Free the Mach-allocated buffer
            LibSystem.vm_deallocate(
                LibSystem.TaskSelf,
                (nuint)infoPtr,
                (nuint)((ulong)infoCnt * 4u));

            var grandTotal = (double)(totalUser + totalSys + totalIdle + totalNice);
            totalPercent = grandTotal > 0
                ? Math.Clamp((totalUser + totalSys + totalNice) / grandTotal * 100.0, 0, 100)
                : 0;

            return corePercents;
        }
        catch
        {
            return [];
        }
    }

    private static double ReadTotalCpuFromTop()
    {
        var topOut = RunCommand("top", "-l 1 -stats pid,cpu -n 0");
        if (string.IsNullOrEmpty(topOut)) return 0;

        var cpuLine = topOut.Split('\n')
            .FirstOrDefault(l => l.Contains("CPU usage:", StringComparison.OrdinalIgnoreCase));
        if (cpuLine == null) return 0;

        double user = 0, sys = 0;
        ExtractPercent(cpuLine, "user", ref user);
        ExtractPercent(cpuLine, "sys",  ref sys);
        return Math.Clamp(user + sys, 0, 100);
    }

    private static void ExtractPercent(string line, string keyword, ref double value)
    {
        var idx = line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        int end = idx - 1;
        while (end >= 0 && line[end] == ' ') end--;
        if (end < 0) return;
        if (line[end] == '%') end--;
        int start = end;
        while (start > 0 && (char.IsDigit(line[start - 1]) || line[start - 1] == '.'))
            start--;
        if (double.TryParse(line[start..(end + 1)], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
            value = v;
    }

    // ── Disks via DriveInfo + statvfs, cached ioreg (I/O rates) + cached df (real used) ──
    private IReadOnlyList<DiskMetrics> ReadDisks()
    {
        var result  = new List<DiskMetrics>();
        var now     = DateTime.UtcNow;
        var elapsed = _prevDiskTime == DateTime.MinValue
            ? 1.0
            : Math.Max(0.1, (now - _prevDiskTime).TotalSeconds);

        // ioreg subprocess is expensive — cache it for 10 seconds
        if (now - _ioregCacheTime > IoregCacheDuration)
        {
            _cachedIoregStats = ReadIoregDiskStats();
            _ioregCacheTime   = now;
        }

        // `df -k` gives each mounted volume's real "used" bytes. On APFS, sibling volumes
        // in the same container (e.g. "/" and "/System/Volumes/Data") share f_blocks /
        // f_bavail at the statfs level — the same numbers DriveInfo.TotalSize /
        // AvailableFreeSpace read — so Total-minus-AvailableFreeSpace collapses to the
        // *container-wide* figure for every sibling volume and can't recover a given
        // volume's own usage (confirmed: os.statvfs("/") and os.statvfs("/System/Volumes/
        // Data") return byte-identical f_blocks/f_bavail on this machine). Only `df`
        // (backed by APFS's ATTR_VOL_SPACEUSED) exposes the real per-volume figure.
        if (now - _dfCacheTime > DfCacheDuration)
        {
            _cachedDfUsedBytes = ReadDfUsedBytesByMount();
            _dfCacheTime       = now;
        }

        // DriveInfo.GetDrives() uses getmntinfo() internally on macOS — no subprocess needed
        try
        {
            // First pass: candidate real volumes, filtering out pseudo filesystems and
            // APFS-internal special-role volumes that aren't meaningful "disks" to a user.
            var candidates = new List<DriveInfo>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory
                                     or DriveType.CDRom  or DriveType.Ram)
                    continue;   // Ram excludes devfs ("/dev") — a tiny always-100%-full pseudo-fs
                if (!drive.IsReady) continue;

                var mountPoint = drive.RootDirectory.FullName;

                // Skip APFS-internal special-role volumes (Preboot, VM, Update, xarts,
                // iSCPreboot, Hardware, ...): not user-meaningful disks, and on the boot
                // container their capacity already overlaps "/" / "/System/Volumes/Data".
                if (mountPoint.StartsWith("/System/Volumes/", StringComparison.Ordinal) &&
                    !mountPoint.Equals("/System/Volumes/Data", StringComparison.Ordinal) &&
                    !mountPoint.StartsWith("/System/Volumes/Data/", StringComparison.Ordinal))
                    continue;

                candidates.Add(drive);
            }

            // Dedupe the boot container's sealed System snapshot ("/") against the
            // writable Data volume it's paired with on every modern macOS install (the
            // Signed System Volume split) — getmntinfo() reports the same container twice.
            // Keep the Data entry: it carries the real, user-meaningful usage.
            var hasDataVolume = candidates.Any(d =>
                d.RootDirectory.FullName.Equals("/System/Volumes/Data", StringComparison.Ordinal));
            if (hasDataVolume)
                candidates.RemoveAll(d => d.RootDirectory.FullName == "/");

            int index = 0;
            foreach (var drive in candidates)
            {
                var mountPoint   = drive.RootDirectory.FullName;
                var isSystemDisk = mountPoint is "/" or "/System/Volumes/Data";

                long freeBytes  = drive.AvailableFreeSpace;   // f_bavail semantics — real writable space
                long totalBytes = drive.TotalSize;

                // Derive Total from df's real Used + our real Free, so the Core-computed
                // UsedPercent = (Total-Free)/Total lands on df's own Used/(Used+Avail)
                // ratio for this specific volume, instead of the container-wide ratio.
                // Falls back to the raw statfs total if df didn't report this mount.
                if (_cachedDfUsedBytes.TryGetValue(mountPoint, out var dfUsedBytes))
                    totalBytes = dfUsedBytes + freeBytes;

                // Match ioreg entry for I/O rates (disk0, disk1, ...)
                long readRate = 0, writeRate = 0;
                var  ioKey   = $"disk{index}";
                if (_cachedIoregStats.TryGetValue(ioKey, out var ioCur))
                {
                    if (_prevDiskBytes.TryGetValue(ioKey, out var ioPrev))
                    {
                        readRate  = (long)Math.Max(0, (ioCur.readBytes  - ioPrev.readBytes)  / elapsed);
                        writeRate = (long)Math.Max(0, (ioCur.writeBytes - ioPrev.writeBytes) / elapsed);
                    }
                    _prevDiskBytes[ioKey] = ioCur;
                }

                result.Add(new DiskMetrics
                {
                    DiskIndex        = index++,
                    DriveLetter      = mountPoint,
                    Label            = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : mountPoint,
                    PhysicalName     = mountPoint,
                    AllDriveLetters  = mountPoint,
                    TotalBytes       = totalBytes,
                    FreeBytes        = freeBytes,
                    ReadBytesPerSec  = readRate,
                    WriteBytesPerSec = writeRate,
                    ActivePercent    = 0,
                    IsSystemDisk     = isSystemDisk,
                });
            }
        }
        catch { }

        _prevDiskTime = now;
        // Prune stale entries for removed/unmounted disks
        var activeDisks = new HashSet<string>(result.Select(d => $"disk{d.DiskIndex}"));
        foreach (var key in _prevDiskBytes.Keys.Where(k => !activeDisks.Contains(k)).ToList())
            _prevDiskBytes.Remove(key);
        return result;
    }

    /// <summary>
    /// Parses `df -k` to get each mounted volume's real "used" bytes — the figure that
    /// Total-minus-AvailableFreeSpace cannot recover for sibling APFS volumes sharing one
    /// container (see ReadDisks()). Keyed by mount point (e.g. "/", "/System/Volumes/Data").
    /// </summary>
    private static Dictionary<string, long> ReadDfUsedBytesByMount()
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            var output = RunCommand("df", "-k");
            if (string.IsNullOrEmpty(output)) return map;

            var lines = output.Split('\n');
            for (int i = 1; i < lines.Length; i++)   // skip header row
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // Filesystem  1024-blocks  Used  Available  Capacity  iused  ifree  %iused  Mounted-on
                if (parts.Length < 9) continue;
                if (!long.TryParse(parts[2], out var usedKb)) continue;

                // Mount point is everything from column 8 onward (paths can contain spaces)
                var mountPoint = string.Join(' ', parts.Skip(8));
                if (mountPoint.Length == 0) continue;

                map[mountPoint] = usedKb * 1024L;
            }
        }
        catch { }
        return map;
    }

    /// <summary>Returns base disk name from a device path, e.g. "/dev/disk3s5" → "disk3".</summary>
    private static string ExtractBaseDiskName(string devPath)
    {
        // Strip /dev/ prefix
        var name = devPath.StartsWith("/dev/", StringComparison.Ordinal)
            ? devPath[5..]
            : devPath;
        // Strip partition suffix: search from end for 's' preceded by a digit and followed by digits
        // e.g. "disk3s5" → 's' at index 5, preceded by '3' (digit), followed by '5' (digit) → "disk3"
        // Avoids matching the 's' in "disk" (index 1, not preceded by a digit)
        for (int i = name.Length - 1; i > 0; i--)
        {
            if (name[i] == 's' && char.IsDigit(name[i - 1])
                && i < name.Length - 1 && char.IsDigit(name[i + 1]))
                return name[..i];
        }
        return name;
    }

    /// <summary>
    /// Parses `ioreg -c IOBlockStorageDriver -r -k Statistics` to get
    /// cumulative bytes read/written per disk (indexed as disk0, disk1, ...).
    /// </summary>
    private static Dictionary<string, (long readBytes, long writeBytes)> ReadIoregDiskStats()
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        try
        {
            var output = RunCommand("ioreg", "-c IOBlockStorageDriver -r -k Statistics");
            if (string.IsNullOrEmpty(output)) return map;

            int diskIndex = 0;
            long readBytes = 0, writeBytes = 0;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();

                // Start of a new driver entry
                if (trimmed.Contains("IOBlockStorageDriver", StringComparison.Ordinal) &&
                    trimmed.Contains("<class", StringComparison.Ordinal))
                {
                    // Save previous entry if valid
                    if (readBytes > 0 || writeBytes > 0)
                    {
                        map[$"disk{diskIndex}"] = (readBytes, writeBytes);
                        diskIndex++;
                    }
                    readBytes  = 0;
                    writeBytes = 0;
                    continue;
                }

                if (trimmed.StartsWith("\"Statistics\"", StringComparison.Ordinal))
                {
                    // Parse inline: "Statistics" = {"Bytes (Read)"=12345,...}
                    readBytes  = ParseIoregLong(trimmed, "\"Bytes (Read)\"=");
                    writeBytes = ParseIoregLong(trimmed, "\"Bytes (Write)\"=");
                }
            }

            // Last entry
            if (readBytes > 0 || writeBytes > 0)
                map[$"disk{diskIndex}"] = (readBytes, writeBytes);
        }
        catch { }

        return map;
    }

    private static long ParseIoregLong(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var rest  = text[(idx + key.Length)..];
        var end   = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return end > 0 && long.TryParse(rest[..end], out var v) ? v : 0;
    }

    // ── Network via NetworkInterface ───────────────────────────────────────────
    private IReadOnlyList<NetworkAdapterMetrics> ReadNetwork()
    {
        var result  = new List<NetworkAdapterMetrics>();
        var now     = DateTime.UtcNow;
        var elapsed = _prevNetTime == DateTime.MinValue
            ? 1.0
            : Math.Max(0.01, (now - _prevNetTime).TotalSeconds);

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var stats   = nic.GetIPStatistics();
                var rxBytes = stats.BytesReceived;
                var txBytes = stats.BytesSent;

                long rxRate = 0, txRate = 0;
                if (_prevNetBytes.TryGetValue(nic.Name, out var prev))
                {
                    rxRate = (long)Math.Max(0, (rxBytes - prev.rxBytes) / elapsed);
                    txRate = (long)Math.Max(0, (txBytes - prev.txBytes) / elapsed);
                }
                _prevNetBytes[nic.Name] = (rxBytes, txBytes);

                var ipv4 = string.Empty;
                var ipv6 = string.Empty;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ipv4 = ua.Address.ToString();
                    else if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        ipv6 = ua.Address.ToString();
                }

                // DNS suffix
                string dnsSuffix = string.Empty;
                try { dnsSuffix = nic.GetIPProperties().DnsSuffix ?? string.Empty; } catch { }

                // MAC address formatted with colons (e.g. "A1:B2:C3:D4:E5:F6")
                string macAddress = string.Empty;
                try
                {
                    var raw = nic.GetPhysicalAddress().ToString(); // "A1B2C3D4E5F6"
                    if (raw.Length == 12)
                    {
                        macAddress = string.Create(17, raw, static (span, src) =>
                        {
                            for (int i = 0, j = 0; i < 12; i += 2, j += 3)
                            {
                                span[j]     = src[i];
                                span[j + 1] = src[i + 1];
                                if (j + 2 < 17) span[j + 2] = ':';
                            }
                        });
                    }
                }
                catch { }

                // Connection type heuristic
                string connectionType = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet      => "Ethernet",
                    _ => nic.Name.StartsWith("utun",  StringComparison.OrdinalIgnoreCase) ||
                         nic.Name.StartsWith("ipsec", StringComparison.OrdinalIgnoreCase)
                         ? "VPN"
                         : nic.NetworkInterfaceType.ToString()
                };

                result.Add(new NetworkAdapterMetrics
                {
                    Name             = nic.Name,
                    Description      = nic.Description,
                    SendBytesPerSec  = txRate,
                    RecvBytesPerSec  = rxRate,
                    TotalSendBytes   = txBytes,
                    TotalRecvBytes   = rxBytes,
                    IsConnected      = nic.OperationalStatus == OperationalStatus.Up,
                    IpAddress        = ipv4,
                    IPv4Address      = ipv4,
                    IPv6Address      = ipv6,
                    LinkSpeedBps     = nic.Speed,
                    AdapterType      = nic.NetworkInterfaceType.ToString(),
                    DnsSuffix        = dnsSuffix,
                    MacAddress       = macAddress,
                    ConnectionType   = connectionType,
                });
            }
        }
        catch { }

        _prevNetTime = now;
        // Prune stale entries for removed adapters
        var activeNics = new HashSet<string>(result.Select(r => r.Name));
        foreach (var key in _prevNetBytes.Keys.Where(k => !activeNics.Contains(k)).ToList())
            _prevNetBytes.Remove(key);
        return result;
    }
}
