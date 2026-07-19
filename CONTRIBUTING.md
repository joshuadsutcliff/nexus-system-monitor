# Contributing to Nexus System Monitor

Thanks for your interest in contributing. This document covers everything you need to get started.

---

## Table of Contents

1. [Ways to contribute](#ways-to-contribute)
2. [Development setup](#development-setup)
3. [Project structure](#project-structure)
4. [How platform implementations work](#how-platform-implementations-work)
5. [Submitting changes](#submitting-changes)
6. [Coding conventions](#coding-conventions)
7. [Platform code honesty contract](#platform-code-honesty-contract)
8. [Reporting bugs](#reporting-bugs)

---

## Ways to Contribute

- **Bug reports** — File an [Issue](https://github.com/joshuadsutcliff/nexus-system-monitor/issues) with platform, version, and reproduction steps
- **Feature requests** — Open a Discussion or Issue with the `enhancement` label
- **Code** — Fix bugs, implement missing features from the [gap analysis](docs/gap-analysis.md), or improve platform implementations
- **Testing** — Run the app on macOS or Linux and report what works / doesn't (see [TESTING.md](TESTING.md))
- **Documentation** — Improve the README, add inline comments, or expand the docs/ folder

---

## Development Setup

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- An IDE: [Visual Studio 2022](https://visualstudio.microsoft.com/), [Rider](https://www.jetbrains.com/rider/), or VS Code with the C# extension

### Clone and build

```bash
git clone https://github.com/joshuadsutcliff/nexus-system-monitor.git
cd nexus-system-monitor
dotnet build NexusMonitor.sln
```

The build should produce **0 errors, 0 warnings**. If you see warnings, fix them before submitting a PR.

### Run

#### Windows

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0-windows10.0.17763.0
```

Some features (IO priority, service control) require elevated privileges. Use "Run as Administrator" on the compiled `.exe` for full access.

#### macOS

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0-macos
```

#### Linux

```bash
dotnet run --project src/NexusMonitor.UI/NexusMonitor.UI.csproj --framework net8.0
```

### Run tests

```bash
dotnet test tests/NexusMonitor.Core.Tests/NexusMonitor.Core.Tests.csproj
```

---

## Project Structure

```
NexusMonitor.sln
├── src/
│   ├── NexusMonitor.Core/              # Abstractions, models, services — no platform code
│   ├── NexusMonitor.UI/                # Avalonia UI app (AXAML views + ViewModels)
│   ├── NexusMonitor.DiskAnalyzer/      # Disk analysis engine
│   ├── NexusMonitor.Platform.Windows/  # Windows API implementations (PDH, WMI, P/Invoke)
│   ├── NexusMonitor.Platform.MacOS/    # macOS implementations (sysctl, Mach, ObjC)
│   └── NexusMonitor.Platform.Linux/    # Linux implementations (procfs, sysfs, systemd)
└── tests/
    └── NexusMonitor.Core.Tests/        # Unit tests
```

The most important design rule: **Core has no platform knowledge.** All OS-specific code lives in the three platform projects.

---

## How Platform Implementations Work

Platform behavior is abstracted behind five interfaces in `NexusMonitor.Core`:

| Interface | Responsibility |
|-----------|---------------|
| `ISystemMetricsProvider` | CPU, memory, disk, network, GPU metrics |
| `IProcessProvider` | Process enumeration and control |
| `INetworkConnectionsProvider` | TCP/UDP connection enumeration |
| `IServicesProvider` | System service management |
| `IStartupProvider` | Startup item management |

Each platform project implements these interfaces. The DI container registers the correct implementation at startup based on the current OS.

**To add a feature that touches platform APIs:**
1. Add the method signature to the relevant interface in `Core`
2. Implement it in all three platform projects (or return a sensible no-op/`NotSupportedException` where it genuinely can't be supported)
3. Call the interface method from the ViewModel — never call platform APIs directly from UI code
4. Check `IPlatformCapabilities` to gate UI elements that only make sense on specific platforms

---

## Submitting Changes

1. **Fork** the repository and create a branch from `main`
2. **Keep PRs focused** — one feature or bug fix per PR. Large PRs are harder to review.
3. **Build clean** — `dotnet build NexusMonitor.sln` must produce 0 errors and 0 warnings
4. **Run tests** — all existing tests must pass
5. **Test on your platform** — describe what you tested in the PR description
6. **Write a clear PR description** — what does this change, why, and how did you test it?

### Commit message style

Use imperative mood in the subject line:

```
Add Memory Reclaim memory reclamation service
Fix null reference in WindowsProcessProvider.GetModules()
Improve IO priority handling on Linux procfs
```

---

## Coding Conventions

- **C# 12 / .NET 8** — use modern language features (primary constructors, collection expressions, etc.)
- **Nullable enabled** — all projects have `<Nullable>enable</Nullable>`. Don't suppress warnings with `!` unless truly necessary.
- **MVVM** — ViewModels must not reference platform types. Keep business logic out of code-behind.
- **Reactive patterns** — use `Observable`/`Subject` for real-time data. Wrap tick handlers in `SemaphoreSlim` to prevent re-entry (see [architecture.md](docs/architecture.md)).
- **Logging** — inject `ILogger<T>` and log errors with context. Don't swallow exceptions silently.
- **Thread safety** — metrics polling runs on background threads. Marshal UI updates with `ObserveOn(RxApp.MainThreadScheduler)`.
- **No warnings** — treat compiler warnings as errors in PRs.

---

## Platform Code Honesty Contract

Nexus has a strong "honesty" convention: when a metric can't be read, the UI shows nothing (an
em dash, "—", or "N/A") rather than fabricate or estimate a value. This section covers what that
means for new platform code specifically.

- **Express unavailability explicitly, never via a sentinel zero.** All *new or touched* P/Invoke
  call sites must return a nullable or Result-shaped type that lets the caller distinguish "this
  hardware/driver never reported the figure" from "a real, present value of zero." See
  `GpuPerformanceStats.ReadMemoryBytes(IReadOnlyDictionary<string, long>, string) : long?` in
  `NexusMonitor.Platform.MacOS` — it returns `null` only when the key is absent from the
  PerformanceStatistics dictionary; a present value always goes through `ClampMemoryBytes` first,
  so a present-but-negative reading still degrades to a real (non-null) zero, never to
  "unavailable." Follow that precedent (nullable numeric types, or a small Result type when a
  richer reason is needed) rather than returning a bare `0`/`-1`/empty string and letting the UI
  layer guess whether it's real.
- **Reason-specific tooltip copy will migrate onto a structured availability channel.** As of
  2026-07-19, the unavailable-metric-tooltips feature added hover tooltips explaining *why* a
  placeholder is showing, using `NexusMonitor.Core.Formatting.UnavailableMetricCopy`. Today those
  reasons are inferred locally at the ViewModel from context that's already available there (e.g.
  "we're on macOS AND this is Apple Silicon AND the value is the idle-placeholder"), because
  several non-nullable models in the pipeline (`GpuMetrics`, `CpuMetrics`) already discard a
  richer "why" that the platform layer *does* know at read time — see the
  `// TODO(availability-enum):` comments on `IOAccelerator.Open()`/`ReadPerformanceStatistics()`
  for a concrete example. This is an adopted decision, not a someday-TODO: as platform code is
  touched going forward, prefer threading that reason through to the ViewModel over re-inferring
  it from local context, and widen the relevant model (or introduce an availability enum/Result
  type) when doing so meaningfully improves tooltip accuracy.

---

## Reporting Bugs

Open an [Issue](https://github.com/joshuadsutcliff/nexus-system-monitor/issues) and include:

- **Platform and version** (e.g. "Windows 11 23H2, v0.1.8")
- **What you did** — steps to reproduce
- **What you expected** — what should have happened
- **What happened** — actual behavior, error message, or screenshot
- **Log file** (if applicable) — `%AppData%\NexusMonitor\logs\nexus-*.log` on Windows, `~/.local/share/NexusMonitor/logs/` on Linux/macOS

For macOS and Linux testing issues specifically, see [TESTING.md](TESTING.md).

---

## Questions?

Open a [Discussion](https://github.com/joshuadsutcliff/nexus-system-monitor/discussions) for general questions, design ideas, or anything that doesn't fit an Issue.
