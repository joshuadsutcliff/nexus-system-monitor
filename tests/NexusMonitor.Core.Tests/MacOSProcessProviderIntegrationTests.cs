using System.Diagnostics;
using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;
using Xunit.Abstractions;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Live integration coverage for the Sym-1 Task 4 macOS process trio: Efficiency Mode (Darwin
/// background QoS clamp), the `sample`-based process dump, and UserName population. These
/// exercise the real provider against actual OS processes — genuinely run on macOS CI runners
/// (the workflow matrix includes macos-latest) and on a macOS dev machine; a deliberate no-op
/// everywhere else, following the pattern established by MacOSLaunchdIndexIntegrationTests
/// (Sym-1 Task 3).
///
/// Total added wall-time is kept under the brief's 15s budget: the sample capture is the
/// dominant cost at ~3-5s (3s capture + symbolication); efficiency-mode and UserName checks are
/// near-instant.
/// </summary>
public class MacOSProcessProviderIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<Process> _spawned = new();
    private readonly List<string> _tempFiles = new();

    public MacOSProcessProviderIntegrationTests(ITestOutputHelper output) => _output = output;

    private Process SpawnSleepChild(int seconds = 30)
    {
        var proc = Process.Start(new ProcessStartInfo("/bin/sleep", seconds.ToString())
        {
            UseShellExecute = false,
        })!;
        _spawned.Add(proc);
        return proc;
    }

    [Fact]
    public async Task EfficiencyMode_RoundTrip_SetReadClearRead_OnSpawnedChild()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSProcessProvider();
        var child = SpawnSleepChild();
        // Let the child fully start before touching its scheduling priority.
        await Task.Delay(300);

        async Task<bool?> ReadIsEfficiencyMode()
        {
            var procs = await provider.GetProcessesAsync();
            var row = procs.FirstOrDefault(p => p.Pid == child.Id);
            return row?.IsEfficiencyMode;
        }

        var before = await ReadIsEfficiencyMode();
        _output.WriteLine($"child pid={child.Id}, IsEfficiencyMode before set = {before}");
        before.Should().Be(false, "a freshly spawned child should not start in the BG clamp");

        await provider.SetEfficiencyModeAsync(child.Id, true);
        var afterSet = await ReadIsEfficiencyMode();
        _output.WriteLine($"IsEfficiencyMode after SetEfficiencyModeAsync(true) = {afterSet}");
        afterSet.Should().Be(true, "the app's own tracked state must reflect a successful set");

        await provider.SetEfficiencyModeAsync(child.Id, false);
        var afterClear = await ReadIsEfficiencyMode();
        _output.WriteLine($"IsEfficiencyMode after SetEfficiencyModeAsync(false) = {afterClear}");
        afterClear.Should().Be(false, "clearing must be reflected the same way");
    }

    [Fact]
    public async Task EfficiencyMode_SetOnRootOwnedProcess_FailsHonestly()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSProcessProvider();

        // pid 1 (launchd) is root-owned; this process is not, so EPERM must surface as a thrown
        // exception rather than a silently-ignored no-op.
        Func<Task> act = () => provider.SetEfficiencyModeAsync(1, true);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Sample_CapturesOwnUserProcess_ProducesNonTrivialReportWithCallGraph()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSProcessProvider();
        // A busy (not merely sleeping) process gives `sample` real call-stack data to capture.
        var busy = Process.Start(new ProcessStartInfo("/usr/bin/yes")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true, // discard 'yes' spam rather than inheriting our stdout
        })!;
        _spawned.Add(busy);

        var outputPath = Path.Combine(Path.GetTempPath(), $"nexus-sample-test-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(outputPath);

        var sw = Stopwatch.StartNew();
        await provider.CreateDumpFileAsync(busy.Id, outputPath);
        sw.Stop();
        _output.WriteLine($"sample capture took {sw.ElapsedMilliseconds} ms");

        File.Exists(outputPath).Should().BeTrue();
        var info = new FileInfo(outputPath);
        _output.WriteLine($"output file size = {info.Length} bytes");
        info.Length.Should().BeGreaterThan(500, "a real sample report is not a trivially small/empty file");

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("Call graph", "the sample report must contain its standard call-graph section marker");
    }

    [Fact]
    public async Task Sample_OnRootOwnedProcess_FailsHonestly()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSProcessProvider();
        var outputPath = Path.Combine(Path.GetTempPath(), $"nexus-sample-denied-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(outputPath);

        // pid 1 (launchd) — sampling a root-owned process without sudo must fail, not silently
        // produce an empty/garbage file.
        Func<Task> act = () => provider.CreateDumpFileAsync(1, outputPath);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UserName_SelfProcess_ResolvesToCurrentAccountName()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSProcessProvider();
        var procs = await provider.GetProcessesAsync();
        var self = procs.FirstOrDefault(p => p.Pid == Environment.ProcessId);

        self.Should().NotBeNull("our own test-runner process must appear in the snapshot");
        _output.WriteLine($"self pid={Environment.ProcessId} UserName='{self!.UserName}' (expect '{Environment.UserName}')");
        self.UserName.Should().Be(Environment.UserName);
    }

    [Fact]
    public async Task UserName_PidOne_ResolvesToRoot()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = new MacOSProcessProvider();
        var procs = await provider.GetProcessesAsync();
        var launchd = procs.FirstOrDefault(p => p.Pid == 1);

        launchd.Should().NotBeNull("pid 1 (launchd) is always present");
        _output.WriteLine($"pid 1 UserName='{launchd!.UserName}' (expect 'root')");
        launchd.UserName.Should().Be("root");
    }

    public void Dispose()
    {
        foreach (var proc in _spawned)
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* best-effort cleanup */ }
            proc.Dispose();
        }
        foreach (var path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
