using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Storage;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class PredictionServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static readonly DateTime FixedNow = new DateTime(2026, 4, 12, 12, 0, 0, DateTimeKind.Utc);

    private (PredictionService svc, Mock<IMetricsReader> reader) CreateService(
        AppSettings?             settings    = null,
        QuietHoursService?       quietHours  = null,
        Func<DateTimeOffset>?    clock       = null)
    {
        var reader = new Mock<IMetricsReader>();
        var svc = new PredictionService(
            reader.Object,
            settings ?? new AppSettings { PredictionsEnabled = true },
            NullLogger<PredictionService>.Instance,
            quietHours,
            clock ?? (() => new DateTimeOffset(FixedNow)));
        return (svc, reader);
    }

    /// <summary>
    /// Creates a <see cref="HealthDataPoint"/> with controllable disk and memory scores.
    /// </summary>
    private static HealthDataPoint MakePoint(
        DateTimeOffset ts,
        double overall  = 80,
        double disk     = 80,
        double memory   = 80)
        => new HealthDataPoint(ts, overall, 80, memory, disk, 80, null);

    /// <summary>
    /// Generates <paramref name="count"/> evenly-spaced health data points whose
    /// disk and/or memory scores change linearly from <paramref name="startScore"/>
    /// to <paramref name="endScore"/> over the window.
    /// </summary>
    /// <param name="spacingSeconds">
    /// Seconds between successive data points. Defaults to 30 (the normal collection interval).
    /// Use larger values (e.g. 3600) to simulate a gradual trend recorded over many hours.
    /// </param>
    private static IReadOnlyList<HealthDataPoint> MakeLinearPoints(
        int    count,
        double startScore,
        double endScore,
        bool   applyToDisk    = true,
        bool   applyToMemory  = false,
        double spacingSeconds = 30.0)
    {
        var now    = new DateTimeOffset(FixedNow);
        var points = new List<HealthDataPoint>(count);
        for (int i = 0; i < count; i++)
        {
            double t     = count == 1 ? 0 : (double)i / (count - 1);
            double score = startScore + t * (endScore - startScore);
            var ts       = now.AddSeconds(-spacingSeconds * (count - 1 - i)); // oldest first
            double disk  = applyToDisk   ? score : 80;
            double mem   = applyToMemory ? score : 80;
            points.Add(MakePoint(ts, 80, disk, mem));
        }
        return points;
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunPredictions_ReturnsEmpty_WhenInsufficientData()
    {
        var (svc, reader) = CreateService();

        // Only 5 data points — below the 10-point minimum
        var sparse = Enumerable.Range(0, 5)
            .Select(i => MakePoint(DateTimeOffset.UtcNow.AddMinutes(-i)))
            .ToList();
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(sparse);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        emitted.Should().NotBeNull();
        emitted!.Should().BeEmpty("fewer than 10 data points should produce no predictions");

        svc.Dispose();
    }

    [Fact]
    public async Task RunPredictions_ReturnsEmpty_WhenDataIsFlat()
    {
        var (svc, reader) = CreateService();

        // 30 points all with disk = 80, memory = 80 — flat trend, slope ≈ 0
        var flat = MakeLinearPoints(30, 80, 80, applyToDisk: true, applyToMemory: true);
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(flat);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        emitted.Should().NotBeNull();
        emitted!.Should().BeEmpty("flat data has slope ≈ 0 — no prediction warranted");

        svc.Dispose();
    }

    [Fact]
    public async Task RunPredictions_ReturnsDiskWarning_WhenDiskTrendingDown()
    {
        var (svc, reader) = CreateService();

        // 30 points at 1-hour spacing, disk declining from 90 → 60.
        // slope = (60-90)/29 ≈ -1.034 per sample; samplesPerHour = 3600/3600 = 1.
        // hoursToZero ≈ 60 / (1.034 * 1) ≈ 58h → Warning (24h < 58h < 168h). R² ≈ 1.
        // Memory stays flat at 80 so only disk should trigger.
        var points = MakeLinearPoints(30, 90, 60, applyToDisk: true, applyToMemory: false,
            spacingSeconds: 3600);
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(points);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        emitted.Should().NotBeNull();
        emitted!.Should().ContainSingle(p => p.Resource == "Disk" && p.Severity == RecommendationSeverity.Warning,
            "a steadily declining disk health score should produce a Warning");
        emitted!.Should().NotContain(p => p.Resource == "Memory",
            "flat memory should not produce a prediction");

        var diskPred = emitted!.Single(p => p.Resource == "Disk");
        diskPred.Confidence.Should().BeGreaterThan(0.9, "a perfectly linear trend should have R² close to 1");
        diskPred.DepletionEstimate.Should().NotBeNull();
        diskPred.DepletionEstimate!.Value.Should().BeAfter(new DateTimeOffset(FixedNow),
            "depletion should be in the future relative to the service's (fixed) evaluation time");

        svc.Dispose();
    }

    [Fact]
    public async Task RunPredictions_ReturnsCritical_WhenDepletionWithin24h()
    {
        var (svc, reader) = CreateService();

        // Disk drops very steeply: 90 → 1 over 30 samples (≈ 15 minutes of 30s samples).
        // Extrapolated to zero: very soon → Critical.
        var points = MakeLinearPoints(30, 90, 1, applyToDisk: true);
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(points);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        emitted.Should().NotBeNull();
        emitted!.Should().ContainSingle(p => p.Resource == "Disk" && p.Severity == RecommendationSeverity.Critical,
            "a steep decline reaching near-zero quickly should be Critical");

        svc.Dispose();
    }

    [Fact]
    public async Task RunPredictions_ReturnsEmpty_WhenRSquaredTooLow()
    {
        var (svc, reader) = CreateService();

        // Alternating 80 / 40 pattern produces a very low R² (noisy data).
        var now = new DateTimeOffset(FixedNow);
        var noisy = Enumerable.Range(0, 30)
            .Select(i =>
            {
                double score = (i % 2 == 0) ? 80 : 40;
                var ts       = now.AddSeconds(-30.0 * (29 - i));
                return MakePoint(ts, 80, score, score);
            })
            .ToList();
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(noisy);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        emitted.Should().NotBeNull();
        emitted!.Should().BeEmpty("highly oscillating data should have R² < 0.5 and be suppressed");

        svc.Dispose();
    }

    [Fact]
    public async Task RunPredictions_StillEmitsPredictions_DuringQuietHours()
    {
        // QuietHoursService with always-active settings
        var qhSettings = new AppSettings
        {
            QuietHoursEnabled = true,
            QuietHoursStart   = "00:00",
            QuietHoursEnd     = "23:59",
        };
        var quietHours = new QuietHoursService(qhSettings, () => FixedNow.ToLocalTime());
        // IsActive should be true because current time falls within 00:00–23:59
        quietHours.IsActive.Should().BeTrue("quiet hours span the full day");

        var (svc, reader) = CreateService(quietHours: quietHours);

        // Declining disk data that should produce a Warning (same parameters as the Warning test)
        var points = MakeLinearPoints(30, 90, 60, applyToDisk: true, spacingSeconds: 3600);
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(points);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        // Predictions should still be emitted even though Quiet Hours is active
        emitted.Should().NotBeNull();
        emitted!.Should().ContainSingle(p => p.Resource == "Disk",
            "predictions must still be emitted during Quiet Hours (only alert firing is suppressed)");

        svc.Dispose();
        quietHours.Dispose();
    }

    [Fact]
    public async Task RunPredictions_ReturnsEmpty_WhenPredictionsDisabled()
    {
        var settings = new AppSettings { PredictionsEnabled = false };
        var (svc, reader) = CreateService(settings: settings);

        IReadOnlyList<ResourcePrediction>? emitted = null;
        svc.Predictions.Subscribe(p => emitted = p);

        await svc.RunPredictionsAsync();

        emitted.Should().NotBeNull();
        emitted!.Should().BeEmpty("disabled predictions should short-circuit and emit an empty list");

        // The DB should never have been queried when PredictionsEnabled = false
        reader.Verify(
            r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "GetHealthHistoryAsync must not be called when PredictionsEnabled is false");

        svc.Dispose();
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var (svc, reader) = CreateService();
        reader.Setup(r => r.GetHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<HealthDataPoint>());

        // Calling Start() twice should not throw and should not double-up the timer
        var act = () =>
        {
            svc.Start();
            svc.Start();
        };
        act.Should().NotThrow();

        svc.Dispose();
    }
}
