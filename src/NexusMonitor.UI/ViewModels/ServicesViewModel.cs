using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.UI.Messages;
using NexusMonitor.UI.Helpers;

namespace NexusMonitor.UI.ViewModels;

public partial class ServicesViewModel : ViewModelBase, IDisposable
{
    private readonly IServicesProvider _servicesProvider;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Exposes platform capability flags for binding in the View.</summary>
    public IPlatformCapabilities Platform { get; }

    // Master list: all services from last successful load, unfiltered.
    private IReadOnlyList<ServiceInfo> _allServices = [];

    [ObservableProperty] private ObservableCollection<ServiceInfo> _services = [];
    [ObservableProperty] private ServiceInfo? _selectedService;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private string _lastError = string.Empty;
    [ObservableProperty] private bool _isDetailPanelVisible = false;

    /// <summary>Sort column path persisted here so it survives tab switches (the View is recreated each time).</summary>
    public string? SortMemberPath { get; set; }
    /// <summary>Sort direction persisted here so it survives tab switches.</summary>
    public System.ComponentModel.ListSortDirection SortDirection { get; set; } = System.ComponentModel.ListSortDirection.Ascending;

    /// <summary>True when detail sidebar should be shown (has selection AND toggle is on).</summary>
    public bool IsServiceDetailShown => SelectedService is not null && IsDetailPanelVisible;

    public ServicesViewModel(IServicesProvider servicesProvider,
        IPlatformCapabilities? platformCapabilities = null)
    {
        _servicesProvider = servicesProvider;
        Platform          = platformCapabilities ?? new MockPlatformCapabilities();
        Title = "Services";
        _ = LoadServicesAsync();
    }

    private async Task LoadServicesAsync()
    {
        if (_cts.IsCancellationRequested) return;
        IsLoading = true;
        try
        {
            var list = await _servicesProvider.GetServicesAsync(_cts.Token);
            _allServices = list;
            ApplyFilter(preserveSelection: true);
        }
        catch (OperationCanceledException) { /* disposed — do nothing */ }
        catch (Exception ex) { LastError = $"Load failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter(preserveSelection: true);
    partial void OnSelectedServiceChanged(ServiceInfo? value) => OnPropertyChanged(nameof(IsServiceDetailShown));
    partial void OnIsDetailPanelVisibleChanged(bool value) => OnPropertyChanged(nameof(IsServiceDetailShown));

    private void ApplyFilter(bool preserveSelection = false)
    {
        var selectedName = preserveSelection ? SelectedService?.Name : null;

        var src = string.IsNullOrWhiteSpace(SearchText)
            ? _allServices
            : _allServices.Where(s =>
                s.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)        ||
                s.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                s.BinaryPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        // Capture counts and sort before the Post to avoid TOCTOU: if a concurrent
        // LoadServicesAsync replaces _allServices between here and the Post executing,
        // the counts would reflect the new list while Services shows the old one.
        var wanted       = src.OrderBy(s => s.DisplayName).ToList();
        int totalCount   = _allServices.Count;
        int runningCount = _allServices.Count(s => s.State == ServiceState.Running);

        Dispatcher.UIThread.Post(() =>
        {
            // Clear + refill keeps it simple. Services are only reloaded on
            // explicit user actions (Start/Stop/Restart/search), so the
            // occasional loss of sort state is acceptable. Selection is always
            // restored explicitly below.
            Services.Clear();
            foreach (var svc in wanted)
                Services.Add(svc);

            TotalCount   = totalCount;
            RunningCount = runningCount;

            // Restore previous selection by service name
            if (selectedName is not null)
                SelectedService = Services.FirstOrDefault(s => s.Name == selectedName);
        });
    }

    [RelayCommand]
    private async Task StartService()
    {
        if (SelectedService is null) return;
        try
        {
            LastError = string.Empty;
            await _servicesProvider.StartServiceAsync(SelectedService.Name, _cts.Token);
            await LoadServicesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Start failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task StopService()
    {
        if (SelectedService is null) return;
        try
        {
            LastError = string.Empty;
            await _servicesProvider.StopServiceAsync(SelectedService.Name, _cts.Token);
            await LoadServicesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Stop failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RestartService()
    {
        if (SelectedService is null) return;
        try
        {
            LastError = string.Empty;
            await _servicesProvider.RestartServiceAsync(SelectedService.Name, _cts.Token);
            await LoadServicesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Restart failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SetStartupType(string typeName)
    {
        if (SelectedService is null) return;
        if (!Enum.TryParse<ServiceStartType>(typeName, out var startType)) return;
        try
        {
            LastError = string.Empty;
            await _servicesProvider.SetStartTypeAsync(SelectedService.Name, startType, _cts.Token);
            await LoadServicesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Set startup type failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void GoToProcess()
    {
        if (SelectedService?.ProcessId is int pid and > 0)
            WeakReferenceMessenger.Default.Send(new NavigateToProcessMessage(pid));
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        var raw = SelectedService?.BinaryPath ?? string.Empty;
        // BinaryPath may be quoted: "C:\path\to\exe.exe" args...
        var exe = raw.StartsWith('"') ? raw[1..].Split('"')[0] : raw.Split(' ')[0];
        ShellHelper.OpenFileLocation(exe);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
