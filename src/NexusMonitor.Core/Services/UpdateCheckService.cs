using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.Services;

/// <summary>
/// Periodically polls GitHub's "latest release" endpoint and surfaces an
/// <see cref="UpdateInfo"/> when a newer version is published. Never downloads or installs
/// anything — this is a passive, read-only checker. All failures (offline, rate-limited,
/// malformed response) are swallowed and logged; the next scheduled cycle simply tries again.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/joshuadsutcliff/nexus-system-monitor/releases/latest";

    private static readonly Uri ReleasesUri = new(ReleasesUrl);

    // First check happens ~30s after Start() so it never competes with app startup I/O.
    private static readonly TimeSpan DefaultInitialDelay  = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly AppSettings                  _settings;
    private readonly ILogger<UpdateCheckService>  _logger;
    private readonly HttpClient                   _http;
    private readonly Func<DateTimeOffset>         _clock;
    private readonly TimeSpan                     _initialDelay;
    private readonly TimeSpan                     _checkInterval;
    private readonly string                       _runningVersion;

    // ── State ──────────────────────────────────────────────────────────────
    private readonly BehaviorSubject<UpdateInfo?> _updates = new(null);
    private Timer?  _timer;
    private volatile bool _running;
    private int _started; // 0 = not started, 1 = started — guarded by Interlocked

    /// <summary>
    /// Emits a non-null <see cref="UpdateInfo"/> whenever a check discovers a release newer
    /// than the running version. Stays at its initial <see langword="null"/> until that happens.
    /// </summary>
    public IObservable<UpdateInfo?> Updates => _updates.AsObservable();

    // ── Constructors ───────────────────────────────────────────────────────

    /// <summary>Production constructor — real HTTP client, real clock, default timing.</summary>
    public UpdateCheckService(AppSettings settings, ILogger<UpdateCheckService> logger)
        : this(settings, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
              clock: null, initialDelay: null, checkInterval: null)
    {
    }

    /// <summary>
    /// Test constructor — accepts a custom <see cref="HttpMessageHandler"/> plus optional
    /// clock/timing overrides so cycles can be driven deterministically.
    /// </summary>
    public UpdateCheckService(
        AppSettings                 settings,
        ILogger<UpdateCheckService> logger,
        HttpMessageHandler          handler,
        Func<DateTimeOffset>?       clock         = null,
        TimeSpan?                   initialDelay  = null,
        TimeSpan?                   checkInterval = null)
        : this(settings, logger, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) },
              clock, initialDelay, checkInterval)
    {
    }

    private UpdateCheckService(
        AppSettings                 settings,
        ILogger<UpdateCheckService> logger,
        HttpClient                  http,
        Func<DateTimeOffset>?       clock,
        TimeSpan?                   initialDelay,
        TimeSpan?                   checkInterval)
    {
        _settings       = settings;
        _logger         = logger;
        _http           = http;
        _clock          = clock ?? (() => DateTimeOffset.UtcNow);
        _initialDelay   = initialDelay ?? DefaultInitialDelay;
        _checkInterval  = checkInterval ?? DefaultCheckInterval;
        _runningVersion = ReadRunningVersion();

        // GitHub requires a User-Agent header on all REST API requests.
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd($"NexusMonitor/{_runningVersion}"))
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("NexusMonitor/0.0.0");
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        _running = true;
        // Fire once after the initial delay, then every _checkInterval; re-arm happens at
        // the end of RunCheckAsync so a slow/failed check never causes overlapping cycles.
        _timer = new Timer(_ => _ = RunCheckAsync(), null, _initialDelay, Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1) return;
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
        _updates.Dispose();
    }

    // ── Core logic ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a single check cycle. Made <c>internal</c> so tests can invoke it directly instead
    /// of waiting on the real timer. Never throws — every failure path is logged and swallowed.
    /// </summary>
    internal async Task RunCheckAsync()
    {
        try
        {
            if (!_settings.CheckForUpdates)
            {
                _logger.LogDebug("UpdateCheckService: update checks are disabled — skipping cycle");
                return;
            }

            using var request  = new HttpRequestMessage(HttpMethod.Get, ReleasesUri);
            using var response = await _http.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Covers offline/DNS failures surfaced as non-success, and GitHub's 403
                // rate-limit response — neither is worth more than an informational log.
                _logger.LogInformation(
                    "UpdateCheckService: GitHub releases API returned {StatusCode}", (int)response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            GitHubReleaseResponse? release;
            try
            {
                release = JsonSerializer.Deserialize<GitHubReleaseResponse>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "UpdateCheckService: failed to parse releases API response");
                return;
            }

            if (release is null)
            {
                _logger.LogDebug("UpdateCheckService: releases API response was empty");
                return;
            }

            var tag = release.TagName;
            if (string.IsNullOrWhiteSpace(tag))
            {
                _logger.LogDebug("UpdateCheckService: release response missing tag_name");
                return;
            }

            if (!UpdateVersionComparer.TryCompare(tag, _runningVersion, out var isNewer))
            {
                _logger.LogDebug("UpdateCheckService: could not compare tag {Tag} against running version {Running}",
                    tag, _runningVersion);
                return;
            }

            if (!isNewer)
            {
                _logger.LogDebug("UpdateCheckService: running version {Running} is up to date (latest {Tag})",
                    _runningVersion, tag);
                return;
            }

            var info = new UpdateInfo(
                Version:     UpdateVersionComparer.StripPrefix(tag),
                ReleaseUrl:  release.HtmlUrl ?? $"https://github.com/joshuadsutcliff/nexus-system-monitor/releases/tag/{tag}",
                PublishedAt: release.PublishedAt ?? _clock(),
                IsNewer:     true);

            _logger.LogInformation("UpdateCheckService: newer version available — {Version}", info.Version);
            _updates.OnNext(info);
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, timeout, TLS error, etc. — never surface as an error toast.
            _logger.LogDebug(ex, "UpdateCheckService: update check failed");
        }
        finally
        {
            // Re-arm for the next cycle (only when still running — Stop()/Dispose() may have
            // fired while this check was in flight).
            if (_running)
            {
                try { _timer?.Change(_checkInterval, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { /* service was disposed mid-tick */ }
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Reads the running version from the assembly's informational version, matching
    /// the "About" panel's version display (SettingsView.axaml.cs) so the two never disagree.</summary>
    private static string ReadRunningVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version?.ToString(3)
                          ?? "0.0.0";

            var metadataIndex = version.IndexOf('+');
            return metadataIndex >= 0 ? version[..metadataIndex] : version;
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>Minimal DTO for GitHub's "get latest release" response — only the fields we use.</summary>
    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
