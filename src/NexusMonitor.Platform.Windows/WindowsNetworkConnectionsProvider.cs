using System.Diagnostics;
using System.Net;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Helpers;
using NexusMonitor.Core.Models;
using NexusMonitor.Platform.Windows.Native;

namespace NexusMonitor.Platform.Windows;

/// <summary>
/// Enumerates active TCP and UDP connections via IPHelper
/// GetExtendedTcpTable / GetExtendedUdpTable.
/// Per-connection TCP throughput is tracked via GetPerTcpConnectionEStats,
/// which uses cumulative byte counters to compute byte-per-second rates.
/// EStats collection is enabled per-connection; failures (e.g. non-admin for
/// foreign-process connections) are silently swallowed — rate shows "—".
// TODO(availability-enum): ProcessName's names.GetValueOrDefault(pid, "—") fallback below is
// provider-baked, not VM-level — out of scope for the unavailable-metric-tooltips PR's
// MetricFormatting/UnavailableMetricCopy consolidation. Once P/Invoke call sites migrate to the
// structured availability channel described in CONTRIBUTING.md ("Platform code honesty
// contract"), this should express "process name lookup failed" explicitly instead of a bare
// sentinel string, so the UI layer can attach a specific tooltip reason.
/// </summary>
public sealed class WindowsNetworkConnectionsProvider : INetworkConnectionsProvider, IDisposable
{
    private const int AF_INET  = 2;
    private const int AF_INET6 = 23;

    // ── Adapter-level throughput tracker (no elevation needed) ─────────────────
    private readonly AdapterThroughputTracker _adapterTracker = new();

    // ── Per-connection EStats state ────────────────────────────────────────────
    // Key: (LocalAddr, LocalPort, RemoteAddr, RemotePort) for TCP4
    private readonly Dictionary<EStatsKey, EStatEntry> _stats = new();
    // Track connections for which SetPerTcpConnectionEStats has already been called —
    // the call is idempotent after the first enable, so skip it for known connections.
    private readonly HashSet<EStatsKey> _enabledEStats = new();

    // ── EStats capability probe ────────────────────────────────────────────────
    private bool _estatsProbed;
    private bool _estatsAvailable;

    private readonly record struct EStatsKey(uint Local, uint LocalPort, uint Remote, uint RemotePort);
    private readonly record struct EStatEntry(ulong SendBytes, ulong RecvBytes, long Ticks);

    // ── Process name cache (avoids Process.GetProcesses() per subscriber) ─────
    private Dictionary<int, string> _processNameCache = [];
    private DateTime _processNameCacheTime = DateTime.MinValue;
    private static readonly TimeSpan ProcessNameCacheTtl = TimeSpan.FromSeconds(2);

    // ── Shared multicast observable ────────────────────────────────────────────
    private IObservable<IReadOnlyList<NetworkConnection>>? _shared;
    private readonly object _sharedLock = new();
    private IDisposable? _connection;

    // ── Public interface ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool SupportsPerConnectionThroughput => !_estatsProbed || _estatsAvailable;

    public IObservable<IReadOnlyList<NetworkConnection>> GetConnectionStream(TimeSpan interval)
    {
        lock (_sharedLock)
        {
            if (_shared is null)
            {
                var sharedInterval = interval < TimeSpan.FromSeconds(2)
                    ? TimeSpan.FromSeconds(2) : interval;
                var connectable = Observable.Timer(TimeSpan.Zero, sharedInterval)
                                            .Select(_ => (IReadOnlyList<NetworkConnection>)Snapshot())
                                            .Publish();
                _shared     = connectable;
                _connection = connectable.Connect();
            }
            return _shared;
        }
    }

    public Task<IReadOnlyList<NetworkConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<NetworkConnection>>(() => Snapshot(), ct);

    public IObservable<AdapterThroughput> GetAdapterThroughputStream(TimeSpan interval) =>
        Observable.Timer(TimeSpan.Zero, interval)
                  .Select(_ => _adapterTracker.Sample());

    // ─── Snapshot ─────────────────────────────────────────────────────────────

    private List<NetworkConnection> Snapshot()
    {
        var names  = GetProcessNames();
        var result = new List<NetworkConnection>(256);
        var seen   = new HashSet<EStatsKey>();

        try { result.AddRange(GetTcp4(names, seen)); } catch { }
        try { result.AddRange(GetTcp6(names)); }       catch { }
        try { result.AddRange(GetUdp4(names)); }       catch { }
        try { result.AddRange(GetUdp6(names)); }       catch { }

        // Prune cache entries for connections that no longer exist
        foreach (var k in _stats.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _stats.Remove(k);
            _enabledEStats.Remove(k);
        }

        return result;
    }

    // ─── TCP IPv4 (with EStats throughput) ────────────────────────────────────

    private IEnumerable<NetworkConnection> GetTcp4(
        Dictionary<int, string> names, HashSet<EStatsKey> seen)
    {
        int size = 0;
        IpHelper.GetExtendedTcpTable(nint.Zero, ref size, 0, AF_INET,
            IpHelper.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) yield break;

        nint buf = Marshal.AllocHGlobal(size);
        try
        {
            if (IpHelper.GetExtendedTcpTable(buf, ref size, 1, AF_INET,
                    IpHelper.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                yield break;

            int count   = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                int pid = (int)row.dwOwningPid;

                TryGetTcpRates(ref row, seen, out long sendRate, out long recvRate);

                yield return new NetworkConnection
                {
                    Protocol        = ConnectionProtocol.Tcp4,
                    LocalAddress    = Addr4(row.dwLocalAddr),
                    LocalPort       = Port(row.dwLocalPort),
                    RemoteAddress   = Addr4(row.dwRemoteAddr),
                    RemotePort      = Port(row.dwRemotePort),
                    State           = TcpState(row.dwState),
                    ProcessId       = pid,
                    ProcessName     = names.GetValueOrDefault(pid, "—"),
                    SendBytesPerSec = sendRate,
                    RecvBytesPerSec = recvRate,
                };
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ─── TCP IPv6 ─────────────────────────────────────────────────────────────

    private static IEnumerable<NetworkConnection> GetTcp6(Dictionary<int, string> names)
    {
        int size = 0;
        IpHelper.GetExtendedTcpTable(nint.Zero, ref size, 0, AF_INET6,
            IpHelper.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) yield break;

        nint buf = Marshal.AllocHGlobal(size);
        try
        {
            if (IpHelper.GetExtendedTcpTable(buf, ref size, 1, AF_INET6,
                    IpHelper.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                yield break;

            int count   = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(buf + 4 + i * rowSize);
                int pid = (int)row.dwOwningPid;
                yield return new NetworkConnection
                {
                    Protocol      = ConnectionProtocol.Tcp6,
                    LocalAddress  = Addr6(row.ucLocalAddr),
                    LocalPort     = Port(row.dwLocalPort),
                    RemoteAddress = Addr6(row.ucRemoteAddr),
                    RemotePort    = Port(row.dwRemotePort),
                    State         = TcpState(row.dwState),
                    ProcessId     = pid,
                    ProcessName   = names.GetValueOrDefault(pid, "—"),
                };
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ─── UDP IPv4 ─────────────────────────────────────────────────────────────

    // TODO(availability-enum): RemoteAddress = "—" below (and in GetUdp6) is UDP being
    // connectionless (no remote address concept), not a failed read — provider-baked, out of
    // scope for the unavailable-metric-tooltips PR. See CONTRIBUTING.md "Platform code honesty
    // contract" for the planned structured-availability migration.
    private static IEnumerable<NetworkConnection> GetUdp4(Dictionary<int, string> names)
    {
        int size = 0;
        IpHelper.GetExtendedUdpTable(nint.Zero, ref size, 0, AF_INET,
            IpHelper.UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
        if (size == 0) yield break;

        nint buf = Marshal.AllocHGlobal(size);
        try
        {
            if (IpHelper.GetExtendedUdpTable(buf, ref size, 1, AF_INET,
                    IpHelper.UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0) != 0)
                yield break;

            int count   = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(buf + 4 + i * rowSize);
                int pid = (int)row.dwOwningPid;
                yield return new NetworkConnection
                {
                    Protocol      = ConnectionProtocol.Udp4,
                    LocalAddress  = Addr4(row.dwLocalAddr),
                    LocalPort     = Port(row.dwLocalPort),
                    RemoteAddress = "—",
                    RemotePort    = 0,
                    State         = TcpConnectionState.Unknown,
                    ProcessId     = pid,
                    ProcessName   = names.GetValueOrDefault(pid, "—"),
                };
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ─── UDP IPv6 ─────────────────────────────────────────────────────────────

    private static IEnumerable<NetworkConnection> GetUdp6(Dictionary<int, string> names)
    {
        int size = 0;
        IpHelper.GetExtendedUdpTable(nint.Zero, ref size, 0, AF_INET6,
            IpHelper.UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
        if (size == 0) yield break;

        nint buf = Marshal.AllocHGlobal(size);
        try
        {
            if (IpHelper.GetExtendedUdpTable(buf, ref size, 1, AF_INET6,
                    IpHelper.UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0) != 0)
                yield break;

            int count   = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(buf + 4 + i * rowSize);
                int pid = (int)row.dwOwningPid;
                yield return new NetworkConnection
                {
                    Protocol      = ConnectionProtocol.Udp6,
                    LocalAddress  = Addr6(row.ucLocalAddr),
                    LocalPort     = Port(row.dwLocalPort),
                    RemoteAddress = "—",
                    RemotePort    = 0,
                    State         = TcpConnectionState.Unknown,
                    ProcessId     = pid,
                    ProcessName   = names.GetValueOrDefault(pid, "—"),
                };
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ─── EStats throughput helper ──────────────────────────────────────────────

    /// <summary>
    /// Attempts to read per-TCP-connection cumulative byte counters via
    /// <c>GetPerTcpConnectionEStats</c> and convert them to per-second rates
    /// using the delta from the previous snapshot.
    /// Silently returns 0/0 on any failure (no admin, connection closed, etc.).
    /// </summary>
    private unsafe void TryGetTcpRates(
        ref MIB_TCPROW_OWNER_PID row,
        HashSet<EStatsKey> seen,
        out long sendRate,
        out long recvRate)
    {
        sendRate = 0;
        recvRate = 0;

        var tcpRow = new MIB_TCPROW_FOR_ESTATS
        {
            dwState      = row.dwState,
            dwLocalAddr  = row.dwLocalAddr,
            dwLocalPort  = row.dwLocalPort,
            dwRemoteAddr = row.dwRemoteAddr,
            dwRemotePort = row.dwRemotePort,
        };

        var key = new EStatsKey(row.dwLocalAddr, row.dwLocalPort, row.dwRemoteAddr, row.dwRemotePort);

        // Enable EStats data collection once per connection — idempotent after first call;
        // skip the kernel transition for connections we have already enabled.
        if (!_enabledEStats.Contains(key))
        {
            var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
            IpHelper.SetPerTcpConnectionEStats(
                ref tcpRow, IpHelper.TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                (nint)(&rw), 0, (uint)sizeof(TCP_ESTATS_DATA_RW_v0), 0);
            _enabledEStats.Add(key);
        }

        // Read the cumulative byte counters.
        TCP_ESTATS_DATA_ROD_v0 rod = default;
        uint hr = IpHelper.GetPerTcpConnectionEStats(
            ref tcpRow, IpHelper.TCP_ESTATS_TYPE.TcpConnectionEstatsData,
            nint.Zero, 0, 0,
            nint.Zero, 0, 0,
            (nint)(&rod), 0, (uint)sizeof(TCP_ESTATS_DATA_ROD_v0));

        if (hr != 0)
        {
            // Record failure on first ESTABLISHED connection — driver doesn't support EStats.
            if (!_estatsProbed && row.dwState == 5)
            {
                _estatsAvailable = false;
                _estatsProbed    = true;
            }
            return;
        }

        _estatsAvailable = true;
        _estatsProbed    = true;

        var now  = DateTime.UtcNow.Ticks;

        seen.Add(key);

        if (_stats.TryGetValue(key, out var prev))
        {
            double elapsed = (now - prev.Ticks) / (double)TimeSpan.TicksPerSecond;
            if (elapsed >= 0.1
                && rod.DataBytesOut >= prev.SendBytes
                && rod.DataBytesIn  >= prev.RecvBytes)
            {
                sendRate = (long)((rod.DataBytesOut - prev.SendBytes) / elapsed);
                recvRate = (long)((rod.DataBytesIn  - prev.RecvBytes) / elapsed);
            }
        }

        _stats[key] = new EStatEntry(rod.DataBytesOut, rod.DataBytesIn, now);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private Dictionary<int, string> GetProcessNames()
    {
        var now = DateTime.UtcNow;
        if (_processNameCache.Count > 0 && (now - _processNameCacheTime) < ProcessNameCacheTtl)
            return _processNameCache;

        try
        {
            var procs = Process.GetProcesses();
            var dict  = new Dictionary<int, string>(procs.Length);
            foreach (var p in procs)
            {
                try { dict[p.Id] = p.ProcessName; } catch { }
                p.Dispose();
            }
            _processNameCache    = dict;
            _processNameCacheTime = now;
            return dict;
        }
        catch { return _processNameCache; }
    }

    // Port: DWORD stores a 16-bit value in network (big-endian) byte order.
    private static int Port(uint p) =>
        (IPAddress.NetworkToHostOrder((short)(p & 0xFFFF)) & 0xFFFF);

    // IPv4 address: DWORD in network byte order — BitConverter gives bytes in
    // memory order which IPAddress(byte[]) expects in network order. ✓
    private static string Addr4(uint a) =>
        new IPAddress(BitConverter.GetBytes(a)).ToString();

    private static string Addr6(byte[]? a) =>
        a is null ? "::" : new IPAddress(a).ToString();

    public void Dispose()
    {
        lock (_sharedLock) { _connection?.Dispose(); }
    }

    private static TcpConnectionState TcpState(uint s) => s switch
    {
        1  => TcpConnectionState.Closed,
        2  => TcpConnectionState.Listen,
        3  => TcpConnectionState.SynSent,
        4  => TcpConnectionState.SynReceived,
        5  => TcpConnectionState.Established,
        6  => TcpConnectionState.FinWait1,
        7  => TcpConnectionState.FinWait2,
        8  => TcpConnectionState.CloseWait,
        9  => TcpConnectionState.Closing,
        10 => TcpConnectionState.LastAck,
        11 => TcpConnectionState.TimeWait,
        12 => TcpConnectionState.DeleteTcb,
        _  => TcpConnectionState.Unknown,
    };
}
