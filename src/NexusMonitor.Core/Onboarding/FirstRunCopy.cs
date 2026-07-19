namespace NexusMonitor.Core.Onboarding;

/// <summary>
/// Copy for the one-time first-run welcome overlay (a single glass-styled card shown centered
/// over the dashboard on first launch only — not a wizard, no steps, no coach marks). Design
/// conductor-approved draft, 2026-07-19.
///
/// Kept as plain constants (not a settings-driven or localizable resource) — the same shape as
/// this codebase uses elsewhere for one-off, hand-tuned UI copy. Pure C#, no Avalonia
/// dependency, so it's testable from NexusMonitor.Core.Tests without spinning up any UI.
/// </summary>
public static class FirstRunCopy
{
    public const string Title = "Welcome to Nexus";

    public const string Row1 =
        "This is your dashboard — a starting layout with the essentials: health, usage, and top consumers.";

    public const string Row2 =
        "Everything is rearrangeable — open Edit mode to drag, resize, add, or remove widgets, and save layouts as profiles.";

    public const string Row3 =
        "Nexus only shows data it can actually read — when a sensor isn't available on your hardware, you'll see \"—\" rather than an estimate.";

    public const string Row4 =
        "The sidebar covers the rest — processes, automation, disks, network, and more.";

    public const string ButtonLabel = "Get started";
}
