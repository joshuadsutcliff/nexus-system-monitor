using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NexusMonitor.UI.Converters;

/// <summary>
/// Renders "N/A" on macOS for fields with no macOS API/concept. On every other
/// platform it formats the value with the format string passed as ConverterParameter
/// (reproducing the StringFormat it replaces) — so non-macOS output is unchanged.
/// </summary>
public sealed class MacOsUnavailableConverter : IValueConverter
{
    public static readonly MacOsUnavailableConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (OperatingSystem.IsMacOS())
            return "N/A";
        if (parameter is string fmt && fmt.Length > 0)
            return string.Format(culture, fmt, value);   // e.g. "{0:F0}°C"
        return value;                                      // no format (e.g. raw HandleCount)
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
