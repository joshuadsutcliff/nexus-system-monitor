using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.ReactiveUI;
using NexusMonitor.Core.Services;
using ReactiveUI;
using Serilog;
using System.Reactive;

namespace NexusMonitor.UI;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // ── Cap ThreadPool — default min = ProcessorCount (16 on Ryzen 5700X3D) which
        //    pre-commits 16 thread stacks unnecessarily. The shared multicast Rx pattern
        //    means the app never needs more than 4 concurrent workers at steady state.
        System.Threading.ThreadPool.SetMinThreads(4, 4);
        System.Threading.ThreadPool.SetMaxThreads(32, 16);

        // ── Initialize Serilog before anything else ─────────────────────────
        LoggingBootstrap.Initialize();

        // ── Catch-all for unhandled exceptions on any thread ────────────────
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                // Issue #42: on Linux/KDE, tray teardown's D-Bus continuation can lose the race
                // against dispatcher shutdown and surface here as a TaskCanceledException /
                // ObjectDisposedException on a background thread. A try/catch around the tray
                // dispose call site can't catch this — it's raised on the D-Bus teardown's own
                // continuation thread, not on the call stack that invoked Dispose(). Recognize
                // and log it at a lower level instead of writing it to crash.log as a real crash.
                if (CrashLogger.ShouldSuppress(ex, App.IsShuttingDown))
                {
                    Log.Information(ex,
                        "Suppressed expected shutdown-teardown exception (IsTerminating={IsTerminating})",
                        e.IsTerminating);
                }
                else
                {
                    Log.Fatal(ex, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
                    CrashLogger.Write(ex,
                        $"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})");
                }
            }
        };

        // ── Catch unobserved Task exceptions (background async failures) ────
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(e.Exception, "Unobserved task exception");
            CrashLogger.Write(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();   // prevents process termination for non-fatal async faults
        };

        // ── Central Rx safety net: swallow any exception that escapes a .Subscribe()
        //    without an onError handler; prevents the error from reaching the CLR's
        //    unhandled-exception path and triggering abort() on close.
        RxApp.DefaultExceptionHandler = Observer.Create<Exception>(ex =>
        {
            Log.Error(ex, "Unhandled Rx exception (suppressed by DefaultExceptionHandler)");
            CrashLogger.Write(ex, "RxApp.DefaultExceptionHandler");
            // do NOT rethrow — swallowing here prevents the error from aborting the process
        });

        // ── Wrap the entire Avalonia lifetime so startup failures are logged ─
        SingleInstanceGuard? guard = null;
        try
        {
            // ── Single-instance guard (issue #38) ───────────────────────────
            // Runs BEFORE BuildAvaloniaApp() — the whole point is that a second launch never gets
            // as far as creating a window. It runs INSIDE this try so the finally below still
            // flushes Serilog on the early-exit path, and it exits via a plain `return` rather
            // than Environment.Exit(1) or a throw, so the catch clause (and therefore crash.log)
            // is never touched: a redirected second launch is normal behaviour, not a crash.
            //
            // The guard is held in a local disposed by the finally, which keeps its FileStream
            // rooted for the whole application lifetime — letting it be collected would release
            // the OS-level lock while the app is still running.
            guard = SingleInstanceGuard.CreateDefault();
            var instanceStatus = guard.TryAcquire();

            if (instanceStatus == SingleInstanceStatus.AlreadyRunning)
            {
                var signalled = guard.RequestActivation();
                Log.Information(
                    "Another Nexus Monitor instance already holds {LockPath}; asked it to restore " +
                    "its window (signalled={Signalled}) and exiting this launch cleanly",
                    guard.LockFilePath, signalled);
                return;   // exit code 0, nothing written to crash.log
            }

            if (instanceStatus == SingleInstanceStatus.Acquired)
            {
                // Only the owning instance watches for activation requests.
                guard.StartActivationWatch(App.RequestWindowRestore);
            }
            else
            {
                // MayStart — the guard could not determine the instance state (unusable lock
                // path, permissions, exotic filesystem). Fail open and start normally: a broken
                // guard must never prevent the user from running the app.
                Log.Warning(
                    "Single-instance guard could not determine instance state for {LockPath}; " +
                    "starting normally (fail-open)",
                    guard.LockFilePath);
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during startup");
            CrashLogger.Write(ex, "Startup — BuildAvaloniaApp / StartWithClassicDesktopLifetime");
#if DEBUG
            throw;   // re-throw so the debugger still sees it in debug builds
#else
            Environment.Exit(1);   // clean exit in release — avoids SIGABRT / crash dialog
#endif
        }
        finally
        {
            // Releases the single-instance lock. Null-safe: on the early-exit path the guard is
            // already non-null and disposing it simply drops the (unheld) stream.
            guard?.Dispose();
            LoggingBootstrap.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .LogToTrace()
                     .UseReactiveUI();
}
