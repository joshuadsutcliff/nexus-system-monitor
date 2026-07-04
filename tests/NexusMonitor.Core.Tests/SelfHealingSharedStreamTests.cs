using FluentAssertions;
using Microsoft.Reactive.Testing;
using NexusMonitor.Core.Reactive;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class SelfHealingSharedStreamTests
{
    [Fact]
    public void TicksContinue_AfterSampleFault_WithAtMostOneTickLost()
    {
        var scheduler = new TestScheduler();
        var errors    = new List<Exception>();
        var values    = new List<(long Time, int Value)>();
        int n = 0;

        int Sample()
        {
            n++;
            if (n == 3) throw new InvalidOperationException("transient sample failure");
            return n;
        }

        using var stream = new SelfHealingSharedStream<int>(
            Sample,
            interval: TimeSpan.FromSeconds(1),
            initialBackoff: TimeSpan.FromMilliseconds(100),
            maxBackoff: TimeSpan.FromSeconds(5),
            scheduler: scheduler,
            onError: errors.Add);

        using var sub = stream.Stream.Subscribe(v => values.Add((scheduler.Clock, v)));

        // Rx's TestScheduler ticks Observable.Timer(TimeSpan.Zero, ...) a hair (1 virtual
        // tick) after each nominal boundary, so bounds below use a +0.5s margin past the
        // nominal tick time they need to include rather than the exact boundary.
        // t≈0 → 1, t≈1s → 2, t≈2s → Sample() throws (fault #3, no value emitted),
        // backoff 100ms → new timer resubscribes at ≈2.1s and ticks immediately → 4,
        // then ≈3.1s → 5, ≈4.1s → 6.
        scheduler.AdvanceTo(TimeSpan.FromSeconds(4.6).Ticks);

        errors.Should().ContainSingle();
        errors[0].Should().BeOfType<InvalidOperationException>();

        // The faulted sample (n=3) never produced a value — exactly one tick lost.
        values.Select(v => v.Value).Should().Equal(1, 2, 4, 5, 6);

        // Recovery happened almost immediately after the short (100ms) backoff, not after
        // waiting out a full extra `interval` (1s): the gap between the last good tick
        // before the fault (value 2, at ≈1s) and the first recovered tick (value 4) is
        // close to backoff + interval (≈1.1s), nowhere near 2 full intervals (2s).
        var gapTicks = values[2].Time - values[1].Time;
        TimeSpan.FromTicks(gapTicks).Should().BeCloseTo(TimeSpan.FromSeconds(1.1), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void MultipleSubscribers_ShareSingleSampleCallPerTick()
    {
        var scheduler = new TestScheduler();
        int sampleCalls = 0;

        int Sample()
        {
            sampleCalls++;
            return sampleCalls;
        }

        using var stream = new SelfHealingSharedStream<int>(
            Sample,
            interval: TimeSpan.FromSeconds(1),
            initialBackoff: TimeSpan.FromMilliseconds(100),
            maxBackoff: TimeSpan.FromSeconds(5),
            scheduler: scheduler);

        var valuesA = new List<int>();
        var valuesB = new List<int>();
        using var subA = stream.Stream.Subscribe(valuesA.Add);
        using var subB = stream.Stream.Subscribe(valuesB.Add);

        scheduler.AdvanceTo(TimeSpan.FromSeconds(3.5).Ticks);

        // 4 ticks (t≈0,1,2,3) — Sample() called once per tick, not once per subscriber.
        sampleCalls.Should().Be(4);
        valuesA.Should().Equal(1, 2, 3, 4);
        valuesB.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Dispose_StopsSampling()
    {
        var scheduler = new TestScheduler();
        int sampleCalls = 0;

        var stream = new SelfHealingSharedStream<int>(
            () => ++sampleCalls,
            interval: TimeSpan.FromSeconds(1),
            initialBackoff: TimeSpan.FromMilliseconds(100),
            maxBackoff: TimeSpan.FromSeconds(5),
            scheduler: scheduler);

        scheduler.AdvanceTo(TimeSpan.FromSeconds(2.5).Ticks);
        sampleCalls.Should().Be(3); // t≈0,1,2

        stream.Dispose();

        scheduler.AdvanceTo(TimeSpan.FromSeconds(5.5).Ticks);
        sampleCalls.Should().Be(3); // no further sampling after Dispose
    }

    [Fact]
    public void RecoversRepeatedly_AcrossMultipleFaults()
    {
        var scheduler = new TestScheduler();
        var errors = new List<Exception>();
        var values = new List<int>();
        int n = 0;

        int Sample()
        {
            n++;
            // Fault on every 3rd sample.
            if (n % 3 == 0) throw new InvalidOperationException($"boom #{n}");
            return n;
        }

        using var stream = new SelfHealingSharedStream<int>(
            Sample,
            interval: TimeSpan.FromSeconds(1),
            initialBackoff: TimeSpan.FromMilliseconds(50),
            maxBackoff: TimeSpan.FromSeconds(5),
            scheduler: scheduler,
            onError: errors.Add);

        using var sub = stream.Stream.Subscribe(values.Add);

        scheduler.AdvanceTo(TimeSpan.FromSeconds(10).Ticks);

        errors.Count.Should().BeGreaterThanOrEqualTo(2);
        // None of the faulted samples (multiples of 3) ever produced a value.
        values.Should().NotContain(v => v % 3 == 0);
        // The stream kept producing values well past the first fault.
        values.Count.Should().BeGreaterThanOrEqualTo(6);
    }
}
