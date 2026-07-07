using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Health;

/// <summary>
/// Analyses real-time hardware metrics and produces a plain-English
/// <see cref="BottleneckReport"/> identifying which component is the
/// performance-limiting factor and why.
///
/// All thresholds are tuned for gaming / creative workloads where sustained
/// high utilisation is expected and normal.
/// </summary>
public static class BottleneckDetector
{
    // ── Utilisation thresholds ────────────────────────────────────────────────

    // A component is considered "maxed" when it exceeds these for a sustained period
    private const double MaxedHigh     = 92.0;  // severe: almost certainly the bottleneck
    private const double MaxedModerate = 80.0;  // moderate: likely contributing
    private const double IdleThreshold = 30.0;  // below this = component has plenty of headroom

    // Thermal throttle: CPU running >10% below base clock is a red flag
    private const double ThrottleRatio = 0.90;

    // Minimum load before we bother classifying (avoid false positives at idle)
    private const double MinWorkloadLoad = 35.0;

    // ── Sustained-load smoothing (5-sample moving average) ────────────────────
    // Prevents single-frame spikes from generating false bottleneck reports.
    private const int SmoothingSamples = 5;
    private static readonly Queue<double> _cpuSmooth  = new(SmoothingSamples);
    private static readonly Queue<double> _gpuSmooth  = new(SmoothingSamples);
    private static readonly Queue<double> _vramSmooth = new(SmoothingSamples);
    private static readonly Queue<double> _memSmooth  = new(SmoothingSamples);
    private static readonly Queue<double> _diskSmooth = new(SmoothingSamples);
    private static readonly object _smoothLock = new();

    public static BottleneckReport Analyse(
        SystemMetrics metrics,
        IReadOnlyList<ProcessInfo> processes)
    {
        lock (_smoothLock)
        {
        // ── Raw values ─────────────────────────────────────────────────────────
        var cpuRaw   = metrics.Cpu.TotalPercent;
        var gpuRaw   = metrics.Gpus.Count > 0 ? metrics.Gpus.Max(g => g.UsagePercent)      : 0;
        var vramRaw  = metrics.Gpus.Count > 0 ? metrics.Gpus.Max(g => g.MemoryUsedPercent)  : 0;
        var memRaw   = metrics.Memory.UsedPercent;
        var diskRaw  = metrics.Disks.Count > 0 ? metrics.Disks.Max(d => d.ActivePercent)    : 0;
        var cpuTemp  = metrics.Cpu.TemperatureCelsius;
        var gpuTemp  = metrics.Gpus.Count > 0 ? metrics.Gpus.Max(g => g.TemperatureCelsius) : 0;
        var cpuFreq  = metrics.Cpu.FrequencyMhz;
        var cpuBase  = metrics.Cpu.BaseSpeedMhz;

        // ── Smoothed values (5-tick rolling average) ───────────────────────────
        var cpu  = Smooth(_cpuSmooth,  cpuRaw);
        var gpu  = Smooth(_gpuSmooth,  gpuRaw);
        var vram = Smooth(_vramSmooth, vramRaw);
        var mem  = Smooth(_memSmooth,  memRaw);
        var disk = Smooth(_diskSmooth, diskRaw);

        // ── Workload classification ────────────────────────────────────────────
        var workload = WorkloadClassifier.Classify(processes, gpu, cpu, out var primaryProcess);

        // ── Idle guard ─────────────────────────────────────────────────────────
        var maxLoad = Math.Max(cpu, gpu);
        if (maxLoad < MinWorkloadLoad)
        {
            return new BottleneckReport
            {
                Bottleneck      = BottleneckType.Idle,
                Workload        = workload,
                WorkloadProcess = primaryProcess,
                Headline        = "No significant workload detected",
                Explanation     = "Your system is largely idle. Bottleneck analysis is most useful during gaming, video editing, or other demanding tasks.",
                CpuPercent      = cpu, GpuPercent = gpu, GpuVramPercent = vram,
                MemoryPercent   = mem, DiskPercent = disk,
                CpuTempCelsius  = cpuTemp, GpuTempCelsius = gpuTemp,
            };
        }

        // ── Thermal throttle check (highest priority) ──────────────────────────
        // CPU throttle: actual frequency significantly below base clock under load
        var cpuThrottling = cpuBase > 0 && cpuFreq > 0 && cpu > 60
            && (cpuFreq / cpuBase) < ThrottleRatio;
        // Temp-based fallback for when frequency data is unavailable
        var cpuThermalAlert = cpuTemp > 95;
        var gpuThermalAlert = gpuTemp > 90;

        if (cpuThrottling || cpuThermalAlert || gpuThermalAlert)
        {
            var which = (cpuThrottling || cpuThermalAlert) && gpuThermalAlert ? "CPU and GPU"
                      : (cpuThrottling || cpuThermalAlert) ? "CPU" : "GPU";

            return Build(BottleneckType.ThermalThrottle, BottleneckSeverity.Severe,
                workload, primaryProcess,
                headline: $"Thermal throttling detected ({which})",
                explanation: $"Your {which} is overheating and automatically slowing itself down to prevent damage. "
                           + "This is masking your real performance — your hardware is intentionally running below its rated speed. "
                           + "Cleaning dust from vents, replacing thermal paste, or improving case airflow should be the first step before any hardware upgrade.",
                upgrade: "Fix cooling first. Upgrading to faster hardware while thermals are unresolved will not deliver the expected performance gain.",
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── VRAM saturation ────────────────────────────────────────────────────
        // VRAM full but GPU compute may be low — game/app is stalling on memory transfers
        if (vram > MaxedHigh && gpu < MaxedModerate)
        {
            var severity = vram > 97 ? BottleneckSeverity.Severe : BottleneckSeverity.Moderate;
            var workloadCtx = WorkloadAdvice_Vram(workload);

            return Build(BottleneckType.VramBound, severity,
                workload, primaryProcess,
                headline: $"VRAM bottleneck — GPU memory is {vram:F0}% full",
                explanation: $"Your GPU's video memory is nearly full. {workloadCtx} "
                           + "When VRAM fills up, your system moves assets to regular RAM or drops texture quality, causing stutters, pop-in, or slowdowns even if your GPU compute isn't maxed.",
                upgrade: UpgradeAdvice_Vram(workload),
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── GPU-bound ─────────────────────────────────────────────────────────
        // GPU maxed, CPU clearly has headroom → classic GPU bottleneck
        if (gpu >= MaxedHigh && cpu < MaxedModerate - 10)
        {
            var severity = cpu < IdleThreshold ? BottleneckSeverity.Severe : BottleneckSeverity.Moderate;

            return Build(BottleneckType.GpuBound, severity,
                workload, primaryProcess,
                headline: $"GPU bottleneck — rendering is the limit ({gpu:F0}% GPU, {cpu:F0}% CPU)",
                explanation: $"Your GPU is working at its maximum capacity while your CPU still has headroom. "
                           + $"{WorkloadAdvice_Gpu(workload)} "
                           + "Your CPU could technically push more work, but your GPU can't keep up with rendering it.",
                upgrade: UpgradeAdvice_Gpu(workload, severity),
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── CPU-bound ─────────────────────────────────────────────────────────
        // CPU maxed, GPU clearly has headroom
        if (cpu >= MaxedHigh && gpu < MaxedModerate - 10)
        {
            var severity = gpu < IdleThreshold ? BottleneckSeverity.Severe : BottleneckSeverity.Moderate;

            return Build(BottleneckType.CpuBound, severity,
                workload, primaryProcess,
                headline: $"CPU bottleneck — processing is the limit ({cpu:F0}% CPU, {gpu:F0}% GPU)",
                explanation: $"Your CPU is maxed out while your GPU is sitting largely idle. "
                           + $"{WorkloadAdvice_Cpu(workload)} "
                           + "Upgrading to a faster GPU would not improve performance here — the CPU is the constraint.",
                upgrade: UpgradeAdvice_Cpu(workload, severity),
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── RAM pressure ──────────────────────────────────────────────────────
        if (mem > MaxedHigh)
        {
            var severity = mem > 97 ? BottleneckSeverity.Severe : BottleneckSeverity.Moderate;

            return Build(BottleneckType.MemoryBound, severity,
                workload, primaryProcess,
                headline: $"Memory bottleneck — RAM is {mem:F0}% full",
                explanation: $"Your system RAM is nearly exhausted. Your computer is likely using your hard drive or SSD as slow overflow memory (paging/swap), "
                           + "which is orders of magnitude slower than RAM. This causes stutters, slow load times, and general sluggishness regardless of your CPU or GPU speed.",
                upgrade: "Adding more RAM is one of the most cost-effective upgrades possible. For gaming and creative work, 32 GB is the current sweet spot. Check your motherboard's maximum supported RAM before purchasing.",
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── Storage-bound ─────────────────────────────────────────────────────
        if (disk > MaxedHigh && cpu < MaxedModerate && gpu < MaxedModerate)
        {
            return Build(BottleneckType.StorageBound, BottleneckSeverity.Moderate,
                workload, primaryProcess,
                headline: $"Storage bottleneck — disk is at {disk:F0}% activity",
                explanation: "Your storage drive is being heavily accessed while CPU and GPU have headroom. "
                           + "This is typical during game loading screens, open-world asset streaming, or when working with large video/3D files. "
                           + "If you're seeing stutters in-game specifically when new areas load, your storage speed is likely the cause.",
                upgrade: "If you're using a hard disk (HDD), upgrading to an NVMe SSD is the single biggest quality-of-life improvement for load times. If you already have an SSD, ensure your game/project files are on it, not a secondary HDD.",
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── Mild CPU-GPU imbalance ─────────────────────────────────────────────
        if (gpu >= MaxedModerate && cpu < IdleThreshold + 10)
        {
            return Build(BottleneckType.GpuBound, BottleneckSeverity.Mild,
                workload, primaryProcess,
                headline: $"Mild GPU bottleneck ({gpu:F0}% GPU, {cpu:F0}% CPU)",
                explanation: "Your GPU is doing most of the work while your CPU has significant headroom. This is actually a healthy sign for gaming — it means your GPU is fully utilized. "
                           + "A mild CPU-GPU mismatch at this level won't cause stutters but does mean a faster GPU would directly improve frame rates.",
                upgrade: "Your CPU has room to spare. If you want higher frame rates or better graphical settings, a GPU upgrade would make a noticeable difference.",
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        if (cpu >= MaxedModerate && gpu < IdleThreshold + 10)
        {
            return Build(BottleneckType.CpuBound, BottleneckSeverity.Mild,
                workload, primaryProcess,
                headline: $"Mild CPU bottleneck ({cpu:F0}% CPU, {gpu:F0}% GPU)",
                explanation: "Your CPU is working harder than your GPU. This can cause frame time inconsistency (stutters) even when average frame rates look acceptable. "
                           + "It's more common in games with lots of AI, physics, or player count (online/simulation games), or when running many background tasks.",
                upgrade: "Closing background applications may help immediately. If this happens consistently, a CPU with better single-core performance (higher clock speed) would reduce stutter.",
                cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        }

        // ── Balanced ──────────────────────────────────────────────────────────
        return Build(BottleneckType.Balanced, BottleneckSeverity.Mild,
            workload, primaryProcess,
            headline: "System is well-balanced — no clear bottleneck",
            explanation: $"Your CPU ({cpu:F0}%) and GPU ({gpu:F0}%) are both contributing roughly equally. This is the ideal state — your hardware is well-matched to your workload. "
                       + "Both components are being used efficiently and neither is sitting idle waiting for the other.",
            upgrade: string.Empty,
            cpu, gpu, vram, mem, disk, cpuTemp, gpuTemp, cpuThrottling);
        } // end lock (_smoothLock)
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the metrics sample carries genuine GPU telemetry rather than
    /// static-identity-only data. Some platforms (e.g. macOS — no public GPU utilization
    /// API) always report <c>UsagePercent = 0</c> and <c>DedicatedMemoryUsedBytes = 0</c>
    /// even when a real GPU is present, which would otherwise read as a perfectly idle
    /// (and thus perfectly healthy) GPU. Used to exclude the GPU subsystem from health
    /// scoring instead of rewarding fabricated zeros with a perfect score.
    /// </summary>
    public static bool HasLiveGpuData(IReadOnlyList<GpuMetrics> gpus) =>
        gpus.Count > 0 && gpus.Any(g => g.UsagePercent > 0 || g.DedicatedMemoryUsedBytes > 0);

    private static BottleneckReport Build(
        BottleneckType type, BottleneckSeverity severity, WorkloadType workload,
        string process, string headline, string explanation, string upgrade,
        double cpu, double gpu, double vram, double mem, double disk,
        double cpuTemp, double gpuTemp, bool cpuThrottling) =>
        new()
        {
            Bottleneck      = type,
            Severity        = severity,
            Workload        = workload,
            WorkloadProcess = process,
            Headline        = headline,
            Explanation     = explanation,
            UpgradeAdvice   = upgrade,
            CpuPercent      = cpu,
            GpuPercent      = gpu,
            GpuVramPercent  = vram,
            MemoryPercent   = mem,
            DiskPercent     = disk,
            CpuTempCelsius  = cpuTemp,
            GpuTempCelsius  = gpuTemp,
            CpuIsThrottling = cpuThrottling,
            Timestamp       = DateTime.UtcNow,
        };

    private static double Smooth(Queue<double> queue, double value)
    {
        if (queue.Count >= SmoothingSamples) queue.Dequeue();
        queue.Enqueue(value);
        return queue.Average();
    }

    // ── Workload-specific text snippets ───────────────────────────────────────

    private static string WorkloadAdvice_Gpu(WorkloadType w) => w switch
    {
        WorkloadType.Gaming         => "In gaming, the GPU handles all the rendering — this is the most common bottleneck for gamers.",
        WorkloadType.VideoEditing   => "Video editing and effects rendering put heavy demand on GPU compute and VRAM.",
        WorkloadType.ThreeDRendering => "3D rendering workloads are extremely GPU-intensive, especially with ray tracing.",
        WorkloadType.Streaming      => "Streaming while gaming doubles GPU demand — encoding and rendering compete for the same resources.",
        _                           => "GPU-accelerated workloads are being limited by your graphics card's compute capacity.",
    };

    private static string WorkloadAdvice_Cpu(WorkloadType w) => w switch
    {
        WorkloadType.Gaming         => "In gaming, a CPU bottleneck typically shows as frame time inconsistency (stutters) more than low average FPS. It's common in games with complex AI, large online lobbies, or open-world simulation.",
        WorkloadType.VideoEditing   => "Video editing is CPU-intensive for effects rendering, encoding, and timeline scrubbing.",
        WorkloadType.ThreeDRendering => "CPU-based renderers (Blender Cycles CPU mode, V-Ray CPU) are entirely limited by processor speed and core count.",
        WorkloadType.CadEngineering => "CAD and simulation workloads are heavily CPU-dependent — viewport performance and solver speed are both CPU-bound.",
        WorkloadType.Streaming      => "Software encoding (x264) is very CPU-intensive. Consider switching to hardware encoding (NVENC/AMF) in OBS settings.",
        _                           => "Your processor is the limiting factor for this workload.",
    };

    private static string WorkloadAdvice_Vram(WorkloadType w) => w switch
    {
        WorkloadType.Gaming         => "Modern games at high resolutions (1440p/4K) with high texture quality can exceed 8–12 GB of VRAM.",
        WorkloadType.VideoEditing   => "High-resolution video timelines and effects keep large frame buffers in VRAM.",
        WorkloadType.ThreeDRendering => "Complex 3D scenes with high-poly meshes and 4K textures demand significant VRAM.",
        _                           => "Your current workload is pushing beyond your GPU's video memory capacity.",
    };

    private static string UpgradeAdvice_Gpu(WorkloadType w, BottleneckSeverity s)
    {
        var urgency = s == BottleneckSeverity.Severe
            ? "A GPU upgrade would make a significant, immediately noticeable difference."
            : "A GPU upgrade would improve performance, though gains would be moderate.";

        return w switch
        {
            WorkloadType.Gaming =>
                $"Your GPU is the limiting factor for gaming performance. {urgency} "
                + "Look for a GPU with higher rasterisation performance (check benchmarks for your specific games). "
                + "Ensure your CPU won't become the new bottleneck after upgrading — check that your CPU single-core speed is competitive.",
            WorkloadType.Streaming =>
                $"Consider a GPU with dedicated hardware encoding (NVIDIA NVENC or AMD AMF). {urgency} "
                + "Hardware encoding offloads streaming compression from your CPU/GPU entirely.",
            WorkloadType.VideoEditing =>
                $"A faster GPU with more CUDA/OpenCL cores will accelerate effects rendering and export. {urgency}",
            WorkloadType.ThreeDRendering =>
                $"GPU rendering performance scales nearly linearly with GPU tier. {urgency} "
                + "More VRAM also allows larger scenes without fallback to CPU rendering.",
            _ => $"Upgrading your GPU is the most direct path to better performance here. {urgency}",
        };
    }

    private static string UpgradeAdvice_Cpu(WorkloadType w, BottleneckSeverity s)
    {
        var urgency = s == BottleneckSeverity.Severe
            ? "A CPU upgrade would make a significant difference."
            : "A faster CPU would reduce stutters and improve responsiveness.";

        return w switch
        {
            WorkloadType.Gaming =>
                $"For gaming, single-core speed matters more than core count. {urgency} "
                + "Look for CPUs with high boost clock speeds. Your GPU may be underutilised after the upgrade, so check whether GPU headroom also exists.",
            WorkloadType.Streaming =>
                "Switch to hardware (GPU) encoding in OBS/Streamlabs to remove CPU encoding load immediately — free fix before buying anything. "
                + $"If you need software encoding quality, more CPU cores and clock speed helps. {urgency}",
            WorkloadType.ThreeDRendering =>
                $"CPU rendering scales well with both core count and clock speed. {urgency} "
                + "High-core-count CPUs (12+ cores) offer the best value for render farms.",
            WorkloadType.CadEngineering =>
                $"CAD viewports and solvers benefit from both clock speed (viewport) and core count (simulation). {urgency}",
            _ => $"A CPU with higher single-core performance or more cores would reduce this bottleneck. {urgency}",
        };
    }

    private static string UpgradeAdvice_Vram(WorkloadType w) => w switch
    {
        WorkloadType.Gaming =>
            "Look for a GPU with at least 12 GB VRAM for modern AAA games at 1440p, or 16 GB+ for 4K with max settings. "
            + "VRAM capacity is printed on the GPU spec sheet — it cannot be upgraded separately.",
        WorkloadType.VideoEditing =>
            "16–24 GB VRAM is recommended for 4K+ video editing workflows. "
            + "Ensure your next GPU has enough VRAM for your highest-resolution project.",
        WorkloadType.ThreeDRendering =>
            "24 GB VRAM (or more) is becoming standard for complex rendering scenes. "
            + "Check your scene's memory requirements in your renderer before purchasing.",
        _ =>
            "Your next GPU should have more VRAM than your current card. Check benchmarks specific to your workload.",
    };
}
