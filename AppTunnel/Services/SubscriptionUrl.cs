namespace AppTunnel.Services;

/// <summary>
/// Validates user-entered subscription URLs and distinguishes them from plain HTTP proxy endpoints.
/// </summary>
public static class SubscriptionUrl
{
    public const int MaxUrlLength = 4096;

    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = LocalizationService.Instance.T("آدرس اشتراک را وارد کنید");
            return false;
        }

        var text = input.Trim();
        if (text.Length > MaxUrlLength || text.Contains('\n') || text.Contains('\r') || text.Contains(' '))
        {
            error = LocalizationService.Instance.T("آدرس اشتراک معتبر نیست");
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            error = LocalizationService.Instance.T("آدرس اشتراک باید با http:// یا https:// شروع شود");
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// A single http(s) URL with a path or query. Bare <c>http://host:port</c> stays an HTTP proxy import.
    /// </summary>
    public static bool LooksLikeSubscriptionUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
            return false;

        if (!TryNormalize(trimmed, out var normalized, out _))
            return false;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var path = uri.AbsolutePath;
        var hasPath = !string.IsNullOrEmpty(path) && path != "/";
        return hasPath || !string.IsNullOrEmpty(uri.Query);
    }

    public static string SuggestName(string normalizedUrl)
    {
        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;
        return LocalizationService.Instance.T("اشتراک");
    }
}
