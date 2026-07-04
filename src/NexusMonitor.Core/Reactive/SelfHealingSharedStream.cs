using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace NexusMonitor.Core.Reactive;

/// <summary>
/// Wraps the "sample-timer-publish" pattern used by every real (Windows/Linux/macOS)
/// <c>ISystemMetricsProvider</c>/<c>IProcessProvider</c> implementation:
/// <c>Observable.Timer(TimeSpan.Zero, interval).Select(_ =&gt; Sample()).Publish()</c>,
/// connected once and shared across every subscriber so only one <c>Sample()</c> call
/// happens per tick regardless of subscriber count.
/// </summary>
/// <remarks>
/// The plain pattern above has a fatal flaw: <c>Select</c> turns any exception thrown by
/// <c>Sample()</c> into an <c>OnError</c>, which permanently terminates the shared
/// <c>Publish()</c> multicast — every current and future subscriber stops receiving ticks
/// for the lifetime of the provider (see <see cref="ObservableResilienceExtensions"/> for
/// the subscriber-side mitigation this class complements). This type fixes the fault at
/// the source: the timer+select chain is wrapped in
/// <see cref="ObservableResilienceExtensions.RetryWithBackoff{T}"/> *before* being
/// published, so a faulted <c>Sample()</c> call is logged and the timer is torn down and
/// recreated after a short backoff — all invisibly to the single upstream subscription
/// that <see cref="Stream"/>'s subscribers share. No subscriber ever observes an
/// <c>OnError</c> or needs to resubscribe.
/// </remarks>
public sealed class SelfHealingSharedStream<T> : IDisposable
{
    private readonly IConnectableObservable<T> _connectable;
    private readonly IDisposable _connection;
    private bool _disposed;

    /// <summary>
    /// Creates and immediately connects a self-healing shared sampling stream.
    /// </summary>
    /// <param name="sample">
    /// Synchronous sampler invoked once per tick. Exceptions are caught by the retry
    /// wrapper (via <c>Select</c>'s standard exception-to-OnError translation) — they
    /// never escape to the caller and never terminate <see cref="Stream"/>.
    /// </param>
    /// <param name="interval">Sampling period. The first sample fires immediately (t=0).</param>
    /// <param name="initialBackoff">Delay before the first resubscribe after a faulted sample.</param>
    /// <param name="maxBackoff">Upper bound on the backoff delay.</param>
    /// <param name="scheduler">Scheduler for the timer and backoff delays. Defaults to <see cref="Scheduler.Default"/>; inject a TestScheduler for deterministic tests.</param>
    /// <param name="onError">Optional callback invoked with each fault (e.g. to log a warning) before the stream recovers.</param>
    /// <param name="healthyResetPeriod">Forwarded to <see cref="ObservableResilienceExtensions.RetryWithBackoff{T}"/> — how long a run must stay healthy before the backoff resets to <paramref name="initialBackoff"/>.</param>
    public SelfHealingSharedStream(
        Func<T> sample,
        TimeSpan interval,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff,
        IScheduler? scheduler = null,
        Action<Exception>? onError = null,
        TimeSpan? healthyResetPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(sample);

        scheduler ??= Scheduler.Default;

        // Each retry-triggered resubscribe creates a brand-new Observable.Timer sequence
        // (Timer is cold — resubscribing restarts it at TimeSpan.Zero), so recovery ticks
        // almost immediately once the backoff elapses rather than waiting for the next
        // full `interval`. RetryWithBackoff shields this single upstream subscription from
        // ever surfacing OnError to Publish(), so the multicast — and therefore every
        // subscriber attached to it — never terminates.
        _connectable = Observable.Timer(TimeSpan.Zero, interval, scheduler)
            .Select(_ => sample())
            .RetryWithBackoff(initialBackoff, maxBackoff, scheduler, onError, healthyResetPeriod)
            .Publish();

        _connection = _connectable.Connect();
    }

    /// <summary>
    /// The durable, multicast, self-healing stream. Attach as many subscribers as needed —
    /// they all share the single underlying timer/sample subscription and none of them ever
    /// see a terminal notification because of a faulted <c>Sample()</c> call.
    /// </summary>
    public IObservable<T> Stream => _connectable;

    /// <summary>Tears down the underlying timer subscription permanently.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}
