namespace NexusMonitor.Platform.Linux;

/// <summary>
/// Pure selection/parsing logic for AMD GPU temperature via the amdgpu hwmon sysfs interface
/// (<c>temp*_input</c> / <c>temp*_label</c> under <c>.../device/hwmon/hwmonN/</c>).
/// Deliberately has no file I/O so the selection policy can be unit tested on any OS —
/// the platform provider reads the sysfs files and hands the parsed (label, value) pairs
/// in here. Mirrors the separation already used for CPU temp (<c>ReadHwmonTemperature</c> /
/// <c>ReadAmdZenTemperature</c> in <see cref="LinuxSystemMetricsProvider"/>), just with the
/// selection step split out as a standalone, reusable type instead of an inline private method,
/// so a future multi-GPU enumeration pass can call it per-card without duplicating the policy.
/// </summary>
public static class AmdGpuTemperature
{
    /// <summary>
    /// Sensor values above this are implausible for a GPU and treated as unavailable rather
    /// than displayed — honest-UI convention: never fabricate a reading.
    /// </summary>
    private const double MaxPlausibleCelsius = 150.0;

    /// <summary>
    /// One <c>temp{Index}_input</c> reading paired with its optional <c>temp{Index}_label</c>
    /// (e.g. "edge", "junction", "mem"). <paramref name="Index"/> is the 1-based hwmon temp
    /// index; <paramref name="MilliDegreesC"/> is the raw millidegree-Celsius sysfs value.
    /// </summary>
    public readonly record struct Reading(int Index, string? Label, long MilliDegreesC);

    /// <summary>
    /// Selects the AMD GPU temperature (°C) from a set of hwmon readings already parsed from
    /// sysfs, per the amdgpu convention:
    ///   1. Prefer the reading labeled "edge" (the conventional GPU package temperature).
    ///   2. Otherwise fall back to temp1 (covers both unlabeled sensors and the case where
    ///      labels exist but none is "edge").
    /// Returns 0 ("unavailable") when no usable reading exists, or when the selected reading's
    /// value is out of the plausible range (&lt;= 0 °C or &gt; 150 °C). Never fabricates a value
    /// by falling back further once a candidate reading has been selected.
    /// </summary>
    public static double SelectTemperatureCelsius(IReadOnlyList<Reading> readings)
    {
        long edgeMilliC = 0;
        bool foundEdge = false;
        long temp1MilliC = 0;
        bool foundTemp1 = false;

        foreach (var reading in readings)
        {
            if (!foundEdge && string.Equals(reading.Label, "edge", StringComparison.OrdinalIgnoreCase))
            {
                edgeMilliC = reading.MilliDegreesC;
                foundEdge = true;
            }
            if (!foundTemp1 && reading.Index == 1)
            {
                temp1MilliC = reading.MilliDegreesC;
                foundTemp1 = true;
            }
        }

        long chosenMilliC;
        if (foundEdge) chosenMilliC = edgeMilliC;
        else if (foundTemp1) chosenMilliC = temp1MilliC;
        else return 0;

        var tempC = chosenMilliC / 1000.0;
        return tempC > 0 && tempC <= MaxPlausibleCelsius ? tempC : 0;
    }
}
