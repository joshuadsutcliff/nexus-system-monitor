using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using System.Linq;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Storage;
using NexusMonitor.UI.Messages;
using SkiaSharp;
using System.Collections.Generic;

namespace NexusMonitor.UI.ViewModels;

public partial class HistoryViewModel : ViewModelBase, IDisposable
{
    private readonly IMetricsReader      _reader;
    private readonly IResourceEventReader _eventReader;
    private readonly AppSettings         _appSettings;
    private CancellationTokenSource? _loadCts;
    private bool _disposed;
    private TimeSpan _currentSpan = TimeSpan.FromHours(24);

    [ObservableProperty] private bool _metricsEnabled;

    // ── UI state ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusText  = "Select a time range to load history.";
    [ObservableProperty] private string _dbInfoText  = string.Empty;
    [ObservableProperty] private bool   _hasData;
    [ObservableProperty] private bool   _is1hActive;
    [ObservableProperty] private bool   _is6hActive;
    [ObservableProperty] private bool   _is24hActive;
    [ObservableProperty] private bool   _is7dActive;
    [ObservableProperty] private bool   _is30dActive;

    // ── Top-process table ────────────────────────────────────────────────────
    public ObservableCollection<ProcessSummary> TopProcesses { get; } = new();

    // ── Events timeline (anomaly alerts) ─────────────────────────────────────
    public ObservableCollection<StoredEvent> Events { get; } = new();

    [ObservableProperty] private int    _eventCount;
    [ObservableProperty] private string _eventTypeFilter = "All";

    // Empty-state guard — EventCount can't be bound through BoolConverters.Not
    // directly since it's an int and the converter returns UnsetValue for
    // non-bool input, leaving IsVisible stuck at its default (true).
    public bool HasEvents => EventCount > 0;
    partial void OnEventCountChanged(int value) => OnPropertyChanged(nameof(HasEvents));

    public static IReadOnlyList<string> EventTypeFilters { get; } =
    [
        "All",
        EventType.CpuHigh,
        EventType.MemHigh,
        EventType.GpuHigh,
        EventType.NetAnomaly,
        EventType.NewConnection,
        EventType.ProcessSpike,
    ];

    // ── Resource Incidents (classified bottleneck events) ─────────────────────
    public ObservableCollection<ResourceEvent> ResourceIncidents { get; } = new();

    [ObservableProperty] private int    _incidentCount;
    [ObservableProperty] private string _classificationFilter = "All";

    // Empty-state guard — same rationale as HasEvents above, for ResourceIncidents.
    public bool HasIncidents => IncidentCount > 0;
    partial void OnIncidentCountChanged(int value) => OnPropertyChanged(nameof(HasIncidents));

    public static IReadOnlyList<string> ClassificationFilters { get; } =
    [
        "All",
        "Hardware Bottleneck",
        "Application Spike",
        "Memory Leak",
        "Thermal Throttle",
    ];

    private static EventClassification? ParseClassification(string filter) => filter switch
    {
        "Hardware Bottleneck" => EventClassification.HardwareBottleneck,
        "Application Spike"   => EventClassification.ApplicationSpike,
        "Memory Leak"         => EventClassification.ApplicationLeak,
        "Thermal Throttle"    => EventClassification.ThermalThrottle,
        _                     => null,
    };

    // ── Chart data collections ───────────────────────────────────────────────
    private readonly ObservableCollection<DateTimePoint> _cpuPts   = new();
    private readonly ObservableCollection<DateTimePoint> _memPts   = new();
    private readonly ObservableCollection<DateTimePoint> _diskRPts = new();
    private readonly ObservableCollection<DateTimePoint> _diskWPts = new();
    private readonly ObservableCollection<DateTimePoint> _netSPts  = new();
    private readonly ObservableCollection<DateTimePoint> _netRPts  = new();
    private readonly ObservableCollection<DateTimePoint> _gpuPts   = new();

    // ── Chart series ─────────────────────────────────────────────────────────
    public ISeries[] CpuSeries  { get; }
    public ISeries[] MemSeries  { get; }
    public ISeries[] DiskSeries { get; }
    public ISeries[] NetSeries  { get; }
    public ISeries[] GpuSeries  { get; }

    // ── Axes (X shared, Y per chart) ─────────────────────────────────────────
    private readonly DateTimeAxis _xAxis;
    public Axis[] XAxes    { get; }
    public Axis[] CpuYAxes  { get; }
    public Axis[] MemYAxes  { get; }
    public Axis[] DiskYAxes { get; }
    public Axis[] NetYAxes  { get; }
    public Axis[] GpuYAxes  { get; }

    public HistoryViewModel(IMetricsReader reader, IResourceEventReader eventReader, AppSettings appSettings)
    {
        _reader      = reader;
        _eventReader = eventReader;
        _appSettings = appSettings;
        _metricsEnabled = appSettings.MetricsEnabled;
        Title        = "History";

        WeakReferenceMessenger.Default.Register<MetricsEnabledChangedMessage>(this, (_, msg) =>
            Dispatcher.UIThread.Post(() => MetricsEnabled = msg.Enabled));

        // ── X axis ──────────────────────────────────────────────────────────
        // DateTimeAxis's constructor Labeler = (v) => labeler(new DateTime((long)(v-0.5)))
        // which crashes when LiveChartsCore probes with out-of-range values (0, double.Max…).
        // We replace Labeler with our own safe version that owns the tick→DateTime conversion,
        // applies the same -0.5 offset, range-guards the cast, and reads _currentSpan.
        _xAxis = new DateTimeAxis(TimeSpan.FromMinutes(1), d => d.ToString("HH:mm"))
        {
            TextSize        = 10,
            LabelsPaint     = new SolidColorPaint(new SKColor(120, 120, 120)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80, 50)),
            TicksPaint      = null,
            SubticksPaint   = null,
        };
        _xAxis.Labeler = value =>
        {
            var ticks = (long)(value - 0.5);
            if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) return string.Empty;
            var dt = new DateTime(ticks);
            if (_currentSpan.TotalHours <= 2)   return dt.ToString("HH:mm:ss");
            if (_currentSpan.TotalDays  <= 1)   return dt.ToString("HH:mm");
            if (_currentSpan.TotalDays  <= 7)   return dt.ToString("ddd HH:mm");
            return dt.ToString("MM/dd");
        };
        XAxes = [_xAxis];

        // ── Series ──────────────────────────────────────────────────────────
        CpuSeries  = [Line(_cpuPts,   new SKColor(10,  132, 255), "CPU %")];
        MemSeries  = [Line(_memPts,   new SKColor(48,  209,  88), "Memory")];
        DiskSeries = [Line(_diskRPts, new SKColor(255, 159,  10), "Read"),
                      Line(_diskWPts, new SKColor(255,  69,  58), "Write")];
        NetSeries  = [Line(_netSPts,  new SKColor(100, 210, 255), "Send"),
                      Line(_netRPts,  new SKColor(191,  90, 242), "Recv")];
        GpuSeries  = [Line(_gpuPts,   new SKColor(255, 214,  10), "GPU %")];

        // ── Y axes ──────────────────────────────────────────────────────────
        CpuYAxes  = [YAxis(0, 100,  v => $"{v:F0}%")];
        MemYAxes  = [YAxis(0, null, v => $"{v:F1} GB")];
        DiskYAxes = [YAxis(0, null, v => $"{v:F2} MB/s")];
        NetYAxes  = [YAxis(0, null, v => $"{v:F2} MB/s")];
        GpuYAxes  = [YAxis(0, 100,  v => $"{v:F0}%")];

        RefreshDbInfo();
    }

    // Re-load when filter changes
    partial void OnEventTypeFilterChanged(string value)       => _ = Refresh();
    partial void OnClassificationFilterChanged(string value)  => _ = Refresh();

    // ── Range commands ───────────────────────────────────────────────────────
    [RelayCommand] private Task Load1h()  => LoadAsync(TimeSpan.FromHours(1),  is1h:  true);
    [RelayCommand] private Task Load6h()  => LoadAsync(TimeSpan.FromHours(6),  is6h:  true);
    [RelayCommand] private Task Load24h() => LoadAsync(TimeSpan.FromHours(24), is24h: true);
    [RelayCommand] private Task Load7d()  => LoadAsync(TimeSpan.FromDays(7),   is7d:  true);
    [RelayCommand] private Task Load30d() => LoadAsync(TimeSpan.FromDays(30),  is30d: true);

    [RelayCommand]
    private Task Refresh()
    {
        if (Is1hActive)  return LoadAsync(TimeSpan.FromHours(1),  is1h:  true);
        if (Is6hActive)  return LoadAsync(TimeSpan.FromHours(6),  is6h:  true);
        if (Is7dActive)  return LoadAsync(TimeSpan.FromDays(7),   is7d:  true);
        if (Is30dActive) return LoadAsync(TimeSpan.FromDays(30),  is30d: true);
        return                         LoadAsync(TimeSpan.FromHours(24), is24h: true);
    }

    // ── Data loading ─────────────────────────────────────────────────────────
    private async Task LoadAsync(TimeSpan span,
        bool is1h = false, bool is6h = false, bool is24h = false,
        bool is7d = false, bool is30d = false)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        Is1hActive  = is1h;
        Is6hActive  = is6h;
        Is24hActive = is24h;
        Is7dActive  = is7d;
        Is30dActive = is30d;

        IsLoading    = true;
        HasData      = false;
        _currentSpan = span;

        // Tune X axis step width; the formatter closure already reads _currentSpan.
        _xAxis.UnitWidth = span.TotalHours <= 2  ? TimeSpan.FromSeconds(1).Ticks  :
                           span.TotalDays  <= 1  ? TimeSpan.FromMinutes(1).Ticks  :
                           span.TotalDays  <= 7  ? TimeSpan.FromMinutes(5).Ticks  :
                                                   TimeSpan.FromHours(1).Ticks;

        var to   = DateTimeOffset.Now;
        var from = to - span;

        try
        {
            var metricsTask   = _reader.GetSystemMetricsAsync(from, to, ct);
            var procsTask     = _reader.GetTopProcessSummariesAsync(from, to, topN: 10, ct);
            var evtType       = EventTypeFilter == "All" ? null : EventTypeFilter;
            var eventsTask    = _reader.GetEventsAsync(from, to, evtType, ct);
            var classification = ParseClassification(ClassificationFilter);
            var incidentsTask = _eventReader.GetResourceEventsAsync(from, to, classification, ct);

            await Task.WhenAll(metricsTask, procsTask, eventsTask, incidentsTask);

            if (ct.IsCancellationRequested) return;

            var metrics   = metricsTask.Result;
            var procs     = procsTask.Result;
            var events    = eventsTask.Result;
            var incidents = incidentsTask.Result;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PopulateCharts(metrics);

                TopProcesses.Clear();
                foreach (var p in procs) TopProcesses.Add(p);

                Events.Clear();
                foreach (var e in events.OrderByDescending(e => e.Timestamp)) Events.Add(e);
                EventCount = events.Count;

                ResourceIncidents.Clear();
                foreach (var i in incidents) ResourceIncidents.Add(i);
                IncidentCount = incidents.Count;

                HasData    = metrics.Count > 0;
                StatusText = metrics.Count > 0
                    ? $"{metrics.Count:N0} data points · {from.LocalDateTime:g} → {to.LocalDateTime:g}"
                    : "No data for this range. Make sure Metrics is enabled in Settings and the app has been running.";

                RefreshDbInfo();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void PopulateCharts(IReadOnlyList<MetricsDataPoint> pts)
    {
        int step = Math.Max(1, pts.Count / 2000);

        static void Fill(ObservableCollection<DateTimePoint> col,
                         IReadOnlyList<MetricsDataPoint> src, int step,
                         Func<MetricsDataPoint, double> getValue)
        {
            col.Clear();
            for (int i = 0; i < src.Count; i += step)
                col.Add(new DateTimePoint(src[i].Timestamp.LocalDateTime, getValue(src[i])));
        }

        Fill(_cpuPts,   pts, step, p => p.CpuPercent);
        Fill(_memPts,   pts, step, p => p.MemUsedBytes / 1_073_741_824.0);
        Fill(_diskRPts, pts, step, p => p.DiskReadBps  / 1_048_576.0);
        Fill(_diskWPts, pts, step, p => p.DiskWriteBps / 1_048_576.0);
        Fill(_netSPts,  pts, step, p => p.NetSendBps   / 1_048_576.0);
        Fill(_netRPts,  pts, step, p => p.NetRecvBps   / 1_048_576.0);
        Fill(_gpuPts,   pts, step, p => p.GpuPercent);
    }

    private void RefreshDbInfo()
    {
        var b = _reader.GetDatabaseSizeBytes();
        DbInfoText = b > 1024 ? $"DB: {b / 1_048_576.0:F1} MB" : string.Empty;
    }

    // ── Factory helpers ──────────────────────────────────────────────────────
    private static LineSeries<DateTimePoint> Line(
        ObservableCollection<DateTimePoint> values, SKColor color, string name) => new()
    {
        Values          = values,
        Name            = name,
        Fill            = new LinearGradientPaint(
            new SKColor(color.Red, color.Green, color.Blue, 45),
            new SKColor(color.Red, color.Green, color.Blue, 0),
            new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
        Stroke          = new SolidColorPaint(color, 1.5f),
        GeometrySize    = 0,
        LineSmoothness  = 0.2,
        AnimationsSpeed = TimeSpan.Zero,
    };

    private static Axis YAxis(double min, double? max, Func<double, string> labeler) => new()
    {
        MinLimit        = min,
        MaxLimit        = max,
        TextSize        = 10,
        LabelsPaint     = new SolidColorPaint(new SKColor(120, 120, 120)),
        SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80, 50)),
        TicksPaint      = null,
        SubticksPaint   = null,
        Labeler         = labeler,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
