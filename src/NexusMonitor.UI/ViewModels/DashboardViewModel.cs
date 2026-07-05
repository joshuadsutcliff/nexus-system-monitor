using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.Messaging;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Pages;
using NexusMonitor.UI.Messages;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly SystemHealthService        _healthService;
    private readonly MemoryLeakDetectionService _leakService;
    private readonly AppSettings                _settings;
    private IDisposable?                        _subscription;
    private IDisposable?                        _predictionsSubscription;
    private IDisposable?                        _staleSubscription;
    private readonly HashSet<string>            _dismissedResources = new();
    private SystemHealthSnapshot?               _latestSnapshot;
    private int                                 _consumerTickCounter;

    // ── Overall health ────────────────────────────────────────────────────────

    // True when the health pipeline has stalled (no snapshot for > 3× the interval).
    [ObservableProperty] private bool _isDataStale;

    [ObservableProperty] private double _overallScore = 100;
    [ObservableProperty] private string _overallLabel = "Excellent";
    [ObservableProperty] private string _overallDescription = "Your system is running smoothly.";
    [ObservableProperty] private IBrush _healthRingBrush  = Brushes.Green;
    [ObservableProperty] private string _trendArrow = "→";

    // ── Subsystem cards ───────────────────────────────────────────────────────

    [ObservableProperty] private SubsystemCardViewModel _cpuCard    = new("CPU",    "\ue9d5");
    [ObservableProperty] private SubsystemCardViewModel _memoryCard = new("Memory", "\ue9d6");
    [ObservableProperty] private SubsystemCardViewModel _diskCard   = new("Disk",   "\ue9d7");
    [ObservableProperty] private SubsystemCardViewModel _gpuCard    = new("GPU",    "\ue9d9");

    // ── Top consumers ─────────────────────────────────────────────────────────

    public ObservableCollection<ProcessImpactViewModel> TopConsumers { get; } = new();

    // ── Active automations ────────────────────────────────────────────────────

    [ObservableProperty] private int    _activeAutomations;
    [ObservableProperty] private string _automationStatus = "No automations active";

    // ── Bottleneck analysis ───────────────────────────────────────────────────

    [ObservableProperty] private BottleneckCardViewModel _bottleneckCard = new();

    // ── Recommendations ───────────────────────────────────────────────────────

    public ObservableCollection<RecommendationViewModel> Recommendations { get; } = new();

    // ── Resource Predictions ──────────────────────────────────────────────────

    public ObservableCollection<PredictionCardViewModel> PredictionCards { get; } = new();
    [ObservableProperty] private bool _hasPredictions;

    // ── Health Trends ─────────────────────────────────────────────────────────

    public HealthTrendsViewModel HealthTrendsViewModel { get; }

    // ── Page engine (Phase 2, flag-gated) ─────────────────────────────────────

    /// <summary>True when the Dashboard renders through the page engine (EnablePageEngine at startup).</summary>
    public bool UsePageEngine { get; }

    /// <summary>The page rendered by the engine path; null when the flag is off or the factory layout failed to load.</summary>
    [ObservableProperty] private PageLayout? _enginePage;

    // Phase 5 superseded PageLayoutStore as the read/write path here (see WorkspaceProfileStore
    // below) — PageLayoutStore itself stays registered in DI only as the legacy-pages migration
    // source (App.axaml.cs); DashboardViewModel no longer reads or writes it.
    private readonly WorkspaceProfileStore? _profileStore;

    public DashboardViewModel(SystemHealthService healthService, MemoryLeakDetectionService leakService, AppSettings settings, HealthTrendsViewModel healthTrendsViewModel, PredictionService? predictionService = null, WorkspaceProfileStore? profileStore = null)
    {
        _healthService      = healthService;
        _leakService        = leakService;
        _settings           = settings;
        HealthTrendsViewModel = healthTrendsViewModel;
        _profileStore       = profileStore;

        var usePageEngine = settings.EnablePageEngine;
        if (usePageEngine)
        {
            try { EnginePage = _profileStore?.LoadActive().Pages.GetValueOrDefault("dashboard") ?? TryFactoryLoad(); }
            catch (InvalidOperationException) { usePageEngine = false; } // packaging bug — never a user path
        }
        UsePageEngine = usePageEngine;

        _subscription = _healthService.HealthStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ApplySnapshot);

        _staleSubscription = _healthService.IsStaleStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(stale => IsDataStale = stale);

        if (predictionService != null)
        {
            _predictionsSubscription = predictionService.Predictions
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(UpdatePredictions);
        }

        // Restart health service when the user changes the metrics polling interval
        WeakReferenceMessenger.Default.Register<MetricsIntervalChangedMessage>(this, (_, msg) =>
        {
            _healthService.Start(msg.Interval);
        });

        // Page engine Phase 5: reload the dashboard layout when the user switches the active
        // workspace profile in Settings. Theme is applied separately (synchronously, before this
        // message is sent) by SettingsViewModel — this handler only ever touches layout.
        WeakReferenceMessenger.Default.Register<WorkspaceProfileSwitchedMessage>(this, (_, _) =>
        {
            EnginePage = _profileStore?.LoadActive().Pages.GetValueOrDefault("dashboard") ?? EnginePage;
        });
    }

    /// <summary>Loads the factory-default "dashboard" layout (used when no layout store is available).</summary>
    private static PageLayout TryFactoryLoad() => BuiltInPageLayouts.Load("dashboard");

    // ── Page engine editing (Phase 3) ──────────────────────────────────────
    private PageEditSession? _editSession;

    /// <summary>True while the Dashboard page is in edit mode.</summary>
    [ObservableProperty] private bool _isEditMode;
    /// <summary>True while the add-widget gallery overlay is open.</summary>
    [ObservableProperty] private bool _isGalleryOpen;
    /// <summary>True when the edit session has an undoable step.</summary>
    [ObservableProperty] private bool _canUndoEdit;

    /// <summary>Enters edit mode over the current page.</summary>
    [RelayCommand]
    private void EnterEditMode()
    {
        if (!UsePageEngine || EnginePage is null || IsEditMode) return;
        _editSession = new PageEditSession(EnginePage);
        IsEditMode = true;
        CanUndoEdit = false;
        WeakReferenceMessenger.Default.Send(new PageEditModeChangedMessage(true));
    }

    /// <summary>Commits edits, persists into the ACTIVE workspace profile (load-modify-save via
    /// the store, replacing just the "dashboard" page — the profile's other pages and theme are
    /// left untouched), leaves edit mode.</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (_editSession is null) return;
        var committed = _editSession.Commit();
        EnginePage = committed;

        if (_profileStore is not null)
        {
            var active = _profileStore.LoadActive();
            var pages = active.Pages.ToDictionary(kv => kv.Key, kv => kv.Value);
            pages["dashboard"] = committed;
            _profileStore.Save(active with { Pages = pages });
        }

        ExitEdit();
    }

    /// <summary>Abandons edits, restores the pre-edit layout, leaves edit mode.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        if (_editSession is null) return;
        EnginePage = _editSession.Cancel();
        ExitEdit();
    }

    /// <summary>Reverts the most recent edit.</summary>
    [RelayCommand]
    private void UndoEdit()
    {
        if (_editSession is null) return;
        _editSession.Undo();
        EnginePage = _editSession.Current;
        CanUndoEdit = _editSession.CanUndo;
    }

    /// <summary>Closes vertical gaps in the current layout (engine Compact).</summary>
    [RelayCommand]
    private void TidyLayout()
    {
        if (_editSession is null) return;
        _editSession.CompactPage();
        AfterEdit();
    }

    /// <summary>Opens the add-widget gallery overlay.</summary>
    [RelayCommand]
    private void OpenGallery() { if (IsEditMode) IsGalleryOpen = true; }

    /// <summary>Adds a widget of the given type at the top of the page (engine push-down applies).</summary>
    [RelayCommand]
    private void AddWidget(string typeId)
    {
        if (_editSession is null) return;
        _editSession.Add(new WidgetInstance(Guid.NewGuid(), typeId, new GridRect(0, 0, 4, 2)));
        AfterEdit();
        IsGalleryOpen = false;
    }

    /// <summary>Adorner callback: commit a drag/resize result.</summary>
    public void EditMove(Guid id, GridRect target)
    {
        if (_editSession is null) return;
        _editSession.Move(id, target);
        AfterEdit();
    }

    /// <summary>Adorner callback: remove a widget.</summary>
    public void EditRemove(Guid id)
    {
        if (_editSession is null) return;
        _editSession.Remove(id);
        AfterEdit();
    }

    private void AfterEdit()
    {
        EnginePage = _editSession!.Current;
        CanUndoEdit = _editSession.CanUndo;
    }

    private void ExitEdit()
    {
        _editSession = null;
        IsEditMode = false;
        IsGalleryOpen = false;
        CanUndoEdit = false;
        WeakReferenceMessenger.Default.Send(new PageEditModeChangedMessage(false));
    }

    private void OnPredictionCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PredictionCardViewModel.IsDismissed))
            HasPredictions = PredictionCards.Any(c => !c.IsDismissed);
    }

    private void UpdatePredictions(IReadOnlyList<ResourcePrediction> predictions)
    {
        foreach (var existing in PredictionCards)
            existing.PropertyChanged -= OnPredictionCardPropertyChanged;
        PredictionCards.Clear();
        foreach (var p in predictions)
        {
            var card = new PredictionCardViewModel(p, _dismissedResources);
            card.PropertyChanged += OnPredictionCardPropertyChanged;
            PredictionCards.Add(card);
        }
        HasPredictions = PredictionCards.Any(c => !c.IsDismissed);
    }

    private void ApplySnapshot(SystemHealthSnapshot snapshot)
    {
        OverallScore       = snapshot.OverallScore;
        OverallLabel       = snapshot.OverallHealth.ToString();
        OverallDescription = DescribeHealth(snapshot.OverallHealth, snapshot.OverallScore);
        HealthRingBrush    = HealthLevelToBrush(snapshot.OverallHealth);
        TrendArrow         = snapshot.OverallTrend switch
        {
            TrendDirection.Improving => "↑",
            TrendDirection.Degrading => "↓",
            _                        => "→",
        };

        UpdateCard(CpuCard,    snapshot.Cpu);
        UpdateCard(MemoryCard, snapshot.Memory);
        UpdateCard(DiskCard,   snapshot.Disk);
        UpdateCard(GpuCard,    snapshot.Gpu);

        // Cache latest snapshot for on-demand use by RunAnalysisCommand and RefreshConsumersCommand
        _latestSnapshot = snapshot;

        // Top consumers — throttled to every 5th tick (~15s at 3s interval)
        _consumerTickCounter++;
        if (_consumerTickCounter >= 5)
        {
            _consumerTickCounter = 0;
            UpdateTopConsumers(snapshot.TopConsumers);
        }

        // Automation status
        ActiveAutomations = snapshot.ActiveAutomations;
        AutomationStatus = snapshot.ActiveAutomations == 0
            ? "No automations active"
            : $"{snapshot.ActiveAutomations} automation{(snapshot.ActiveAutomations > 1 ? "s" : "")} active";

        // Bottleneck analysis — NOT auto-updated; user triggers via RunAnalysisCommand

        // Recommendations — only rebuild when content changes (recommendations are stable tick-to-tick)
        var recs = RecommendationEngine.Evaluate(snapshot, _settings, _leakService.CurrentSuspects);
        bool recsChanged = Recommendations.Count != recs.Count ||
            recs.Where((r, i) => i < Recommendations.Count && Recommendations[i].Title != r.Title).Any();
        if (recsChanged)
        {
            Recommendations.Clear();
            foreach (var r in recs)
                Recommendations.Add(new RecommendationViewModel(r));
        }
    }

    [RelayCommand]
    private void RunAnalysis()
    {
        if (_latestSnapshot?.Bottleneck is not null)
            BottleneckCard.Apply(_latestSnapshot.Bottleneck);
    }

    [RelayCommand]
    private void RefreshConsumers()
    {
        if (_latestSnapshot is not null)
        {
            _consumerTickCounter = 0;
            UpdateTopConsumers(_latestSnapshot.TopConsumers);
        }
    }

    private void UpdateTopConsumers(IReadOnlyList<ProcessImpact> newConsumers)
    {
        bool consumersChanged = TopConsumers.Count != newConsumers.Count ||
            newConsumers.Where((c, i) => i < TopConsumers.Count && TopConsumers[i].Name != c.Name).Any();
        if (consumersChanged)
        {
            TopConsumers.Clear();
            foreach (var c in newConsumers)
                TopConsumers.Add(new ProcessImpactViewModel(c));
        }
    }

    private static void UpdateCard(SubsystemCardViewModel card, SubsystemHealth health)
    {
        card.Score        = health.Score;
        card.Level        = health.Level.ToString();
        card.Summary      = health.Summary;
        card.Value        = health.CurrentValue;
        card.TrendArrow   = health.Trend switch
        {
            TrendDirection.Improving => "↑",
            TrendDirection.Degrading => "↓",
            _                        => "→",
        };
        card.LevelBrush   = HealthLevelToBrush(health.Level);
    }

    private static string DescribeHealth(HealthLevel level, double score) => level switch
    {
        HealthLevel.Excellent => "Your system is running smoothly.",
        HealthLevel.Good      => "Your system is in good shape.",
        HealthLevel.Fair      => "Your system is under moderate load.",
        HealthLevel.Poor      => "Your system is under heavy load. Consider closing unused apps.",
        HealthLevel.Critical  => "Your system is critically stressed. Immediate action recommended.",
        _                     => string.Empty,
    };

    // Static brush cache — avoids allocating new SolidColorBrush objects every tick
    private static readonly IBrush _brushExcellent = new SolidColorBrush(Color.Parse("#30D158"));
    private static readonly IBrush _brushGood      = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush _brushFair      = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush _brushPoor      = new SolidColorBrush(Color.Parse("#FF6B35"));
    private static readonly IBrush _brushCritical  = new SolidColorBrush(Color.Parse("#FF3B30"));

    private static IBrush HealthLevelToBrush(HealthLevel level) => level switch
    {
        HealthLevel.Excellent => _brushExcellent,
        HealthLevel.Good      => _brushGood,
        HealthLevel.Fair      => _brushFair,
        HealthLevel.Poor      => _brushPoor,
        HealthLevel.Critical  => _brushCritical,
        _                     => Brushes.Gray,
    };

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _subscription?.Dispose();
        _subscription = null;
        _predictionsSubscription?.Dispose();
        _predictionsSubscription = null;
        _staleSubscription?.Dispose();
        _staleSubscription = null;
    }
}

// ── Sub-ViewModels ─────────────────────────────────────────────────────────────

public partial class SubsystemCardViewModel : ObservableObject
{
    public string Name { get; }
    public string Icon { get; }

    [ObservableProperty] private double _score = 100;
    [ObservableProperty] private string _level = "Excellent";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private double _value;
    [ObservableProperty] private string _trendArrow = "→";
    [ObservableProperty] private IBrush _levelBrush = Brushes.Green;

    public SubsystemCardViewModel(string name, string icon)
    {
        Name = name;
        Icon = icon;
    }
}

public sealed class ProcessImpactViewModel
{
    public string Name         { get; }
    public double ImpactScore  { get; }
    public double CpuPercent   { get; }
    public string MemoryLabel  { get; }
    public double BarWidth     => Math.Min(ImpactScore, 100);

    public ProcessImpactViewModel(ProcessImpact impact)
    {
        Name        = impact.Name;
        ImpactScore = impact.ImpactScore;
        CpuPercent  = impact.CpuPercent;
        MemoryLabel = FormatBytes(impact.MemoryBytes);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}

/// <summary>
/// Wraps a <see cref="BottleneckReport"/> for the Dashboard UI card.
/// Mutable so it can be updated in-place every tick without recreating the object.
/// </summary>
public partial class BottleneckCardViewModel : ObservableObject
{
    // Static brush cache — avoids allocating new brushes every tick
    private static readonly IBrush _severityIdle     = new SolidColorBrush(Color.Parse("#8E8E93"));
    private static readonly IBrush _severityBalanced = new SolidColorBrush(Color.Parse("#30D158"));
    private static readonly IBrush _severityMild     = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush _severityModerate = new SolidColorBrush(Color.Parse("#FF6B35"));
    private static readonly IBrush _severitySevere   = new SolidColorBrush(Color.Parse("#FF3B30"));

    [ObservableProperty] private string _headline       = "Click Run Analysis to check for bottlenecks";
    [ObservableProperty] private string _explanation    = string.Empty;
    [ObservableProperty] private string _upgradeAdvice  = string.Empty;
    [ObservableProperty] private string _workloadLabel  = string.Empty;
    [ObservableProperty] private string _workloadProcess = string.Empty;
    [ObservableProperty] private bool   _hasUpgradeAdvice;
    [ObservableProperty] private bool   _isIdle = true;
    [ObservableProperty] private IBrush _severityBrush = Brushes.Gray;
    [ObservableProperty] private string _bottleneckIcon = "⚙";

    // Detail bars
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _cpuTempLabel  = string.Empty;
    [ObservableProperty] private string _gpuTempLabel  = string.Empty;
    [ObservableProperty] private bool   _showThermalWarning;

    // HVCI / Memory Integrity hint
    [ObservableProperty] private bool   _showCpuTempHint;
    [ObservableProperty] private string _cpuTempHintText = string.Empty;

    public void Apply(BottleneckReport r)
    {
        Headline        = r.Headline;
        Explanation     = r.Explanation;
        UpgradeAdvice   = r.UpgradeAdvice;
        WorkloadLabel   = WorkloadClassifier.WorkloadLabel(r.Workload);
        WorkloadProcess = string.IsNullOrEmpty(r.WorkloadProcess) ? string.Empty : $"({r.WorkloadProcess})";
        HasUpgradeAdvice = !string.IsNullOrEmpty(r.UpgradeAdvice);
        IsIdle           = r.Bottleneck == BottleneckType.Idle;

        SeverityBrush = r.Bottleneck switch
        {
            BottleneckType.Idle     => _severityIdle,
            BottleneckType.Balanced => _severityBalanced,
            _ => r.Severity switch
            {
                BottleneckSeverity.Mild     => _severityMild,
                BottleneckSeverity.Moderate => _severityModerate,
                BottleneckSeverity.Severe   => _severitySevere,
                _                           => Brushes.Gray,
            }
        };

        BottleneckIcon = r.Bottleneck switch
        {
            BottleneckType.GpuBound      => "🖥",
            BottleneckType.VramBound     => "💾",
            BottleneckType.CpuBound      => "⚡",
            BottleneckType.MemoryBound   => "🧠",
            BottleneckType.StorageBound  => "💿",
            BottleneckType.ThermalThrottle => "🌡",
            BottleneckType.Balanced      => "✅",
            _                            => "⏸",
        };

        CpuPercent  = r.CpuPercent;
        GpuPercent  = r.GpuPercent;
        VramPercent = r.GpuVramPercent;
        MemPercent  = r.MemoryPercent;
        DiskPercent = r.DiskPercent;

        CpuTempLabel = r.CpuTempCelsius > 0 ? $"{r.CpuTempCelsius:F0}°C" : "—";
        GpuTempLabel = r.GpuTempCelsius > 0 ? $"{r.GpuTempCelsius:F0}°C" : "—";
        ShowThermalWarning = r.CpuIsThrottling || r.CpuTempCelsius > 95 || r.GpuTempCelsius > 90;

        // Show HVCI hint only when CPU temp is unavailable and Memory Integrity is on
#if WINDOWS
        if (r.CpuTempCelsius < 1)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                if (key?.GetValue("Enabled") is int enabled && enabled == 1)
                {
                    ShowCpuTempHint = true;
                    if (string.IsNullOrEmpty(CpuTempHintText) || CpuTempHintText.StartsWith("CPU temperature"))
                        CpuTempHintText = "CPU temperature requires a kernel-mode sensor driver. Windows Memory Integrity (Core Isolation) is currently blocking it. Disabling Memory Integrity allows the driver to load — a restart is required to take effect.";
                    return;
                }
            }
            catch { }
        }
#endif
        ShowCpuTempHint = false;
    }

    [RelayCommand]
    private void DisableMemoryIntegrity()
    {
#if WINDOWS
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                writable: true);
            key?.SetValue("Enabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            CpuTempHintText = "Memory Integrity disabled. Restart your PC to enable CPU temperature monitoring.";
        }
        catch
        {
            CpuTempHintText = "Registry update failed — ensure the app is running as Administrator.";
        }
#endif
    }
}

public sealed class RecommendationViewModel
{
    // Static brush cache
    private static readonly IBrush _recCriticalBrush = new SolidColorBrush(Color.Parse("#FF3B30"));
    private static readonly IBrush _recWarningBrush  = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush _recInfoBrush     = new SolidColorBrush(Color.Parse("#30D158"));

    public string Title    { get; }
    public string Body     { get; }
    public IBrush IconBrush { get; }
    public string Icon     { get; }

    public RecommendationViewModel(Recommendation rec)
    {
        Title    = rec.Title;
        Body     = rec.Body;
        (Icon, IconBrush) = rec.Severity switch
        {
            RecommendationSeverity.Critical => ("\ue9b2", _recCriticalBrush),
            RecommendationSeverity.Warning  => ("\ue9b1", _recWarningBrush),
            _                               => ("\ue9b0", _recInfoBrush),
        };
    }
}
