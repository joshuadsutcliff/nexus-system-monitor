namespace NexusMonitor.Core.Formatting;

/// <summary>
/// Pure "used / total" GPU-memory display-string formatting backing
/// <c>NexusMonitor.UI.ViewModels.GpuDeviceViewModel.SubValueDisplay</c>/<c>DedicatedDisplay</c> and
/// <c>NexusMonitor.UI.ViewModels.PerformanceViewModel.GpuMemDisplay</c> (Sym-2 Task 6's GPU-memory
/// work plus its zero-total display follow-up fix). Lives in Core (with these tests) because the
/// UI assembly has no test project of its own — the same "Core-adjacent logic in a UI assembly"
/// carve-out <c>NexusMonitor.Core.Backdrop.BackdropMath</c>/<c>Motion.MotionMath</c>/
/// <c>Typography.TypeScaleMath</c> already document.
///
/// An honestly-unknown total (0 GB — e.g. Apple Silicon unified memory has no dedicated VRAM pool
/// to report a total for; see <c>GpuMetrics.DedicatedMemoryTotalBytes</c>) must not render
/// "used / 0 GB" — that reads as a fabricated zero-capacity pool. A real dedicated pool
/// (Windows/Linux/Intel Mac) keeps the original "used / total" format unchanged.
/// </summary>
public static class GpuMemoryDisplayMath
{
    /// <summary>
    /// Formats a GPU-memory display string. When <paramref name="totalGb"/> is greater than 0,
    /// renders <c>"{usedGb:F1} / {totalGb:F0} GB{totalSuffix}"</c> (the real-total branch);
    /// when it is exactly 0 (honestly unknown), renders <c>"{usedGb:F1} GB{zeroTotalSuffix}"</c>
    /// (the used-only branch) instead of a misleading "used / 0 GB".
    /// </summary>
    /// <param name="usedGb">GPU memory currently in use, in GB.</param>
    /// <param name="totalGb">GPU memory total capacity, in GB, or 0 if honestly unknown.</param>
    /// <param name="totalSuffix">Optional label appended after "GB" in the real-total branch
    /// (e.g. <c>" VRAM"</c> for <c>SubValueDisplay</c>). Empty for callers that show a bare
    /// "used / total GB" with no label.</param>
    /// <param name="zeroTotalSuffix">Optional label appended after "GB" in the used-only branch
    /// (e.g. <c>" GPU memory"</c> for <c>SubValueDisplay</c>). Empty for callers that show a bare
    /// "used GB" with no label.</param>
    public static string FormatUsedTotal(
        double usedGb, double totalGb, string totalSuffix = "", string zeroTotalSuffix = "") =>
        totalGb > 0
            ? $"{usedGb:F1} / {totalGb:F0} GB{totalSuffix}"
            : $"{usedGb:F1} GB{zeroTotalSuffix}";
}
