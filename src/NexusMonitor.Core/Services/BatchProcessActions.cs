using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Services;

/// <summary>Per-item outcome of a batch process action.</summary>
public enum BatchItemStatus
{
    Success,

    /// <summary>
    /// The mutating call itself failed after the item passed the pre-flight liveness probe.
    /// <see cref="IProcessProvider"/> only exposes generic exceptions (no typed Win32/errno
    /// codes) so this bucket covers every such failure; the raw exception message is preserved
    /// in <see cref="BatchItemResult.Detail"/> for anyone who wants specifics. In practice this
    /// is overwhelmingly a permission failure (the OS refusing the privileged operation on a
    /// process that still existed a moment earlier).
    /// </summary>
    AccessDenied,

    /// <summary>
    /// The pre-flight liveness probe (<see cref="IProcessProvider.GetPriorityAsync"/>) returned
    /// null — the process has exited, become inaccessible, or reported an unrecognized value
    /// between being listed and the batch action running. The mutating call was never attempted.
    /// </summary>
    AlreadyExited,

    /// <summary>
    /// Skipped before any provider call because the process is protected
    /// (<see cref="ProcessCategory.SystemKernel"/> — mirrors <c>AutoBalanceService</c>'s
    /// throttle-candidate exclusion).
    /// </summary>
    ProtectedSkipped,
}

/// <summary>A single process targeted by a batch action.</summary>
public sealed record BatchTarget(int Pid, string Name, ProcessCategory Category);

/// <summary>The outcome of a batch action for one target process.</summary>
public sealed record BatchItemResult(int Pid, string Name, BatchItemStatus Status, string? Detail = null);

/// <summary>
/// Aggregate result of a batch process action — one honest summary instead of silently dropping
/// per-item failures. Never fabricates a blanket "succeeded"/"failed"; every item's outcome is
/// captured in <see cref="Items"/> in input order.
/// </summary>
public sealed class BatchActionResult
{
    public IReadOnlyList<BatchItemResult> Items { get; }

    public BatchActionResult(IReadOnlyList<BatchItemResult> items) => Items = items;

    public int TotalCount => Items.Count;
    public int SuccessCount => Items.Count(i => i.Status == BatchItemStatus.Success);
    public int FailureCount => TotalCount - SuccessCount;
    public bool AllSucceeded => TotalCount > 0 && FailureCount == 0;

    /// <summary>
    /// One-line, human-readable summary of the batch outcome, e.g.
    /// "Ended all 3 processes.", "Ended chrome.", or "Ended 3 of 5 — 2 failed: access denied."
    /// <paramref name="verbPastTense"/> is capitalized action wording for the sentence start
    /// (e.g. "Ended", "Updated priority for").
    /// </summary>
    public string Summarize(string verbPastTense)
    {
        if (TotalCount == 0)
            return "No processes selected.";

        if (TotalCount == 1)
            return SummarizeSingle(Items[0], verbPastTense);

        if (AllSucceeded)
            return $"{verbPastTense} all {TotalCount} processes.";

        var failureGroups = Items
            .Where(i => i.Status != BatchItemStatus.Success)
            .GroupBy(i => i.Status)
            .OrderByDescending(g => g.Count())
            .ToList();

        string reasons = failureGroups.Count == 1
            ? DescribeStatus(failureGroups[0].Key)
            : string.Join(", ", failureGroups.Select(g => $"{g.Count()} {DescribeStatus(g.Key)}"));

        return $"{verbPastTense} {SuccessCount} of {TotalCount} — {FailureCount} failed: {reasons}.";
    }

    // Failure phrasing deliberately avoids a "Could not <verb>" construction: verbPastTense
    // ("Ended", "Updated priority for") has no reliable base-form derivation (LowerFirst("Ended")
    // is "ended", not "end") without callers passing a second verb form. "{Name} — reason." reads
    // fine for every action and sidesteps the grammar problem entirely.
    private static string SummarizeSingle(BatchItemResult item, string verbPastTense) => item.Status switch
    {
        BatchItemStatus.Success => $"{verbPastTense} {item.Name}.",
        BatchItemStatus.AccessDenied => $"{item.Name} — access denied.",
        BatchItemStatus.AlreadyExited => $"{item.Name} had already exited.",
        BatchItemStatus.ProtectedSkipped => $"{item.Name} is a protected system process — skipped.",
        _ => $"{item.Name}: unknown outcome.",
    };

    private static string DescribeStatus(BatchItemStatus status) => status switch
    {
        BatchItemStatus.AccessDenied => "access denied",
        BatchItemStatus.AlreadyExited => "already exited",
        BatchItemStatus.ProtectedSkipped => "protected",
        _ => "failed",
    };
}

/// <summary>
/// Executes a process action (kill, set-priority, set-affinity) across a multi-selection,
/// capturing a per-item outcome so one process's failure never aborts the rest of the batch.
/// Thin ViewModel-facing wrapper around <see cref="IProcessProvider"/> — all iteration and
/// outcome-capture semantics live here so they are unit-testable without an Avalonia DataGrid.
/// </summary>
public sealed class BatchProcessActions
{
    private readonly IProcessProvider _provider;

    public BatchProcessActions(IProcessProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public Task<BatchActionResult> KillAsync(
        IReadOnlyList<BatchTarget> targets, bool killTree, CancellationToken ct = default) =>
        RunAsync(targets, (t, token) => _provider.KillProcessAsync(t.Pid, killTree, token), ct);

    public Task<BatchActionResult> SetPriorityAsync(
        IReadOnlyList<BatchTarget> targets, ProcessPriority priority, CancellationToken ct = default) =>
        RunAsync(targets, (t, token) => _provider.SetPriorityAsync(t.Pid, priority, token), ct);

    public Task<BatchActionResult> SetAffinityAsync(
        IReadOnlyList<BatchTarget> targets, long affinityMask, CancellationToken ct = default) =>
        RunAsync(targets, (t, token) => _provider.SetAffinityAsync(t.Pid, affinityMask, token), ct);

    /// <summary>
    /// Shared per-item pipeline: protected-category guard, then the "read real state or skip"
    /// liveness probe, then the mutating call — with per-item exception capture so a single
    /// failure never stops the remaining targets. Cancellation is the one exception that
    /// propagates rather than being captured as a per-item result: an explicit cancel means
    /// "stop the whole batch," not "this one item failed."
    /// </summary>
    private async Task<BatchActionResult> RunAsync(
        IReadOnlyList<BatchTarget> targets,
        Func<BatchTarget, CancellationToken, Task> action,
        CancellationToken ct)
    {
        var results = new List<BatchItemResult>(targets.Count);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (target.Category == ProcessCategory.SystemKernel)
            {
                results.Add(new BatchItemResult(target.Pid, target.Name, BatchItemStatus.ProtectedSkipped));
                continue;
            }

            // Read-real-priority-or-skip, reused as a liveness/accessibility probe: IProcessProvider
            // has no lighter "is this pid still valid" primitive, so GetPriorityAsync doubles as
            // the pre-flight check (mirrors AutoBalanceService/IdleThrottleService's original-value
            // read — never assume, skip when it can't be determined).
            var probe = await _provider.GetPriorityAsync(target.Pid, ct);
            if (probe is null)
            {
                results.Add(new BatchItemResult(target.Pid, target.Name, BatchItemStatus.AlreadyExited));
                continue;
            }

            try
            {
                await action(target, ct);
                results.Add(new BatchItemResult(target.Pid, target.Name, BatchItemStatus.Success));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new BatchItemResult(target.Pid, target.Name, BatchItemStatus.AccessDenied, ex.Message));
            }
        }

        return new BatchActionResult(results);
    }
}

/// <summary>
/// Pure text-formatting helpers for the batch-kill confirmation dialog — name-list truncation
/// and the confirmation prompt itself. Kept separate from <see cref="BatchProcessActions"/> so
/// the ViewModel can build the confirmation message before any provider call.
/// </summary>
public static class BatchConfirmationText
{
    /// <summary>
    /// Joins up to <paramref name="maxShown"/> names with ", "; beyond that, appends
    /// "and N more" instead of listing every name (used to keep the confirmation dialog short).
    /// </summary>
    public static string FormatNameList(IReadOnlyList<string> names, int maxShown = 8)
    {
        if (names.Count == 0) return string.Empty;
        if (names.Count <= maxShown) return string.Join(", ", names);

        int remaining = names.Count - maxShown;
        return $"{string.Join(", ", names.Take(maxShown))}, and {remaining} more";
    }

    /// <summary>
    /// Builds the single confirmation-dialog message for a batch kill (N &gt;= 2): process
    /// count + up to 8 names + "and X more". Callers only invoke this for N &gt;= 2 — the
    /// single-process kill flow keeps its existing no-dialog behavior unchanged.
    /// </summary>
    public static string BuildKillConfirmationMessage(IReadOnlyList<string> names) =>
        $"End {names.Count} processes?\n\n{FormatNameList(names)}";
}
