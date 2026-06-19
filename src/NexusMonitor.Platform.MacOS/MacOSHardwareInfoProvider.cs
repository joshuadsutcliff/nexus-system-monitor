using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Reads hardware information on macOS using sysctl and system_profiler.
/// Subprocess results are cached — hardware doesn't change at runtime.
/// </summary>
public sealed class MacOSHardwareInfoProvider
{
    public Task<SystemHardwareInfo> QueryAsync(CancellationToken ct = default) =>
        Task.Run(BuildInfo, ct);

    private static SystemHardwareInfo BuildInfo()
    {
        var cpuName      = SysctlString("machdep.cpu.brand_string");
        var physCores    = SysctlInt("hw.physicalcpu");
        var logCores     = Environment.ProcessorCount;
        var maxFreqHz    = SysctlLong("hw.cpufrequency_max");
        var maxFreqMhz   = maxFreqHz > 0 ? maxFreqHz / 1_000_000.0 : 0;
        var totalMemBytes= SysctlLong("hw.memsize");
        var model        = SysctlString("hw.model");
        var uptime       = ReadUptime();

        if (physCores <= 0) physCores = logCores;

        // CPU Stepping: sysctl machdep.cpu.stepping (Intel only; returns 0/empty on Apple Silicon)
        var steppingVal = SysctlInt("machdep.cpu.stepping");
        var stepping    = steppingVal > 0 ? steppingVal.ToString() : string.Empty;

        var cpu = new CpuHardwareInfo(
            Name:          cpuName.Length > 0 ? cpuName : model,
            Architecture:  RuntimeInformation.OSArchitecture.ToString(),
            PhysicalCores: physCores,
            LogicalCores:  logCores,
            L2CacheKB:     (int)(SysctlLong("hw.l2cachesize") / 1024),
            L3CacheKB:     (int)(SysctlLong("hw.l3cachesize") / 1024),
            MaxClockMhz:   maxFreqMhz,
            Socket:        model,
            Stepping:      stepping);

        var gpus    = ReadGpus();
        var storage = ReadStorage();
        var ramSlots= ReadRamSlots();

        var biosVersion = ReadBiosVersion();

        return new SystemHardwareInfo(
            Hostname:                Environment.MachineName,
            OsName:                  RuntimeInformation.OSDescription,
            OsBuild:                 Environment.OSVersion.ToString(),
            OsArchitecture:          RuntimeInformation.OSArchitecture.ToString(),
            Uptime:                  uptime,
            BiosVendor:              "Apple",
            BiosVersion:             biosVersion,
            MotherboardManufacturer: "Apple",
            MotherboardModel:        model,
            Cpu:                     cpu,
            TotalRamBytes:           totalMemBytes,
            RamSlots:                ramSlots,
            Gpus:                    gpus,
            Storage:                 storage);
    }

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

    // ── BIOS / firmware version ────────────────────────────────────────────────

    /// <summary>
    /// Reads the firmware version from system_profiler SPHardwareDataType -json.
    /// Prefers boot_rom_version; falls back to firmware_version if present.
    /// Returns empty string if unavailable.
    /// </summary>
    private static string ReadBiosVersion()
    {
        try
        {
            var json = RunCommand("system_profiler", "SPHardwareDataType -json");
            if (string.IsNullOrEmpty(json)) return string.Empty;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPHardwareDataType", out var hwArray)) return string.Empty;

            foreach (var hw in hwArray.EnumerateArray())
            {
                if (hw.TryGetProperty("boot_rom_version", out var brv))
                {
                    var v = brv.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                if (hw.TryGetProperty("firmware_version", out var fv))
                {
                    var v = fv.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
        }
        catch { }
        return string.Empty;
    }

    // ── Uptime ────────────────────────────────────────────────────────────────

    private static TimeSpan ReadUptime()
    {
        try
        {
            nuint size = 16;
            var buf = new byte[size];
            // kern.boottime returns a struct timeval (tv_sec, tv_usec) as 8+8 bytes on 64-bit
            if (LibSystem.sysctlbyname("kern.boottime", buf, ref size, nint.Zero, 0) == 0)
            {
                var bootSec = BitConverter.ToInt64(buf, 0);
                var nowSec  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (bootSec > 0 && nowSec > bootSec)
                    return TimeSpan.FromSeconds(nowSec - bootSec);
            }
        }
        catch { }
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    // ── GPUs via system_profiler ───────────────────────────────────────────────

    private static IReadOnlyList<GpuHardwareInfo> ReadGpus()
    {
        var result = new List<GpuHardwareInfo>();
        try
        {
            var json = RunCommand("system_profiler", "SPDisplaysDataType -json");
            if (string.IsNullOrEmpty(json)) return result;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var displays)) return result;

            foreach (var gpu in displays.EnumerateArray())
            {
                var name       = gpu.TryGetProperty("sppci_model",       out var n) ? n.GetString() ?? "" : "";
                var vramStr    = gpu.TryGetProperty("sppci_vram",         out var v) ? v.GetString() ?? "" : "";
                var driverVer  = gpu.TryGetProperty("spdisplays_driver",  out var d) ? d.GetString() ?? "" : "";

                long vramBytes = ParseVramString(vramStr);

                result.Add(new GpuHardwareInfo(
                    Name:           name,
                    DriverVersion:  driverVer,
                    VramBytes:      vramBytes,
                    VideoProcessor: name,
                    Status:         string.Empty));
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Parses strings like "4096 MB", "8 GB", or "499.96 GB" into bytes.
    /// Uses double.Parse so fractional values (e.g. "499.96 GB") are not truncated.
    /// Also handles TB and pure-number JSON inputs (treated as bytes directly).
    /// </summary>
    private static long ParseVramString(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Pure number with no unit suffix → treat as bytes (JSON number passed as string)
        if (parts.Length == 1)
        {
            return long.TryParse(parts[0], out var raw) ? raw : 0;
        }
        // Use double so "499.96 GB" is not truncated to 499 GB
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var value)) return 0;
        return parts[1].Equals("TB", StringComparison.OrdinalIgnoreCase) ? (long)Math.Round(value * 1_099_511_627_776.0)
             : parts[1].Equals("GB", StringComparison.OrdinalIgnoreCase) ? (long)Math.Round(value * 1_073_741_824.0)
             : parts[1].Equals("MB", StringComparison.OrdinalIgnoreCase) ? (long)Math.Round(value * 1_048_576.0)
             : 0;
    }

    // ── Storage via system_profiler ────────────────────────────────────────────

    private static IReadOnlyList<StorageDriveInfo> ReadStorage()
    {
        var result = new List<StorageDriveInfo>();
        try
        {
            var json = RunCommand("system_profiler", "SPStorageDataType -json");
            if (string.IsNullOrEmpty(json)) return result;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPStorageDataType", out var volumes)) return result;

            int idx = 0;
            foreach (var vol in volumes.EnumerateArray())
            {
                var name     = vol.TryGetProperty("_name",              out var n) ? n.GetString() ?? "" : "";
                var medium   = vol.TryGetProperty("spstorage_medium_type",out var m) ? m.GetString() ?? "" : "";
                long sizeB   = 0;
                if (vol.TryGetProperty("spstorage_volume_size", out var sz))
                {
                    // Value may be a number (bytes) or string "499.96 GB"
                    if (sz.ValueKind == JsonValueKind.Number)
                        sizeB = sz.GetInt64();
                    else if (sz.ValueKind == JsonValueKind.String)
                        sizeB = ParseVramString(sz.GetString() ?? "");
                }
                result.Add(new StorageDriveInfo(
                    Index:        idx++,
                    Model:        name,
                    Interface:    medium,
                    SizeBytes:    sizeB,
                    MediaType:    medium,
                    SerialNumber: string.Empty,
                    Status:       string.Empty));
            }
        }
        catch { }
        return result;
    }

    // ── RAM slots via system_profiler ──────────────────────────────────────────

    private static IReadOnlyList<RamSlotInfo> ReadRamSlots()
    {
        var result = new List<RamSlotInfo>();
        try
        {
            var json = RunCommand("system_profiler", "SPMemoryDataType -json");
            if (string.IsNullOrEmpty(json)) return result;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPMemoryDataType", out var memArray)) return result;

            foreach (var bank in memArray.EnumerateArray())
            {
                // Each bank has an "Items" or "_items" array with slot details
                var itemsProp = bank.TryGetProperty("_items", out var it) ? it
                              : bank.TryGetProperty("Items",  out var it2) ? it2
                              : default;

                if (itemsProp.ValueKind != JsonValueKind.Array) continue;

                foreach (var slot in itemsProp.EnumerateArray())
                {
                    var locator  = slot.TryGetProperty("_name",           out var l) ? l.GetString() ?? "" : "";
                    var sizeStr  = slot.TryGetProperty("spdisplays_size", out var s) ? s.GetString() ?? "" : "";
                    var speed    = slot.TryGetProperty("dimm_speed",      out var sp)? sp.GetString() ?? "" : "";
                    var type     = slot.TryGetProperty("dimm_type",       out var t) ? t.GetString() ?? "" : "";
                    var mfg      = slot.TryGetProperty("dimm_manufacturer",out var m)? m.GetString() ?? "" : "";
                    var part     = slot.TryGetProperty("dimm_part_number",out var p) ? p.GetString() ?? "" : "";

                    long capBytes = ParseVramString(sizeStr);
                    int.TryParse(speed.Replace(" MHz","").Trim(), out var speedMhz);

                    result.Add(new RamSlotInfo(
                        DeviceLocator: locator,
                        CapacityBytes: capBytes,
                        SpeedMhz:      speedMhz,
                        MemoryType:    type,
                        Manufacturer:  mfg,
                        PartNumber:    part));
                }
            }
        }
        catch { }
        return result;
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
            if (!proc.WaitForExit(5000)) { proc.Kill(); return string.Empty; }
            return outputTask.Result;
        }
        catch { return string.Empty; }
    }
}
