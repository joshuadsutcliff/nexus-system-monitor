using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Gaming;

/// <summary>
/// One-click Gaming Mode: switches power plan to High Performance and lowers
/// all non-excluded background processes to Below Normal priority.
/// Restores everything when deactivated.
/// </summary>
public sealed class GamingModeService : IDisposable
{
    private readonly IProcessProvider     _processProvider;
    private readonly IPowerPlanProvider   _powerPlanProvider;
    private readonly AppSettings          _settings;
    private readonly ILogger<GamingModeService> _logger;
    private readonly IPlatformCapabilities _platform;

    // pid to exact provider-owned restore receipt, recorded only after a confirmed change
    private readonly Dictionary<int, PriorityRestoreReceipt> _throttled = new();
    private readonly Subject<string> _statusMessages = new();
    private IDisposable? _pollingSubscription;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private Guid _savedPowerPlan = Guid.Empty;
    private bool _active;

    public bool IsActive => _active;
    public int PendingRestoreCount => _throttled.Count;
    public PriorityRestoreReport LastRestoreReport { get; private set; } = PriorityRestoreReport.Empty;

    public IObservable<string> StatusMessages => _statusMessages.AsObservable();

    public GamingModeService(
        IProcessProvider processProvider,
        IPowerPlanProvider powerPlanProvider,
        AppSettings settings,
        ILogger<GamingModeService> logger,
        IPlatformCapabilities? platformCapabilities = null)
    {
        _processProvider   = processProvider;
        _powerPlanProvider = powerPlanProvider;
        _settings          = settings;
        _logger            = logger;
        _platform          = platformCapabilities ?? new MockPlatformCapabilities();
    }

    /// <summary>
    /// Activate Gaming Mode.
    /// 1. Saves the current power plan, then switches to High Performance
    ///    (or Ultimate Performance if available).
    /// 2. Lowers all non-excluded, non-game background processes to BelowNormal.
    /// 3. Starts a 2-second polling loop to apply the same to any new processes
    ///    that appear while Gaming Mode is active.
    /// </summary>
    public void Start(string? gameProcessName = null)
    {
        if (_active) return;
        if (_throttled.Count > 0)
        {
            _statusMessages.OnNext(
                $"Gaming Mode cannot activate while {_throttled.Count} priority restore(s) remain pending");
            return;
        }
        _active = true;

        // -- Power plan -------------------------------------------------------
        // Skipped on platforms where IPlatformCapabilities.SupportsPowerPlan is false (macOS):
        // _savedPowerPlan stays Guid.Empty, so Stop()'s restore step below naturally no-ops too.
        if (_platform.SupportsPowerPlan)
        {
            try
            {
                _savedPowerPlan = _powerPlanProvider.GetActivePlan();

                // Prefer Ultimate Performance; fall back to High Performance
                var plans = _powerPlanProvider.GetPowerPlans();
                bool hasUltimate = plans.Any(p =>
                    p.SchemeGuid == IPowerPlanProvider.UltimatePerf);

                Guid targetPlan = hasUltimate
                    ? IPowerPlanProvider.UltimatePerf
                    : IPowerPlanProvider.HighPerformance;

                _powerPlanProvider.SetActivePlan(targetPlan);

                var planName = plans.FirstOrDefault(p => p.SchemeGuid == targetPlan)?.Name
                               ?? targetPlan.ToString();
                _statusMessages.OnNext($"Power plan switched to: {planName}");
            }
            catch (Exception ex)
            {
                _statusMessages.OnNext($"Power plan change failed: {ex.Message}");
            }
        }

        // -- Initial process throttle -----------------------------------------
        _ = ThrottleBackgroundProcessesAsync(gameProcessName ?? _settings.GamingModeGameProcess);

        // -- Polling loop (re-apply to new processes every 2 s via shared stream) ------
        string capturedGameProcess = gameProcessName ?? _settings.GamingModeGameProcess;
        _pollingSubscription = _processProvider
            .GetProcessStream(MonitoringCadence.Fast)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "GamingModeService process stream faulted; retrying with backoff"))
            .Sample(TimeSpan.FromSeconds(2))
            .Subscribe(
                processes => { _ = ThrottleBackgroundProcessesAsync(capturedGameProcess, processes); },
                ex => { _logger.LogError(ex, "GamingModeService stream faulted"); _active = false; });

        _statusMessages.OnNext("Gaming Mode activated");
    }

    /// <summary>
    /// Deactivate Gaming Mode.
    /// Restores the original power plan and each process's exact native priority.
    /// </summary>
    public void Stop() => StopAsync().GetAwaiter().GetResult();

    public async Task<PriorityRestoreReport> StopAsync(CancellationToken ct = default)
    {
        if (!_active && _throttled.Count == 0) return LastRestoreReport;
        bool wasActive = _active;
        _active = false;

        _pollingSubscription?.Dispose();
        _pollingSubscription = null;

        // -- Restore power plan -----------------------------------------------
        if (_savedPowerPlan != Guid.Empty)
        {
            try
            {
                _powerPlanProvider.SetActivePlan(_savedPowerPlan);
                var plans = _powerPlanProvider.GetPowerPlans();
                var name = plans.FirstOrDefault(p => p.SchemeGuid == _savedPowerPlan)?.Name
                           ?? _savedPowerPlan.ToString();
                _statusMessages.OnNext($"Power plan restored to: {name}");
            }
            catch (Exception ex)
            {
                _statusMessages.OnNext($"Power plan restore failed: {ex.Message}");
            }
            _savedPowerPlan = Guid.Empty;
        }

        // -- Restore process priorities ----------------------------------------
        var report = await RetryPendingRestoresAsync(ct).ConfigureAwait(false);

        if (wasActive) _statusMessages.OnNext("Gaming Mode deactivated");
        return report;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task ThrottleBackgroundProcessesAsync(string? gameProcess, IReadOnlyList<ProcessInfo>? processes = null)
    {
        if (!_active) return;
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_active) return;
            processes  ??= await _processProvider.GetProcessesAsync().ConfigureAwait(false);
            var exclusions = _settings.GamingModeExclusions;

            var candidates = processes
                .Where(p =>
                    p.Pid != Environment.ProcessId &&
                    p.Category != ProcessCategory.SystemKernel &&
                    !_throttled.ContainsKey(p.Pid) &&
                    !IsExcluded(p.Name, gameProcess, exclusions))
                .ToList();

            foreach (var proc in candidates)
            {
                try
                {
                    var result = await _processProvider.ApplyTemporaryPriorityAsync(
                        proc.Pid, ProcessPriority.BelowNormal).ConfigureAwait(false);
                    if (result.Succeeded && result.Receipt is not null)
                    {
                        _throttled[proc.Pid] = result.Receipt;
                        _statusMessages.OnNext($"Throttled: {proc.Name} (PID {proc.Pid})");
                    }
                    else
                    {
                        var message = result.Failure?.Message ?? "unknown priority error";
                        _statusMessages.OnNext($"Throttle failed: {proc.Name} (PID {proc.Pid}): {message}");
                        _logger.LogWarning(
                            "Gaming mode: could not throttle {Name} (PID {Pid}): {Message}",
                            proc.Name, proc.Pid, message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Gaming mode: could not throttle {Name} (PID {Pid})", proc.Name, proc.Pid);
                    _statusMessages.OnNext($"Throttle failed: {proc.Name} (PID {proc.Pid}): {ex.Message}");
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Gaming mode: throttle loop error"); }
        finally { _stateLock.Release(); }
    }

    public async Task<PriorityRestoreReport> RetryPendingRestoresAsync(CancellationToken ct = default)
    {
        if (_throttled.Count == 0) return LastRestoreReport;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int attempted = 0;
            int restored = 0;
            int exited = 0;
            var failures = new List<PriorityRestoreFailure>();

            foreach (var (pid, receipt) in _throttled.ToList())
            {
                attempted++;
                try
                {
                    var result = await _processProvider.RestorePriorityAsync(receipt, ct)
                        .ConfigureAwait(false);
                    if (result.Status == PriorityRestoreStatus.Restored)
                    {
                        restored++;
                        _throttled.Remove(pid);
                        _statusMessages.OnNext($"Restored priority: PID {pid}");
                    }
                    else if (result.Status == PriorityRestoreStatus.ProcessExited)
                    {
                        exited++;
                        _throttled.Remove(pid);
                    }
                    else
                    {
                        failures.Add(new PriorityRestoreFailure(
                            pid,
                            result.Failure ?? new ProcessPriorityFailure(
                                ProcessPriorityFailureKind.Unknown,
                                "managed",
                                0,
                                $"Priority restore failed for process {pid}")));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gaming mode: could not restore PID {Pid}", pid);
                    failures.Add(new PriorityRestoreFailure(
                        pid,
                        new ProcessPriorityFailure(
                            ProcessPriorityFailureKind.Unknown,
                            "managed",
                            0,
                            ex.Message)));
                }
            }

            LastRestoreReport = new PriorityRestoreReport(attempted, restored, exited, failures);
            if (failures.Count > 0)
                _statusMessages.OnNext(
                    $"Priority restore incomplete: {restored} restored, {exited} exited, {failures.Count} pending");
            return LastRestoreReport;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static bool IsExcluded(
        string processName,
        string? gameProcess,
        List<string> exclusions)
    {
        if (!string.IsNullOrEmpty(gameProcess) &&
            processName.Equals(gameProcess, StringComparison.OrdinalIgnoreCase))
            return true;

        return exclusions.Any(ex =>
            processName.Equals(ex, StringComparison.OrdinalIgnoreCase));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
        _statusMessages.Dispose();
    }

    // ── Public stat helpers ───────────────────────────────────────────────────

    /// <summary>Number of processes with a confirmed temporary priority change.</summary>
    public int ThrottledCount => _throttled.Count;
}
