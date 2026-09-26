using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class LocalProxyAuthTests
{
    [Fact]
    public void Resolve_uses_profile_when_username_set()
    {
        var creds = LocalProxyAuth.Resolve("profile-user", "profile-pass", "default-user", "default-pass");

        Assert.True(creds.IsEnabled);
        Assert.Equal("profile-user", creds.Username);
        Assert.Equal("profile-pass", creds.Password);
    }

    [Fact]
    public void Resolve_falls_back_to_settings_defaults_when_profile_username_empty()
    {
        var creds = LocalProxyAuth.Resolve("", "ignored", "default-user", "default-pass");

        Assert.True(creds.IsEnabled);
        Assert.Equal("default-user", creds.Username);
        Assert.Equal("default-pass", creds.Password);
    }

    [Fact]
    public void Resolve_disables_auth_when_no_username_anywhere()
    {
        var creds = LocalProxyAuth.Resolve("  ", "x", null, "y");

        Assert.False(creds.IsEnabled);
        Assert.Equal("", creds.Username);
    }

    [Fact]
    public void Matches_compares_username_and_password_exactly()
    {
        var expected = new LocalProxyAuth.Credentials("alice", "secret");

        Assert.True(LocalProxyAuth.Matches(expected, "alice", "secret"));
        Assert.False(LocalProxyAuth.Matches(expected, "alice", "wrong"));
        Assert.False(LocalProxyAuth.Matches(expected, "bob", "secret"));
    }
}
