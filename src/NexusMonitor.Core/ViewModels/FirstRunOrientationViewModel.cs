using CommunityToolkit.Mvvm.ComponentModel;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Core.ViewModels;

/// <summary>
/// ViewModel for the one-time first-run welcome overlay. Pure C# — no Avalonia dependencies.
/// Testable from NexusMonitor.Core.Tests. Mirrors <see cref="CommandPaletteViewModel"/>'s
/// shape (settings + onSave injected, no direct SettingsService dependency).
/// </summary>
public partial class FirstRunOrientationViewModel : ObservableObject
{
    private readonly AppSettings? _settings;
    private readonly Action? _onSave;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// Constructs the ViewModel from the live <see cref="AppSettings"/> instance.
    /// <see cref="IsOpen"/> starts true iff the user has never dismissed this overlay
    /// (<see cref="AppSettings.HasSeenFirstRunOrientation"/> is false) — including the
    /// existing-user migration guard's effect, since that guard already ran by the time
    /// <see cref="Services.SettingsService.Current"/> is handed to this constructor.
    /// </summary>
    /// <param name="settings">Live AppSettings instance.</param>
    /// <param name="onSave">
    /// Called immediately on <see cref="Dismiss"/> (e.g. settingsService.Save()) so the
    /// dismissal is persisted right away and the overlay never constructs again, even within
    /// the same session — per the settings save-race semantics documented on
    /// <see cref="Services.SettingsService.Save"/>, this captures a JSON snapshot on the
    /// caller's (UI) thread at call time.
    /// </param>
    public FirstRunOrientationViewModel(AppSettings settings, Action onSave)
    {
        _settings = settings;
        _onSave = onSave;
        IsOpen = !settings.HasSeenFirstRunOrientation;
    }

    /// <summary>
    /// Dismisses the overlay: marks it seen, persists immediately, and closes. Idempotent —
    /// a second call (e.g. Enter and the button click both firing) is a no-op.
    /// </summary>
    public void Dismiss()
    {
        if (!IsOpen) return;

        IsOpen = false;
        if (_settings is not null) _settings.HasSeenFirstRunOrientation = true;
        _onSave?.Invoke();
    }
}
