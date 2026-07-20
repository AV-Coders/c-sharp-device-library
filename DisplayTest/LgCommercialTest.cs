using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;
using Moq;

namespace AVCoders.Display.Tests;

public class LgCommercialTest
{
    private readonly LGCommercial _display;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
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

    public LgCommercialTest()
    {
        _display = new LGCommercial(_mockClient.Object, "Test display", "00-00-00-00-00-00",null, 0);
    }
    
    private static void SetConnectionState(Mock<TcpClient> mock, ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mock.Object, state);

    private Task InvokeDoPoll() =>
        (Task)typeof(LGCommercial).GetMethod("DoPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_display, [CancellationToken.None])!;

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aabb.ccdd.eeff")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("not a mac")]
    public void Constructor_ToleratesCommonMacFormats(string mac)
    {
        var exception = Record.Exception(() =>
            new LGCommercial(TestFactory.CreateTcpClient().Object, "Mac test display", mac, null, 0));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Poll_WhileDisconnected_LeavesPowerStateAlone()
    {
        SetConnectionState(_mockClient, ConnectionState.Disconnected);
        _display.PowerOn();

        await InvokeDoPoll();

        // The old poll equated disconnected with off, flipping the state and re-firing
        // the power command every cycle.
        Assert.Equal(PowerState.On, _display.PowerState);
        _mockClient.Verify(x => x.Send("ka 00 FF\r"), Times.Never);
    }

    [Fact]
    public async Task Poll_WhileConnected_TrustsThePowerResponse_EvenWhenTheScreenIsOff()
    {
        SetConnectionState(_mockClient, ConnectionState.Connected);
        _mockClient.Object.ResponseHandlers!.Invoke("a 00 OK00x");
        Assert.Equal(PowerState.Off, _display.PowerState);

        await InvokeDoPoll();

        // The old poll forced PowerState.On whenever the connection was up, which lies
        // for displays with PM Mode set to Screen Off Always.
        Assert.Equal(PowerState.Off, _display.PowerState);
        _mockClient.Verify(x => x.Send("ka 00 FF\r"), Times.AtLeastOnce);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        string expectedPowerOnCommand = "ka 00 01\r";
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        string expectedPowerOffCommand = "ka 00 00\r";
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOffCommand), Times.Once);
    }
    
    [Theory]
    [InlineData(Input.Hdmi1, "xb 00 90\r")]
    [InlineData(Input.Hdmi2, "xb 00 91\r")]
    [InlineData(Input.Hdmi3, "xb 00 92\r")]
    [InlineData(Input.Hdmi4, "xb 00 93\r")]
    [InlineData(Input.DvbtTuner, "xb 00 00\r")]
    public void SetInput_SetsTheInput(Input input, string expectedInputCommand)
    {
        _display.SetInput(input);

        _mockClient.Verify(x => x.Send(expectedInputCommand), Times.Once);
    }

    [Theory]
    [InlineData(0, "kf 00 00\r")]
    [InlineData(50, "kf 00 32\r")]
    [InlineData(100, "kf 00 64\r")]
    public void SetVolume_SetsTheVolume(int volume, string expectedVolumeCommand)
    {
        _display.SetVolume(volume);

        _mockClient.Verify(x => x.Send(expectedVolumeCommand), Times.Once);
    }
    
    [Theory]
    [InlineData(MuteState.On, "ke 00 00\r")]
    [InlineData(MuteState.Off, "ke 00 01\r")]
    public void setAudioMute_SendsTheCommand(MuteState state, string expectedMuteCommand)
    {
        _display.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(expectedMuteCommand), Times.Once);
    }

    [Theory]
    [InlineData(1, "ma 00 00 01 10\r")]
    [InlineData(10, "ma 00 00 0A 10\r")]
    [InlineData(72, "ma 00 00 48 10\r")]
    [InlineData(99, "ma 00 00 63 10\r")]
    public void SetChannel_SendsTheCommand(int channel, string expectedCommand)
    {
        _display.SetChannel(channel);
        
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData("a 00 OK01x", PowerState.On)]
    [InlineData("a 00 OK00x", PowerState.Off)]
    public void HandleResponse_UpdatesThePowerState(string response, PowerState expectedState)
    {

        Mock<PowerStateHandler> handler = new Mock<PowerStateHandler>();
        _display.PowerStateHandlers += handler.Object;
        
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedState));
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseHandlers!.Invoke("a 00 OK01x");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_GivenAnNgResponse_ReportsAnError()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("a 00 NG01x");

        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
    }

    [Theory]
    [InlineData("b 00 OK90x", Input.Hdmi1)]
    [InlineData("b 00 OK91x", Input.Hdmi2)]
    [InlineData("b 00 OK92x", Input.Hdmi3)]
    [InlineData("b 00 OK93x", Input.Hdmi4)]
    [InlineData("b 00 OK00x", Input.DvbtTuner)]
    public void HandleResponse_UpdatesTheInput(string response, Input expectedInput)
    {

        Mock<InputHandler> handler = new Mock<InputHandler>();
        _display.InputHandlers += handler.Object;
        
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedInput));
    }

    [Theory]
    [InlineData("e 00 OK01x", MuteState.Off)]
    [InlineData("e 00 OK00x", MuteState.On)]
    public void HandleResponse_UpdatesTheMuteState(string response, MuteState expectedState)
    {

        Mock<MuteStateHandler> handler = new Mock<MuteStateHandler>();
        _display.MuteStateHandlers += handler.Object;
        
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedState));
    }

    [Theory]
    [InlineData("f 00 OK01x", 1)]
    [InlineData("f 00 OK09x", 9)]
    [InlineData("f 00 OK64x", 100)]
    public void HandleResponse_UpdatesTheVolumeLevel(string response, int expectedVolume)
    {

        Mock<VolumeLevelHandler> handler = new Mock<VolumeLevelHandler>();
        _display.VolumeLevelHandlers += handler.Object;
        
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedVolume));
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _display.SendIRCode(button);
    }
}