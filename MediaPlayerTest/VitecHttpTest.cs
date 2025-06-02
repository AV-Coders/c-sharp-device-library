namespace AVCoders.MediaPlayer.Tests;

public class VitecHttpTest
{
    private readonly VitecHttp _vitecHttp;
    private static RemoteButton[] _excludedButtons = 
    [
        RemoteButton.Display, RemoteButton.Eject, 
        RemoteButton.PopupMenu, RemoteButton.TopMenu
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
    }
}