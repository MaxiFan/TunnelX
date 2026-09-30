namespace AppTunnel.Services;

/// <summary>
/// WireGuard-for-Windows split-tunnel helpers. Table=off is required so AllowedIPs
/// of 0.0.0.0/0 does not install a system-wide default route.
/// </summary>
internal static class WireGuardSplitRouteOptions
{
    public static bool LooksLikeUnsupportedTableOption(string? stdout, string? stderr)
    {
        var text = $"{stdout}\n{stderr}";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Must mention Table as an option/key — not generic "unknown error".
        if (!ContainsWord(text, "table"))
            return false;

        return ContainsIgnoreCase(text, "unrecognized")
               || ContainsIgnoreCase(text, "unknown option")
               || ContainsIgnoreCase(text, "unknown key")
               || ContainsIgnoreCase(text, "invalid option")
               || ContainsIgnoreCase(text, "unsupported");
    }

    private static bool ContainsIgnoreCase(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWord(string text, string word)
    {
        var start = 0;
        while (start < text.Length)
        {
            var found = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;

            var beforeOk = found == 0 || !IsIdentChar(text[found - 1]);
            var after = found + word.Length;
            var afterOk = after >= text.Length || !IsIdentChar(text[after]);
            if (beforeOk && afterOk)
                return true;

            start = found + 1;
        }

        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
