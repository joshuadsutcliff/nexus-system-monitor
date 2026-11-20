# Dependency Upgrade Queue

Audit date: 2026-09-09  
SDK: .NET 8.0.422  
Command: `dotnet list NexusMonitor.sln package --vulnerable --include-transitive`

## Current advisory result

The fresh audit reports two High transitive vulnerabilities:

- `SQLitePCLRaw.lib.e_sqlite3` 2.1.6 across projects that inherit `Microsoft.Data.Sqlite` 8.0.1. Advisory: [`GHSA-2m69-gcr7-jv3q`](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
- `Tmds.DBus.Protocol` 0.20.0 in the UI and UI test dependency graphs. Advisory: [`GHSA-xrw6-gwf8-vvr9`](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9).

No direct package was reported as vulnerable. The scan does not establish a formal support policy for any package family. Version availability below comes from the 2026-09-09 NuGet graph and must be checked again when each upgrade starts.

The CI package-audit job records this command on every push and pull request. Each queue item below must land and pass independently. Do not combine major framework changes.

## Ranked queue

1. **Update Microsoft.Data.Sqlite within .NET 8.** Test 8.0.1 to 8.0.31 as one focused change. Rerun snapshot database recovery, retention, comparison, and file-release tests. Confirm the SQLitePCLRaw advisory leaves every project graph.
2. **Update the Avalonia 11 family together.** Test the coordinated 11.3.x line before considering Avalonia 12. The UI packages must stay version-aligned. Confirm the Tmds.DBus.Protocol advisory clears, then run all three platform builds plus UI tests and live smoke checks.
3. **Take independent low-coupling updates.** Test CommunityToolkit.Mvvm 8.3.2 to 8.4.2, System.Reactive 6.0.1 to 6.1.0, Serilog 4.2.0 to 4.4.0, Microsoft.NET.Test.Sdk 17.12.0 to 17.14.1, and xUnit 2.9.2 to 2.9.3 as separate changes or tightly related package-family changes.
4. **Move LiveCharts from release candidate to stable.** Test 2.0.0-rc4 to 2.0.5 separately. Exercise every chart view and all theme transitions because the current dependency is a prerelease build.
5. **Defer major upgrades to isolated projects.** Avalonia 12.1.x, System.Reactive 7, SkiaSharp 4.152.x, Spectre.Console 0.5x, Serilog sinks 7, the test SDK 18, and xUnit runner 4 need separate compatibility work. SkiaSharp must be tested with LiveCharts and every native asset package as one aligned family.
6. **Hold FluentAssertions 6.12.2 pending a license decision.** Version 8 is a major change with commercial-use licensing considerations. Josh must approve that constraint before technical migration work begins.

## Compatibility debt

`EnablePageEngine` remains in `AppSettings` only so existing settings files deserialize across the 1.0 transition. Nothing reads the field. Keep it through the full 1.0 compatibility horizon, then remove it in the first planned settings-schema break after 1.x migration telemetry or release support no longer requires pre-1.0 settings files.

## Verification for each queue item

1. Restore and build `NexusMonitor.sln` in Release configuration.
2. Run the complete test suite on Windows, Linux, and macOS.
3. Run `dotnet list NexusMonitor.sln package --vulnerable --include-transitive`.
4. Record the resolved graph and the live checks specific to the changed package family.
