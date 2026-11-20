use nexus_core::{MetricReading, SystemSnapshot};
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::fs;
use std::io::{BufRead, BufReader, ErrorKind, Read, Write};
#[cfg(windows)]
use std::net::{TcpListener, TcpStream};
#[cfg(unix)]
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

pub const SCREENSAVER_STALENESS_LIMIT: Duration = Duration::from_secs(5);

/// Command messages exchanged over the single-instance Unix domain socket.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IpcCommand {
    ActivateWindow,
    TogglePopout,
    ToggleGamingMode,
    ReadTelemetry,
    Quit,
}

impl IpcCommand {
    pub const fn as_wire_line(&self) -> &'static str {
        match self {
            Self::ActivateWindow => "ACTIVATE\n",
            Self::TogglePopout => "TOGGLE_POPOUT\n",
            Self::ToggleGamingMode => "TOGGLE_GAMING_MODE\n",
            Self::ReadTelemetry => "READ_TELEMETRY\n",
            Self::Quit => "QUIT\n",
        }
    }

    pub fn from_wire_line(line: &str) -> Option<Self> {
        match line.trim() {
            "ACTIVATE" => Some(Self::ActivateWindow),
            "TOGGLE_POPOUT" => Some(Self::TogglePopout),
            "TOGGLE_GAMING_MODE" => Some(Self::ToggleGamingMode),
            "READ_TELEMETRY" => Some(Self::ReadTelemetry),
            "QUIT" => Some(Self::Quit),
            _ => None,
        }
    }
}

/// JSON payload returned by `IpcCommand::ReadTelemetry`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScreensaverTelemetryPayload {
    pub cpu: f64,
    pub memory: f64,
    pub gpu: f64,
    pub disk: f64,
    pub stale: bool,
    pub age_ms: u64,
}

/// Shared screensaver telemetry state updated on every snapshot, enforcing the 5-second honesty staleness limit.
#[derive(Debug, Clone)]
pub struct SharedScreensaverTelemetry {
    pub cpu: f64,
    pub memory: f64,
    pub gpu: f64,
    pub disk: f64,
    pub last_updated_at: Instant,
    pub has_sample: bool,
}

impl Default for SharedScreensaverTelemetry {
    fn default() -> Self {
        Self::new()
    }
}

impl SharedScreensaverTelemetry {
    pub fn new() -> Self {
        Self {
            cpu: -1.0,
            memory: -1.0,
            gpu: -1.0,
            disk: -1.0,
            last_updated_at: Instant::now(),
            has_sample: false,
        }
    }

    pub fn update_from_snapshot(&mut self, snap: &SystemSnapshot) {
        let val_or_neg1 = |m: &nexus_core::Metric<f64>| match m.reading {
            MetricReading::Measured { value, .. } if value.is_finite() => value,
            _ => -1.0,
        };
        self.cpu = val_or_neg1(&snap.system.cpu_percent);
        self.memory = val_or_neg1(&snap.system.mem_used_percent);
        self.gpu = val_or_neg1(&snap.system.gpu_utilization_percent);
        self.disk = snap
            .disks
            .first()
            .map(|d| val_or_neg1(&d.root_used_percent))
            .unwrap_or(-1.0);
        self.last_updated_at = Instant::now();
        self.has_sample = true;
    }

    pub fn to_honest_payload(&self) -> ScreensaverTelemetryPayload {
        let elapsed = self.last_updated_at.elapsed();
        let age_ms = elapsed.as_millis() as u64;
        let stale = !self.has_sample || elapsed > SCREENSAVER_STALENESS_LIMIT;
        if stale {
            ScreensaverTelemetryPayload {
                cpu: -1.0,
                memory: -1.0,
                gpu: -1.0,
                disk: -1.0,
                stale: true,
                age_ms,
            }
        } else {
            ScreensaverTelemetryPayload {
                cpu: self.cpu,
                memory: self.memory,
                gpu: self.gpu,
                disk: self.disk,
                stale: false,
                age_ms,
            }
        }
    }
}

/// Result of attempting to acquire the single-instance lock (`Nexus blocker #38`).
pub enum InstanceLockOutcome {
    /// This process is the primary owner and holds the active `SingleInstanceGuard`.
    Primary(SingleInstanceGuard),
    /// An existing primary instance was already running and received `IpcCommand::ActivateWindow`.
    ForwardedToExistingPrimary,
}

/// RAII guard for the primary instance's Unix domain socket. Removes the socket path on drop.
pub struct SingleInstanceGuard {
    socket_path: PathBuf,
    stop_flag: Arc<AtomicBool>,
}

impl SingleInstanceGuard {
    pub fn socket_path(&self) -> &Path {
        &self.socket_path
    }
}

impl Drop for SingleInstanceGuard {
    fn drop(&mut self) {
        self.stop_flag.store(true, Ordering::Relaxed);
        let _ = fs::remove_file(&self.socket_path);
    }
}

/// Returns the default per-user single-instance socket/lock path:
/// - macOS: `~/Library/Application Support/NexusSystemMonitor/ipc.sock` (fallback `/tmp/com.joshsutcliff.nexusmonitor.{user}.sock`)
/// - Linux: `$XDG_RUNTIME_DIR/nexus-monitor.sock` (fallback `/tmp/com.joshsutcliff.nexusmonitor.{user}.sock`)
/// - Windows: `%APPDATA%\NexusSystemMonitor\ipc.sock`
pub fn default_ipc_socket_path() -> PathBuf {
    #[cfg(target_os = "windows")]
    {
        crate::prefs::app_support_dir().join("ipc.sock")
    }
    #[cfg(target_os = "linux")]
    {
        if let Ok(xdg_rt) = std::env::var("XDG_RUNTIME_DIR")
            && !xdg_rt.is_empty()
        {
            let candidate = PathBuf::from(xdg_rt).join("nexus-monitor.sock");
            if candidate.as_os_str().len() < 100 {
                return candidate;
            }
        }
        let user = std::env::var("USER").unwrap_or_else(|_| "default".to_string());
        PathBuf::from(format!("/tmp/com.joshsutcliff.nexusmonitor.{user}.sock"))
    }
    #[cfg(not(any(target_os = "windows", target_os = "linux")))]
    {
        if let Ok(home) = std::env::var("HOME")
            && !home.is_empty()
        {
            let candidate = PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join("NexusSystemMonitor")
                .join("ipc.sock");
            if candidate.as_os_str().len() < 100 {
                return candidate;
            }
        }
        let user = std::env::var("USER").unwrap_or_else(|_| "default".to_string());
        PathBuf::from(format!("/tmp/com.joshsutcliff.nexusmonitor.{user}.sock"))
    }
}

#[cfg(windows)]
fn connect_windows_ipc(socket_path: &Path) -> std::io::Result<TcpStream> {
    let raw = fs::read_to_string(socket_path)?;
    let port: u16 = raw
        .trim()
        .parse()
        .map_err(|e| std::io::Error::new(ErrorKind::InvalidData, e))?;
    let addr = std::net::SocketAddr::from(([127, 0, 0, 1], port));
    TcpStream::connect_timeout(&addr, Duration::from_millis(250))
}

/// Attempts to connect to an existing primary instance at `socket_path` and send `cmd` with a 250ms write timeout.
pub fn send_ipc_command(socket_path: &Path, cmd: IpcCommand) -> std::io::Result<()> {
    #[cfg(unix)]
    let mut stream = UnixStream::connect(socket_path)?;
    #[cfg(windows)]
    let mut stream = connect_windows_ipc(socket_path)?;
    let _ = stream.set_write_timeout(Some(Duration::from_millis(250)));
    stream.write_all(cmd.as_wire_line().as_bytes())?;
    stream.flush()
}

/// Queries live screensaver telemetry (`IpcCommand::ReadTelemetry`) from the running primary instance.
pub fn query_ipc_telemetry(socket_path: &Path) -> std::io::Result<ScreensaverTelemetryPayload> {
    #[cfg(unix)]
    let mut stream = UnixStream::connect(socket_path)?;
    #[cfg(windows)]
    let mut stream = connect_windows_ipc(socket_path)?;
    let _ = stream.set_write_timeout(Some(Duration::from_millis(250)));
    let _ = stream.set_read_timeout(Some(Duration::from_millis(250)));
    stream.write_all(IpcCommand::ReadTelemetry.as_wire_line().as_bytes())?;
    stream.flush()?;
    let mut resp = String::new();
    stream.read_to_string(&mut resp)?;
    serde_json::from_str(&resp).map_err(|e| std::io::Error::new(ErrorKind::InvalidData, e))
}

/// Acquires the single-instance lock at `socket_path`, or forwards `IpcCommand::ActivateWindow`
/// to the already-running primary instance.
pub fn acquire_single_instance_or_activate<F>(
    socket_path: PathBuf,
    on_command: F,
) -> std::io::Result<InstanceLockOutcome>
where
    F: Fn(IpcCommand) + Send + Sync + 'static,
{
    let shared = Arc::new(Mutex::new(SharedScreensaverTelemetry::new()));
    acquire_single_instance_with_telemetry(socket_path, shared, on_command)
}

/// Acquires the single-instance lock at `socket_path` with a `SharedScreensaverTelemetry` bridge
/// for `IpcCommand::ReadTelemetry` queries.
pub fn acquire_single_instance_with_telemetry<F>(
    socket_path: PathBuf,
    shared_telemetry: Arc<Mutex<SharedScreensaverTelemetry>>,
    on_command: F,
) -> std::io::Result<InstanceLockOutcome>
where
    F: Fn(IpcCommand) + Send + Sync + 'static,
{
    if let Some(parent) = socket_path.parent() {
        fs::create_dir_all(parent)?;
    }

    #[cfg(windows)]
    {
        if socket_path.exists() {
            if send_ipc_command(&socket_path, IpcCommand::ActivateWindow).is_ok() {
                return Ok(InstanceLockOutcome::ForwardedToExistingPrimary);
            }
            let _ = fs::remove_file(&socket_path);
        }
        let listener = TcpListener::bind("127.0.0.1:0")?;
        let port = listener.local_addr()?.port();
        fs::write(&socket_path, format!("{port}\n"))?;
        let stop_flag = Arc::new(AtomicBool::new(false));
        let thread_stop = Arc::clone(&stop_flag);
        let callback = Arc::new(on_command);

        std::thread::spawn(move || {
            for incoming in listener.incoming() {
                if thread_stop.load(Ordering::Relaxed) {
                    break;
                }
                if let Ok(mut stream) = incoming {
                    let _ = stream.set_read_timeout(Some(Duration::from_millis(250)));
                    let mut reader = BufReader::new(&stream);
                    let mut line = String::new();
                    if reader.read_line(&mut line).is_ok()
                        && let Some(cmd) = IpcCommand::from_wire_line(&line)
                    {
                        if cmd == IpcCommand::ReadTelemetry {
                            let payload = shared_telemetry.lock().to_honest_payload();
                            if let Ok(json) = serde_json::to_string(&payload) {
                                let _ = stream.write_all(json.as_bytes());
                                let _ = stream.write_all(b"\n");
                                let _ = stream.flush();
                            }
                        } else {
                            callback(cmd);
                        }
                    }
                }
            }
        });

        Ok(InstanceLockOutcome::Primary(SingleInstanceGuard {
            socket_path,
            stop_flag,
        }))
    }

    #[cfg(unix)]
    {
        let mut last_err = None;
        for _attempt in 0..3 {
            if socket_path.exists() {
                if send_ipc_command(&socket_path, IpcCommand::ActivateWindow).is_ok() {
                    return Ok(InstanceLockOutcome::ForwardedToExistingPrimary);
                }
                let _ = fs::remove_file(&socket_path);
            }

            match UnixListener::bind(&socket_path) {
                Ok(listener) => {
                    let stop_flag = Arc::new(AtomicBool::new(false));
                    let thread_stop = Arc::clone(&stop_flag);
                    let callback = Arc::new(on_command);

                    std::thread::spawn(move || {
                        for incoming in listener.incoming() {
                            if thread_stop.load(Ordering::Relaxed) {
                                break;
                            }
                            if let Ok(mut stream) = incoming {
                                let _ = stream.set_read_timeout(Some(Duration::from_millis(250)));
                                let mut reader = BufReader::new(&stream);
                                let mut line = String::new();
                                if reader.read_line(&mut line).is_ok()
                                    && let Some(cmd) = IpcCommand::from_wire_line(&line)
                                {
                                    if cmd == IpcCommand::ReadTelemetry {
                                        let payload = shared_telemetry.lock().to_honest_payload();
                                        if let Ok(json) = serde_json::to_string(&payload) {
                                            let _ = stream.write_all(json.as_bytes());
                                            let _ = stream.write_all(b"\n");
                                            let _ = stream.flush();
                                        }
                                    } else {
                                        callback(cmd);
                                    }
                                }
                            }
                        }
                    });

                    return Ok(InstanceLockOutcome::Primary(SingleInstanceGuard {
                        socket_path,
                        stop_flag,
                    }));
                }
                Err(e) if e.kind() == ErrorKind::AddrInUse => {
                    if send_ipc_command(&socket_path, IpcCommand::ActivateWindow).is_ok() {
                        return Ok(InstanceLockOutcome::ForwardedToExistingPrimary);
                    }
                    let _ = fs::remove_file(&socket_path);
                    last_err = Some(e);
                }
                Err(e) => return Err(e),
            }
        }

        Err(last_err
            .unwrap_or_else(|| std::io::Error::other("failed to bind single-instance socket")))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn read_telemetry_ipc_enforces_5s_honesty_staleness_guard() {
        let sock =
            std::env::temp_dir().join(format!("nexus_telem_ipc_{}.sock", std::process::id()));
        let _ = fs::remove_file(&sock);
        let shared = Arc::new(Mutex::new(SharedScreensaverTelemetry::new()));
        {
            let mut st = shared.lock();
            st.cpu = 34.5;
            st.memory = 61.2;
            st.gpu = 18.0;
            st.disk = 55.0;
            st.last_updated_at = Instant::now();
            st.has_sample = true;
        }
        let guard =
            match acquire_single_instance_with_telemetry(sock.clone(), Arc::clone(&shared), |_| {})
                .unwrap()
            {
                InstanceLockOutcome::Primary(g) => g,
                _ => panic!("expected primary"),
            };

        let fresh = query_ipc_telemetry(&sock).expect("fresh read");
        assert!(!fresh.stale);
        assert!((fresh.cpu - 34.5).abs() < 1e-6);
        assert!((fresh.memory - 61.2).abs() < 1e-6);

        // Simulate stale sample (> 5s old)
        {
            let mut st = shared.lock();
            st.last_updated_at = Instant::now() - Duration::from_secs(6);
        }
        let stale = query_ipc_telemetry(&sock).expect("stale read");
        assert!(stale.stale);
        assert_eq!(stale.cpu, -1.0);
        assert_eq!(stale.memory, -1.0);
        assert_eq!(stale.gpu, -1.0);
        assert_eq!(stale.disk, -1.0);
        assert!(stale.age_ms >= 5000);

        drop(guard);
    }
}
