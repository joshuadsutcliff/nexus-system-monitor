# Snapshot & Compare Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist every completed disk scan as a compact SQLite snapshot and let users diff any two snapshots of the same root — in the Disk Analyzer UI and via new `nexus disk` CLI commands.

**Architecture:** New `Snapshots/` unit inside `NexusMonitor.DiskAnalyzer` (store + differ + formatter, pure managed code) writing to a separate `disk-snapshots.db` (Microsoft.Data.Sqlite, WAL) that mirrors `MetricsDatabase` conventions. Threshold-hybrid granularity: every directory row + every file ≥ threshold; smaller files roll into per-directory aggregates. UI adds a fourth Disk Analyzer mode ("Snapshots") following the existing mode-toggle pattern; CLI adds a `disk` branch following the existing Spectre pattern.

**Tech Stack:** .NET 8, Microsoft.Data.Sqlite 8.0.1, CommunityToolkit.Mvvm, Avalonia (existing DataGrid patterns), Spectre.Console CLI, xunit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-07-19-snapshot-compare-design.md` (rev 2).
**Plan rev 2 (2026-07-19):** amended after three-way review (Kiro w/ repo access + nemotron slim brief + conductor empirical tests): retention cap loop measures live bytes with a single post-loop VACUUM; targeted `ClearPool` instead of process-wide `ClearAllPools`; dedicated read connection (SqliteConnection is not thread-safe); simplified retention SQL; + 7 smaller review tweaks. The multi-statement `ExecuteScalar` "bug" claimed in review was disproven by a live driver test — those patterns stand. Two deliberate consolidations vs. the spec's component list, both invisible at the API boundary: (1) `SnapshotWriter`/`SnapshotReader` are private concerns of one `SnapshotStore` class implementing `ISnapshotStore` — two files of ceremony saved, same interface; (2) the spec's nested `diskSnapshots:{}` settings block becomes four flat `Snapshot*` properties on `AppSettings`, because the repo's settings convention is flat props under comment banners (`AppSettings.cs:53-56`), not nested objects.

## Global Constraints

- Repo: `~/Github/nexus-system-monitor`, branch `feat/snapshot-compare` off `main`. **All `gh`/push operations require `gh auth switch --user joshuadsutcliff` first, switch back to brass458 after. A 403/404 on push means wrong account — switch, never retry blind.**
- Workers executing in worktrees: create your own worktree **of the target repo** (`git -C ~/Github/nexus-system-monitor worktree add …`), never of the session cwd.
- TDD: every behavior lands test-first; watch the test fail before implementing. Suite currently at 962 executed cases — must not regress; CI must be green on all three OSes before merge. **Merges happen only on Josh's explicit word.**
- Pure managed code only — no new P/Invoke (spec §9). Scanner files (`RecursiveScanner.cs`, `MftScanner.cs`) must NOT be modified.
- All timestamps stored UTC ISO-8601 (`"o"` format); displayed local.
- Snapshot defaults (spec §2/§4): threshold 1 MiB (1048576), retention 26/root, global cap 500 MB, feature enabled.
- Never touch the real `%AppData%/NexusMonitor/` in tests — temp-dir injection only (a real data-loss incident motivated this; see `SettingsServiceTests.cs:13-18`).
- Commit messages end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## File Structure

```
src/NexusMonitor.DiskAnalyzer/
  NexusMonitor.DiskAnalyzer.csproj        MODIFY: + Microsoft.Data.Sqlite 8.0.1
  Snapshots/PathKeys.cs                   CREATE: root normalization + name comparers
  Snapshots/SnapshotModels.cs             CREATE: SnapshotInfo, SnapshotNode, ChangeKind, DiffNode, DiffResult, SnapshotOptions
  Snapshots/ISnapshotStore.cs             CREATE: store interface
  Snapshots/SnapshotDatabase.cs           CREATE: connection + schema + corrupt-recovery (mirrors MetricsDatabase)
  Snapshots/SnapshotStore.cs              CREATE: save (threshold walk), list/load, delete, sweep, retention
  Snapshots/SnapshotDiffer.cs             CREATE: two-tree diff
  Snapshots/DiffFormatter.cs              CREATE: table/json/top formatting (CLI-facing, unit-tested)
src/NexusMonitor.CLI/
  NexusMonitor.CLI.csproj                 MODIFY: + ProjectReference DiskAnalyzer
  Program.cs                              MODIFY: register store + AddBranch("disk", …)
  Commands/Disk/DiskScanCommand.cs        CREATE
  Commands/Disk/DiskSnapshotsListCommand.cs  CREATE
  Commands/Disk/DiskSnapshotsDeleteCommand.cs CREATE
  Commands/Disk/DiskDiffCommand.cs        CREATE
src/NexusMonitor.UI/
  ViewModels/DiskAnalyzerViewModel.cs     MODIFY: auto-save hook, Snapshots mode, diff rows
  Views/DiskAnalyzerView.axaml            MODIFY: Snapshots mode tab + panels
  App.axaml.cs                            MODIFY: register ISnapshotStore in UI container
src/NexusMonitor.Core/
  Models/AppSettings.cs                   MODIFY: + 4 Snapshot* props
tests/NexusMonitor.DiskAnalyzer.Tests/    CREATE (new project, added to .sln)
  NexusMonitor.DiskAnalyzer.Tests.csproj
  PathKeysTests.cs · SnapshotStoreTests.cs · RetentionTests.cs
  SnapshotDifferTests.cs · DiffFormatterTests.cs · TestTrees.cs (helpers)
README.md                                 MODIFY: feature section (final task)
```

Dependency note: `NexusMonitor.DiskAnalyzer` already references `NexusMonitor.Core`; the Sqlite package is added to DiskAnalyzer explicitly (don't rely on transitive flow). The CLI gains a direct DiskAnalyzer reference. `Hosting` is untouched — the store is registered where it's consumed (UI container in `App.axaml.cs`, CLI container in `Program.cs`), matching how `DiskAnalyzerViewModel` itself is UI-registered today (`App.axaml.cs:650`).

---

### Task 1: Test project scaffold + PathKeys (normalization & comparers)

**Files:**
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/NexusMonitor.DiskAnalyzer.Tests.csproj`
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/PathKeysTests.cs`
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/PathKeys.cs`
- Modify: `nexus-system-monitor.sln` (via `dotnet sln add`)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `PathKeys.NormalizeDisplay(string) : string`, `PathKeys.ToRootKey(string) : string`, `PathKeys.NamesAreCaseInsensitive : bool`, `PathKeys.NameComparer : StringComparer` — used by Tasks 3, 5, 6.

- [ ] **Step 1: Create the test project**

```bash
cd ~/Github/nexus-system-monitor
mkdir -p tests/NexusMonitor.DiskAnalyzer.Tests
```

`tests/NexusMonitor.DiskAnalyzer.Tests/NexusMonitor.DiskAnalyzer.Tests.csproj` (mirrors `NexusMonitor.Core.Tests.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentAssertions" Version="6.12.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\NexusMonitor.DiskAnalyzer\NexusMonitor.DiskAnalyzer.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln add tests/NexusMonitor.DiskAnalyzer.Tests/NexusMonitor.DiskAnalyzer.Tests.csproj
```

- [ ] **Step 2: Write failing PathKeys tests**

`tests/NexusMonitor.DiskAnalyzer.Tests/PathKeysTests.cs`:

```csharp
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class PathKeysTests
{
    [Fact]
    public void NormalizeDisplay_StripsTrailingSeparator()
    {
        var baseDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        PathKeys.NormalizeDisplay(baseDir + Path.DirectorySeparatorChar)
            .Should().Be(baseDir);
    }

    [Fact]
    public void NormalizeDisplay_KeepsRootSeparator()
    {
        // A filesystem root ("/" or "C:\") must not be stripped to empty.
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        PathKeys.NormalizeDisplay(root).Should().Be(root);
    }

    [Fact]
    public void ToRootKey_FoldsCase_OnCaseInsensitivePlatforms()
    {
        var a = PathKeys.ToRootKey(Path.Combine(Path.GetTempPath(), "MyData"));
        var b = PathKeys.ToRootKey(Path.Combine(Path.GetTempPath(), "mydata"));
        if (PathKeys.NamesAreCaseInsensitive) a.Should().Be(b);
        else                                  a.Should().NotBe(b);
    }

    [Fact]
    public void ToRootKey_SameForTrailingSlashVariants()
    {
        var p = Path.Combine(Path.GetTempPath(), "SnapRoot");
        PathKeys.ToRootKey(p + Path.DirectorySeparatorChar).Should().Be(PathKeys.ToRootKey(p));
    }

    [Fact]
    public void NameComparer_MatchesPlatformRule()
    {
        var equal = PathKeys.NameComparer.Equals("README.md", "readme.md");
        equal.Should().Be(PathKeys.NamesAreCaseInsensitive);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests -v q`
Expected: build FAILS — `PathKeys` does not exist.

- [ ] **Step 4: Implement PathKeys**

`src/NexusMonitor.DiskAnalyzer/Snapshots/PathKeys.cs`:

```csharp
namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// Path normalization and name-equality rules for snapshot grouping and diffing.
/// Spec §3.1: root_path keeps display casing; root_key is the case-folded grouping key.
/// Spec §5: names match ordinal-ignore-case on Windows/macOS, ordinal on Linux.
/// </summary>
public static class PathKeys
{
    public static bool NamesAreCaseInsensitive =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer NameComparer =>
        NamesAreCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Canonical absolute path, trailing separators stripped (except filesystem roots).</summary>
    public static string NormalizeDisplay(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        while (full.Length > root.Length &&
               (full[^1] == System.IO.Path.DirectorySeparatorChar ||
                full[^1] == System.IO.Path.AltDirectorySeparatorChar))
        {
            full = full[..^1];
        }
        return full;
    }

    /// <summary>Grouping key: normalized path, case-folded on case-insensitive platforms.</summary>
    public static string ToRootKey(string path)
    {
        var norm = NormalizeDisplay(path);
        return NamesAreCaseInsensitive ? norm.ToUpperInvariant() : norm;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests -v q`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add tests/NexusMonitor.DiskAnalyzer.Tests src/NexusMonitor.DiskAnalyzer/Snapshots/PathKeys.cs nexus-system-monitor.sln
git commit -m "feat(snapshots): test project scaffold + path normalization keys"
```

---

### Task 2: Snapshot models + SnapshotDatabase (schema, pragmas, corrupt recovery)

**Files:**
- Modify: `src/NexusMonitor.DiskAnalyzer/NexusMonitor.DiskAnalyzer.csproj`
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotModels.cs`
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotDatabase.cs`
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/SnapshotDatabaseTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `SnapshotDatabase(string dbPath)` with `.Connection : SqliteConnection` (writes), `.ReadConnection : SqliteConnection` (reads — WAL concurrent-reader; SqliteConnection is not thread-safe so reads never share the write connection), `.GetDatabaseSizeBytes() : long`, `.GetLiveSizeBytes() : long`, `.Checkpoint()`, `IDisposable`; static `SnapshotDatabase.OpenOrRecover(string dbPath, out bool recovered) : SnapshotDatabase`. Model types: `SnapshotInfo` (record: `Id, RootPath, RootKey, CreatedAtUtc, Scanner, FileSystem, VolumeTotal, VolumeFree, TotalSize, TotalFiles, TotalDirs, ThresholdBytes, AppVersion`), `SnapshotNode` (class: `Id, ParentId, Name, IsDirectory, Size, AllocatedSize, FileCount, FolderCount, LastModified, Created, LastAccessed, SmallFilesSize, SmallFilesCount`), `SnapshotOptions` (record: `ThresholdBytes, RetentionPerRoot, MaxDbSizeBytes`), enum `ChangeKind { Added, Removed, Grown, Shrunk, Unchanged }`, `DiffNode`, `DiffResult` — used by every later task.

- [ ] **Step 1: Add the Sqlite package reference**

In `src/NexusMonitor.DiskAnalyzer/NexusMonitor.DiskAnalyzer.csproj`, add to the existing package `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.1" />
```

(Same version Core pins; explicit rather than transitive.)

- [ ] **Step 2: Write the model types (no test needed — data only)**

`src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotModels.cs`:

```csharp
namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>Snapshot header row. Volume/filesystem fields captured at scan time — cannot be re-captured later (spec §3.1).</summary>
public sealed record SnapshotInfo(
    long Id,
    string RootPath,
    string RootKey,
    DateTime CreatedAtUtc,
    string Scanner,
    string? FileSystem,
    long VolumeTotal,
    long VolumeFree,
    long TotalSize,
    long TotalFiles,
    long TotalDirs,
    long ThresholdBytes,
    string? AppVersion);

/// <summary>One persisted node. Directories carry rollups + sub-threshold aggregates; files ≥ threshold are individual rows.</summary>
public sealed class SnapshotNode
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public DateTime? LastModified { get; set; }
    public DateTime? Created { get; set; }        // schema insurance; never diffed in v1 (spec §3.1)
    public DateTime? LastAccessed { get; set; }   // schema insurance; never diffed in v1 (spec §3.1)
    public long SmallFilesSize { get; set; }      // dirs only
    public long SmallFilesCount { get; set; }     // dirs only
}

public sealed record SnapshotOptions(
    long ThresholdBytes = 1_048_576,
    int RetentionPerRoot = 26,
    long MaxDbSizeBytes = 500L * 1024 * 1024);

public enum ChangeKind { Added, Removed, Grown, Shrunk, Unchanged }

/// <summary>Diff output node. Children are sorted by |Delta| descending (spec §5 rename-pair clustering).</summary>
public sealed class DiffNode
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public ChangeKind Kind { get; set; }
    public long? SizeBefore { get; set; }
    public long? SizeAfter { get; set; }
    public long Delta { get; set; }
    public long SmallFilesDelta { get; set; }     // dirs only
    public List<DiffNode> Children { get; } = new();
}

public sealed class DiffResult
{
    public required SnapshotInfo Older { get; init; }
    public required SnapshotInfo Newer { get; init; }
    public required DiffNode Root { get; init; }
    public long EffectiveThreshold { get; init; }
    public bool ThresholdMismatch { get; init; }
    public bool NamesMatchedCaseInsensitively { get; init; }
}
```

- [ ] **Step 3: Write failing SnapshotDatabase tests**

`tests/NexusMonitor.DiskAnalyzer.Tests/SnapshotDatabaseTests.cs`:

```csharp
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class SnapshotDatabaseTests : IDisposable
{
    private readonly string _dir;
    private string DbPath => Path.Combine(_dir, "disk-snapshots.db");

    public SnapshotDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Constructor_CreatesSchemaAndFile()
    {
        using var db = new SnapshotDatabase(DbPath);
        File.Exists(DbPath).Should().BeTrue();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
        tables.Should().Contain(new[] { "meta", "nodes", "snapshots" });
    }

    [Fact]
    public void OpenOrRecover_RenamesCorruptFileAndRecreates()
    {
        File.WriteAllText(DbPath, "this is not a sqlite database, not even close");
        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeTrue();
        Directory.GetFiles(_dir, "disk-snapshots.db.corrupt-*").Should().HaveCount(1);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(0);
    }

    [Fact]
    public void OpenOrRecover_HealthyDbIsNotRecovered()
    {
        new SnapshotDatabase(DbPath).Dispose();
        using var db = SnapshotDatabase.OpenOrRecover(DbPath, out var recovered);
        recovered.Should().BeFalse();
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests --filter SnapshotDatabaseTests -v q`
Expected: build FAILS — `SnapshotDatabase` does not exist.

- [ ] **Step 5: Implement SnapshotDatabase**

`src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotDatabase.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// Owns the disk-snapshots.db connection. Mirrors MetricsDatabase conventions
/// (WAL, NORMAL sync, busy timeout, meta version table) but is a separate file
/// by design — retention/vacuum/schema evolve independently and corruption here
/// can never damage metrics history (spec §3).
/// </summary>
public sealed class SnapshotDatabase : IDisposable
{
    public const int SchemaVersion = 1;

    public SqliteConnection Connection { get; }
    private readonly string _dbPath;

    public SnapshotDatabase(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Connection = new SqliteConnection($"Data Source={dbPath}");
        Connection.Open();
        try
        {
            ConfigurePragmas();
            InitSchema();
            // Second connection for reads: WAL supports concurrent readers with one
            // writer, and SqliteConnection itself is NOT thread-safe — UI-thread reads
            // must never share the write connection with background saves (review
            // consensus 2026-07-19; mirrors the MetricsStore two-connection pattern).
            ReadConnection = new SqliteConnection($"Data Source={dbPath}");
            ReadConnection.Open();
        }
        catch
        {
            Connection.Dispose(); // never leak an open handle on a corrupt file
            throw;
        }
    }

    public SqliteConnection ReadConnection { get; private set; } = null!;

    /// <summary>Spec §8: corrupt DB → rename aside, recreate empty, report recovery.</summary>
    public static SnapshotDatabase OpenOrRecover(string dbPath, out bool recovered)
    {
        recovered = false;
        SnapshotDatabase? db = null;
        try
        {
            db = new SnapshotDatabase(dbPath);
            // A file can open yet still be corrupt; force a real read.
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
            cmd.ExecuteScalar();
            return db;
        }
        catch (SqliteException)
        {
            // Release only THIS file's handles. ClearAllPools() is process-wide and
            // would disturb the metrics DB's pooling (review finding, 2026-07-19).
            db?.Dispose();
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));
            var aside = $"{dbPath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Move(dbPath, aside, overwrite: true);
            recovered = true;
            return new SnapshotDatabase(dbPath);
        }
    }

    private void ConfigurePragmas()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode  = WAL;
            PRAGMA synchronous   = NORMAL;
            PRAGMA busy_timeout  = 5000;
            PRAGMA foreign_keys  = OFF;";
        cmd.ExecuteNonQuery();
    }

    private void InitSchema()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                root_path       TEXT NOT NULL,
                root_key        TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                scanner         TEXT NOT NULL,
                file_system     TEXT,
                volume_total    INTEGER,
                volume_free     INTEGER,
                total_size      INTEGER,
                total_files     INTEGER,
                total_dirs      INTEGER,
                threshold_bytes INTEGER NOT NULL,
                app_version     TEXT,
                complete        INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_snap_rootkey ON snapshots(root_key, created_at);
            CREATE TABLE IF NOT EXISTS nodes (
                snapshot_id       INTEGER NOT NULL,
                id                INTEGER NOT NULL,
                parent_id         INTEGER,
                name              TEXT NOT NULL,
                is_dir            INTEGER NOT NULL,
                size              INTEGER,
                allocated_size    INTEGER,
                file_count        INTEGER,
                folder_count      INTEGER,
                last_modified     TEXT,
                created           TEXT,
                last_accessed     TEXT,
                small_files_size  INTEGER,
                small_files_count INTEGER,
                PRIMARY KEY (snapshot_id, id)
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_parent ON nodes(snapshot_id, parent_id);
            INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '1');";
        cmd.ExecuteNonQuery();
    }

    public long GetDatabaseSizeBytes()
    {
        var f = new FileInfo(_dbPath);
        return f.Exists ? f.Length : 0;
    }

    /// <summary>Live (non-free) bytes. Unlike file length, this shrinks as rows are
    /// deleted, without needing VACUUM — the retention cap loop depends on that.</summary>
    public long GetLiveSizeBytes()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = @"SELECT ((SELECT * FROM pragma_page_count) -
                                    (SELECT * FROM pragma_freelist_count))
                                 * (SELECT * FROM pragma_page_size);";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Checkpoint()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Checkpoint(); } catch { /* best effort */ }
        ReadConnection?.Dispose();
        Connection.Dispose();
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests --filter SnapshotDatabaseTests -v q`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/NexusMonitor.DiskAnalyzer tests/NexusMonitor.DiskAnalyzer.Tests
git commit -m "feat(snapshots): models + SQLite database with corrupt recovery"
```

---

### Task 3: SnapshotStore — save with threshold walk, list, load, delete, incomplete sweep

**Files:**
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/ISnapshotStore.cs`
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotStore.cs`
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/TestTrees.cs`
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/SnapshotStoreTests.cs`

**Interfaces:**
- Consumes: `SnapshotDatabase`, `PathKeys`, model types (Tasks 1–2); `ScanResult`/`DiskNode` from `NexusMonitor.DiskAnalyzer.Models`.
- Produces (`ISnapshotStore`, used by Tasks 5–10):

```csharp
public interface ISnapshotStore : IDisposable
{
    /// <summary>Persist a completed scan. Returns the new snapshot id. Applies retention afterwards.</summary>
    long Save(ScanResult result, SnapshotOptions options, string? appVersion = null);
    IReadOnlyList<SnapshotInfo> ListSnapshots(string? rootPath = null); // newest first; null = all roots
    SnapshotInfo? GetSnapshot(long id);
    IReadOnlyList<SnapshotNode> LoadNodes(long snapshotId);
    void Delete(long snapshotId);
    /// <summary>Delete rows of any snapshot with complete = 0 (crash recovery). Call at startup.</summary>
    int SweepIncomplete();
    long GetStoreSizeBytes();
    bool WasRecovered { get; } // true when the DB file was corrupt and recreated
}
```

- [ ] **Step 0: Add InternalsVisibleTo (tests touch the internal `Database` property)**

In `src/NexusMonitor.DiskAnalyzer/NexusMonitor.DiskAnalyzer.csproj`, add a new item group (SDK-native form — avoids any duplicate-assembly-attribute risk from generated AssemblyInfo):

```xml
<ItemGroup>
  <InternalsVisibleTo Include="NexusMonitor.DiskAnalyzer.Tests" />
</ItemGroup>
```

- [ ] **Step 1: Write the test-tree helper**

`tests/NexusMonitor.DiskAnalyzer.Tests/TestTrees.cs`:

```csharp
using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Tests;

/// <summary>Builds in-memory DiskNode trees — no real filesystem needed (spec §10).</summary>
public static class TestTrees
{
    public static DiskNode Dir(string name, params DiskNode[] children)
    {
        var d = new DiskNode { Name = name, IsDirectory = true };
        foreach (var c in children)
        {
            c.Parent = d;
            d.Children.Add(c);
            d.Size += c.Size;
            d.AllocatedSize += c.AllocatedSize;
            d.FileCount += c.IsDirectory ? c.FileCount : 1;
            d.FolderCount += c.IsDirectory ? c.FolderCount + 1 : 0;
        }
        return d;
    }

    public static DiskNode File(string name, long size) => new()
    {
        Name = name, IsDirectory = false, Size = size, AllocatedSize = size,
        LastModified = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static ScanResult Result(DiskNode root, string scannedPath) => new()
    {
        Root = root, ScannedPath = scannedPath,
        TotalFiles = root.FileCount, TotalFolders = root.FolderCount, TotalSize = root.Size,
        FileSystem = "TESTFS", VolumeTotal = 1_000_000_000, VolumeFree = 400_000_000,
    };
}
```

- [ ] **Step 2: Write failing SnapshotStore tests**

`tests/NexusMonitor.DiskAnalyzer.Tests/SnapshotStoreTests.cs`:

```csharp
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;
    private static readonly SnapshotOptions Opts = new(ThresholdBytes: 1000);

    public SnapshotStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SnapshotStore(Path.Combine(_dir, "disk-snapshots.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ScanRoot => Path.Combine(_dir, "ScanRoot");

    [Fact]
    public void Save_RoundTrips_MetadataAndVolumeInfo()
    {
        var tree = TestTrees.Dir("ScanRoot", TestTrees.File("big.bin", 5000));
        var id = _store.Save(TestTrees.Result(tree, ScanRoot), Opts, appVersion: "1.0-test");

        var info = _store.GetSnapshot(id)!;
        info.RootPath.Should().Be(PathKeys.NormalizeDisplay(ScanRoot));
        info.RootKey.Should().Be(PathKeys.ToRootKey(ScanRoot));
        info.FileSystem.Should().Be("TESTFS");
        info.VolumeTotal.Should().Be(1_000_000_000);
        info.VolumeFree.Should().Be(400_000_000);
        info.ThresholdBytes.Should().Be(1000);
        info.AppVersion.Should().Be("1.0-test");
    }

    [Fact]
    public void Save_AppliesThreshold_SmallFilesRollUpPerDirectory()
    {
        var tree = TestTrees.Dir("ScanRoot",
            TestTrees.File("big.bin", 5000),
            TestTrees.File("tiny1.txt", 10),
            TestTrees.File("tiny2.txt", 20),
            TestTrees.Dir("sub",
                TestTrees.File("also-big.bin", 2000),
                TestTrees.File("also-tiny.txt", 5)));
        var id = _store.Save(TestTrees.Result(tree, ScanRoot), Opts);

        var nodes = _store.LoadNodes(id);
        var names = nodes.Select(n => n.Name).ToList();
        names.Should().Contain(new[] { "ScanRoot", "big.bin", "sub", "also-big.bin" });
        names.Should().NotContain(new[] { "tiny1.txt", "tiny2.txt", "also-tiny.txt" });

        var root = nodes.Single(n => n.ParentId == null);
        root.SmallFilesSize.Should().Be(30);   // tiny1 + tiny2
        root.SmallFilesCount.Should().Be(2);
        root.Size.Should().Be(7035);           // dir rollup keeps the TRUE total

        var sub = nodes.Single(n => n.Name == "sub");
        sub.SmallFilesSize.Should().Be(5);
        sub.SmallFilesCount.Should().Be(1);
        sub.Size.Should().Be(2005);
    }

    [Fact]
    public void Save_FileExactlyAtThreshold_IsStoredIndividually()
    {
        var tree = TestTrees.Dir("ScanRoot", TestTrees.File("edge.bin", 1000));
        var id = _store.Save(TestTrees.Result(tree, ScanRoot), Opts);
        _store.LoadNodes(id).Select(n => n.Name).Should().Contain("edge.bin");
    }

    [Fact]
    public void ListSnapshots_GroupsByRootKey_CasingVariantsAreOneRoot()
    {
        var t1 = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var t2 = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 6000));
        _store.Save(TestTrees.Result(t1, ScanRoot), Opts);
        _store.Save(TestTrees.Result(t2, ScanRoot.ToUpperInvariant()), Opts);

        var forRoot = _store.ListSnapshots(ScanRoot);
        if (PathKeys.NamesAreCaseInsensitive) forRoot.Should().HaveCount(2);
        else                                  forRoot.Should().HaveCount(1);
    }

    [Fact]
    public void ListSnapshots_NewestFirst()
    {
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var id1 = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        var id2 = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        _store.ListSnapshots(ScanRoot).Select(s => s.Id).First().Should().Be(id2);
    }

    [Fact]
    public void Delete_RemovesSnapshotAndNodes()
    {
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var id = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        _store.Delete(id);
        _store.GetSnapshot(id).Should().BeNull();
        _store.LoadNodes(id).Should().BeEmpty();
    }

    [Fact]
    public void SweepIncomplete_RemovesOnlyIncompleteSnapshots()
    {
        var t = TestTrees.Dir("ScanRoot", TestTrees.File("a.bin", 5000));
        var goodId = _store.Save(TestTrees.Result(t, ScanRoot), Opts);
        // Forge an incomplete snapshot the way a crash would leave one.
        using (var cmd = _store.Database.Connection.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO snapshots
                (root_path, root_key, created_at, scanner, threshold_bytes, complete)
                VALUES ('x', 'x', '2026-07-19T00:00:00.0000000Z', 'recursive', 1000, 0)";
            cmd.ExecuteNonQuery();
        }
        _store.SweepIncomplete().Should().Be(1);
        _store.GetSnapshot(goodId).Should().NotBeNull();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests --filter SnapshotStoreTests -v q`
Expected: build FAILS — `SnapshotStore` does not exist.

- [ ] **Step 4: Implement ISnapshotStore + SnapshotStore**

`src/NexusMonitor.DiskAnalyzer/Snapshots/ISnapshotStore.cs`: the interface exactly as shown in **Interfaces → Produces** above (copy verbatim, namespace `NexusMonitor.DiskAnalyzer.Snapshots`, plus `using NexusMonitor.DiskAnalyzer.Models;`).

`src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotStore.cs`:

```csharp
using Microsoft.Data.Sqlite;
using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// ISnapshotStore over SnapshotDatabase. Writer/reader are private concerns of
/// this one class (plan note: consolidation of the spec's SnapshotWriter/Reader).
/// All writes are transactional; 'complete' is set last so a crash can never
/// leave a snapshot that looks valid (spec §4/§8).
/// </summary>
public sealed class SnapshotStore : ISnapshotStore
{
    internal SnapshotDatabase Database { get; }
    public bool WasRecovered { get; }
    private readonly object _writeLock = new();

    public SnapshotStore(string dbPath)
    {
        Database = SnapshotDatabase.OpenOrRecover(dbPath, out var recovered);
        WasRecovered = recovered;
    }

    public long Save(ScanResult result, SnapshotOptions options, string? appVersion = null)
    {
        lock (_writeLock)
        {
            long snapshotId;
            using (var tx = Database.Connection.BeginTransaction())
            {
                using (var cmd = Database.Connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO snapshots
                        (root_path, root_key, created_at, scanner, file_system,
                         volume_total, volume_free, total_size, total_files, total_dirs,
                         threshold_bytes, app_version, complete)
                        VALUES ($rp, $rk, $ca, $sc, $fs, $vt, $vf, $ts, $tf, $td, $th, $av, 0);
                        SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$rp", PathKeys.NormalizeDisplay(result.ScannedPath));
                    cmd.Parameters.AddWithValue("$rk", PathKeys.ToRootKey(result.ScannedPath));
                    cmd.Parameters.AddWithValue("$ca", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$sc", OperatingSystem.IsWindows() ? "mft-or-recursive" : "recursive");
                    cmd.Parameters.AddWithValue("$fs", (object?)NullIfEmpty(result.FileSystem) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$vt", result.VolumeTotal);
                    cmd.Parameters.AddWithValue("$vf", result.VolumeFree);
                    cmd.Parameters.AddWithValue("$ts", result.TotalSize);
                    cmd.Parameters.AddWithValue("$tf", result.TotalFiles);
                    cmd.Parameters.AddWithValue("$td", result.TotalFolders);
                    cmd.Parameters.AddWithValue("$th", options.ThresholdBytes);
                    cmd.Parameters.AddWithValue("$av", (object?)appVersion ?? DBNull.Value);
                    snapshotId = Convert.ToInt64(cmd.ExecuteScalar());
                }

                InsertNodes(tx, snapshotId, result.Root, options.ThresholdBytes);

                using (var done = Database.Connection.CreateCommand())
                {
                    done.Transaction = tx;
                    done.CommandText = "UPDATE snapshots SET complete = 1 WHERE id = $id";
                    done.Parameters.AddWithValue("$id", snapshotId);
                    done.ExecuteNonQuery();
                }
                tx.Commit();
            }

            ApplyRetention(options);
            return snapshotId;
        }
    }

    private void InsertNodes(SqliteTransaction tx, long snapshotId, DiskNode root, long threshold)
    {
        using var cmd = Database.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO nodes
            (snapshot_id, id, parent_id, name, is_dir, size, allocated_size,
             file_count, folder_count, last_modified, created, last_accessed,
             small_files_size, small_files_count)
            VALUES ($sid, $id, $pid, $n, $d, $s, $a, $fc, $dc, $lm, $cr, $la, $sfs, $sfc)";
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pId  = cmd.Parameters.Add("$id",  SqliteType.Integer);
        var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var pN   = cmd.Parameters.Add("$n",   SqliteType.Text);
        var pD   = cmd.Parameters.Add("$d",   SqliteType.Integer);
        var pS   = cmd.Parameters.Add("$s",   SqliteType.Integer);
        var pA   = cmd.Parameters.Add("$a",   SqliteType.Integer);
        var pFc  = cmd.Parameters.Add("$fc",  SqliteType.Integer);
        var pDc  = cmd.Parameters.Add("$dc",  SqliteType.Integer);
        var pLm  = cmd.Parameters.Add("$lm",  SqliteType.Text);
        var pCr  = cmd.Parameters.Add("$cr",  SqliteType.Text);
        var pLa  = cmd.Parameters.Add("$la",  SqliteType.Text);
        var pSfs = cmd.Parameters.Add("$sfs", SqliteType.Integer);
        var pSfc = cmd.Parameters.Add("$sfc", SqliteType.Integer);
        pSid.Value = snapshotId;

        long nextId = 0;
        // Iterative DFS: (node, parentRowId). Only dirs and files >= threshold get rows.
        var stack = new Stack<(DiskNode Node, long? ParentRowId)>();
        stack.Push((root, null));
        while (stack.Count > 0)
        {
            var (node, parentRowId) = stack.Pop();
            var rowId = nextId++;
            long smallSize = 0, smallCount = 0;
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    if (!child.IsDirectory && child.Size < threshold)
                    {
                        smallSize += child.Size;
                        smallCount++;
                    }
                }
            }

            pId.Value  = rowId;
            pPid.Value = (object?)parentRowId ?? DBNull.Value;
            pN.Value   = node.Name;
            pD.Value   = node.IsDirectory ? 1 : 0;
            pS.Value   = node.Size;
            pA.Value   = node.AllocatedSize;
            pFc.Value  = node.FileCount;
            pDc.Value  = node.FolderCount;
            pLm.Value  = IsoOrNull(node.LastModified);
            pCr.Value  = IsoOrNull(node.Created);
            pLa.Value  = IsoOrNull(node.LastAccessed);
            pSfs.Value = smallSize;
            pSfc.Value = smallCount;
            cmd.ExecuteNonQuery();

            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    if (child.IsDirectory || child.Size >= threshold)
                        stack.Push((child, rowId));
                }
            }
        }
    }

    private static object IsoOrNull(DateTime dt) =>
        dt == default ? DBNull.Value : dt.ToUniversalTime().ToString("o");

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    public IReadOnlyList<SnapshotInfo> ListSnapshots(string? rootPath = null)
    {
        using var cmd = Database.ReadConnection.CreateCommand();
        cmd.CommandText = @"SELECT id, root_path, root_key, created_at, scanner, file_system,
                                   volume_total, volume_free, total_size, total_files, total_dirs,
                                   threshold_bytes, app_version
                            FROM snapshots WHERE complete = 1"
                          + (rootPath != null ? " AND root_key = $rk" : "")
                          + " ORDER BY created_at DESC, id DESC";
        if (rootPath != null) cmd.Parameters.AddWithValue("$rk", PathKeys.ToRootKey(rootPath));
        var list = new List<SnapshotInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadInfo(r));
        return list;
    }

    public SnapshotInfo? GetSnapshot(long id)
    {
        using var cmd = Database.ReadConnection.CreateCommand();
        cmd.CommandText = @"SELECT id, root_path, root_key, created_at, scanner, file_system,
                                   volume_total, volume_free, total_size, total_files, total_dirs,
                                   threshold_bytes, app_version
                            FROM snapshots WHERE id = $id AND complete = 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadInfo(r) : null;
    }

    private static SnapshotInfo ReadInfo(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        RootPath: r.GetString(1),
        RootKey: r.GetString(2),
        CreatedAtUtc: DateTime.Parse(r.GetString(3), null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        Scanner: r.GetString(4),
        FileSystem: r.IsDBNull(5) ? null : r.GetString(5),
        VolumeTotal: r.IsDBNull(6) ? 0 : r.GetInt64(6),
        VolumeFree: r.IsDBNull(7) ? 0 : r.GetInt64(7),
        TotalSize: r.IsDBNull(8) ? 0 : r.GetInt64(8),
        TotalFiles: r.IsDBNull(9) ? 0 : r.GetInt64(9),
        TotalDirs: r.IsDBNull(10) ? 0 : r.GetInt64(10),
        ThresholdBytes: r.GetInt64(11),
        AppVersion: r.IsDBNull(12) ? null : r.GetString(12));

    public IReadOnlyList<SnapshotNode> LoadNodes(long snapshotId)
    {
        using var cmd = Database.ReadConnection.CreateCommand();
        cmd.CommandText = @"SELECT id, parent_id, name, is_dir, size, allocated_size,
                                   file_count, folder_count, last_modified, created, last_accessed,
                                   small_files_size, small_files_count
                            FROM nodes WHERE snapshot_id = $sid";
        cmd.Parameters.AddWithValue("$sid", snapshotId);
        var list = new List<SnapshotNode>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SnapshotNode
            {
                Id = r.GetInt64(0),
                ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
                Name = r.GetString(2),
                IsDirectory = r.GetInt64(3) == 1,
                Size = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                AllocatedSize = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                FileCount = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                FolderCount = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                LastModified = ParseIso(r, 8),
                Created = ParseIso(r, 9),
                LastAccessed = ParseIso(r, 10),
                SmallFilesSize = r.IsDBNull(11) ? 0 : r.GetInt64(11),
                SmallFilesCount = r.IsDBNull(12) ? 0 : r.GetInt64(12),
            });
        }
        return list;
    }

    private static DateTime? ParseIso(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), null,
            System.Globalization.DateTimeStyles.RoundtripKind);

    public void Delete(long snapshotId)
    {
        lock (_writeLock)
        {
            using var tx = Database.Connection.BeginTransaction();
            using var cmd = Database.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"DELETE FROM nodes WHERE snapshot_id = $id;
                                DELETE FROM snapshots WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", snapshotId);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int SweepIncomplete()
    {
        lock (_writeLock)
        {
            using var tx = Database.Connection.BeginTransaction();
            using var cmd = Database.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"DELETE FROM nodes WHERE snapshot_id IN
                                    (SELECT id FROM snapshots WHERE complete = 0);
                                DELETE FROM snapshots WHERE complete = 0;
                                SELECT changes();";
            var removed = Convert.ToInt32(cmd.ExecuteScalar());
            tx.Commit();
            return removed;
        }
    }

    public long GetStoreSizeBytes() => Database.GetDatabaseSizeBytes();

    // ApplyRetention implemented in Task 5 — until then a no-op.
    private void ApplyRetention(SnapshotOptions options) { }

    public void Dispose() => Database.Dispose();
}
```

Note on the `scanner` column value: `ScanResult` does not carry which scanner produced it, and `MftScanner` silently falls back to `RecursiveScanner` when the MFT read fails — so on Windows the honest value is `"mft-or-recursive"`, not a confident `"mft"` that may be wrong (review finding). Do NOT modify `ScanResult` to add the real value — scanner files are out of bounds.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests -v q`
Expected: PASS (all tests so far).

- [ ] **Step 6: Commit**

```bash
git add src/NexusMonitor.DiskAnalyzer tests/NexusMonitor.DiskAnalyzer.Tests
git commit -m "feat(snapshots): SnapshotStore save/list/load/delete with threshold walk"
```

---

### Task 4: SnapshotDiffer

**Files:**
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotDiffer.cs`
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/SnapshotDifferTests.cs`

**Interfaces:**
- Consumes: `ISnapshotStore.GetSnapshot/LoadNodes`, `PathKeys.NameComparer`, model types.
- Produces: `SnapshotDiffer.Diff(ISnapshotStore store, long olderId, long newerId) : DiffResult` (static). Throws `InvalidOperationException` when ids are missing or roots differ. Used by Tasks 7–10.

- [ ] **Step 1: Write failing differ tests**

`tests/NexusMonitor.DiskAnalyzer.Tests/SnapshotDifferTests.cs`:

```csharp
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class SnapshotDifferTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;

    public SnapshotDifferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SnapshotStore(Path.Combine(_dir, "disk-snapshots.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Root => Path.Combine(_dir, "R");
    private static readonly SnapshotOptions Opts = new(ThresholdBytes: 1000);

    private long Snap(params NexusMonitor.DiskAnalyzer.Models.DiskNode[] children) =>
        _store.Save(TestTrees.Result(TestTrees.Dir("R", children), Root), Opts);

    [Fact]
    public void Diff_DetectsAddedRemovedGrownShrunk()
    {
        var older = Snap(
            TestTrees.File("stays.bin", 5000),
            TestTrees.File("grows.bin", 2000),
            TestTrees.File("shrinks.bin", 9000),
            TestTrees.File("leaves.bin", 3000));
        var newer = Snap(
            TestTrees.File("stays.bin", 5000),
            TestTrees.File("grows.bin", 6000),
            TestTrees.File("shrinks.bin", 1500),
            TestTrees.File("arrives.bin", 4000));

        var diff = SnapshotDiffer.Diff(_store, older, newer);
        var byName = diff.Root.Children.ToDictionary(c => c.Name);

        byName["grows.bin"].Kind.Should().Be(ChangeKind.Grown);
        byName["grows.bin"].Delta.Should().Be(4000);
        byName["shrinks.bin"].Kind.Should().Be(ChangeKind.Shrunk);
        byName["shrinks.bin"].Delta.Should().Be(-7500);
        byName["leaves.bin"].Kind.Should().Be(ChangeKind.Removed);
        byName["leaves.bin"].Delta.Should().Be(-3000);
        byName["arrives.bin"].Kind.Should().Be(ChangeKind.Added);
        byName["arrives.bin"].Delta.Should().Be(4000);
        byName.Should().NotContainKey("stays.bin"); // Unchanged excluded from output
    }

    [Fact]
    public void Diff_ChildrenSortedByAbsoluteDeltaDescending()
    {
        var older = Snap(TestTrees.File("small-change.bin", 5000), TestTrees.File("big-change.bin", 5000));
        var newer = Snap(TestTrees.File("small-change.bin", 5100), TestTrees.File("big-change.bin", 90000));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        diff.Root.Children.Select(c => c.Name).Should()
            .ContainInOrder("big-change.bin", "small-change.bin");
    }

    [Fact]
    public void Diff_SmallFilesRollupDelta_SurfacesOnDirectory()
    {
        var older = Snap(TestTrees.Dir("cache", TestTrees.File("t1", 100)));
        var newer = Snap(TestTrees.Dir("cache",
            TestTrees.File("t1", 100), TestTrees.File("t2", 300), TestTrees.File("t3", 200)));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        var cache = diff.Root.Children.Single(c => c.Name == "cache");
        cache.SmallFilesDelta.Should().Be(500);
        cache.Kind.Should().Be(ChangeKind.Grown);
    }

    [Fact]
    public void Diff_CaseRename_FollowsPlatformRule()
    {
        var older = Snap(TestTrees.File("README.bin", 5000));
        var newer = Snap(TestTrees.File("readme.bin", 5000));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        if (PathKeys.NamesAreCaseInsensitive)
            diff.Root.Children.Should().BeEmpty(); // same file, unchanged
        else
            diff.Root.Children.Should().HaveCount(2); // Removed + Added
        diff.NamesMatchedCaseInsensitively.Should().Be(PathKeys.NamesAreCaseInsensitive);
    }

    [Fact]
    public void Diff_MismatchedThresholds_UsesMaxAndFlags()
    {
        var t = TestTrees.Dir("R", TestTrees.File("mid.bin", 500));
        var older = _store.Save(TestTrees.Result(t, Root), new SnapshotOptions(ThresholdBytes: 100));
        var newer = _store.Save(TestTrees.Result(t, Root), new SnapshotOptions(ThresholdBytes: 1000));
        var diff = SnapshotDiffer.Diff(_store, older, newer);
        diff.ThresholdMismatch.Should().BeTrue();
        diff.EffectiveThreshold.Should().Be(1000);
        // mid.bin is a row only in the older snapshot; at effective threshold it must
        // NOT appear as a Removed file — it is re-aggregated instead.
        diff.Root.Children.Should().NotContain(c => c.Name == "mid.bin");
        // Full accounting: same file, same size, both sides at effective resolution —
        // the re-aggregated small-files delta must net to zero (review finding).
        diff.Root.SmallFilesDelta.Should().Be(0);
    }

    [Fact]
    public void Diff_DifferentRoots_Throws()
    {
        var a = _store.Save(TestTrees.Result(TestTrees.Dir("A", TestTrees.File("x", 5000)),
            Path.Combine(_dir, "A")), Opts);
        var b = _store.Save(TestTrees.Result(TestTrees.Dir("B", TestTrees.File("x", 5000)),
            Path.Combine(_dir, "B")), Opts);
        var act = () => SnapshotDiffer.Diff(_store, a, b);
        act.Should().Throw<InvalidOperationException>().WithMessage("*root*");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests --filter SnapshotDifferTests -v q`
Expected: build FAILS — `SnapshotDiffer` does not exist.

- [ ] **Step 3: Implement SnapshotDiffer**

`src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotDiffer.cs`:

```csharp
namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// Two-tree diff (spec §5). Walks both snapshot trees simultaneously, matching
/// children by name using the platform rule. Diffs logical size only. Unchanged
/// nodes are excluded from output. With mismatched thresholds, files below the
/// effective (max) threshold are re-aggregated into the small-files rollup so
/// the two sides compare at equal resolution.
/// </summary>
public static class SnapshotDiffer
{
    public static DiffResult Diff(ISnapshotStore store, long olderId, long newerId)
    {
        var older = store.GetSnapshot(olderId)
            ?? throw new InvalidOperationException($"Snapshot {olderId} not found.");
        var newer = store.GetSnapshot(newerId)
            ?? throw new InvalidOperationException($"Snapshot {newerId} not found.");
        if (!string.Equals(older.RootKey, newer.RootKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Snapshots have different roots ('{older.RootPath}' vs '{newer.RootPath}').");

        var threshold = Math.Max(older.ThresholdBytes, newer.ThresholdBytes);
        var oldSide = Sides.Build(store.LoadNodes(olderId), threshold);
        var newSide = Sides.Build(store.LoadNodes(newerId), threshold);

        var root = DiffDir(oldSide, oldSide.Root, newSide, newSide.Root, threshold);
        root ??= new DiffNode
        {
            Name = newSide.Root.Name, IsDirectory = true, Kind = ChangeKind.Unchanged,
            SizeBefore = oldSide.Root.Size, SizeAfter = newSide.Root.Size,
        };

        return new DiffResult
        {
            Older = older, Newer = newer, Root = root,
            EffectiveThreshold = threshold,
            ThresholdMismatch = older.ThresholdBytes != newer.ThresholdBytes,
            NamesMatchedCaseInsensitively = PathKeys.NamesAreCaseInsensitive,
        };
    }

    /// <summary>One snapshot side: node lookup + children-by-parent, re-aggregated to the effective threshold.</summary>
    private sealed class Sides
    {
        public SnapshotNode Root = null!;
        public ILookup<long, SnapshotNode> ChildrenByParent = null!;
        public Dictionary<long, (long Size, long Count)> ExtraSmall = new(); // re-aggregated per dir id

        public static Sides Build(IReadOnlyList<SnapshotNode> nodes, long effectiveThreshold)
        {
            var s = new Sides
            {
                Root = nodes.Single(n => n.ParentId == null),
                ChildrenByParent = nodes.Where(n => n.ParentId != null)
                                        .ToLookup(n => n.ParentId!.Value),
            };
            foreach (var n in nodes)
            {
                if (!n.IsDirectory && n.Size < effectiveThreshold && n.ParentId is long pid)
                {
                    var cur = s.ExtraSmall.TryGetValue(pid, out var v) ? v : (0L, 0L);
                    s.ExtraSmall[pid] = (cur.Item1 + n.Size, cur.Item2 + 1);
                }
            }
            return s;
        }

        public IEnumerable<SnapshotNode> VisibleChildren(SnapshotNode dir, long effectiveThreshold) =>
            ChildrenByParent[dir.Id].Where(c => c.IsDirectory || c.Size >= effectiveThreshold);

        public long SmallSize(SnapshotNode dir) =>
            dir.SmallFilesSize + (ExtraSmall.TryGetValue(dir.Id, out var v) ? v.Size : 0);
    }

    /// <summary>Returns null when the subtree is entirely unchanged.</summary>
    private static DiffNode? DiffDir(
        Sides oldSide, SnapshotNode oldDir, Sides newSide, SnapshotNode newDir, long effectiveThreshold)
    {
        var cmp = PathKeys.NameComparer;
        var oldKids = oldSide.VisibleChildren(oldDir, effectiveThreshold).ToDictionary(c => c.Name, cmp);
        var newKids = newSide.VisibleChildren(newDir, effectiveThreshold).ToDictionary(c => c.Name, cmp);

        var children = new List<DiffNode>();

        foreach (var (name, oldChild) in oldKids)
        {
            if (newKids.TryGetValue(name, out var newChild) && oldChild.IsDirectory == newChild.IsDirectory)
            {
                if (oldChild.IsDirectory)
                {
                    var sub = DiffDir(oldSide, oldChild, newSide, newChild, effectiveThreshold);
                    if (sub != null) children.Add(sub);
                }
                else if (newChild.Size != oldChild.Size)
                {
                    children.Add(Leaf(name, false,
                        newChild.Size > oldChild.Size ? ChangeKind.Grown : ChangeKind.Shrunk,
                        oldChild.Size, newChild.Size));
                }
                // equal size => Unchanged => excluded
            }
            else
            {
                children.Add(Leaf(name, oldChild.IsDirectory, ChangeKind.Removed, oldChild.Size, null));
            }
        }
        foreach (var (name, newChild) in newKids)
        {
            if (!oldKids.TryGetValue(name, out var oldMatch) || oldMatch.IsDirectory != newChild.IsDirectory)
                children.Add(Leaf(name, newChild.IsDirectory, ChangeKind.Added, null, newChild.Size));
            // (a type-flip already emitted its Removed in the oldKids loop above)
        }

        var smallDelta = newSide.SmallSize(newDir) - oldSide.SmallSize(oldDir);
        var sizeDelta = newDir.Size - oldDir.Size;
        if (children.Count == 0 && smallDelta == 0 && sizeDelta == 0)
            return null; // entirely unchanged subtree

        children.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));

        var dirNode = new DiffNode
        {
            Name = newDir.Name,
            IsDirectory = true,
            Kind = sizeDelta > 0 ? ChangeKind.Grown
                 : sizeDelta < 0 ? ChangeKind.Shrunk
                 : ChangeKind.Unchanged,
            SizeBefore = oldDir.Size,
            SizeAfter = newDir.Size,
            Delta = sizeDelta,
            SmallFilesDelta = smallDelta,
        };
        dirNode.Children.AddRange(children);
        return dirNode;
    }

    private static DiffNode Leaf(string name, bool isDir, ChangeKind kind, long? before, long? after) => new()
    {
        Name = name, IsDirectory = isDir, Kind = kind,
        SizeBefore = before, SizeAfter = after,
        Delta = (after ?? 0) - (before ?? 0),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests -v q`
Expected: PASS (all tests).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.DiskAnalyzer tests/NexusMonitor.DiskAnalyzer.Tests
git commit -m "feat(snapshots): two-tree differ with threshold reconciliation and case rules"
```

---

### Task 5: Retention (per-root count + fair global size cap)

**Files:**
- Modify: `src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotStore.cs` (replace the `ApplyRetention` no-op)
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/RetentionTests.cs`

**Interfaces:**
- Consumes: `SnapshotStore` internals (private method), `SnapshotOptions`.
- Produces: retention behavior invoked automatically by `Save` (no public API change). `ApplyRetention(SnapshotOptions)` becomes `internal` (`InternalsVisibleTo` already added in Task 3 Step 0).

- [ ] **Step 1: Write failing retention tests**

`tests/NexusMonitor.DiskAnalyzer.Tests/RetentionTests.cs`:

```csharp
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _dir;
    private readonly SnapshotStore _store;

    public RetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SnapshotStore(Path.Combine(_dir, "disk-snapshots.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private long Snap(string root, long fileSize, SnapshotOptions opts) =>
        _store.Save(TestTrees.Result(
            TestTrees.Dir("R", TestTrees.File("data.bin", fileSize)),
            Path.Combine(_dir, root)), opts);

    [Fact]
    public void Save_PrunesOldestBeyondPerRootLimit()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 3);
        var ids = Enumerable.Range(0, 5).Select(_ => Snap("A", 5000, opts)).ToList();

        var kept = _store.ListSnapshots(Path.Combine(_dir, "A")).Select(s => s.Id).ToList();
        kept.Should().HaveCount(3);
        kept.Should().BeEquivalentTo(ids.TakeLast(3));
    }

    [Fact]
    public void PerRootLimit_DoesNotCrossRoots()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 2);
        Snap("A", 5000, opts); Snap("A", 5000, opts);
        Snap("B", 5000, opts);
        _store.ListSnapshots(Path.Combine(_dir, "A")).Should().HaveCount(2);
        _store.ListSnapshots(Path.Combine(_dir, "B")).Should().HaveCount(1);
    }

    [Fact]
    public void GlobalSizeCap_PrunesFromRootWithMostSnapshots()
    {
        // Cap tiny so it triggers immediately; root A has 3 snapshots, root B has 1.
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 50, MaxDbSizeBytes: 1);
        Snap("A", 5000, opts); Snap("A", 5000, opts); Snap("A", 5000, opts);
        Snap("B", 5000, opts);

        // Fairness (spec §4): the cap takes from the root with the MOST snapshots (A),
        // never starving the lone root B. B's single snapshot must survive at least as
        // long as A has more than one.
        var a = _store.ListSnapshots(Path.Combine(_dir, "A"));
        var b = _store.ListSnapshots(Path.Combine(_dir, "B"));
        b.Should().HaveCount(1);
        a.Count.Should().BeLessThan(3);
    }

    [Fact]
    public void GlobalSizeCap_AlwaysKeepsAtLeastOneSnapshotPerRoot()
    {
        var opts = new SnapshotOptions(ThresholdBytes: 100, RetentionPerRoot: 50, MaxDbSizeBytes: 1);
        Snap("A", 5000, opts);
        Snap("B", 5000, opts);
        // Even with an absurd cap, the newest snapshot of each root survives —
        // retention must never delete the snapshot that was just saved.
        _store.ListSnapshots(Path.Combine(_dir, "A")).Should().HaveCount(1);
        _store.ListSnapshots(Path.Combine(_dir, "B")).Should().HaveCount(1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests --filter RetentionTests -v q`
Expected: FAIL — retention is currently a no-op, extra snapshots survive.

- [ ] **Step 3: Implement ApplyRetention**

Replace the no-op in `SnapshotStore.cs`:

```csharp
internal void ApplyRetention(SnapshotOptions options)
{
    // Pass 1 — per-root count (spec §4: default 26 ≈ six months of weekly scans).
    // Delete over-limit snapshot rows directly, then orphan-sweep their nodes.
    using (var tx = Database.Connection.BeginTransaction())
    {
        using (var cmd = Database.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM snapshots WHERE id IN (
                    SELECT id FROM snapshots s
                    WHERE (SELECT COUNT(*) FROM snapshots n
                           WHERE n.root_key = s.root_key
                             AND (n.created_at > s.created_at
                                  OR (n.created_at = s.created_at AND n.id > s.id)))
                          >= $keep);";
            cmd.Parameters.AddWithValue("$keep", options.RetentionPerRoot);
            cmd.ExecuteNonQuery();
        }
        using (var orphans = Database.Connection.CreateCommand())
        {
            orphans.Transaction = tx;
            orphans.CommandText = "DELETE FROM nodes WHERE snapshot_id NOT IN (SELECT id FROM snapshots);";
            orphans.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Pass 2 — fair global size cap (spec §4): while over cap, delete the OLDEST
    // snapshot from the root that currently holds the MOST snapshots; never drop
    // a root's last remaining snapshot. Size is LIVE bytes — file length doesn't
    // shrink until VACUUM (the loop would otherwise over-delete every root to 1),
    // and a VACUUM per iteration rewrites the whole file each time (review
    // consensus, 2026-07-19). One VACUUM after the loop reclaims the space.
    var deletedAny = false;
    while (Database.GetLiveSizeBytes() > options.MaxDbSizeBytes)
    {
        long victim;
        using (var pick = Database.Connection.CreateCommand())
        {
            pick.CommandText = @"
                SELECT s.id FROM snapshots s
                WHERE s.root_key = (
                    SELECT root_key FROM snapshots
                    GROUP BY root_key HAVING COUNT(*) > 1
                    ORDER BY COUNT(*) DESC LIMIT 1)
                ORDER BY s.created_at ASC, s.id ASC LIMIT 1";
            var res = pick.ExecuteScalar();
            if (res is null || res is DBNull) break; // every root is down to 1 — stop
            victim = Convert.ToInt64(res);
        }
        DeleteCore(victim);
        deletedAny = true;
    }
    if (deletedAny)
    {
        Database.Checkpoint();
        RunVacuum();
    }
}

private void RunVacuum()
{
    using var cmd = Database.Connection.CreateCommand();
    cmd.CommandText = "VACUUM;";
    cmd.ExecuteNonQuery();
}
```

Note: `Save` holds `_writeLock` when calling `ApplyRetention`, and `Delete` also takes `_writeLock` — make the lock reentrant-safe by extracting the unlocked body: rename the current `Delete` body into `private void DeleteCore(long id)` (no lock), have public `Delete` do `lock (_writeLock) DeleteCore(id);` and `ApplyRetention` call `DeleteCore`. (C# `lock` on the same thread is reentrant, so this is belt-and-braces clarity, not a deadlock fix — but do it anyway; it makes the locking story auditable.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests -v q`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.DiskAnalyzer tests/NexusMonitor.DiskAnalyzer.Tests
git commit -m "feat(snapshots): retention — per-root count + fair global size cap"
```

---

### Task 6: DiffFormatter + snapshot-reference resolution (CLI-facing, pure logic)

**Files:**
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/DiffFormatter.cs`
- Create: `src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotRefs.cs`
- Create: `tests/NexusMonitor.DiskAnalyzer.Tests/DiffFormatterTests.cs`

**Interfaces:**
- Consumes: `DiffResult`, `DiffNode`, `SnapshotInfo`, `DiskNode.FormatSize`.
- Produces (used by Task 7 CLI and Task 9 UI header):

```csharp
public static class DiffFormatter
{
    public sealed record Line(string Path, string Kind, bool IsDir,
        long? SizeBefore, long? SizeAfter, long Delta, long SmallFilesDelta);
    public static IReadOnlyList<Line> Flatten(DiffResult diff, int? top = null); // |delta| desc
    public static string ToJson(DiffResult diff, int? top = null);   // stable shape, asserted in tests
    public static string ToTable(DiffResult diff, int? top = null);  // human table + footer
    public static string ThresholdFooter(DiffResult diff);           // spec §5/§7 exact disclosure
}
public static class SnapshotRefs
{
    /// <summary>Resolve 'latest' | integer id | ISO date against a newest-first list. Null when unresolvable.</summary>
    public static SnapshotInfo? Resolve(IReadOnlyList<SnapshotInfo> newestFirst, string reference);
}
```

- [ ] **Step 1: Write failing tests**

`tests/NexusMonitor.DiskAnalyzer.Tests/DiffFormatterTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Xunit;

namespace NexusMonitor.DiskAnalyzer.Tests;

public class DiffFormatterTests
{
    private static SnapshotInfo Info(long id, long threshold = 1000, string created = "2026-07-12T00:00:00.0000000Z") =>
        new(id, "/data", "/data", DateTime.Parse(created, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            "recursive", "TESTFS", 100, 40, 10_000, 3, 1, threshold, "test");

    private static DiffResult SampleDiff(bool mismatch = false)
    {
        var root = new DiffNode { Name = "data", IsDirectory = true, Kind = ChangeKind.Grown, Delta = 2500,
                                  SizeBefore = 6000, SizeAfter = 8500 };
        root.Children.Add(new DiffNode { Name = "big.bin", Kind = ChangeKind.Added, SizeAfter = 4000, Delta = 4000 });
        root.Children.Add(new DiffNode { Name = "old.bin", Kind = ChangeKind.Removed, SizeBefore = 1500, Delta = -1500 });
        return new DiffResult
        {
            Older = Info(1, mismatch ? 100 : 1000), Newer = Info(2),
            Root = root, EffectiveThreshold = 1000, ThresholdMismatch = mismatch,
            NamesMatchedCaseInsensitively = true,
        };
    }

    [Fact]
    public void Flatten_SortsByAbsDelta_AndBuildsPaths()
    {
        var lines = DiffFormatter.Flatten(SampleDiff());
        // Sorted by |delta| desc: big.bin (4000) > root (2500) > old.bin (1500);
        // paths are rooted at the snapshot's RootPath.
        lines.Select(l => l.Path).Should().ContainInOrder("/data/big.bin", "/data", "/data/old.bin");
        lines[0].Kind.Should().Be("added");
        lines[2].Delta.Should().Be(-1500);
    }

    [Fact]
    public void Flatten_Top_TruncatesAfterSorting()
    {
        DiffFormatter.Flatten(SampleDiff(), top: 1).Should().HaveCount(1);
    }

    [Fact]
    public void ToJson_HasStableShape()
    {
        using var doc = JsonDocument.Parse(DiffFormatter.ToJson(SampleDiff(), top: 5));
        var r = doc.RootElement;
        r.GetProperty("older").GetProperty("id").GetInt64().Should().Be(1);
        r.GetProperty("newer").GetProperty("id").GetInt64().Should().Be(2);
        r.GetProperty("root").GetString().Should().Be("/data");
        r.GetProperty("threshold").GetProperty("effectiveBytes").GetInt64().Should().Be(1000);
        r.GetProperty("threshold").GetProperty("mismatch").GetBoolean().Should().BeFalse();
        r.GetProperty("changes").GetArrayLength().Should().Be(3);
        r.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ToTable_ContainsFooterDisclosure()
    {
        DiffFormatter.ToTable(SampleDiff()).Should().Contain("aggregated");
    }

    [Fact]
    public void ThresholdFooter_MismatchSentence_IsExplicit()
    {
        var footer = DiffFormatter.ThresholdFooter(SampleDiff(mismatch: true));
        footer.Should().Contain("different thresholds");
        footer.Should().Contain("diff uses");
    }

    [Fact]
    public void SnapshotRefs_ResolvesLatestIdAndDate()
    {
        var list = new List<SnapshotInfo>
        {
            Info(3, created: "2026-07-19T00:00:00.0000000Z"),
            Info(2, created: "2026-07-12T00:00:00.0000000Z"),
            Info(1, created: "2026-07-05T00:00:00.0000000Z"),
        };
        SnapshotRefs.Resolve(list, "latest")!.Id.Should().Be(3);
        SnapshotRefs.Resolve(list, "2")!.Id.Should().Be(2);
        SnapshotRefs.Resolve(list, "2026-07-14")!.Id.Should().Be(2);   // newest at-or-before date
        SnapshotRefs.Resolve(list, "2026-01-01").Should().BeNull();    // none before
        SnapshotRefs.Resolve(list, "999").Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests --filter DiffFormatterTests -v q`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/NexusMonitor.DiskAnalyzer/Snapshots/SnapshotRefs.cs`:

```csharp
using System.Globalization;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

public static class SnapshotRefs
{
    public static SnapshotInfo? Resolve(IReadOnlyList<SnapshotInfo> newestFirst, string reference)
    {
        if (string.Equals(reference, "latest", StringComparison.OrdinalIgnoreCase))
            return newestFirst.FirstOrDefault();

        if (long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return newestFirst.FirstOrDefault(s => s.Id == id);

        if (DateTime.TryParse(reference, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            // Date-only input means "end of that day": newest snapshot at or before it.
            var cutoff = reference.Length <= 10 ? date.Date.AddDays(1).AddTicks(-1) : date;
            return newestFirst.FirstOrDefault(s => s.CreatedAtUtc <= cutoff);
        }
        return null;
    }
}
```

`src/NexusMonitor.DiskAnalyzer/Snapshots/DiffFormatter.cs`:

```csharp
using System.Text;
using System.Text.Json;
using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Snapshots;

public static class DiffFormatter
{
    public sealed record Line(string Path, string Kind, bool IsDir,
        long? SizeBefore, long? SizeAfter, long Delta, long SmallFilesDelta);

    public static IReadOnlyList<Line> Flatten(DiffResult diff, int? top = null)
    {
        var lines = new List<Line>();
        void Walk(DiffNode n, string path)
        {
            var p = path.Length == 0 ? diff.Older.RootPath : $"{path}/{n.Name}";
            if (n.Kind != ChangeKind.Unchanged || n.SmallFilesDelta != 0)
                lines.Add(new Line(p, n.Kind.ToString().ToLowerInvariant(), n.IsDirectory,
                    n.SizeBefore, n.SizeAfter, n.Delta, n.SmallFilesDelta));
            foreach (var c in n.Children) Walk(c, p);
        }
        Walk(diff.Root, string.Empty);
        lines.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return top is int t && lines.Count > t ? lines.Take(t).ToList() : lines;
    }

    public static string ToJson(DiffResult diff, int? top = null)
    {
        var all = Flatten(diff);
        var shown = top is int t && all.Count > t ? all.Take(t).ToList() : (List<Line>)all;
        var payload = new
        {
            older = new { id = diff.Older.Id, createdAt = diff.Older.CreatedAtUtc.ToString("o") },
            newer = new { id = diff.Newer.Id, createdAt = diff.Newer.CreatedAtUtc.ToString("o") },
            root = diff.Older.RootPath,
            threshold = new
            {
                effectiveBytes = diff.EffectiveThreshold,
                mismatch = diff.ThresholdMismatch,
                olderBytes = diff.Older.ThresholdBytes,
                newerBytes = diff.Newer.ThresholdBytes,
            },
            namesMatchedCaseInsensitively = diff.NamesMatchedCaseInsensitively,
            changes = shown.Select(l => new
            {
                path = l.Path, kind = l.Kind, isDir = l.IsDir,
                sizeBefore = l.SizeBefore, sizeAfter = l.SizeAfter,
                delta = l.Delta, smallFilesDelta = l.SmallFilesDelta,
            }),
            truncated = shown.Count < all.Count,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToTable(DiffResult diff, int? top = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"CHANGE",-9} {"DELTA",12} {"BEFORE",12} {"AFTER",12}  PATH");
        foreach (var l in Flatten(diff, top))
        {
            sb.AppendLine($"{l.Kind,-9} {Signed(l.Delta),12} " +
                          $"{(l.SizeBefore is long b ? DiskNode.FormatSize(b) : "—"),12} " +
                          $"{(l.SizeAfter is long a ? DiskNode.FormatSize(a) : "—"),12}  {l.Path}");
        }
        sb.AppendLine();
        sb.AppendLine(ThresholdFooter(diff));
        return sb.ToString();
    }

    public static string ThresholdFooter(DiffResult diff)
    {
        var caseNote = diff.NamesMatchedCaseInsensitively
            ? "names matched case-insensitively" : "names matched case-sensitively";
        if (!diff.ThresholdMismatch)
            return $"Files under {DiskNode.FormatSize(diff.EffectiveThreshold)} aggregated per folder · " +
                   $"renames appear as removed + added · {caseNote}.";
        var lo = Math.Min(diff.Older.ThresholdBytes, diff.Newer.ThresholdBytes);
        return $"Snapshots used different thresholds ({DiskNode.FormatSize(diff.Older.ThresholdBytes)} and " +
               $"{DiskNode.FormatSize(diff.Newer.ThresholdBytes)}); diff uses " +
               $"{DiskNode.FormatSize(diff.EffectiveThreshold)} — files between {DiskNode.FormatSize(lo)} and " +
               $"{DiskNode.FormatSize(diff.EffectiveThreshold)} are aggregated · " +
               $"renames appear as removed + added · {caseNote}.";
    }

    private static string Signed(long v) =>
        v >= 0 ? "+" + DiskNode.FormatSize(v) : "-" + DiskNode.FormatSize(-v);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/NexusMonitor.DiskAnalyzer.Tests -v q`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.DiskAnalyzer tests/NexusMonitor.DiskAnalyzer.Tests
git commit -m "feat(snapshots): diff formatter (table/json/top) + snapshot ref resolution"
```

---

### Task 7: CLI — `nexus disk` command branch

**Files:**
- Modify: `src/NexusMonitor.CLI/NexusMonitor.CLI.csproj` (+ ProjectReference)
- Modify: `src/NexusMonitor.CLI/Program.cs` (register store + commands, `AddBranch("disk", …)`)
- Create: `src/NexusMonitor.CLI/Commands/Disk/DiskScanCommand.cs`
- Create: `src/NexusMonitor.CLI/Commands/Disk/DiskSnapshotsListCommand.cs`
- Create: `src/NexusMonitor.CLI/Commands/Disk/DiskSnapshotsDeleteCommand.cs`
- Create: `src/NexusMonitor.CLI/Commands/Disk/DiskDiffCommand.cs`

**Interfaces:**
- Consumes: `ISnapshotStore`, `SnapshotDiffer`, `DiffFormatter`, `SnapshotRefs`, `MftScanner` (which self-falls-back to `RecursiveScanner` off-Windows), `ScanOptions`.
- Produces: user-facing commands `nexus disk scan <path> [--diff <ref>] [--format table|json] [--top N] [--threshold BYTES]`, `nexus disk snapshots list [path]`, `nexus disk snapshots delete <id>`, `nexus disk diff <a> <b> [--format] [--top]`.
- v1 note (document in command help): CLI saves use `SnapshotOptions` defaults unless `--threshold` is passed — GUI settings do not flow to the CLI in v1 (the CLI container does not load `SettingsService`).

- [ ] **Step 1: Add the project reference**

In `src/NexusMonitor.CLI/NexusMonitor.CLI.csproj`, next to the existing Hosting reference:

```xml
<ProjectReference Include="..\NexusMonitor.DiskAnalyzer\NexusMonitor.DiskAnalyzer.csproj" />
```

- [ ] **Step 2: Implement the four commands**

`src/NexusMonitor.CLI/Commands/Disk/DiskScanCommand.cs` (the others follow the same shape — mirror `ExportCommand.cs` conventions):

```csharp
using System.ComponentModel;
using NexusMonitor.DiskAnalyzer.Models;
using NexusMonitor.DiskAnalyzer.Scanning;
using NexusMonitor.DiskAnalyzer.Snapshots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NexusMonitor.CLI.Commands.Disk;

internal sealed class DiskScanCommand : AsyncCommand<DiskScanCommand.Settings>
{
    private readonly ISnapshotStore _store;
    public DiskScanCommand(ISnapshotStore store) { _store = store; }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Directory to scan")]
        public string Path { get; init; } = string.Empty;

        [CommandOption("--diff")]
        [Description("After scanning, diff against: 'latest', a snapshot id, or a date (newest at-or-before)")]
        public string? Diff { get; init; }

        [CommandOption("--format")]
        [Description("Output format: table or json (default: table)")]
        [DefaultValue("table")]
        public string Format { get; init; } = "table";

        [CommandOption("--top")]
        [Description("Limit diff output to the N largest changes by absolute delta")]
        public int? Top { get; init; }

        [CommandOption("--threshold")]
        [Description("Small-file aggregation threshold in bytes for THIS snapshot (default 1048576)")]
        public long? Threshold { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        if (!Directory.Exists(s.Path))
        {
            AnsiConsole.MarkupLine($"[red]Path not found:[/] {s.Path}");
            return 1;
        }

        // Resolve the diff reference BEFORE saving: 'latest' means the newest
        // snapshot that existed before this scan (spec §7). DO NOT move this below
        // Save — 'latest' would then resolve to the snapshot we just saved and
        // every --diff latest would be an empty self-diff.
        SnapshotInfo? baseline = null;
        if (s.Diff != null)
        {
            baseline = SnapshotRefs.Resolve(_store.ListSnapshots(s.Path), s.Diff);
            if (baseline == null)
            {
                AnsiConsole.MarkupLine($"[red]No snapshot matches[/] '{s.Diff}' for this root.");
                return 1;
            }
        }

        var scanner = new MftScanner(); // falls back to RecursiveScanner off-Windows
        var result = await scanner.ScanAsync(s.Path, new ScanOptions(), progress: null, CancellationToken.None);

        var opts = s.Threshold is long t
            ? new SnapshotOptions(ThresholdBytes: t)
            : new SnapshotOptions();
        var id = _store.Save(result, opts,
            typeof(DiskScanCommand).Assembly.GetName().Version?.ToString());

        if (s.Format != "json")
            AnsiConsole.MarkupLine(
                $"Scanned [bold]{result.TotalFiles:N0}[/] files, {DiskNode.FormatSize(result.TotalSize)} " +
                $"— saved snapshot [bold]#{id}[/].");

        if (baseline == null) return 0;

        var diff = SnapshotDiffer.Diff(_store, baseline.Id, id);
        Console.Write(s.Format.ToLowerInvariant() == "json"
            ? DiffFormatter.ToJson(diff, s.Top)
            : DiffFormatter.ToTable(diff, s.Top));
        return 0;
    }
}
```

`DiskDiffCommand.cs`: `CommandArgument(0, "<older-id>")` + `CommandArgument(1, "<newer-id>")` (both `long`), `--format`/`--top` options identical to above; body is `SnapshotDiffer.Diff(_store, olderId, newerId)` in a try/catch that prints `InvalidOperationException.Message` in red and returns 1; output via the same `ToJson`/`ToTable` switch.

`DiskSnapshotsListCommand.cs`: optional `[CommandArgument(0, "[path]")]`; `--format table|json`. Table: `Grid`/`Table` with columns Id · Created (local) · Root · Size · Files · Threshold; **path omitted ⇒ all roots, grouped: order rows by root then newest-first, print a heading row per root** (spec §7). JSON: serialize the `SnapshotInfo` list (camelCase properties: `id, rootPath, createdAt, totalSize, totalFiles, thresholdBytes, fileSystem, volumeTotal, volumeFree`).

`DiskSnapshotsDeleteCommand.cs`: `[CommandArgument(0, "<id>")] long Id`; verify `GetSnapshot(id) != null` (red error, return 1, if not), then `Delete(id)` and confirm.

- [ ] **Step 3: Wire into Program.cs**

Near the other service registrations in `src/NexusMonitor.CLI/Program.cs`:

```csharp
var snapshotDbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "NexusMonitor", "disk-snapshots.db");
services.AddSingleton<NexusMonitor.DiskAnalyzer.Snapshots.ISnapshotStore>(_ =>
{
    var store = new NexusMonitor.DiskAnalyzer.Snapshots.SnapshotStore(snapshotDbPath);
    store.SweepIncomplete(); // spec §4: crash-recovery sweep at startup
    return store;
});
services.AddSingleton<Commands.Disk.DiskScanCommand>();
services.AddSingleton<Commands.Disk.DiskSnapshotsListCommand>();
services.AddSingleton<Commands.Disk.DiskSnapshotsDeleteCommand>();
services.AddSingleton<Commands.Disk.DiskDiffCommand>();
```

In the command-tree config, after the existing branches:

```csharp
config.AddBranch("disk", disk =>
{
    disk.SetDescription("Disk scanning, snapshots, and snapshot diffs");
    disk.AddCommand<Commands.Disk.DiskScanCommand>("scan")
        .WithDescription("Scan a directory, save a snapshot, optionally diff against a previous one");
    disk.AddBranch("snapshots", snaps =>
    {
        snaps.SetDescription("Manage stored disk snapshots");
        snaps.AddCommand<Commands.Disk.DiskSnapshotsListCommand>("list")
            .WithDescription("List snapshots (all roots when no path given)");
        snaps.AddCommand<Commands.Disk.DiskSnapshotsDeleteCommand>("delete")
            .WithDescription("Delete a snapshot by id");
    });
    disk.AddCommand<Commands.Disk.DiskDiffCommand>("diff")
        .WithDescription("Diff two stored snapshots of the same root");
});
```

- [ ] **Step 4: Build + smoke-test the CLI end-to-end**

```bash
dotnet build src/NexusMonitor.CLI -v q
mkdir -p /tmp/nexus-cli-smoke/sub
head -c 2000000 /dev/urandom > /tmp/nexus-cli-smoke/big.bin
head -c 500 /dev/urandom > /tmp/nexus-cli-smoke/sub/tiny.bin
dotnet run --project src/NexusMonitor.CLI -- disk scan /tmp/nexus-cli-smoke
head -c 3000000 /dev/urandom > /tmp/nexus-cli-smoke/big.bin
dotnet run --project src/NexusMonitor.CLI -- disk scan /tmp/nexus-cli-smoke --diff latest
dotnet run --project src/NexusMonitor.CLI -- disk scan /tmp/nexus-cli-smoke --diff latest --format json --top 5
dotnet run --project src/NexusMonitor.CLI -- disk snapshots list
```

Expected: first scan reports a saved snapshot id; second prints a table containing `grown` for `big.bin` and the threshold footer; JSON parses (`| python3 -m json.tool` if in doubt); list groups by root. NOTE: this writes to the real `%AppData%/NexusMonitor/disk-snapshots.db` on the dev machine — acceptable for a smoke test (it's the feature working as designed); delete the test snapshots afterwards with `disk snapshots delete <id>`.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.CLI
git commit -m "feat(cli): nexus disk scan/snapshots/diff commands"
```

---

### Task 8: UI wiring — store registration, auto-save on scan completion, failure toast

**Files:**
- Modify: `src/NexusMonitor.UI/App.axaml.cs` (register `ISnapshotStore` near the other singletons, ~line 623)
- Modify: `src/NexusMonitor.UI/ViewModels/DiskAnalyzerViewModel.cs`

**Interfaces:**
- Consumes: `ISnapshotStore`, `SnapshotOptions`, `IInAppNotificationService`/`InAppNotification` (`App.axaml.cs:371-385` shape), `SettingsService.Current` (AppSettings), Task 10's `Snapshot*` settings props (until Task 10 lands, use the literals shown — they are identical values).
- Produces: `IsSavingSnapshot : bool` (observable), auto-save behavior; `RefreshSnapshotList()` stub (filled by Task 9 — until then an empty private method).

- [ ] **Step 1: Verify SettingsService is resolvable in the UI container**

Run: `grep -n "SettingsService" src/NexusMonitor.UI/App.axaml.cs src/NexusMonitor.Hosting/NexusServiceCollectionExtensions.cs`
Two outcomes:
- **Registered** (an `AddSingleton<SettingsService>`/factory line exists): constructor injection just works. (.NET DI honors C# optional-parameter defaults — the VM's existing `IPlatformCapabilities? = null` parameter already relies on exactly this, so the new optional params are safe either way.)
- **Not registered** (App constructs its own instance — e.g. the `saved` variable used near `:371`): register THAT existing instance in the UI container before it is built (`services.AddSingleton(settingsInstance)`), so the VM receives the same object App reads. Do not construct a second SettingsService.

- [ ] **Step 2: Register the store in App.axaml.cs**

Next to the `InAppNotificationService` registrations (~`:623`):

```csharp
var snapshotDbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "NexusMonitor", "disk-snapshots.db");
services.AddSingleton<NexusMonitor.DiskAnalyzer.Snapshots.ISnapshotStore>(_ =>
{
    var store = new NexusMonitor.DiskAnalyzer.Snapshots.SnapshotStore(snapshotDbPath);
    store.SweepIncomplete(); // spec §4: crash-recovery sweep at startup
    return store;
});
```

- [ ] **Step 3: Extend DiskAnalyzerViewModel**

Constructor (current form at `:144-148` takes only `IPlatformCapabilities?`) becomes:

```csharp
public DiskAnalyzerViewModel(
    IPlatformCapabilities? platformCapabilities = null,
    ISnapshotStore? snapshotStore = null,
    IInAppNotificationService? notifications = null,
    SettingsService? settings = null)
{
    Title    = "Disk Analyzer";
    Platform = platformCapabilities ?? new MockPlatformCapabilities();
    _snapshotStore = snapshotStore;
    _notifications = notifications;
    _settings      = settings;
    if (_snapshotStore?.WasRecovered == true)
        _notifications?.Show(new InAppNotification(
            Title: "Snapshot store recovered",
            Body: "The snapshot database was corrupt and has been recreated. Previous snapshots were set aside.",
            Severity: InAppSeverity.Warning,
            AutoDismiss: TimeSpan.FromSeconds(8)));
}
```

(All parameters optional with null defaults — existing constructions and DI both keep working; adjust `using`s accordingly.)

New members:

```csharp
private readonly ISnapshotStore? _snapshotStore;
private readonly IInAppNotificationService? _notifications;
private readonly SettingsService? _settings;

[ObservableProperty] private bool _isSavingSnapshot;

private SnapshotOptions CurrentSnapshotOptions()
{
    var s = _settings?.Current;
    return new SnapshotOptions(
        ThresholdBytes:   s?.SnapshotThresholdBytes  ?? 1_048_576,
        RetentionPerRoot: s?.SnapshotRetentionPerRoot ?? 26,
        MaxDbSizeBytes:  (s?.SnapshotMaxDbSizeMb ?? 500) * 1024L * 1024L);
}

private async Task SaveSnapshotAsync(ScanResult result)
{
    // Entire body inside try — this task is fire-and-forget, so an exception
    // thrown before the first await would otherwise vanish unobserved.
    try
    {
        if (_snapshotStore is null) return;
        if (_settings?.Current is { SnapshotsEnabled: false }) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSavingSnapshot = true);
        await Task.Run(() => _snapshotStore.Save(result, CurrentSnapshotOptions(),
            GetType().Assembly.GetName().Version?.ToString())).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshSnapshotList);
    }
    catch (Exception ex)
    {
        // Spec §4/§8: never let the user silently believe a snapshot exists.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _notifications?.Show(new InAppNotification(
                Title: "Snapshot not saved",
                Body: $"Scan completed but the snapshot could not be saved: {ex.Message}",
                Severity: InAppSeverity.Warning,
                AutoDismiss: TimeSpan.FromSeconds(8))));
    }
    finally
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSavingSnapshot = false);
    }
}

private void RefreshSnapshotList() { } // Task 9 fills this
```

In `StartScan()` (`:178-231`), immediately after `ScanStatus = SummaryText;`:

```csharp
_ = SaveSnapshotAsync(result); // fire-and-forget; failures surface as a toast
```

Only complete scans reach this line — the cancelled/failed paths return earlier, satisfying "cancelled scans never produce a snapshot" (spec §4).

- [ ] **Step 4: Build + existing suite**

Run: `dotnet build -v q && dotnet test -v q`
Expected: builds clean; full suite (existing 962 + new snapshot tests) passes. There is no UI test project — the VM auto-save path gets its live validation in the Windows/macOS manual pass (tracked in the PR checklist), which is why every branch it calls into (store, options, differ) is unit-tested below the VM boundary.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.UI
git commit -m "feat(ui): auto-save snapshot on scan completion with failure toast"
```

---

### Task 9: UI — Snapshots mode + diff view

**Files:**
- Modify: `src/NexusMonitor.UI/ViewModels/DiskAnalyzerViewModel.cs`
- Modify: `src/NexusMonitor.UI/Views/DiskAnalyzerView.axaml`

**Interfaces:**
- Consumes: everything above; existing mode-toggle pattern (`IsFolderViewActive`/`IsFileViewActive`/`IsDuplicateViewActive` + `Select*View` commands, VM `:92-94, 152-174`; `IsVisible` bindings in the View).
- Produces: fourth mode `IsSnapshotViewActive`; `SnapshotRow`/`DiffRow` collections consumed only by the View.

- [ ] **Step 1: Add the mode + snapshot list to the VM**

```csharp
[ObservableProperty] private bool _isSnapshotViewActive;
[ObservableProperty] private string _snapshotStoreSummary = string.Empty;
[ObservableProperty] private string _diffHeader = string.Empty;
[ObservableProperty] private bool _hasDiff;
public ObservableCollection<SnapshotRow> Snapshots { get; } = new();
public ObservableCollection<DiffRow> DiffRows { get; } = new();

[RelayCommand]
private void SelectSnapshotView()
{
    IsFolderViewActive    = false;
    IsFileViewActive      = false;
    IsDuplicateViewActive = false;
    IsSnapshotViewActive  = true;
    RefreshSnapshotList();
}
```

**Also add `IsSnapshotViewActive = false;` to each of the three existing `Select*View` commands** (`:152-174`) — the mode flags are set exhaustively by convention.

`SnapshotRow` (nested in the VM file or beside it):

```csharp
public sealed partial class SnapshotRow : ObservableObject
{
    public required SnapshotInfo Info { get; init; }
    [ObservableProperty] private bool _isSelected;
    public string CreatedDisplay => Info.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string SizeDisplay    => DiskNode.FormatSize(Info.TotalSize);
    public string FilesDisplay   => $"{Info.TotalFiles:N0} files";
}
```

`RefreshSnapshotList()` (replacing the Task 8 stub):

```csharp
private void RefreshSnapshotList()
{
    if (_snapshotStore is null || string.IsNullOrEmpty(SelectedPath)) return;
    Snapshots.Clear();
    foreach (var info in _snapshotStore.ListSnapshots(SelectedPath))
        Snapshots.Add(new SnapshotRow { Info = info });
    SnapshotStoreSummary =
        $"{Snapshots.Count} snapshots for this root · store size " +
        $"{DiskNode.FormatSize(_snapshotStore.GetStoreSizeBytes())} · retention " +
        $"{CurrentSnapshotOptions().RetentionPerRoot} per root";
}
```

(`SelectedPath` is the VM's existing scan-path property — confirm the exact name at `:178-231` where `StartScan` reads it, and use that.)

- [ ] **Step 2: Add compare commands + diff rows**

```csharp
[RelayCommand]
private void CompareWithPrevious() // spec §6: first-class "now vs. last time" entry
{
    var two = Snapshots.Take(2).ToList();
    if (two.Count < 2) return;
    RunDiff(older: two[1].Info.Id, newer: two[0].Info.Id);
}

[RelayCommand]
private void CompareSelected()
{
    var sel = Snapshots.Where(s => s.IsSelected).ToList();
    if (sel.Count != 2) return;
    var (a, b) = (sel[0].Info, sel[1].Info);
    var older = a.CreatedAtUtc <= b.CreatedAtUtc ? a : b;
    var newer = ReferenceEquals(older, a) ? b : a;
    RunDiff(older.Id, newer.Id);
}

[RelayCommand]
private void DeleteSnapshot(SnapshotRow row)
{
    _snapshotStore?.Delete(row.Info.Id);
    RefreshSnapshotList();
}

private void RunDiff(long older, long newer)
{
    if (_snapshotStore is null) return;
    try
    {
        var diff = SnapshotDiffer.Diff(_snapshotStore, older, newer);
        DiffHeader =
            $"Comparing {diff.Older.CreatedAtUtc.ToLocalTime():MMM d} → " +
            $"{diff.Newer.CreatedAtUtc.ToLocalTime():MMM d} · {DiffFormatter.ThresholdFooter(diff)}";
        DiffRows.Clear();
        foreach (var row in BuildDiffRows(diff.Root, level: 0))
            DiffRows.Add(row);
        HasDiff = true;
    }
    catch (InvalidOperationException ex)
    {
        _notifications?.Show(new InAppNotification(
            Title: "Cannot compare", Body: ex.Message,
            Severity: InAppSeverity.Warning, AutoDismiss: TimeSpan.FromSeconds(6)));
    }
}
```

`DiffRow` + collapsed-by-default expansion (spec §6 — never materialize the full tree; mirrors the folder grid's row-building approach at `BuildTreeRows`):

```csharp
public sealed partial class DiffRow : ObservableObject
{
    public required DiffNode Node { get; init; }
    public required int Level { get; init; }
    [ObservableProperty] private bool _isExpanded;
    public bool HasChildren   => Node.Children.Count > 0;
    public string Indent      => new(' ', Level * 3);
    public string KindDisplay => Node.Kind.ToString();
    public string DeltaDisplay =>
        (Node.Delta >= 0 ? "+" : "-") + DiskNode.FormatSize(Math.Abs(Node.Delta));
    public string BeforeAfterDisplay =>
        $"{(Node.SizeBefore is long b ? DiskNode.FormatSize(b) : "—")} → " +
        $"{(Node.SizeAfter is long a ? DiskNode.FormatSize(a) : "—")}";
    public string SmallFilesDisplay => Node.IsDirectory && Node.SmallFilesDelta != 0
        ? $"({(Node.SmallFilesDelta >= 0 ? "+" : "-")}{DiskNode.FormatSize(Math.Abs(Node.SmallFilesDelta))} in small files)"
        : string.Empty;
}

private static IEnumerable<DiffRow> BuildDiffRows(DiffNode root, int level)
{
    yield return new DiffRow { Node = root, Level = level };
    // Children appear only when a row is expanded — see ToggleDiffRow.
}

[RelayCommand]
private void ToggleDiffRow(DiffRow row)
{
    var index = DiffRows.IndexOf(row);
    if (index < 0 || !row.HasChildren) return;
    if (row.IsExpanded)
    {
        // Collapse: remove all following rows deeper than this one.
        while (index + 1 < DiffRows.Count && DiffRows[index + 1].Level > row.Level)
            DiffRows.RemoveAt(index + 1);
        row.IsExpanded = false;
    }
    else
    {
        var insert = index + 1;
        foreach (var child in row.Node.Children) // already sorted by |Δ| desc
            DiffRows.Insert(insert++, new DiffRow { Node = child, Level = row.Level + 1 });
        row.IsExpanded = true;
    }
}
```

- [ ] **Step 3: Add the XAML**

In `DiskAnalyzerView.axaml`: a fourth mode button beside the existing three (`:173-227` — copy the exact Button markup of the Duplicates toggle, bind `Command="{Binding SelectSnapshotViewCommand}"`, content "Snapshots"), and a snapshots panel as a sibling of the Duplicates panel:

```xml
<Grid IsVisible="{Binding IsSnapshotViewActive}" RowDefinitions="Auto,Auto,*,Auto,*">
  <StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="8" Margin="0,0,0,8">
    <Button Content="Compare with previous" Command="{Binding CompareWithPreviousCommand}" />
    <Button Content="Compare selected" Command="{Binding CompareSelectedCommand}" />
    <TextBlock Text="{Binding SnapshotStoreSummary}" VerticalAlignment="Center" Opacity="0.7" />
  </StackPanel>
  <TextBlock Grid.Row="1" Text="Saving snapshot…" IsVisible="{Binding IsSavingSnapshot}" Opacity="0.7" />
  <DataGrid Grid.Row="2" ItemsSource="{Binding Snapshots}" AutoGenerateColumns="False"
            IsReadOnly="False" CanUserSortColumns="False">
    <DataGrid.Columns>
      <DataGridCheckBoxColumn Binding="{Binding IsSelected}" Width="40" IsReadOnly="False" />
      <DataGridTextColumn Header="Created" Binding="{Binding CreatedDisplay}" IsReadOnly="True" />
      <DataGridTextColumn Header="Size"    Binding="{Binding SizeDisplay}"    IsReadOnly="True" />
      <DataGridTextColumn Header="Files"   Binding="{Binding FilesDisplay}"   IsReadOnly="True" />
      <DataGridTemplateColumn Width="60">
        <DataGridTemplateColumn.CellTemplate>
          <DataTemplate>
            <Button Content="✕"
                    Command="{Binding $parent[DataGrid].((vm:DiskAnalyzerViewModel)DataContext).DeleteSnapshotCommand}"
                    CommandParameter="{Binding}" />
          </DataTemplate>
        </DataGridTemplateColumn.CellTemplate>
      </DataGridTemplateColumn>
    </DataGrid.Columns>
  </DataGrid>
  <TextBlock Grid.Row="3" Text="{Binding DiffHeader}" IsVisible="{Binding HasDiff}"
             Margin="0,8,0,4" Opacity="0.8" TextWrapping="Wrap" />
  <DataGrid Grid.Row="4" ItemsSource="{Binding DiffRows}" IsVisible="{Binding HasDiff}"
            AutoGenerateColumns="False" IsReadOnly="True">
    <DataGrid.Columns>
      <DataGridTemplateColumn Header="Name" Width="*">
        <DataGridTemplateColumn.CellTemplate>
          <DataTemplate>
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="{Binding Indent}" />
              <Button Content="▸" IsVisible="{Binding HasChildren}"
                      Command="{Binding $parent[DataGrid].((vm:DiskAnalyzerViewModel)DataContext).ToggleDiffRowCommand}"
                      CommandParameter="{Binding}" Background="Transparent" BorderThickness="0" />
              <TextBlock Text="{Binding Node.Name}" />
              <TextBlock Text="{Binding SmallFilesDisplay}" Opacity="0.6" Margin="6,0,0,0" />
            </StackPanel>
          </DataTemplate>
        </DataGridTemplateColumn.CellTemplate>
      </DataGridTemplateColumn>
      <DataGridTextColumn Header="Change"        Binding="{Binding KindDisplay}" />
      <DataGridTextColumn Header="Before → After" Binding="{Binding BeforeAfterDisplay}" />
      <DataGridTextColumn Header="Δ"             Binding="{Binding DeltaDisplay}" />
    </DataGrid.Columns>
  </DataGrid>
</Grid>
```

**Style note:** match the surrounding file's real markup — column styles, brushes, paddings, the `vm:` namespace alias, and the exact Button style used by the existing mode toggles. The XAML above is the structure; the file's existing idiom wins on styling. Colors for change kinds (added/removed green/red) only if an equivalent precedent exists in the file — no new styling system.

- [ ] **Step 4: Build + run the app locally (functional check)**

```bash
dotnet build -v q
dotnet run --project src/NexusMonitor.UI &
```

Scan a small directory twice (touch a file between scans) → Snapshots tab shows 2 rows → "Compare with previous" renders the diff with the honesty header. This is a dev-loop sanity check on the current OS; the cross-OS pass happens in the Windows/macOS validation (PR checklist).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMonitor.UI
git commit -m "feat(ui): snapshots mode with compare-with-previous and collapsed diff tree"
```

---

### Task 10: Settings properties, README, full-suite gate, PR

**Files:**
- Modify: `src/NexusMonitor.Core/Models/AppSettings.cs`
- Modify: `README.md`
- Create (branch → PR): no new files

- [ ] **Step 1: Add settings properties**

In `AppSettings.cs`, after an existing feature block (e.g. the AutoBalance block at `:53-56`), following the flat-prop-with-banner convention:

```csharp
// Disk snapshots (Snapshot & Compare)
public bool SnapshotsEnabled        { get; set; } = true;
public long SnapshotThresholdBytes  { get; set; } = 1_048_576;
public int  SnapshotRetentionPerRoot { get; set; } = 26;
public int  SnapshotMaxDbSizeMb     { get; set; } = 500;
```

Then in `DiskAnalyzerViewModel.CurrentSnapshotOptions()` (Task 8) confirm the property names match exactly — they were written against these names.

Also verify settings round-trip: run `dotnet test tests/NexusMonitor.Core.Tests --filter SettingsServiceTests -v q` — the existing serialize-whole-object tests cover new props automatically (defaults survive a save/load cycle).

- [ ] **Step 2: README feature section**

Add under the disk-analyzer feature docs (match the README's existing feature-blurb style):

```markdown
### Snapshot & Compare

Every completed disk scan is saved automatically as a compact snapshot
(directories + files ≥ 1 MB; smaller files aggregated per folder — the
threshold is configurable). Scan a root again later and compare any two
snapshots to see exactly what grew, shrank, appeared, or vanished — or use
"Compare with previous" for an instant now-vs-last-time answer. Free-space
history is captured with every snapshot.

Works headlessly too:

    nexus disk scan /data                      # scan + save a snapshot
    nexus disk scan /data --diff latest        # what changed since last scan?
    nexus disk scan /data --diff 2026-07-01 --format json --top 20
    nexus disk snapshots list

Honesty notes, by design: sub-threshold files are aggregated (and every diff
says so), renames appear as removed + added (no move detection), and name
matching follows your platform's case rule — each diff header states the
rules it applied.
```

- [ ] **Step 3: Full gate**

```bash
dotnet build -c Release -v q
dotnet test -v q
```

Expected: clean build, full suite green (962 pre-existing + all new snapshot tests). Fix anything red before proceeding.

- [ ] **Step 4: Commit, push branch, open PR**

```bash
git add src/NexusMonitor.Core README.md
git commit -m "feat(snapshots): settings properties + README docs"
gh auth switch --user joshuadsutcliff
git push -u origin feat/snapshot-compare
gh pr create --title "feat: Snapshot & Compare (disk-scan history + diff)" --body "..."
gh auth switch --user brass458
```

PR body: link the spec (`docs/superpowers/specs/2026-07-19-snapshot-compare-design.md`), summarize the 10 tasks, and include the live-validation checklist (Windows pass: scan/auto-save/compare/toast; macOS pass: same; CLI smoke on both). **Do NOT merge — CI green on all three OSes, then Josh's explicit word.**

---

## Plan Self-Review (performed at write time)

- **Spec coverage:** §2 decisions → Tasks 3 (threshold walk), 5 (retention 26 + fair cap), 9 (diff view, compare-with-previous); §3.1 schema incl. volume/filesystem/nullable timestamps + root_key → Tasks 2–3; §4 capture/auto-save/toast/sweep → Tasks 7 (CLI sweep), 8 (UI); §5 differ semantics incl. case rules, mismatch reconciliation, |Δ| clustering, logical-size-only → Tasks 4, 6; §6 UI incl. collapsed+virtualized rendering (DataGrid row virtualization is Avalonia-default; rows are additionally materialized only on expand) → Task 9; §7 CLI incl. `--diff`/`--format`/`--top`/grouped list/footer → Tasks 6–7; §8 error handling → Tasks 2 (corrupt recovery), 3 (complete flag), 8 (toast); §9 honesty note (allocated_size stored, never diffed) → Tasks 3–4; §10 testing map → every task is test-first; the spec's "CLI command tests follow existing precedent" resolves to: there IS no CLI test project — CLI logic was therefore pushed down into `DiffFormatter`/`SnapshotRefs` (unit-tested) leaving commands as thin shells, validated by the Task 7 smoke run.
- **Known deviations (all argued inline):** SnapshotWriter/Reader consolidated into SnapshotStore; flat settings props instead of nested block; CLI uses defaults + `--threshold` instead of reading GUI settings (v1); `scanner` column inferred by platform since ScanResult doesn't carry it.
- **Type consistency:** `ISnapshotStore` signatures identical in Tasks 3/7/8; `SnapshotOptions` record positional order consistent (`ThresholdBytes, RetentionPerRoot, MaxDbSizeBytes`); `DiffResult`/`DiffNode` fields as defined in Task 2 and consumed in 4/6/9.
