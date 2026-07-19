using System.Runtime.InteropServices;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Pure, OS-agnostic helpers for macOS thermal sensing: SMC value-type decoders, the
/// physical-plausibility filter, per-sensor-set aggregation, and the per-generation SMC key
/// tables. No P/Invoke lives here — <see cref="AppleSmc"/> does the IOKit calls and feeds raw
/// (dataType, bytes) tuples through <see cref="Decode"/> — so every rule below is unit-testable
/// on any host (Windows/Linux CI included), mirroring the pure/IO split used by
/// <see cref="MacOSEfficiencyMode"/> (Sym-1 Task 4) and <c>LaunchdStartType</c> (Sym-1 Task 3).
///
/// SMC key tables ported from exelban/stats (github.com/exelban/stats, MIT License) —
/// Modules/Sensors/values.swift — with two deliberate, probe-driven corrections for the base
/// M4 documented on <see cref="AppleSiliconTempKeys"/> for M4.
/// </summary>
internal static class SmcTemperature
{
    /// <summary>Minimum accepted temperature (°C). Below this is treated as an unreliable/garbage
    /// sensor reading and discarded — e.g. the base-M4 Mac mini's Tg* GPU keys return -4.5/0.8 °C
    /// (probe-verified physically impossible), which this filter rejects so GPU temp reports as
    /// honestly unavailable rather than shipping a fabricated sub-freezing value.</summary>
    public const double MinPlausibleCelsius = 10.0;

    /// <summary>Maximum accepted temperature (°C). Above this is treated as garbage.</summary>
    public const double MaxPlausibleCelsius = 120.0;

    public static bool IsPlausible(double celsius) =>
        celsius >= MinPlausibleCelsius && celsius <= MaxPlausibleCelsius;

    /// <summary>
    /// Decodes an SMC value from its 4-character <paramref name="dataType"/> FourCC and raw bytes.
    /// Type-driven (never assumed): the decoder branches on the dataType the SMC itself reported
    /// for the key, so an <c>flt </c> key on Apple Silicon and an <c>sp78</c> key on Intel are
    /// each read correctly. Returns <c>null</c> for an unknown type or a size that doesn't match
    /// the type (caller treats null as "no reading from this key").
    /// </summary>
    /// <remarks>
    /// Endianness (validated against the base-M4 probe): <c>flt </c> is little-endian float32
    /// (Apple Silicon), <c>sp78</c> is big-endian fixed-point (Intel), the unsigned integer types
    /// are big-endian, and <c>ioft</c> is a big-endian 64-bit fixed-point value scaled by 65536.
    /// </remarks>
    public static double? Decode(string dataType, ReadOnlySpan<byte> bytes)
    {
        switch (dataType)
        {
            case "flt ":
                if (bytes.Length < 4) return null;
                // Little-endian float32. On the (LE) Apple platforms this always runs on,
                // BitConverter reads native LE directly; guard the theoretical BE host anyway.
                if (BitConverter.IsLittleEndian)
                    return BitConverter.ToSingle(bytes[..4]);
                Span<byte> le = stackalloc byte[4];
                bytes[..4].CopyTo(le);
                le.Reverse();
                return BitConverter.ToSingle(le);

            case "sp78":
                // Big-endian signed 8.8 fixed-point (Intel classic thermal encoding).
                if (bytes.Length < 2) return null;
                return (short)((bytes[0] << 8) | bytes[1]) / 256.0;

            case "ui8 ":
                if (bytes.Length < 1) return null;
                return bytes[0];

            case "ui16":
                if (bytes.Length < 2) return null;
                return (bytes[0] << 8) | bytes[1];

            case "ui32":
                if (bytes.Length < 4) return null;
                return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

            case "ioft":
                // Big-endian 64-bit fixed-point, 65536 scale.
                if (bytes.Length < 8) return null;
                ulong v = 0;
                for (int i = 0; i < 8; i++) v = (v << 8) | bytes[i];
                return v / 65536.0;

            default:
                return null;
        }
    }

    /// <summary>
    /// Mean of the plausibility-passing readings, or <c>0</c> (the repo's "unavailable" sentinel)
    /// when none pass. Callers pass already-decoded values; this applies the plausibility filter
    /// and averages the survivors, so a mix of good and garbage keys yields the mean of the good
    /// ones and an all-garbage set yields honest 0.
    /// </summary>
    public static double MeanOfPlausible(IEnumerable<double> readings)
    {
        double sum = 0;
        int n = 0;
        foreach (var r in readings)
        {
            if (!IsPlausible(r)) continue;
            sum += r;
            n++;
        }
        return n == 0 ? 0.0 : sum / n;
    }

    /// <summary>Packs a 4-character SMC key into the big-endian uint32 the SMC protocol uses,
    /// matching the community <c>strtokey</c> (<c>s[0]&lt;&lt;24 | s[1]&lt;&lt;16 | s[2]&lt;&lt;8 | s[3]</c>).</summary>
    public static uint KeyToUInt32(string key)
    {
        if (key.Length != 4) throw new ArgumentException("SMC key must be exactly 4 characters", nameof(key));
        return ((uint)key[0] << 24) | ((uint)key[1] << 16) | ((uint)key[2] << 8) | key[3];
    }

    /// <summary>Unpacks a big-endian uint32 SMC FourCC (key or dataType) back to its 4-character
    /// string (matching the community <c>keytostr</c>). Used to turn the reported dataType into a
    /// string for <see cref="Decode"/>.</summary>
    public static string UInt32ToKey(uint value)
    {
        Span<char> c = stackalloc char[4];
        c[0] = (char)((value >> 24) & 0xff);
        c[1] = (char)((value >> 16) & 0xff);
        c[2] = (char)((value >> 8) & 0xff);
        c[3] = (char)(value & 0xff);
        return new string(c);
    }

    /// <summary>
    /// Resolves the CPU (performance + efficiency) and GPU SMC key sets for a machine from its
    /// <c>machdep.cpu.brand_string</c> and architecture. Called once at startup; the returned
    /// sets are read every tick. An unknown/future Apple Silicon generation falls back to the
    /// union of all known Apple Silicon tables — the plausibility filter makes wrong keys harmless
    /// (worst case: honest unavailability, never garbage).
    /// </summary>
    public static TempKeySet ResolveKeySet(string brandString, bool isArm64)
    {
        if (!isArm64)
            return IntelTempKeys.Set;

        var b = brandString ?? string.Empty;
        // Token match with a digit boundary, NOT a plain Contains: "Apple M10" contains the
        // substring "M1", so a Contains check would silently hand a future two-digit
        // generation the M1 table. A token followed by another digit is a different
        // generation and must fall through to the union.
        if (HasGenerationToken(b, "M1")) return AppleSiliconTempKeys.M1;
        if (HasGenerationToken(b, "M2")) return AppleSiliconTempKeys.M2;
        if (HasGenerationToken(b, "M3")) return AppleSiliconTempKeys.M3;
        if (HasGenerationToken(b, "M4")) return AppleSiliconTempKeys.M4;
        if (HasGenerationToken(b, "M5")) return AppleSiliconTempKeys.M5;

        return AppleSiliconTempKeys.UnknownUnion;
    }

    private static bool HasGenerationToken(string brand, string token)
    {
        for (int i = brand.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = brand.IndexOf(token, i + 1, StringComparison.Ordinal))
        {
            int end = i + token.Length;
            bool startOk = i == 0 || !char.IsLetterOrDigit(brand[i - 1]);
            bool endOk   = end == brand.Length || !char.IsDigit(brand[end]);
            if (startOk && endOk) return true;
        }
        return false;
    }
}

/// <summary>Resolved SMC key sets for one machine: performance-core, efficiency-core, and GPU
/// temperature keys. CPU temp is the mean of passing performance keys, falling back to the
/// efficiency set; GPU temp is the mean of passing GPU keys.</summary>
internal sealed record TempKeySet(string[] CpuPerformance, string[] CpuEfficiency, string[] Gpu);

/// <summary>
/// Per-generation Apple Silicon SMC temperature key tables, ported from exelban/stats
/// (Modules/Sensors/values.swift, MIT License).
///
/// Two probe-driven corrections for the base M4 (this machine — see
/// .superpowers/sdd/sym2-ground-truth.md): (1) the efficiency-core set is
/// <c>Tp00/Tp04/Tp08/Tp0C</c> (confirmed ≈36 °C live), because the Stats <c>Te0*</c> M4
/// efficiency keys are absent from this machine's SMC key space; (2) the M4 performance set is
/// <c>Tp01/Tp05/Tp09/Tp0D/Tp0V/Tp0Y/Tp0b/Tp0e</c> (confirmed ≈45 °C live), matching Stats.
/// </summary>
internal static class AppleSiliconTempKeys
{
    public static readonly TempKeySet M1 = new(
        CpuPerformance: new[] { "Tp01", "Tp05", "Tp0D", "Tp0H", "Tp0L", "Tp0P", "Tp0X", "Tp0b" },
        CpuEfficiency:  new[] { "Tp09", "Tp0T" },
        Gpu:            new[] { "Tg05", "Tg0D", "Tg0L", "Tg0T" });

    public static readonly TempKeySet M2 = new(
        CpuPerformance: new[] { "Tp01", "Tp05", "Tp09", "Tp0D", "Tp0X", "Tp0b", "Tp0f", "Tp0j" },
        CpuEfficiency:  new[] { "Tp1h", "Tp1t", "Tp1p", "Tp1l" },
        Gpu:            new[] { "Tg0f", "Tg0j" });

    public static readonly TempKeySet M3 = new(
        CpuPerformance: new[] { "Tf04", "Tf09", "Tf0A", "Tf0B", "Tf0D", "Tf0E", "Tf44", "Tf49", "Tf4A", "Tf4B", "Tf4D", "Tf4E" },
        CpuEfficiency:  new[] { "Te05", "Te0L", "Te0P", "Te0S" },
        Gpu:            new[] { "Tf14", "Tf18", "Tf19", "Tf1A", "Tf24", "Tf28", "Tf29", "Tf2A" });

    // Base M4: perf set matches Stats; efficiency set + GPU set are the probe-confirmed keys on
    // this machine. GPU set includes the M4 Pro/Max keys (Tg1U/Tg1k) too — harmless on base M4
    // (absent → no reading), correct on the higher-tier parts.
    public static readonly TempKeySet M4 = new(
        CpuPerformance: new[] { "Tp01", "Tp05", "Tp09", "Tp0D", "Tp0V", "Tp0Y", "Tp0b", "Tp0e" },
        CpuEfficiency:  new[] { "Tp00", "Tp04", "Tp08", "Tp0C" },
        Gpu:            new[] { "Tg0G", "Tg0H", "Tg0K", "Tg0L", "Tg0d", "Tg0e", "Tg0j", "Tg0k", "Tg1U", "Tg1k" });

    // M5 (Stats): "super" + performance cores treated together as the performance tier; Stats
    // lists no separate efficiency set for M5.
    public static readonly TempKeySet M5 = new(
        CpuPerformance: new[]
        {
            "Tp00", "Tp04", "Tp08", "Tp0C", "Tp0G", "Tp0K",
            "Tp0O", "Tp0R", "Tp0U", "Tp0X", "Tp0a", "Tp0d",
            "Tp0g", "Tp0j", "Tp0m", "Tp0p", "Tp0u", "Tp0y",
        },
        CpuEfficiency:  Array.Empty<string>(),
        Gpu:            new[] { "Tg0U", "Tg0X", "Tg0d", "Tg0g", "Tg0j", "Tg1Y", "Tg1c", "Tg1g" });

    /// <summary>Union of every known Apple Silicon table — used for an unknown/future generation.
    /// The plausibility filter discards keys that don't belong to the actual chip.</summary>
    public static readonly TempKeySet UnknownUnion = new(
        CpuPerformance: Union(M1.CpuPerformance, M2.CpuPerformance, M3.CpuPerformance, M4.CpuPerformance, M5.CpuPerformance),
        CpuEfficiency:  Union(M1.CpuEfficiency,  M2.CpuEfficiency,  M3.CpuEfficiency,  M4.CpuEfficiency,  M5.CpuEfficiency),
        Gpu:            Union(M1.Gpu, M2.Gpu, M3.Gpu, M4.Gpu, M5.Gpu));

    private static string[] Union(params string[][] sets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var set in sets)
            foreach (var k in set)
                if (seen.Add(k)) result.Add(k);
        return result.ToArray();
    }
}

/// <summary>Intel Mac SMC temperature keys (sp78-encoded). Ported from exelban/stats
/// (Modules/Sensors/values.swift, MIT License).</summary>
internal static class IntelTempKeys
{
    public static readonly TempKeySet Set = new(
        CpuPerformance: new[] { "TC0P", "TC0D", "TC0E", "TC0F", "TC0H" },
        CpuEfficiency:  Array.Empty<string>(),
        Gpu:            new[] { "TG0P", "TG0D", "TG0H", "TCGC" });
}
