using FluentAssertions;
using NexusMonitor.Platform.MacOS;
using Xunit;

namespace NexusMonitor.Core.Tests;

/// <summary>
/// Pure-logic coverage for the Sym-2 Task 5 macOS temperature work: SMC value-type decoders
/// (flt/sp78/ui*/ioft with endianness), the plausibility filter, mean aggregation with partial
/// failures, and per-generation key-set selection. No IOKit here, so these run on every OS
/// (Windows/Linux CI included) — the live SMC/IOHID path is exercised by the macOS-gated
/// MacOSTemperatureIntegrationTests.
/// </summary>
public class SmcTemperatureTests
{
    // ── Type-driven decoders ────────────────────────────────────────────────────

    [Fact]
    public void Decode_Flt_ReadsLittleEndianFloat32()
    {
        // flt is little-endian float32 (dominant on Apple Silicon). Round-trip a known value.
        var bytes = BitConverter.GetBytes(45.25f);   // native LE on the test host
        SmcTemperature.Decode("flt ", bytes).Should().BeApproximately(45.25, 1e-4);
    }

    [Fact]
    public void Decode_Sp78_ReadsBigEndianSignedFixedPoint()
    {
        // 45.0 °C → 45*256 = 0x2D00 big-endian.
        SmcTemperature.Decode("sp78", new byte[] { 0x2D, 0x00 }).Should().BeApproximately(45.0, 1e-9);
        // Negative: -4.5 °C → 0xFB80 as big-endian int16.
        SmcTemperature.Decode("sp78", new byte[] { 0xFB, 0x80 }).Should().BeApproximately(-4.5, 1e-9);
    }

    [Fact]
    public void Decode_UnsignedInts_ReadBigEndian()
    {
        SmcTemperature.Decode("ui8 ", new byte[] { 0x50 }).Should().Be(80);
        SmcTemperature.Decode("ui16", new byte[] { 0x00, 0x50 }).Should().Be(80);
        SmcTemperature.Decode("ui32", new byte[] { 0x00, 0x00, 0x00, 0x50 }).Should().Be(80);
    }

    [Fact]
    public void Decode_Ioft_ReadsBigEndian64BitScaledBy65536()
    {
        // 50.0 → 50*65536 = 0x00320000, big-endian in 8 bytes.
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00 };
        SmcTemperature.Decode("ioft", bytes).Should().BeApproximately(50.0, 1e-9);
    }

    [Fact]
    public void Decode_UnknownType_ReturnsNull()
    {
        SmcTemperature.Decode("zzzz", new byte[] { 1, 2, 3, 4 }).Should().BeNull();
    }

    [Fact]
    public void Decode_SizeShorterThanType_ReturnsNull()
    {
        SmcTemperature.Decode("flt ", new byte[] { 1, 2 }).Should().BeNull();          // needs 4
        SmcTemperature.Decode("ioft", new byte[] { 1, 2, 3, 4 }).Should().BeNull();    // needs 8
    }

    // ── Plausibility filter (10–120 °C, binding) ────────────────────────────────

    [Theory]
    [InlineData(10.0, true)]    // inclusive lower bound
    [InlineData(120.0, true)]   // inclusive upper bound
    [InlineData(45.0, true)]
    [InlineData(9.99, false)]
    [InlineData(120.01, false)]
    [InlineData(-4.5, false)]   // the base-M4 garbage Tg* reading
    [InlineData(0.8, false)]    // the other base-M4 garbage Tg* reading
    [InlineData(0.0, false)]
    public void IsPlausible_EnforcesTenToOneTwenty(double value, bool expected)
    {
        SmcTemperature.IsPlausible(value).Should().Be(expected);
    }

    // ── Aggregation ─────────────────────────────────────────────────────────────

    [Fact]
    public void MeanOfPlausible_AveragesOnlyPassingReadings()
    {
        // Two good perf-core readings plus a garbage one → mean of the two good ones.
        SmcTemperature.MeanOfPlausible(new[] { 44.0, 46.0, -4.5 })
            .Should().BeApproximately(45.0, 1e-9);
    }

    [Fact]
    public void MeanOfPlausible_AllGarbage_ReturnsZero()
    {
        // Mirrors the base-M4 GPU case: every reading filtered out → honest 0 (unavailable).
        SmcTemperature.MeanOfPlausible(new[] { -4.5, 0.8, -4.5, 0.8 }).Should().Be(0.0);
    }

    [Fact]
    public void MeanOfPlausible_Empty_ReturnsZero()
    {
        SmcTemperature.MeanOfPlausible(System.Array.Empty<double>()).Should().Be(0.0);
    }

    // ── Key packing round-trip ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Tp01")]
    [InlineData("Tg0G")]
    [InlineData("flt ")]
    [InlineData("sp78")]
    public void KeyToUInt32_RoundTrips(string key)
    {
        SmcTemperature.UInt32ToKey(SmcTemperature.KeyToUInt32(key)).Should().Be(key);
    }

    [Fact]
    public void KeyToUInt32_MatchesCommunityStrtokey()
    {
        // "Tp01" = 0x54_70_30_31 (big-endian ASCII pack), same as smcFanControl/Stats strtokey.
        SmcTemperature.KeyToUInt32("Tp01").Should().Be(0x54703031u);
    }

    // ── Key-set selection per brand string ──────────────────────────────────────

    [Fact]
    public void ResolveKeySet_M4_UsesProbeConfirmedPerfAndEfficiencyKeys()
    {
        var set = SmcTemperature.ResolveKeySet("Apple M4", isArm64: true);
        set.CpuPerformance.Should().Contain(new[] { "Tp01", "Tp05", "Tp09", "Tp0D", "Tp0V", "Tp0Y", "Tp0b", "Tp0e" });
        set.CpuEfficiency.Should().BeEquivalentTo(new[] { "Tp00", "Tp04", "Tp08", "Tp0C" });
        set.Gpu.Should().Contain(new[] { "Tg0G", "Tg0H" });
    }

    [Fact]
    public void ResolveKeySet_M1_SelectsM1Table()
    {
        var set = SmcTemperature.ResolveKeySet("Apple M1", isArm64: true);
        set.CpuPerformance.Should().Contain("Tp0H");     // M1-specific perf key
        set.Gpu.Should().Contain("Tg05");                // M1-specific GPU key
    }

    [Fact]
    public void ResolveKeySet_Intel_SelectsIntelSp78Keys()
    {
        var set = SmcTemperature.ResolveKeySet("Intel(R) Core(TM) i7", isArm64: false);
        set.CpuPerformance.Should().Contain("TC0P");
        set.Gpu.Should().Contain("TG0P");
    }

    [Fact]
    public void ResolveKeySet_UnknownAppleSilicon_FallsBackToUnionOfAllTables()
    {
        // A future/unknown Apple Silicon part → union of every known table, so at least one
        // generation's keys will match and the plausibility filter discards the rest.
        var set = SmcTemperature.ResolveKeySet("Apple M99", isArm64: true);
        set.CpuPerformance.Should().Contain("Tp01");     // from M1/M2/M4
        set.CpuPerformance.Should().Contain("Tf04");     // from M3
        set.Gpu.Should().Contain("Tg05");                // from M1
    }

    [Fact]
    public void ResolveKeySet_FutureM10_DoesNotSubstringMatchM1_FallsBackToUnion()
    {
        // "Apple M10" contains the substring "M1" — a plain Contains check silently selects
        // the M1 table. A generation token immediately followed by another digit must not
        // match; an unknown generation falls back to the union.
        var set = SmcTemperature.ResolveKeySet("Apple M10", isArm64: true);
        set.CpuPerformance.Should().Contain("Tf04");     // M3-only key → proves union, not the M1 table

        var pro = SmcTemperature.ResolveKeySet("Apple M10 Pro", isArm64: true);
        pro.CpuPerformance.Should().Contain("Tf04");
    }
}
