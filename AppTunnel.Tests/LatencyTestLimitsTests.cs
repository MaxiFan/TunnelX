using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class LatencyTestLimitsTests
{
    [Fact]
    public void Options_AreOneThroughEight()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, LatencyTestLimits.Options);
    }
}
