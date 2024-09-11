namespace AVCoders.MediaPlayer.Tests;

public class VitecHttpTest
{
    private readonly VitecHttp _vitecHttp;
    public static IEnumerable<object[]> RemoteButtonValues()
    {
        return Enum.GetValues(typeof(RemoteButton)).Cast<RemoteButton>().Select(rb => new object[] { rb });
    }

    public VitecHttpTest()
    {
        _vitecHttp = new VitecHttp("foo", "bar");
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _vitecHttp.SendIRCode(button);
    }
}