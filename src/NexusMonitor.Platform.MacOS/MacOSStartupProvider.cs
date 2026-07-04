using Microsoft.Extensions.Logging;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Enumerates macOS Login Items by scanning LaunchAgent plist directories.
/// Full login item management requires the ServiceManagement framework.
/// </summary>
public sealed class MacOSStartupProvider : IStartupProvider
{
    private readonly ILogger<MacOSStartupProvider> _logger;

    public MacOSStartupProvider(ILogger<MacOSStartupProvider> logger)
    {
        _logger = logger;
    }

    private static readonly string[] s_launchAgentDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Library", "LaunchAgents"),
        "/Library/LaunchAgents",
        "/Library/LaunchDaemons",
        "/System/Library/LaunchAgents",
        "/System/Library/LaunchDaemons",
    ];

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<StartupItem>>(Enumerate, ct);

    private static IReadOnlyList<StartupItem> Enumerate()
    {
        var result = new List<StartupItem>();
        foreach (var dir in s_launchAgentDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.plist"))
                {
                    try
                    {
                        var name    = Path.GetFileNameWithoutExtension(file);
                        var content = File.ReadAllText(file);
                        var program = ExtractPlistString(content, "ProgramArguments")
                                   ?? ExtractPlistString(content, "Program")
                                   ?? string.Empty;
                        var disabled = content.Contains("<key>Disabled</key>", StringComparison.Ordinal)
                                    && content.Contains("<true/>", StringComparison.Ordinal);

                        result.Add(new StartupItem
                        {
                            Name      = name,
                            Command   = program,
                            Publisher = string.Empty,
                            Location  = file,
                            IsEnabled = !disabled,
                            ItemType  = StartupItemType.StartupFolder,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        return result;
    }

    public Task SetEnabledAsync(StartupItem item, bool enabled, CancellationToken ct = default)
    {
        // Modifying system plist files requires elevated permissions — this is a documented
        // no-op, not a bug. IPlatformCapabilities.SupportsStartupToggle is false on macOS so
        // the UI hides the Enable/Disable controls; this warning covers any caller that
        // invokes SetEnabledAsync directly without going through the gated UI.
        _logger.LogWarning(
            "SetEnabledAsync({ItemName}, {Enabled}) was called on macOS, but toggling startup " +
            "items is not supported here (requires elevated permissions to modify system " +
            "LaunchAgent/LaunchDaemon plists). No change was made.",
            item.Name, enabled);
        return Task.CompletedTask;
    }

    // Minimal plist string extraction — avoids a full XML parser dependency
    private static string? ExtractPlistString(string content, string key)
    {
        var keyTag = $"<key>{key}</key>";
        var idx    = content.IndexOf(keyTag, StringComparison.Ordinal);
        if (idx < 0) return null;

        var after = content[(idx + keyTag.Length)..].TrimStart();

        if (after.StartsWith("<string>", StringComparison.Ordinal))
        {
            var end = after.IndexOf("</string>", StringComparison.Ordinal);
            return end >= 0 ? after[8..end] : null;
        }

        // ProgramArguments is an <array> of <string> — grab first element
        if (after.StartsWith("<array>", StringComparison.Ordinal))
        {
            var strStart = after.IndexOf("<string>", StringComparison.Ordinal);
            if (strStart < 0) return null;
            var strEnd = after.IndexOf("</string>", strStart, StringComparison.Ordinal);
            return strEnd >= 0 ? after[(strStart + 8)..strEnd] : null;
        }

        return null;
    }
}
