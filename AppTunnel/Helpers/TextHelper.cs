using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Helpers;

/// <summary>
/// Helper methods for text direction detection and Persian text handling
/// </summary>
public static class TextHelper
{
    /// <summary>
    /// تشخیص خودکار جهت متن بر اساس محتوای آن
    /// </summary>
    /// <param name="text">متن ورودی</param>
    /// <returns>FlowDirection.RightToLeft برای فارسی/عربی، FlowDirection.LeftToRight برای انگلیسی</returns>
    public static FlowDirection DetectFlowDirection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FlowDirection.RightToLeft; // پیش‌فرض برای اپلیکیشن فارسی

        // بررسی اولین کاراکتر معنادار
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c))
                continue;

            // محدوده Unicode برای فارسی و عربی: 0x0600-0x06FF
            if (c >= 0x0600 && c <= 0x06FF)
                return FlowDirection.RightToLeft;

            // محدوده Unicode برای حروف لاتین
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return FlowDirection.LeftToRight;
        }

        return FlowDirection.RightToLeft;
    }

    /// <summary>
    /// بررسی اینکه آیا متن شامل حروف فارسی است یا خیر
    /// </summary>
    public static bool IsPersianText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (char c in text)
        {
            // محدوده Unicode فارسی: 0x0600-0x06FF
            if (c >= 0x0600 && c <= 0x06FF)
                return true;
        }

        return false;
    }

    /// <summary>
    /// بررسی اینکه آیا متن شامل حروف انگلیسی است یا خیر
    /// </summary>
    public static bool IsEnglishText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (char c in text)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return true;
        }

        return false;
    }

    /// <summary>
    /// نرمال‌سازی فاصله‌های فارسی (تبدیل ZWNJ، نیم‌فاصله و ...)
    /// </summary>
    public static string NormalizePersianSpaces(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // تبدیل نیم‌فاصله عربی به فاصله معمولی
        text = text.Replace('\u200C', ' '); // ZWNJ
        text = text.Replace('\u00A0', ' '); // Non-breaking space

        return text;
    }
}
