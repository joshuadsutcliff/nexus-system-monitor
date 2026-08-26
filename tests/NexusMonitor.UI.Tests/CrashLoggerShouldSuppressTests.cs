using FluentAssertions;
using Xunit;

namespace NexusMonitor.UI.Tests;

/// <summary>
/// Issue #42: on Linux/KDE, the D-Bus StatusNotifierItem tray teardown posts a continuation to
/// the Avalonia dispatcher after shutdown has begun, surfacing as an unhandled
/// TaskCanceledException/ObjectDisposedException on a background thread and polluting
/// crash.log. CrashLogger.ShouldSuppress narrowly recognizes exactly that case so it can be
/// logged at a lower level instead of written as a crash. These tests exercise the predicate
/// exhaustively; this is the only verification available on macOS since the underlying bug is
/// Linux-only at runtime.
/// </summary>
public class CrashLoggerShouldSuppressTests
{
    // ── Helpers to build exceptions with a real, controlled stack trace ─────────────────

    // Throwing from Tmds.DBus.Protocol.DBusConnection (see TestSupport/FakeTmdsDBusConnection.cs)
    // and catching here makes ex.StackTrace's top frame literally read
    // "at Tmds.DBus.Protocol.DBusConnection.Dispose()", matching issue #42's real stack without
    // depending on the actual Tmds.DBus package.
    private static Exception ThrowAndCatchTaskCanceledFromDBus()
    {
        try
        {
            global::Tmds.DBus.Protocol.DBusConnection.Dispose();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception ThrowAndCatchObjectDisposedFromDBus()
    {
        try
        {
            global::Tmds.DBus.Protocol.DBusConnection.DisposeObjectDisposed();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Same exception types, but thrown from a frame that has nothing to do with Tmds.DBus, so
    // the stack trace text does NOT contain "Tmds.DBus".
    private static Exception ThrowAndCatchLocally(Exception toThrow)
    {
        try
        {
            throw toThrow;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Wrong exception type, but thrown from the Tmds.DBus stand-in frame, so the stack DOES
    // contain "Tmds.DBus" while the type is not suppressible.
    private static Exception ThrowAndCatchWrongTypeFromDBusLikeFrame()
    {
        try
        {
            global::Tmds.DBus.Protocol.DBusConnection.ThrowWrongType();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ── Suppresses ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Suppresses_ShuttingDown_TaskCanceled_WithTmdsDBusStack()
    {
        var ex = ThrowAndCatchTaskCanceledFromDBus();

        CrashLogger.ShouldSuppress(ex, isShuttingDown: true).Should().BeTrue();
    }

    [Fact]
    public void Suppresses_ShuttingDown_ObjectDisposed_WithTmdsDBusStack()
    {
        var ex = ThrowAndCatchObjectDisposedFromDBus();

        CrashLogger.ShouldSuppress(ex, isShuttingDown: true).Should().BeTrue();
    }

    [Fact]
    public void Suppresses_AggregateException_WrappingTaskCanceled_WithTmdsDBusStack_WhenShuttingDown()
    {
        var inner = ThrowAndCatchTaskCanceledFromDBus();
        var agg = new AggregateException("wrapped", inner);

        CrashLogger.ShouldSuppress(agg, isShuttingDown: true).Should().BeTrue();
    }

    // ── Does NOT suppress ────────────────────────────────────────────────────────────────

    [Fact]
    public void DoesNotSuppress_WhenNotShuttingDown()
    {
        var ex = ThrowAndCatchTaskCanceledFromDBus();

        CrashLogger.ShouldSuppress(ex, isShuttingDown: false).Should().BeFalse();
    }

    [Fact]
    public void DoesNotSuppress_DifferentExceptionType_EvenWithTmdsDBusStack()
    {
        var ex = ThrowAndCatchWrongTypeFromDBusLikeFrame();

        CrashLogger.ShouldSuppress(ex, isShuttingDown: true).Should().BeFalse();
    }

    [Fact]
    public void DoesNotSuppress_RightType_ButStackHasNoTmdsDBus()
    {
        var ex = ThrowAndCatchLocally(new TaskCanceledException("canceled elsewhere"));

        CrashLogger.ShouldSuppress(ex, isShuttingDown: true).Should().BeFalse();
    }

    [Fact]
    public void DoesNotSuppress_NullException()
    {
        CrashLogger.ShouldSuppress(null, isShuttingDown: true).Should().BeFalse();
    }
}
