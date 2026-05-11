using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
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
/// تشخیص خودکار جهت متن بر اساس محتوا
/// اگر متن شامل حروف فارسی یا عربی باشد: RightToLeft
/// اگر متن شامل حروف انگلیسی باشد: LeftToRight
/// </summary>
public class TextToFlowDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            // بررسی اولین کاراکتر معنادار
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c))
                    continue;

                // محدوده Unicode برای فارسی و عربی: 0x0600-0x06FF
                if (c >= 0x0600 && c <= 0x06FF)
                    return FlowDirection.RightToLeft;

                // محدوده Unicode برای حروف لاتین: A-Z, a-z
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    return FlowDirection.LeftToRight;
            }
        }

        // پیش‌فرض: راست به چپ (برای اپلیکیشن فارسی)
        return FlowDirection.RightToLeft;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
