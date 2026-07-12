using System.Runtime.InteropServices;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Thin, blittable mirror of the community-consensus <c>SMCKeyData_t</c> layout used by
/// smcFanControl / exelban's Stats (and validated byte-for-byte on the base-M4 probe — see
/// .superpowers/sdd/sym2-ground-truth.md). Field order and widths are the ABI contract of the
/// AppleSMC user-client <c>IOConnectCallStructMethod</c> selector, so nothing here may be
/// reordered. All fields are blittable → the struct marshals with no per-call copies.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SmcVersion
{
    public byte Major;
    public byte Minor;
    public byte Build;
    public byte Reserved;
    public ushort Release;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SmcPLimitData
{
    public ushort Version;
    public ushort Length;
    public uint CpuPLimit;
    public uint GpuPLimit;
    public uint MemPLimit;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SmcKeyInfoData
{
    public uint DataSize;
    public uint DataType;
    public byte DataAttributes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SmcKeyData
{
    public uint Key;
    public SmcVersion Vers;
    public SmcPLimitData PLimitData;
    public SmcKeyInfoData KeyInfo;
    public byte Result;
    public byte Status;
    public byte Data8;
    public uint Data32;
    public fixed byte Bytes[32];
}

/// <summary>
/// Opens one AppleSMC user-client via public IOKit (<c>IOServiceGetMatchingService</c> →
/// <c>IOServiceOpen</c>), reused for every per-tick key read and closed on <see cref="Dispose"/>.
/// Reads are two <c>IOConnectCallStructMethod</c> calls per key (READ_KEYINFO then READ_BYTES)
/// and hand the raw (dataType, bytes) to <see cref="SmcTemperature.Decode"/> — no value decoding
/// lives here, keeping the type/endianness rules unit-testable off-host.
///
/// Honest-failure convention (this arc): a read that can't produce a real value returns
/// <c>null</c> (degrade), never a fabricated number; only <see cref="Open"/> surfaces a hard
/// failure as a null connection so the provider can fall back to the IOHID route.
/// </summary>
internal sealed class AppleSmc : IDisposable
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    // AppleSMC user-client selector + sub-commands (community constants, probe-validated).
    private const uint KernelIndexSmc   = 2;
    private const byte CmdReadBytes     = 5;
    private const byte CmdReadKeyInfo   = 9;

    private const int KernReturnSuccess = 0;

    private uint _connection;
    private bool _disposed;

    private AppleSmc(uint connection) => _connection = connection;

    /// <summary>Opens the AppleSMC connection, or returns <c>null</c> if the service is absent or
    /// won't open (the provider then falls back to IOHID).</summary>
    public static AppleSmc? Open()
    {
        var matching = IOServiceMatching("AppleSMC");
        if (matching == nint.Zero) return null;

        // IOServiceGetMatchingService consumes (releases) the matching dictionary — no CFRelease.
        var service = IOServiceGetMatchingService(0, matching);
        if (service == 0) return null;

        var rc = IOServiceOpen(service, LibSystem.TaskSelf, 0, out var connection);
        IOObjectRelease(service);
        if (rc != KernReturnSuccess || connection == 0) return null;

        return new AppleSmc(connection);
    }

    /// <summary>
    /// Reads one temperature key and returns its decoded °C value, or <c>null</c> if the key is
    /// absent, the SMC call fails, or the dataType isn't one we decode. Does NOT apply the
    /// plausibility filter — the caller aggregates and filters (keeps that logic pure/testable).
    /// </summary>
    public double? ReadTemperature(string key)
    {
        if (_disposed) return null;

        // Step 1: READ_KEYINFO — resolve dataSize/dataType for the key.
        var input = new SmcKeyData { Key = SmcTemperature.KeyToUInt32(key), Data8 = CmdReadKeyInfo };
        if (!Call(ref input, out var keyInfoOut)) return null;
        if (keyInfoOut.Result != 0) return null;

        var dataSize = keyInfoOut.KeyInfo.DataSize;
        var dataType = SmcTemperature.UInt32ToKey(keyInfoOut.KeyInfo.DataType);
        if (dataSize == 0 || dataSize > 32) return null;

        // Step 2: READ_BYTES — carry the resolved keyInfo forward, ask for the payload bytes.
        input.KeyInfo = keyInfoOut.KeyInfo;
        input.Data8   = CmdReadBytes;
        if (!Call(ref input, out var bytesOut)) return null;
        if (bytesOut.Result != 0) return null;

        Span<byte> payload = stackalloc byte[(int)dataSize];
        unsafe
        {
            for (int i = 0; i < (int)dataSize; i++) payload[i] = bytesOut.Bytes[i];
        }
        return SmcTemperature.Decode(dataType, payload);
    }

    private bool Call(ref SmcKeyData input, out SmcKeyData output)
    {
        output = default;
        nuint outSize = (nuint)Marshal.SizeOf<SmcKeyData>();
        var rc = IOConnectCallStructMethod(
            _connection, KernelIndexSmc,
            ref input, (nuint)Marshal.SizeOf<SmcKeyData>(),
            ref output, ref outSize);
        return rc == KernReturnSuccess;
    }

    public void Dispose()
    {
        // Idempotent dispose (matches the provider's standardized pattern): safe to call twice.
        if (_disposed) return;
        _disposed = true;
        if (_connection != 0)
        {
            IOServiceClose(_connection);
            _connection = 0;
        }
    }

    // ── IOKit P/Invoke (public functions; same binary as the IOHID fallback) ────────────────
    [DllImport(IOKit, CharSet = CharSet.Ansi)]
    private static extern nint IOServiceMatching(string name);

    [DllImport(IOKit)]
    private static extern uint IOServiceGetMatchingService(uint mainPort, nint matching);

    [DllImport(IOKit)]
    private static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);

    [DllImport(IOKit)]
    private static extern int IOServiceClose(uint connect);

    [DllImport(IOKit)]
    private static extern int IOObjectRelease(uint obj);

    [DllImport(IOKit)]
    private static extern int IOConnectCallStructMethod(
        uint connection, uint selector,
        ref SmcKeyData inputStruct, nuint inputStructCnt,
        ref SmcKeyData outputStruct, ref nuint outputStructCnt);
}
