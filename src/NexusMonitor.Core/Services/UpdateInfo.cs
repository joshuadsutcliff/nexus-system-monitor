namespace NexusMonitor.Core.Services;

/// <summary>
/// Describes a release discovered by <see cref="UpdateCheckService"/> while polling
/// GitHub's "latest release" endpoint.
/// </summary>
/// <param name="Version">The release version with any leading "v" stripped, e.g. "0.6.0".</param>
/// <param name="ReleaseUrl">The GitHub release page URL (opens in the browser).</param>
/// <param name="PublishedAt">When the release was published, per GitHub.</param>
/// <param name="IsNewer">True when <paramref name="Version"/> is newer than the running version.</param>
public sealed record UpdateInfo(
    string         Version,
    string         ReleaseUrl,
    DateTimeOffset PublishedAt,
    bool           IsNewer);
