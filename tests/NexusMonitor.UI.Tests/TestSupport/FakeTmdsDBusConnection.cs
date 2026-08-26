// Stand-in for Tmds.DBus.Protocol.DBusConnection, the real type whose Dispose() teardown
// is at the bottom of issue #42's reported stack trace. Declared under the actual
// "Tmds.DBus.Protocol" namespace (not a nested class) so that a caught exception's
// ex.StackTrace text genuinely contains "Tmds.DBus", matching what
// CrashLogger.ShouldSuppress looks for, without taking a dependency on the real Tmds.DBus
// NuGet package.
namespace Tmds.DBus.Protocol;

internal static class DBusConnection
{
    public static void Dispose() => throw new TaskCanceledException("dispose canceled");

    public static void DisposeObjectDisposed() => throw new ObjectDisposedException("DBusConnection");

    // Used only to prove ShouldSuppress does NOT match on stack content alone: a Tmds.DBus
    // frame, but an exception type that is neither TaskCanceledException nor
    // ObjectDisposedException.
    public static void ThrowWrongType() => throw new InvalidOperationException("not suppressible");
}
