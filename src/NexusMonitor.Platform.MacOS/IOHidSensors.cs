using System.Runtime.InteropServices;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Fallback SoC-temperature route for the CPU slot, used only when the AppleSMC primary route
/// yields no plausible CPU key (older chips / OS variance). Reads the IOHIDEventSystemClient
/// temperature sensors (private-but-stable API in the same IOKit binary; page 0xff00 / usage 5),
/// averaging the <c>PMU … tdie*</c> die sensors — excluding <c>PMU tcal</c>, which is a constant
/// ~51.8 °C calibration reference, not a live temperature (probe-verified). Names differ per
/// OS/chip, so matching is prefix/substring-defensive; no passing sensor → honest 0.
///
/// This runs per-tick ONLY as a fallback. Every CoreFoundation object obtained from a Copy* call
/// is released before return (CFRelease discipline), and any interop failure degrades to 0 rather
/// than throwing (honest-failure convention: reads degrade).
/// </summary>
internal static class IOHidSensors
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const long IOHIDEventTypeTemperature = 15;
    private const uint  KCFStringEncodingUTF8    = 0x08000100;
    private const int   KCFNumberSInt32Type      = 3;

    // Field selector for IOHIDEventGetFloatValue on a temperature event: (type << 16).
    private static readonly int TemperatureField = (int)(IOHIDEventTypeTemperature << 16);

    /// <summary>
    /// Returns the mean plausible <c>PMU … tdie*</c> die temperature (°C), or <c>0</c> if the HID
    /// route is unavailable or no die sensor passes the plausibility filter.
    /// </summary>
    public static double ReadSocTemperature()
    {
        try
        {
            return ReadSocTemperatureCore();
        }
        catch
        {
            return 0.0;
        }
    }

    private static double ReadSocTemperatureCore()
    {
        var client = IOHIDEventSystemClientCreate(nint.Zero);
        if (client == nint.Zero) return 0.0;

        nint pageKey = nint.Zero, usageKey = nint.Zero, pageNum = nint.Zero, usageNum = nint.Zero;
        nint matching = nint.Zero, services = nint.Zero;
        try
        {
            pageKey  = CFStr("PrimaryUsagePage");
            usageKey = CFStr("PrimaryUsage");
            int page = 0xff00, usage = 5;
            pageNum  = CFNumberCreate(nint.Zero, KCFNumberSInt32Type, ref page);
            usageNum = CFNumberCreate(nint.Zero, KCFNumberSInt32Type, ref usage);
            if (pageKey == nint.Zero || usageKey == nint.Zero || pageNum == nint.Zero || usageNum == nint.Zero)
                return 0.0;

            var keys = new[] { pageKey, usageKey };
            var vals = new[] { pageNum, usageNum };
            matching = CFDictionaryCreate(nint.Zero, keys, vals, 2,
                CfTypeDictionaryKeyCallBacks, CfTypeDictionaryValueCallBacks);
            if (matching == nint.Zero) return 0.0;

            IOHIDEventSystemClientSetMatching(client, matching);
            services = IOHIDEventSystemClientCopyServices(client);
            if (services == nint.Zero) return 0.0;

            var count = CFArrayGetCount(services);
            double sum = 0;
            int n = 0;
            for (nint i = 0; i < count; i++)
            {
                // Borrowed reference from the array — do NOT release.
                var svc = CFArrayGetValueAtIndex(services, i);
                if (svc == nint.Zero) continue;

                var name = ReadProductName(svc);   // may be null
                if (name is null) continue;
                if (name.IndexOf("tcal", StringComparison.OrdinalIgnoreCase) >= 0) continue; // calibration ref
                if (name.IndexOf("tdie", StringComparison.OrdinalIgnoreCase) < 0)  continue; // die sensors only

                var evt = IOHIDServiceClientCopyEvent(svc, IOHIDEventTypeTemperature, 0, 0);
                if (evt == nint.Zero) continue;
                try
                {
                    var value = IOHIDEventGetFloatValue(evt, TemperatureField);
                    if (SmcTemperature.IsPlausible(value)) { sum += value; n++; }
                }
                finally
                {
                    CFRelease(evt);
                }
            }

            return n == 0 ? 0.0 : sum / n;
        }
        finally
        {
            if (services != nint.Zero) CFRelease(services);
            if (matching != nint.Zero) CFRelease(matching);
            if (usageNum != nint.Zero) CFRelease(usageNum);
            if (pageNum  != nint.Zero) CFRelease(pageNum);
            if (usageKey != nint.Zero) CFRelease(usageKey);
            if (pageKey  != nint.Zero) CFRelease(pageKey);
            CFRelease(client);
        }
    }

    /// <summary>Reads the "Product" string property of a HID service, releasing the CF string.</summary>
    private static string? ReadProductName(nint service)
    {
        var key = CFStr("Product");
        if (key == nint.Zero) return null;
        nint prop = nint.Zero;
        try
        {
            prop = IOHIDServiceClientCopyProperty(service, key);
            if (prop == nint.Zero) return null;
            if (CFGetTypeID(prop) != CFStringGetTypeID()) return null;

            Span<byte> buf = stackalloc byte[256];
            unsafe
            {
                fixed (byte* p = buf)
                {
                    if (!CFStringGetCString(prop, (nint)p, buf.Length, KCFStringEncodingUTF8))
                        return null;
                    return Marshal.PtrToStringUTF8((nint)p);
                }
            }
        }
        finally
        {
            if (prop != nint.Zero) CFRelease(prop);
            CFRelease(key);
        }
    }

    private static nint CFStr(string s) =>
        CFStringCreateWithCString(nint.Zero, s, KCFStringEncodingUTF8);

    // ── CoreFoundation callback-struct globals (passed by address to CFDictionaryCreate) ────
    private static readonly nint CfTypeDictionaryKeyCallBacks   = ExportAddr("kCFTypeDictionaryKeyCallBacks");
    private static readonly nint CfTypeDictionaryValueCallBacks = ExportAddr("kCFTypeDictionaryValueCallBacks");

    private static nint ExportAddr(string symbol)
    {
        try
        {
            var handle = NativeLibrary.Load(CoreFoundation);
            return NativeLibrary.GetExport(handle, symbol);
        }
        catch
        {
            return nint.Zero;
        }
    }

    // ── IOHIDEventSystemClient (private-but-stable, IOKit binary) ────────────────────────────
    [DllImport(IOKit)]
    private static extern nint IOHIDEventSystemClientCreate(nint allocator);

    [DllImport(IOKit)]
    private static extern int IOHIDEventSystemClientSetMatching(nint client, nint matching);

    [DllImport(IOKit)]
    private static extern nint IOHIDEventSystemClientCopyServices(nint client);

    [DllImport(IOKit)]
    private static extern nint IOHIDServiceClientCopyProperty(nint service, nint key);

    [DllImport(IOKit)]
    private static extern nint IOHIDServiceClientCopyEvent(nint service, long type, int options, long unused);

    [DllImport(IOKit)]
    private static extern double IOHIDEventGetFloatValue(nint @event, int field);

    // ── CoreFoundation P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
    private static extern nint CFStringCreateWithCString(nint alloc, string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nint CFNumberCreate(nint alloc, int theType, ref int valuePtr);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreate(
        nint alloc, nint[] keys, nint[] values, nint numValues, nint keyCallBacks, nint valueCallBacks);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

    [DllImport(CoreFoundation)]
    private static extern nint CFGetTypeID(nint cf);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetTypeID();

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint theString, nint buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);
}
