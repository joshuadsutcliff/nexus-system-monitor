using System.Diagnostics;
using FluentAssertions;
using NexusMonitor.Core.Tests.Helpers;
using NexusMonitor.Platform.MacOS;
using Xunit;
using Xunit.Abstractions;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Live integration coverage for the Sym-2 Task 5 macOS temperature work. Exercises the real
/// AppleSMC + IOHID path against this host's sensors — runs on macOS CI runners (the workflow
/// matrix includes macos-latest) and on a macOS dev machine; a deliberate no-op everywhere else
/// so it never fails CI on Windows/Linux runners.
///
/// Machine-specific expectations documented per the ground-truth probe
/// (.superpowers/sdd/sym2-ground-truth.md, base M4 Mac mini):
///  • CPU temp comes from the SMC perf-core keys and reads within 10–120 °C.
///  • GPU temp: DRIFT ADDENDUM (2026-07-11, T6 gate discovery, ground-truth file bottom) — the
///    base-M4 Tg* keys returned -4.5/0.8 °C garbage at ~7% idle GPU utilization when originally
///    probed, but return real plausible values (~48–49 °C) under sustained GPU load. GPU temp on
///    THIS machine is therefore load-dependent, not a fixed constant, and the invariant every
///    assertion below must encode is: <c>gpuTemp == 0.0 (honest-unavailable) OR gpuTemp is
///    within [10, 120] (plausible)</c> — never a filtered-out garbage value (negative, sub-10, or
///    121+). Do NOT re-pin either snapshot (idle-0 or loaded-nonzero) as the sole expected value;
///    the plausibility filter is already designed to handle both honestly.
///
/// AVAILABILITY-GATED (2026-07-11, PR #24 CI failure): GitHub's macos-latest runner is a VM with
/// no real AppleSMC/PMU sensors — <see cref="MacSensorAvailability"/> detects at runtime whether
/// each test's sensor route is actually backed by hardware and every test below branches on it:
/// full plausible-value assertions when present (this machine, real Mac CI hardware if ever
/// added), honest-degrade assertions (never throws, reads settle to 0/null) when absent (GitHub's
/// VM runner today). No test silently no-ops on "sensor absent" — that would hide a real
/// regression on VM runners just as easily as asserting real-hardware values there flakes.
/// </summary>
public class MacOSTemperatureIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MacOSTemperatureIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Provider_OnRealHost_ReportsPlausibleCpuTempAndHonestOrPlausibleGpuTemp()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Either CPU temperature route (SMC perf/efficiency keys, or the IOHID die fallback)
        // being real hardware is enough for the provider to produce a plausible reading.
        var cpuSensorAvailable = MacSensorAvailability.HasAppleSmcCpuKey || MacSensorAvailability.HasIoHidTemperature;

        using var provider = new MacOSSystemMetricsProvider();

        // First tick.
        var metrics = await provider.GetMetricsAsync();
        var cpuTemp = metrics.Cpu.TemperatureCelsius;
        _output.WriteLine($"CPU temperature: {cpuTemp:F2} °C");

        if (cpuSensorAvailable)
        {
            _output.WriteLine("sensor present — full-fidelity assertions running");
            cpuTemp.Should().BeInRange(10.0, 120.0,
                "the SMC perf-core keys (or the IOHID die fallback) must yield a plausible CPU temperature on this host");
        }
        else
        {
            _output.WriteLine("sensor absent — degraded-mode assertions ran");
            cpuTemp.Should().Be(0.0,
                "neither AppleSMC nor the IOHID fallback is backed by real hardware on this host (e.g. a CI VM) — " +
                "the provider must degrade to honest 0, never throw or fabricate a plausible-looking value");
        }

        // GPU temp: DRIFT ADDENDUM (.superpowers/sdd/sym2-ground-truth.md, bottom) — the base-M4
        // Tg* keys read garbage at idle GPU utilization but real plausible values under
        // sustained GPU load, so this is NOT a fixed 0 on this machine. The invariant is
        // honest-unavailable (0) OR plausible (10–120); a filtered-out garbage value (negative,
        // sub-10, 121+) must never appear either way. Do not re-pin this to either snapshot. This
        // invariant already covers the sensor-absent case (0 is one of the two allowed states),
        // so no separate availability branch is needed here.
        if (metrics.Gpus.Count > 0)
        {
            var gpuTemp = metrics.Gpus[0].TemperatureCelsius;
            _output.WriteLine($"GPU temperature: {gpuTemp:F2} °C (expected 0 = unavailable, or 10-120 = plausible under load)");
            (gpuTemp == 0.0 || (gpuTemp >= 10.0 && gpuTemp <= 120.0)).Should().BeTrue(
                "GPU temp must be either honestly unavailable (0, idle-GPU base-M4 Tg* garbage filtered out, or no hardware at all on a VM) or a plausible reading (10-120 °C, real value seen under sustained GPU load per the drift addendum) — never a filtered-out garbage value");
        }
        else
        {
            _output.WriteLine("No GPU row from system_profiler — GPU temp assertion skipped.");
        }
    }

    [Fact]
    public async Task Provider_FiftyConsecutiveTicks_NeverThrowAndStayPlausible()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var cpuSensorAvailable = MacSensorAvailability.HasAppleSmcCpuKey || MacSensorAvailability.HasIoHidTemperature;
        _output.WriteLine(cpuSensorAvailable
            ? "sensor present — full-fidelity assertions running"
            : "sensor absent — degraded-mode assertions ran");

        using var provider = new MacOSSystemMetricsProvider();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            var m = await provider.GetMetricsAsync();

            if (cpuSensorAvailable)
            {
                m.Cpu.TemperatureCelsius.Should().BeInRange(10.0, 120.0,
                    $"tick {i}: CPU temperature must stay plausible across consecutive ticks");
            }
            else
            {
                m.Cpu.TemperatureCelsius.Should().Be(0.0,
                    $"tick {i}: no real CPU temperature sensor on this host (e.g. a CI VM) — must stay honestly 0 across consecutive ticks, never throw");
            }

            if (m.Gpus.Count > 0)
            {
                // DRIFT ADDENDUM (.superpowers/sdd/sym2-ground-truth.md, bottom): base-M4 Tg* keys
                // are load-dependent (garbage at idle, plausible under sustained GPU load) — the
                // invariant across every tick is honest-unavailable (0) OR plausible (10-120), not
                // a pinned 0. Already covers the sensor-absent case (0 is allowed either way). See
                // Provider_OnRealHost_ReportsPlausibleCpuTempAndHonestOrPlausibleGpuTemp.
                var gpuTemp = m.Gpus[0].TemperatureCelsius;
                (gpuTemp == 0.0 || (gpuTemp >= 10.0 && gpuTemp <= 120.0)).Should().BeTrue(
                    $"tick {i}: GPU temp must stay either honestly unavailable (0) or plausible (10-120 °C) across consecutive ticks");
            }
        }
        sw.Stop();
        _output.WriteLine($"50 full BuildMetrics ticks: {sw.ElapsedMilliseconds} ms total (includes disk/net/cached subprocesses)");
    }

    [Fact]
    public void AppleSmc_PerTickTemperatureReadCost_IsCheap()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // This test's entire purpose is timing real AppleSMC reads — on a host where the service
        // is absent (GitHub's macos-latest VM runner, PR #24 CI) there is nothing to time. The
        // honest-degrade invariant here is simply "Open() returns null, never throws."
        if (!MacSensorAvailability.HasAppleSmcCpuKey)
        {
            _output.WriteLine("sensor absent — degraded-mode assertions ran");
            AppleSmc? smcAbsent = null;
            Action act = () => smcAbsent = AppleSmc.Open();
            act.Should().NotThrow("AppleSmc.Open() must never throw even when the service is entirely absent from this host");
            smcAbsent.Should().BeNull("AppleSMC is absent on this host (e.g. a CI VM) — Open() must degrade to null, never throw or fabricate a connection");
            smcAbsent?.Dispose();
            return;
        }

        _output.WriteLine("sensor present — full-fidelity assertions running");

        // Isolate the SMC temperature cost from the rest of BuildMetrics: open ONE connection
        // (as the provider does) and read this machine's full resolved key set repeatedly.
        var keySet = SmcTemperature.ResolveKeySet(
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                System.Runtime.InteropServices.Architecture.Arm64
                ? "Apple M4" : "Intel",
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                System.Runtime.InteropServices.Architecture.Arm64);

        using var smc = AppleSmc.Open();
        smc.Should().NotBeNull("AppleSMC must open unprivileged on this host (probe-validated, availability-detected)");

        var allKeys = keySet.CpuPerformance
            .Concat(keySet.CpuEfficiency)
            .Concat(keySet.Gpu)
            .ToArray();

        // Warm up (first read resolves key info) then time 50 full ticks.
        foreach (var k in allKeys) _ = smc!.ReadTemperature(k);

        const int ticks = 50;
        var sw = Stopwatch.StartNew();
        double lastPerfMean = 0;
        for (int i = 0; i < ticks; i++)
        {
            var perf = new List<double>();
            foreach (var k in keySet.CpuPerformance)
            {
                var v = smc!.ReadTemperature(k);
                if (v.HasValue) perf.Add(v.Value);
            }
            foreach (var k in keySet.CpuEfficiency) _ = smc!.ReadTemperature(k);
            foreach (var k in keySet.Gpu)           _ = smc!.ReadTemperature(k);
            lastPerfMean = SmcTemperature.MeanOfPlausible(perf);
        }
        sw.Stop();

        var perTickMicros = sw.Elapsed.TotalMilliseconds / ticks * 1000.0;
        _output.WriteLine($"SMC per-tick temperature read ({allKeys.Length} keys): " +
                          $"{perTickMicros:F0} µs/tick over {ticks} ticks; last perf-core mean = {lastPerfMean:F2} °C");

        lastPerfMean.Should().BeInRange(10.0, 120.0, "perf-core mean must be plausible on this host");
        // Generous ceiling — this is a cost regression guard, not a benchmark.
        (sw.Elapsed.TotalMilliseconds / ticks).Should().BeLessThan(50.0,
            "reading the resolved temperature keys should be a cheap per-tick operation");
    }

    [Fact]
    public void IOHidSensors_ReadSocTemperature_DirectCall_NeverThrowsAndIsPlausibleOrHonestZero()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Direct call to the IOHID fallback route, BYPASSING MacOSSystemMetricsProvider's
        // SMC-first gate (ReadCpuTemperature only invokes IOHidSensors.ReadSocTemperature() when
        // both the SMC perf-core AND efficiency-core means come back 0 — which never happens on
        // this machine, since SMC always wins here; see class doc + T5 gate review). Without a
        // direct call like this one, the IOHID interop path (IOHIDEventSystemClientCreate,
        // service matching on page 0xff00/usage 5, tdie*/tcal filtering, CFRelease discipline)
        // is completely dark to the suite: ReadSocTemperature() swallows every exception and
        // returns 0.0 on ANY failure (honest-degrade convention), which is indistinguishable from
        // "this hardware has no readable PMU die sensors." A silent P/Invoke signature bug here
        // would never surface as a test failure — only as a permanently-dormant fallback in the
        // field. The T5 gate reviewer already proved this exact call passes live on this machine
        // (throwaway test in an isolated worktree, deleted after use — result 36.78 °C; see
        // .superpowers/sdd/sym2-task-5-gate.md, "Important" finding). This test makes that proof
        // permanent instead of re-losing it.
        double temp = 0.0;
        Action act = () => temp = IOHidSensors.ReadSocTemperature();

        act.Should().NotThrow("ReadSocTemperature must never surface an interop exception to callers");

        _output.WriteLine($"IOHidSensors.ReadSocTemperature() direct call: {temp:F2} °C");

        // Primary assertion is deliberately permissive so it cannot flake across hardware: 0 is
        // an honest, allowed result on any machine whose PMU die sensors aren't exposed under
        // this name/path (older chips, future OS revisions that rename the HID service, a CI VM
        // with no PMU HID services at all, or a machine that genuinely has none) — the
        // plausibility filter inside ReadSocTemperatureCore already rejects garbage before it
        // reaches this return, so 0 here means "no sensor passed," never "something broke."
        // Anything non-zero must still be a plausible die temperature.
        (temp == 0.0 || (temp >= 10.0 && temp <= 120.0)).Should().BeTrue(
            "the direct HID route must return either honest-unavailable (0) or a plausible die temperature (10-120 °C), never a garbage value");

        // Availability-gated (2026-07-11, PR #24 CI): on real hardware where the ground-truth
        // probe and the T5 gate review confirmed live, readable PMU tdie* sensors, a 0 here would
        // be a genuine regression — assert non-zero. On a host with no such sensors (GitHub's
        // macos-latest CI VM), 0 is the correct, expected, honest-degrade result — assert it
        // exactly rather than merely tolerating it, so a real interop regression on a
        // sensor-present host still fails loudly.
        if (MacSensorAvailability.HasIoHidTemperature)
        {
            _output.WriteLine("sensor present — full-fidelity assertions running");
            temp.Should().BeGreaterThan(0.0,
                "this host's PMU tdie* sensors are availability-detected as present, so a 0 here would indicate a regression, not hardware absence");
        }
        else
        {
            _output.WriteLine("sensor absent — degraded-mode assertions ran");
            temp.Should().Be(0.0,
                "no PMU HID temperature sensors are present on this host (e.g. a CI VM) — the honest-degrade result is exactly 0");
        }
    }

    [Fact]
    public void AppleSmc_OpenDispose_IsIdempotentAndReleasesHandle()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var available = MacSensorAvailability.HasAppleSmcCpuKey;
        _output.WriteLine(available
            ? "sensor present — full-fidelity assertions running"
            : "sensor absent — degraded-mode assertions ran");

        // Handle-leak guard: 50 open+dispose cycles must each succeed. If Dispose failed to
        // IOServiceClose the connection, the accumulating user-clients would eventually refuse to
        // open; 50 clean cycles demonstrate the connection is released each time. The provider
        // itself opens exactly one connection (EnsureSmc) and reuses it — this test proves the
        // underlying lifecycle it depends on. On a host with no AppleSMC service at all (e.g. a CI
        // VM), the equivalent honest-degrade guarantee is that Open() consistently, idempotently
        // returns null across 50 cycles rather than throwing or intermittently succeeding.
        for (int i = 0; i < 50; i++)
        {
            var smc = AppleSmc.Open();
            if (available)
            {
                smc.Should().NotBeNull($"cycle {i}: AppleSMC should still open — a leaked connection would eventually block this");
                smc!.ReadTemperature("Tp01");   // exercise a read
                smc.Dispose();
                smc.Dispose();                  // idempotent: second dispose must be a safe no-op
            }
            else
            {
                smc.Should().BeNull($"cycle {i}: AppleSMC is absent on this host (e.g. a CI VM) — Open() must degrade to null consistently, never throw");
                smc?.Dispose();                 // no-op (null), kept for structural/no-throw parity with the present-hardware branch
            }
        }
        _output.WriteLine($"50 AppleSMC open/{(available ? "read/" : "")}dispose/dispose cycles completed without failure.");
    }
}
