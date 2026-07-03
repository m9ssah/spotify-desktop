using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpotifyTaskbarWidget.Converters;

/// <summary>
/// Converts a boolean value to a Visibility value.
/// True → Visible, False → Collapsed (default behavior).
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;

        // "Invert" parameter reverses the logic
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            boolValue = !boolValue;
        }

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// Converts a boolean to an opacity value (true = 1.0, false = 0.4 for greyed-out idle state).
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isActive = value is bool b && b;
        return isActive ? 1.0 : 0.4;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double d && d > 0.5;
    }
}
