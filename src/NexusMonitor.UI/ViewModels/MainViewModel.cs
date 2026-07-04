using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Services;
using NexusMonitor.UI.Messages;

namespace NexusMonitor.UI.ViewModels;

public enum NavGroup { Pinned, Monitor, Tools, System }

public partial class MainViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private NavItem _selectedNavItem;

    /// <summary>
    /// The sidebar navigation entries including group separators.
    /// ObservableCollection so drag-to-reorder is reflected in the UI without re-creating the list.
    /// </summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    private readonly SettingsService _settings;
    private readonly IDisposable? _quietHoursSubscription;

    public MainViewModel(IServiceProvider services, SettingsService settings,
        QuietHoursService? quietHoursService = null)
    {
        _settings = settings;

        // ── Build the default ordered list (real nav items only, no separators) ───
        var allNavItems = new List<NavItem>
        {
            // Pinned
            new NavItem("Dashboard",    "\uF481", () => services.GetRequiredService<DashboardViewModel>(),    NavGroup.Pinned,   eager: true),
            // Monitor — alphabetical
            new NavItem("Network",      "\uF45B", () => services.GetRequiredService<NetworkViewModel>(),      NavGroup.Monitor,  eager: false),
            new NavItem("Performance",  "\uE2DE", () => services.GetRequiredService<PerformanceViewModel>(),  NavGroup.Monitor,  eager: false),
            new NavItem("Processes",    "\uF134", () => services.GetRequiredService<ProcessesViewModel>(),    NavGroup.Monitor,  eager: false),
            new NavItem("Services",     "\uF76C", () => services.GetRequiredService<ServicesViewModel>(),     NavGroup.Monitor,  eager: false),
            new NavItem("Startup",      "\uF678", () => services.GetRequiredService<StartupViewModel>(),      NavGroup.Monitor,  eager: false),
            new NavItem("System Info",  "\uF35A", () => services.GetRequiredService<SystemInfoViewModel>(),   NavGroup.Monitor,  eager: false),
            // Tools — alphabetical
            new NavItem("Automation",   "\uE945", () => services.GetRequiredService<AutomationViewModel>(),             NavGroup.Tools,    eager: false),
            new NavItem("Diagnostics",  "\uE9D8", () => services.GetRequiredService<DiagnosticsViewModel>(),            NavGroup.Tools,    eager: false),
            new NavItem("Disk Analyzer","\uE9D7", () => services.GetRequiredService<DiskAnalyzerViewModel>(),           NavGroup.Tools,    eager: false),
            new NavItem("Gaming Mode",  "\uF451", () => services.GetRequiredService<GamingModeViewModel>(),             NavGroup.Tools,    eager: false),
            new NavItem("LAN Scanner",  "\uEA5D", () => services.GetRequiredService<LanScannerViewModel>(),             NavGroup.Tools,    eager: false),
            new NavItem("Optimization", "\uE619", () => services.GetRequiredService<OptimizationViewModel>(),           NavGroup.Tools,    eager: false),
            new NavItem("Profiles",     "\uF63E", () => services.GetRequiredService<PerformanceProfilesViewModel>(),    NavGroup.Tools,    eager: false),
            new NavItem("ProBalance",   "\uEA51", () => services.GetRequiredService<ProBalanceViewModel>(),             NavGroup.Tools,    eager: false),
            // System — alphabetical
            new NavItem("Alerts",       "\uF115", () => services.GetRequiredService<AlertsViewModel>(),       NavGroup.System,   eager: false),
            new NavItem("History",      "\uF47F", () => services.GetRequiredService<HistoryViewModel>(),      NavGroup.System,   eager: false),
            new NavItem("Rules",        "\uF407", () => services.GetRequiredService<RulesViewModel>(),        NavGroup.System,   eager: false),
            new NavItem("Settings",     "\uF6AA", () => services.GetRequiredService<SettingsViewModel>(),     NavGroup.System,   eager: false),
        };

        // ── Apply saved per-group order (if any) ────────────────────────────────
        var savedOrder = settings.Current.NavOrder;
        List<NavItem> orderedItems;
        if (savedOrder.Count > 0)
        {
            orderedItems = new List<NavItem>();
            foreach (var group in new[] { NavGroup.Pinned, NavGroup.Monitor, NavGroup.Tools, NavGroup.System })
            {
                var groupItems = allNavItems.Where(n => n.Group == group).ToList();
                // Restore saved sequence within this group
                var savedGroupItems = savedOrder
                    .Select(label => groupItems.FirstOrDefault(n => n.Label == label))
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToList();
                // Append new items not present in saved order
                var unsaved = groupItems.Where(n => !savedGroupItems.Contains(n));
                orderedItems.AddRange(savedGroupItems);
                orderedItems.AddRange(unsaved);
            }
        }
        else
        {
            orderedItems = allNavItems;
        }

        // ── Insert group separators and populate NavItems ────────────────────────
        foreach (var item in BuildNavItemsWithSeparators(orderedItems))
            NavItems.Add(item);

        // Navigate to the user's configured default tab (or Dashboard if not set)
        var defaultLabel = settings.Current.DefaultTab;
        _selectedNavItem = (!string.IsNullOrEmpty(defaultLabel)
            ? NavItems.FirstOrDefault(n => !n.IsSeparator && n.Label == defaultLabel)
            : null)
            ?? NavItems.First(n => !n.IsSeparator);
        _selectedNavItem.IsActive = true;
        _currentPage = _selectedNavItem.GetOrCreate();
        if (_selectedNavItem.VM is IActivatable initialActivatable)
            initialActivatable.Activate();
        Title = "Nexus Monitor";

        WeakReferenceMessenger.Default.Register<NavigateToProcessMessage>(this, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var nav = NavItems.First(n => !n.IsSeparator && n.Label == "Processes");
                if (SelectedNavItem is not null)
                {
                    SelectedNavItem.IsActive = false;
                    if (SelectedNavItem.VM is IActivatable leaving)
                        leaving.Deactivate();
                }
                SelectedNavItem = nav;
                nav.IsActive    = true;
                CurrentPage     = nav.GetOrCreate();
                if (nav.VM is IActivatable entering)
                    entering.Activate();
            });
        });

        // Pause/resume the currently selected tab's UI-only data stream when the main
        // window is hidden (minimized or sent to the tray) / made visible again.
        // Background enforcement services are untouched — they don't implement
        // IActivatable and never see this message. See WindowVisibilityChangedMessage.
        WeakReferenceMessenger.Default.Register<WindowVisibilityChangedMessage>(this, (_, msg) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (SelectedNavItem?.VM is not IActivatable activatable) return;
                if (msg.IsVisible) activatable.Activate();
                else                activatable.Deactivate();
            });
        });

        if (quietHoursService != null)
        {
            UpdateAutomationBadge(quietHoursService.IsActive);
            _quietHoursSubscription = quietHoursService.IsActiveChanged
                .Subscribe(active => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => UpdateAutomationBadge(active)));
        }
    }

    private void UpdateAutomationBadge(bool active)
    {
        var automationNav = NavItems.FirstOrDefault(n => n.Label == "Automation");
        if (automationNav is null) return;
        automationNav.StatusBadge = active ? "🌙" : string.Empty;
    }

    private static List<NavItem> BuildNavItemsWithSeparators(List<NavItem> items)
    {
        var result = new List<NavItem>();

        result.AddRange(items.Where(n => n.Group == NavGroup.Pinned));

        result.Add(new NavItem(NavGroup.Monitor, "MONITOR"));
        result.AddRange(items.Where(n => n.Group == NavGroup.Monitor));

        result.Add(new NavItem(NavGroup.Tools, "TOOLS"));
        result.AddRange(items.Where(n => n.Group == NavGroup.Tools));

        result.Add(new NavItem(NavGroup.System, "SYSTEM"));
        result.AddRange(items.Where(n => n.Group == NavGroup.System));

        return result;
    }

    [RelayCommand]
    internal void Navigate(NavItem item)
    {
        if (item.IsSeparator) return;
        if (item == SelectedNavItem) return;

        if (SelectedNavItem is not null)
        {
            SelectedNavItem.IsActive = false;
            if (SelectedNavItem.VM is IActivatable leaving)
                leaving.Deactivate();
        }

        SelectedNavItem = item;
        item.IsActive   = true;
        CurrentPage     = item.GetOrCreate();

        if (item.VM is IActivatable entering)
            entering.Activate();
    }

    /// <summary>Persists the current sidebar order (real items only) to settings.</summary>
    internal void SaveNavOrder()
    {
        _settings.Current.NavOrder = NavItems
            .Where(n => !n.IsSeparator)
            .Select(n => n.Label)
            .ToList();
        _settings.Save();
    }

    /// <summary>
    /// Disposes every ViewModel that was created during this session.
    /// Called when the main window closes.
    /// </summary>
    public void Dispose()
    {
        _quietHoursSubscription?.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
        foreach (var item in NavItems)
            item.DisposeViewModel();
    }
}

/// <summary>
/// Represents either a sidebar navigation entry or a group separator/label.
/// Separator items have IsSeparator=true and no ViewModel.
/// </summary>
public sealed class NavItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string   Label       { get; }
    public string   Icon        { get; }
    public NavGroup Group       { get; }
    public bool     IsSeparator { get; }
    public bool     IsPinned    => Group == NavGroup.Pinned && !IsSeparator;
    public string?  GroupLabel  { get; }

    private bool _isActive;
    /// <summary>True when this item is the currently selected navigation page.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    private bool _isDragging;
    /// <summary>True while this item is being dragged to a new position.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        internal set => SetProperty(ref _isDragging, value);
    }

    private string _statusBadge = string.Empty;
    /// <summary>Short badge text shown next to the nav label (e.g., "🌙" for Quiet Hours).</summary>
    public string StatusBadge
    {
        get => _statusBadge;
        internal set
        {
            if (SetProperty(ref _statusBadge, value))
                OnPropertyChanged(nameof(HasStatusBadge));
        }
    }

    public bool HasStatusBadge => !string.IsNullOrEmpty(StatusBadge);

    private readonly Func<ViewModelBase>? _factory;
    private ViewModelBase? _cached;

    /// <summary>Regular navigation item.</summary>
    public NavItem(string label, string icon, Func<ViewModelBase> factory, NavGroup group, bool eager = false)
    {
        Label       = label;
        Icon        = icon;
        Group       = group;
        _factory    = factory;
        IsSeparator = false;

        if (eager)
            GetOrCreate();   // start data streams immediately
    }

    /// <summary>Group separator / label entry — not navigable, no ViewModel.</summary>
    public NavItem(NavGroup group, string? groupLabel = null)
    {
        Label       = string.Empty;
        Icon        = string.Empty;
        Group       = group;
        GroupLabel  = groupLabel;
        IsSeparator = true;
        _factory    = null;
    }

    /// <summary>
    /// Returns the cached ViewModel, creating it on first call.
    /// Subsequent calls always return the same instance.
    /// </summary>
    public ViewModelBase GetOrCreate() => _cached ??= _factory!();

    /// <summary>Returns the cached ViewModel if already created, or null if never navigated to.</summary>
    public ViewModelBase? VM => _cached;

    /// <summary>Disposes the cached ViewModel if it was created and implements IDisposable.</summary>
    public void DisposeViewModel() => (_cached as IDisposable)?.Dispose();
}
