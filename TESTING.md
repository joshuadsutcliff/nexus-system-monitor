# Testing & Verification Guide (`nexus-system-monitor`)

This document covers unit testing, static analysis, CLI verification, headless TUI testing, and automated GUI screenshot verification across **macOS**, **Linux**, and **Windows**.

---

## 1. Automated Test Suite & Static Gates

Run the workspace test suite (`59` tests across 8 crates) and zero-warning Clippy gate:

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

### Key Invariants Verified by `cargo test --workspace`
- **Honesty & Provenance (`nexus-core`):** Rejects `NaN`, `+Inf`, and `-Inf`; normalizes `-0.0` to `+0.0`; enforces severity-ranked aggregation (`UnavailableReason`); verifies RFC 8439 ChaCha20-Poly1305, X25519, HKDF-SHA256, and 7 `.nexusskin` security validators.
- **Collector Parsers & Live Host Telemetry (`nexus-collectors-{macos,linux,windows}`):** Verifies `libIOReport` DVFS and residency decoding, macOS `launchctl`/`lsof`/`system_profiler`/`diskutil`/`kmutil` parsers, Linux `/proc` and `/sys` parsers, Windows `GetSystemTimes` kernel-idle subtraction and `netstat`/`sc` parsers, and live host snapshot coherence.
- **Disk Scanner, SQLite Stores & Flight Recorder (`nexus-disk`):** Verifies streaming directory scans, WGSL hit-testing parity, 2-pass SHA-256 duplicate grouping, SQLite `SnapshotDatabase` / `DiskSnapshotStore` / `MetricsHistoryStore`, and 32 MiB `.nexustrace` preflight safety.
- **PID-Reuse-Safe Automation (`nexus-automation`):** Verifies `ActionCoordinator` double `ProcessKey` checks, `RulesEngine` staleness refusal, and crash-safe `GamingModeService` journal recovery.
- **Shaders, VFD Ballistics & Single-Instance IPC (`nexus-ui`):** Validates all 3 embedded WGSL shaders via `naga`, verifies 60 Hz frame deduplication in `CompressedHistoryGraph`, and tests single-instance IPC with the 5-second screensaver honesty staleness guard.

---

## 2. CLI & Headless TUI Verification

Run all non-interactive CLI subcommands and the headless `ratatui` `TestBackend` harness:

```bash
cargo run --release -p nexus-cli --bin nexus -- status
cargo run --release -p nexus-cli --bin nexus -- processes -n 10
cargo run --release -p nexus-cli --bin nexus -- services
cargo run --release -p nexus-cli --bin nexus -- startup
cargo run --release -p nexus-cli --bin nexus -- connections
cargo run --release -p nexus-cli --bin nexus -- disk-usage --path . --max-depth 3
cargo run --release -p nexus-cli --bin nexus -- health
cargo run --release -p nexus-cli --bin nexus -- benchmark --suite cpu --duration-secs 1.0
cargo run --release -p nexus-cli --bin nexus -- doctor
cargo run --release -p nexus-cli --bin nexus -- dashboard --frames 5
```

---

## 3. Automated GUI & Popout Screenshot Harness (`nexus-monitor`)

`nexus-monitor` includes built-in visual verification flags (`--no-single-instance`, `--view <slug>`, `--popout-skin <id>`, `--preset <id>`, `--screenshot-out <path.png>`, and `--auto-exit-ms <ms>`) so every view can be rendered and captured deterministically on macOS Metal, Linux Xvfb + Mesa Lavapipe Vulkan, or Windows / Wine Vulkan:

```bash
# Capture primary Apple HIG views
for view in dashboard performance processes services startup connections wireless \
            disk-space apple-silicon power-and-freq system-info users installed-apps \
            drivers flight-recorder benchmarks settings honesty; do
  ./target/release/nexus-monitor --no-single-instance \
    --view "$view" \
    --screenshot-out "/tmp/nexus_${view}.png" \
    --auto-exit-ms 3500
done

# Capture the independent native .nexusskin desktop popout window
./target/release/nexus-monitor --no-single-instance \
  --popout-skin winamp \
  --screenshot-out "/tmp/nexus_popout_winamp.png" \
  --auto-exit-ms 3500
```

### Headless Linux Xvfb + Software Vulkan (Lavapipe)
On a headless Linux server or VM with `xvfb` and `mesa-vulkan-drivers` installed:

```bash
xvfb-run -a -s "-screen 0 1440x900x24" \
  ./target/release/nexus-monitor --no-single-instance \
  --view dashboard \
  --screenshot-out /tmp/linux_dashboard.png \
  --auto-exit-ms 3500
```
