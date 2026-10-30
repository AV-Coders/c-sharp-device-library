using System.Text.Json;

namespace AVCoders.Dsp.Tests;

public class TtpJsonTest
{
    [Theory]
    [InlineData("\"5.2.0.8\"", "\"5.2.0.8\"")]
    [InlineData("-12.000000", "-12.000000")]
    [InlineData("2", "2")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("01", "\"01\"")]
    [InlineData("LINK_1_GB", "\"LINK_1_GB\"")]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    [InlineData("[\"Aec1\" \"DEVICE\" \"InputLevels\"]", "[\"Aec1\",\"DEVICE\",\"InputLevels\"]")]
    [InlineData("[{\"a\":1} {\"b\":[x y]}]", "[{\"a\":1},{\"b\":[\"x\",\"y\"]}]")]
    [InlineData("{\"secondaryDNSServer\":\"\" \"domainName\":\"millpark.acarbone.com\"}", "{\"secondaryDNSServer\":\"\",\"domainName\":\"millpark.acarbone.com\"}")]
    [InlineData("{\"date\":\"Mon Oct 12 15:36:17 AUS Eastern Standard Time 2026\" \"mac\":\"78:45:01:0a:ee:9f\"}",
        "{\"date\":\"Mon Oct 12 15:36:17 AUS Eastern Standard Time 2026\",\"mac\":\"78:45:01:0a:ee:9f\"}")]
    [InlineData("{\"text\":\"a \\\"quoted\\\" word\" \"next\":1}", "{\"text\":\"a \\\"quoted\\\" word\",\"next\":1}")]
    [InlineData("[{\"id\":INDICATOR_NONE_IN_DEVICE \"name\":\"No fault in device\" \"faults\":[] \"serialNumber\":\"04351854\"}]",
        "[{\"id\":\"INDICATOR_NONE_IN_DEVICE\",\"name\":\"No fault in device\",\"faults\":[],\"serialNumber\":\"04351854\"}]")]
    public void ToJson_ConvertsTheTtpValue(string ttp, string expected)
    {
        Assert.Equal(expected, TtpJson.ToJson(ttp));
    }

    [Fact]
    public void ToJson_GivenTheNetworkStatus_KeepsEveryValue()
    {
        using var document = JsonDocument.Parse(TtpJson.ToJson(Value(TesiraReplies.NetworkStatus)));
        var root = document.RootElement;
        var control = root.GetProperty("networkInterfaceStatusWithName")[0];

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("TesiraForte04351854", root.GetProperty("hostname").GetString());
        Assert.Equal("control", control.GetProperty("interfaceId").GetString());
        Assert.Equal("LINK_1_GB", control.GetProperty("networkInterfaceStatus").GetProperty("linkStatus").GetString());
        Assert.Equal("Mon Oct 12 15:36:17 AUS Eastern Standard Time 2026",
            control.GetProperty("networkInterfaceStatus").GetProperty("dhcpLeaseObtainedDate").GetString());
        Assert.Equal("", root.GetProperty("dnsStatus").GetProperty("secondaryDNSServer").GetString());
        Assert.True(root.GetProperty("mDNSEnabled").GetBoolean());
        Assert.Equal("SWITCH_PORT_MODE_VLT_CONTROL_AND_MEDIA", root.GetProperty("volteraSwitchPortMode").GetString());
    }

    [Fact]
    public void ToJson_GivenTheDeviceInfo_KeepsTheModelName()
    {
        using var document = JsonDocument.Parse(TtpJson.ToJson(Value(TesiraReplies.DeviceInfo)));

        Assert.Equal("TesiraFORTÉ AVB VT", document.RootElement.GetProperty("deviceModel").GetString());
    }

    [Fact]
    public void ToJson_GivenTheDiscoveredServers_QuotesTheCategoryAndKeepsTheNumbers()
    {
        using var document = JsonDocument.Parse(TtpJson.ToJson(Value(TesiraReplies.DiscoveredServers)));
        var server = document.RootElement[0];

        Assert.Equal("AUDIO_SERVER", server.GetProperty("deviceCategory").GetString());
        Assert.Equal(11, server.GetProperty("deviceType").GetInt32());
        Assert.False(server.GetProperty("sleeping").GetBoolean());
    }

    [Fact]
    public void ToJson_GivenAnUnterminatedString_GivesJsonThatDoesNotParse()
    {
        string json = TtpJson.ToJson("{\"hostname\":\"TesiraForte");

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json));
    }

    private const string ReplyKeyEnd = "\":";

    private static string Value(string reply) => reply[(reply.IndexOf(ReplyKeyEnd, StringComparison.Ordinal) + ReplyKeyEnd.Length)..];
}
