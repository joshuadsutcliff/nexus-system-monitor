using System.Text;
using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="MacOSProcName"/> — pure proc_name(3) buffer decode. No P/Invoke or file I/O,
/// so this runs identically on every OS.
///
/// Covers the live-verified macOS 26 corruption this class fixes: the kernel does not zero-pad
/// proc_name's output buffer past the name's NUL, so a reused buffer can carry stale printable
/// bytes from a PREVIOUS call past the current name's terminator (e.g. "contactsd\0k"). Decoding
/// only the syscall's actual returned length — not the whole fixed buffer — avoids the corruption.
/// </summary>
public class MacOSProcNameTests
{
    [Fact]
    public void DecodeProcName_CleanName_ReturnsName()
    {
        var buffer = new byte[256];
        var bytes  = Encoding.UTF8.GetBytes("zsh");
        Array.Copy(bytes, buffer, bytes.Length);

        MacOSProcName.DecodeProcName(buffer, bytes.Length).Should().Be("zsh");
    }

    [Fact]
    public void DecodeProcName_StaleBytesPastNul_TruncatesAtReturnedLength()
    {
        // Live-verified pattern: a reused buffer holds "contactsd\0k" — "contactsd" (9 bytes,
        // proc_name's real live-verified return value) followed by its NUL terminator, then a
        // stray 'k' left over from a previous, longer name that never got zeroed by the kernel.
        var buffer = new byte[256];
        var bytes  = Encoding.UTF8.GetBytes("contactsd\0k");
        Array.Copy(bytes, buffer, bytes.Length);

        MacOSProcName.DecodeProcName(buffer, 9).Should().Be("contactsd");
    }

    [Fact]
    public void DecodeProcName_ReturnedLenZero_ReturnsEmpty()
    {
        var buffer = new byte[256];
        MacOSProcName.DecodeProcName(buffer, 0).Should().Be(string.Empty);
    }

    [Fact]
    public void DecodeProcName_ReturnedLenNegative_ReturnsEmpty()
    {
        var buffer = new byte[256];
        MacOSProcName.DecodeProcName(buffer, -1).Should().Be(string.Empty);
    }

    [Fact]
    public void DecodeProcName_ReturnedLenGreaterThanBuffer_ClampsToBufferLength()
    {
        var buffer = new byte[5];
        var bytes  = Encoding.UTF8.GetBytes("hello");
        Array.Copy(bytes, buffer, bytes.Length);

        MacOSProcName.DecodeProcName(buffer, 10).Should().Be("hello");
    }

    [Fact]
    public void DecodeProcName_EmbeddedNulInsideReturnedLen_TruncatesAtNul()
    {
        // Defense-in-depth case: an embedded NUL appears BEFORE the reported length ends. Should
        // not happen in practice (the kernel's returned length is the true name length), but the
        // helper must not decode past it if it ever does.
        var buffer = new byte[256];
        var bytes  = Encoding.UTF8.GetBytes("ab\0cd");
        Array.Copy(bytes, buffer, bytes.Length);

        MacOSProcName.DecodeProcName(buffer, 5).Should().Be("ab");
    }
}
