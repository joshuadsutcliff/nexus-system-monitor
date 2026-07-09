using FluentAssertions;
using NexusMonitor.Core.Models;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Tests for <see cref="LaunchdStartType"/> — pure, dependency-free logic for deriving an honest
/// <see cref="ServiceStartType"/> from launchd metadata (Sym-1 Task 3). No process/file I/O here,
/// so this runs identically on every OS regardless of whether real launchd plists are present
/// (mirrors the AmdGpuTemperature pure/IO split used for the Linux amdgpu hwmon work).
/// </summary>
public class LaunchdStartTypeTests
{
    // ── Map — priority order per the Sym-1 Task 3 brief ─────────────────────────

    [Fact]
    public void Map_LabelDisabled_ReturnsDisabled_RegardlessOfOtherFlags()
    {
        LaunchdStartType.Map(plistFound: true,  isDisabled: true, runAtLoad: true,  keepAliveTruthy: true)
            .Should().Be(ServiceStartType.Disabled);
        LaunchdStartType.Map(plistFound: false, isDisabled: true, runAtLoad: false, keepAliveTruthy: false)
            .Should().Be(ServiceStartType.Disabled);
    }

    [Fact]
    public void Map_NoPlistFound_NotDisabled_ReturnsUnknown()
    {
        LaunchdStartType.Map(plistFound: false, isDisabled: false, runAtLoad: false, keepAliveTruthy: false)
            .Should().Be(ServiceStartType.Unknown);
    }

    [Fact]
    public void Map_PlistFound_RunAtLoadTrue_ReturnsAutomatic()
    {
        LaunchdStartType.Map(plistFound: true, isDisabled: false, runAtLoad: true, keepAliveTruthy: false)
            .Should().Be(ServiceStartType.Automatic);
    }

    [Fact]
    public void Map_PlistFound_KeepAliveTruthy_ReturnsAutomatic()
    {
        LaunchdStartType.Map(plistFound: true, isDisabled: false, runAtLoad: false, keepAliveTruthy: true)
            .Should().Be(ServiceStartType.Automatic);
    }

    [Fact]
    public void Map_PlistFound_NeitherRunAtLoadNorKeepAlive_ReturnsManual()
    {
        LaunchdStartType.Map(plistFound: true, isDisabled: false, runAtLoad: false, keepAliveTruthy: false)
            .Should().Be(ServiceStartType.Manual);
    }

    // ── ParseDisabledLabels ──────────────────────────────────────────────────────

    private const string PrintDisabledSample = """

    	disabled services = {
    		"com.apple.AEServer" => enabled
    		"com.apple.screensharing" => enabled
    		"com.apple.CSCSupportd" => disabled
    		"com.apple.mdmclient.daemon.runatboot" => disabled
    		"com.apple.ftpd" => disabled
    		"com.openssh.sshd" => enabled
    	}
    """;

    [Fact]
    public void ParseDisabledLabels_OnlyIncludesLabelsMarkedDisabled_NotEnabled()
    {
        var result = LaunchdStartType.ParseDisabledLabels(PrintDisabledSample);

        result.Should().BeEquivalentTo(new[]
        {
            "com.apple.CSCSupportd",
            "com.apple.mdmclient.daemon.runatboot",
            "com.apple.ftpd",
        });

        result.Should().NotContain("com.apple.AEServer");
        result.Should().NotContain("com.openssh.sshd");
    }

    [Fact]
    public void ParseDisabledLabels_EmptyOrWhitespaceInput_ReturnsEmptySet()
    {
        LaunchdStartType.ParseDisabledLabels(string.Empty).Should().BeEmpty();
        LaunchdStartType.ParseDisabledLabels("   \n\t  ").Should().BeEmpty();
    }

    [Fact]
    public void ParseDisabledLabels_MalformedLines_AreSkippedNotThrown()
    {
        const string malformed = """

        	disabled services = {
        		not a quoted line at all
        		"unterminated
        		"com.apple.valid" => disabled
        	}
        """;

        var act = () => LaunchdStartType.ParseDisabledLabels(malformed);
        act.Should().NotThrow();

        var result = LaunchdStartType.ParseDisabledLabels(malformed);
        result.Should().ContainSingle().Which.Should().Be("com.apple.valid");
    }

    [Fact]
    public void ParseDisabledLabels_StateComparisonIsCaseInsensitive()
    {
        const string sample = """
        		"com.apple.thing" => DISABLED
        """;

        LaunchdStartType.ParseDisabledLabels(sample).Should().Contain("com.apple.thing");
    }

    // ── TryParsePlist ────────────────────────────────────────────────────────────

    private const string RunAtLoadTruePlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>Label</key>
        	<string>com.apple.securityd</string>
        	<key>RunAtLoad</key>
        	<true/>
        	<key>ProgramArguments</key>
        	<array>
        		<string>/usr/sbin/securityd</string>
        	</array>
        </dict>
        </plist>
        """;

    [Fact]
    public void TryParsePlist_RunAtLoadTrue_ParsesFactsAndLabel()
    {
        LaunchdStartType.TryParsePlist(RunAtLoadTruePlist, out var facts).Should().BeTrue();
        facts.RunAtLoad.Should().BeTrue();
        facts.KeepAliveTruthy.Should().BeFalse();
        facts.Label.Should().Be("com.apple.securityd");
    }

    [Fact]
    public void TryParsePlist_KeepAliveBooleanTrue_IsTruthy()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.apple.example</string>
            	<key>KeepAlive</key>
            	<true/>
            </dict>
            </plist>
            """;

        LaunchdStartType.TryParsePlist(plist, out var facts).Should().BeTrue();
        facts.KeepAliveTruthy.Should().BeTrue();
        facts.RunAtLoad.Should().BeFalse();
    }

    [Fact]
    public void TryParsePlist_KeepAliveBooleanFalse_IsNotTruthy()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.apple.example</string>
            	<key>KeepAlive</key>
            	<false/>
            </dict>
            </plist>
            """;

        LaunchdStartType.TryParsePlist(plist, out var facts).Should().BeTrue();
        facts.KeepAliveTruthy.Should().BeFalse();
    }

    [Fact]
    public void TryParsePlist_KeepAliveNonEmptyDict_IsTruthy()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.apple.example</string>
            	<key>KeepAlive</key>
            	<dict>
            		<key>Crashed</key>
            		<true/>
            	</dict>
            </dict>
            </plist>
            """;

        LaunchdStartType.TryParsePlist(plist, out var facts).Should().BeTrue();
        facts.KeepAliveTruthy.Should().BeTrue();
    }

    [Fact]
    public void TryParsePlist_KeepAliveEmptyDict_IsNotTruthy()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.apple.example</string>
            	<key>KeepAlive</key>
            	<dict/>
            </dict>
            </plist>
            """;

        LaunchdStartType.TryParsePlist(plist, out var facts).Should().BeTrue();
        facts.KeepAliveTruthy.Should().BeFalse();
    }

    [Fact]
    public void TryParsePlist_NeitherKeyPresent_BothFalse_LabelStillParsed()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.apple.quiet</string>
            	<key>ProgramArguments</key>
            	<array>
            		<string>/usr/libexec/quiet</string>
            	</array>
            </dict>
            </plist>
            """;

        LaunchdStartType.TryParsePlist(plist, out var facts).Should().BeTrue();
        facts.RunAtLoad.Should().BeFalse();
        facts.KeepAliveTruthy.Should().BeFalse();
        facts.Label.Should().Be("com.apple.quiet");
    }

    [Fact]
    public void TryParsePlist_MalformedXml_ReturnsFalse()
    {
        LaunchdStartType.TryParsePlist("<not-a-plist", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParsePlist_EmptyOrWhitespaceInput_ReturnsFalse()
    {
        LaunchdStartType.TryParsePlist(string.Empty, out _).Should().BeFalse();
        LaunchdStartType.TryParsePlist("   ", out _).Should().BeFalse();
    }

    // ── Gate-review finding: malformed-plist blast radius ───────────────────────
    //
    // TryParsePlist used to catch only XmlException; any other exception type escaped, aggregated
    // out of the Parallel.ForEach in MacOSLaunchdIndex.GetOrBuildIndex, and emptied the ENTIRE
    // services list for that refresh instead of degrading just the one bad plist. The catch clause
    // is now broadened to `catch (Exception)`.
    //
    // We could not construct an input that throws anything OTHER than XmlException through this
    // method's only I/O-free parsing path (XmlReader + XDocument.Load over a StringReader) — a raw
    // NUL character, a lone UTF-16 surrogate (both as a raw char and as a numeric character
    // reference), an undeclared entity reference, a duplicate attribute, and an invalid XML
    // declaration version were all empirically verified (via a standalone scratch program using
    // the exact same XmlReaderSettings as this method) to surface as System.Xml.XmlException, not
    // some other type. The tests below pin the "never throws" contract across that same set of
    // malformed inputs — the best available proxy for the non-XmlException case the gate review
    // couldn't itself reproduce.
    [Theory]
    [InlineData("<plist><dict><key>Label</key><string>a&undefined;b</string></dict></plist>")]  // undeclared entity
    [InlineData("<plist><dict><key>Label</key><string>a&#xD800;b</string></dict></plist>")]     // invalid numeric char ref (lone surrogate)
    [InlineData("<plist a=\"1\" a=\"2\"><dict/></plist>")]                                      // duplicate attribute
    [InlineData("<?xml version=\"9.9\"?><plist><dict/></plist>")]                               // invalid XML-decl version
    [InlineData("not xml at all { }")]
    [InlineData("<plist><dict><key>Label</key><string>unterminated")]
    public void TryParsePlist_MalformedInputs_NeverThrows_AlwaysReturnsFalse(string malformed)
    {
        var act = () => LaunchdStartType.TryParsePlist(malformed, out _);
        act.Should().NotThrow();
        LaunchdStartType.TryParsePlist(malformed, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParsePlist_NulCharacter_NeverThrows_AlwaysReturnsFalse()
    {
        var malformed = "<plist><dict><key>Label</key><string>a" + '\0' + "b</string></dict></plist>";

        var act = () => LaunchdStartType.TryParsePlist(malformed, out _);
        act.Should().NotThrow();
        LaunchdStartType.TryParsePlist(malformed, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParsePlist_NoDtd_StillParsesOffline()
    {
        // No DOCTYPE line at all — plutil output doesn't always include one; must not require
        // network access to resolve the Apple DTD either way (DtdProcessing.Ignore + no resolver).
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.apple.nodtdcase</string>
            	<key>RunAtLoad</key>
            	<true/>
            </dict>
            </plist>
            """;

        LaunchdStartType.TryParsePlist(plist, out var facts).Should().BeTrue();
        facts.RunAtLoad.Should().BeTrue();
        facts.Label.Should().Be("com.apple.nodtdcase");
    }
}
