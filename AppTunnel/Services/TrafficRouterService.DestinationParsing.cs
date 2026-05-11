using System.Net;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    private static string NormalizeDestinationEntry(string entry)
    {
        entry = entry.Trim();
        if (string.IsNullOrWhiteSpace(entry)) return "";

        if (IPAddress.TryParse(entry, out _))
            return entry;

        if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host.Trim('[', ']');

        var withoutScheme = entry;
        var schemeIdx = withoutScheme.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            withoutScheme = withoutScheme[(schemeIdx + 3)..];

        var slashIdx = withoutScheme.IndexOfAny(new[] { '/', '\\', '?', '#' });
        if (slashIdx >= 0)
            withoutScheme = withoutScheme[..slashIdx];

        if (withoutScheme.StartsWith("[", StringComparison.Ordinal) &&
            withoutScheme.Contains(']'))
        {
            var end = withoutScheme.IndexOf(']');
            return withoutScheme[1..end];
        }

        var colonIdx = withoutScheme.LastIndexOf(':');
        if (colonIdx > 0 &&
            withoutScheme.IndexOf(':') == colonIdx &&
            int.TryParse(withoutScheme[(colonIdx + 1)..], out _))
            withoutScheme = withoutScheme[..colonIdx];

        return withoutScheme.Trim().TrimEnd('.');
    }
}
