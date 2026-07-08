using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Services;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

public partial class AutoBalanceViewModel : ViewModelBase, IDisposable
{
    private readonly AutoBalanceService _autoBalance;
    private readonly SettingsService   _settings;
    private IDisposable? _sub;
    private const int MaxLogEntries = 200;

    // ── Toggle / status ───────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isEnabled;
    [ObservableProperty] private string _statusLabel = "Inactive";
    [ObservableProperty] private string _statusColor = "#636366";

    // ── Configuration ─────────────────────────────────────────────────────────
    [ObservableProperty] private double _cpuThreshold  = 80.0;
    [ObservableProperty] private string _exclusionsText = "";

    // ── Session stats ─────────────────────────────────────────────────────────
    [ObservableProperty] private int _throttledNow    = 0;
    [ObservableProperty] private int _totalThrottled  = 0;
    [ObservableProperty] private int _totalRestored   = 0;

    // ── Activity log ─────────────────────────────────────────────────────────
    public ObservableCollection<AutoBalanceEvent> EventLog { get; } = [];

    // ── How Auto-Balance works ──────────────────────────────────────────────────
    public static string HowItWorks =>
        "Auto-Balance monitors total system CPU usage every 500 ms. When CPU exceeds the " +
        "threshold, background processes (not the active foreground window) using more " +
        "than 5% CPU are temporarily lowered to \"Below Normal\" priority. When load " +
        "drops below 70% of the threshold, all processes are restored to their original " +
        "priorities automatically.";

    // ── Constructor ───────────────────────────────────────────────────────────

    public AutoBalanceViewModel(AutoBalanceService autoBalance, SettingsService settings)
    {
        Title       = "Auto-Balance";
        _autoBalance = autoBalance;
        _settings   = settings;

        // Load persisted values via backing fields (no partial callback fire during init)
        _isEnabled      = settings.Current.AutoBalanceEnabled;
        _cpuThreshold   = settings.Current.AutoBalanceCpuThreshold;
        _exclusionsText = string.Join(Environment.NewLine,
                              settings.Current.AutoBalanceExclusions);

        UpdateStatus();

        // Subscribe to events on the UI thread
        _sub = autoBalance.Events
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnEvent);

        // Start the service now if setting was persisted as enabled
        if (IsEnabled) _autoBalance.Start();
    }

    // ── Partial callbacks ─────────────────────────────────────────────────────

    partial void OnIsEnabledChanged(bool value)
    {
        _settings.Current.AutoBalanceEnabled = value;
        _settings.Save();
        if (value) _autoBalance.Start();
        else        _autoBalance.Stop();
        UpdateStatus();
    }

    partial void OnCpuThresholdChanged(double value)
    {
        _settings.Current.AutoBalanceCpuThreshold = value;
        _settings.Save();
    }

    partial void OnExclusionsTextChanged(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0)
                         .ToList();
        _settings.Current.AutoBalanceExclusions = lines;
        _settings.Save();
    }

    // ── Event handler ─────────────────────────────────────────────────────────

    private void OnEvent(AutoBalanceEvent e)
    {
        EventLog.Insert(0, e);
        while (EventLog.Count > MaxLogEntries)
            EventLog.RemoveAt(EventLog.Count - 1);

        switch (e.Type)
        {
            case AutoBalanceEventType.Throttled:
                ThrottledNow++;
                TotalThrottled++;
                break;
            case AutoBalanceEventType.Restored:
                if (ThrottledNow > 0) ThrottledNow--;
                TotalRestored++;
                break;
            case AutoBalanceEventType.Stopped:
                ThrottledNow = 0;
                break;
        }
    }

    private void UpdateStatus()
    {
        if (IsEnabled)
        {
            StatusLabel = "Active — monitoring background CPU usage";
            StatusColor = "#30D158";
        }
        else
        {
            StatusLabel = "Inactive — enable to start automatic CPU balancing";
            StatusColor = "#636366";
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearLog()
    {
        EventLog.Clear();
        ThrottledNow   = 0;
        TotalThrottled = 0;
        TotalRestored  = 0;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _sub?.Dispose();
}
