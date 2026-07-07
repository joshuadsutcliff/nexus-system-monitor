using System.Diagnostics;
using System.Reactive.Linq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Helpers;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

public sealed class MacOSNetworkConnectionsProvider : INetworkConnectionsProvider, IDisposable
{
    private readonly AdapterThroughputTracker _adapterTracker = new();

    // Connection result cache — netstat subprocesses are expensive (fork/exec × 4).
    // Connections don't churn every 2 s, so cache for 5 s.
    private static readonly TimeSpan ConnectionsCacheDuration = TimeSpan.FromSeconds(5);
    private IReadOnlyList<NetworkConnection> _cachedConnections = Array.Empty<NetworkConnection>();
    private DateTime _connectionsCacheTime = DateTime.MinValue;

    private IObservable<IReadOnlyList<NetworkConnection>>? _shared;
    private TimeSpan _sharedInterval;
    private readonly object _sharedLock = new();
    private IDisposable? _connection;

    public bool SupportsPerConnectionThroughput => false;

    public IObservable<IReadOnlyList<NetworkConnection>> GetConnectionStream(TimeSpan interval)
    {
        lock (_sharedLock)
        {
            var clampedInterval = interval < TimeSpan.FromSeconds(2)
                ? TimeSpan.FromSeconds(2) : interval;
            if (_shared is not null && _sharedInterval != clampedInterval)
            {
                _connection?.Dispose();
                _shared = null;
            }
            if (_shared is null)
            {
                _sharedInterval = clampedInterval;
                var connectable = Observable.Timer(TimeSpan.Zero, clampedInterval)
                                            .Select(_ => (IReadOnlyList<NetworkConnection>)GetConnections())
                                            .Publish();
                _shared     = connectable;
                _connection = connectable.Connect();
            }
            return _shared;
        }
    }

    public Task<IReadOnlyList<NetworkConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<NetworkConnection>>(GetConnections, ct);

    public IObservable<AdapterThroughput> GetAdapterThroughputStream(TimeSpan interval) =>
        Observable.Timer(TimeSpan.Zero, interval)
                  .Select(_ => _adapterTracker.Sample());

    private IReadOnlyList<NetworkConnection> GetConnections()
    {
        var now = DateTime.UtcNow;
        if ((now - _connectionsCacheTime) < ConnectionsCacheDuration)
            return _cachedConnections;

        var result = new List<NetworkConnection>();
        try
        {
            // 4 subprocesses, cache result for 5 s.
            // BSD/macOS netstat rejects "-p tcp6"/"-p udp6" (unknown/uninstrumented protocol);
            // IPv6 sockets require the "-f inet6" family flag instead. The IPv4 passes must be
            // family-filtered too ("-f inet"): a bare "-p tcp" lists ALL families, so v6 rows
            // would be double-counted by the inet6 pass. Dual-stack ("tcp46"/"udp46") sockets
            // are listed by BOTH families — ParseNetstat attributes them to the IPv4 pass only.
            ParseNetstat("-anv -f inet -p tcp",   ConnectionProtocol.Tcp4, result);
            ParseNetstat("-anv -f inet6 -p tcp",  ConnectionProtocol.Tcp6, result);
            ParseNetstat("-anv -f inet -p udp",   ConnectionProtocol.Udp4, result);
            ParseNetstat("-anv -f inet6 -p udp",  ConnectionProtocol.Udp6, result);
        }
        catch { }

        _cachedConnections   = result;
        _connectionsCacheTime = now;
        return result;
    }

    private static void ParseNetstat(string netstatArgs, ConnectionProtocol protocol,
                                     List<NetworkConnection> result)
    {
        var output = RunNetstat(netstatArgs);
        if (string.IsNullOrEmpty(output)) return;

        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            // Only real data rows start with a proto token (tcp4/tcp6/tcp46/udp4/udp6/udp46).
            // This is what actually distinguishes data from the two header lines macOS netstat
            // prints ("Active Internet connections..." and "Proto Recv-Q Send-Q Local Address
            // ... process:pid state options ..."). The second header line has >=8 fields too,
            // so a field-count-only check lets it through and gets parsed as a garbage
            // connection (Local='Local', Remote='Address', PID=0) — checking the proto prefix
            // instead makes the skip robust regardless of column count.
            bool isUdp;
            if (parts[0].StartsWith("tcp", StringComparison.OrdinalIgnoreCase)) isUdp = false;
            else if (parts[0].StartsWith("udp", StringComparison.OrdinalIgnoreCase)) isUdp = true;
            else continue;

            // Dual-stack sockets ("tcp46"/"udp46") appear in both the -f inet and -f inet6
            // outputs; count them only in the IPv4 pass so the two invocations never
            // double-report a socket.
            if (parts[0].EndsWith("46", StringComparison.Ordinal)
                && protocol is ConnectionProtocol.Tcp6 or ConnectionProtocol.Udp6) continue;

            // macOS `netstat -anv` columns (both tcp and udp share the same 8 trailing
            // fields after process:pid — state options gencnt flags flags1 usecnt rtncnt fltrs):
            //   tcp: Proto Recv-Q Send-Q Local Foreign State  rxbytes txbytes rhiwat shiwat process:pid <8 trailing>
            //   udp: Proto Recv-Q Send-Q Local Foreign        rxbytes txbytes rhiwat shiwat process:pid <8 trailing>
            // So process:pid is always the 9th-from-last field, regardless of proto — this also
            // holds when the process name itself contains embedded spaces (observed in the wild,
            // e.g. "Obsidian Helper:1234" or "Obsidian Helper :1234"), which shifts every
            // left-anchored index but not a right-anchored one.
            var pidIdx      = parts.Length - 9;
            var minFixedIdx = isUdp ? 4 : 5; // last fixed left-hand column consumed below
            if (pidIdx <= minFixedIdx) continue; // too short to be a well-formed data row

            string localAddr  = parts[3];
            string remoteAddr = parts[4];
            TcpConnectionState state = isUdp ? TcpConnectionState.Unknown : ParseTcpState(parts[5]);

            var pidField = parts[pidIdx];
            var colon    = pidField.LastIndexOf(':');
            int pid = 0;
            if (colon >= 0) int.TryParse(pidField[(colon + 1)..], out pid);
            else            int.TryParse(pidField, out pid);

            SplitAddressPort(localAddr,  out var lAddr, out var lPort);
            SplitAddressPort(remoteAddr, out var rAddr, out var rPort);

            result.Add(new NetworkConnection
            {
                Protocol      = protocol,
                LocalAddress  = lAddr,
                LocalPort     = lPort,
                RemoteAddress = rAddr,
                RemotePort    = rPort,
                State         = state,
                ProcessId     = pid,
                ProcessName   = string.Empty,
            });
        }
    }

    private static string RunNetstat(string netstatArgs)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("netstat", netstatArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            // Start draining both streams concurrently *before* WaitForExit — BSD netstat
            // writes to stderr for unsupported family/proto combos (e.g. "tcp6: unknown or
            // uninstrumented protocol") and if stderr isn't read, that stream's OS pipe buffer
            // fills up and the child blocks writing to it, deadlocking against our WaitForExit.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask  = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } }
            _ = errorTask.Result; // discard — must never reach the console
            return outputTask.Result;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SplitAddressPort(string addrPort, out string address, out int port)
    {
        address = addrPort;
        port    = 0;
        if (string.IsNullOrEmpty(addrPort)) return;

        // IPv6 addresses are enclosed in brackets: [::1].port
        if (addrPort.StartsWith('['))
        {
            var closeBracket = addrPort.IndexOf(']');
            if (closeBracket >= 0)
            {
                address = addrPort[1..closeBracket];
                if (closeBracket + 1 < addrPort.Length && addrPort[closeBracket + 1] == '.')
                    int.TryParse(addrPort[(closeBracket + 2)..], out port);
                return;
            }
        }

        // IPv4 or hostname: last '.' separates port on macOS netstat
        var lastDot = addrPort.LastIndexOf('.');
        if (lastDot >= 0)
        {
            address = addrPort[..lastDot];
            int.TryParse(addrPort[(lastDot + 1)..], out port);
        }
    }

    private static TcpConnectionState ParseTcpState(string s) => s.ToUpperInvariant() switch
    {
        "ESTABLISHED" => TcpConnectionState.Established,
        "LISTEN"      => TcpConnectionState.Listen,
        "SYN_SENT"    => TcpConnectionState.SynSent,
        "SYN_RCVD"    => TcpConnectionState.SynReceived,
        "FIN_WAIT_1"  => TcpConnectionState.FinWait1,
        "FIN_WAIT_2"  => TcpConnectionState.FinWait2,
        "CLOSE_WAIT"  => TcpConnectionState.CloseWait,
        "CLOSING"     => TcpConnectionState.Closing,
        "LAST_ACK"    => TcpConnectionState.LastAck,
        "TIME_WAIT"   => TcpConnectionState.TimeWait,
        "CLOSED"      => TcpConnectionState.Closed,
        _             => TcpConnectionState.Unknown,
    };

    public void Dispose()
    {
        lock (_sharedLock) { _connection?.Dispose(); }
    }
}
