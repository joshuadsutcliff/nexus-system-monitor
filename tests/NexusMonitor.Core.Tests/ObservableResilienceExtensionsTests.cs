using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using NexusMonitor.Core.Reactive;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class ObservableResilienceExtensionsTests
{
    [Fact]
    public void RetryWithBackoff_ResubscribesOnError_WithGrowingDelays()
    {
        var scheduler = new TestScheduler();
        var subscribeTicks = new List<long>();
        var errors = new List<Exception>();

        // Cold source that records the virtual time of each subscription then immediately faults.
        var source = Observable.Create<int>(observer =>
        {
            subscribeTicks.Add(scheduler.Clock);
            observer.OnError(new InvalidOperationException("boom"));
            return Disposable.Empty;
        });

        using var sub = source
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), scheduler, errors.Add)
            .Subscribe(_ => { }, _ => { });

        // The initial subscription happens synchronously at t=0.
        subscribeTicks.Should().ContainSingle();
        subscribeTicks[0].Should().Be(0);

        // Exponential backoff schedule: 1s, then 2s, then 4s.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);   // → 2nd subscribe at t=1s
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);   // → 3rd subscribe at t=3s
        scheduler.AdvanceBy(TimeSpan.FromSeconds(4).Ticks);   // → 4th subscribe at t=7s

        subscribeTicks.Should().HaveCount(4);

        var deltas = new List<long>();
        for (int i = 1; i < subscribeTicks.Count; i++)
            deltas.Add(subscribeTicks[i] - subscribeTicks[i - 1]);

        deltas.Should().Equal(
            TimeSpan.FromSeconds(1).Ticks,
            TimeSpan.FromSeconds(2).Ticks,
            TimeSpan.FromSeconds(4).Ticks);

        // Every fault was surfaced to the onError callback.
        errors.Should().HaveCount(4);
    }

    [Fact]
    public void RetryWithBackoff_CapsDelayAtMaxDelay()
    {
        var scheduler = new TestScheduler();
        var subscribeTicks = new List<long>();

        var source = Observable.Create<int>(observer =>
        {
            subscribeTicks.Add(scheduler.Clock);
            observer.OnError(new InvalidOperationException("boom"));
            return Disposable.Empty;
        });

        // initial 1s, max 3s → delays: 1s, 2s, 3s(capped), 3s(capped)…
        using var sub = source
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), scheduler)
            .Subscribe(_ => { }, _ => { });

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks); // t=1s (delta 1s)
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks); // t=3s (delta 2s)
        scheduler.AdvanceBy(TimeSpan.FromSeconds(3).Ticks); // t=6s (delta 3s, capped)
        scheduler.AdvanceBy(TimeSpan.FromSeconds(3).Ticks); // t=9s (delta 3s, capped)

        subscribeTicks.Should().HaveCount(5);
        (subscribeTicks[3] - subscribeTicks[2]).Should().Be(TimeSpan.FromSeconds(3).Ticks);
        (subscribeTicks[4] - subscribeTicks[3]).Should().Be(TimeSpan.FromSeconds(3).Ticks);
    }

    [Fact]
    public void RetryWithBackoff_ForwardsValues_WhenSourceHealthy()
    {
        var scheduler = new TestScheduler();
        var received = new List<int>();

        using var sub = Observable.Return(42)
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), scheduler)
            .Subscribe(received.Add);

        // Return emits synchronously and completes — no retry on completion.
        received.Should().Equal(42);
    }

    [Fact]
    public void RetryWithBackoff_ResetsBackoff_AfterHealthyPeriod()
    {
        var scheduler = new TestScheduler();
        var subscribeTicks = new List<long>();

        // Each subscription survives 5s of virtual time before faulting.
        var source = Observable.Create<int>(observer =>
        {
            subscribeTicks.Add(scheduler.Clock);
            var timer = scheduler.Schedule(TimeSpan.FromSeconds(5),
                () => observer.OnError(new InvalidOperationException("boom")));
            return timer;
        });

        // healthyResetPeriod = 3s: since each attempt lives 5s (> 3s), the backoff resets
        // every time, so the resubscribe delay stays pinned at the initial 1s.
        using var sub = source
            .RetryWithBackoff(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), scheduler,
                healthyResetPeriod: TimeSpan.FromSeconds(3))
            .Subscribe(_ => { }, _ => { });

        // Attempt#1 at t=0 → fault at t=5 → resubscribe (+1s) at t=6
        // Attempt#2 at t=6 → fault at t=11 → resubscribe (+1s) at t=12
        scheduler.AdvanceBy(TimeSpan.FromSeconds(6).Ticks);  // reach t=6 → 2nd subscribe
        scheduler.AdvanceBy(TimeSpan.FromSeconds(6).Ticks);  // reach t=12 → 3rd subscribe

        subscribeTicks.Should().HaveCount(3);
        subscribeTicks[0].Should().Be(0);
        subscribeTicks[1].Should().Be(TimeSpan.FromSeconds(6).Ticks);
        subscribeTicks[2].Should().Be(TimeSpan.FromSeconds(12).Ticks);
    }
}
