using System.Diagnostics;

namespace NexusMonitor.Core.Services;

/// <summary>
/// Outcome of a <see cref="SingleInstanceGuard.TryAcquire"/> attempt.
/// </summary>
public enum SingleInstanceStatus
{
    /// <summary>This process now owns the lock and must run normally.</summary>
    Acquired,

    /// <summary>Another live instance owns the lock. The caller should signal it and exit 0.</summary>
    AlreadyRunning,

    /// <summary>
    /// The guard could not make a determination (unusable lock path, permission failure,
    /// unexpected IO error). The caller MUST start normally. A broken guard must never stop
    /// the user from running the app — see the fail-open discussion on
    /// <see cref="SingleInstanceGuard"/>.
    /// </summary>
    MayStart
}

/// <summary>
/// Cross-platform single-instance guard for issue #38 (a second launch used to spawn a fully
/// independent duplicate process/window while the first instance sat in the tray).
///
/// ── Why ONE code path instead of Mutex-on-Windows / flock-on-Linux ──────────────────────────
/// The obvious implementation is <c>System.Threading.Mutex</c> on Windows plus some
/// <c>flock</c>-based shim elsewhere. That is two mechanisms, two stale-lock stories, and two
/// sets of platform bugs. Instead this holds a single lock FILE open for the whole process
/// lifetime with <see cref="FileShare.None"/>:
///
///   new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
///
/// .NET implements FileShare.None as a share-mode open on Windows and as an advisory
/// <c>flock(LOCK_EX | LOCK_NB)</c> on Unix. In BOTH cases the lock is owned by the OS-level
/// open file description, so the kernel drops it when the owning process dies for ANY reason,
/// including SIGKILL and a hard crash. That is what buys stale-lock recovery for free on all
/// three platforms with zero <c>RuntimeInformation.IsOSPlatform</c> branching.
///
/// ── Defence in depth: the recorded PID ──────────────────────────────────────────────────────
/// Relying on the kernel alone is one mechanism, and a permanent lockout is a strictly worse
/// bug than the duplicate-instance bug being fixed here. So the owner also writes its own PID
/// into the lock file. When acquisition FAILS, the guard reads that PID back and asks the OS
/// whether such a process actually exists. If it does not, the lock file is treated as a
/// leftover from a crashed run: it is deleted and acquisition is retried exactly ONCE (never in
/// a loop — a retry loop against a genuinely live holder would just burn startup time and could
/// livelock two racing launches against each other).
///
/// If the PID cannot be read at all, the holder is assumed to be ALIVE. That is deliberate: on
/// Windows a FileShare.None holder blocks readers outright, so "unreadable" is the normal
/// signature of a healthy running instance, not evidence of staleness.
///
/// ── Fail open, never fail closed ────────────────────────────────────────────────────────────
/// Every unexpected <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> that
/// is not positively identified as "someone else holds this lock" resolves to
/// <see cref="SingleInstanceStatus.MayStart"/>. A read-only home directory, a missing runtime
/// dir, an exotic filesystem without advisory locking: none of those may ever prevent the app
/// from launching.
///
/// ── Activation signalling ───────────────────────────────────────────────────────────────────
/// Deliberately the dumbest cross-platform mechanism that works, per the issue brief's
/// "correctness over elegance" guidance: the second instance writes a marker file next to the
/// lock file and exits 0; the running instance polls for it, deletes it, and raises its window.
/// See <see cref="StartActivationWatch"/> for why this polls rather than using
/// <see cref="FileSystemWatcher"/>.
///
/// The instance is <see cref="IDisposable"/>; the held <see cref="FileStream"/> lives in a field
/// for the entire application lifetime so it can never be finalized out from under the process,
/// and is released on dispose.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Name of the lock file inside the resolved per-user directory.</summary>
    public const string LockFileName = "nexus-monitor.lock";

    /// <summary>Name of the activation-request marker written by a redirected second launch.</summary>
    public const string ActivationFileName = "nexus-monitor.activate";

    /// <summary>Per-user subdirectory created under whichever base directory is resolved.</summary>
    private const string AppDirectoryName = "NexusMonitor";

    private readonly object _sync = new();

    private FileStream? _lockStream;
    private Timer? _activationTimer;
    private bool _disposed;

    /// <summary>Full path of the lock file this guard operates on.</summary>
    public string LockFilePath { get; }

    /// <summary>Full path of the activation marker file (a sibling of the lock file).</summary>
    public string ActivationFilePath { get; }

    /// <summary>
    /// Creates a guard over the given directory. Production callers pass
    /// <see cref="ResolveLockDirectory()"/>; tests pass a temporary directory so they can never
    /// collide with each other or with a real running app.
    /// </summary>
    public SingleInstanceGuard(string lockDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);

        LockFilePath       = Path.Combine(lockDirectory, LockFileName);
        ActivationFilePath = Path.Combine(lockDirectory, ActivationFileName);
    }

    /// <summary>Creates a guard over the resolved real per-user lock directory.</summary>
    public static SingleInstanceGuard CreateDefault() => new(ResolveLockDirectory());

    // ── Lock-path resolution ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the per-user directory the lock file lives in, using the real environment.
    /// </summary>
    public static string ResolveLockDirectory()
        => ResolveLockDirectory(Environment.GetEnvironmentVariable, Directory.Exists);

    /// <summary>
    /// Testable core of lock-path resolution. The environment lookup and the existence probe are
    /// injected rather than read directly, because environment variables are PROCESS-global: a
    /// test that really set <c>XDG_RUNTIME_DIR</c> would race every other test in the run.
    ///
    /// Order:
    ///   1. <c>$XDG_RUNTIME_DIR</c> when it is set AND the directory exists. On Linux this is the
    ///      correct home for runtime lock files — it is already per-user, mode 0700, and cleared
    ///      on logout.
    ///   2. Otherwise the platform's local application-data folder.
    ///   3. Otherwise (LocalApplicationData can legitimately resolve to an empty string on some
    ///      Unix configurations) a per-USER subdirectory of the temp path. The user name is part
    ///      of the path on purpose: a bare shared "/tmp/nexus.lock" would let one user of a
    ///      multi-user machine lock out another, or pre-create and hijack the path.
    ///
    /// In every branch an application-specific subdirectory is appended, so the guard never drops
    /// files directly into a shared root.
    /// </summary>
    public static string ResolveLockDirectory(
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var xdgRuntimeDir = getEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDir) && directoryExists(xdgRuntimeDir))
            return Path.Combine(xdgRuntimeDir, AppDirectoryName);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, AppDirectoryName);

        // Last resort. Scope by user name so the path can never be shared across accounts.
        var userName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(userName))
            userName = "unknown-user";

        return Path.Combine(Path.GetTempPath(), $"{AppDirectoryName}-{userName}");
    }

    // ── Acquisition ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to become the single instance. Never throws: any unexpected failure resolves to
    /// <see cref="SingleInstanceStatus.MayStart"/> so the app still launches.
    /// </summary>
    public SingleInstanceStatus TryAcquire()
    {
        lock (_sync)
        {
            if (_disposed)
                return SingleInstanceStatus.MayStart;

            if (_lockStream is not null)
                return SingleInstanceStatus.Acquired;

            try
            {
                var directory = Path.GetDirectoryName(LockFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The lock directory is unusable (a file sits at that path, the parent is
                // read-only, ...). Fail open.
                return SingleInstanceStatus.MayStart;
            }

            // First attempt.
            if (TryOpenLockFile())
                return SingleInstanceStatus.Acquired;

            // Someone (or something) holds the file. Decide whether that holder is real.
            if (IsRecordedOwnerAlive())
                return SingleInstanceStatus.AlreadyRunning;

            // The recorded owner is gone: a crashed run left the file behind on a platform or
            // filesystem where the OS-level release did not take effect. Clear it and retry
            // exactly ONCE.
            try
            {
                File.Delete(LockFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cannot clear it — most likely it really is held. Treat as a live instance so
                // the second launch at least raises the existing window rather than duplicating.
                return SingleInstanceStatus.AlreadyRunning;
            }

            return TryOpenLockFile()
                ? SingleInstanceStatus.Acquired
                : SingleInstanceStatus.AlreadyRunning;
        }
    }

    /// <summary>
    /// Opens the lock file with <see cref="FileShare.None"/> and stamps this process's PID into
    /// it. Returns false when the file is already locked by another open file description.
    /// </summary>
    private bool TryOpenLockFile()
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                LockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            // Record the owner PID for the staleness fallback above. The file is truncated first
            // so a shorter PID can never leave trailing digits from a previous owner behind.
            stream.SetLength(0);
            var payload = System.Text.Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);

            _lockStream = stream;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Reads the PID recorded in the lock file and asks the OS whether such a process exists.
    ///
    /// Returns TRUE when the holder should be considered alive. Note that every ambiguous case
    /// resolves to "alive": if the file cannot be opened or parsed, the safest reading is that a
    /// healthy instance is holding it. On Windows a FileShare.None holder blocks readers, so
    /// "unreadable" is the ordinary signature of a running instance, not evidence of staleness.
    /// Guessing "stale" there would delete a live instance's lock and reintroduce issue #38.
    /// </summary>
    private bool IsRecordedOwnerAlive()
    {
        int pid;
        try
        {
            using var reader = new FileStream(
                LockFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var textReader = new StreamReader(reader);
            var contents = textReader.ReadToEnd().Trim();

            if (!int.TryParse(contents, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out pid))
                return true;   // unparseable -> assume alive
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;       // unreadable -> assume alive
        }

        if (pid <= 0)
            return true;

        if (pid == Environment.ProcessId)
            return true;       // our own PID: something in this process holds it

        try
        {
            using var process = Process.GetProcessById(pid);

            // On Unix, Process.GetProcessById succeeds for zombies too. HasExited is the extra
            // question that separates "reaped and gone" from "still in the table".
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;      // no such process -> the lock is stale
        }
        catch (InvalidOperationException)
        {
            return false;      // process exited between lookup and inspection
        }
        catch (Exception ex) when (ex is NotSupportedException or SystemException)
        {
            // Cannot tell. Fall back to "alive" rather than deleting a possibly-live lock.
            return true;
        }
    }

    // ── Activation signalling ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by a launch that lost the race: drops a marker file next to the lock so the running
    /// instance knows to raise its window. Never throws — if the marker cannot be written, the
    /// second launch still exits 0 and the user simply has to click the tray icon.
    /// </summary>
    public bool RequestActivation()
    {
        try
        {
            var directory = Path.GetDirectoryName(ActivationFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                ActivationFilePath,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts watching for activation markers. The callback fires on a ThreadPool thread, so the
    /// UI-side handler is responsible for marshalling (App.RestoreMainWindow already posts to
    /// Dispatcher.UIThread, which is exactly why activation routes through it).
    ///
    /// This POLLS rather than using <see cref="FileSystemWatcher"/> on purpose. FileSystemWatcher
    /// is backed by a different native mechanism per platform (ReadDirectoryChangesW, inotify,
    /// FSEvents/kqueue) with materially different reliability: inotify watches can be silently
    /// exhausted by the per-user max_user_watches limit, and the macOS backends have well-known
    /// coalescing and missed-event behaviour for short-lived files — and a marker file that is
    /// created and deleted within a second is exactly the short-lived case. A one-second poll of
    /// a single File.Exists call is unmeasurable overhead next to the app's own metric sampling,
    /// and it cannot silently stop working. Correctness over elegance, per the issue brief.
    ///
    /// The marker is deleted BEFORE the callback runs so a slow restore cannot re-trigger, and so
    /// a stale marker left by a crash is consumed once rather than looping forever.
    /// </summary>
    public void StartActivationWatch(Action onActivationRequested, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        var interval = pollInterval ?? TimeSpan.FromSeconds(1);

        lock (_sync)
        {
            if (_disposed || _activationTimer is not null)
                return;

            // Clear any marker predating this instance so startup does not immediately self-raise.
            TryConsumeActivationMarker();

            _activationTimer = new Timer(
                _ =>
                {
                    if (TryConsumeActivationMarker())
                    {
                        try
                        {
                            onActivationRequested();
                        }
                        catch
                        {
                            // A failing restore must never take down the timer or the process.
                        }
                    }
                },
                state: null,
                dueTime: interval,
                period: interval);
        }
    }

    /// <summary>
    /// Deletes the activation marker if present. Returns true only when this call is the one that
    /// removed it, so a marker is honoured exactly once.
    /// </summary>
    internal bool TryConsumeActivationMarker()
    {
        try
        {
            if (!File.Exists(ActivationFilePath))
                return false;

            File.Delete(ActivationFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── Lifetime ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Releases the lock. The file itself is deleted on a best-effort basis; if deletion fails the
    /// next launch still recovers via the OS-level release plus the PID-staleness fallback.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            _activationTimer?.Dispose();
            _activationTimer = null;

            if (_lockStream is null) return;

            try
            {
                _lockStream.Dispose();
            }
            catch (IOException)
            {
                // Nothing useful to do while tearing down.
            }
            finally
            {
                _lockStream = null;
            }

            try
            {
                File.Delete(LockFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftover file is harmless — the next launch treats it as stale.
            }
        }
    }
}
