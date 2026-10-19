using System.Diagnostics;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class VitecHttpTest
{
    private readonly VitecHttp _vitecHttp;
    private static readonly RemoteButton[] _excludedButtons = 
    [
        RemoteButton.Display, RemoteButton.Eject, 
        RemoteButton.PopupMenu, RemoteButton.TopMenu,
        RemoteButton.PowerOn, RemoteButton.PowerOff
    ];
    public static IEnumerable<object[]> RemoteButtonValues()
    {
        return Enum.GetValues(typeof(RemoteButton))
            .Cast<RemoteButton>()
            .Where(rb => !_excludedButtons.Contains(rb))
            .Select(rb => new object[] { rb });
    }

    public VitecHttpTest()
    {
        _vitecHttp = new VitecHttp("foo", "bar", "Name");
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _vitecHttp.SendIRCode(button);

        Assert.Contains(button, _vitecHttp.SupportedButtons);
    }

    [Fact]
    public void SupportedButtons_MatchTheRemoteMap()
    {
        Assert.Contains(RemoteButton.Guide, _vitecHttp.SupportedButtons);
        Assert.Contains(RemoteButton.Home, _vitecHttp.SupportedButtons);
        Assert.Contains(RemoteButton.Button0, _vitecHttp.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Eject, _vitecHttp.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.PowerOn, _vitecHttp.SupportedButtons);
    }

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b), _vitecHttp.SupportedButtons.OrderBy(b => b));
    }

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    [Theory]
    [MemberData(nameof(ExcludedButtonValues))]
    public void SendIRCode_ExcludedButtonsAreNotSupportedAndSendNothing(RemoteButton button)
    {
        _vitecHttp.SendIRCode(button);

        Assert.DoesNotContain(button, _vitecHttp.SupportedButtons);
    }

    [Fact]
    public async Task PowerOn_ToASelfSignedDeviceUnderAnActivity_SendsNoTraceContextHeaders()
    {
        using var server = new TlsHeaderCapturingServer();
        var vitec = new VitecHttp($"127.0.0.1:{server.Port}/loopback", "bar", "Loopback");
        using var activity = new Activity("test").Start();
        activity.TraceStateString = "vendor=value";
        activity.AddBaggage("key", "value");

        var request = server.NextRequestHeadAsync();
        vitec.PowerOn();
        var head = await request;

        Assert.StartsWith("PUT /loopback:8080/devicestate", head);
        Assert.All(TraceContext.Headers,
            header => Assert.DoesNotContain($"\r\n{header}:", head, StringComparison.OrdinalIgnoreCase));
    }
}
