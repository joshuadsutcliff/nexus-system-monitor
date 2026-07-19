using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.ViewModels;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class FirstRunOrientationViewModelTests
{
    [Fact]
    public void Constructor_HasNotSeenOverlay_IsOpenTrue()
    {
        var settings = new AppSettings { HasSeenFirstRunOrientation = false };
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => { });

        vm.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Constructor_AlreadySeenOverlay_IsOpenFalse()
    {
        var settings = new AppSettings { HasSeenFirstRunOrientation = true };
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => { });

        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Dismiss_ClosesOverlay()
    {
        var settings = new AppSettings { HasSeenFirstRunOrientation = false };
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => { });

        vm.Dismiss();

        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Dismiss_SetsHasSeenFlagOnSettings()
    {
        var settings = new AppSettings { HasSeenFirstRunOrientation = false };
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => { });

        vm.Dismiss();

        settings.HasSeenFirstRunOrientation.Should().BeTrue();
    }

    [Fact]
    public void Dismiss_InvokesOnSaveImmediately()
    {
        var settings = new AppSettings { HasSeenFirstRunOrientation = false };
        var saveCount = 0;
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => saveCount++);

        vm.Dismiss();

        saveCount.Should().Be(1, "the flag must persist immediately on dismissal, not on some later save");
    }

    [Fact]
    public void Dismiss_CalledTwice_IsIdempotent_OnSaveOnlyInvokedOnce()
    {
        // Enter and the "Get started" button click could both fire dismissal in the same
        // frame — the second call must be a no-op, not a double-save.
        var settings = new AppSettings { HasSeenFirstRunOrientation = false };
        var saveCount = 0;
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => saveCount++);

        vm.Dismiss();
        vm.Dismiss();

        saveCount.Should().Be(1);
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Dismiss_WhenNeverOpened_DoesNotInvokeOnSave()
    {
        // Existing user (HasSeenFirstRunOrientation already true) — the overlay never
        // constructs as open, so nothing should ever call Dismiss in real usage, but this
        // pins the guard is IsOpen-driven, not unconditional.
        var settings = new AppSettings { HasSeenFirstRunOrientation = true };
        var saveCount = 0;
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => saveCount++);

        vm.Dismiss();

        saveCount.Should().Be(0);
    }

    [Fact]
    public void IsOpen_PropertyChanged_FiresOnDismiss()
    {
        var settings = new AppSettings { HasSeenFirstRunOrientation = false };
        var vm = new FirstRunOrientationViewModel(settings, onSave: () => { });
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsOpen)) fired = true; };

        vm.Dismiss();

        fired.Should().BeTrue();
    }
}
