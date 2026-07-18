using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Rules;
using NexusMonitor.Core.Services;
using NexusMonitor.Core.Tests.Helpers;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingsService"/>: loading, saving, persistence, migration, and disposal.
///
/// INVARIANT — never touch the real per-user settings file. This class used to construct
/// <see cref="SettingsService"/> against the actual %AppData%/NexusMonitor/settings.json (Windows)
/// / ~/Library/Application Support/NexusMonitor/settings.json (macOS) path, backing up and
/// restoring it around every test. That is exactly what caused a confirmed real-data-loss
/// incident: a full-suite run on a dev machine overwrote the live settings.json with test
/// fixture values (e.g. UpdateIntervalMs 9999) — twice, across two separate sessions.
///
/// Every test in this class now uses <see cref="_settingsPath"/>, a unique
/// Path.Combine(Path.GetTempPath(), "NexusMonitorTests", &lt;guid&gt;) directory created fresh
/// per test instance (xUnit constructs a new <see cref="SettingsServiceTests"/> per [Fact]) and
/// deleted in <see cref="Dispose"/>. <see cref="CreateService"/> is the only place a
/// <see cref="SettingsService"/> is constructed in this file — it always passes
/// <see cref="_settingsPath"/> explicitly, so no test can ever resolve to the real path via the
/// constructor's default-path fallback.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    // Unique per test instance — never the real per-user settings directory.
    private readonly string _testDir;
    private readonly string _settingsPath;

    public SettingsServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "NexusMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _settingsPath = Path.Combine(_testDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — a leftover temp dir under Path.GetTempPath() is harmless
            // and must never fail a test.
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The only place a <see cref="SettingsService"/> is constructed in this file —
    /// always bound to this test instance's throwaway <see cref="_settingsPath"/>, never the
    /// real per-user path.</summary>
    private SettingsService CreateService() =>
        new(MockFactory.CreateLogger<SettingsService>().Object, _settingsPath);

    /// <summary>
    /// Writes (or deletes) the settings file at <see cref="_settingsPath"/>, creates a
    /// <see cref="SettingsService"/> bound to it, and runs the test body.
    /// </summary>
    private void WithSettings(string? json, Action<SettingsService> test)
    {
        if (json != null)
            File.WriteAllText(_settingsPath, json);
        else if (File.Exists(_settingsPath))
            File.Delete(_settingsPath);

        using var svc = CreateService();
        test(svc);
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_CurrentIsDefaults()
    {
        WithSettings(null, svc =>
        {
            svc.Current.Should().NotBeNull();
            svc.Current.UpdateIntervalMs.Should().Be(new AppSettings().UpdateIntervalMs);
            svc.Current.Rules.Should().BeEmpty();
        });
    }

    [Fact]
    public void Load_ValidJson_DeserializesCorrectly()
    {
        var json = """{"ThemeMode":"Light","UpdateIntervalMs":1000}""";
        WithSettings(json, svc =>
        {
            svc.Current.ThemeMode.Should().Be("Light");
            svc.Current.UpdateIntervalMs.Should().Be(1000);
        });
    }

    [Fact]
    public void Load_CorruptJson_FallsBackToDefaults()
    {
        WithSettings("{ not valid json", svc =>
        {
            svc.Current.UpdateIntervalMs.Should().Be(new AppSettings().UpdateIntervalMs);
            svc.Current.Rules.Should().BeEmpty();
        });
    }

    [Fact]
    public void Load_EmptyJsonObject_MigrationApplied()
    {
        // "{}" is valid JSON. ThemeMode is absent, so the migration branch fires.
        // IsDarkTheme defaults to true (C# initializer on AppSettings), so ThemeMode
        // is derived as "Dark" by the migration logic.
        WithSettings("{}", svc =>
        {
            svc.Current.ThemeMode.Should().Be("Dark");
            svc.Current.UpdateIntervalMs.Should().Be(new AppSettings().UpdateIntervalMs);
        });
    }

    [Fact]
    public void Load_Migration_ThemeModeAbsent_IsDarkThemeTrue_DerivesThemeModeDark()
    {
        // Old settings file has IsDarkTheme but no ThemeMode field.
        var json = """{"IsDarkTheme":true,"UpdateIntervalMs":2000}""";
        WithSettings(json, svc =>
        {
            svc.Current.ThemeMode.Should().Be("Dark");
        });
    }

    [Fact]
    public void Load_Migration_ThemeModeAbsent_IsDarkThemeFalse_DerivesThemeModeLight()
    {
        var json = """{"IsDarkTheme":false,"UpdateIntervalMs":2000}""";
        WithSettings(json, svc =>
        {
            svc.Current.ThemeMode.Should().Be("Light");
        });
    }

    [Fact]
    public void Load_Migration_ThemeModePresent_NotOverridden()
    {
        // When ThemeMode is present, the migration branch must NOT overwrite it.
        var json = """{"ThemeMode":"System","IsDarkTheme":false}""";
        WithSettings(json, svc =>
        {
            svc.Current.ThemeMode.Should().Be("System");
        });
    }

    [Fact]
    public void Load_NoFile_DefaultThemeModeIsSystem()
    {
        WithSettings(null, svc =>
        {
            svc.Current.ThemeMode.Should().Be("System");
        });
    }

    // ── Saving (via Dispose — immediate write, no debounce) ───────────────────

    [Fact]
    public void Dispose_WritesSettingsToDisk()
    {
        // First instance: mutate and dispose (writes immediately).
        var svc1 = CreateService();
        svc1.Current.ThemeMode = "Light";
        svc1.Current.UpdateIntervalMs = 500;
        svc1.Dispose();

        // Second instance: must load what first instance wrote.
        using var svc2 = CreateService();
        svc2.Current.ThemeMode.Should().Be("Light");
        svc2.Current.UpdateIntervalMs.Should().Be(500);
    }

    [Fact]
    public void Save_ThenDispose_PersistsLatestState()
    {
        var svc = CreateService();
        svc.Current.UpdateIntervalMs = 5000;
        svc.Save();   // Schedules a write 250 ms from now …
        svc.Current.UpdateIntervalMs = 999;
        svc.Dispose(); // … but Dispose writes immediately with the LATEST value.

        using var svc2 = CreateService();
        svc2.Current.UpdateIntervalMs.Should().Be(999);
    }

    // ── Atomic write ──────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_WritesAtomically_NoTmpFileLeftBehind()
    {
        var svc = CreateService();
        svc.Dispose();

        var tmpPath = _settingsPath + ".tmp";
        File.Exists(tmpPath).Should().BeFalse(
            "WriteToDisk() must rename the .tmp file to the final path before returning");
    }

    // ── Round-trip for complex types ──────────────────────────────────────────

    [Fact]
    public void Dispose_WithRules_RulesPersistedAndLoadedBack()
    {
        var rule = new ProcessRule
        {
            Name = "TestRule",
            ProcessNamePattern = "chrome",
            IsEnabled = true
        };

        var svc1 = CreateService();
        svc1.Current.Rules.Add(rule);
        svc1.Dispose();

        using var svc2 = CreateService();
        svc2.Current.Rules.Should().HaveCount(1);
        svc2.Current.Rules[0].Name.Should().Be("TestRule");
        svc2.Current.Rules[0].ProcessNamePattern.Should().Be("chrome");
    }

    // ── Dispose / IDisposable ─────────────────────────────────────────────────

    [Fact]
    public void Dispose_IsIdempotent_NoException()
    {
        WithSettings(null, svc =>
        {
            // Second Dispose() must not throw even though the timer has already been nulled.
            var act = () => svc.Dispose();
            act.Should().NotThrow();
        });
        // The using block in WithSettings calls Dispose() a third time; still must not throw.
    }

    [Fact]
    public void Dispose_AfterSave_WritesLatestDebouncedState()
    {
        var svc = CreateService();
        svc.Current.UpdateIntervalMs = 1234;
        svc.Save();
        svc.Dispose(); // cancels debounce timer and writes synchronously

        File.Exists(_settingsPath).Should().BeTrue("Dispose must write the file");
        var contents = File.ReadAllText(_settingsPath);
        contents.Should().Contain("1234");
    }

    // ── Multiple instances ────────────────────────────────────────────────────

    [Fact]
    public void TwoInstances_LoadSameFile_SeesSameData()
    {
        // Write via first instance.
        var svc1 = CreateService();
        svc1.Current.ThemeMode = "Light";
        svc1.Current.PrometheusPort = 7777;
        svc1.Dispose();

        // Read via second instance.
        using var svc2 = CreateService();
        svc2.Current.ThemeMode.Should().Be("Light");
        svc2.Current.PrometheusPort.Should().Be(7777);
    }

    // ── Session persistence ───────────────────────────────────────────────────

    [Fact]
    public void SessionFields_DefaultValues_AreCorrect()
    {
        WithSettings(null, svc =>
        {
            svc.Current.LastActiveTab.Should().Be(string.Empty);
            svc.Current.LastWindowWidth.Should().Be(0);
            svc.Current.LastWindowHeight.Should().Be(0);
            svc.Current.LastWindowX.Should().Be(-1);
            svc.Current.LastWindowY.Should().Be(-1);
            svc.Current.LastWindowState.Should().Be("Normal");
        });
    }

    [Fact]
    public void SessionFields_PersistAcrossSaveAndReload()
    {
        var svc1 = CreateService();
        svc1.Current.LastActiveTab    = "Processes";
        svc1.Current.LastWindowWidth  = 1280;
        svc1.Current.LastWindowHeight = 720;
        svc1.Current.LastWindowX      = 100;
        svc1.Current.LastWindowY      = 200;
        svc1.Current.LastWindowState  = "Maximized";
        svc1.Dispose();   // flushes synchronously

        using var svc2 = CreateService();
        svc2.Current.LastActiveTab.Should().Be("Processes");
        svc2.Current.LastWindowWidth.Should().Be(1280);
        svc2.Current.LastWindowHeight.Should().Be(720);
        svc2.Current.LastWindowX.Should().Be(100);
        svc2.Current.LastWindowY.Should().Be(200);
        svc2.Current.LastWindowState.Should().Be("Maximized");
    }

    // ── Motion & Depth (Phase 8 — UI design polish) ───────────────────────────

    [Fact]
    public void MotionDepthFields_DefaultValues_AreCorrect()
    {
        WithSettings(null, svc =>
        {
            svc.Current.AnimationSpeed.Should().Be(1.0);
            svc.Current.AnimatePageTransitions.Should().BeTrue();
            svc.Current.AnimateHoverEffects.Should().BeTrue();
            svc.Current.AnimatePopOutMotion.Should().BeTrue();
            svc.Current.AnimateEditChrome.Should().BeTrue();
            // Quiet-defaults ruling (2026-07-11): AnimateValueChanges/AnimateSpecularShimmer now
            // default to false on a fresh install — see AppSettings' doc comment and the
            // QuietDefaults_* tests below for the existing-user migration guard that protects
            // anyone who already had a settings.json before this change.
            svc.Current.AnimateValueChanges.Should().BeFalse();
            svc.Current.AnimateSpecularShimmer.Should().BeFalse();
            svc.Current.DepthIntensity.Should().Be(0.5);
            svc.Current.ScaleTextWithWidgetSize.Should().BeTrue();
        });
    }

    [Fact]
    public void MotionDepthFields_PersistAcrossSaveAndReload()
    {
        var svc1 = CreateService();
        svc1.Current.AnimationSpeed          = 1.5;
        svc1.Current.AnimatePageTransitions  = false;
        svc1.Current.AnimateHoverEffects     = false;
        svc1.Current.AnimatePopOutMotion     = false;
        svc1.Current.AnimateEditChrome       = false;
        svc1.Current.AnimateValueChanges     = false;
        svc1.Current.AnimateSpecularShimmer  = false;
        svc1.Current.DepthIntensity          = 0.9;
        svc1.Current.ScaleTextWithWidgetSize = false;
        svc1.Dispose();   // flushes synchronously

        using var svc2 = CreateService();
        svc2.Current.AnimationSpeed.Should().Be(1.5);
        svc2.Current.AnimatePageTransitions.Should().BeFalse();
        svc2.Current.AnimateHoverEffects.Should().BeFalse();
        svc2.Current.AnimatePopOutMotion.Should().BeFalse();
        svc2.Current.AnimateEditChrome.Should().BeFalse();
        svc2.Current.AnimateValueChanges.Should().BeFalse();
        svc2.Current.AnimateSpecularShimmer.Should().BeFalse();
        svc2.Current.DepthIntensity.Should().Be(0.9);
        svc2.Current.ScaleTextWithWidgetSize.Should().BeFalse();
    }

    // ── Timer cleanup ─────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DisposesDebounceTimer_NoTimerFireAfterDisposal()
    {
        var svc = CreateService();
        svc.Current.UpdateIntervalMs = 9999;
        svc.Save();    // arms debounce timer (fires at T+250 ms)
        svc.Dispose(); // must cancel that timer before it fires

        // Wait beyond the debounce window; if the timer still fired we would get
        // a write from a disposed object — no exception should surface.
        Thread.Sleep(400);

        // The file should exist (Dispose wrote it) and must not be corrupted.
        File.Exists(_settingsPath).Should().BeTrue();
        var act = () => File.ReadAllText(_settingsPath);
        act.Should().NotThrow();
    }

    // ── Quiet-defaults migration guard (first-launch redesign, 2026-07-11) ────────────────────
    //
    // Three scenarios per the task brief:
    //  1. No settings.json at all -> genuinely fresh install -> NEW quiet defaults apply.
    //  2. A settings.json a real prior version wrote (Save() always serializes every key) ->
    //     the OLD values are already explicit in the file -> preserved with ZERO migration code
    //     (this is the "full-object Save() already protects existing users" claim — the fixture
    //     below deliberately does NOT rely on MigrateQuietDefaultsGap to prove that).
    //  3. A hand-made older file that PREDATES one of the affected keys (never serialized it at
    //     all) -> MigrateQuietDefaultsGap pins the OLD default for exactly the missing key(s).

    [Fact]
    public void QuietDefaults_NoFile_NewQuietDefaultsApply()
    {
        WithSettings(null, svc =>
        {
            svc.Current.IsGlassEnabled.Should().BeFalse();
            svc.Current.BackdropBlurMode.Should().Be("None");
            svc.Current.IsSpecularEnabled.Should().BeFalse();
            svc.Current.AnimateSpecularShimmer.Should().BeFalse();
            svc.Current.AnimateValueChanges.Should().BeFalse();
            svc.Current.ActiveThemePresetId.Should().Be("nexus-default");
        });
    }

    [Fact]
    public void QuietDefaults_FullObjectFileFromPriorVersion_OldValuesPreservedWithoutMigrationCode()
    {
        // Simulates exactly what a real prior version (e.g. v0.6.0, before this change) would
        // have written to settings.json: a FULL serialization (every public property, via the
        // same JsonSerializerOptions shape SettingsService.WriteToDisk uses) of an AppSettings
        // instance carrying the OLD "loud" defaults for the 6 affected keys. Because every key is
        // explicitly present, Deserialize reproduces these values untouched — this must hold even
        // if MigrateQuietDefaultsGap were deleted entirely, since every key it looks for is
        // present here (the guard's own foreach short-circuits via `TryGetProperty` finding each
        // key and doing nothing).
        var priorVersionSettings = new AppSettings
        {
            IsGlassEnabled         = true,
            BackdropBlurMode       = "Acrylic",
            IsSpecularEnabled      = true,
            AnimateSpecularShimmer = true,
            AnimateValueChanges    = true,
            ActiveThemePresetId    = "",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            priorVersionSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        WithSettings(json, svc =>
        {
            svc.Current.IsGlassEnabled.Should().BeTrue();
            svc.Current.BackdropBlurMode.Should().Be("Acrylic");
            svc.Current.IsSpecularEnabled.Should().BeTrue();
            svc.Current.AnimateSpecularShimmer.Should().BeTrue();
            svc.Current.AnimateValueChanges.Should().BeTrue();
            svc.Current.ActiveThemePresetId.Should().Be("");
        });
    }

    [Fact]
    public void QuietDefaults_PartialFileMissingAllAffectedKeys_MigrationGuardPinsOldDefaults()
    {
        // A hand-made file mimicking a genuinely ancient settings.json that predates every one of
        // the 6 affected properties — none of them were ever serialized, so without
        // MigrateQuietDefaultsGap, Deserialize would silently apply the NEW initializer defaults
        // (false/"None"/false/false/false/"nexus-default") to an existing user who never asked
        // for a re-skin. This is the case the migration guard exists for.
        var json = """{"ThemeMode":"Dark","UpdateIntervalMs":2000}""";

        WithSettings(json, svc =>
        {
            svc.Current.IsGlassEnabled.Should().BeTrue();
            svc.Current.BackdropBlurMode.Should().Be("Acrylic");
            svc.Current.IsSpecularEnabled.Should().BeTrue();
            svc.Current.AnimateSpecularShimmer.Should().BeTrue();
            svc.Current.AnimateValueChanges.Should().BeTrue();
            svc.Current.ActiveThemePresetId.Should().Be("");
        });
    }

    [Fact]
    public void QuietDefaults_PartialFileWithSomeKeysPresent_OnlyMissingKeysAreMigrated()
    {
        // Per-key granularity: a file that already carries an explicit (new-style) value for
        // IsGlassEnabled but predates the other 5 keys. The present key must be left exactly as
        // written (even though it happens to equal the new default here — the point is it's not
        // re-derived), while the missing keys still get the OLD default each.
        var json = """{"ThemeMode":"Dark","IsGlassEnabled":false}""";

        WithSettings(json, svc =>
        {
            svc.Current.IsGlassEnabled.Should().BeFalse("the file already pinned this key explicitly");
            svc.Current.BackdropBlurMode.Should().Be("Acrylic", "this key was absent from the file");
            svc.Current.IsSpecularEnabled.Should().BeTrue("this key was absent from the file");
            svc.Current.AnimateSpecularShimmer.Should().BeTrue("this key was absent from the file");
            svc.Current.AnimateValueChanges.Should().BeTrue("this key was absent from the file");
            svc.Current.ActiveThemePresetId.Should().Be("", "this key was absent from the file");
        });
    }

    [Fact]
    public void QuietDefaults_LoadCalledTwiceOverSameUnchangedFile_IsIdempotent()
    {
        // Gate-review suggestion (2026-07-11): Load() is private, called only once from the
        // constructor, and MigrateQuietDefaultsGap is a pure function of the parsed JsonElement
        // (not of any prior Current state) — so two separate SettingsServices constructed
        // back-to-back against the SAME unchanged file (never re-Save()'d in between) must
        // migrate to identical Current state both times. This is cheap insurance against a
        // future edit that makes the migration stateful/order-dependent.
        //
        // Uses this class's own throwaway _settingsPath/CreateService() (never the real per-user
        // path) — rebased onto main's temp-dir redirect (see class doc): the original version of
        // this test predated that redirect and would otherwise resolve to the real settings path
        // via the constructor's default-path fallback, exactly the incident that redirect exists
        // to prevent.
        var json = """{"ThemeMode":"Dark","IsGlassEnabled":false}""";
        File.WriteAllText(_settingsPath, json);

        using var svc1 = CreateService();
        using var svc2 = CreateService();

        svc2.Current.IsGlassEnabled.Should().Be(svc1.Current.IsGlassEnabled);
        svc2.Current.BackdropBlurMode.Should().Be(svc1.Current.BackdropBlurMode);
        svc2.Current.IsSpecularEnabled.Should().Be(svc1.Current.IsSpecularEnabled);
        svc2.Current.AnimateSpecularShimmer.Should().Be(svc1.Current.AnimateSpecularShimmer);
        svc2.Current.AnimateValueChanges.Should().Be(svc1.Current.AnimateValueChanges);
        svc2.Current.ActiveThemePresetId.Should().Be(svc1.Current.ActiveThemePresetId);

        // Pin the actual values too, not just cross-instance equality — both instances must
        // land on the same (correct) migrated state, not merely agree with each other on some
        // wrong-but-consistent value.
        svc2.Current.IsGlassEnabled.Should().BeFalse("the file already pinned this key explicitly");
        svc2.Current.BackdropBlurMode.Should().Be("Acrylic", "this key was absent from the file, both times");
        svc2.Current.IsSpecularEnabled.Should().BeTrue("this key was absent from the file, both times");
    }
}
