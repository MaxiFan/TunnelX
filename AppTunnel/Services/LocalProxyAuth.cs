namespace AppTunnel.Services;

/// <summary>
/// Resolves effective credentials for the local mixed SOCKS5/HTTP listener.
/// Profile values win when a username is set; otherwise Settings defaults apply.
/// Auth is enabled only when the effective username is non-empty.
/// </summary>
internal static class LocalProxyAuth
{
    public readonly record struct Credentials(string Username, string Password)
    {
        public bool IsEnabled => !string.IsNullOrEmpty(Username);
    }

    public static Credentials Resolve(
        string? profileUsername,
        string? profilePassword,
        string? defaultUsername,
        string? defaultPassword)
    {
        if (!string.IsNullOrWhiteSpace(profileUsername))
        {
            return new Credentials(profileUsername.Trim(), profilePassword ?? "");
        }

        var fallbackUser = (defaultUsername ?? "").Trim();
        return new Credentials(fallbackUser, defaultPassword ?? "");
    }

    public static bool Matches(Credentials expected, string? username, string? password) =>
        string.Equals(expected.Username, username ?? "", StringComparison.Ordinal) &&
        string.Equals(expected.Password, password ?? "", StringComparison.Ordinal);
}
