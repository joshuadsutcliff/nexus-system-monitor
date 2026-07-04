using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Platform.Windows.Native;

namespace NexusMonitor.Platform.Windows;

/// <summary>
/// Real Windows system-metrics provider using PerformanceCounters, GlobalMemoryStatusEx
/// and the Registry.  Counters are initialised lazily on first call and "warm-up"
/// discarded (PerformanceCounter rate counters always return 0 on the first read).
/// </summary>
public sealed class WindowsSystemMetricsProvider : ISystemMetricsProvider, IDisposable
{
    // ─── Lazy-init state ──────────────────────────────────────────────────────

    private volatile bool _initialized;
    private readonly object _lock = new();

    // CPU
    private PerformanceCounter?   _cpuTotal = null;
    private PerformanceCounter[]  _cpuCores = [];
    private string _cpuModel    = string.Empty;
    private int    _logicalCores;
    private int    _physicalCores;

    // CPU — one-shot cached detail
    private double _cpuBaseSpeedMhz;
    private int    _cpuSockets = 1;
    private string _cpuVirtualization = string.Empty;
    private long   _cpuL1CacheBytes;
    private long   _cpuL2CacheBytes;
    private long   _cpuL3CacheBytes;

    // Hardware sensors (LibreHardwareMonitor) — CPU + GPU temperatures
    private Computer? _lhm;
    private readonly object _lhmLock = new();
    private double   _lastCpuTempC;
    private double   _lastGpuTempC;
    private DateTime _lastTempSample    = DateTime.MinValue;
    private static readonly TimeSpan TempCacheDuration = TimeSpan.FromSeconds(5);


    // CPU frequency — registry read; cache for 2 s (Windows updates ~every 1 s anyway)
    private double   _lastFreqMhz;
    private DateTime _lastFreqSample = DateTime.MinValue;
    private static readonly TimeSpan FreqCacheDuration = TimeSpan.FromSeconds(2);

    // Disk (per-disk counters)
    private (string instance, PerformanceCounter read, PerformanceCounter write, PerformanceCounter active,
             PerformanceCounter? avgResponse)[] _diskCounters = [];

    // Disk — one-shot cached detail (keyed by disk index)
    private Dictionary<int, string> _diskTypes = new();     // index → "SSD", "HDD", "NVMe"

    // Network (all active adapters)
    private (string instance, PerformanceCounter send, PerformanceCounter recv)[] _netCounters = [];
    // NetworkInterface metadata cache: static data refreshed every 30 s
    private System.Net.NetworkInformation.NetworkInterface[]? _cachedNics;
    private DateTime _nicsLastRefresh = DateTime.MinValue;
    private static readonly TimeSpan NicsCacheDuration = TimeSpan.FromSeconds(30);

    // Drive topology cache: refreshed every 60 s (drives rarely added/removed at runtime)
    private DriveInfo[]? _cachedDrives;
    private DateTime _drivesLastRefresh = DateTime.MinValue;
    private static readonly TimeSpan DrivesCacheDuration = TimeSpan.FromSeconds(60);

    // Memory — one-shot cached detail
    private int    _memSpeedMhz;
    private int    _memSlotsUsed;
    private int    _memTotalSlots;
    private string _memFormFactor = string.Empty;
    private long   _memWmiTotalBytes;       // WMI total physical (before hardware reservation)

    // GPU
    private PerformanceCounter[] _gpuEngineCounters = [];
    private PerformanceCounter[]? _gpuMemCounters;
    private PerformanceCounter[] _gpuCopyCounters   = [];
    private PerformanceCounter[] _gpuDecodeCounters  = [];
    private PerformanceCounter[] _gpuEncodeCounters  = [];
    private PerformanceCounter[] _gpuSharedCounters  = [];
    private string _gpuName       = string.Empty;
    private long   _gpuTotalVram  = 0;

    // GPU — one-shot cached detail
    private long   _gpuSharedTotalBytes;
    private string _gpuDriverVersion   = string.Empty;
    private string _gpuPhysicalLocation = string.Empty;

    // GPU — sample-result cache (2 s) + periodic re-enumeration (30 s)
    private IReadOnlyList<GpuMetrics>? _gpuCachedResult;
    private DateTime _gpuLastSampleTime      = DateTime.MinValue;
    private DateTime _gpuLastReenumerateTime = DateTime.MinValue;
    private static readonly TimeSpan GpuSampleCacheDuration    = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GpuReenumerateDuration    = TimeSpan.FromSeconds(30);

    // Shared multicast observable — all callers share one timer + one Sample() call per tick.
    // SelfHealingSharedStream wraps the timer+Publish() pattern with RetryWithBackoff so a
    // faulted Sample() call (e.g. a transient PerformanceCounter/WMI hiccup) is logged and
    // the timer is recreated after a short backoff instead of permanently killing the
    // multicast for every subscriber (see NexusMonitor.Core.Reactive.SelfHealingSharedStream).
    private SelfHealingSharedStream<SystemMetrics>? _shared;
    private TimeSpan _sharedInterval;
    private readonly object _sharedLock = new();

    // ─── ISystemMetricsProvider ───────────────────────────────────────────────

    public IObservable<SystemMetrics> GetMetricsStream(TimeSpan interval)
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
                _shared = new SelfHealingSharedStream<SystemMetrics>(
                    Sample, clampedInterval,
                    initialBackoff: TimeSpan.FromSeconds(1), maxBackoff: TimeSpan.FromSeconds(30));
            }
            return _shared.Stream;
        }
    }

    public Task<SystemMetrics> GetMetricsAsync(CancellationToken ct = default) =>
        Task.Run(Sample, ct);

    // ─── Sampling ─────────────────────────────────────────────────────────────

    private SystemMetrics Sample()
    {
        EnsureInitialized();
        // PERFORMANCE_INFORMATION is used by both SampleCpu and SampleMemory — call once here
        var pi = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        PsApi.GetPerformanceInfo(ref pi, pi.cb);
        return new SystemMetrics
        {
            Cpu            = SampleCpu(pi),
            Memory         = SampleMemory(pi),
            Disks          = SampleDisks(),
            NetworkAdapters= SampleNetwork(),
            Gpus           = SampleGpu(),
            Timestamp      = DateTime.UtcNow,
        };
    }

    private CpuMetrics SampleCpu(PERFORMANCE_INFORMATION pi)
    {
        double total = 0;
        try { total = SanitizeCounter(_cpuTotal?.NextValue() ?? 0); } catch { }

        var corePercents = new double[_cpuCores.Length];
        for (int i = 0; i < _cpuCores.Length; i++)
            try { corePercents[i] = SanitizeCounter(_cpuCores[i]?.NextValue() ?? 0); } catch { }

        // Update temperature cache (shared by SampleGpu via _lastGpuTempC)
        SampleTemperatures();

        return new CpuMetrics
        {
            TotalPercent          = Math.Round(total, 1),
            CorePercents          = corePercents,
            FrequencyMhz          = SampleCpuFrequencyMhz(),
            TemperatureCelsius    = _lastCpuTempC,
            LogicalCores          = _logicalCores,
            PhysicalCores         = _physicalCores,
            ModelName             = _cpuModel,
            BaseSpeedMhz          = _cpuBaseSpeedMhz,
            Sockets               = _cpuSockets,
            VirtualizationStatus  = _cpuVirtualization,
            L1CacheBytes          = _cpuL1CacheBytes,
            L2CacheBytes          = _cpuL2CacheBytes,
            L3CacheBytes          = _cpuL3CacheBytes,
            ProcessCount          = (int)pi.ProcessCount,
            ThreadCount           = (int)pi.ThreadCount,
            HandleCount           = (int)pi.HandleCount,
        };
    }

    private MemoryMetrics SampleMemory(PERFORMANCE_INFORMATION pi)
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        Kernel32.GlobalMemoryStatusEx(ref ms);

        long pageSize = (long)pi.PageSize;
        long totalBytes = (long)ms.ullTotalPhys;

        // Hardware reserved = WMI total (installed RAM) minus OS-visible RAM
        long hwReserved = _memWmiTotalBytes > totalBytes ? _memWmiTotalBytes - totalBytes : 0;

        return new MemoryMetrics
        {
            TotalBytes            = totalBytes,
            UsedBytes             = (long)(ms.ullTotalPhys - ms.ullAvailPhys),
            AvailableBytes        = (long)ms.ullAvailPhys,
            CachedBytes           = (long)pi.SystemCache * pageSize,
            PagedPoolBytes        = (long)pi.KernelPaged   * pageSize,
            NonPagedPoolBytes     = (long)pi.KernelNonpaged* pageSize,
            CommitTotalBytes      = (long)pi.CommitTotal   * pageSize,
            CommitLimitBytes      = (long)pi.CommitLimit   * pageSize,
            SpeedMhz              = _memSpeedMhz,
            SlotsUsed             = _memSlotsUsed,
            TotalSlots            = _memTotalSlots,
            FormFactor            = _memFormFactor,
            HardwareReservedBytes = hwReserved,
        };
    }

    private List<DiskMetrics> SampleDisks()
    {
        // Refresh drive topology at most every 60 s — drives rarely change at runtime
        var dNow = DateTime.UtcNow;
        if (_cachedDrives is null || (dNow - _drivesLastRefresh) >= DrivesCacheDuration)
        {
            _cachedDrives = DriveInfo.GetDrives()
                                     .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                                     .ToArray();
            _drivesLastRefresh = dNow;
        }
        var drives = _cachedDrives;
        var list = new List<DiskMetrics>();

        if (_diskCounters.Length == 0)
        {
            foreach (var d in drives)
                list.Add(new DiskMetrics
                {
                    DriveLetter = d.Name.TrimEnd('\\', '/'),
                    Label       = d.VolumeLabel,
                    TotalBytes  = d.TotalSize,
                    FreeBytes   = d.TotalFreeSpace,
                });
            return list.Count > 0 ? list : [new DiskMetrics { DriveLetter = "C:" }];
        }

        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\', '/').ToUpperInvariant();

        foreach (var (inst, readCtr, writeCtr, activeCtr, avgRespCtr) in _diskCounters)
        {
            double readBytes = 0, writeBytes = 0, activePercent = 0, avgRespSec = 0;
            try { readBytes    = SanitizeCounter(readCtr.NextValue());   } catch { }
            try { writeBytes   = SanitizeCounter(writeCtr.NextValue());  } catch { }
            try { activePercent= SanitizeCounter(activeCtr.NextValue()); } catch { }
            try { if (avgRespCtr != null) avgRespSec = SanitizeCounter(avgRespCtr.NextValue()); } catch { }

            var (idx, letters) = ParseDiskInstance(inst);
            string allLetters  = string.Join(" ", letters.Select(l => l + ":"));
            string firstLetter = letters.Length > 0 ? letters[0] + ":" : string.Empty;

            // Match drives belonging to this physical disk
            var matchedDrives = drives.Where(d =>
                letters.Any(l => d.Name.StartsWith(l, StringComparison.OrdinalIgnoreCase))).ToList();
            var drive = matchedDrives.FirstOrDefault();

            // IsSystemDisk: does this physical disk contain the system drive?
            bool isSystem = letters.Any(l =>
                systemDrive.StartsWith(l, StringComparison.OrdinalIgnoreCase));

            // HasPageFile: check if any volume on this disk has pagefile.sys
            bool hasPageFile = matchedDrives.Any(d =>
            {
                try { return File.Exists(Path.Combine(d.Name, "pagefile.sys")); }
                catch { return false; }
            });

            // Build volume list
            var volumes = matchedDrives.Select(d => new VolumeInfo
            {
                DriveLetter = d.Name.TrimEnd('\\', '/'),
                Label       = d.VolumeLabel,
                FileSystem  = d.DriveFormat,
                TotalBytes  = d.TotalSize,
                FreeBytes   = d.TotalFreeSpace,
            }).ToList();

            // Disk type from cached WMI
            _diskTypes.TryGetValue(idx, out string? diskType);

            list.Add(new DiskMetrics
            {
                DriveLetter      = firstLetter,
                Label            = drive?.VolumeLabel ?? string.Empty,
                DiskIndex        = idx,
                PhysicalName     = $"Physical Drive {idx}",
                AllDriveLetters  = allLetters,
                ReadBytesPerSec  = (long)readBytes,
                WriteBytesPerSec = (long)writeBytes,
                ActivePercent    = activePercent,
                TotalBytes       = drive?.TotalSize ?? 0,
                FreeBytes        = drive?.TotalFreeSpace ?? 0,
                DiskType         = diskType ?? string.Empty,
                AverageResponseMs= Math.Round(avgRespSec * 1000.0, 2),
                IsSystemDisk     = isSystem,
                HasPageFile      = hasPageFile,
                Volumes          = volumes,
            });
        }

        return list.Count > 0 ? list : drives.Select(d => new DiskMetrics
        {
            DriveLetter = d.Name.TrimEnd('\\', '/'),
            Label       = d.VolumeLabel,
            TotalBytes  = d.TotalSize,
            FreeBytes   = d.TotalFreeSpace,
        }).ToList();
    }

    private static (int index, string[] letters) ParseDiskInstance(string name)
    {
        // PDH instance names look like "0 C: D:" or "1 E:"
        var parts = name.Split(' ');
        int.TryParse(parts[0], out int idx);
        var letters = parts.Skip(1)
            .Where(p => p.Length == 2 && p[1] == ':')
            .Select(p => p[0].ToString().ToUpper())
            .ToArray();
        return (idx, letters);
    }

    private List<NetworkAdapterMetrics> SampleNetwork()
    {
        if (_netCounters.Length == 0) return [];

        // Refresh adapter metadata (IPs, type, speed, DNS) at most every 30 s — it's static
        var now = DateTime.UtcNow;
        if (_cachedNics is null || (now - _nicsLastRefresh) >= NicsCacheDuration)
        {
            _cachedNics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            _nicsLastRefresh = now;
        }
        var nics = _cachedNics;
        var list  = new List<NetworkAdapterMetrics>();

        foreach (var (inst, sendCtr, recvCtr) in _netCounters)
        {
            double send = 0, recv = 0;
            try { send = SanitizeCounter(sendCtr.NextValue()); } catch { }
            try { recv = SanitizeCounter(recvCtr.NextValue()); } catch { }

            // Fuzzy-match PDH instance name to a NetworkInterface (PDH replaces / with _)
            var nic = nics.FirstOrDefault(n =>
            {
                var norm = n.Description.Replace("/", "_").Replace("(", "[").Replace(")", "]");
                return inst.Equals(norm, StringComparison.OrdinalIgnoreCase)
                    || (norm.Length >= 20 && inst.Contains(norm[..20], StringComparison.OrdinalIgnoreCase));
            });

            string ipv4 = string.Empty, ipv6 = string.Empty, type = string.Empty;
            string dnsSuffix = string.Empty, connType = string.Empty;
            long speed = 0;
            if (nic != null)
            {
                try
                {
                    var ip = nic.GetIPProperties();
                    ipv4 = ip.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()).FirstOrDefault() ?? string.Empty;
                    ipv6 = ip.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .Select(a => a.Address.ToString()).FirstOrDefault() ?? string.Empty;
                    speed = nic.Speed;
                    dnsSuffix = ip.DnsSuffix ?? string.Empty;
                    bool isWifi = nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211;
                    type     = isWifi ? "IEEE 802.11" : "Ethernet";
                    connType = isWifi ? "Wi-Fi" : nic.NetworkInterfaceType switch
                    {
                        System.Net.NetworkInformation.NetworkInterfaceType.Ethernet  => "Ethernet",
                        System.Net.NetworkInformation.NetworkInterfaceType.Ppp       => "Cellular",
                        System.Net.NetworkInformation.NetworkInterfaceType.Wwanpp    => "Cellular",
                        System.Net.NetworkInformation.NetworkInterfaceType.Wwanpp2   => "Cellular",
                        _ => nic.NetworkInterfaceType.ToString()
                    };
                }
                catch { }
            }

            list.Add(new NetworkAdapterMetrics
            {
                Name            = nic?.Name ?? inst,
                Description     = nic?.Description ?? inst,
                SendBytesPerSec = (long)send,
                RecvBytesPerSec = (long)recv,
                IsConnected     = nic?.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up,
                IPv4Address     = ipv4,
                IPv6Address     = ipv6,
                LinkSpeedBps    = speed,
                AdapterType     = type,
                DnsSuffix       = dnsSuffix,
                ConnectionType  = connType,
            });
        }

        return list;
    }

    // ─── Lazy initialisation ──────────────────────────────────────────────────

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            InitCounters();
            _initialized = true;
        }
    }

    private void InitCounters()
    {
        // ── CPU model + base speed from registry ────────────────────────────────
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpuModel = ((key?.GetValue("ProcessorNameString") as string) ?? "Unknown CPU").Trim();
            _cpuBaseSpeedMhz = Convert.ToDouble(key?.GetValue("~MHz") ?? 0);
        }
        catch { _cpuModel = "Unknown CPU"; }

        _logicalCores  = Environment.ProcessorCount;
        _physicalCores = GetPhysicalCoreCount();

        // ── CPU one-shot WMI (sockets, virtualization, cache) ────────────────
        InitCpuWmiCache();

        // ── Memory one-shot WMI (speed, slots, form factor) ─────────────────
        InitMemoryWmiCache();

        // ── CPU total counter ─────────────────────────────────────────────────
        try
        {
            _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuTotal.NextValue(); // warm-up (always returns 0 on first call)
        }
        catch { _cpuTotal = null; }

        // ── Per-core counters ─────────────────────────────────────────────────
        try
        {
            var coreList = new List<PerformanceCounter>();
            for (int i = 0; i < _logicalCores; i++)
            {
                try
                {
                    var c = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), readOnly: true);
                    c.NextValue(); // warm-up
                    coreList.Add(c);
                }
                catch { coreList.Add(null!); }
            }
            _cpuCores = coreList.ToArray();
        }
        catch { _cpuCores = []; }

        // ── Per-disk counters ─────────────────────────────────────────────────
        try
        {
            var diskInstances = new PerformanceCounterCategory("PhysicalDisk")
                .GetInstanceNames()
                .Where(n => n != "_Total")
                .OrderBy(n => n)
                .ToArray();
            var diskList = new List<(string, PerformanceCounter, PerformanceCounter, PerformanceCounter, PerformanceCounter?)>();
            foreach (var inst in diskInstances)
            {
                try
                {
                    var r = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",  inst, readOnly: true);
                    var w = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, readOnly: true);
                    var a = new PerformanceCounter("PhysicalDisk", "% Disk Time",          inst, readOnly: true);
                    PerformanceCounter? ar = null;
                    try
                    {
                        ar = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", inst, readOnly: true);
                        ar.NextValue();
                    }
                    catch { ar = null; }
                    r.NextValue(); w.NextValue(); a.NextValue();
                    diskList.Add((inst, r, w, a, ar));
                }
                catch { }
            }
            _diskCounters = diskList.ToArray();
        }
        catch { _diskCounters = []; }

        // ── Disk one-shot WMI (media type) ──────────────────────────────────
        InitDiskWmiCache();

        // ── Network counters (all active adapters) ────────────────────────────
        try
        {
            var netInstances = new PerformanceCounterCategory("Network Interface")
                .GetInstanceNames()
                .Where(n => !n.Contains("Loopback",        StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("Pseudo-Interface", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var netList = new List<(string, PerformanceCounter, PerformanceCounter)>();
            foreach (var inst in netInstances)
            {
                try
                {
                    var s = new PerformanceCounter("Network Interface", "Bytes Sent/sec",     inst, readOnly: true);
                    var r = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst, readOnly: true);
                    s.NextValue(); r.NextValue();
                    netList.Add((inst, s, r));
                }
                catch { }
            }
            _netCounters = netList.ToArray();
        }
        catch { _netCounters = []; }

        // ── GPU ───────────────────────────────────────────────────────────────
        InitGpu();

        // ── Hardware sensors (LibreHardwareMonitor) ───────────────────────────
        // Must come after InitGpu so _gpuName is set for matching.
        // Runs on a background thread to avoid blocking the first Sample() call.
        System.Threading.ThreadPool.QueueUserWorkItem(_ => InitLhm());
    }

    private void InitGpu()
    {
        // 1. WMI: name + total VRAM + driver + location (runs once; slow but acceptable at startup)
        // Iterate all GPUs and pick the one with the most VRAM so that on systems with both
        // an iGPU and a dGPU (e.g. Ryzen iGPU + RTX) we show the discrete adapter.
        // Note: AdapterRAM is a uint32 field capped at ~4 GB — PDH step below corrects this.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID FROM Win32_VideoController WHERE VideoProcessor <> NULL");
            using var results = searcher.Get();
            string bestName = string.Empty, bestDriver = string.Empty, bestPnp = string.Empty;
            long bestVram = -1;
            foreach (ManagementBaseObject obj in results)
            using (obj)
            {
                long   vram   = Convert.ToInt64(obj["AdapterRAM"] ?? 0L);
                string name   = (obj["Name"]          as string ?? string.Empty).Trim();
                string driver = (obj["DriverVersion"] as string ?? string.Empty).Trim();
                string pnp    = (obj["PNPDeviceID"]   as string ?? string.Empty).Trim();
                if (vram > bestVram)
                {
                    bestVram   = vram;
                    bestName   = name;
                    bestDriver = driver;
                    bestPnp    = pnp;
                }
            }
            _gpuName             = bestName;
            _gpuTotalVram        = Math.Max(0, bestVram);
            _gpuDriverVersion    = bestDriver;
            _gpuPhysicalLocation = bestPnp;
        }
        catch { _gpuName = string.Empty; }

        // 2. PDH: GPU Engine utilization (Windows 10 1803+, all GPU vendors)
        // Enumerate instances once and reuse across all engine-type counter sets
        string[] gpuEngineInstances = [];
        try { gpuEngineInstances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames(); }
        catch { }

        _gpuEngineCounters = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_3D");
        _gpuCopyCounters   = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_Copy");
        _gpuDecodeCounters = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_VideoDecode");
        _gpuEncodeCounters = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_VideoEncode");

        // GPU adapter memory: enumerate instances ONCE for both dedicated + shared
        string[] memInstances = [];
        try { memInstances = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames(); }
        catch { }

        _gpuMemCounters    = InitAdapterMemCounters(memInstances, "Dedicated Usage");
        _gpuSharedCounters = InitAdapterMemCounters(memInstances, "Shared Usage");

        // Shared total (read once from "Shared Limit" counter)
        foreach (var inst in memInstances)
        {
            try
            {
                using var lim = new PerformanceCounter("GPU Adapter Memory", "Shared Limit", inst, readOnly: true);
                _gpuSharedTotalBytes += (long)lim.NextValue();
            }
            catch { }
        }

        // Dedicated VRAM total (read once from "Dedicated Limit" counter).
        // PDH stores this as a 64-bit value — no 4 GB cap unlike WMI AdapterRAM (uint32).
        // Take the maximum across all adapter instances to match the discrete GPU selected above.
        long pdhDedicatedMax = 0;
        foreach (var inst in memInstances)
        {
            try
            {
                using var lim = new PerformanceCounter("GPU Adapter Memory", "Dedicated Limit", inst, readOnly: true);
                long val = (long)lim.NextValue();
                if (val > pdhDedicatedMax) pdhDedicatedMax = val;
            }
            catch { }
        }
        if (pdhDedicatedMax > _gpuTotalVram)
            _gpuTotalVram = pdhDedicatedMax;

        _gpuLastReenumerateTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Reads current CPU clock speed from HKLM\…\CentralProcessor\0\~MHz.
    /// Windows updates this value every second; much faster than WMI.
    /// Result is cached for <see cref="FreqCacheDuration"/> to avoid opening
    /// the registry key on every metrics tick.
    /// </summary>
    private double SampleCpuFrequencyMhz()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFreqSample) < FreqCacheDuration)
            return _lastFreqMhz;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _lastFreqMhz   = System.Convert.ToDouble(key?.GetValue("~MHz") ?? 0);
            _lastFreqSample = now;
            return _lastFreqMhz;
        }
        catch
        {
            _lastFreqSample = now;
            return _lastFreqMhz;
        }
    }

    /// <summary>
    /// Opens the LibreHardwareMonitor Computer object on a background thread.
    /// Safe to call before _lhm is ready — SampleTemperatures() returns cached 0
    /// until the first successful update completes.
    /// </summary>
    private void InitLhm()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
            };
            computer.Open();
            lock (_lhmLock)
                _lhm = computer;
        }
        catch { /* sensors unavailable — fall through to 0 */ }
    }

    /// <summary>
    /// Reads CPU and GPU temperatures from LibreHardwareMonitor sensors.
    /// Caches results for <see cref="TempCacheDuration"/> to avoid polling on every tick.
    /// Falls back to 0 if LHM is not yet initialised or sensors are unavailable.
    /// </summary>
    private void SampleTemperatures()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTempSample) < TempCacheDuration) return;
        _lastTempSample = now;

        Computer? lhm;
        lock (_lhmLock) lhm = _lhm;
        if (lhm is null) return;

        double cpuMax = 0, gpuMax = 0;
        try
        {
            foreach (var hw in lhm.Hardware)
            {
                hw.Update();
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                        {
                            // Prefer the aggregate "CPU Package" / "Core Max" sensor
                            bool isPackage = sensor.Name.Contains("Package",   StringComparison.OrdinalIgnoreCase)
                                          || sensor.Name.Contains("Core Max",  StringComparison.OrdinalIgnoreCase)
                                          || sensor.Name.Contains("CCD",       StringComparison.OrdinalIgnoreCase);
                            double v = sensor.Value.Value;
                            if (isPackage)
                            {
                                cpuMax = v;   // prefer package/CCD sensor — stop checking others
                                break;
                            }
                            if (v > cpuMax) cpuMax = v;
                        }
                    }
                    // Walk sub-hardware (e.g. chiplets on multi-CCD Ryzen)
                    foreach (var sub in hw.SubHardware)
                    {
                        sub.Update();
                        foreach (var sensor in sub.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                double v = sensor.Value.Value;
                                if (v > cpuMax) cpuMax = v;
                            }
                        }
                    }
                }
                else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                        {
                            double v = sensor.Value.Value;
                            if (v > gpuMax) gpuMax = v;
                        }
                    }
                }
            }
        }
        catch { /* keep previous cached values */ }

        if (cpuMax > 1) _lastCpuTempC = Math.Round(cpuMax, 1);
        if (gpuMax > 1) _lastGpuTempC = Math.Round(gpuMax, 1);
    }

    // ─── One-shot WMI cache helpers ────────────────────────────────────────────

    private void InitCpuWmiCache()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT NumberOfCores, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled, SocketDesignation " +
                "FROM Win32_Processor");
            using var results = searcher.Get();
            var sockets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementBaseObject obj in results)
            using (obj)
            {
                var sock = obj["SocketDesignation"] as string ?? "Socket";
                sockets.Add(sock);

                // L2/L3 are in KB in WMI
                _cpuL2CacheBytes = Convert.ToInt64(obj["L2CacheSize"] ?? 0) * 1024L;
                _cpuL3CacheBytes = Convert.ToInt64(obj["L3CacheSize"] ?? 0) * 1024L;

                try
                {
                    bool virtEnabled = Convert.ToBoolean(obj["VirtualizationFirmwareEnabled"] ?? false);
                    _cpuVirtualization = virtEnabled ? "Enabled" : "Disabled";
                }
                catch { _cpuVirtualization = "Not available"; }
            }
            _cpuSockets = Math.Max(1, sockets.Count);
        }
        catch { /* WMI unavailable — keep defaults */ }

        // L1 cache: use GetLogicalProcessorInformation or estimate
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT MaxCacheSize, Level FROM Win32_CacheMemory WHERE Level = 3");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            using (obj)
            {
                // Level 3 in WMI = L1 data cache (WMI Level encoding: 3=L1, 4=L2, 5=L3)
                _cpuL1CacheBytes = Convert.ToInt64(obj["MaxCacheSize"] ?? 0) * 1024L;
                break;
            }
        }
        catch { /* L1 info unavailable */ }
    }

    private void InitMemoryWmiCache()
    {
        // Physical memory sticks: speed + form factor + count
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Speed, FormFactor, Capacity FROM Win32_PhysicalMemory");
            using var results = searcher.Get();
            int slotsUsed = 0;
            long wmiTotal = 0;
            foreach (ManagementBaseObject obj in results)
            using (obj)
            {
                slotsUsed++;
                if (_memSpeedMhz == 0)
                    _memSpeedMhz = Convert.ToInt32(obj["Speed"] ?? 0);
                if (string.IsNullOrEmpty(_memFormFactor))
                    _memFormFactor = MapMemoryFormFactor(Convert.ToInt32(obj["FormFactor"] ?? 0));
                wmiTotal += Convert.ToInt64(obj["Capacity"] ?? 0);
            }
            _memSlotsUsed = slotsUsed;
            _memWmiTotalBytes = wmiTotal;
        }
        catch { /* WMI unavailable */ }

        // Total physical slots
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            using (obj)
            {
                _memTotalSlots += Convert.ToInt32(obj["MemoryDevices"] ?? 0);
            }
        }
        catch { /* fallback: _memTotalSlots stays 0 */ }
    }

    private void InitDiskWmiCache()
    {
        try
        {
            // Try MSFT_PhysicalDisk (Storage cmdlet namespace) for best media type info
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            using (obj)
            {
                var devId = (obj["DeviceId"] as string ?? string.Empty).Trim();
                if (int.TryParse(devId, out int idx))
                {
                    int mediaType = Convert.ToInt32(obj["MediaType"] ?? 0);
                    _diskTypes[idx] = mediaType switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM", // Storage Class Memory
                        _ => "Unknown"
                    };
                }
            }
        }
        catch
        {
            // Fallback: try Win32_DiskDrive
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Index, MediaType, Model FROM Win32_DiskDrive");
                using var results = searcher.Get();
                foreach (ManagementBaseObject obj in results)
                using (obj)
                {
                    int idx = Convert.ToInt32(obj["Index"] ?? -1);
                    var mt  = (obj["MediaType"] as string ?? string.Empty).ToLowerInvariant();
                    var mdl = (obj["Model"]     as string ?? string.Empty).ToLowerInvariant();
                    string type = "Unknown";
                    if (mt.Contains("ssd") || mdl.Contains("nvme") || mdl.Contains("ssd"))
                        type = mdl.Contains("nvme") ? "NVMe" : "SSD";
                    else if (mt.Contains("fixed") || mt.Contains("hdd"))
                        type = "HDD";
                    if (idx >= 0) _diskTypes[idx] = type;
                }
            }
            catch { /* no disk type info available */ }
        }
    }

    private static string MapMemoryFormFactor(int code) => code switch
    {
        8  => "DIMM",
        12 => "SODIMM",
        9  => "SIMM",
        13 => "RIMM",
        15 => "FB-DIMM",
        _  => code == 0 ? "Unknown" : $"Type {code}"
    };

    /// <summary>
    /// Sums GPU engine utilization counters and caps at 100%.
    /// Returns an empty list if no GPU was detected at init time.
    /// Results are cached for 2 seconds; counters are re-enumerated every 30 seconds
    /// to drop stale per-PID instances and pick up new ones.
    /// </summary>
    private IReadOnlyList<GpuMetrics> SampleGpu()
    {
        if (string.IsNullOrEmpty(_gpuName)) return [];

        var now = DateTime.UtcNow;

        // Periodic re-enumeration: drop stale per-PID counters, add new ones
        if ((now - _gpuLastReenumerateTime) >= GpuReenumerateDuration)
        {
            foreach (var c in _gpuEngineCounters) c?.Dispose();
            foreach (var c in _gpuCopyCounters)   c?.Dispose();
            foreach (var c in _gpuDecodeCounters)  c?.Dispose();
            foreach (var c in _gpuEncodeCounters)  c?.Dispose();
            if (_gpuMemCounters != null) foreach (var c in _gpuMemCounters) c?.Dispose();
            foreach (var c in _gpuSharedCounters)  c?.Dispose();

            // Single enumeration of GPU Engine category
            string[] gpuEngineInstances = [];
            try { gpuEngineInstances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames(); } catch { }

            _gpuEngineCounters = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_3D");
            _gpuCopyCounters   = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_Copy");
            _gpuDecodeCounters = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_VideoDecode");
            _gpuEncodeCounters = InitEngineCountersFromInstances(gpuEngineInstances, "engtype_VideoEncode");

            // Re-enumerate GPU Adapter Memory once
            string[] memInstances = [];
            try { memInstances = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames(); } catch { }
            _gpuMemCounters = InitAdapterMemCounters(memInstances, "Dedicated Usage");
            _gpuSharedCounters = InitAdapterMemCounters(memInstances, "Shared Usage");

            _gpuLastReenumerateTime = now;
            _gpuCachedResult = null; // force fresh sample after re-enum
        }

        // Return cached result if still fresh
        if (_gpuCachedResult is not null && (now - _gpuLastSampleTime) < GpuSampleCacheDuration)
            return _gpuCachedResult;

        double usage3D = 0, usageCopy = 0, usageDecode = 0, usageEncode = 0;
        foreach (var c in _gpuEngineCounters)  try { usage3D     += SanitizeCounter(c.NextValue()); } catch { }
        foreach (var c in _gpuCopyCounters)    try { usageCopy   += SanitizeCounter(c.NextValue()); } catch { }
        foreach (var c in _gpuDecodeCounters)  try { usageDecode += SanitizeCounter(c.NextValue()); } catch { }
        foreach (var c in _gpuEncodeCounters)  try { usageEncode += SanitizeCounter(c.NextValue()); } catch { }
        usage3D     = Math.Clamp(usage3D,     0, 100);
        usageCopy   = Math.Clamp(usageCopy,   0, 100);
        usageDecode = Math.Clamp(usageDecode, 0, 100);
        usageEncode = Math.Clamp(usageEncode, 0, 100);

        long dedicatedUsed = 0;
        if (_gpuMemCounters is { Length: > 0 })
            foreach (var c in _gpuMemCounters)
                try { dedicatedUsed += (long)SanitizeCounter(c.NextValue()); } catch { }

        long sharedUsed = 0;
        if (_gpuSharedCounters is { Length: > 0 })
            foreach (var c in _gpuSharedCounters)
                try { sharedUsed += (long)SanitizeCounter(c.NextValue()); } catch { }

        _gpuCachedResult = [
            new GpuMetrics
            {
                Name                      = _gpuName,
                UsagePercent              = Math.Round(usage3D, 1),
                DedicatedMemoryTotalBytes = _gpuTotalVram,
                DedicatedMemoryUsedBytes  = dedicatedUsed,
                SharedMemoryUsedBytes     = sharedUsed,
                SharedMemoryTotalBytes    = _gpuSharedTotalBytes,
                TemperatureCelsius        = _lastGpuTempC,
                Engine3DPercent           = Math.Round(usage3D,     1),
                EngineCopyPercent         = Math.Round(usageCopy,   1),
                EngineVideoDecodePercent  = Math.Round(usageDecode, 1),
                EngineVideoEncodePercent  = Math.Round(usageEncode, 1),
                DriverVersion             = _gpuDriverVersion,
                DirectXVersion            = "DirectX 12",
                PhysicalLocation          = _gpuPhysicalLocation,
            }
        ];
        _gpuLastSampleTime = now;
        return _gpuCachedResult;
    }

    private static PerformanceCounter[] InitEngineCountersFromInstances(string[] instances, string engineType)
    {
        var counters = new List<PerformanceCounter>();
        foreach (var inst in instances)
        {
            if (!inst.Contains(engineType, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                c.NextValue();
                counters.Add(c);
            }
            catch { }
        }
        return counters.ToArray();
    }

    private static PerformanceCounter[] InitAdapterMemCounters(string[] instances, string counterName)
    {
        var counters = new List<PerformanceCounter>();
        foreach (var inst in instances)
        {
            try
            {
                var c = new PerformanceCounter("GPU Adapter Memory", counterName, inst, readOnly: true);
                c.NextValue();
                counters.Add(c);
            }
            catch { }
        }
        return counters.ToArray();
    }

    private static int GetPhysicalCoreCount()
    {
        // Registry keys are per logical processor, not per physical core.
        // Use Win32_Processor.NumberOfCores which correctly reports physical cores.
        try
        {
            int total = 0;
            using var searcher = new ManagementObjectSearcher(
                "SELECT NumberOfCores FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            using (obj)
                total += Convert.ToInt32(obj["NumberOfCores"] ?? 0);
            if (total > 0) return total;
        }
        catch { }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    private static float SanitizeCounter(float val) =>
        float.IsNaN(val) || float.IsInfinity(val) ? 0f : val;

    // ─── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        lock (_sharedLock) { _shared?.Dispose(); }
        lock (_lhmLock) { try { _lhm?.Close(); } catch { } _lhm = null; }
        _cpuTotal?.Dispose();
        foreach (var c in _cpuCores) c?.Dispose();
        foreach (var (_, r, w, a, ar) in _diskCounters) { r?.Dispose(); w?.Dispose(); a?.Dispose(); ar?.Dispose(); }
        foreach (var (_, s, r) in _netCounters) { s?.Dispose(); r?.Dispose(); }
        foreach (var c in _gpuEngineCounters) c?.Dispose();
        if (_gpuMemCounters != null) foreach (var c in _gpuMemCounters) c?.Dispose();
        foreach (var c in _gpuCopyCounters)   c?.Dispose();
        foreach (var c in _gpuDecodeCounters)  c?.Dispose();
        foreach (var c in _gpuEncodeCounters)  c?.Dispose();
        foreach (var c in _gpuSharedCounters)  c?.Dispose();
    }
}
