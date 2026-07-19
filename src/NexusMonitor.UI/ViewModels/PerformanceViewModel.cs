using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Formatting;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using NexusMonitor.UI.Messages;
using Avalonia.Threading;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

/// <summary>Represents one CPU core cell in the per-core heatmap.</summary>
public record CoreCellViewModel(int Index, double Percent);

/// <summary>Represents one fixed drive in the per-drive disk usage breakdown.</summary>
public record DriveRowViewModel(
    string DriveLetter,
    string Label,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UsedPercent);

public partial class PerformanceViewModel : ViewModelBase, IActivatable, IDisposable
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private IDisposable? _subscription;
    private int _initialIntervalMs = 1000;
    private int _ringIdx;
    private const int HistoryLength = 60;

    // CPU
    private readonly ObservableCollection<ObservableValue> _cpuValues = new(Enumerable.Range(0, HistoryLength).Select(_ => new ObservableValue(0)));
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _cpuFrequencyGhz;
    [ObservableProperty] private double _cpuTempC;
    // Display-ready labels: some platforms (e.g. macOS — no public sensor/frequency API) report
    // a hard 0 for these rather than omitting the reading. A real 0°C / 0 GHz doesn't occur on
    // hardware that actually reports the value, so "—" is keyed off the value, not the OS.
    [ObservableProperty] private string _cpuTempLabel = MetricFormatting.Dash;
    [ObservableProperty] private string _cpuFrequencyLabel = MetricFormatting.Dash;
    // Null when a real value is showing (no tooltip); explains WHY when the "—" placeholder above
    // is showing instead. See UnavailableMetricCopy.
    [ObservableProperty] private string? _cpuTempUnavailableTooltip;
    [ObservableProperty] private string? _cpuFrequencyUnavailableTooltip;
    [ObservableProperty] private string _cpuModelName = string.Empty;
    [ObservableProperty] private int _logicalCores;
    [ObservableProperty] private IReadOnlyList<CoreCellViewModel> _coreCells = [];
    public ISeries[] CpuSeries { get; }
    public Axis[] CpuXAxes { get; }
    public Axis[] CpuYAxes { get; }

    // Memory
    private readonly ObservableCollection<ObservableValue> _memValues = new(Enumerable.Range(0, HistoryLength).Select(_ => new ObservableValue(0)));
    [ObservableProperty] private double _memUsedGb;
    [ObservableProperty] private double _memTotalGb;
    [ObservableProperty] private double _memPercent;
    public ISeries[] MemSeries { get; }
    public Axis[] MemYAxes { get; }

    // Disk
    private readonly ObservableCollection<ObservableValue> _diskReadValues = new(Enumerable.Range(0, HistoryLength).Select(_ => new ObservableValue(0)));
    private readonly ObservableCollection<ObservableValue> _diskWriteValues = new(Enumerable.Range(0, HistoryLength).Select(_ => new ObservableValue(0)));
    [ObservableProperty] private double _diskReadMbps;
    [ObservableProperty] private double _diskWriteMbps;
    public ISeries[] DiskSeries { get; }

    // Network
    private readonly ObservableCollection<ObservableValue> _netSendValues = new(Enumerable.Range(0, HistoryLength).Select(_ => new ObservableValue(0)));
    private readonly ObservableCollection<ObservableValue> _netRecvValues = new(Enumerable.Range(0, HistoryLength).Select(_ => new ObservableValue(0)));
    [ObservableProperty] private double _netSendMbps;
    [ObservableProperty] private double _netRecvMbps;
    public ISeries[] NetSeries { get; }

    // Disk drives
    [ObservableProperty] private IReadOnlyList<DriveRowViewModel> _driveRows = [];

    // GPU
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _gpuMemGb;
    [ObservableProperty] private double _gpuMemTotalGb;
    [ObservableProperty] private string _gpuMemDisplay = string.Empty;
    [ObservableProperty] private string _gpuName = string.Empty;
    [ObservableProperty] private bool   _hasGpuData;

    // ── Device sidebar ────────────────────────────────────────────────────────────
    public ObservableCollection<PerfDeviceViewModel> Devices { get; } = new();
    [ObservableProperty] private PerfDeviceViewModel? _selectedDevice;
    private CpuDeviceViewModel?    _cpuDevice;
    private MemoryDeviceViewModel? _memDevice;

    // Dictionary-backed device maps — replace per-tick O(N) .OfType<T>().Any() scans
    private readonly Dictionary<int,    DiskDeviceViewModel>    _diskDeviceMap = new();
    private readonly Dictionary<string, NetworkDeviceViewModel> _netDeviceMap  = new();
    private readonly Dictionary<string, GpuDeviceViewModel>    _gpuDeviceMap  = new();

    // Disk fingerprint — skip DriveRows rebuild when free space hasn't changed
    private long _lastDiskFingerprint;

    public PerformanceViewModel(ISystemMetricsProvider metricsProvider, SettingsService settings)
    {
        _metricsProvider = metricsProvider;
        Title = "Performance";
        _initialIntervalMs = settings.Current.UpdateIntervalMs;

        var sharedXAxes = new Axis[]
        {
            new() { IsVisible = false, MinLimit = 0, MaxLimit = HistoryLength - 1 }
        };

        CpuSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = _cpuValues,
                Fill = new LinearGradientPaint(
                    new SKColor(10, 132, 255, 80), new SKColor(10, 132, 255, 0),
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
                Stroke = new SolidColorPaint(new SKColor(10, 132, 255), 2),
                GeometrySize = 0,
                LineSmoothness = 0.6,
                Name = "CPU"
            }
        ];
        CpuXAxes = sharedXAxes;
        CpuYAxes = [new Axis { MinLimit = 0, MaxLimit = 100, IsVisible = false }];

        MemSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = _memValues,
                Fill = new LinearGradientPaint(
                    new SKColor(52, 199, 89, 80), new SKColor(52, 199, 89, 0),
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
                Stroke = new SolidColorPaint(new SKColor(52, 199, 89), 2),
                GeometrySize = 0,
                LineSmoothness = 0.6,
                Name = "Memory"
            }
        ];
        MemYAxes = [new Axis { MinLimit = 0, MaxLimit = 100, IsVisible = false }];

        DiskSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = _diskReadValues,
                Stroke = new SolidColorPaint(new SKColor(100, 210, 255), 2),
                Fill = null, GeometrySize = 0, LineSmoothness = 0.6, Name = "Read"
            },
            new LineSeries<ObservableValue>
            {
                Values = _diskWriteValues,
                Stroke = new SolidColorPaint(new SKColor(255, 159, 10), 2),
                Fill = null, GeometrySize = 0, LineSmoothness = 0.6, Name = "Write"
            }
        ];

        NetSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = _netRecvValues,
                Stroke = new SolidColorPaint(new SKColor(191, 90, 242), 2),
                Fill = null, GeometrySize = 0, LineSmoothness = 0.6, Name = "Download"
            },
            new LineSeries<ObservableValue>
            {
                Values = _netSendValues,
                Stroke = new SolidColorPaint(new SKColor(255, 69, 58), 2),
                Fill = null, GeometrySize = 0, LineSmoothness = 0.6, Name = "Upload"
            }
        ];

        StartMetricsStream(TimeSpan.FromMilliseconds(_initialIntervalMs));

        WeakReferenceMessenger.Default.Register<MetricsIntervalChangedMessage>(this, (_, msg) =>
            Dispatcher.UIThread.InvokeAsync(() => StartMetricsStream(msg.Interval)));

        // Populate sidebar devices
        var overview = new OverviewDeviceViewModel();
        _cpuDevice   = new CpuDeviceViewModel(string.Empty);
        _memDevice   = new MemoryDeviceViewModel();
        Devices.Add(overview);
        Devices.Add(_cpuDevice);
        Devices.Add(_memDevice);
        SelectedDevice = overview;
        overview.IsSelected = true;
    }

    partial void OnSelectedDeviceChanged(PerfDeviceViewModel? oldValue, PerfDeviceViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    // Already on UI thread via ObserveOn(RxApp.MainThreadScheduler) — no inner Post needed.
    private void Update(SystemMetrics m)
    {
        // CPU
        CpuPercent      = Math.Round(m.Cpu.TotalPercent, 1);
        CpuFrequencyGhz = Math.Round(m.Cpu.FrequencyMhz / 1000.0, 2);
        CpuTempC        = Math.Round(m.Cpu.TemperatureCelsius, 0);
        // Keyed off the raw reading, not the OS: no sensor data comes back as <= 0, never as
        // a real measurement, so "—" reliably means "unavailable" everywhere.
        CpuFrequencyLabel = MetricFormatting.FormatOrDash(m.Cpu.FrequencyMhz, CpuFrequencyGhz, "{0:F2} GHz");
        CpuTempLabel      = MetricFormatting.FormatOrDash(m.Cpu.TemperatureCelsius, CpuTempC, "{0}°C");
        CpuFrequencyUnavailableTooltip = CpuFrequencyLabel == MetricFormatting.Dash
            ? UnavailableMetricCopy.Generic
            : null;
        CpuTempUnavailableTooltip = CpuTempLabel == MetricFormatting.Dash
            ? UnavailableMetricCopy.CpuTempUnsupported
            : null;
        CpuModelName    = m.Cpu.ModelName;
        LogicalCores    = m.Cpu.LogicalCores;
        Push(_cpuValues, _ringIdx, m.Cpu.TotalPercent);

        // Per-core heatmap cells — update in-place to avoid allocating a new list every tick
        if (m.Cpu.CorePercents.Count > 0)
        {
            var corePercents = m.Cpu.CorePercents;
            var existing     = CoreCells;
            if (existing.Count == corePercents.Count)
            {
                // Update in-place: replace only changed cells (record with{} allocates only for changed)
                bool changed = false;
                var updated = new CoreCellViewModel[corePercents.Count];
                for (int ci = 0; ci < corePercents.Count; ci++)
                {
                    var rounded = Math.Round(corePercents[ci], 0);
                    updated[ci] = existing[ci] with { Percent = rounded };
                    if (updated[ci].Percent != existing[ci].Percent) changed = true;
                }
                if (changed) CoreCells = updated;
            }
            else
            {
                CoreCells = [.. corePercents.Select((p, i) => new CoreCellViewModel(i, Math.Round(p, 0)))];
            }
        }

        // Memory
        MemTotalGb = Math.Round(m.Memory.TotalBytes / 1e9, 1);
        MemUsedGb  = Math.Round(m.Memory.UsedBytes  / 1e9, 1);
        MemPercent = Math.Round(m.Memory.UsedPercent, 1);
        Push(_memValues, _ringIdx, m.Memory.UsedPercent);

        // Disk (aggregate first disk)
        if (m.Disks.Count > 0)
        {
            DiskReadMbps  = Math.Round(m.Disks[0].ReadBytesPerSec  / 1e6, 1);
            DiskWriteMbps = Math.Round(m.Disks[0].WriteBytesPerSec / 1e6, 1);
            Push(_diskReadValues,  _ringIdx, m.Disks[0].ReadBytesPerSec  / 1e6);
            Push(_diskWriteValues, _ringIdx, m.Disks[0].WriteBytesPerSec / 1e6);
        }

        // Per-drive summary — skip rebuild when disk free space hasn't changed
        {
            long fp = 0;
            foreach (var d in m.Disks)
                fp = fp * 31 + (d.DriveLetter.Length > 0 ? d.DriveLetter[0] : 0) + (d.FreeBytes >> 20);
            if (fp != _lastDiskFingerprint)
            {
                _lastDiskFingerprint = fp;
                DriveRows = m.Disks
                    .Where(d => d.TotalBytes > 0)
                    .Select(d =>
                    {
                        double totalGb = Math.Round(d.TotalBytes / 1e9, 1);
                        double freeGb  = Math.Round(d.FreeBytes  / 1e9, 1);
                        double usedGb  = Math.Round(totalGb - freeGb, 1);
                        double pct     = totalGb > 0 ? Math.Round((usedGb / totalGb) * 100, 0) : 0;
                        return new DriveRowViewModel(d.DriveLetter, d.Label, totalGb, usedGb, freeGb, pct);
                    })
                    .ToList();
            }
        }

        // Network — prefer the adapter with the most activity (linear scan, no sort/alloc)
        if (m.NetworkAdapters.Count > 0)
        {
            var activeNic = m.NetworkAdapters[0];
            long maxThroughput = activeNic.SendBytesPerSec + activeNic.RecvBytesPerSec;
            for (int ai = 1; ai < m.NetworkAdapters.Count; ai++)
            {
                var a = m.NetworkAdapters[ai];
                long t = a.SendBytesPerSec + a.RecvBytesPerSec;
                if (t > maxThroughput || (t == maxThroughput && a.IsConnected && !activeNic.IsConnected))
                {
                    activeNic      = a;
                    maxThroughput  = t;
                }
            }
            NetSendMbps = Math.Round(activeNic.SendBytesPerSec / 1e6, 2);
            NetRecvMbps = Math.Round(activeNic.RecvBytesPerSec / 1e6, 2);
            Push(_netSendValues, _ringIdx, activeNic.SendBytesPerSec / 1e6);
            Push(_netRecvValues, _ringIdx, activeNic.RecvBytesPerSec / 1e6);
        }

        _ringIdx++;

        // GPU
        HasGpuData = m.Gpus.Count > 0;
        if (m.Gpus.Count > 0)
        {
            GpuPercent    = Math.Round(m.Gpus[0].UsagePercent, 1);
            GpuMemGb      = Math.Round(m.Gpus[0].DedicatedMemoryUsedBytes   / 1e9, 1);
            GpuMemTotalGb = Math.Round(m.Gpus[0].DedicatedMemoryTotalBytes  / 1e9, 1);
            // Same honest-unknown-total handling as GpuDeviceViewModel.DedicatedDisplay /
            // SubValueDisplay (Sym-2 Task 6): an unknown total (0, e.g. Apple Silicon unified
            // memory) must not render "used / 0 GB" — fall back to a used-only figure. Platforms
            // with a real dedicated pool keep the original "used / total GB" format unchanged.
            // Pure logic lives in GpuMemoryDisplayMath (NexusMonitor.Core.Formatting) so it's
            // unit-testable — see GpuMemoryDisplayMathTests.
            GpuMemDisplay = GpuMemoryDisplayMath.FormatUsedTotal(GpuMemGb, GpuMemTotalGb);
            GpuName       = m.Gpus[0].Name;
        }

        // Update CPU device name when model is known
        if (_cpuDevice is not null && m.Cpu.ModelName.Length > 0)
            _cpuDevice.SetModelName(m.Cpu.ModelName);

        // Update all device VMs
        foreach (var dev in Devices)
            dev.Update(m);

        // Sync dynamic devices (add new, remove stale)
        SyncDiskDevices(m.Disks);
        SyncNetworkDevices(m.NetworkAdapters);
        SyncGpuDevices(m.Gpus);
    }

    private static void Push(ObservableCollection<ObservableValue> col, int ringIdx, double value)
        => col[ringIdx % col.Count].Value = value;

    private void SyncDiskDevices(IReadOnlyList<DiskMetrics> disks)
    {
        // Add new disks
        foreach (var d in disks)
        {
            if (!_diskDeviceMap.ContainsKey(d.DiskIndex))
            {
                var vm = new DiskDeviceViewModel(d);
                _diskDeviceMap[d.DiskIndex] = vm;
                Devices.Add(vm);
            }
        }
        // Remove stale
        if (_diskDeviceMap.Count != disks.Count)
        {
            var activeSet = new HashSet<int>(disks.Select(d => d.DiskIndex));
            var stale = _diskDeviceMap.Keys.Where(k => !activeSet.Contains(k)).ToList();
            foreach (var k in stale)
            {
                Devices.Remove(_diskDeviceMap[k]);
                _diskDeviceMap.Remove(k);
            }
        }
    }

    private void SyncNetworkDevices(IReadOnlyList<NetworkAdapterMetrics> adapters)
    {
        // Add new adapters
        foreach (var n in adapters)
        {
            string key = n.Name.Length > 0 ? n.Name : n.Description;
            if (!_netDeviceMap.ContainsKey(key))
            {
                var vm = new NetworkDeviceViewModel(n);
                _netDeviceMap[key] = vm;
                Devices.Add(vm);
            }
        }
        // Remove stale
        if (_netDeviceMap.Count != adapters.Count)
        {
            var activeSet = new HashSet<string>(adapters.Select(n => n.Name.Length > 0 ? n.Name : n.Description));
            var stale = _netDeviceMap.Keys.Where(k => !activeSet.Contains(k)).ToList();
            foreach (var k in stale)
            {
                Devices.Remove(_netDeviceMap[k]);
                _netDeviceMap.Remove(k);
            }
        }
    }

    private void SyncGpuDevices(IReadOnlyList<GpuMetrics> gpus)
    {
        // Add new GPUs
        foreach (var g in gpus)
        {
            if (!_gpuDeviceMap.ContainsKey(g.Name))
            {
                var vm = new GpuDeviceViewModel(g);
                _gpuDeviceMap[g.Name] = vm;
                Devices.Add(vm);
            }
        }
        // Remove stale
        if (_gpuDeviceMap.Count != gpus.Count)
        {
            var activeSet = new HashSet<string>(gpus.Select(g => g.Name));
            var stale = _gpuDeviceMap.Keys.Where(k => !activeSet.Contains(k)).ToList();
            foreach (var k in stale)
            {
                Devices.Remove(_gpuDeviceMap[k]);
                _gpuDeviceMap.Remove(k);
            }
        }
    }

    private void StartMetricsStream(TimeSpan interval)
    {
        _subscription?.Dispose();
        _subscription = null;
        _subscription = _metricsProvider
            .GetMetricsStream(interval)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(Update);
    }

    /// <inheritdoc/>
    public void Activate()
    {
        if (_subscription is not null) return;  // idempotent guard
        StartMetricsStream(TimeSpan.FromMilliseconds(_initialIntervalMs));
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _subscription?.Dispose();
    }
}
