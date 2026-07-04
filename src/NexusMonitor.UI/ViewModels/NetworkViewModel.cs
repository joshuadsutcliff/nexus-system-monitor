using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using ReactiveUI;
using System.Reactive.Linq;
using NexusMonitor.UI.Messages;
using NexusMonitor.UI.Helpers;

namespace NexusMonitor.UI.ViewModels;

public partial class NetworkViewModel : ViewModelBase, IActivatable, IDisposable
{
    private readonly INetworkConnectionsProvider _provider;
    private IDisposable? _subscription;
    private IDisposable? _adapterSubscription;
    private IReadOnlyList<NetworkConnection> _allConnections = [];

    [ObservableProperty] private ObservableCollection<NetworkConnection> _connections = [];
    [ObservableProperty] private int    _totalCount;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private NetworkConnection? _selectedConnection;
    [ObservableProperty] private bool   _isDetailPanelVisible = false;
    [ObservableProperty] private string _adapterSendDisplay = "—";
    [ObservableProperty] private string _adapterRecvDisplay = "—";
    [ObservableProperty] private bool   _showThroughputColumns = true;

    /// <summary>Sort column path persisted here so it survives tab switches (the View is recreated each time).</summary>
    public string? SortMemberPath { get; set; }
    /// <summary>Sort direction persisted here so it survives tab switches.</summary>
    public System.ComponentModel.ListSortDirection SortDirection { get; set; } = System.ComponentModel.ListSortDirection.Ascending;

    private bool _throughputChecked;

    // Reusable collections for SyncCollection — cleared and repopulated each tick instead of allocated anew
    private readonly HashSet<ConnKey>                        _wantedSet  = new();
    private readonly Dictionary<ConnKey, NetworkConnection>  _wantedMap  = new();
    private readonly Dictionary<ConnKey, int>                _currentMap = new();

    /// <summary>True when detail sidebar should be shown (has selection AND toggle is on).</summary>
    public bool IsConnectionDetailShown => SelectedConnection is not null && IsDetailPanelVisible;

    public NetworkViewModel(INetworkConnectionsProvider provider)
    {
        Title     = "Network";
        _provider = provider;

        StartMonitoring();
    }

    private void StartMonitoring()
    {
        _subscription = _provider
            .GetConnectionStream(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(Update);

        _adapterSubscription = _provider
            .GetAdapterThroughputStream(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                AdapterSendDisplay = t.SendDisplay;
                AdapterRecvDisplay = t.RecvDisplay;
            });
    }

    /// <summary>
    /// Resumes the connection/adapter-throughput subscriptions (UI-only — the underlying
    /// provider streams are shared multicasts driven by other subscribers regardless).
    /// </summary>
    /// <inheritdoc/>
    public void Activate()
    {
        if (_subscription is not null) return; // idempotent guard
        StartMonitoring();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        _subscription?.Dispose();
        _subscription = null;
        _adapterSubscription?.Dispose();
        _adapterSubscription = null;
    }

    // Re-filter when search text changes
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedConnectionChanged(NetworkConnection? value) => OnPropertyChanged(nameof(IsConnectionDetailShown));
    partial void OnIsDetailPanelVisibleChanged(bool value) => OnPropertyChanged(nameof(IsConnectionDetailShown));

    private void Update(IReadOnlyList<NetworkConnection> all)
    {
        _allConnections = all;
        TotalCount      = all.Count;

        if (!_throughputChecked)
        {
            _throughputChecked    = true;
            ShowThroughputColumns = _provider.SupportsPerConnectionThroughput;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = Filter(_allConnections);
        SyncCollection(filtered);
    }

    /// <summary>
    /// Syncs the <see cref="Connections"/> collection in-place so the DataGrid
    /// never loses its sort order or column-resize state.
    /// Handles duplicate keys gracefully (e.g. multiple UDP sockets on the
    /// same local address+port from the same process).
    /// Already on the UI thread via ObserveOn(RxApp.MainThreadScheduler) — no Post needed.
    /// </summary>
    private void SyncCollection(List<NetworkConnection> wanted)
    {
        // Build the wanted set/map using [key] = value so duplicate keys don't
        // throw — the last entry for a given key wins (matching OS behaviour
        // where the most-recently-reported binding is most relevant).
        // Reuse field collections (clear + repopulate) to avoid per-tick allocation.
        _wantedSet.Clear();
        _wantedMap.Clear();
        foreach (var conn in wanted)
        {
            var key = MakeKey(conn);
            _wantedSet.Add(key);
            _wantedMap[key] = conn;   // overwrite on duplicate — no exception
        }

        // ── 1. Remove connections no longer present ───────────────────────────
        for (int i = Connections.Count - 1; i >= 0; i--)
            if (!_wantedSet.Contains(MakeKey(Connections[i])))
                Connections.RemoveAt(i);

        // ── 2. Rebuild current index map after removals ───────────────────────
        _currentMap.Clear();
        for (int i = 0; i < Connections.Count; i++)
            _currentMap[MakeKey(Connections[i])] = i;

        // ── 3. Add new connections; update state of existing ones ─────────────
        foreach (var (key, conn) in _wantedMap)
        {
            if (!_currentMap.TryGetValue(key, out int idx))
            {
                // New — append; the DataGrid will sort it into place
                Connections.Add(conn);
            }
            else if (Connections[idx].State           != conn.State           ||
                     Connections[idx].SendBytesPerSec != conn.SendBytesPerSec ||
                     Connections[idx].RecvBytesPerSec != conn.RecvBytesPerSec)
            {
                // State or throughput changed — replace in-place so DataGrid refreshes
                Connections[idx] = conn;
            }
        }
    }

    private static ConnKey MakeKey(NetworkConnection c) =>
        new(c.Protocol, c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort, c.ProcessId);

    private List<NetworkConnection> Filter(IReadOnlyList<NetworkConnection> src)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return src is List<NetworkConnection> list ? list : [.. src];

        var ft = SearchText.Trim();
        return src.Where(c =>
            c.ProcessName.Contains(ft, StringComparison.OrdinalIgnoreCase)  ||
            c.LocalAddress.Contains(ft,  StringComparison.OrdinalIgnoreCase)||
            c.RemoteAddress.Contains(ft, StringComparison.OrdinalIgnoreCase)||
            c.LocalPort.ToString().Contains(ft)  ||
            c.RemotePort.ToString().Contains(ft) ||
            c.State.ToString().Contains(ft, StringComparison.OrdinalIgnoreCase)    ||
            c.Protocol.ToString().Contains(ft, StringComparison.OrdinalIgnoreCase))
        .ToList();
    }

    [RelayCommand]
    private Task CopyLocalAddress() =>
        ClipboardHelper.CopyAsync($"{SelectedConnection?.LocalAddress}:{SelectedConnection?.LocalPort}");

    [RelayCommand]
    private Task CopyRemoteAddress() =>
        ClipboardHelper.CopyAsync($"{SelectedConnection?.RemoteAddress}:{SelectedConnection?.RemotePort}");

    [RelayCommand]
    private void GoToProcess()
    {
        if (SelectedConnection?.ProcessId is int pid and > 0)
            WeakReferenceMessenger.Default.Send(new NavigateToProcessMessage(pid));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _adapterSubscription?.Dispose();
    }
}

/// <summary>
/// Uniquely identifies a network connection, excluding the mutable State field.
/// Named <c>ConnKey</c> (not <c>ConnectionKey</c>) to avoid a method/type name
/// collision that, while legal C#, is confusing to read.
/// </summary>
internal readonly record struct ConnKey(
    ConnectionProtocol Protocol,
    string LocalAddress,
    int    LocalPort,
    string RemoteAddress,
    int    RemotePort,
    int    ProcessId);
