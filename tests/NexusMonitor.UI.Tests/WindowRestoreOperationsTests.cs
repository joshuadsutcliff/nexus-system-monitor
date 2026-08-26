using System.Collections.Generic;
using FluentAssertions;
using NexusMonitor.UI.Services;
using Xunit;

namespace NexusMonitor.UI.Tests;

/// <summary>
/// Issue #43 (KDE tray left-click doesn't restore a minimized window): the bug was purely in the
/// ORDER of Show/WindowState=Normal/Activate. A real Avalonia <c>Window</c> can't be constructed
/// in this test project without a configured Avalonia application (no Avalonia.Headless
/// dependency here, and no other test in this project constructs a control/window), so these
/// tests exercise the pure, extracted seam in <see cref="WindowRestoreOperations"/> against a
/// recording fake and assert on the exact call sequence. That sequence is the entire bug: these
/// tests fail against the old order (Activate before Normal) and pass against the fixed order
/// (Show, then Normal, then Activate).
/// </summary>
public class WindowRestoreOperationsTests
{
    private sealed class RecordingWindowRestoreTarget : IWindowRestoreTarget
    {
        public List<string> Calls { get; } = new();

        public void Show() => Calls.Add("Show");
        public void SetWindowStateNormal() => Calls.Add("Normal");
        public void Activate() => Calls.Add("Activate");
    }

    [Fact]
    public void Restore_CallsShowThenNormalThenActivate_InThatExactOrder()
    {
        var target = new RecordingWindowRestoreTarget();

        WindowRestoreOperations.Restore(target);

        target.Calls.Should().Equal("Show", "Normal", "Activate");
    }

    [Fact]
    public void Restore_ActivateIsCalledAfterWindowStateIsSetNormal()
    {
        // This is the precise regression this issue is about: the old code called Activate()
        // while the window could still be Iconic/Minimized, which is a no-op on X11/XWayland.
        var target = new RecordingWindowRestoreTarget();

        WindowRestoreOperations.Restore(target);

        var normalIndex = target.Calls.IndexOf("Normal");
        var activateIndex = target.Calls.IndexOf("Activate");
        normalIndex.Should().BeLessThan(activateIndex);
    }

    [Fact]
    public void Restore_ShowIsCalledBeforeActivate()
    {
        // Covers the hidden-to-tray case: Show() must happen before Activate() so a window
        // that isn't presented at all gets mapped before focus is requested.
        var target = new RecordingWindowRestoreTarget();

        WindowRestoreOperations.Restore(target);

        var showIndex = target.Calls.IndexOf("Show");
        var activateIndex = target.Calls.IndexOf("Activate");
        showIndex.Should().BeLessThan(activateIndex);
    }

    [Fact]
    public void Restore_CallsAllThreeOperationsExactlyOnce()
    {
        var target = new RecordingWindowRestoreTarget();

        WindowRestoreOperations.Restore(target);

        target.Calls.Should().HaveCount(3);
    }
}
