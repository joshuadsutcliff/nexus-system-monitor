using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.UI.Helpers;

namespace NexusMonitor.UI.ViewModels;

public partial class StartupViewModel : ViewModelBase, IDisposable
{
    private readonly IStartupProvider _provider;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private IReadOnlyList<StartupItem> _allItems = [];

    /// <summary>Exposes platform capability flags for binding in the View.</summary>
    public IPlatformCapabilities Platform { get; }

    [ObservableProperty] private ObservableCollection<StartupItem> _items = [];
    [ObservableProperty] private StartupItem? _selectedItem;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _lastError = string.Empty;

    /// <summary>Sort column path persisted here so it survives tab switches (the View is recreated each time).</summary>
    public string? SortMemberPath { get; set; }
    /// <summary>Sort direction persisted here so it survives tab switches.</summary>
    public System.ComponentModel.ListSortDirection SortDirection { get; set; } = System.ComponentModel.ListSortDirection.Ascending;

    public StartupViewModel(IStartupProvider provider,
        IPlatformCapabilities? platformCapabilities = null)
    {
        Title     = "Startup";
        _provider = provider;
        Platform  = platformCapabilities ?? new MockPlatformCapabilities();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_cts.IsCancellationRequested) return;
        IsLoading = true;
        LastError = string.Empty;
        try
        {
            _allItems = await _provider.GetStartupItemsAsync(_cts.Token);
            ApplyFilter();
        }
        catch (OperationCanceledException) { /* disposed — do nothing */ }
        catch (Exception ex) { LastError = $"Load failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var src = string.IsNullOrWhiteSpace(SearchText)
            ? _allItems
            : _allItems.Where(i =>
                i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)     ||
                i.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase)||
                i.Command.Contains(SearchText, StringComparison.OrdinalIgnoreCase)  ||
                i.Location.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        // Capture counts before the Post to avoid TOCTOU if _allItems is replaced concurrently.
        var snapshot     = src.ToList();
        int totalCount   = _allItems.Count;
        int enabledCount = _allItems.Count(i => i.IsEnabled);

        Dispatcher.UIThread.Post(() =>
        {
            Items        = new ObservableCollection<StartupItem>(snapshot);
            TotalCount   = totalCount;
            EnabledCount = enabledCount;
        });
    }

    [RelayCommand]
    private async Task ToggleEnabled()
    {
        if (SelectedItem is null) return;
        try
        {
            LastError = string.Empty;
            bool newState = !SelectedItem.IsEnabled;
            await _provider.SetEnabledAsync(SelectedItem, newState, _cts.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Toggle failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        try
        {
            LastError = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex) { LastError = $"Refresh failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task EnableItem()
    {
        if (SelectedItem is null || SelectedItem.IsEnabled) return;
        try
        {
            LastError = string.Empty;
            await _provider.SetEnabledAsync(SelectedItem, true, _cts.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Enable failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DisableItem()
    {
        if (SelectedItem is null || !SelectedItem.IsEnabled) return;
        try
        {
            LastError = string.Empty;
            await _provider.SetEnabledAsync(SelectedItem, false, _cts.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = $"Disable failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        var cmd = SelectedItem?.Command ?? string.Empty;
        var exe = cmd.TrimStart('"').Split('"')[0].Split(' ')[0];
        ShellHelper.OpenFileLocation(exe);
    }

    [RelayCommand]
    private void OpenRegistryKey()
    {
        ShellHelper.Launch("regedit.exe");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
