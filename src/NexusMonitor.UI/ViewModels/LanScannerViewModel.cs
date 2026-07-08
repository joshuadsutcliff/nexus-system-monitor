using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Network;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

public partial class LanScannerViewModel : ViewModelBase, IDisposable
{
    private readonly NmapScannerService _scanner;
    private IDisposable? _progressSub;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string  _target            = NmapScannerService.DetectLocalSubnet();
    [ObservableProperty] private int     _scanTypeIndex      = 1; // DefaultPorts
    [ObservableProperty] private bool    _osDetection        = false;
    [ObservableProperty] private bool    _serviceVersion     = false;
    [ObservableProperty] private bool    _isScanning         = false;
    [ObservableProperty] private string  _statusText         = "Ready";
    [ObservableProperty] private int     _hostsUp            = 0;
    [ObservableProperty] private double  _progress           = 0;
    [ObservableProperty] private bool    _nmapAvailable      = false;
    [ObservableProperty] private bool    _isInstalling       = false;
    [ObservableProperty] private string  _installOutput      = "";
    [ObservableProperty] private string  _packageManagerName = "";
    [ObservableProperty] private NmapHost? _selectedHost;
    [ObservableProperty] private bool    _showInstallDetails = false;
    [ObservableProperty] private bool    _installSucceeded   = false;
    [ObservableProperty] private bool    _showPrivilegeNotice = false;

    public bool HasPackageManager  => !string.IsNullOrEmpty(PackageManagerName);
    public bool ShowInstallOutput  => IsInstalling || !string.IsNullOrEmpty(InstallOutput);

    public ObservableCollection<NmapHost> Hosts { get; } = [];

    public static IReadOnlyList<string> ScanTypeLabels { get; } =
        ["Ping Sweep", "Default Ports", "Full Ports", "Service Detection", "OS Detection"];

    public LanScannerViewModel(NmapScannerService scanner)
    {
        Title    = "LAN Scanner";
        _scanner = scanner;

        // Check nmap availability and package manager on background thread, then marshal back to UI thread
        _ = Task.Run(() =>
        {
            var available = NmapScannerService.IsAvailable();
            var pkgMgr    = NmapScannerService.GetPackageManagerName();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                NmapAvailable      = available;
                PackageManagerName = pkgMgr;
                if (!NmapAvailable)
                    StatusText = "nmap not found \u2014 install nmap to use this feature";
            });
        });
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task Scan()
    {
        if (!NmapAvailable) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        Hosts.Clear();
        SelectedHost = null;
        IsScanning   = true;
        Progress     = 0;
        HostsUp      = 0;
        ShowPrivilegeNotice = false;

        // Subscribe to progress
        _progressSub?.Dispose();
        _progressSub = _scanner.Progress
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(p =>
            {
                StatusText = p.StatusText;
                if (p.HostsUp >= 0) HostsUp = p.HostsUp;
                if (p.PercentDone >= 0) Progress = p.PercentDone;
            });

        var options = new NmapScanOptions(
            Target:         Target,
            ScanType:       (NmapScanType)ScanTypeIndex,
            OsDetection:    OsDetection,
            ServiceVersion: ServiceVersion);

        try
        {
            var result = await _scanner.ScanAsync(options, _cts.Token);
            if (result is not null)
            {
                foreach (var host in result.Hosts.OrderBy(h => ParseIp(h.IpAddress)))
                    Hosts.Add(host);
                StatusText = $"Scan complete \u2014 {result.Hosts.Count} hosts up in {result.Elapsed.TotalSeconds:F1}s";
                HostsUp    = result.Hosts.Count;
                Progress   = 100;

                // Unprivileged nmap on macOS/Linux can't do ARP/SYN discovery, so it silently
                // undercounts hosts that don't answer TCP probes - a "clean" completion can still
                // be an incomplete scan. Windows nmap (Npcap driver) doesn't have this restriction.
                ShowPrivilegeNotice = !OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess;
            }
            else
            {
                StatusText = "Scan cancelled";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _progressSub?.Dispose();
            _progressSub = null;
        }
    }

    private bool CanScan() => NmapAvailable && !IsScanning;

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ToggleInstallDetails() => ShowInstallDetails = !ShowInstallDetails;

    private static long ParseIp(string ip)
    {
        try
        {
            var parts = ip.Split('.');
            if (parts.Length != 4) return 0;
            return (long.Parse(parts[0]) << 24) | (long.Parse(parts[1]) << 16) |
                   (long.Parse(parts[2]) << 8)  | long.Parse(parts[3]);
        }
        catch { return 0; }
    }

    [RelayCommand]
    private async Task InstallNmap()
    {
        IsInstalling      = true;
        InstallOutput     = "";
        InstallSucceeded  = false;
        ShowInstallDetails = false;

        var prog = new Progress<string>(line => InstallOutput += line + "\n");
        var (success, _) = await NmapScannerService.InstallAsync(prog);

        IsInstalling = false;

        if (!success)
        {
            StatusText         = "Installation failed \u2014 see output below for details";
            ShowInstallDetails = true; // auto-expand details on failure
            return;
        }

        NmapAvailable = await Task.Run(() => NmapScannerService.IsAvailable());
        if (NmapAvailable)
        {
            InstallSucceeded   = true;
            ShowInstallDetails = false; // hide raw output on success
            StatusText         = "nmap installed successfully \u2014 ready to scan";
        }
        else
        {
            ShowInstallDetails = true;
            StatusText         = "Install completed but nmap not found \u2014 try re-checking";
        }
    }

    [RelayCommand]
    private async Task RecheckNmap()
    {
        InstallOutput = "";
        NmapAvailable = await Task.Run(() => NmapScannerService.IsAvailable());
        StatusText = NmapAvailable
            ? "nmap found \u2014 ready to scan"
            : "nmap not found \u2014 install nmap to use this feature";
    }

    partial void OnIsScanningChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();
    partial void OnNmapAvailableChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();
    partial void OnIsInstallingChanged(bool value)  => OnPropertyChanged(nameof(ShowInstallOutput));
    partial void OnInstallOutputChanged(string value) => OnPropertyChanged(nameof(ShowInstallOutput));
    partial void OnPackageManagerNameChanged(string value) => OnPropertyChanged(nameof(HasPackageManager));

    public void Dispose()
    {
        _progressSub?.Dispose();
        _cts?.Dispose();
    }
}
