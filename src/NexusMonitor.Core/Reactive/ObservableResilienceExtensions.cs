using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace NexusMonitor.Core.Reactive;

/// <summary>
/// Resilience operators for long-lived Rx pipelines. The provider streams in this
/// codebase are <c>Publish()</c> multicasts driven by <c>Observable.Timer(...).Select(Sample)</c>;
/// a single <c>Sample()</c> exception <c>OnError</c>-terminates the multicast for every
/// subscriber permanently. <see cref="RetryWithBackoff{T}"/> shields a subscriber from that
/// failure mode by resubscribing after an exponential, capped backoff instead of tearing
/// the subscription down.
/// </summary>
public static class ObservableResilienceExtensions
{
    /// <summary>
    /// Wraps <paramref name="source"/> so that an <c>OnError</c> is logged via
    /// <paramref name="onError"/> and then swallowed: the operator resubscribes to
    /// <paramref name="source"/> after a delay that grows exponentially from
    /// <paramref name="initialDelay"/> up to <paramref name="maxDelay"/>. If a subscription
    /// stays healthy for at least <paramref name="healthyResetPeriod"/> before faulting, the
    /// backoff resets to <paramref name="initialDelay"/>. <c>OnCompleted</c> is forwarded
    /// unchanged (a completing source is not retried).
    /// </summary>
    /// <param name="source">The upstream observable to make resilient.</param>
    /// <param name="initialDelay">Delay before the first resubscribe after a fault.</param>
    /// <param name="maxDelay">Upper bound on the backoff delay.</param>
    /// <param name="scheduler">Scheduler used for backoff timing. Defaults to <see cref="Scheduler.Default"/>. Inject a TestScheduler for deterministic tests.</param>
    /// <param name="onError">Optional callback invoked with each fault before resubscribing (e.g. to log a warning).</param>
    /// <param name="healthyResetPeriod">
    /// How long a subscription must survive before its next fault resets the backoff to
    /// <paramref name="initialDelay"/>. Defaults to twice <paramref name="maxDelay"/>.
    /// </param>
    public static IObservable<T> RetryWithBackoff<T>(
        this IObservable<T> source,
        TimeSpan initialDelay,
        TimeSpan maxDelay,
        IScheduler? scheduler = null,
        Action<Exception>? onError = null,
        TimeSpan? healthyResetPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (initialDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay));
        if (maxDelay < initialDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay));

        scheduler ??= Scheduler.Default;
        var resetPeriod = healthyResetPeriod ?? TimeSpan.FromTicks(maxDelay.Ticks * 2);

        return Observable.Create<T>(observer =>
        {
            // 'active' holds whichever disposable is currently live — either the source
            // subscription or the pending backoff-scheduled resubscribe. A per-attempt
            // SingleAssignmentDisposable isolates the source subscription so that a
            // *synchronous* OnError (as a faulted Publish() subject replays on resubscribe)
            // can reassign 'active' to the scheduled retry without the outer assignment
            // clobbering it.
            var active = new SerialDisposable();
            var disposed = false;
            var currentDelay = initialDelay;

            void Attempt()
            {
                if (disposed) return;

                var subscribedAt = scheduler.Now;
                var sourceSub = new SingleAssignmentDisposable();
                active.Disposable = sourceSub;

                sourceSub.Disposable = source.Subscribe(
                    observer.OnNext,
                    ex =>
                    {
                        onError?.Invoke(ex);

                        // Reset the backoff if the failed subscription stayed healthy long enough.
                        if (scheduler.Now - subscribedAt >= resetPeriod)
                            currentDelay = initialDelay;

                        var delay = currentDelay;

                        // Grow the delay for the next fault, capped at maxDelay.
                        var nextTicks = Math.Min(maxDelay.Ticks, currentDelay.Ticks * 2);
                        currentDelay = TimeSpan.FromTicks(Math.Max(nextTicks, initialDelay.Ticks));

                        var retrySchedule = new SingleAssignmentDisposable();
                        active.Disposable = retrySchedule; // supersedes (and disposes) the faulted source sub
                        retrySchedule.Disposable = scheduler.Schedule(delay, Attempt);
                    },
                    observer.OnCompleted); // a completing source is not retried
            }

            Attempt();
            return new CompositeDisposable(active, Disposable.Create(() => disposed = true));
        });
    }
}
