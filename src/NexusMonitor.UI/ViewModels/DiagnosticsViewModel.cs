using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

public partial class DiagnosticsViewModel : ViewModelBase, IDisposable
{
    private readonly MemoryLeakDetectionService _leakService;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private IDisposable? _subscription;

    [ObservableProperty] private bool _isDetectionEnabled;
    [ObservableProperty] private int _observationWindowMinutes;
    [ObservableProperty] private double _leakRateThresholdMbPerHour;
    [ObservableProperty] private double _handleLeakThresholdPerHour;
    [ObservableProperty] private int _suspectCount;
    [ObservableProperty] private string _statusText = "Monitoring…";
    [ObservableProperty] private bool _hasSuspects;
    [ObservableProperty] private string _emptyStateHeadline = "No memory leaks detected";
    [ObservableProperty] private string _emptyStateSubtitle = "Processes are actively monitored for sustained memory growth.";

    public ObservableCollection<LeakSuspectCardViewModel> Suspects { get; } = new();

    public DiagnosticsViewModel(
        MemoryLeakDetectionService leakService,
        AppSettings settings,
        SettingsService settingsService)
    {
        _leakService     = leakService;
        _settings        = settings;
        _settingsService = settingsService;

        _isDetectionEnabled         = settings.MemoryLeakDetectionEnabled;
        _observationWindowMinutes   = settings.LeakObservationWindowMinutes;
        _leakRateThresholdMbPerHour = settings.LeakRateThresholdMbPerHour;
        _handleLeakThresholdPerHour = settings.HandleLeakThresholdPerHour;

        _subscription = leakService.Suspects
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateSuspects);
    }

    private void UpdateSuspects(IReadOnlyList<MemoryLeakSuspect> suspects)
    {
        Suspects.Clear();
        foreach (var s in suspects)
            Suspects.Add(new LeakSuspectCardViewModel(s));

        SuspectCount = suspects.Count;
        HasSuspects  = suspects.Count > 0;
        StatusText   = IsDetectionEnabled
            ? (suspects.Count > 0 ? $"{suspects.Count} suspect(s) found" : "Monitoring…")
            : "Detection paused";

        RefreshEmptyState();
    }

    /// <summary>
    /// Keeps the empty-state card's copy in lockstep with <see cref="IsDetectionEnabled"/> —
    /// the same flag that drives the toggle and <see cref="StatusText"/> — so the page never
    /// shows "paused" and "monitored" at the same time.
    /// </summary>
    private void RefreshEmptyState()
    {
        EmptyStateHeadline = IsDetectionEnabled
            ? "No memory leaks detected"
            : "Detection paused";
        EmptyStateSubtitle = IsDetectionEnabled
            ? "Processes are actively monitored for sustained memory growth."
            : "Enable detection above to monitor processes for sustained memory growth.";
    }

    partial void OnIsDetectionEnabledChanged(bool value)
    {
        _settings.MemoryLeakDetectionEnabled = value;
        _settingsService.Save();

        if (value)
        {
            _leakService.Start();
            StatusText = "Monitoring…";
        }
        else
        {
            _leakService.Stop();
            StatusText = "Detection paused";
        }

        RefreshEmptyState();
    }

    [RelayCommand]
    private void DismissSuspect(string processName)
    {
        _leakService.Dismiss(processName);
    }

    partial void OnObservationWindowMinutesChanged(int value)
    {
        _settings.LeakObservationWindowMinutes = value;
        _settingsService.Save();
        if (IsDetectionEnabled)
        {
            _leakService.Stop();
            _leakService.Start();
        }
    }

    partial void OnLeakRateThresholdMbPerHourChanged(double value)
    {
        _settings.LeakRateThresholdMbPerHour = value;
        _settingsService.Save();
    }

    partial void OnHandleLeakThresholdPerHourChanged(double value)
    {
        _settings.HandleLeakThresholdPerHour = value;
        _settingsService.Save();
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}

public sealed partial class LeakSuspectCardViewModel : ObservableObject
{
    public int Pid { get; init; }
    public string ProcessName { get; init; }
    public string LeakRateLabel { get; }
    public string HandleRateLabel { get; }
    public string ConfidenceLabel { get; }
    public double ConfidenceValue { get; }
    public string ObservationTimeLabel { get; }
    public bool IsWarning { get; }
    public bool IsCritical { get; }
    public IList<Avalonia.Point> WorkingSetSparkPoints { get; }
    public IList<Avalonia.Point> HandleSparkPoints { get; }

    public LeakSuspectCardViewModel(MemoryLeakSuspect suspect)
    {
        Pid         = suspect.Pid;
        ProcessName = suspect.ProcessName;

        double rateMb = suspect.LeakRateBytesPerHour / 1024.0 / 1024.0;
        LeakRateLabel = rateMb >= 1
            ? $"+{rateMb:F0} MB/hr"
            : $"+{rateMb * 1024:F0} KB/hr";

        HandleRateLabel   = suspect.HandleLeakRatePerHour > 0
            ? $"+{suspect.HandleLeakRatePerHour:F0}/hr" : "—";
        ConfidenceLabel   = $"{suspect.Confidence:P0}";
        ConfidenceValue   = suspect.Confidence;

        var elapsed = DateTimeOffset.UtcNow - suspect.FirstDetected;
        ObservationTimeLabel = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
            : $"{elapsed.Seconds}s";

        IsCritical = suspect.Confidence > 0.8;
        IsWarning  = !IsCritical;

        WorkingSetSparkPoints = BuildSparkPoints(suspect.WorkingSetHistory, 120, 44);
        HandleSparkPoints     = BuildSparkPoints(suspect.HandleHistory, 120, 44);
    }

    private static IList<Avalonia.Point> BuildSparkPoints(IReadOnlyList<double> history, double width, double height)
    {
        if (history.Count == 0) return [];

        double min = history.Min();
        double max = history.Max();
        double range = max - min;
        if (range < 1) range = 1;

        var points = new List<Avalonia.Point>(history.Count);
        for (int i = 0; i < history.Count; i++)
        {
            double x = history.Count > 1 ? i / (double)(history.Count - 1) * width : 0;
            double y = height - (history[i] - min) / range * height;
            points.Add(new Avalonia.Point(x, y));
        }
        return points;
    }
}
