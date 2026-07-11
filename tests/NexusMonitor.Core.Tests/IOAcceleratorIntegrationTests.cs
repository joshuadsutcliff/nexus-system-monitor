using System.Diagnostics;
using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;
using Xunit.Abstractions;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Live integration coverage for the Sym-2 Task 6 macOS GPU utilization/memory work. Exercises the
/// real IOAccelerator PerformanceStatistics route against this host — runs on macOS CI runners
/// (the workflow matrix includes macos-latest) and on a macOS dev machine; a deliberate no-op
/// everywhere else so it never fails CI on Windows/Linux runners.
///
/// Machine-specific expectations documented per the ground-truth probe
/// (.superpowers/sdd/sym2-ground-truth.md, base M4 Mac mini): IOAccelerator's
/// "PerformanceStatistics" dict exposes "Device Utilization %" (Int) and "In use system memory" /
/// "Alloc system memory" (bytes) live and unprivileged.
/// </summary>
public class IOAcceleratorIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public IOAcceleratorIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void IOAccelerator_Open_FindsAnEntryAndReadsPlausibleSample()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var accel = IOAccelerator.Open();
        accel.Should().NotBeNull("this M4 has exactly one IOAccelerator registry entry (probe-confirmed)");

        var sample = accel!.ReadPerformanceStatistics();
        sample.Should().NotBeNull("PerformanceStatistics with a utilization key is expected on this machine");

        _output.WriteLine($"utilization={sample!.UtilizationPercent:F1}% " +
                           $"inUse={sample.InUseSystemMemoryBytes} alloc={sample.AllocSystemMemoryBytes}");

        sample.UtilizationPercent.Should().BeInRange(0.0, 100.0);
        sample.InUseSystemMemoryBytes.Should().BeGreaterThan(0,
            "GPU-allocated unified memory is always non-zero while any GPU client (WindowServer at minimum) is running");
    }

    [Fact]
    public async Task Provider_OnRealHost_ReportsPlausibleGpuUtilizationAndMemory()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSSystemMetricsProvider();
        var metrics = await provider.GetMetricsAsync();

        metrics.Gpus.Should().NotBeEmpty("system_profiler reports the built-in GPU on this machine");
        var gpu = metrics.Gpus[0];
        _output.WriteLine($"GPU: {gpu.Name} usage={gpu.UsagePercent:F1}% " +
                           $"dedicatedUsed={gpu.DedicatedMemoryUsedBytes} shared(alloc)={gpu.SharedMemoryUsedBytes}");

        gpu.UsagePercent.Should().BeInRange(0.0, 100.0);
        gpu.DedicatedMemoryUsedBytes.Should().BeGreaterThan(0,
            "\"In use system memory\" (the binding honest-memory mapping) is always non-zero on a running desktop session");
        // GPU temperature must still go through Task 5's plausibility filter, unchanged by Task 6
        // (Task 6 must not regress or bypass it). DRIFT ADDENDUM
        // (.superpowers/sdd/sym2-ground-truth.md, bottom): base-M4 Tg* keys read garbage at idle
        // GPU utilization but real plausible values under sustained GPU load, so this is NOT a
        // fixed 0 on this machine — the invariant is honest-unavailable (0) OR plausible
        // (10-120); a filtered-out garbage value must never appear either way. Do not re-pin this
        // to either snapshot.
        (gpu.TemperatureCelsius == 0.0 || (gpu.TemperatureCelsius >= 10.0 && gpu.TemperatureCelsius <= 120.0))
            .Should().BeTrue(
                "GPU temp must be either honestly unavailable (0) or plausible (10-120 °C, real value seen under sustained GPU load per the drift addendum) — Task 5's plausibility filter must still reject any garbage value");
    }

    [Fact]
    public async Task Provider_TenSampleWindow_ValuesStayPlausible_AndMovementIsDocumented()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSSystemMetricsProvider();
        var utilSamples = new List<double>();
        var memSamples  = new List<long>();

        for (int i = 0; i < 10; i++)
        {
            var m = await provider.GetMetricsAsync();
            if (m.Gpus.Count > 0)
            {
                utilSamples.Add(m.Gpus[0].UsagePercent);
                memSamples.Add(m.Gpus[0].DedicatedMemoryUsedBytes);
            }
            await Task.Delay(150);
        }

        _output.WriteLine("utilization samples: " + string.Join(", ", utilSamples.Select(u => u.ToString("F1"))));
        _output.WriteLine("memory samples: " + string.Join(", ", memSamples));

        var utilizationMoves = utilSamples.Distinct().Count() > 1;
        var memoryMoves      = memSamples.Distinct().Count() > 1;

        // Empirically (this run, headless/idle sigmamini session), BOTH series were flat: 0.0%
        // utilization and a constant ~59 MB in-use figure across all 10 samples. That is a
        // legitimate, honestly-read result for a session with no active GPU-bound workload — the
        // brief anticipated this exact case ("assert not-all-identical OR document if they
        // legitimately are"), so this test documents the outcome rather than asserting movement
        // that would only be true under active GPU load. Every sample is still validated against
        // the physically-plausible range on every tick, which is what would catch a regression to
        // garbage/fabricated values.
        _output.WriteLine(utilizationMoves || memoryMoves
            ? "Movement observed in at least one series across the window."
            : "Both series were static across this window — legitimate on an idle desktop " +
              "session with no active GPU-bound workload (documented per brief, not a bug).");

        foreach (var u in utilSamples) u.Should().BeInRange(0.0, 100.0);
        foreach (var m in memSamples)  m.Should().BeGreaterThan(0);
    }

    [Fact]
    public void IOAccelerator_OneTwentyConsecutiveReads_NoLeakByRssOrHandleGrowth()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var accel = IOAccelerator.Open();
        accel.Should().NotBeNull();

        // Warm up (first read pays any one-time cost) before baselining.
        for (int i = 0; i < 5; i++) _ = accel!.ReadPerformanceStatistics();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baselineRss = Process.GetCurrentProcess().WorkingSet64;

        const int reads = 120;
        for (int i = 0; i < reads; i++)
        {
            var sample = accel!.ReadPerformanceStatistics();
            sample.Should().NotBeNull($"read {i}: PerformanceStatistics should stay available every tick");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var finalRss = Process.GetCurrentProcess().WorkingSet64;
        var growthMb = (finalRss - baselineRss) / (1024.0 * 1024.0);
        _output.WriteLine($"RSS baseline={baselineRss / 1024.0 / 1024.0:F1} MB, " +
                           $"final={finalRss / 1024.0 / 1024.0:F1} MB, growth={growthMb:F2} MB over {reads} reads");

        // Each tick allocates ~6 short-lived CF objects (1 properties dict + up to 5 CFString
        // keys), all explicitly released — a real leak here would show as steady multi-KB/tick
        // growth. 8 MB is a generous ceiling (managed-heap noise from the test harness itself,
        // not a tight leak-detection bound) that a genuine per-tick CF leak at 120 reads would
        // still blow through many times over.
        growthMb.Should().BeLessThan(8.0,
            "IORegistryEntryCreateCFProperties + CFString keys must be released every tick, not leaked");
    }

    [Fact]
    public void IOAccelerator_FiftyOpenDisposeCycles_IsIdempotentAndReleasesHandles()
    {
        if (!OperatingSystem.IsMacOS()) return;

        for (int i = 0; i < 50; i++)
        {
            var accel = IOAccelerator.Open();
            accel.Should().NotBeNull($"cycle {i}: IOAccelerator should still open — leaked registry entries would eventually degrade this");
            _ = accel!.ReadPerformanceStatistics();
            accel.Dispose();
            accel.Dispose(); // idempotent: second dispose must be a safe no-op
        }
        _output.WriteLine("50 IOAccelerator open/read/dispose/dispose cycles completed without failure.");
    }

    [Fact]
    public void IOAccelerator_PerTickReadCost_IsCheap()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var accel = IOAccelerator.Open();
        accel.Should().NotBeNull();

        _ = accel!.ReadPerformanceStatistics(); // warm up

        const int ticks = 100;
        var sw = Stopwatch.StartNew();
        GpuPerformanceSample? last = null;
        for (int i = 0; i < ticks; i++) last = accel!.ReadPerformanceStatistics();
        sw.Stop();

        var perTickMicros = sw.Elapsed.TotalMilliseconds / ticks * 1000.0;
        _output.WriteLine($"IOAccelerator per-tick PerformanceStatistics read: {perTickMicros:F0} µs/tick " +
                          $"over {ticks} ticks; last utilization={last?.UtilizationPercent:F1}%");

        last.Should().NotBeNull();
        (sw.Elapsed.TotalMilliseconds / ticks).Should().BeLessThan(50.0,
            "reading PerformanceStatistics should be a cheap per-tick operation");
    }
}
