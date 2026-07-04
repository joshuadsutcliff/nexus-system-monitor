using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Services;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class UpdateCheckServiceTests
{
    // ── Fake HTTP handlers ─────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public HttpStatusCode           StatusCode { get; set; } = HttpStatusCode.OK;
        public string                   Body       { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated network failure");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string ReleaseJson(
        string tag,
        string url         = "https://github.com/joshuadsutcliff/nexus-system-monitor/releases/tag/vX",
        string publishedAt = "2026-01-01T00:00:00Z") =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"" + url + "\",\"published_at\":\"" + publishedAt + "\"}";

    private static UpdateCheckService Create(AppSettings settings, HttpMessageHandler handler) =>
        new(settings, NullLogger<UpdateCheckService>.Instance, handler);

    // ── Emits UpdateInfo on newer tag ────────────────────────────────────────

    [Fact]
    public async Task RunCheckAsync_EmitsUpdateInfo_WhenNewerTagFound()
    {
        var handler  = new FakeHandler { Body = ReleaseJson("v99.0.0") };
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        UpdateInfo? received = null;
        using var sub = svc.Updates.Subscribe(u => received = u);

        await svc.RunCheckAsync();

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Headers.UserAgent.ToString().Should().Contain("NexusMonitor");

        received.Should().NotBeNull();
        received!.Version.Should().Be("99.0.0");
        received.IsNewer.Should().BeTrue();
        received.ReleaseUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunCheckAsync_StripsVPrefix_FromEmittedVersion()
    {
        var handler  = new FakeHandler { Body = ReleaseJson("V42.0.0") };
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        UpdateInfo? received = null;
        using var sub = svc.Updates.Subscribe(u => received = u);

        await svc.RunCheckAsync();

        received.Should().NotBeNull();
        received!.Version.Should().Be("42.0.0");
    }

    // ── Silent when not newer / disabled / malformed ─────────────────────────

    [Fact]
    public async Task RunCheckAsync_DoesNotEmit_WhenTagIsNotNewer()
    {
        var handler  = new FakeHandler { Body = ReleaseJson("v0.0.1") };
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        UpdateInfo? received = null;
        using var sub = svc.Updates.Subscribe(u => received = u);

        await svc.RunCheckAsync();

        handler.Requests.Should().HaveCount(1); // still checks — just finds nothing newer
        received.Should().BeNull();
    }

    [Fact]
    public async Task RunCheckAsync_DoesNotCallApi_WhenCheckForUpdatesDisabled()
    {
        var handler  = new FakeHandler { Body = ReleaseJson("v999.0.0") };
        var settings = new AppSettings { CheckForUpdates = false };
        using var svc = Create(settings, handler);

        UpdateInfo? received = null;
        using var sub = svc.Updates.Subscribe(u => received = u);

        await svc.RunCheckAsync();

        handler.Requests.Should().BeEmpty();
        received.Should().BeNull();
    }

    [Fact]
    public async Task RunCheckAsync_DoesNotEmit_WhenTagNameMissing()
    {
        var handler  = new FakeHandler { Body = "{\"html_url\":\"https://example.com\"}" };
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        UpdateInfo? received = null;
        using var sub = svc.Updates.Subscribe(u => received = u);

        await svc.RunCheckAsync();

        received.Should().BeNull();
    }

    // ── Never throws ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCheckAsync_DoesNotThrow_On403RateLimit()
    {
        var handler = new FakeHandler
        {
            StatusCode = HttpStatusCode.Forbidden,
            Body       = "{\"message\":\"API rate limit exceeded\"}",
        };
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        Func<Task> act = () => svc.RunCheckAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunCheckAsync_DoesNotThrow_OnMalformedJson()
    {
        var handler  = new FakeHandler { Body = "{not valid json at all" };
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        Func<Task> act = () => svc.RunCheckAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunCheckAsync_DoesNotThrow_OnNetworkFailure()
    {
        var handler  = new ThrowingHandler();
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        Func<Task> act = () => svc.RunCheckAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunCheckAsync_DoesNotEmit_OnNetworkFailure()
    {
        var handler  = new ThrowingHandler();
        var settings = new AppSettings { CheckForUpdates = true };
        using var svc = Create(settings, handler);

        UpdateInfo? received = null;
        using var sub = svc.Updates.Subscribe(u => received = u);

        await svc.RunCheckAsync();

        received.Should().BeNull();
    }
}
