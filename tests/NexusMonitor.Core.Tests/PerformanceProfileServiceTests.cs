using System.Reactive.Subjects;
using System.Diagnostics;
using FluentAssertions;
using Moq;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Automation;
using NexusMonitor.Core.Gaming;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Tests.Helpers;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class PerformanceProfileServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static ProcessInfo MakeProcess(string name, int pid = 1234)
        => new ProcessInfo { Pid = pid, Name = name };

    private (PerformanceProfileService svc,
             Subject<IReadOnlyList<ProcessInfo>> processSubject,
             Mock<IProcessProvider> mockProcess,
             Mock<IPowerPlanProvider> mockPower)
        CreateService(AppSettings? settings = null)
    {
        var mockProcess = Helpers.MockFactory.CreateProcessProvider();
        var mockPower   = new Mock<IPowerPlanProvider>(MockBehavior.Loose);
        var logger      = Helpers.MockFactory.CreateLogger<PerformanceProfileService>();
        settings      ??= new AppSettings();

        var processSubject = new Subject<IReadOnlyList<ProcessInfo>>();
        mockProcess.Setup(p => p.GetProcessStream(It.IsAny<TimeSpan>()))
            .Returns(processSubject);

        var svc = new PerformanceProfileService(
            mockProcess.Object, mockPower.Object, settings, logger.Object);
        return (svc, processSubject, mockProcess, mockPower);
    }

    private (AppSettings settings, PerformanceProfile profile, Guid profileId) MakeSettings(
        string processName   = "chrome",
        ProcessPriority? priority = ProcessPriority.High,
        bool? efficiencyMode = null,
        bool changePowerPlan = false,
        string powerPlanName = "")
    {
        var profileId = Guid.NewGuid();
        var profile = new PerformanceProfile
        {
            Id              = profileId,
            Name            = "TestProfile",
            ChangePowerPlan = changePowerPlan,
            PowerPlanName   = powerPlanName,
            ProcessRules    = new List<ProfileProcessRule>
            {
                new() { ProcessNamePattern = processName, Priority = priority, EfficiencyMode = efficiencyMode }
            }
        };
        var settings = new AppSettings { PerformanceProfiles = new List<PerformanceProfile> { profile } };
        return (settings, profile, profileId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Timed out waiting for the service operation.");
            await Task.Delay(10);
        }
    }

    // ── Basic activate / deactivate ────────────────────────────────────────

    [Fact]
    public void ActivateProfile_ValidId_SetsIsActive()
    {
        var (settings, _, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        svc.ActivateProfile(profileId);

        svc.IsActive.Should().BeTrue();
        svc.Dispose();
    }

    [Fact]
    public void ActivateProfile_ValidId_SetsActiveProfileId()
    {
        var (settings, _, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        svc.ActivateProfile(profileId);

        svc.ActiveProfileId.Should().Be(profileId);
        svc.Dispose();
    }

    [Fact]
    public void ActivateProfile_ValidId_SetsActiveProfileName()
    {
        var (settings, profile, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        svc.ActivateProfile(profileId);

        svc.ActiveProfileName.Should().Be(profile.Name);
        svc.Dispose();
    }

    [Fact]
    public void ActivateProfile_InvalidId_NoChange()
    {
        var (settings, _, _) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        svc.ActivateProfile(Guid.NewGuid()); // ID not in settings

        svc.IsActive.Should().BeFalse();
        svc.Dispose();
    }

    [Fact]
    public void ActivateProfile_WhenAlreadyActive_DeactivatesFirst()
    {
        // Set up two profiles in settings
        var profileAId = Guid.NewGuid();
        var profileBId = Guid.NewGuid();
        var profileA = new PerformanceProfile { Id = profileAId, Name = "ProfileA" };
        var profileB = new PerformanceProfile { Id = profileBId, Name = "ProfileB" };
        var settings = new AppSettings
        {
            PerformanceProfiles = new List<PerformanceProfile> { profileA, profileB }
        };
        var (svc, _, _, _) = CreateService(settings);

        svc.ActivateProfile(profileAId);
        svc.ActivateProfile(profileBId);

        svc.ActiveProfileId.Should().Be(profileBId);
        svc.ActiveProfileName.Should().Be("ProfileB");
        svc.Dispose();
    }

    [Fact]
    public void DeactivateProfile_WhenActive_SetsIsActiveFalse()
    {
        var (settings, _, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        svc.ActivateProfile(profileId);
        svc.DeactivateProfile();

        svc.IsActive.Should().BeFalse();
        svc.Dispose();
    }

    [Fact]
    public void DeactivateProfile_WhenNotActive_NoOp()
    {
        var (svc, _, _, _) = CreateService();

        var act = () => svc.DeactivateProfile();

        act.Should().NotThrow();
        svc.Dispose();
    }

    // ── Observables ────────────────────────────────────────────────────────

    [Fact]
    public void ActivateProfile_FiresProfileChanged()
    {
        var (settings, profile, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        string? received = null;
        svc.ProfileChanged.Subscribe(name => received = name);

        svc.ActivateProfile(profileId);

        received.Should().Be(profile.Name);
        svc.Dispose();
    }

    [Fact]
    public void DeactivateProfile_FiresProfileChangedNull()
    {
        var (settings, _, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        var received = new List<string?>();
        svc.ProfileChanged.Subscribe(name => received.Add(name));

        svc.ActivateProfile(profileId);
        svc.DeactivateProfile();

        received.Should().HaveCount(2);
        received[0].Should().NotBeNull();  // profile name on activate
        received[1].Should().BeNull();     // null on deactivate
        svc.Dispose();
    }

    [Fact]
    public void ActivateProfile_FiresStatusMessage()
    {
        var (settings, profile, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        var messages = new List<string>();
        svc.StatusMessages.Subscribe(m => messages.Add(m));

        svc.ActivateProfile(profileId);

        messages.Should().Contain(m => m.Contains("activated") && m.Contains(profile.Name));
        svc.Dispose();
    }

    [Fact]
    public void DeactivateProfile_FiresStatusMessage()
    {
        var (settings, profile, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        var messages = new List<string>();
        svc.StatusMessages.Subscribe(m => messages.Add(m));

        svc.ActivateProfile(profileId);
        svc.DeactivateProfile();

        messages.Should().Contain(m => m.Contains("deactivated") && m.Contains(profile.Name));
        svc.Dispose();
    }

    // ── Process priority application ───────────────────────────────────────

    [Fact]
    public async Task ActivateProfile_SetsProcessPriority()
    {
        var (settings, _, profileId) = MakeSettings(
            processName: "chrome",
            priority: ProcessPriority.High);
        var (svc, processSubject, mockProcess, _) = CreateService(settings);

        svc.ActivateProfile(profileId);

        // Emit processes — satisfies the FirstAsync in ApplyProfileAsync
        processSubject.OnNext(new[] { MakeProcess("chrome", 1000) });
        await WaitUntilAsync(() => mockProcess.Invocations.Any(i =>
            i.Method.Name == nameof(IProcessProvider.ApplyTemporaryPriorityAsync)));

        mockProcess.Verify(p => p.ApplyTemporaryPriorityAsync(
                1000, ProcessPriority.High, It.IsAny<CancellationToken>()),
            Times.Once);
        svc.Dispose();
    }

    [Fact]
    public async Task DeactivateProfile_RestoresCapturedNativePriority()
    {
        var (settings, _, profileId) = MakeSettings(
            processName: "chrome",
            priority: ProcessPriority.High);
        var (svc, processSubject, mockProcess, _) = CreateService(settings);
        var receipt = new PriorityRestoreReceipt(1000, "test-native", 17);
        mockProcess.Setup(p => p.ApplyTemporaryPriorityAsync(
                1000, ProcessPriority.High, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityApplyResult.Success(receipt));

        svc.ActivateProfile(profileId);
        processSubject.OnNext(new[] { MakeProcess("chrome", 1000) });
        await WaitUntilAsync(() => mockProcess.Invocations.Any(i =>
            i.Method.Name == nameof(IProcessProvider.ApplyTemporaryPriorityAsync)));

        var report = await svc.DeactivateProfileAsync();

        report.Restored.Should().Be(1);
        mockProcess.Verify(p => p.RestorePriorityAsync(
                It.Is<PriorityRestoreReceipt>(r =>
                    r.Pid == 1000 && r.ProviderKind == "test-native" && r.NativeValue == 17),
                It.IsAny<CancellationToken>()), Times.Once);
        mockProcess.Verify(p => p.SetPriorityAsync(
                1000, It.IsAny<ProcessPriority>(), It.IsAny<CancellationToken>()), Times.Never);
        svc.Dispose();
    }

    [Fact]
    public async Task ApplyPriorityFailure_DoesNotCreateRestoreState()
    {
        var (settings, _, profileId) = MakeSettings(priority: ProcessPriority.High);
        var (svc, processSubject, mockProcess, _) = CreateService(settings);
        var failure = new ProcessPriorityFailure(
            ProcessPriorityFailureKind.PermissionDenied, "test", 5, "denied");
        mockProcess.Setup(p => p.ApplyTemporaryPriorityAsync(
                1234, ProcessPriority.High, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityApplyResult.Failed(failure));
        var messages = new List<string>();
        svc.StatusMessages.Subscribe(messages.Add);

        svc.ActivateProfile(profileId);
        processSubject.OnNext(new[] { MakeProcess("chrome") });
        await WaitUntilAsync(() => mockProcess.Invocations.Any(i =>
            i.Method.Name == nameof(IProcessProvider.ApplyTemporaryPriorityAsync)));

        svc.PendingRestoreCount.Should().Be(0);
        messages.Should().Contain(m => m.Contains("Priority change failed") && m.Contains("denied"));

        var report = await svc.DeactivateProfileAsync();
        report.Attempted.Should().Be(0);
        mockProcess.Verify(p => p.RestorePriorityAsync(
                It.IsAny<PriorityRestoreReceipt>(), It.IsAny<CancellationToken>()), Times.Never);
        svc.Dispose();
    }

    [Fact]
    public async Task RestoreProcessExited_IsTerminalAndNotPending()
    {
        var (settings, _, profileId) = MakeSettings(priority: ProcessPriority.High);
        var (svc, processSubject, mockProcess, _) = CreateService(settings);
        var receipt = new PriorityRestoreReceipt(1234, "test-native", 9);
        var failure = new ProcessPriorityFailure(
            ProcessPriorityFailureKind.ProcessExited, "test", 3, "process exited");
        mockProcess.Setup(p => p.ApplyTemporaryPriorityAsync(
                1234, ProcessPriority.High, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityApplyResult.Success(receipt));
        mockProcess.Setup(p => p.RestorePriorityAsync(
                It.Is<PriorityRestoreReceipt>(r => r.Pid == 1234), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PriorityRestoreResult.Exited(failure));

        svc.ActivateProfile(profileId);
        processSubject.OnNext(new[] { MakeProcess("chrome") });
        await WaitUntilAsync(() => mockProcess.Invocations.Any(i =>
            i.Method.Name == nameof(IProcessProvider.ApplyTemporaryPriorityAsync)));

        var report = await svc.DeactivateProfileAsync();

        report.Exited.Should().Be(1);
        report.Pending.Should().Be(0);
        svc.PendingRestoreCount.Should().Be(0);
        mockProcess.Verify(p => p.RestorePriorityAsync(
                It.Is<PriorityRestoreReceipt>(r => r.NativeValue == 9),
                It.IsAny<CancellationToken>()), Times.Once);
        svc.Dispose();
    }

    [Fact]
    public async Task PartialRestoreFailure_IsReportedPendingAndRetryClearsIt()
    {
        var profileId = Guid.NewGuid();
        var settings = new AppSettings
        {
            PerformanceProfiles = new List<PerformanceProfile>
            {
                new()
                {
                    Id = profileId,
                    Name = "TestProfile",
                    ProcessRules = new List<ProfileProcessRule>
                    {
                        new() { ProcessNamePattern = "chrome", Priority = ProcessPriority.BelowNormal }
                    }
                }
            }
        };
        var (svc, processSubject, mockProcess, _) = CreateService(settings);
        var first = new PriorityRestoreReceipt(1000, "test-native", 7);
        var second = new PriorityRestoreReceipt(1001, "test-native", 13);
        mockProcess.Setup(p => p.ApplyTemporaryPriorityAsync(
                It.IsAny<int>(), ProcessPriority.BelowNormal, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pid, ProcessPriority _, CancellationToken _) =>
                PriorityApplyResult.Success(pid == 1000 ? first : second));
        var restoreAttempts = new Dictionary<int, int>();
        var failure = new ProcessPriorityFailure(
            ProcessPriorityFailureKind.PermissionDenied, "test", 13, "restore denied");
        mockProcess.Setup(p => p.RestorePriorityAsync(
                It.IsAny<PriorityRestoreReceipt>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PriorityRestoreReceipt r, CancellationToken _) =>
            {
                restoreAttempts[r.Pid] = restoreAttempts.GetValueOrDefault(r.Pid) + 1;
                return r.Pid == 1001 && restoreAttempts[r.Pid] == 1
                    ? PriorityRestoreResult.Failed(failure)
                    : PriorityRestoreResult.Restored();
            });
        var messages = new List<string>();
        svc.StatusMessages.Subscribe(messages.Add);

        svc.ActivateProfile(profileId);
        processSubject.OnNext(new[] { MakeProcess("chrome", 1000), MakeProcess("chrome", 1001) });
        await WaitUntilAsync(() => mockProcess.Invocations.Count(i =>
            i.Method.Name == nameof(IProcessProvider.ApplyTemporaryPriorityAsync)) == 2);

        var firstReport = await svc.DeactivateProfileAsync();

        firstReport.Restored.Should().Be(1);
        firstReport.Pending.Should().Be(1);
        firstReport.IsPartial.Should().BeTrue();
        svc.PendingRestoreCount.Should().Be(1);
        messages.Should().Contain(m => m.Contains("Priority restore incomplete"));

        var retryReport = await svc.RetryPendingRestoresAsync();

        retryReport.Restored.Should().Be(1);
        retryReport.Pending.Should().Be(0);
        svc.PendingRestoreCount.Should().Be(0);
        restoreAttempts[1001].Should().Be(2);
        svc.Dispose();
    }

    // ── Power plan ─────────────────────────────────────────────────────────

    [Fact]
    public void ActivateProfile_WithPowerPlan_CallsSetActivePlan()
    {
        var originalPlan = Guid.NewGuid();
        var highPerfGuid = Guid.NewGuid();

        var (settings, _, profileId) = MakeSettings(
            changePowerPlan: true,
            powerPlanName: "High Performance");
        var (svc, _, _, mockPower) = CreateService(settings);

        mockPower.Setup(p => p.GetActivePlan()).Returns(originalPlan);
        mockPower.Setup(p => p.GetPowerPlans()).Returns(new List<PowerPlanInfo>
        {
            new PowerPlanInfo(highPerfGuid, "High Performance", false)
        });

        svc.ActivateProfile(profileId);

        mockPower.Verify(p => p.SetActivePlan(highPerfGuid), Times.Once);
        svc.Dispose();
    }

    [Fact]
    public void DeactivateProfile_RestoresPowerPlan()
    {
        var originalPlan = Guid.NewGuid();
        var highPerfGuid = Guid.NewGuid();

        var (settings, _, profileId) = MakeSettings(
            changePowerPlan: true,
            powerPlanName: "High Performance");
        var (svc, _, _, mockPower) = CreateService(settings);

        mockPower.Setup(p => p.GetActivePlan()).Returns(originalPlan);
        mockPower.Setup(p => p.GetPowerPlans()).Returns(new List<PowerPlanInfo>
        {
            new PowerPlanInfo(highPerfGuid, "High Performance", false)
        });

        svc.ActivateProfile(profileId);
        svc.DeactivateProfile();

        // Should restore to originalPlan
        mockPower.Verify(p => p.SetActivePlan(originalPlan), Times.Once);
        svc.Dispose();
    }

    // ── Idempotency / safety ───────────────────────────────────────────────

    [Fact]
    public async Task ActivateProfile_SamePidTwice_OnlyAppliesOnce()
    {
        var (settings, _, profileId) = MakeSettings(
            processName: "chrome",
            priority: ProcessPriority.High);
        var (svc, processSubject, mockProcess, _) = CreateService(settings);

        svc.ActivateProfile(profileId);

        // Emit the same process twice. The saved receipt prevents a second apply.
        processSubject.OnNext(new[] { MakeProcess("chrome", 1000) });
        await WaitUntilAsync(() => mockProcess.Invocations.Any(i =>
            i.Method.Name == nameof(IProcessProvider.ApplyTemporaryPriorityAsync)));

        // Emit again after the first apply has completed.
        processSubject.OnNext(new[] { MakeProcess("chrome", 1000) });
        await Task.Delay(100);

        mockProcess.Verify(p => p.ApplyTemporaryPriorityAsync(
                1000, ProcessPriority.High, It.IsAny<CancellationToken>()),
            Times.Once);
        svc.Dispose();
    }

    [Fact]
    public void Dispose_CallsDeactivate()
    {
        var (settings, _, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);

        string? lastProfileChanged = "sentinel"; // non-null sentinel
        svc.ProfileChanged.Subscribe(name => lastProfileChanged = name);

        svc.ActivateProfile(profileId);
        // At this point lastProfileChanged == profile.Name
        svc.Dispose();

        // Dispose calls DeactivateProfile which fires ProfileChanged(null)
        lastProfileChanged.Should().BeNull();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (settings, _, profileId) = MakeSettings();
        var (svc, _, _, _) = CreateService(settings);
        svc.ActivateProfile(profileId);

        var act = () =>
        {
            svc.Dispose();
            svc.Dispose();
        };

        act.Should().NotThrow();
    }
}
