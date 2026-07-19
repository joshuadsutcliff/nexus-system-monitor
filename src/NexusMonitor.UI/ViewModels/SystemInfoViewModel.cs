using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusMonitor.Core.Formatting;
using NexusMonitor.Core.Models;
#if WINDOWS
using NexusMonitor.Platform.Windows;
#elif MACOS
using NexusMonitor.Platform.MacOS;
#elif LINUX
using NexusMonitor.Platform.Linux;
#endif

namespace NexusMonitor.UI.ViewModels;

public partial class SystemInfoViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRamSlots))]
    private SystemHardwareInfo? _info;
    [ObservableProperty] private bool   _isLoading = true;
    [ObservableProperty] private string _loadError  = "";

    // Display-ready labels: some platforms (e.g. macOS — hw.cpufrequency_max /
    // hw.l3cachesize are absent on Apple Silicon) report a hard 0 rather than omitting the
    // reading. A real max clock or L3 cache size of 0 doesn't occur on hardware that actually
    // reports the value, so "—" is keyed off the value, not the OS.
    [ObservableProperty] private string _maxClockDisplay = MetricFormatting.Dash;
    [ObservableProperty] private string _l3CacheDisplay  = MetricFormatting.Dash;

    // Socket is meaningless on Apple Silicon (no discrete CPU socket) — the provider reports
    // string.Empty there rather than fabricate a value, so "—" is keyed off the value like the
    // fields above.
    [ObservableProperty] private string _socketDisplay = MetricFormatting.Dash;

    // Null when a real value is showing (no tooltip); explains WHY when the "—" placeholder
    // above is showing instead. See UnavailableMetricCopy.
    [ObservableProperty] private string? _maxClockUnavailableTooltip;
    [ObservableProperty] private string? _l3CacheUnavailableTooltip;
    [ObservableProperty] private string? _socketUnavailableTooltip;

    /// <summary>True when RAM slot data is available. False on Apple Silicon (unified memory has
    /// no discrete slots), where the Memory section collapses the slot table to a single line
    /// (see <see cref="Views.SystemInfoView"/>).</summary>
    public bool HasRamSlots => Info is not null && Info.RamSlots.Count > 0;

    partial void OnInfoChanged(SystemHardwareInfo? value)
    {
        MaxClockDisplay = value is null
            ? MetricFormatting.Dash
            : MetricFormatting.FormatOrDash(value.Cpu.MaxClockMhz, "{0:F0}");
        L3CacheDisplay = value is null
            ? MetricFormatting.Dash
            : MetricFormatting.FormatOrDash(value.Cpu.L3CacheKB, "{0} KB");
        SocketDisplay = value is not null && !string.IsNullOrWhiteSpace(value.Cpu.Socket)
            ? value.Cpu.Socket
            : MetricFormatting.Dash;

        MaxClockUnavailableTooltip = MaxClockDisplay == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
        L3CacheUnavailableTooltip  = L3CacheDisplay  == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
        SocketUnavailableTooltip   = SocketDisplay   == MetricFormatting.Dash ? UnavailableMetricCopy.Generic : null;
    }

#if WINDOWS
    public SystemInfoViewModel(WindowsHardwareInfoProvider provider)
        => _ = LoadAsync(provider);

    private async Task LoadAsync(WindowsHardwareInfoProvider provider)
    {
        try   { Info = await provider.QueryAsync(); }
        catch (Exception ex) { LoadError = $"Failed to read hardware info: {ex.Message}"; }
        finally { IsLoading = false; }
    }
#elif MACOS
    public SystemInfoViewModel(MacOSHardwareInfoProvider provider)
        => _ = LoadMacOSAsync(provider);

    private async Task LoadMacOSAsync(MacOSHardwareInfoProvider provider)
    {
        try   { Info = await provider.QueryAsync(); }
        catch (Exception ex) { LoadError = $"Failed to read hardware info: {ex.Message}"; }
        finally { IsLoading = false; }
    }
#elif LINUX
    public SystemInfoViewModel(LinuxHardwareInfoProvider provider)
        => _ = LoadLinuxAsync(provider);

    private async Task LoadLinuxAsync(LinuxHardwareInfoProvider provider)
    {
        try   { Info = await provider.QueryAsync(); }
        catch (Exception ex) { LoadError = $"Failed to read hardware info: {ex.Message}"; }
        finally { IsLoading = false; }
    }
#else
    public SystemInfoViewModel()
    {
        _ = LoadCrossPlatformAsync();
    }

    private async Task LoadCrossPlatformAsync()
    {
        SystemHardwareInfo? info = null;
        string? error = null;

        await Task.Run(() =>
        {
            try
            {
                var uptime   = TimeSpan.FromMilliseconds(Environment.TickCount64);
                var ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

                var cpu = new CpuHardwareInfo(
                    Name:          RuntimeInformation.OSArchitecture.ToString(),
                    Architecture:  RuntimeInformation.OSArchitecture.ToString(),
                    PhysicalCores: Environment.ProcessorCount,
                    LogicalCores:  Environment.ProcessorCount,
                    L2CacheKB:     0,
                    L3CacheKB:     0,
                    MaxClockMhz:   0,
                    Socket:        string.Empty,
                    Stepping:      string.Empty);

                info = new SystemHardwareInfo(
                    Hostname:               Environment.MachineName,
                    OsName:                 RuntimeInformation.OSDescription,
                    OsBuild:                Environment.OSVersion.ToString(),
                    OsArchitecture:         RuntimeInformation.OSArchitecture.ToString(),
                    Uptime:                 uptime,
                    BiosVendor:             string.Empty,
                    BiosVersion:            string.Empty,
                    MotherboardManufacturer: string.Empty,
                    MotherboardModel:       string.Empty,
                    Cpu:                    cpu,
                    TotalRamBytes:          ramBytes,
                    RamSlots:               [],
                    Gpus:                   [],
                    Storage:                []);
            }
            catch (Exception ex)
            {
                error = $"Failed to read system info: {ex.Message}";
            }
        });

        // Assign ObservableProperties on the UI thread to avoid off-thread PropertyChanged
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (info is not null)  Info = info;
            if (error is not null) LoadError = error;
            IsLoading = false;
        });
    }
#endif
}
