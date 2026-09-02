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
}
