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

    // pid → rule that applied Priority (so we restore to Normal on deactivate)
    private readonly HashSet<int> _boostedPids = new();
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
            .GetProcessStream(TimeSpan.FromSeconds(1))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "PerformanceProfileService process stream faulted; retrying with backoff"))
            .Sample(TimeSpan.FromSeconds(2))
            .Subscribe(processes => { _ = ApplyProfileAsync(profile, processes); });

        _statusMessages.OnNext($"Profile '{profile.Name}' activated");
        _profileChanged.OnNext(profile.Name);
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    public void DeactivateProfile()
    {
        if (!_activeProfileId.HasValue) return;

        var profileName = ActiveProfileName;
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

        // Restore process priorities
        _ = RestoreProcessesAsync();

        _statusMessages.OnNext($"Profile '{profileName}' deactivated");
        _profileChanged.OnNext(null);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task ApplyProfileAsync(
        PerformanceProfile profile,
        IReadOnlyList<ProcessInfo>? processes = null)
    {
        if (!await _applyLock.WaitAsync(0)) return; // skip if previous apply still running
        try
        {
        processes ??= await _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(1))
            .FirstAsync()
            .ToTask();

        foreach (var proc in processes)
        {
            foreach (var rule in profile.ProcessRules.Where(r => r.Matches(proc.Name)))
            {
                if (rule.Priority.HasValue && _boostedPids.Add(proc.Pid))
                    try { await _processProvider.SetPriorityAsync(proc.Pid, rule.Priority.Value); }
                    catch (Exception ex) { _logger.LogWarning(ex, "PerformanceProfile: set priority on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }

                if (rule.EfficiencyMode.HasValue && _efficiencyPids.Add(proc.Pid))
                    try { await _processProvider.SetEfficiencyModeAsync(proc.Pid, rule.EfficiencyMode.Value); }
                    catch (Exception ex) { _logger.LogWarning(ex, "PerformanceProfile: set efficiency mode on {Name} (PID {Pid}) failed", proc.Name, proc.Pid); }
            }
        }
        } // end try
        finally { _applyLock.Release(); }
    }

    private async Task RestoreProcessesAsync()
    {
        await _applyLock.WaitAsync(); // wait for any in-progress apply to finish
        foreach (var pid in _boostedPids.ToList())
            try { await _processProvider.SetPriorityAsync(pid, ProcessPriority.Normal); }
            catch (Exception ex) { _logger.LogDebug(ex, "PerformanceProfile: restore priority PID {Pid} failed", pid); }
        foreach (var pid in _efficiencyPids.ToList())
            try { await _processProvider.SetEfficiencyModeAsync(pid, false); }
            catch (Exception ex) { _logger.LogDebug(ex, "PerformanceProfile: restore efficiency mode PID {Pid} failed", pid); }
        _boostedPids.Clear();
        _efficiencyPids.Clear();
        _applyLock.Release();
    }

    public void Dispose()
    {
        DeactivateProfile();
        _pollingSubscription?.Dispose();
        _statusMessages.Dispose();
        _profileChanged.Dispose();
    }
}
