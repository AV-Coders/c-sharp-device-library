using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class AntricaEncoderTest
{
    private const string VideoLossPath = "/httpapi/GetState?action=getinput&GIS_VIDEOLOSS1=0";
    private const string InputFormatPath = "/httpapi/ReadParam?action=readparam&VID_INPUTFORMAT=0";

    private readonly Mock<RestComms> _restComms = new("host", (ushort)80, "test");
    private readonly Mock<SyncInfoHandler> _inputStatusHandler = new();
    private readonly AntricaEncoder _encoder;

    public AntricaEncoderTest()
    {
        _encoder = new AntricaEncoder("Encoder", _restComms.Object);
        _encoder.InputStatusChangedHandlers += _inputStatusHandler.Object;
    }

    private void Respond(string body) => _restComms.Object.ResponseHandlers!.Invoke(body);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_restComms.Object, state);

    private int CountGets(string path) => _restComms.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(RestComms.Get) &&
        invocation.Arguments.Count == 1 &&
        (invocation.Arguments[0] as Uri)?.OriginalString == path);

    [Fact]
    public void Constructor_ReportsAnEncoderWithoutHdcp()
    {
        Assert.Equal(AVEndpointType.Encoder, _encoder.DeviceType);
        Assert.Equal(HdcpStatus.NotSupported, _encoder.InputHdcpStatus);
    }

    [Fact]
    public async Task Poll_ReadsTheVideoLossAndTheInputFormat()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((CountGets(VideoLossPath) == 0 || CountGets(InputFormatPath) == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(CountGets(VideoLossPath) > 0);
        Assert.True(CountGets(InputFormatPath) > 0);
    }

    [Theory]
    [InlineData("GIS_VIDEOLOSS1=0\n", ConnectionState.Connected)]
    [InlineData("GIS_VIDEOLOSS1=1\n", ConnectionState.Disconnected)]
    [InlineData("GIS_VIDEOLOSS1=0\r\n", ConnectionState.Connected)]
    public void VideoLoss_SetsTheInputConnectionStatus(string body, ConnectionState expected)
    {
        Respond(body);

        Assert.Equal(expected, _encoder.InputConnectionStatus);
        _inputStatusHandler.Verify(x => x.Invoke(expected, string.Empty, HdcpStatus.NotSupported));
    }

    [Theory]
    [InlineData("778", "1080p60")]
    [InlineData("0x030A", "1080p60")]
    [InlineData("0x030a", "1080p60")]
    [InlineData("796", "3840x2160p30")]
    [InlineData("0x031C", "3840x2160p30")]
    [InlineData("768", "480p30")]
    [InlineData("0x0300", "480p30")]
    [InlineData("0x031E", "4096x2160p30")]
    public void InputFormat_SetsTheInputResolution(string code, string expected)
    {
        Respond($"VID_INPUTFORMAT={code}\n");

        Assert.Equal(expected, _encoder.InputResolution);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x0000")]
    public void InputFormat_NoneClearsTheInputResolution(string code)
    {
        Respond("VID_INPUTFORMAT=0x030A\n");

        Respond($"VID_INPUTFORMAT={code}\n");

        Assert.Equal(string.Empty, _encoder.InputResolution);
    }

    [Theory]
    [InlineData("0x0201")]
    [InlineData("513")]
    public void InputFormat_ShowsAnUnrecognisedCodeAsReceived(string code)
    {
        Respond($"VID_INPUTFORMAT={code}\n");

        Assert.Equal(code, _encoder.InputResolution);
    }

    [Fact]
    public void InputStatus_CarriesTheResolutionAndNotSupportedHdcp()
    {
        Respond("GIS_VIDEOLOSS1=0\nVID_INPUTFORMAT=0x030A\n");

        _inputStatusHandler.Verify(x => x.Invoke(ConnectionState.Connected, "1080p60", HdcpStatus.NotSupported));
        Assert.DoesNotContain(_inputStatusHandler.Invocations,
            invocation => (HdcpStatus)invocation.Arguments[2] != HdcpStatus.NotSupported);
    }

    [Theory]
    [InlineData("ERROR:GIS_VIDEOLOSS1 Out of range.\r\n")]
    [InlineData("No Data\r\n")]
    [InlineData("GIS_VIDEOLOSS1=-1\n")]
    [InlineData("VID_INPUTFORMAT=-1\n")]
    [InlineData("VID_INPUTFORMAT=ERROR\n")]
    public void ErrorReply_LeavesTheInputStatusAlone(string body)
    {
        Respond("GIS_VIDEOLOSS1=0\nVID_INPUTFORMAT=0x030A\n");
        _inputStatusHandler.Invocations.Clear();

        Respond(body);

        Assert.Equal(ConnectionState.Connected, _encoder.InputConnectionStatus);
        Assert.Equal("1080p60", _encoder.InputResolution);
        Assert.Equal(HdcpStatus.NotSupported, _encoder.InputHdcpStatus);
        _inputStatusHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public void DeviceConnectionState_FollowsTheRestClient()
    {
        SetConnection(ConnectionState.Connected);
        Assert.Equal(ConnectionState.Connected, _encoder.DeviceConnectionState);

        SetConnection(ConnectionState.Error);
        Assert.Equal(ConnectionState.Error, _encoder.DeviceConnectionState);
    }

    [Fact]
    public void Constructor_TakesTheStateOfAnAlreadyConnectedClient()
    {
        SetConnection(ConnectionState.Connected);

        var encoder = new AntricaEncoder("Second Encoder", _restComms.Object);

        Assert.Equal(ConnectionState.Connected, encoder.DeviceConnectionState);
    }

    [Fact]
    public void LosingTheEncoder_ClearsTheInputStatusButKeepsHdcpNotSupported()
    {
        SetConnection(ConnectionState.Connected);
        Respond("GIS_VIDEOLOSS1=0\nVID_INPUTFORMAT=0x030A\n");

        SetConnection(ConnectionState.Error);

        Assert.Equal(ConnectionState.Unknown, _encoder.InputConnectionStatus);
        Assert.Equal(string.Empty, _encoder.InputResolution);
        Assert.Equal(HdcpStatus.NotSupported, _encoder.InputHdcpStatus);
    }
}
