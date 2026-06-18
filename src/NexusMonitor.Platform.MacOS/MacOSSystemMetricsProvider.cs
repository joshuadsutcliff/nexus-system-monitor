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
    public static readonly int HostSelf = mach_host_self();

    // mach_task_self_ is a DATA export (the cached task-self Mach port), NOT a function —
    // `mach_task_self()` is a C macro that reads this global. P/Invoking it as a function
    // and calling it jumps into a data address and bus-errors (EXC_BAD_ACCESS /
    // KERN_PROTECTION_FAILURE). Read the symbol's value once and cache it (it never
    // changes for the lifetime of the process).
    public static readonly int TaskSelf = ReadTaskSelfPort();

    private static int ReadTaskSelfPort()
    {
        var handle = NativeLibrary.Load("libSystem.B.dylib");
        var addr   = NativeLibrary.GetExport(handle, "mach_task_self_");
        return Marshal.ReadInt32(addr);   // mach_port_t is a 32-bit unsigned int
    }

    [LibraryImport("libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sysctlbyname(
        string name, [Out] byte[] oldp, ref nuint oldlenp, nint newp, nuint newlen);

    [DllImport("libSystem.B.dylib")]
    public static extern int mach_host_self();

    [DllImport("libSystem.B.dylib")]
    public static extern int host_processor_info(
        int host,
        int flavor,
        out int processorCount,
        out nint processorInfo,
        out int processorInfoCount);

    [DllImport("libSystem.B.dylib")]
    public static extern int vm_deallocate(int task, nint address, nint size);

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
        if (parts.Length < 2 || !long.TryParse(parts[0], out var value)) return 0;
        return parts[1].Equals("GB", StringComparison.OrdinalIgnoreCase) ? value * 1_073_741_824L
             : parts[1].Equals("MB", StringComparison.OrdinalIgnoreCase) ? value * 1_048_576L
             : 0;
    }

    // Shared multicast observable
    private IObservable<SystemMetrics>? _shared;
    private TimeSpan _sharedInterval;
    private readonly object _sharedLock = new();
    private readonly object _metricsLock = new();
    private IDisposable? _connection;

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
        lock (_sharedLock) { _connection?.Dispose(); }
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
    private MemoryMetrics ReadMemory()
    {
        // All page counts available directly via sysctl — no vm_stat subprocess needed
        var freePages      = (long)(uint)SysctlInt("vm.page_free_count");
        var specPages      = (long)(uint)SysctlInt("vm.page_speculative_count");
        var inactPages     = (long)(uint)SysctlInt("vm.page_inactive_count");

        // Available = free + speculative (pages that can be reclaimed instantly)
        var availableBytes = (freePages + specPages) * _pageSize;
        var cachedBytes    = inactPages * _pageSize;

        var usedBytes = _totalMemBytes > 0
            ? Math.Max(0L, _totalMemBytes - availableBytes)
            : 0L;

        return new MemoryMetrics
        {
            TotalBytes          = _totalMemBytes,
            AvailableBytes      = availableBytes,
            UsedBytes           = usedBytes,
            CachedBytes         = cachedBytes,
            PagedPoolBytes      = 0,
            NonPagedPoolBytes   = 0,
            CommitTotalBytes    = 0,
            CommitLimitBytes    = 0,
        };
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

        return new CpuMetrics
        {
            TotalPercent       = totalPercent,
            CorePercents       = corePercents,
            FrequencyMhz       = freqMhz,
            TemperatureCelsius = 0,
            LogicalCores       = _logicalCores,
            PhysicalCores      = _physicalCores,
            ModelName          = _cpuModel,
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
                out int cpuCount,
                out nint infoPtr,
                out int infoCnt);

            if (result != 0 || infoPtr == nint.Zero || cpuCount <= 0)
                return [];

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
                infoPtr,
                (nint)(infoCnt * 4));

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

    // ── Disks via DriveInfo + statvfs (no df subprocess) + cached ioreg ─────────
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

        // DriveInfo.GetDrives() uses getmntinfo() internally on macOS — no subprocess needed
        try
        {
            int index = 0;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.CDRom)
                    continue;
                if (!drive.IsReady) continue;

                var mountPoint = drive.RootDirectory.FullName;
                long totalBytes = drive.TotalSize;
                long freeBytes  = drive.AvailableFreeSpace;

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
