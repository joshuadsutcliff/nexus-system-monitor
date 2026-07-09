namespace NexusMonitor.Core.Models;

/// <summary>Describes a single open handle belonging to a process.</summary>
public record HandleInfo(
    ulong  HandleValue,
    string TypeName,
    string ObjectName,
    string AccessDisplay)    // e.g. "0x001F0001"
{
    /// <summary>Display string for the handle value in hex.</summary>
    public string HandleHex => $"0x{HandleValue:X}";
}

/// <summary>Describes one virtual-memory region in a process's address space.</summary>
public record MemoryRegionInfo(
    ulong  BaseAddress,
    ulong  RegionSize,
    string State,             // "Committed" | "Reserved"
    string RegionType,        // "Private" | "Mapped" | "Image"
    string Protection,        // "RW" | "RX" | "R" | "RWX" | "No Access" | "Guard"
    string Description)       // Windows: image path for Image regions only, "" otherwise.
                               // Linux: backing file path for Image/Mapped regions, or a bracket
                               // tag ([heap]/[stack]/[vdso]/...) for Private regions, "" when
                               // nothing is known (anonymous mapping).
{
    /// <summary>Base address formatted as hex string.</summary>
    public string BaseAddressHex => $"0x{BaseAddress:X12}";

    /// <summary>Region size formatted as KB/MB.</summary>
    public string RegionSizeDisplay
    {
        get
        {
            if (RegionSize >= 1024 * 1024)
                return $"{RegionSize / (1024.0 * 1024.0):F1} MB";
            if (RegionSize >= 1024)
                return $"{RegionSize / 1024.0:F0} KB";
            return $"{RegionSize} B";
        }
    }
}
