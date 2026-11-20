use crate::{Metric, ProcessKey};
use compact_str::CompactString;
use serde::{Deserialize, Serialize};

/// Volume space summary preserving `Metric<f64>` provenance and avoiding fabricated 100% usage.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct VolumeSpaceSummary {
    pub mount_point: CompactString,
    pub filesystem: CompactString,
    pub total_bytes: Metric<f64>,
    pub used_bytes: Metric<f64>,
    pub available_bytes: Metric<f64>,
    pub used_percent: Metric<f64>,
}

/// Lifecycle state of an OS service / launchd job / systemd unit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ServiceState {
    Running,
    Stopped,
    Paused,
    StartPending,
    StopPending,
    Unknown,
}

impl ServiceState {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Running => "Running",
            Self::Stopped => "Stopped",
            Self::Paused => "Paused",
            Self::StartPending => "Starting",
            Self::StopPending => "Stopping",
            Self::Unknown => "Unknown",
        }
    }
}

/// Startup policy of an OS service / launchd job / systemd unit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ServiceStartType {
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Unknown,
}

impl ServiceStartType {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Automatic => "Automatic",
            Self::AutomaticDelayed => "Automatic (Delayed)",
            Self::Manual => "Manual",
            Self::Disabled => "Disabled",
            Self::Unknown => "Unknown",
        }
    }
}

/// Unified OS service record across macOS (`launchd`), Linux (`systemd`), and Windows (SCM).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ServiceEntry {
    pub name: CompactString,
    pub display_name: CompactString,
    pub description: CompactString,
    pub state: ServiceState,
    pub start_type: ServiceStartType,
    pub pid: Option<i32>,
    pub binary_path: CompactString,
    pub user_account: CompactString,
}

/// Result of a service control or startup-type mutation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ServiceActionResult {
    Ok {
        dry_run: bool,
        message: CompactString,
    },
    Unsupported(CompactString),
    Failed(CompactString),
}

/// Origin kind for a startup / login item.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum StartupItemType {
    LaunchAgent,
    LaunchDaemon,
    XdgAutostart,
    WindowsRegistryRun,
    StartupFolder,
}

impl StartupItemType {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::LaunchAgent => "LaunchAgent",
            Self::LaunchDaemon => "LaunchDaemon",
            Self::XdgAutostart => "XDG Autostart",
            Self::WindowsRegistryRun => "Registry Run",
            Self::StartupFolder => "Startup Folder",
        }
    }
}

/// Unified startup / autostart item across macOS, Linux, and Windows.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StartupItem {
    pub name: CompactString,
    pub command: CompactString,
    pub publisher: CompactString,
    pub location: CompactString,
    pub is_enabled: bool,
    pub run_at_load: bool,
    pub item_type: StartupItemType,
    pub user_writable: bool,
}

/// Transport protocol for an active network socket.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ConnectionProtocol {
    Tcp4,
    Tcp6,
    Udp4,
    Udp6,
}

impl ConnectionProtocol {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Tcp4 => "TCP4",
            Self::Tcp6 => "TCP6",
            Self::Udp4 => "UDP4",
            Self::Udp6 => "UDP6",
        }
    }
}

/// Standard TCP FSM state (`Unknown` for UDP datagram sockets).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum TcpState {
    Established,
    Listen,
    SynSent,
    SynReceived,
    FinWait1,
    FinWait2,
    CloseWait,
    Closing,
    LastAck,
    TimeWait,
    Closed,
    Unknown,
}

impl TcpState {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Established => "ESTABLISHED",
            Self::Listen => "LISTEN",
            Self::SynSent => "SYN_SENT",
            Self::SynReceived => "SYN_RCVD",
            Self::FinWait1 => "FIN_WAIT_1",
            Self::FinWait2 => "FIN_WAIT_2",
            Self::CloseWait => "CLOSE_WAIT",
            Self::Closing => "CLOSING",
            Self::LastAck => "LAST_ACK",
            Self::TimeWait => "TIME_WAIT",
            Self::Closed => "CLOSED",
            Self::Unknown => "-",
        }
    }
}

/// Active TCP/UDP socket with PID-reuse-safe `ProcessKey` attribution when accessible.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NetworkConnectionEntry {
    pub protocol: ConnectionProtocol,
    pub local_address: CompactString,
    pub local_port: u16,
    pub remote_address: CompactString,
    pub remote_port: u16,
    pub state: TcpState,
    pub pid: Option<i32>,
    pub process_key: Option<ProcessKey>,
    pub process_name: CompactString,
    pub rx_bytes: Option<u64>,
    pub tx_bytes: Option<u64>,
}

/// Wi-Fi RF spectrum band.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum WifiBand {
    Band2_4GHz,
    Band5GHz,
    Band6GHz,
    Unknown,
}

impl WifiBand {
    #[inline]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Band2_4GHz => "2.4 GHz",
            Self::Band5GHz => "5 GHz",
            Self::Band6GHz => "6 GHz",
            Self::Unknown => "Unknown",
        }
    }
}

/// Wireless access point entry preserving measurement honesty (`AccessDenied` / redacted SSIDs).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WifiNetworkEntry {
    pub ssid: CompactString,
    pub bssid: Option<CompactString>,
    pub rssi_dbm: Metric<f64>,
    pub noise_dbm: Metric<f64>,
    pub snr_db: Metric<f64>,
    pub channel: u32,
    pub channel_width_mhz: Option<u32>,
    pub band: WifiBand,
    pub phy_mode: CompactString,
    pub security: CompactString,
    pub is_connected: bool,
    pub redacted: bool,
}

/// Discovered open port on a LAN host.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LanPortEntry {
    pub port: u16,
    pub protocol: CompactString,
    pub state: CompactString,
    pub service: CompactString,
    pub version: CompactString,
}

/// Discovered host on the local subnet via `nmap` XML or native TCP probe fallback.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LanHostEntry {
    pub ip: CompactString,
    pub hostname: CompactString,
    pub mac: Option<CompactString>,
    pub vendor: Option<CompactString>,
    pub latency_ms: Metric<f64>,
    pub open_ports: Vec<LanPortEntry>,
    pub os_guess: Option<CompactString>,
}

/// Complete wireless RF spectrum scan report.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WirelessScanReport {
    pub interface: CompactString,
    pub connected_ssid: Option<CompactString>,
    pub networks: Vec<WifiNetworkEntry>,
    pub notes: CompactString,
}

/// Complete LAN subnet discovery report.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LanRadarReport {
    pub subnet: CompactString,
    pub scanner_backend: CompactString,
    pub elapsed_ms: u64,
    pub hosts: Vec<LanHostEntry>,
    pub notes: CompactString,
}

/// Logged-in interactive user session.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UserSessionEntry {
    pub username: CompactString,
    pub tty: CompactString,
    pub host: CompactString,
    pub login_time: CompactString,
    pub state: CompactString,
    pub pid: Option<i32>,
}

/// Installed application bundle metadata.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct InstalledAppEntry {
    pub name: CompactString,
    pub version: CompactString,
    pub bundle_id: CompactString,
    pub path: CompactString,
    pub size_bytes: Option<u64>,
}

/// Related Library file candidate discovered for an installed application.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RelatedFileEntry {
    pub path: CompactString,
    pub category: CompactString,
    pub size_bytes: u64,
}

/// Receipt returned after moving an application bundle and related files to Trash.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UninstallReceipt {
    pub app_name: CompactString,
    pub trashed_paths: Vec<CompactString>,
    pub failed_paths: Vec<(CompactString, CompactString)>,
}

/// Driver bundle classification (`.kext` vs DriverKit `.dext`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum DriverKind {
    KernelExtension,
    SystemExtension,
}

impl DriverKind {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::KernelExtension => "Kernel (.kext)",
            Self::SystemExtension => "DriverKit (.dext)",
        }
    }
}

/// Driver ownership scope (`com.apple.*` vs third-party).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum DriverScope {
    AppleSystem,
    ThirdParty,
}

impl DriverScope {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::AppleSystem => "Apple System",
            Self::ThirdParty => "Third-Party",
        }
    }
}

/// Reconciled driver lifecycle state across `kextstat`, `systemextensionsctl`, and on-disk bundles.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum DriverState {
    Loaded,
    InstalledNotLoaded,
    ActivatedEnabled,
    WaitingForUser,
    Unloading,
}

impl DriverState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Loaded => "Loaded",
            Self::InstalledNotLoaded => "Installed (Not Loaded)",
            Self::ActivatedEnabled => "Activated Enabled",
            Self::WaitingForUser => "Waiting for User",
            Self::Unloading => "Unloading / Staged",
        }
    }
}

/// Loaded kernel extension / DriverKit system extension entry.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DriverEntry {
    pub index: i64,
    pub name: CompactString,
    pub version: CompactString,
    pub loaded: bool,
    pub address: CompactString,
    pub size_bytes: u64,
    pub kind: DriverKind,
    pub scope: DriverScope,
    pub state: DriverState,
    pub uuid: Option<CompactString>,
    pub dependencies: Vec<u32>,
    pub bundle_path: Option<CompactString>,
}

/// Static + slow-changing system hardware inventory.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SystemHardwareSummary {
    pub hostname: CompactString,
    pub os_name: CompactString,
    pub os_version: CompactString,
    pub architecture: CompactString,
    pub chip_model: CompactString,
    pub physical_cores: u32,
    pub logical_cores: u32,
    pub p_cores: u32,
    pub e_cores: u32,
    pub l2_cache_kb: u64,
    pub l3_cache_kb: u64,
    pub total_ram_bytes: u64,
    pub gpu_model: CompactString,
    pub gpu_cores: Option<u32>,
    pub firmware_version: CompactString,
}
