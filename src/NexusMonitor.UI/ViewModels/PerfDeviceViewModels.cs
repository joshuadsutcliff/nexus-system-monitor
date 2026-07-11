using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NexusMonitor.Core.Models;
using SkiaSharp;

namespace NexusMonitor.UI.ViewModels;

/// <summary>Abstract base for all Performance sidebar device entries.</summary>
public abstract partial class PerfDeviceViewModel : ObservableObject
{
    public abstract string DeviceName    { get; }
    public abstract string DeviceSubName { get; }

    [ObservableProperty] private string _valueDisplay    = "0%";
    [ObservableProperty] private string _subValueDisplay = string.Empty;
    [ObservableProperty] private bool _isDragging;
    [ObservableProperty] private bool _isSelected;

    // Ring-buffer index — shared across all collections of this device VM
    protected int _ringIdx;

    // 20-point mini sparkline for the sidebar list item
    public ObservableCollection<ObservableValue> MiniHistory { get; } =
        new(Enumerable.Range(0, 20).Select(_ => new ObservableValue(0)));

    public ISeries[] MiniSeries { get; }
    public Axis[]    MiniXAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 19 }];
    public Axis[]    MiniYAxes  { get; } = [new() { IsVisible = false, MinLimit = 0 }];

    protected PerfDeviceViewModel(SKColor color)
    {
        MiniSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values          = MiniHistory,
                GeometrySize    = 0,
                LineSmoothness  = 0.4,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(color.WithAlpha(200), 1.5f),
                Fill   = new LinearGradientPaint(
                    new[] { color.WithAlpha(80), color.WithAlpha(0) },
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
            }
        ];
    }

    public abstract void Update(SystemMetrics m);

    // In-place ring-buffer update: mutates ObservableValue.Value in the oldest slot.
    // LiveChartsCore detects Value changes via INotifyPropertyChanged — no allocation, no O(n) shift.
    protected static void Push(ObservableCollection<ObservableValue> col, int ringIdx, double value)
        => col[ringIdx % col.Count].Value = value;

    protected void PushAll(double value, params ObservableCollection<ObservableValue>[] cols)
    {
        foreach (var col in cols)
            col[_ringIdx % col.Count].Value = value;
        _ringIdx++;
    }

    protected static string FmtBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824L => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576L     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024L         => $"{bytes / 1_024.0:F0} KB",
        _                 => $"{bytes} B",
    };
}

// ── Overview ───────────────────────────────────────────────────────────────────
/// <summary>Triggers the Overview DataTemplate — no device-specific data.</summary>
public sealed class OverviewDeviceViewModel : PerfDeviceViewModel
{
    public override string DeviceName    => "Overview";
    public override string DeviceSubName => string.Empty;
    public OverviewDeviceViewModel() : base(new SKColor(120, 120, 120)) { }
    public override void Update(SystemMetrics m) { } // parent PerformanceViewModel owns overview bindings
}

// ── CPU ────────────────────────────────────────────────────────────────────────
public sealed partial class CpuDeviceViewModel : PerfDeviceViewModel
{
    private string _modelName;
    public override string DeviceName    => _modelName.Length > 0 ? _modelName : "CPU";
    public override string DeviceSubName => "CPU";

    public ObservableCollection<ObservableValue> History { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ISeries[] HistorySeries { get; }
    public Axis[]    HistoryXAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 59 }];
    public Axis[]    HistoryYAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 100 }];

    [ObservableProperty] private double _utilPercent;
    [ObservableProperty] private string _speedDisplay  = string.Empty;
    [ObservableProperty] private double _tempC;
    [ObservableProperty] private int    _logicalCores;
    [ObservableProperty] private int    _physicalCores;
    [ObservableProperty] private string _uptimeDisplay = string.Empty;
    [ObservableProperty] private IReadOnlyList<CoreCellViewModel> _coreCells = [];

    // ── Extended detail (Task Manager parity) ─────────────────────────────
    [ObservableProperty] private string _baseSpeedDisplay  = string.Empty;
    [ObservableProperty] private int    _sockets;
    [ObservableProperty] private string _virtualization = string.Empty;
    [ObservableProperty] private string _l1CacheDisplay = string.Empty;
    [ObservableProperty] private string _l2CacheDisplay = string.Empty;
    [ObservableProperty] private string _l3CacheDisplay = string.Empty;
    [ObservableProperty] private int    _processCount;
    [ObservableProperty] private int    _threadCount;
    [ObservableProperty] private int    _handleCount;

    public CpuDeviceViewModel(string modelName) : base(new SKColor(10, 132, 255))
    {
        _modelName = modelName;
        HistorySeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = History, GeometrySize = 0, LineSmoothness = 0.6,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(10, 132, 255), 2),
                Fill   = new LinearGradientPaint(
                    new SKColor(10, 132, 255, 80), new SKColor(10, 132, 255, 0),
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
            }
        ];
    }

    internal void SetModelName(string name)
    {
        if (name != _modelName)
        {
            _modelName = name;
            OnPropertyChanged(nameof(DeviceName));
        }
    }

    public override void Update(SystemMetrics m)
    {
        UtilPercent   = Math.Round(m.Cpu.TotalPercent, 1);
        SpeedDisplay  = $"{m.Cpu.FrequencyMhz / 1000.0:F2} GHz";
        TempC         = Math.Round(m.Cpu.TemperatureCelsius, 0);
        LogicalCores  = m.Cpu.LogicalCores;
        PhysicalCores = m.Cpu.PhysicalCores;
        var up        = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeDisplay = up.TotalDays >= 1
            ? $"{(int)up.TotalDays}d {up.Hours:D2}h {up.Minutes:D2}m"
            : $"{up.Hours:D2}h {up.Minutes:D2}m {up.Seconds:D2}s";

        // Extended detail
        BaseSpeedDisplay = m.Cpu.BaseSpeedMhz > 0
            ? $"{m.Cpu.BaseSpeedMhz / 1000.0:F2} GHz" : string.Empty;
        Sockets         = m.Cpu.Sockets;
        Virtualization  = m.Cpu.VirtualizationStatus;
        L1CacheDisplay  = m.Cpu.L1CacheBytes > 0 ? FmtBytes(m.Cpu.L1CacheBytes) : string.Empty;
        L2CacheDisplay  = m.Cpu.L2CacheBytes > 0 ? FmtBytes(m.Cpu.L2CacheBytes) : string.Empty;
        L3CacheDisplay  = m.Cpu.L3CacheBytes > 0 ? FmtBytes(m.Cpu.L3CacheBytes) : string.Empty;
        ProcessCount    = m.Cpu.ProcessCount;
        ThreadCount     = m.Cpu.ThreadCount;
        HandleCount     = m.Cpu.HandleCount;

        ValueDisplay    = $"{UtilPercent:F1}%";
        SubValueDisplay = SpeedDisplay;
        Push(History,     _ringIdx, m.Cpu.TotalPercent);
        Push(MiniHistory, _ringIdx, m.Cpu.TotalPercent);
        _ringIdx++;

        // Update in-place to avoid allocating a new list every tick
        if (m.Cpu.CorePercents.Count > 0)
        {
            var corePercents = m.Cpu.CorePercents;
            var existing     = CoreCells;
            if (existing.Count == corePercents.Count)
            {
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
    }
}

// ── Memory ─────────────────────────────────────────────────────────────────────
public sealed partial class MemoryDeviceViewModel : PerfDeviceViewModel
{
    public override string DeviceName    => "Memory";
    public override string DeviceSubName => "RAM";

    public ObservableCollection<ObservableValue> History { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ISeries[] HistorySeries { get; }
    public Axis[]    HistoryXAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 59 }];
    public Axis[]    HistoryYAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 100 }];

    [ObservableProperty] private double _usedGb;
    [ObservableProperty] private double _totalGb;
    [ObservableProperty] private double _availGb;
    [ObservableProperty] private double _cachedGb;
    [ObservableProperty] private double _pagedPoolGb;
    [ObservableProperty] private double _nonPagedPoolGb;
    [ObservableProperty] private double _usedPercent;

    // ── Extended detail ─────────────────────────────────────────────────────
    [ObservableProperty] private string _committedDisplay   = string.Empty;
    [ObservableProperty] private int    _speedMhz;
    [ObservableProperty] private string _slotsDisplay       = string.Empty;
    [ObservableProperty] private string _formFactor         = string.Empty;
    [ObservableProperty] private string _hwReservedDisplay  = string.Empty;
    [ObservableProperty] private string _compressedDisplay  = string.Empty;

    public MemoryDeviceViewModel() : base(new SKColor(52, 199, 89))
    {
        HistorySeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = History, GeometrySize = 0, LineSmoothness = 0.6,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(52, 199, 89), 2),
                Fill   = new LinearGradientPaint(
                    new SKColor(52, 199, 89, 80), new SKColor(52, 199, 89, 0),
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
            }
        ];
    }

    public override void Update(SystemMetrics m)
    {
        UsedGb         = Math.Round(m.Memory.UsedBytes      / 1e9, 1);
        TotalGb        = Math.Round(m.Memory.TotalBytes     / 1e9, 1);
        AvailGb        = Math.Round(m.Memory.AvailableBytes / 1e9, 1);
        CachedGb       = Math.Round(m.Memory.CachedBytes    / 1e9, 1);
        PagedPoolGb    = Math.Round(m.Memory.PagedPoolBytes    / 1e9, 2);
        NonPagedPoolGb = Math.Round(m.Memory.NonPagedPoolBytes / 1e9, 2);
        UsedPercent    = Math.Round(m.Memory.UsedPercent, 1);

        // Extended detail
        CommittedDisplay = m.Memory.CommitLimitBytes > 0
            ? $"{m.Memory.CommitTotalBytes / 1e9:F1} / {m.Memory.CommitLimitBytes / 1e9:F1} GB"
            : string.Empty;
        SpeedMhz         = m.Memory.SpeedMhz;
        SlotsDisplay     = m.Memory.TotalSlots > 0
            ? $"{m.Memory.SlotsUsed} of {m.Memory.TotalSlots}" : string.Empty;
        FormFactor       = m.Memory.FormFactor;
        HwReservedDisplay = m.Memory.HardwareReservedBytes > 0
            ? FmtBytes(m.Memory.HardwareReservedBytes) : string.Empty;
        CompressedDisplay = m.Memory.CompressedBytes > 0
            ? FmtBytes(m.Memory.CompressedBytes) : string.Empty;

        ValueDisplay    = $"{UsedPercent:F1}%";
        SubValueDisplay = $"{UsedGb:F1} / {TotalGb:F0} GB";
        Push(History,     _ringIdx, m.Memory.UsedPercent);
        Push(MiniHistory, _ringIdx, m.Memory.UsedPercent);
        _ringIdx++;
    }
}

// ── Disk ───────────────────────────────────────────────────────────────────────
public sealed partial class DiskDeviceViewModel : PerfDeviceViewModel
{
    public override string DeviceName    => PhysicalName;
    public override string DeviceSubName => DriveLetters;

    public int    DiskIndex    { get; }
    public string PhysicalName { get; }
    public string DriveLetters { get; }

    public ObservableCollection<ObservableValue> ReadHistory  { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ObservableCollection<ObservableValue> WriteHistory { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ISeries[] HistorySeries { get; }
    public Axis[]    HistoryXAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 59 }];

    [ObservableProperty] private double _activePercent;
    [ObservableProperty] private string _readRateDisplay   = "0 B/s";
    [ObservableProperty] private string _writeRateDisplay  = "0 B/s";
    [ObservableProperty] private double _totalGb;
    [ObservableProperty] private double _freeGb;
    [ObservableProperty] private double _capacityUsedPercent;
    [ObservableProperty] private string _labelText = string.Empty;

    // ── Extended detail ─────────────────────────────────────────────────────
    [ObservableProperty] private string _diskType       = string.Empty;
    [ObservableProperty] private string _avgResponseDisplay = string.Empty;
    [ObservableProperty] private bool   _isSystemDisk;
    [ObservableProperty] private bool   _hasPageFile;
    [ObservableProperty] private IReadOnlyList<VolumeInfoViewModel> _volumes = [];

    public DiskDeviceViewModel(DiskMetrics d) : base(new SKColor(100, 210, 255))
    {
        DiskIndex    = d.DiskIndex;
        PhysicalName = d.PhysicalName.Length > 0 ? d.PhysicalName : $"Disk {d.DiskIndex}";
        DriveLetters = d.AllDriveLetters.Length > 0 ? d.AllDriveLetters
                     : d.DriveLetter.Length   > 0 ? d.DriveLetter : "—";
        HistorySeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = ReadHistory, GeometrySize = 0, LineSmoothness = 0.5,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(100, 210, 255), 2),
                Fill = null, Name = "Read"
            },
            new LineSeries<ObservableValue>
            {
                Values = WriteHistory, GeometrySize = 0, LineSmoothness = 0.5,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(255, 159, 10), 2),
                Fill = null, Name = "Write"
            },
        ];
        ApplyMetrics(d);
    }

    public override void Update(SystemMetrics m)
    {
        var d = m.Disks.FirstOrDefault(x => x.DiskIndex == DiskIndex);
        if (d is not null) ApplyMetrics(d);
    }

    private void ApplyMetrics(DiskMetrics d)
    {
        ActivePercent       = Math.Round(d.ActivePercent, 1);
        ReadRateDisplay     = FormatRate(d.ReadBytesPerSec);
        WriteRateDisplay    = FormatRate(d.WriteBytesPerSec);
        TotalGb             = Math.Round(d.TotalBytes / 1e9, 0);
        FreeGb              = Math.Round(d.FreeBytes  / 1e9, 0);
        CapacityUsedPercent = Math.Round(d.UsedPercent, 1);
        LabelText           = d.Label;

        // Extended detail
        DiskType           = d.DiskType;
        AvgResponseDisplay = d.AverageResponseMs > 0
            ? $"{d.AverageResponseMs:F1} ms" : string.Empty;
        IsSystemDisk       = d.IsSystemDisk;
        HasPageFile        = d.HasPageFile;
        Volumes = d.Volumes.Select(v => new VolumeInfoViewModel
        {
            DriveLetter = v.DriveLetter,
            Label       = v.Label.Length > 0 ? v.Label : "(Unnamed)",
            FileSystem  = v.FileSystem,
            TotalGb     = Math.Round(v.TotalBytes / 1e9, 1),
            FreeGb      = Math.Round(v.FreeBytes  / 1e9, 1),
            UsedPercent = Math.Round(v.UsedPercent, 1),
        }).ToList();

        ValueDisplay    = $"{ActivePercent:F0}%";
        SubValueDisplay = $"R:{ReadRateDisplay} W:{WriteRateDisplay}";
        Push(ReadHistory,  _ringIdx, d.ReadBytesPerSec  / 1e6);
        Push(WriteHistory, _ringIdx, d.WriteBytesPerSec / 1e6);
        Push(MiniHistory,  _ringIdx, ActivePercent);
        _ringIdx++;
    }

    private static string FormatRate(long bps) => bps switch
    {
        >= 1_000_000 => $"{bps / 1_000_000.0:F1} MB/s",
        >= 1_000     => $"{bps / 1_000.0:F0} KB/s",
        _            => $"{bps} B/s",
    };
}

/// <summary>View model for per-volume info displayed in the disk detail panel.</summary>
public sealed class VolumeInfoViewModel
{
    public string DriveLetter { get; init; } = string.Empty;
    public string Label       { get; init; } = string.Empty;
    public string FileSystem  { get; init; } = string.Empty;
    public double TotalGb     { get; init; }
    public double FreeGb      { get; init; }
    public double UsedPercent { get; init; }
}

// ── Network ────────────────────────────────────────────────────────────────────
public sealed partial class NetworkDeviceViewModel : PerfDeviceViewModel
{
    public override string DeviceName    => AdapterName;
    public override string DeviceSubName => AdapterType;

    [ObservableProperty] private string _adapterName      = "Network";
    [ObservableProperty] private string _adapterType      = string.Empty;
    [ObservableProperty] private string _ipv4Display      = string.Empty;
    [ObservableProperty] private string _ipv6Display      = string.Empty;
    [ObservableProperty] private string _linkSpeedDisplay = string.Empty;
    [ObservableProperty] private string _sendRateDisplay  = "0 B/s";
    [ObservableProperty] private string _recvRateDisplay  = "0 B/s";

    // ── Extended detail ─────────────────────────────────────────────────────
    [ObservableProperty] private string _dnsSuffix       = string.Empty;
    [ObservableProperty] private string _connectionType  = string.Empty;
    [ObservableProperty] private string _totalSentDisplay  = string.Empty;
    [ObservableProperty] private string _totalRecvDisplay  = string.Empty;

    public ObservableCollection<ObservableValue> SendHistory { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ObservableCollection<ObservableValue> RecvHistory { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ISeries[] HistorySeries { get; }
    public Axis[]    HistoryXAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 59 }];

    public NetworkDeviceViewModel(NetworkAdapterMetrics n) : base(new SKColor(191, 90, 242))
    {
        HistorySeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = RecvHistory, GeometrySize = 0, LineSmoothness = 0.5,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(191, 90, 242), 2),
                Fill = null, Name = "Download"
            },
            new LineSeries<ObservableValue>
            {
                Values = SendHistory, GeometrySize = 0, LineSmoothness = 0.5,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(255, 69, 58), 2),
                Fill = null, Name = "Upload"
            },
        ];
        ApplyMetrics(n);
    }

    public override void Update(SystemMetrics m)
    {
        var n = m.NetworkAdapters.FirstOrDefault(x => x.Name == AdapterName || x.Description == AdapterName);
        if (n is not null) ApplyMetrics(n);
    }

    private void ApplyMetrics(NetworkAdapterMetrics n)
    {
        AdapterName      = n.Name.Length > 0 ? n.Name : n.Description;
        AdapterType      = n.AdapterType;
        Ipv4Display      = n.IPv4Address;
        Ipv6Display      = n.IPv6Address;
        LinkSpeedDisplay = n.LinkSpeedBps >= 1_000_000_000L ? $"{n.LinkSpeedBps / 1_000_000_000.0:F1} Gbps"
                         : n.LinkSpeedBps >= 1_000_000L     ? $"{n.LinkSpeedBps / 1_000_000.0:F0} Mbps"
                         : n.LinkSpeedBps > 0               ? $"{n.LinkSpeedBps / 1_000.0:F0} Kbps"
                         : string.Empty;
        SendRateDisplay  = FormatRate(n.SendBytesPerSec);
        RecvRateDisplay  = FormatRate(n.RecvBytesPerSec);

        // Extended detail
        DnsSuffix        = n.DnsSuffix;
        ConnectionType   = n.ConnectionType;
        TotalSentDisplay = FmtBytes(n.TotalSendBytes);
        TotalRecvDisplay = FmtBytes(n.TotalRecvBytes);

        ValueDisplay    = RecvRateDisplay;
        SubValueDisplay = $"↑ {SendRateDisplay}";
        Push(SendHistory, _ringIdx, n.SendBytesPerSec / 1e6);
        Push(RecvHistory, _ringIdx, n.RecvBytesPerSec / 1e6);
        Push(MiniHistory, _ringIdx, Math.Min(100, (n.SendBytesPerSec + n.RecvBytesPerSec) / 1e6));
        _ringIdx++;
    }

    private static string FormatRate(long bps) => bps switch
    {
        >= 1_000_000 => $"{bps / 1_000_000.0:F1} MB/s",
        >= 1_000     => $"{bps / 1_000.0:F0} KB/s",
        _            => $"{bps} B/s",
    };
}

// ── GPU ────────────────────────────────────────────────────────────────────────
public sealed partial class GpuDeviceViewModel : PerfDeviceViewModel
{
    private readonly string _gpuName;
    public override string DeviceName    => _gpuName.Length > 0 ? _gpuName : "GPU";
    public override string DeviceSubName => "GPU";

    public ObservableCollection<ObservableValue> History { get; } =
        new(Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    public ISeries[] HistorySeries { get; }
    public Axis[]    HistoryXAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 59 }];
    public Axis[]    HistoryYAxes  { get; } = [new() { IsVisible = false, MinLimit = 0, MaxLimit = 100 }];

    [ObservableProperty] private double _usagePercent;
    [ObservableProperty] private double _engine3DPercent;
    [ObservableProperty] private double _engineCopyPercent;
    [ObservableProperty] private double _engineDecodePercent;
    [ObservableProperty] private double _engineEncodePercent;
    [ObservableProperty] private double _dedicatedUsedGb;
    [ObservableProperty] private double _dedicatedTotalGb;
    [ObservableProperty] private double _sharedUsedGb;
    [ObservableProperty] private double _tempC;

    // ── Extended detail ─────────────────────────────────────────────────────
    [ObservableProperty] private double _sharedTotalGb;
    [ObservableProperty] private string _driverVersion    = string.Empty;
    [ObservableProperty] private string _directXVersion   = string.Empty;
    [ObservableProperty] private string _physicalLocation = string.Empty;

    public GpuDeviceViewModel(GpuMetrics g) : base(new SKColor(255, 159, 10))
    {
        _gpuName = g.Name;
        HistorySeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = History, GeometrySize = 0, LineSmoothness = 0.6,
                AnimationsSpeed = TimeSpan.Zero,
                Stroke = new SolidColorPaint(new SKColor(255, 159, 10), 2),
                Fill   = new LinearGradientPaint(
                    new SKColor(255, 159, 10, 80), new SKColor(255, 159, 10, 0),
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
            }
        ];
        ApplyMetrics(g);
    }

    public override void Update(SystemMetrics m)
    {
        var g = m.Gpus.FirstOrDefault(x => x.Name == _gpuName);
        if (g is not null) ApplyMetrics(g);
    }

    private void ApplyMetrics(GpuMetrics g)
    {
        UsagePercent        = Math.Round(g.UsagePercent, 1);
        Engine3DPercent     = Math.Round(g.Engine3DPercent, 1);
        EngineCopyPercent   = Math.Round(g.EngineCopyPercent, 1);
        EngineDecodePercent = Math.Round(g.EngineVideoDecodePercent, 1);
        EngineEncodePercent = Math.Round(g.EngineVideoEncodePercent, 1);
        DedicatedUsedGb     = Math.Round(g.DedicatedMemoryUsedBytes  / 1e9, 1);
        DedicatedTotalGb    = Math.Round(g.DedicatedMemoryTotalBytes / 1e9, 1);
        SharedUsedGb        = Math.Round(g.SharedMemoryUsedBytes / 1e9, 1);
        TempC               = Math.Round(g.TemperatureCelsius, 0);

        // Extended detail
        SharedTotalGb    = Math.Round(g.SharedMemoryTotalBytes / 1e9, 1);
        DriverVersion    = g.DriverVersion;
        DirectXVersion   = g.DirectXVersion;
        PhysicalLocation = g.PhysicalLocation;

        ValueDisplay    = $"{UsagePercent:F1}%";
        // DedicatedTotalGb is honestly 0 on Apple Silicon (unified memory has no dedicated VRAM
        // pool to report a total for — see GpuMetrics.DedicatedMemoryTotalBytes). Showing
        // "X.X / 0 GB VRAM" there would read as a fabricated zero-capacity pool, so an unknown
        // total gets a used-only, VRAM-free label instead; platforms with a real dedicated pool
        // keep the original "used / total GB VRAM" format unchanged.
        SubValueDisplay = DedicatedTotalGb > 0
            ? $"{DedicatedUsedGb:F1} / {DedicatedTotalGb:F0} GB VRAM"
            : $"{DedicatedUsedGb:F1} GB GPU memory";
        Push(History,     _ringIdx, g.UsagePercent);
        Push(MiniHistory, _ringIdx, g.UsagePercent);
        _ringIdx++;
    }
}
