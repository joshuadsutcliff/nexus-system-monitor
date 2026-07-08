using FluentAssertions;
using NexusMonitor.Platform.Linux;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="ProcFdClassifier"/> — pure classification of
/// <c>/proc/&lt;pid&gt;/fd/*</c> readlink(2) targets for the Handle Viewer. No file I/O, so
/// these run identically on every OS.
/// </summary>
public class ProcFdClassifierTests
{
    [Fact]
    public void ClassifyTypeName_SocketTarget_IsSocket()
    {
        ProcFdClassifier.ClassifyTypeName("socket:[12345]").Should().Be("Socket");
    }

    [Fact]
    public void ClassifyTypeName_PipeTarget_IsPipe()
    {
        ProcFdClassifier.ClassifyTypeName("pipe:[67890]").Should().Be("Pipe");
    }

    [Theory]
    [InlineData("anon_inode:[eventfd]", "eventfd")]
    [InlineData("anon_inode:[eventpoll]", "eventpoll")]
    [InlineData("anon_inode:[timerfd]", "timerfd")]
    [InlineData("anon_inode:inotify", "inotify")]
    public void ClassifyTypeName_AnonInodeTarget_ReturnsSubtype(string target, string expectedSubtype)
    {
        ProcFdClassifier.ClassifyTypeName(target).Should().Be(expectedSubtype);
    }

    [Fact]
    public void ClassifyTypeName_AnonInodeWithNoSubtype_ReturnsAnonymous()
    {
        ProcFdClassifier.ClassifyTypeName("anon_inode:").Should().Be("Anonymous");
    }

    [Fact]
    public void ClassifyTypeName_AbsolutePath_IsFile()
    {
        ProcFdClassifier.ClassifyTypeName("/home/user/document.txt").Should().Be("File");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ClassifyTypeName_EmptyOrNullTarget_IsUnknown(string? target)
    {
        ProcFdClassifier.ClassifyTypeName(target!).Should().Be("Unknown");
    }

    [Fact]
    public void ClassifyTypeName_UnrecognizedTarget_IsUnknownRatherThanFabricated()
    {
        ProcFdClassifier.ClassifyTypeName("some-weird-magic-link-format").Should().Be("Unknown");
    }
}
