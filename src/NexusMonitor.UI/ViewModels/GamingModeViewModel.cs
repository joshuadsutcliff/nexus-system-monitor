using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Formatting;
using NexusMonitor.Core.Gaming;
using NexusMonitor.Core.Services;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

public partial class GamingModeViewModel : ViewModelBase, IDisposable
{
    private readonly GamingModeService  _gamingMode;
    private readonly IPowerPlanProvider _powerPlanProvider;
    private readonly SettingsService    _settings;
    private IDisposable? _sub;
    private const int MaxLogEntries = 100;

    /// <summary>Exposes platform capability flags for binding in the View.</summary>
    public IPlatformCapabilities Platform { get; }

    // ── Toggle / status ───────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isActive;
    [ObservableProperty] private string _statusLabel = "Inactive";
    [ObservableProperty] private string _statusColor = "#636366";

    // ── Stats ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private int    _throttledCount  = 0;
    [ObservableProperty] private string _currentPlanName = MetricFormatting.Dash;
    // Null when a real value is showing (no tooltip); explains WHY when CurrentPlanName has
    // fallen back to "—" instead. See UnavailableMetricCopy.
    [ObservableProperty] private string? _currentPlanNameUnavailableTooltip;

    // ── Configuration ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _gameProcessName = "";
    [ObservableProperty] private string _exclusionsText  = "";

    // ── Power plan selector ───────────────────────────────────────────────────

    public ObservableCollection<PowerPlanInfo> AvailablePlans { get; } = [];
    [ObservableProperty] private PowerPlanInfo? _selectedPlan;

    // ── Activity log ─────────────────────────────────────────────────────────

    public ObservableCollection<string> StatusLog { get; } = [];
    [ObservableProperty] private bool _hasStatusLog;

    // ── Constructor ───────────────────────────────────────────────────────────

    public GamingModeViewModel(
        GamingModeService  gamingMode,
        IPowerPlanProvider powerPlanProvider,
        SettingsService    settings,
        IPlatformCapabilities? platformCapabilities = null)
    {
        Title               = "Gaming Mode";
        _gamingMode         = gamingMode;
        _powerPlanProvider  = powerPlanProvider;
        _settings           = settings;
        Platform            = platformCapabilities ?? new MockPlatformCapabilities();

        // Load persisted values via backing fields — avoids partial callbacks during init
        _isActive         = settings.Current.GamingModeEnabled;
        _gameProcessName  = settings.Current.GamingModeGameProcess;
        _exclusionsText   = string.Join(Environment.NewLine,
                                settings.Current.GamingModeExclusions);

        // Load available power plans
        LoadPowerPlans();

        // Subscribe to Gaming Mode status messages
        _sub = gamingMode.StatusMessages
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnStatusMessage);

        UpdateStatus();

        // Re-activate if setting was persisted as on
        if (IsActive)
            _gamingMode.Start(_gameProcessName);
    }

    // ── Partial callbacks ─────────────────────────────────────────────────────

    partial void OnIsActiveChanged(bool value)
    {
        _settings.Current.GamingModeEnabled = value;
        _settings.Save();

        if (value)
            _gamingMode.Start(GameProcessName);
        else
            _gamingMode.Stop();

        UpdateStatus();
        RefreshStats();
    }

    partial void OnGameProcessNameChanged(string value)
    {
        _settings.Current.GamingModeGameProcess = value;
        _settings.Save();
    }

    partial void OnExclusionsTextChanged(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0)
                         .ToList();
        _settings.Current.GamingModeExclusions = lines;
        _settings.Save();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Toggle()
    {
        IsActive = !IsActive;
    }

    /// <summary>Immediately applies the selected power plan (independent of Gaming Mode toggle).</summary>
    [RelayCommand]
    private void SetPlan()
    {
        if (SelectedPlan is null) return;
        try
        {
            _powerPlanProvider.SetActivePlan(SelectedPlan.SchemeGuid);
            CurrentPlanName = SelectedPlan.Name;
            OnStatusMessage($"Power plan manually set to: {SelectedPlan.Name}");
            // Refresh the list to update IsActive flags
            LoadPowerPlans();
        }
        catch (Exception ex)
        {
            OnStatusMessage($"Failed to set power plan: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        StatusLog.Clear();
        HasStatusLog = StatusLog.Count > 0;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void OnStatusMessage(string message)
    {
        StatusLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (StatusLog.Count > MaxLogEntries)
            StatusLog.RemoveAt(StatusLog.Count - 1);
        HasStatusLog = StatusLog.Count > 0;

        // Keep ThrottledCount in sync
        RefreshStats();
    }

    private void UpdateStatus()
    {
        if (IsActive)
        {
            StatusLabel = "Active — system optimized for gaming";
            StatusColor = "#30D158";
        }
        else
        {
            StatusLabel = "Inactive — click the toggle to activate";
            StatusColor = "#636366";
        }
    }

    private void RefreshStats()
    {
        ThrottledCount = _gamingMode.ThrottledCount;

        try
        {
            var activePlan = _powerPlanProvider.GetActivePlan();
            var plans      = _powerPlanProvider.GetPowerPlans();
            CurrentPlanName = plans.FirstOrDefault(p => p.SchemeGuid == activePlan)?.Name ?? MetricFormatting.Dash;
        }
        catch
        {
            CurrentPlanName = MetricFormatting.Dash;
        }
    }

    private void LoadPowerPlans()
    {
        try
        {
            AvailablePlans.Clear();
            Guid active = _powerPlanProvider.GetActivePlan();
            foreach (var plan in _powerPlanProvider.GetPowerPlans())
                AvailablePlans.Add(plan);

            CurrentPlanName = AvailablePlans.FirstOrDefault(p => p.SchemeGuid == active)?.Name ?? MetricFormatting.Dash;
            SelectedPlan    = AvailablePlans.FirstOrDefault(p => p.SchemeGuid == active)
                              ?? AvailablePlans.FirstOrDefault();
        }
        catch
        {
            CurrentPlanName = MetricFormatting.Dash;
        }
    }

    partial void OnCurrentPlanNameChanged(string value) =>
        CurrentPlanNameUnavailableTooltip = value == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _sub?.Dispose();
}
