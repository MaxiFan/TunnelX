using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AppTunnel.Helpers;
using AppTunnel.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException) { }
        }
        var fallback = new SolidColorBrush(Colors.Gray);
        fallback.Freeze();
        return fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Returns Visible when the bound enum value's string name matches ConverterParameter.
/// Usage: Visibility="{Binding SomeEnum, Converter={StaticResource EnumToVis}, ConverterParameter=MyValue}"
/// </summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Uses app language direction unless the string is technical-only (IP, host, ports, metrics).
/// </summary>
public class TextToFlowDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            if (TextHelper.IsPersianText(text))
                return FlowDirection.RightToLeft;
            if (IsTechnicalOnly(text))
                return FlowDirection.LeftToRight;
            if (TextHelper.IsEnglishText(text))
                return FlowDirection.LeftToRight;
        }

        return LocalizationService.Instance.FlowDirection;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    internal static bool IsTechnicalOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasContent = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;

            hasContent = true;
            if (c >= 0x0600 && c <= 0x06FF)
                return false;

            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                char.IsDigit(c) || c is '.' or ':' or '-' or '/' or '\\' or '_' or '@' or '*' or '✓' or '✗' or '●' or '○' or '–')
                continue;

            return false;
        }

        return hasContent;
    }
}

/// <summary>
/// Uses app text alignment unless the string is technical-only.
/// </summary>
public class TextToTextAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            if (TextHelper.IsPersianText(text))
                return TextAlignment.Right;
            if (TextToFlowDirectionConverter.IsTechnicalOnly(text) || TextHelper.IsEnglishText(text))
                return TextAlignment.Left;
        }

        return LocalizationService.Instance.TextAlignment;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
