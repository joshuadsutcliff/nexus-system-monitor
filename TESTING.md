# Nexus System Monitor — Tester's Guide

This guide is for anyone doing early testing of Nexus System Monitor on macOS or Linux.
The application is in active development — your feedback directly shapes what gets built next.

---

## What You're Testing

Nexus System Monitor is a cross-platform desktop system monitor. Think Task Manager, Activity Monitor, and htop — unified into a single interface. This is a **pre-release testing build**, not a production release. Expect rough edges; the goal right now is to verify that core functionality works correctly on real hardware.

---

## Prerequisites

Install the **.NET 8 SDK** for your platform:

- **macOS:** https://dotnet.microsoft.com/en-us/download/dotnet/8.0 — download the macOS installer (choose `Arm64` for Apple Silicon, `x64` for Intel)
- **Linux:** https://dotnet.microsoft.com/en-us/download/dotnet/8.0 — or use your package manager:
  ```bash
  # Fedora
  sudo dnf install dotnet-sdk-8.0

  # Ubuntu / Debian
  sudo apt install dotnet-sdk-8.0
  ```

Verify the install worked:
```bash
dotnet --version
# Should print 8.x.x
```

---

## Getting the Code

```bash
git clone https://github.com/joshuadsutcliff/nexus-system-monitor.git
cd nexus-system-monitor
```

No other setup is required. All dependencies are restored automatically on first build.

---

## Running on macOS

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0-macos
```

The first run compiles the project — this takes 30–60 seconds. Subsequent runs start in a few seconds.

### If macOS blocks the app (Gatekeeper)

Because the binary is unsigned, macOS may refuse to open it. To allow it:

**Option A** — Right-click the binary → **Open** → click Open in the dialog.

**Option B** — Run this command, replacing the path with where the binary was built:
```bash
xattr -d com.apple.quarantine path/to/NexusMonitor
```

### Gaming Mode / power plan switching

Switching power profiles (Power Saver / Balanced / High Performance) uses `pmset`, which may require administrator privileges on some Macs. If the power plan button doesn't appear to do anything, try:

```bash
sudo dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0-macos
```

Process throttling (the core Gaming Mode behavior) works without elevation regardless.

---

## Running on Linux

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0
```

The app automatically detects your init system and uses the correct backend:

| Init system | Detection | Commands used |
|-------------|-----------|---------------|
| systemd | `/proc/1/comm` = `systemd` | `systemctl` |
| SysVinit | `/etc/init.d/` present | `service` |
| OpenRC | `/run/openrc/softlevel` present | `rc-status`, `rc-service` |

### Power plan switching on Linux

Gaming Mode power plan switching works if `power-profiles-daemon` is installed:
```bash
# Fedora
sudo dnf install power-profiles-daemon

# Ubuntu
sudo apt install power-profiles-daemon
```

Without it, the app falls back to writing `/sys/devices/system/cpu/*/cpufreq/scaling_governor` directly (may require root). If neither is available, the power plan UI shows plans but switching is a no-op — all other Gaming Mode features still work.

### Wayland note

ProBalance (automatic background process throttling) works best on **X11**. Under Wayland, the app cannot reliably detect which process is in the foreground, so it applies background throttling more broadly. This is a known limitation — all other features are unaffected.

---

## What to Test

Work through each tab and note what works, what's wrong, and what's missing.

### Processes tab

- [ ] Process list populates with real processes from the system
- [ ] CPU % and memory columns update in real time (every ~1–2 seconds)
- [ ] Right-clicking a process shows a context menu (Set Priority, Suspend, Kill, etc.)
- [ ] Suspending a process (right-click → Suspend) and resuming it works
- [ ] Killing a process works (try a non-critical app)
- [ ] Clicking a process name opens a detail panel (Threads, Modules, Environment tabs)

### Performance tab

- [ ] CPU graph updates in real time
- [ ] Per-core CPU graphs visible in the sidebar (click CPU in left sidebar)
- [ ] Memory usage graph reflects actual usage
- [ ] Disk read/write rates update when doing disk activity
- [ ] Network send/receive rates update when doing network activity
- [ ] Left sidebar lets you switch between CPU, Memory, Disk, Network

### System Info tab

- [ ] Tab loads without error
- [ ] Shows correct hostname, OS name/version, architecture
- [ ] Shows uptime and total RAM

### Services tab

- [ ] Service list populates (may take a few seconds)
- [ ] Running services are distinguished from stopped services
- [ ] Right-click → Stop on a non-critical service works (test something safe like a print spooler or bluetooth service if present)
- [ ] Right-click → Start brings it back
- **Linux only:** Confirm the services listed match what you'd expect from your init system

### Startup Items tab

- [ ] List of startup items populates
- [ ] Items can be enabled and disabled

### Network tab

- [ ] Active connections list populates (TCP/UDP)
- [ ] Local and remote addresses are shown correctly
- [ ] Send/receive rate display in the bottom status bar updates
- **Linux only:** Process names appear next to connections (e.g., "firefox", "sshd")

### Optimization tab

- [ ] Loads and shows process recommendations
- [ ] Applying a recommendation doesn't crash the app

### ProBalance tab

- [ ] Toggle ProBalance on
- [ ] Verify no crash or error
- [ ] Open a few apps and switch between them — CPU-intensive background processes should be slightly deprioritized

### Gaming Mode tab

- [ ] Power plan list shows Power Saver / Balanced / High Performance
- [ ] Toggling Gaming Mode on/off doesn't crash
- [ ] Current active power plan is highlighted correctly

### Alerts tab

- [ ] Create an alert (e.g., CPU > 90% for 10 seconds)
- [ ] Alert appears in the list
- [ ] Deleting an alert works

### Settings tab

- [ ] Dark/Light theme toggle works
- [ ] Accent color changes apply immediately
- [ ] Overlay widget toggle shows/hides the floating widget

### Desktop Overlay Widget

- [ ] Widget appears when enabled
- [ ] Draggable to different screen positions
- [ ] Displays CPU, RAM, and network values
- [ ] Stays on top of other windows

---

## Known Limitations

These are known gaps — no need to report them as bugs:

| Area | Limitation |
|------|------------|
| **Disk Analyzer tab** | Disabled on all platforms — coming in a future release |
| **System Info** | macOS/Linux show basic info (hostname, OS, RAM, uptime) — detailed hardware inventory (CPU cache, memory slot specs, GPU driver) is Windows-only for now |
| **GPU metrics** | GPU utilization only available on Windows |
| **Process handles** | Handle count column shows 0 on Linux (kernel doesn't expose this directly) |
| **Notifications** | Toast notifications not implemented on macOS/Linux |
| **Gaming Mode — power plans on macOS** | May require `sudo` to apply; see running instructions above |
| **ProBalance on Wayland** | No foreground window detection; throttles broadly instead |

---

## Reporting Issues

Open an issue at: **https://github.com/joshuadsutcliff/nexus-system-monitor/issues**

Please include:

1. **Platform** — macOS version + chip, or Linux distro + kernel version
2. **Init system** (Linux) — systemd / SysVinit / OpenRC
3. **Steps to reproduce** — what you did, what you expected, what happened
4. **Screenshot** if applicable
5. **Terminal output** — if the app crashed or printed errors, paste the output

---

## Quick Start Checklist

If you only have a few minutes, focus on these:

1. Does the app launch without crashing?
2. Does the Processes tab show real processes with live CPU/memory?
3. Does the Services tab list your actual system services?
4. Does the Network tab show active connections (with process names on Linux)?
5. Does Gaming Mode open without error?
