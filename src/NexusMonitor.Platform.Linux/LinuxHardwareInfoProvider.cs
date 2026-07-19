using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.Linux;

public sealed class LinuxHardwareInfoProvider
{
    public Task<SystemHardwareInfo> QueryAsync(CancellationToken ct = default) =>
        Task.Run(BuildInfo, ct);

    private static SystemHardwareInfo BuildInfo()
    {
        var uptime    = ReadUptime();
        var cpuName   = ReadCpuModel();
        var (physical, logical) = ReadCoreCounts();
        var (l2Kb, l3Kb)        = ReadCacheSizes();
        var maxFreqMhz           = ReadMaxFreqMhz();
        var stepping             = ReadCpuStepping();
        var totalRamBytes        = ReadTotalMem();

        var cpu = new CpuHardwareInfo(
            Name:          cpuName,
            Architecture:  System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            PhysicalCores: physical,
            LogicalCores:  logical,
            L2CacheKB:     (int)l2Kb,
            L3CacheKB:     (int)l3Kb,
            MaxClockMhz:   (int)maxFreqMhz,
            Socket:        ReadCpuSocket(),
            Stepping:      stepping);

        var gpus     = ReadGpus();
        var storage  = ReadStorage();
        var ramSlots = ReadRamSlots();

        return new SystemHardwareInfo(
            Hostname:               Environment.MachineName,
            OsName:                 System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            OsBuild:                Environment.OSVersion.ToString(),
            OsArchitecture:         System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Uptime:                 uptime,
            BiosVendor:             ReadDmiField("bios_vendor"),
            BiosVersion:            ReadDmiField("bios_version"),
            MotherboardManufacturer: ReadDmiField("board_vendor"),
            MotherboardModel:       ReadDmiField("board_name"),
            Cpu:                    cpu,
            TotalRamBytes:          totalRamBytes,
            RamSlots:               ramSlots,
            Gpus:                   gpus,
            Storage:                storage);
    }

    // ── Uptime ────────────────────────────────────────────────────────────────
    private static TimeSpan ReadUptime()
    {
        try
        {
            var txt = File.ReadAllText("/proc/uptime").Trim().Split(' ')[0];
            if (double.TryParse(txt, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var secs))
                return TimeSpan.FromSeconds(secs);
        }
        catch { }
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    // ── DMI / sysfs fields ────────────────────────────────────────────────────
    private static string ReadDmiField(string name)
    {
        try
        {
            var path = $"/sys/class/dmi/id/{name}";
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch { return string.Empty; }
    }

    // ── CPU model ─────────────────────────────────────────────────────────────
    private static string ReadCpuModel()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0) return line[(idx + 1)..].Trim();
                }
            }
        }
        catch { }
        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
    }

    // ── Core counts ───────────────────────────────────────────────────────────
    private static (int physical, int logical) ReadCoreCounts()
    {
        var logical = Environment.ProcessorCount;
        try
        {
            var seen = new HashSet<(int physId, int coreId)>();
            int physId = 0, coreId = 0;
            foreach (var line in File.ReadAllLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("physical id", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0) int.TryParse(line[(idx + 1)..].Trim(), out physId);
                }
                else if (line.StartsWith("core id", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0) int.TryParse(line[(idx + 1)..].Trim(), out coreId);
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    seen.Add((physId, coreId));
                }
            }
            if (seen.Count > 0) return (seen.Count, logical);
        }
        catch { }
        return (logical, logical);
    }

    // ── Cache sizes ───────────────────────────────────────────────────────────
    private static (long l2Kb, long l3Kb) ReadCacheSizes()
    {
        long l2 = 0, l3 = 0;
        try
        {
            // Walk cache indices for cpu0
            var cacheBase = "/sys/devices/system/cpu/cpu0/cache";
            if (!Directory.Exists(cacheBase)) return (0, 0);

            foreach (var dir in Directory.GetDirectories(cacheBase, "index*"))
            {
                var levelPath = Path.Combine(dir, "level");
                var sizePath  = Path.Combine(dir, "size");
                if (!File.Exists(levelPath) || !File.Exists(sizePath)) continue;

                if (!int.TryParse(File.ReadAllText(levelPath).Trim(), out var level)) continue;
                var sizeStr = File.ReadAllText(sizePath).Trim();

                long sizeKb = 0;
                if (sizeStr.EndsWith("K", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(sizeStr[..^1], out sizeKb);
                else
                    long.TryParse(sizeStr, out sizeKb);

                if (level == 2 && sizeKb > l2) l2 = sizeKb;
                if (level == 3 && sizeKb > l3) l3 = sizeKb;
            }
        }
        catch { }
        return (l2, l3);
    }

    // ── Max CPU frequency ─────────────────────────────────────────────────────
    private static double ReadMaxFreqMhz()
    {
        try
        {
            var path = "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq";
            if (File.Exists(path))
            {
                var txt = File.ReadAllText(path).Trim();
                if (long.TryParse(txt, out var kHz)) return kHz / 1000.0;
            }
        }
        catch { }
        return 0;
    }

    // ── CPU stepping ──────────────────────────────────────────────────────────
    private static string ReadCpuStepping()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("stepping", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0) return line[(idx + 1)..].Trim();
                }
            }
        }
        catch { }
        return string.Empty;
    }

    // ── Total RAM ─────────────────────────────────────────────────────────────
    private static long ReadTotalMem()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length < 2) continue;
                    var val = parts[1].Trim().Split(' ')[0];
                    if (long.TryParse(val, out var kb)) return kb * 1024L;
                }
            }
        }
        catch { }
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    // ── CPU socket ────────────────────────────────────────────────────────────
    private static string ReadCpuSocket()
    {
        // Try dmidecode -t processor for socket info (requires root or elevated capability)
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("dmidecode", "-t processor")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } }
            var output = outputTask.Result;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("Socket Designation:", StringComparison.Ordinal))
                {
                    var val = trimmed["Socket Designation:".Length..].Trim();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
        }
        catch { }
        // TODO(availability-enum): this "N/A" is provider-baked (bypasses
        // SystemInfoViewModel.SocketDisplay's own dash logic entirely, since "N/A" isn't
        // whitespace) — out of scope for the unavailable-metric-tooltips PR. See CONTRIBUTING.md
        // "Platform code honesty contract" for the planned structured-availability migration that
        // would let this express "dmidecode unavailable/unprivileged" explicitly instead.
        return "N/A";
    }

    // ── RAM slots via dmidecode ────────────────────────────────────────────────
    private static IReadOnlyList<RamSlotInfo> ReadRamSlots()
    {
        var result = new List<RamSlotInfo>();
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("dmidecode", "-t memory")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } }
            var output = outputTask.Result;

            // Parse Memory Device blocks
            var blocks = output.Split(new[] { "\nMemory Device\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks.Skip(1)) // first element is header before first block
            {
                string slot = "", manufacturer = "", partNumber = "", speed = "", formFactor = "";
                long sizeBytes = 0;
                foreach (var line in block.Split('\n'))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("Locator:", StringComparison.Ordinal) && !t.StartsWith("Bank Locator:", StringComparison.Ordinal))
                        slot = t["Locator:".Length..].Trim();
                    else if (t.StartsWith("Size:", StringComparison.Ordinal))
                    {
                        var sz = t["Size:".Length..].Trim();
                        if (!sz.Contains("No Module", StringComparison.OrdinalIgnoreCase))
                        {
                            // "8192 MB" or "16 GB"
                            var szParts = sz.Split(' ');
                            if (szParts.Length >= 2 && long.TryParse(szParts[0], out var num))
                            {
                                sizeBytes = szParts[1].Equals("GB", StringComparison.OrdinalIgnoreCase)
                                    ? num * 1_073_741_824L
                                    : num * 1_048_576L; // MB
                            }
                        }
                    }
                    else if (t.StartsWith("Manufacturer:", StringComparison.Ordinal))
                        manufacturer = t["Manufacturer:".Length..].Trim();
                    else if (t.StartsWith("Part Number:", StringComparison.Ordinal))
                        partNumber = t["Part Number:".Length..].Trim();
                    else if (t.StartsWith("Speed:", StringComparison.Ordinal))
                        speed = t["Speed:".Length..].Trim();
                    else if (t.StartsWith("Form Factor:", StringComparison.Ordinal))
                        formFactor = t["Form Factor:".Length..].Trim();
                }
                if (string.IsNullOrEmpty(slot)) continue;
                result.Add(new RamSlotInfo(
                    DeviceLocator: slot,
                    CapacityBytes: sizeBytes,
                    SpeedMhz:      int.TryParse(speed.Split(' ')[0], out var mhz) ? mhz : 0,
                    MemoryType:    formFactor,
                    Manufacturer:  manufacturer,
                    PartNumber:    partNumber));
            }
        }
        catch { }
        return result;
    }

    // ── GPUs via /sys/class/drm + nvidia-smi ──────────────────────────────────
    private static IReadOnlyList<GpuHardwareInfo> ReadGpus()
    {
        var result = new List<GpuHardwareInfo>();
        try
        {
            var drmBase = "/sys/class/drm";
            if (!Directory.Exists(drmBase)) return result;

            var seen = new HashSet<string>();
            foreach (var card in Directory.GetDirectories(drmBase, "card*"))
            {
                var name = Path.GetFileName(card);
                if (name.Contains('-')) continue;

                var vendorPath = Path.Combine(card, "device", "vendor");
                var devicePath = Path.Combine(card, "device", "device");

                var vendorId = File.Exists(vendorPath) ? File.ReadAllText(vendorPath).Trim() : "";
                var deviceId = File.Exists(devicePath) ? File.ReadAllText(devicePath).Trim() : "";

                var key = $"{vendorId}:{deviceId}";
                if (!seen.Add(key)) continue;

                // ── NVIDIA: use nvidia-smi for product name, VRAM, and driver ───
                if (vendorId == "0x10de")
                {
                    var nvInfo = ReadNvidiaHardwareInfo();
                    if (nvInfo is not null)
                    {
                        result.Add(nvInfo);
                        continue;
                    }
                }

                // ── AMD: read product name from lspci, VRAM from sysfs ──────────
                if (vendorId == "0x1002")
                {
                    var amdInfo = ReadAmdHardwareInfo(card, name);
                    result.Add(amdInfo);
                    continue;
                }

                // ── Intel or unknown: generic fallback ──────────────────────────
                var vendorName = vendorId switch
                {
                    "0x8086" => "Intel",
                    _        => vendorId
                };
                result.Add(new GpuHardwareInfo(
                    Name:           $"{vendorName} GPU ({name})",
                    DriverVersion:  string.Empty,
                    VramBytes:      0,
                    VideoProcessor: deviceId,
                    Status:         string.Empty));
            }
        }
        catch { }
        return result;
    }

    private static GpuHardwareInfo? ReadNvidiaHardwareInfo()
    {
        try
        {
            var smiBin = File.Exists("/usr/bin/nvidia-smi") ? "/usr/bin/nvidia-smi" : "/usr/local/bin/nvidia-smi";
            if (!File.Exists(smiBin)) return null;

            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(smiBin,
                    "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } }
            var output = outputTask.Result;

            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is null) return null;

            var parts = line.Split(',');
            if (parts.Length < 3) return null;

            var gpuName       = parts[0].Trim();
            var vramMb        = long.TryParse(parts[1].Trim(), out var mb) ? mb : 0;
            var driverVersion = parts[2].Trim();

            return new GpuHardwareInfo(
                Name:           gpuName,
                DriverVersion:  driverVersion,
                VramBytes:      vramMb * 1_048_576L,
                VideoProcessor: gpuName,
                Status:         "OK");
        }
        catch { return null; }
    }

    private static GpuHardwareInfo ReadAmdHardwareInfo(string cardPath, string cardName)
    {
        // VRAM from sysfs
        long vramBytes = 0;
        try
        {
            var vramPath = Path.Combine(cardPath, "device", "mem_info_vram_total");
            if (File.Exists(vramPath) && long.TryParse(File.ReadAllText(vramPath).Trim(), out var v))
                vramBytes = v;
        }
        catch { }

        // Product name from lspci output
        var gpuName = $"AMD GPU ({cardName})";
        try
        {
            var devicePath = Path.Combine(cardPath, "device", "device");
            var deviceId   = File.Exists(devicePath) ? File.ReadAllText(devicePath).Trim() : "";

            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("lspci", "-mm -d 1002:")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var lspciTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } }
            var lspci = lspciTask.Result;
            // Format: BDF "Class" "Vendor" "Device" ...
            var matchLine = lspci.Split('\n')
                .FirstOrDefault(l => !string.IsNullOrEmpty(deviceId)
                    && l.Contains(deviceId[2..], StringComparison.OrdinalIgnoreCase)); // strip "0x" prefix
            if (matchLine is not null)
            {
                // Extract quoted fields
                var fields = System.Text.RegularExpressions.Regex.Matches(matchLine, "\"([^\"]*)\"");
                if (fields.Count >= 4) gpuName = fields[3].Groups[1].Value; // "Device" field
            }
        }
        catch { }

        return new GpuHardwareInfo(
            Name:           gpuName,
            DriverVersion:  string.Empty,
            VramBytes:      vramBytes,
            VideoProcessor: gpuName,
            Status:         "OK");
    }

    // ── Storage via /sys/block ────────────────────────────────────────────────
    private static IReadOnlyList<StorageDriveInfo> ReadStorage()
    {
        var result = new List<StorageDriveInfo>();
        try
        {
            var blockBase = "/sys/block";
            if (!Directory.Exists(blockBase)) return result;

            int idx = 0;
            foreach (var dev in Directory.GetDirectories(blockBase))
            {
                var devName = Path.GetFileName(dev);
                // Skip loop, ram, dm devices
                if (devName.StartsWith("loop", StringComparison.Ordinal) ||
                    devName.StartsWith("ram",  StringComparison.Ordinal) ||
                    devName.StartsWith("dm-",  StringComparison.Ordinal) ||
                    devName.StartsWith("zram", StringComparison.Ordinal)) continue;

                var modelPath      = Path.Combine(dev, "device", "model");
                var sizePath       = Path.Combine(dev, "size");
                var rotationalPath = Path.Combine(dev, "queue", "rotational");
                var serialPath     = Path.Combine(dev, "device", "serial");
                var transportPath  = Path.Combine(dev, "device", "transport");

                var model = File.Exists(modelPath) ? File.ReadAllText(modelPath).Trim() : devName;

                long sizeBytes = 0;
                if (File.Exists(sizePath) && long.TryParse(File.ReadAllText(sizePath).Trim(), out var sectors))
                    sizeBytes = sectors * 512L;

                // MediaType: 0=SSD/NVMe, 1=HDD
                var mediaType = "Unknown";
                if (File.Exists(rotationalPath))
                {
                    var rot = File.ReadAllText(rotationalPath).Trim();
                    mediaType = rot == "0"
                        ? (devName.StartsWith("nvme", StringComparison.Ordinal) ? "NVMe SSD" : "SSD")
                        : "HDD";
                }

                // Serial number
                var serial = string.Empty;
                if (File.Exists(serialPath))
                    serial = File.ReadAllText(serialPath).Trim();

                // Interface: NVMe, SATA, or USB based on device path/name
                var iface = "Unknown";
                if (devName.StartsWith("nvme", StringComparison.Ordinal))
                    iface = "NVMe";
                else if (File.Exists(transportPath))
                    iface = File.ReadAllText(transportPath).Trim().ToUpperInvariant();
                else if (devName.StartsWith("sd", StringComparison.Ordinal))
                    iface = "SATA";

                result.Add(new StorageDriveInfo(
                    Index:        idx++,
                    Model:        model,
                    Interface:    iface,
                    SizeBytes:    sizeBytes,
                    MediaType:    mediaType,
                    SerialNumber: serial,
                    Status:       "Healthy"));
            }
        }
        catch { }
        return result;
    }
}
