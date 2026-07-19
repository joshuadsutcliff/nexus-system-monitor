namespace NexusMonitor.Core.Formatting;

/// <summary>
/// Approved user-facing tooltip copy explaining WHY an honest "—"/"N/A" placeholder is showing,
/// for the unavailable-metric-tooltips feature. <see cref="Generic"/> covers every placeholder
/// site that isn't one of the three locally-identifiable flagship cases below; those three get
/// reason-specific wording because the owning ViewModel already has enough local context (current
/// OS, architecture, and/or the specific field) to say more than "not available."
///
/// This wording is approved-final — do not reword. If a legacy product name ever needs to appear
/// in code near this class, it belongs only in the <c>SettingsService</c> pre-rename migration
/// shim (ProBalance/IdleSaver/SmartTrim → Auto-Balance/Idle Throttle/Memory Reclaim), never here.
/// </summary>
public static class UnavailableMetricCopy
{
    /// <summary>Catch-all copy for every unavailable-metric placeholder that isn't one of the
    /// three reason-specific cases below.</summary>
    public const string Generic =
        "Not available on this system — Nexus shows nothing rather than estimate.";

    /// <summary>GPU temperature on macOS, Apple Silicon, at idle — the sensor sometimes reports a
    /// real value under load, so Nexus doesn't fabricate one when idle.</summary>
    public const string GpuTempAppleSiliconIdle =
        "GPU temperature isn't reliably readable at idle on this Apple Silicon model — Nexus shows a value only when it's real.";

    /// <summary>CPU temperature unsupported on the current hardware/platform (no sensor exposed
    /// a real reading).</summary>
    public const string CpuTempUnsupported =
        "CPU temperature isn't accessible on this hardware — Nexus shows nothing rather than estimate.";

    /// <summary>GPU total memory unavailable on macOS (e.g. Apple Silicon unified memory has no
    /// dedicated VRAM pool to report a total for).</summary>
    public const string GpuMemoryTotalMacOS =
        "This GPU doesn't report a total memory figure — Nexus shows only what's actually measured.";
}
