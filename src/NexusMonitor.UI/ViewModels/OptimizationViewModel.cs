﻿using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using ReactiveUI;

namespace NexusMonitor.UI.ViewModels;

// ── Impact tier ───────────────────────────────────────────────────────────────

public enum ImpactLevel { Critical, High, Medium }

/// <summary>
/// Single row in the recommendations list.  Processes are bucketed into impact
/// tiers based on CPU and RAM usage thresholds.
/// </summary>
public record RecommendationRow(
    int Pid, string Name, double CpuPercent, long WorkingSetBytes, ImpactLevel Impact)
{
    public string CpuDisplay  => CpuPercent < 0.1 ? "< 0.1%" : $"{CpuPercent:F1}%";
    public string MemDisplay  => ProcessRowViewModel.FormatBytes(WorkingSetBytes);

    public string ImpactIcon  => Impact switch
    {
        ImpactLevel.Critical => "🔴",
        ImpactLevel.High     => "🟠",
        _                    => "🟡",
    };
    public string ImpactLabel => Impact switch
    {
        ImpactLevel.Critical => "Critical",
        ImpactLevel.High     => "High",
        _                    => "Medium",
    };
    public string ImpactColor => Impact switch
    {
        ImpactLevel.Critical => "#FF453A",
        ImpactLevel.High     => "#FF9F0A",
        _                    => "#FFD60A",
    };
    public string ActionTip => Impact == ImpactLevel.Critical
        ? "Severe resource usage — consider terminating or reducing priority"
        : "Elevated resource usage — reducing priority frees up resources for other apps";
}

// ── ViewModel ─────────────────────────────────────────────────────────────────

public partial class OptimizationViewModel : ViewModelBase, IActivatable, IDisposable
{
    private readonly IProcessProvider _processProvider;
    private IDisposable? _subscription;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // ── Data ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<RecommendationRow> _recommendations = [];

    // ── Overview stats ────────────────────────────────────────────────────────

    [ObservableProperty] private int    _criticalCount;
    [ObservableProperty] private int    _highCount;
    [ObservableProperty] private int    _mediumCount;
    [ObservableProperty] private string _totalCpuDisplay = "0%";
    [ObservableProperty] private string _topRamProcess   = "—";
    [ObservableProperty] private string _summaryLine     = "Scanning processes…";

    // ── Status bar ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _lastAction = string.Empty;

    // ── Constructor ───────────────────────────────────────────────────────────

    private record struct UpdateResult(
        string TotalCpuDisplay, string TopRamProcess,
        List<RecommendationRow> Recs,
        int CriticalCount, int HighCount, int MediumCount,
        string SummaryLine);

    public OptimizationViewModel(IProcessProvider processProvider)
    {
        Title             = "Optimization";
        _processProvider  = processProvider;
        StartMonitoring();
    }

    private void StartMonitoring()
    {
        // ComputeUpdate runs on the timer/background thread; ApplyUpdate on the UI thread.
        // This keeps the classification LINQ + sort off the UI thread.
        _subscription = _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(2))
            .Select(ComputeUpdate)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ApplyUpdate);
    }

    /// <summary>
    /// Resumes the recommendations subscription (UI-only — the underlying process stream
    /// is a shared multicast driven by other subscribers regardless).
    /// </summary>
    /// <inheritdoc/>
    public void Activate()
    {
        if (_subscription is not null) return; // idempotent guard
        StartMonitoring();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    // ── Update (split into compute + apply) ───────────────────────────────────

    private static UpdateResult ComputeUpdate(IReadOnlyList<ProcessInfo> processes)
    {
        double totalCpu = Math.Min(processes.Sum(p => p.CpuPercent), 100.0);
        string totalCpuDisplay = $"{totalCpu:F0}%";

        var topRam = processes.OrderByDescending(p => p.WorkingSetBytes).FirstOrDefault();
        string topRamProcess = topRam is not null
            ? $"{topRam.Name} ({ProcessRowViewModel.FormatBytes(topRam.WorkingSetBytes)})"
            : "—";

        // Classify & sort: Critical → High → Medium; within tier sort by CPU desc
        var recs = processes
            .Select(p => new { p, tier = Classify(p) })
            .Where(x => x.tier.HasValue)
            .Select(x => new RecommendationRow(
                x.p.Pid, x.p.Name, x.p.CpuPercent, x.p.WorkingSetBytes, x.tier!.Value))
            .OrderBy(r => (int)r.Impact)
            .ThenByDescending(r => r.CpuPercent)
            .ThenByDescending(r => r.WorkingSetBytes)
            .Take(15)
            .ToList();

        int criticalCount = recs.Count(r => r.Impact == ImpactLevel.Critical);
        int highCount     = recs.Count(r => r.Impact == ImpactLevel.High);
        int mediumCount   = recs.Count(r => r.Impact == ImpactLevel.Medium);
        string summaryLine = recs.Count == 0
            ? "✅  System is running efficiently — no high-impact processes detected"
            : $"Found {recs.Count} process{(recs.Count == 1 ? "" : "es")} using significant resources";

        return new UpdateResult(totalCpuDisplay, topRamProcess, recs,
            criticalCount, highCount, mediumCount, summaryLine);
    }

    private void ApplyUpdate(UpdateResult r)
    {
        TotalCpuDisplay = r.TotalCpuDisplay;
        TopRamProcess   = r.TopRamProcess;
        CriticalCount   = r.CriticalCount;
        HighCount       = r.HighCount;
        MediumCount     = r.MediumCount;
        SummaryLine     = r.SummaryLine;
        SyncCollection(Recommendations, r.Recs);
    }

    /// <summary>
    /// Thresholds:
    ///   Critical = CPU > 25 % OR RAM > 1 GB
    ///   High     = CPU > 8 %  OR RAM > 512 MB
    ///   Medium   = CPU > 2 %  OR RAM > 200 MB
    /// </summary>
    private static ImpactLevel? Classify(ProcessInfo p) =>
        (p.CpuPercent > 25 || p.WorkingSetBytes > 1_073_741_824L) ? ImpactLevel.Critical :
        (p.CpuPercent > 8  || p.WorkingSetBytes > 536_870_912L)   ? ImpactLevel.High    :
        (p.CpuPercent > 2  || p.WorkingSetBytes > 209_715_200L)   ? ImpactLevel.Medium  :
        null;

    // ── Per-row commands (bound via CommandParameter="{Binding}") ─────────────

    /// <summary>Lower a process one step: set it to Below Normal priority.</summary>
    [RelayCommand]
    private async Task SetBelowNormal(RecommendationRow? row)
    {
        if (row is null) return;
        try
        {
            await _processProvider.SetPriorityAsync(row.Pid, ProcessPriority.BelowNormal, _cts.Token);
            LastAction = $"'{row.Name}' → Below Normal priority.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastAction = $"Priority change failed: {ex.Message}"; }
    }

    /// <summary>Set a process to Idle (lowest OS priority).</summary>
    [RelayCommand]
    private async Task SetIdle(RecommendationRow? row)
    {
        if (row is null) return;
        try
        {
            await _processProvider.SetPriorityAsync(row.Pid, ProcessPriority.Idle, _cts.Token);
            LastAction = $"'{row.Name}' → Idle priority.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastAction = $"Priority change failed: {ex.Message}"; }
    }

    /// <summary>Terminate a process immediately (no tree kill).</summary>
    [RelayCommand]
    private async Task TerminateProcess(RecommendationRow? row)
    {
        if (row is null) return;
        try
        {
            await _processProvider.KillProcessAsync(row.Pid, false, _cts.Token);
            LastAction = $"Terminated '{row.Name}'.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastAction = $"Terminate failed: {ex.Message}"; }
    }

    // ── Bulk / quick-action commands ──────────────────────────────────────────

    /// <summary>Set every currently-listed process to Below Normal priority at once.</summary>
    [RelayCommand]
    private async Task ThrottleAll()
    {
        LastAction = string.Empty;
        var rows  = Recommendations.ToList();
        int count = 0;
        foreach (var row in rows)
        {
            try
            {
                await _processProvider.SetPriorityAsync(row.Pid, ProcessPriority.BelowNormal, _cts.Token);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* process may have exited between snapshot and action */ }
        }
        LastAction = count > 0
            ? $"Throttled {count} process{(count == 1 ? "" : "es")} to Below Normal."
            : "Nothing to throttle.";
    }

    /// <summary>Reset ALL non-system processes back to Normal priority.</summary>
    [RelayCommand]
    private async Task NormalizePriorities()
    {
        LastAction = string.Empty;
        int count  = 0;
        try
        {
            var processes = await _processProvider.GetProcessesAsync(_cts.Token);
            foreach (var p in processes.Where(p => p.Category != ProcessCategory.SystemKernel))
            {
                try
                {
                    await _processProvider.SetPriorityAsync(p.Pid, ProcessPriority.Normal, _cts.Token);
                    count++;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* access denied or process exited — skip */ }
            }
            LastAction = $"Normalized priority for {count} processes.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastAction = $"Failed: {ex.Message}"; }
    }


    /// <summary>Trim working sets of all non-system processes to free up RAM.</summary>
    [RelayCommand]
    private async Task SmartTrimAll()
    {
        LastAction = string.Empty;
        int count = 0;
        long savedBytes = 0;
        try
        {
            var processes = await _processProvider.GetProcessesAsync(_cts.Token);
            foreach (var p in processes.Where(p => p.Category != ProcessCategory.SystemKernel))
            {
                try
                {
                    long before = p.WorkingSetBytes;
                    await _processProvider.TrimWorkingSetAsync(p.Pid, _cts.Token);
                    count++;
                    // Approximate savings (actual reduction varies)
                    savedBytes += Math.Max(0, before / 4);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* access denied or exited */ }
            }
            LastAction = count > 0
                ? $"SmartTrim: trimmed {count} processes, freed ~{ProcessRowViewModel.FormatBytes(savedBytes)}."
                : "No processes trimmed.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastAction = $"SmartTrim failed: {ex.Message}"; }
    }
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SyncCollection(
        ObservableCollection<RecommendationRow> target,
        List<RecommendationRow> source)
    {
        for (int i = target.Count - 1; i >= source.Count; i--) target.RemoveAt(i);
        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count) target[i] = source[i];
            else                  target.Add(source[i]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _subscription?.Dispose();
    }
}
