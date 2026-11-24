using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pill.Converters;

/// <summary>Преобразует уровень эквалайзера 0..1 в высоту полоски.</summary>
public sealed class LevelToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double level = value is double d ? d : 0d;
        double max = 18d;
        if (parameter is double maxParam) max = maxParam;
        else if (parameter is string text && double.TryParse(text, CultureInfo.InvariantCulture, out double parsed)) max = parsed;
        double min = 2.5d;
        return min + Math.Clamp(level, 0d, 1d) * max;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visibility из bool, с возможностью инверсии параметром "invert".</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
