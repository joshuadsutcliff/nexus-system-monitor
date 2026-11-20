using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using NexusMonitor.Core.Reactive;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Gaming;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Activates/deactivates named Performance Profiles that boost or throttle
/// specific processes on demand. Follows the GamingModeService pattern.
/// </summary>
public sealed class PerformanceProfileService : IDisposable
{
    private readonly IProcessProvider   _processProvider;
    private readonly IPowerPlanProvider _powerPlanProvider;
    private readonly AppSettings        _settings;
    private readonly ILogger<PerformanceProfileService> _logger;

    // pid to exact provider-owned restore receipt, recorded only after a confirmed change
    private readonly Dictionary<int, PriorityRestoreReceipt> _priorityRestores = new();
    // pid → rule that changed EfficiencyMode
    private readonly HashSet<int> _efficiencyPids = new();

    private readonly Subject<string>  _statusMessages = new();
    private readonly Subject<string?> _profileChanged = new();
    private IDisposable? _pollingSubscription;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    private Guid? _activePowerPlan; // power plan before profile was activated
    private Guid? _activeProfileId;

    public bool   IsActive          => _activeProfileId.HasValue;
    public Guid?  ActiveProfileId   => _activeProfileId;
    public int PendingRestoreCount => _priorityRestores.Count;
    public PriorityRestoreReport LastRestoreReport { get; private set; } = PriorityRestoreReport.Empty;
    public string ActiveProfileName =>
        _activeProfileId is { } id
            ? _settings.PerformanceProfiles.FirstOrDefault(p => p.Id == id)?.Name ?? ""
            : "";

    public IObservable<string>  StatusMessages => _statusMessages.AsObservable();
    /// <summary>Fires the active profile name when a profile is activated, or null when deactivated.</summary>
    public IObservable<string?> ProfileChanged => _profileChanged.AsObservable();

    public PerformanceProfileService(
        IProcessProvider   processProvider,
        IPowerPlanProvider powerPlanProvider,
        AppSettings        settings,
        ILogger<PerformanceProfileService> logger)
    {
        _processProvider   = processProvider;
        _powerPlanProvider = powerPlanProvider;
        _settings          = settings;
        _logger            = logger;
    }

    // ── Activate ──────────────────────────────────────────────────────────────

    public void ActivateProfile(Guid profileId)
    {
        if (_activeProfileId.HasValue) DeactivateProfile();
        if (_priorityRestores.Count > 0)
        {
            _statusMessages.OnNext(
                $"Cannot activate a profile while {_priorityRestores.Count} priority restore(s) remain pending");
            return;
        }

        var profile = _settings.PerformanceProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return;

        _activeProfileId = profileId;

        // -- Power plan -------------------------------------------------------
        if (profile.ChangePowerPlan && !string.IsNullOrEmpty(profile.PowerPlanName))
        {
            try
            {
                _activePowerPlan = _powerPlanProvider.GetActivePlan();
                var plans = _powerPlanProvider.GetPowerPlans();
                var target = plans.FirstOrDefault(p =>
                    p.Name.Contains(profile.PowerPlanName, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    _powerPlanProvider.SetActivePlan(target.SchemeGuid);
                    _statusMessages.OnNext($"Power plan: {target.Name}");
                }
            }
            catch (Exception ex)
            {
                _statusMessages.OnNext($"Power plan change failed: {ex.Message}");
            }
        }

        // -- Apply to existing processes --------------------------------------
        _ = ApplyProfileAsync(profile);

        // -- Polling loop — apply to new processes every 2 s -----------------
        _pollingSubscription = _processProvider
            .GetProcessStream(MonitoringCadence.Fast)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "PerformanceProfileService process stream faulted; retrying with backoff"))
            .Sample(TimeSpan.FromSeconds(2))
            .Subscribe(processes => { _ = ApplyProfileAsync(profile, processes); });

        _statusMessages.OnNext($"Profile '{profile.Name}' activated");
        _profileChanged.OnNext(profile.Name);
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    public void DeactivateProfile() => DeactivateProfileAsync().GetAwaiter().GetResult();

    public async Task<PriorityRestoreReport> DeactivateProfileAsync(CancellationToken ct = default)
    {
        if (!_activeProfileId.HasValue && _priorityRestores.Count == 0 && _efficiencyPids.Count == 0)
            return LastRestoreReport;

        var profileName = ActiveProfileName;
        bool wasActive = _activeProfileId.HasValue;
        _activeProfileId = null;

        _pollingSubscription?.Dispose();
        _pollingSubscription = null;

        // Restore power plan
        if (_activePowerPlan.HasValue)
        {
            try { _powerPlanProvider.SetActivePlan(_activePowerPlan.Value); }
            catch (Exception ex) { _logger.LogWarning(ex, "PerformanceProfile: power plan restore failed"); }
            _activePowerPlan = null;
        }

        var report = await RetryPendingRestoresAsync(ct).ConfigureAwait(false);
        await RestoreEfficiencyModesAsync(ct).ConfigureAwait(false);

        if (wasActive)
        {
            _statusMessages.OnNext($"Profile '{profileName}' deactivated");
            _profileChanged.OnNext(null);
        }
        return report;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task ApplyProfileAsync(
        PerformanceProfile profile,
        IReadOnlyList<ProcessInfo>? processes = null)
    {
        processes ??= await _processProvider
            .GetProcessStream(MonitoringCadence.Fast)
            .FirstAsync()
            .ToTask()
            .ConfigureAwait(false);

        if (!await _applyLock.WaitAsync(0).ConfigureAwait(false)) return; // skip if previous apply still running
        try
        {
        if (_activeProfileId != profile.Id) return;

        foreach (var proc in processes)
        {
            foreach (var rule in profile.ProcessRules.Where(r => r.Matches(proc.Name)))
            {
                if (rule.Priority.HasValue && !_priorityRestores.ContainsKey(proc.Pid))
                {
                    try
                    {
                        var result = await _processProvider.ApplyTemporaryPriorityAsync(
                            proc.Pid, rule.Priority.Value).ConfigureAwait(false);
                        if (result.Succeeded && result.Receipt is not null)
                        {
                            _priorityRestores[proc.Pid] = result.Receipt;
                        }
                        else
                        {
                            var message = result.Failure?.Message ?? "unknown priority error";
                            _statusMessages.OnNext(
                                $"Priority change failed: {proc.Name} (PID {proc.Pid}): {message}");
                            _logger.LogWarning(
                                "PerformanceProfile: set priority on {Name} (PID {Pid}) failed: {Message}",
                                proc.Name, proc.Pid, message);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "PerformanceProfile: set priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid);
                        _statusMessages.OnNext(
                            $"Priority change failed: {proc.Name} (PID {proc.Pid}): {ex.Message}");
                    }
                }

                if (rule.EfficiencyMode.HasValue && !_efficiencyPids.Contains(proc.Pid))
                {
                    try
                    {
                        await _processProvider.SetEfficiencyModeAsync(proc.Pid, rule.EfficiencyMode.Value)
                            .ConfigureAwait(false);
                        _efficiencyPids.Add(proc.Pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "PerformanceProfile: set efficiency mode on {Name} (PID {Pid}) failed", proc.Name, proc.Pid);
                    }
                }
            }
        }
        } // end try
        finally { _applyLock.Release(); }
    }

    public async Task<PriorityRestoreReport> RetryPendingRestoresAsync(CancellationToken ct = default)
    {
        if (_priorityRestores.Count == 0) return LastRestoreReport;
        await _applyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int attempted = 0;
            int restored = 0;
            int exited = 0;
            var failures = new List<PriorityRestoreFailure>();

            foreach (var (pid, receipt) in _priorityRestores.ToList())
            {
                attempted++;
                try
                {
                    var result = await _processProvider.RestorePriorityAsync(receipt, ct)
                        .ConfigureAwait(false);
                    if (result.Status == PriorityRestoreStatus.Restored)
                    {
                        restored++;
                        _priorityRestores.Remove(pid);
                    }
                    else if (result.Status == PriorityRestoreStatus.ProcessExited)
                    {
                        exited++;
                        _priorityRestores.Remove(pid);
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
                    _logger.LogWarning(ex, "PerformanceProfile: restore priority PID {Pid} failed", pid);
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
            _applyLock.Release();
        }
    }

    private async Task RestoreEfficiencyModesAsync(CancellationToken ct)
    {
        await _applyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var pid in _efficiencyPids.ToList())
            {
                try
                {
                    await _processProvider.SetEfficiencyModeAsync(pid, false, ct).ConfigureAwait(false);
                    _efficiencyPids.Remove(pid);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PerformanceProfile: restore efficiency mode PID {Pid} failed", pid);
                }
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    public void Dispose()
    {
        DeactivateProfile();
        _pollingSubscription?.Dispose();
        _statusMessages.Dispose();
        _profileChanged.Dispose();
    }
}
