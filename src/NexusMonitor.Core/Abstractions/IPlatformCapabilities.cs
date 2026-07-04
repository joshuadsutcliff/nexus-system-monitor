namespace NexusMonitor.Core.Abstractions;

/// <summary>
/// Describes which features are available on the current platform.
/// Injected into ViewModels to gate UI elements at runtime.
/// </summary>
public interface IPlatformCapabilities
{
    bool SupportsCpuAffinity  { get; }
    bool SupportsTrimMemory   { get; }
    bool SupportsCreateDump   { get; }
    bool SupportsFindWindow   { get; }
    bool SupportsIoPriority   { get; }
    bool SupportsMemoryPriority { get; }
    /// <summary>True on macOS where the Meta (Cmd) key is the primary modifier.</summary>
    bool UsesMetaKey          { get; }
    /// <summary>Platform name for the file manager (e.g. "Explorer", "Finder", "Files").</summary>
    string FileManagerName    { get; }
    /// <summary>Platform name for the service manager (e.g. "Services", "Launch Daemons", "systemd").</summary>
    string ServiceManagerName { get; }
    /// <summary>True if the Services tab's startup-type submenu is supported on this platform.</summary>
    bool SupportsServiceStartupType { get; }
    /// <summary>True on Windows where the registry editor and registry-key startup entries exist.</summary>
    bool SupportsRegistry { get; }
    /// <summary>True on Windows 11+ where EcoQoS (Efficiency Mode) is available.</summary>
    bool SupportsEfficiencyMode { get; }
    /// <summary>True on Windows where handle enumeration via NtQuerySystemInformation is available.</summary>
    bool SupportsHandles { get; }
    /// <summary>True on Windows where VirtualQueryEx memory map is available.</summary>
    bool SupportsMemoryMap { get; }
    /// <summary>True on platforms where power plans are switchable (Windows and Linux; not macOS).</summary>
    bool SupportsPowerPlan { get; }
    /// <summary>Platform-correct label for the "Open file location" context menu item.</summary>
    string OpenLocationMenuLabel { get; }
    /// <summary>True on Windows where DirectX version information is meaningful.</summary>
    bool SupportsDirectX { get; }
    /// <summary>
    /// True on platforms where the Startup tab's Enable/Disable toggle actually takes effect
    /// (Windows and Linux). False on macOS, where <c>MacOSStartupProvider.SetEnabledAsync</c>
    /// is a documented no-op — modifying system LaunchAgent/LaunchDaemon plists requires
    /// elevated permissions, so the UI hides the toggle instead of pretending it works.
    /// </summary>
    bool SupportsStartupToggle { get; }
}

/// <summary>Full-featured fallback used in mock/design-time builds.</summary>
public sealed class MockPlatformCapabilities : IPlatformCapabilities
{
    public bool SupportsCpuAffinity        => true;
    public bool SupportsTrimMemory         => true;
    public bool SupportsCreateDump         => true;
    public bool SupportsFindWindow         => true;
    public bool SupportsIoPriority         => true;
    public bool SupportsMemoryPriority     => true;
    public bool UsesMetaKey                => false;
    public string FileManagerName          => "Explorer";
    public string ServiceManagerName       => "Services";
    public bool SupportsServiceStartupType => true;
    public bool SupportsRegistry           => true;
    public bool SupportsEfficiencyMode     => true;
    public bool SupportsHandles            => true;
    public bool SupportsMemoryMap          => true;
    public bool SupportsPowerPlan          => true;
    public string OpenLocationMenuLabel    => "Open File Location";
    public bool SupportsDirectX            => true;
    public bool SupportsStartupToggle      => true;
}
