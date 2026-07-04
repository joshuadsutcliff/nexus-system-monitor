using FluentAssertions;
using Moq;
using NexusMonitor.Core.Abstractions;
using Xunit;

namespace NexusMonitor.Core.Tests;

public class MockPlatformCapabilitiesTests
{
    private readonly MockPlatformCapabilities _sut = new();

    [Fact] public void SupportsCpuAffinity_IsTrue() => _sut.SupportsCpuAffinity.Should().BeTrue();
    [Fact] public void SupportsTrimMemory_IsTrue() => _sut.SupportsTrimMemory.Should().BeTrue();
    [Fact] public void SupportsCreateDump_IsTrue() => _sut.SupportsCreateDump.Should().BeTrue();
    [Fact] public void SupportsFindWindow_IsTrue() => _sut.SupportsFindWindow.Should().BeTrue();
    [Fact] public void SupportsIoPriority_IsTrue() => _sut.SupportsIoPriority.Should().BeTrue();
    [Fact] public void SupportsMemoryPriority_IsTrue() => _sut.SupportsMemoryPriority.Should().BeTrue();
    [Fact] public void UsesMetaKey_IsFalse() => _sut.UsesMetaKey.Should().BeFalse();
    [Fact] public void FileManagerName_IsExplorer() => _sut.FileManagerName.Should().Be("Explorer");
    [Fact] public void ServiceManagerName_IsServices() => _sut.ServiceManagerName.Should().Be("Services");
    [Fact] public void SupportsServiceStartupType_IsTrue() => _sut.SupportsServiceStartupType.Should().BeTrue();
    [Fact] public void SupportsRegistry_IsTrue() => _sut.SupportsRegistry.Should().BeTrue();
    [Fact] public void SupportsEfficiencyMode_IsTrue() => _sut.SupportsEfficiencyMode.Should().BeTrue();
    [Fact] public void SupportsHandles_IsTrue() => _sut.SupportsHandles.Should().BeTrue();
    [Fact] public void SupportsMemoryMap_IsTrue() => _sut.SupportsMemoryMap.Should().BeTrue();
    [Fact] public void SupportsPowerPlan_IsTrue() => _sut.SupportsPowerPlan.Should().BeTrue();
    [Fact] public void OpenLocationMenuLabel_IsOpenFileLocation() => _sut.OpenLocationMenuLabel.Should().Be("Open File Location");
    [Fact] public void SupportsDirectX_IsTrue() => _sut.SupportsDirectX.Should().BeTrue();
    [Fact] public void SupportsStartupToggle_IsTrue() => _sut.SupportsStartupToggle.Should().BeTrue();
}

public class PlatformCapabilitiesContractTests
{
    [Fact]
    public void Mock_CanConfigureAllBoolProperties()
    {
        var mock = new Mock<IPlatformCapabilities>();
        mock.Setup(p => p.SupportsCpuAffinity).Returns(false);
        mock.Setup(p => p.UsesMetaKey).Returns(true);

        mock.Object.SupportsCpuAffinity.Should().BeFalse();
        mock.Object.UsesMetaKey.Should().BeTrue();
    }

    [Fact]
    public void Mock_ImplementsInterface()
    {
        var mock = new Mock<IPlatformCapabilities>();
        mock.Object.Should().BeAssignableTo<IPlatformCapabilities>();
    }

    [Fact]
    public void MockPlatformCapabilities_ImplementsInterface()
    {
        IPlatformCapabilities caps = new MockPlatformCapabilities();
        caps.Should().BeAssignableTo<IPlatformCapabilities>();
    }
}
