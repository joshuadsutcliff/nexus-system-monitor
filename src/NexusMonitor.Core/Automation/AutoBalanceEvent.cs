using NexusMonitor.Core.Abstractions;

namespace NexusMonitor.Core.Automation;

public enum AutoBalanceEventType { Throttled, Restored, Started, Stopped }

public record AutoBalanceEvent(
    AutoBalanceEventType Type,
    int Pid,
    string ProcessName,
    ProcessPriority OriginalPriority,
    ProcessPriority NewPriority,
    DateTime Timestamp)
{
    public string Description => Type switch
    {
        AutoBalanceEventType.Throttled => $"Lowered '{ProcessName}' ({Pid}): {OriginalPriority} \u2192 {NewPriority}",
        AutoBalanceEventType.Restored  => $"Restored '{ProcessName}' ({Pid}): {NewPriority}",
        AutoBalanceEventType.Started   => "Auto-Balance activated",
        AutoBalanceEventType.Stopped   => "Auto-Balance deactivated",
        _ => string.Empty
    };

    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string TypeIcon => Type switch
    {
        AutoBalanceEventType.Throttled => "\u2b07",
        AutoBalanceEventType.Restored  => "\u2b06",
        AutoBalanceEventType.Started   => "\u25b6",
        AutoBalanceEventType.Stopped   => "\u23f9",
        _ => "\u2022"
    };

    public string TypeColor => Type switch
    {
        AutoBalanceEventType.Throttled => "#FF9F0A",
        AutoBalanceEventType.Restored  => "#30D158",
        AutoBalanceEventType.Started   => "#0A84FF",
        AutoBalanceEventType.Stopped   => "#636366",
        _ => "#EBEBF5"
    };
}
