using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class IpGeoLookupServiceTests
{
    [Fact]
    public void Parses_internetdata_nested_location()
    {
        const string body = """
            {
              "ip": "8.8.8.8",
              "location": {
                "continent": "NA",
                "country": "US",
                "country_name": "United States",
                "timezone": "America/Los_Angeles"
              },
              "asn": { "asn": 15169, "name": "Google LLC", "country": "US" }
            }
            """;

        Assert.True(IpGeoLookupService.TryParseGeo(body, out var info));
        Assert.Equal(new IpGeoInfo("US", "United States"), info);
    }

    [Fact]
    public void Still_parses_ip_api_answer()
    {
        const string body = """{"status":"success","country":"Germany","countryCode":"DE"}""";

        Assert.True(IpGeoLookupService.TryParseGeo(body, out var info));
        Assert.Equal(new IpGeoInfo("DE", "Germany"), info);
    }

    [Fact]
    public void Rejects_answer_without_country()
    {
        Assert.False(IpGeoLookupService.TryParseGeo("""{"ip":"10.0.0.1","location":{}}""", out _));
        Assert.False(IpGeoLookupService.TryParseGeo("""{"success":false,"message":"Invalid IP address"}""", out _));
    }
}
