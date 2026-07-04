using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Services;

namespace NexusMonitor.Core.Alerts;

/// <summary>
/// Background service that monitors system metrics and fires <see cref="AlertEvent"/>s
/// when configured thresholds are exceeded for a sustained period.
/// </summary>
public sealed class AlertsService : IDisposable
{
    private readonly ISystemMetricsProvider  _metrics;
    private readonly AppSettings             _settings;
    private readonly INotificationService    _notifications;
    private readonly ILogger<AlertsService>  _logger;
    private readonly QuietHoursService?      _quietHours;

    // Sustain tracking: first moment this rule's value crossed the threshold
    private readonly Dictionary<Guid, DateTime> _firstSeen  = new();
    // Cooldown tracking: last time an alert was fired for a rule
    private readonly Dictionary<Guid, DateTime> _lastFired  = new();

    private readonly Subject<AlertEvent> _events = new();
    private IDisposable? _subscription;
    private volatile bool _running;
    private int  _alertCount;
    private int  _startedGuard;

    public IObservable<AlertEvent> Events     => _events.AsObservable();
    public bool                    IsRunning  => _running;
    public int                     AlertCount => _alertCount;

    public AlertsService(ISystemMetricsProvider metrics, AppSettings settings,
                        INotificationService notifications,
                        ILogger<AlertsService> logger,
                        QuietHoursService? quietHours = null)
    {
        _metrics       = metrics;
        _settings      = settings;
        _notifications = notifications;
        _logger        = logger;
        _quietHours    = quietHours;
    }

    /// <summary>Start the monitoring loop. Safe to call multiple times.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _startedGuard, 1, 0) != 0) return;
        _running = true;
        _subscription = _metrics
            .GetMetricsStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "AlertsService metrics stream faulted; retrying with backoff"))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "AlertsService stream faulted");
                _running = false;
            });
    }

    /// <summary>Stop the monitoring loop.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Interlocked.Exchange(ref _startedGuard, 0);
        _subscription?.Dispose();
        _subscription = null;
        _firstSeen.Clear();
        _lastFired.Clear();
        Interlocked.Exchange(ref _alertCount, 0);
    }

    private void OnTick(SystemMetrics m)
    {
        var rules = _settings.AlertRules ?? [];
        if (rules.Count == 0) return;

        // Extract current values for each metric
        double cpuPercent = m.Cpu.TotalPercent;
        double ramPercent = m.Memory.UsedPercent;
        double diskPercent = m.Disks.Count > 0
            ? m.Disks.Max(d => d.ActivePercent)
            : 0.0;
        double gpuPercent = m.Gpus.Count > 0
            ? m.Gpus[0].UsagePercent
            : 0.0;
        double cpuTemp = m.Cpu.TemperatureCelsius;

        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            if (!rule.IsEnabled) continue;

            double value = rule.Metric switch
            {
                AlertMetric.CpuPercent     => cpuPercent,
                AlertMetric.RamPercent     => ramPercent,
                AlertMetric.DiskPercent    => diskPercent,
                AlertMetric.GpuPercent     => gpuPercent,
                AlertMetric.CpuTemperature => cpuTemp,
                _                          => 0.0
            };

            if (value > rule.Threshold)
            {
                // Start sustain tracking if not yet tracking
                if (!_firstSeen.TryGetValue(rule.Id, out var firstSeen))
                {
                    firstSeen = now;
                    _firstSeen[rule.Id] = firstSeen;
                }

                double sustainedSeconds = (now - firstSeen).TotalSeconds;
                bool sustainMet = sustainedSeconds >= rule.SustainSec;

                // Check cooldown
                bool cooldownElapsed = !_lastFired.TryGetValue(rule.Id, out var lastFired)
                    || (now - lastFired).TotalSeconds >= rule.CooldownSec;

                if (sustainMet && cooldownElapsed)
                {
                    var alertEvent = new AlertEvent(rule, value, now);
                    _events.OnNext(alertEvent);
                    _lastFired[rule.Id] = now;
                    System.Threading.Interlocked.Increment(ref _alertCount);

                    // Fire desktop toast notification if enabled and not in quiet hours
                    if (_settings.DesktopNotificationsEnabled && (_quietHours?.IsActive != true))
                        _notifications.ShowAlert(rule.Name, alertEvent.ValueDisplay, rule.Severity);
                }
            }
            else
            {
                // Value dropped below threshold — reset sustain tracking
                _firstSeen.Remove(rule.Id);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _events.Dispose();
    }
}
