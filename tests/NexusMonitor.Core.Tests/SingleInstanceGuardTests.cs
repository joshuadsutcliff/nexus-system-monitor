using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using FluentAssertions;
using NexusMonitor.Core.Services;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for the issue #38 single-instance guard.
///
/// Every test creates and cleans up its OWN temporary directory and never touches the real
/// per-user lock path, so a test run can never fight another test, another test run, or a real
/// running copy of the app. Path-resolution tests inject the environment lookup rather than
/// mutating process-global environment variables, which is what keeps this file safe to run
/// under xUnit's default cross-class parallelism without a collection fixture.
/// </summary>
public sealed class SingleInstanceGuardTests : IDisposable
{
    private readonly string _tempDirectory;

    public SingleInstanceGuardTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "nexus-single-instance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only — a leftover temp dir must never fail a run.
        }
    }

    // ── 1. Acquisition on a clean path ──────────────────────────────────────────────────────

    [Fact]
    public void TryAcquire_WithNoExistingLock_Acquires()
    {
        using var guard = new SingleInstanceGuard(_tempDirectory);

        guard.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);
        File.Exists(guard.LockFilePath).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_RecordsOwningProcessId()
    {
        using var guard = new SingleInstanceGuard(_tempDirectory);
        guard.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);

        // Read with a permissive share mode; the owner holds FileShare.None, so on Windows this
        // read is expected to fail and the assertion is skipped there. On Unix the advisory
        // flock does not block a plain read, so the PID is observable.
        try
        {
            using var stream = new FileStream(
                guard.LockFilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            reader.ReadToEnd().Trim()
                  .Should().Be(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // Share-mode platform: the holder blocks readers. That is itself correct behaviour.
        }
    }

    // ── 2. A second acquisition against a held lock is detected ─────────────────────────────

    [Fact]
    public void TryAcquire_WhileAnotherGuardHoldsTheSamePath_ReportsAlreadyRunning()
    {
        using var first = new SingleInstanceGuard(_tempDirectory);
        first.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);

        using var second = new SingleInstanceGuard(_tempDirectory);
        second.TryAcquire().Should().Be(SingleInstanceStatus.AlreadyRunning);
    }

    // ── 3. Stale-lock recovery ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryAcquire_WithStaleLockFileFromDeadProcess_TakesOver()
    {
        // Pick a PID we can PROVE is dead rather than hardcoding a number: start a real short
        // lived child process, wait for it to exit, then reuse its (now free) PID. PID reuse is
        // possible in principle but requires the OS to wrap the whole PID space within
        // milliseconds, which does not happen in practice on any supported platform.
        var deadPid = StartAndReapShortLivedProcess();

        var lockPath = Path.Combine(_tempDirectory, SingleInstanceGuard.LockFileName);
        File.WriteAllText(lockPath, deadPid.ToString(CultureInfo.InvariantCulture));

        using var guard = new SingleInstanceGuard(_tempDirectory);
        guard.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);
    }

    [Fact]
    public void TryAcquire_WithUnparseableLockFile_TreatsHolderAsAlive()
    {
        // An unreadable / unparseable lock file must NOT be assumed stale: on Windows a healthy
        // FileShare.None holder produces exactly that signature, and deleting its lock would
        // reintroduce issue #38. The safe reading is "someone is running".
        var lockPath = Path.Combine(_tempDirectory, SingleInstanceGuard.LockFileName);
        File.WriteAllText(lockPath, "not-a-pid");

        using var holder = new FileStream(
            lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using var guard = new SingleInstanceGuard(_tempDirectory);
        guard.TryAcquire().Should().Be(SingleInstanceStatus.AlreadyRunning);
    }

    // ── 4. Release allows a later acquisition ───────────────────────────────────────────────

    [Fact]
    public void Dispose_ReleasesTheLockForASubsequentAcquisition()
    {
        var first = new SingleInstanceGuard(_tempDirectory);
        first.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);
        first.Dispose();

        using var second = new SingleInstanceGuard(_tempDirectory);
        second.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);
    }

    [Fact]
    public void TryAcquire_CalledTwiceOnTheSameGuard_IsIdempotent()
    {
        using var guard = new SingleInstanceGuard(_tempDirectory);

        guard.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);
        guard.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);
    }

    // ── 5. Path resolution ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveLockDirectory_UsesXdgRuntimeDir_WhenSetAndExisting()
    {
        var resolved = SingleInstanceGuard.ResolveLockDirectory(
            name => name == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null,
            _ => true);

        resolved.Should().Be(Path.Combine("/run/user/1000", "NexusMonitor"));
    }

    [Fact]
    public void ResolveLockDirectory_IgnoresXdgRuntimeDir_WhenDirectoryDoesNotExist()
    {
        var resolved = SingleInstanceGuard.ResolveLockDirectory(
            name => name == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null,
            _ => false);

        resolved.Should().NotStartWith("/run/user/1000");
    }

    [Fact]
    public void ResolveLockDirectory_FallsBack_WhenXdgRuntimeDirNotSet()
    {
        var resolved = SingleInstanceGuard.ResolveLockDirectory(_ => null, _ => true);

        resolved.Should().NotBeNullOrWhiteSpace();
        resolved.Should().EndWith("NexusMonitor");
    }

    [Fact]
    public void ResolveLockDirectory_FallbackIsNeverAWorldSharedRoot()
    {
        var resolved = SingleInstanceGuard.ResolveLockDirectory(_ => null, _ => true);

        // Must be an app-specific subdirectory, never a bare shared path such as /tmp itself.
        Path.GetDirectoryName(resolved).Should().NotBeNullOrWhiteSpace();
        resolved.Should().NotBe("/tmp");
        resolved.Should().NotBe(Path.GetTempPath());
    }

    [Fact]
    public void ResolveLockDirectory_UsingTheRealEnvironment_ReturnsAUsablePath()
    {
        var resolved = SingleInstanceGuard.ResolveLockDirectory();

        resolved.Should().NotBeNullOrWhiteSpace();
        Path.IsPathRooted(resolved).Should().BeTrue();
    }

    // ── 6. Fail open ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAcquire_WhenLockDirectoryPathIsAFile_ReportsMayStart()
    {
        // A regular file sitting where the lock directory should be makes Directory.CreateDirectory
        // throw. The guard must report "may start" rather than throwing or claiming another
        // instance is running: failing closed would leave the user unable to launch at all.
        var blockingFile = Path.Combine(_tempDirectory, "blocking-file");
        File.WriteAllText(blockingFile, "not a directory");

        using var guard = new SingleInstanceGuard(Path.Combine(blockingFile, "sub"));

        guard.TryAcquire().Should().Be(SingleInstanceStatus.MayStart);
    }

    [Fact]
    public void TryAcquire_AfterDispose_ReportsMayStart()
    {
        var guard = new SingleInstanceGuard(_tempDirectory);
        guard.Dispose();

        guard.TryAcquire().Should().Be(SingleInstanceStatus.MayStart);
    }

    // ── Activation marker ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestActivation_WritesAMarkerThatTheOwnerConsumesExactlyOnce()
    {
        using var owner = new SingleInstanceGuard(_tempDirectory);
        owner.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);

        using var secondLaunch = new SingleInstanceGuard(_tempDirectory);
        secondLaunch.TryAcquire().Should().Be(SingleInstanceStatus.AlreadyRunning);
        secondLaunch.RequestActivation().Should().BeTrue();

        File.Exists(owner.ActivationFilePath).Should().BeTrue();
        owner.TryConsumeActivationMarker().Should().BeTrue();
        owner.TryConsumeActivationMarker().Should().BeFalse();
        File.Exists(owner.ActivationFilePath).Should().BeFalse();
    }

    [Fact]
    public void StartActivationWatch_InvokesTheCallbackWhenAMarkerAppears()
    {
        using var owner = new SingleInstanceGuard(_tempDirectory);
        owner.TryAcquire().Should().Be(SingleInstanceStatus.Acquired);

        using var raised = new ManualResetEventSlim(false);
        owner.StartActivationWatch(() => raised.Set(), TimeSpan.FromMilliseconds(25));

        using var secondLaunch = new SingleInstanceGuard(_tempDirectory);
        secondLaunch.RequestActivation().Should().BeTrue();

        raised.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        File.Exists(owner.ActivationFilePath).Should().BeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a trivial child process, waits for it to exit and be reaped, and returns its PID.
    /// This gives a PID that is provably not running, instead of hardcoding a number and hoping.
    /// </summary>
    private static int StartAndReapShortLivedProcess()
    {
        var isWindows = OperatingSystem.IsWindows();

        var startInfo = new ProcessStartInfo
        {
            FileName               = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
        startInfo.ArgumentList.Add("exit 0");

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        var pid = process!.Id;
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();

        return pid;
    }
}
