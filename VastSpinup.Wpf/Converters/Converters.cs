using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VastSpinup.Wpf.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter is string s && s == "invert";
        bool isNull = value == null || (value is string str && string.IsNullOrEmpty(str));
        return (isNull ^ invert) ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"running" → green, "stopped"/"exited" → yellow, error states → red, default → text primary.</summary>
public class InstanceStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string)?.ToLowerInvariant() ?? "";
        return s switch
        {
            "running" => new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)),
            "stopped" or "exited" => new SolidColorBrush(Color.FromArgb(0xFF, 0xEA, 0xB3, 0x08)),
            "error" or "failed" or "offline" or "destroyed" => new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44)),
            _ => new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF5)),
        };
    }
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
