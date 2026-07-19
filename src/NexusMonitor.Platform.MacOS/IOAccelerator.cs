using System.Runtime.InteropServices;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Matches every "IOAccelerator" service in the public IOKit registry
/// (<c>IOServiceGetMatchingServices</c> → <c>IOIteratorNext</c>), reused for every per-tick
/// <see cref="ReadPerformanceStatistics"/> call and released on <see cref="Dispose"/> — the same
/// open/reuse/Dispose discipline <see cref="AppleSmc"/> established for Task 5.
///
/// Per tick, <c>IORegistryEntryCreateCFProperties</c> allocates a fresh properties CFDictionary
/// for each held entry — that dictionary (and every short-lived CFString key created to look
/// inside it) is released before the method moves to the next entry or returns, every tick, so a
/// multi-hour run at ~1s ticks never accumulates CoreFoundation objects. The registry entry
/// handles themselves (io_object_t) are the only long-lived objects, held across ticks and
/// released once in <see cref="Dispose"/>.
///
/// Honest-failure convention (this arc): a read that can't produce a real sample returns
/// <c>null</c> (degrade) — this class has no mutating surface.
/// </summary>
internal sealed class IOAccelerator : IDisposable
{
    private const string IOKit           = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation  = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int  KernReturnSuccess     = 0;
    private const uint KCFStringEncodingUTF8 = 0x08000100;
    private const int  KCFNumberSInt64Type   = 4;

    private uint[] _entries;
    private bool   _disposed;

    private IOAccelerator(uint[] entries) => _entries = entries;

    // TODO(availability-enum): Open() returning null and ReadMemoryBytes (GpuPerformanceStats.cs)
    // returning null both already distinguish "no accelerator node" / "key absent" from a real
    // zero — but that reason is discarded by the time it reaches GpuMetrics (a non-nullable
    // record) further up the stack, so the VM only ever sees "total is 0" with no way to know
    // whether that's "no dedicated VRAM pool" (macOS/Apple Silicon — GpuDeviceViewModel already
    // infers this locally via OperatingSystem.IsMacOS()) or something else entirely. See
    // CONTRIBUTING.md "Platform code honesty contract" for the planned structured-availability
    // channel that would carry this reason all the way to the tooltip instead of it being
    // re-inferred at the VM.
    /// <summary>
    /// Matches every "IOAccelerator" service in the IOKit registry and keeps their handles open
    /// for reuse. Returns <c>null</c> if none are found (older hardware / no GPU accelerator
    /// node) — the provider then reports GPU utilization/memory as unavailable (0).
    /// </summary>
    public static IOAccelerator? Open()
    {
        var matching = IOServiceMatching("IOAccelerator");
        if (matching == nint.Zero) return null;

        // IOServiceGetMatchingServices consumes (releases) the matching dictionary — no CFRelease.
        var kr = IOServiceGetMatchingServices(0, matching, out var iterator);
        if (kr != KernReturnSuccess || iterator == 0) return null;

        var entries = new List<uint>();
        uint entry;
        while ((entry = IOIteratorNext(iterator)) != 0)
            entries.Add(entry);
        IOObjectRelease(iterator);

        if (entries.Count == 0) return null;
        return new IOAccelerator(entries.ToArray());
    }

    /// <summary>
    /// Reads "PerformanceStatistics" from the first held entry that exposes a usable utilization
    /// key (see <see cref="GpuPerformanceStats.HasUtilizationKey"/>), or <c>null</c> if no held
    /// entry has one. Multi-accelerator machines are handled by iterating all held entries every
    /// tick; on this M4 exactly one entry is expected to match.
    /// </summary>
    public GpuPerformanceSample? ReadPerformanceStatistics()
    {
        if (_disposed) return null;

        foreach (var entry in _entries)
        {
            var kr = IORegistryEntryCreateCFProperties(entry, out var props, nint.Zero, 0);
            if (kr != KernReturnSuccess || props == nint.Zero) continue;
            try
            {
                var stats = ReadPerformanceStatisticsDictionary(props);
                if (stats is null) continue;
                if (!GpuPerformanceStats.HasUtilizationKey(stats)) continue;

                var utilization = GpuPerformanceStats.SelectUtilization(stats);
                if (utilization is null) continue; // guarded above by HasUtilizationKey; defensive only

                // C4: ReadMemoryBytes distinguishes "key absent" (null — honest unavailable) from
                // "key present with value 0" (a genuinely reported zero); plain TryGetValue alone
                // left both as the same fabricated 0.
                // TODO(availability-enum): inUse/alloc's null-vs-zero distinction is still lost
                // once GpuPerformanceSample flows into the non-nullable GpuMetrics further up the
                // stack (see the TODO on IOAccelerator.Open() above and CONTRIBUTING.md
                // "Platform code honesty contract").
                var inUse  = GpuPerformanceStats.ReadMemoryBytes(stats, GpuPerformanceStats.InUseSystemMemoryKey);
                var alloc  = GpuPerformanceStats.ReadMemoryBytes(stats, GpuPerformanceStats.AllocSystemMemoryKey);

                return new GpuPerformanceSample(
                    utilization.Value,
                    inUse,
                    alloc);
            }
            finally
            {
                CFRelease(props);
            }
        }
        return null;
    }

    /// <summary>
    /// Looks up "PerformanceStatistics" inside <paramref name="props"/> and, if present, decodes
    /// the known keys into a plain dictionary. Every CFString key created to do the lookups is
    /// released before returning — <paramref name="props"/> itself (and the borrowed
    /// "PerformanceStatistics" sub-dictionary and CFNumber values inside it, which the caller does
    /// not separately retain) are released by the caller's single <c>CFRelease(props)</c>.
    /// </summary>
    private static Dictionary<string, long>? ReadPerformanceStatisticsDictionary(nint props)
    {
        var perfKey = CFStr("PerformanceStatistics");
        if (perfKey == nint.Zero) return null;
        nint perfStats;
        try
        {
            perfStats = CFDictionaryGetValue(props, perfKey);
        }
        finally
        {
            CFRelease(perfKey);
        }
        if (perfStats == nint.Zero) return null;

        var result = new Dictionary<string, long>(4, StringComparer.Ordinal);
        TryReadNumber(perfStats, GpuPerformanceStats.DeviceUtilizationKey, result);
        TryReadNumber(perfStats, GpuPerformanceStats.GpuActivityKey, result);
        TryReadNumber(perfStats, GpuPerformanceStats.InUseSystemMemoryKey, result);
        TryReadNumber(perfStats, GpuPerformanceStats.AllocSystemMemoryKey, result);
        return result;
    }

    /// <summary>Reads one CFNumber value out of <paramref name="dict"/> by string key, adding it to
    /// <paramref name="result"/> only if present and actually a CFNumber. The lookup CFString key
    /// is created and released within this call — never held past it.</summary>
    private static void TryReadNumber(nint dict, string keyName, Dictionary<string, long> result)
    {
        var key = CFStr(keyName);
        if (key == nint.Zero) return;
        try
        {
            var value = CFDictionaryGetValue(dict, key);
            if (value == nint.Zero) return;
            if (CFGetTypeID(value) != CFNumberGetTypeID()) return;
            if (CFNumberGetValue(value, KCFNumberSInt64Type, out long l)) result[keyName] = l;
        }
        finally
        {
            CFRelease(key);
        }
    }

    private static nint CFStr(string s) =>
        CFStringCreateWithCString(nint.Zero, s, KCFStringEncodingUTF8);

    public void Dispose()
    {
        // Idempotent dispose (matches AppleSmc's pattern): safe to call twice.
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries)
            IOObjectRelease(entry);
        _entries = Array.Empty<uint>();
    }

    // ── IOKit P/Invoke (public functions; same binary as AppleSmc/IOHidSensors) ─────────────
    [DllImport(IOKit, CharSet = CharSet.Ansi)]
    private static extern nint IOServiceMatching(string name);

    [DllImport(IOKit)]
    private static extern int IOServiceGetMatchingServices(uint mainPort, nint matching, out uint iterator);

    [DllImport(IOKit)]
    private static extern uint IOIteratorNext(uint iterator);

    [DllImport(IOKit)]
    private static extern int IOObjectRelease(uint obj);

    [DllImport(IOKit)]
    private static extern int IORegistryEntryCreateCFProperties(
        uint entry, out nint properties, nint allocator, uint options);

    // ── CoreFoundation P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
    private static extern nint CFStringCreateWithCString(nint alloc, string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetValue(nint theDict, nint key);

    [DllImport(CoreFoundation)]
    private static extern nint CFGetTypeID(nint cf);

    [DllImport(CoreFoundation)]
    private static extern nint CFNumberGetTypeID();

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(nint number, int theType, out long value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);
}
