using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace AppTunnel.Services;

public sealed class SubscriptionTrafficInfo
{
    public long Upload { get; init; }
    public long Download { get; init; }
    public long Total { get; init; }
    public long Expire { get; init; }
}

public sealed class SubscriptionFetchResult
{
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = "";
    public string Body { get; init; } = "";
    public string? ProfileTitle { get; init; }
    public SubscriptionTrafficInfo? Traffic { get; init; }

    public static SubscriptionFetchResult Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };

    public static SubscriptionFetchResult Ok(string body, string? title, SubscriptionTrafficInfo? traffic) => new()
    {
        Success = true,
        Body = body,
        ProfileTitle = title,
        Traffic = traffic
    };
}

public static class SubscriptionHeaders
{
    public static string? ParseProfileTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim().Trim('"');
        const string prefix = "base64:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b64 = value[prefix.Length..].Trim().Replace('-', '+').Replace('_', '/');
                var mod = b64.Length % 4;
                if (mod is > 0 and < 4)
                    b64 = b64.PadRight(b64.Length + (4 - mod), '=');
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Trim().Trim('"');
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? ParseContentDispositionFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var star = Regex.Match(raw, @"filename\*\s*=\s*(?:[^']*)''([^;]+)", RegexOptions.IgnoreCase);
        if (star.Success)
        {
            var decoded = Uri.UnescapeDataString(star.Groups[1].Value.Trim().Trim('"'));
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }

        var plain = Regex.Match(raw, @"filename\s*=\s*(""[^""]*""|[^;]+)", RegexOptions.IgnoreCase);
        if (!plain.Success)
            return null;

        var name = plain.Groups[1].Value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static SubscriptionTrafficInfo? ParseUserInfo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        long upload = 0, download = 0, total = 0, expire = 0;
        var any = false;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (!long.TryParse(value, out var number))
                continue;

            any = true;
            if (key.Equals("upload", StringComparison.OrdinalIgnoreCase))
                upload = number;
            else if (key.Equals("download", StringComparison.OrdinalIgnoreCase))
                download = number;
            else if (key.Equals("total", StringComparison.OrdinalIgnoreCase))
                total = number;
            else if (key.Equals("expire", StringComparison.OrdinalIgnoreCase))
                expire = number;
        }

        return any ? new SubscriptionTrafficInfo
        {
            Upload = upload,
            Download = download,
            Total = total,
            Expire = expire
        } : null;
    }
}

/// <summary>
/// Downloads a subscription document. The handler can be replaced in tests.
/// </summary>
public sealed class SubscriptionClient
{
    public const int MaxBodyBytes = 2_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly HttpMessageHandler? _handler;

    public SubscriptionClient(HttpMessageHandler? handler = null)
    {
        _handler = handler;
    }

    public static string UserAgent =>
        "TunnelX/" + (typeof(SubscriptionClient).Assembly.GetName().Version?.ToString(3) ?? "2.1.2");

    public async Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!SubscriptionUrl.TryNormalize(url, out var normalized, out var error))
            return SubscriptionFetchResult.Fail(error);

        var ownsHandler = _handler == null;
        var handler = _handler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        try
        {
            using var client = new HttpClient(handler, disposeHandler: ownsHandler)
            {
                Timeout = Timeout
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, normalized);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return SubscriptionFetchResult.Fail(LocalizationService.Instance.Format(
                    "دریافت اشتراک ناموفق بود (HTTP {0})",
                    (int)response.StatusCode));
            }

            var body = await ReadBodyAsync(response, cancellationToken);
            if (body == null)
            {
                return SubscriptionFetchResult.Fail(LocalizationService.Instance.T("پاسخ اشتراک خیلی بزرگ است"));
            }

            if (string.IsNullOrWhiteSpace(body))
                return SubscriptionFetchResult.Fail(LocalizationService.Instance.T("پاسخ اشتراک خالی است"));

            var title = SubscriptionHeaders.ParseProfileTitle(GetHeader(response.Headers, "profile-title"))
                        ?? SubscriptionHeaders.ParseContentDispositionFileName(GetHeader(response.Content.Headers, "Content-Disposition"));
            var traffic = SubscriptionHeaders.ParseUserInfo(GetHeader(response.Headers, "subscription-userinfo"));
            return SubscriptionFetchResult.Ok(body, title, traffic);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SubscriptionFetchResult.Fail(LocalizationService.Instance.T("مهلت دریافت اشتراک تمام شد"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            var host = Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri.Host : "subscription";
            Logger.Warning($"[SUB] Fetch failed for {host}: {ex.Message}");
            return SubscriptionFetchResult.Fail(LocalizationService.Instance.Format(
                "دریافت اشتراک ناموفق بود: {0}",
                ex.Message));
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch { encoding = Encoding.UTF8; }
        }

        return encoding.GetString(buffer.ToArray()).Trim().TrimStart('\uFEFF');
    }

    private static string? GetHeader(HttpHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
