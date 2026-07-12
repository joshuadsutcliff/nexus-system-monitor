namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Pure decode of proc_name(3)'s output buffer. No P/Invoke here — <see cref="MacOSProcessProvider"/>
/// does the actual syscall — so the decode is unit-testable on every OS, mirroring the pure/IO split
/// used by <see cref="MacOSEfficiencyMode"/> and <c>LaunchdStartType</c> (Sym-1 Task 3).
///
/// Root cause (live-verified on macOS 26, this app instance's own reusable 256-byte buffer):
/// proc_name(3) returns the actual name byte-length (e.g. "contactsd" -> 9, "zsh" -> 3) as its int
/// return value, but the kernel does NOT zero-pad the buffer past the name's terminating NUL — stale
/// printable bytes from a PREVIOUS call can remain past that NUL. Decoding the entire fixed buffer
/// (as the old code did, ignoring the return value) turns those stale bytes into visible garbage
/// once the embedded NUL is stripped with TrimEnd — e.g. "contactsd\0k" renders as "contactsdk".
/// Decoding only the kernel-reported length fixes this at the source.
/// </summary>
public static class MacOSProcName
{
    /// <summary>
    /// Decodes a proc_name(3) buffer using the syscall's ACTUAL returned length rather than the
    /// whole fixed-size buffer. Returns <see cref="string.Empty"/> when <paramref name="returnedLen"/>
    /// is non-positive (proc_name failed or reported an empty name) — callers apply their own
    /// fallback (e.g. "pid{pid}") for that case. Defense-in-depth: if the sliced region still
    /// contains an embedded NUL (shouldn't happen — the kernel reports the name's true length — but
    /// costs nothing to guard), truncates at the first one rather than trusting <paramref
    /// name="returnedLen"/> blindly.
    /// </summary>
    public static string DecodeProcName(byte[] buffer, int returnedLen)
    {
        if (buffer is null || returnedLen <= 0)
            return string.Empty;

        int len = Math.Min(returnedLen, buffer.Length);

        int nulIndex = Array.IndexOf(buffer, (byte)0, 0, len);
        if (nulIndex >= 0)
            len = nulIndex;

        return len > 0 ? System.Text.Encoding.UTF8.GetString(buffer, 0, len) : string.Empty;
    }
}
