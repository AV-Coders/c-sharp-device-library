using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;
using Moq;

namespace AVCoders.Display.Tests;

public class SonySimpleIPControlTest
{
    private readonly SonySimpleIpControl _sonyTv;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    readonly Mock<PowerStateHandler> _powerStateHandler = new ();
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

    public SonySimpleIPControlTest()
    {
        _sonyTv = new SonySimpleIpControl(_mockClient.Object, "Test display", Input.Hdmi1);
        _sonyTv.PowerStateHandlers += _powerStateHandler.Object;
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        string input = "Foo";

        var method = _sonyTv.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        method?.Invoke(_sonyTv, [input]);
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationIsOkay()
    {
        string input = "Foo";

        var method = _sonyTv.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        method?.Invoke(_sonyTv, [input]);
        Assert.Equal(CommunicationState.Okay, _sonyTv.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        string input = "Foo";

        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));
        var method = _sonyTv.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        method?.Invoke(_sonyTv, [input]);
        Assert.Equal(CommunicationState.Error, _sonyTv.CommunicationState);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        string expectedPowerOnCommand = "*SCPOWR0000000000000001\n";
        _sonyTv.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        string expectedPowerOffCommand = "*SCPOWR0000000000000000\n";
        _sonyTv.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOffCommand), Times.Once);
    }

    [Theory]
    [InlineData("*SNPOWR0000000000000001\n", PowerState.On)]
    [InlineData("*SNPOWR0000000000000000\n", PowerState.Off)]
    public void HandleResponse_SetsThePowerState(string response, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        Assert.Equal(expectedPowerState, _sonyTv.PowerState);
    }

    [Theory]
    [InlineData("*SNPOWR0000000000000001\n", PowerState.On)]
    [InlineData("*SNPOWR0000000000000000\n", PowerState.Off)]
    public void HandleResponse_CallsThePowerDelegate(string response, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);
        
        _powerStateHandler.Verify(x => x.Invoke(expectedPowerState));
    }

    [Fact]
    public void HandleResponse_SetsTheVolume()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("*SNVOLU0000000000000010\n");

        Assert.Equal(10, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_InvokesTheVolumeDelegate()
    {
        Mock<VolumeLevelHandler> volumeLevelHandler = new Mock<VolumeLevelHandler>();
        _sonyTv.VolumeLevelHandlers += volumeLevelHandler.Object;
        _mockClient.Object.ResponseHandlers?.Invoke("*SNVOLU0000000000000010\n");

        volumeLevelHandler.Verify(x => x.Invoke(10));
    }

    [Theory]
    [InlineData("*SNAMUT0000000000000000\n", MuteState.Off)]
    [InlineData("*SNAMUT0000000000000001\n", MuteState.On)]
    public void HandleResponse_SetsTheAudioMuteState(string input, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(input);
        
        Assert.Equal(expectedMuteState, _sonyTv.AudioMute);
    }

    [Theory]
    [InlineData("*SNAMUT0000000000000000\n", MuteState.Off)]
    [InlineData("*SNAMUT0000000000000001\n", MuteState.On)]
    public void HandleResponse_InvokesTheDelegate(string input, MuteState expectedMuteState)
    {
        Mock<MuteStateHandler> muteStateHandler = new Mock<MuteStateHandler>();
        _sonyTv.MuteStateHandlers += muteStateHandler.Object;
        _mockClient.Object.ResponseHandlers?.Invoke(input);
        
        muteStateHandler.Verify(x => x.Invoke(expectedMuteState));
    }

    [Theory]
    [InlineData("*SNINPT0000000100000001\n", Input.Hdmi1)]
    [InlineData("*SNINPT0000000100000002\n", Input.Hdmi2)]
    [InlineData("*SNINPT0000000100000003\n", Input.Hdmi3)]
    [InlineData("*SNINPT0000000100000004\n", Input.Hdmi4)]
    [InlineData("*SNINPT0000000000000000\n", Input.DvbtTuner)]
    public void HandleResponse_SetsTheInput(string response, Input expectedInput)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        Assert.Equal(expectedInput, _sonyTv.Input);
    }

    [Theory]
    [InlineData("*SNINPT0000000100000001\n", Input.Hdmi1)]
    [InlineData("*SNINPT0000000100000002\n", Input.Hdmi2)]
    [InlineData("*SNINPT0000000100000003\n", Input.Hdmi3)]
    [InlineData("*SNINPT0000000100000004\n", Input.Hdmi4)]
    [InlineData("*SNINPT0000000000000000\n", Input.DvbtTuner)]
    public void HandleResponse_InvokesTheInputDelegate(string response, Input expectedInput)
    {
        Mock<InputHandler> inputHandler = new Mock<InputHandler>();
        _sonyTv.InputHandlers += inputHandler.Object;
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        inputHandler.Verify(x => x.Invoke(expectedInput));
    }

    [Fact]
    public void HandleResponse_HandlesAMultiResponseString()
    {
        _mockClient.Object.ResponseHandlers?.Invoke(
            "*SNINPT0000000000000000\n*SNPOWR0000000000000001\n*SNVOLU0000000000000010\n*SAVOLU0000000000000000\n");

        Assert.Equal(10, _sonyTv.Volume);
        Assert.Equal(PowerState.On, _sonyTv.PowerState);
        Assert.Equal(Input.DvbtTuner, _sonyTv.Input);
    }

    [Fact]
    public void HandleResponse_HandlesWhiteSpaceInAMultiResponseString()
    {
        _mockClient.Object.ResponseHandlers?.Invoke(
            "*SNINPT0000000000000000\n\t \t*SNPOWR0000000000000001\n                        *SNVOLU0000000000000010\n");

        Assert.Equal(10, _sonyTv.Volume);
        Assert.Equal(PowerState.On, _sonyTv.PowerState);
        Assert.Equal(Input.DvbtTuner, _sonyTv.Input);
    }

    [Theory]
    [InlineData(Input.Hdmi1, "*SCINPT0000000100000001\n")]
    [InlineData(Input.Hdmi2, "*SCINPT0000000100000002\n")]
    [InlineData(Input.Hdmi3, "*SCINPT0000000100000003\n")]
    [InlineData(Input.Hdmi4, "*SCINPT0000000100000004\n")]
    public void SetInput_SetsTheInput(Input input, string expectedInputCommand)
    {
        _sonyTv.SetInput(input);

        _mockClient.Verify(x => x.Send(expectedInputCommand), Times.Once);
    }

    [Theory]
    [InlineData(0, "*SCVOLU0000000000000000\n")]
    [InlineData(100, "*SCVOLU0000000000000100\n")]
    public void SetVolume_SetsTheVolume(int volume, string expectedVolumeCommand)
    {
        _sonyTv.SetVolume(volume);

        _mockClient.Verify(x => x.Send(expectedVolumeCommand), Times.Once);
    }

    [Fact]
    public void SetVolume_UpdatesInternalState()
    {
        _sonyTv.SetVolume(15);

        Assert.Equal(15, _sonyTv.Volume);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SetVolume_IgnoresInvalidValues(int volume)
    {
        _sonyTv.SetVolume(volume);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(MuteState.On, "*SCAMUT0000000000000001\n")]
    [InlineData(MuteState.Off, "*SCAMUT0000000000000000\n")]
    public void setAudioMute_SendsTheCommand(MuteState state, string expectedMuteCommand)
    {
        _sonyTv.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(expectedMuteCommand), Times.Once);
    }


    [Theory]
    [InlineData(MuteState.On, "*SCPMUT0000000000000001\n")]
    [InlineData(MuteState.Off, "*SCPMUT0000000000000000\n")]
    public void setPictureMute_SendsTheCommand(MuteState state, string expectedMuteCommand)
    {
        _sonyTv.SetVideoMute(state);

        _mockClient.Verify(x => x.Send(expectedMuteCommand), Times.Once);
    }

    [Fact]
    public void setPictureMute_UpdatesInternalState()
    {
        _sonyTv.SetVideoMute(MuteState.On);

        Assert.Equal(MuteState.On, _sonyTv.VideoMute);
    }

    [Fact]
    public void SendIrCode_SendsTheCommand()
    {
        string expectedCommand = "*SCIRCC0000000000000032\n";

        _sonyTv.SendIRCode(RemoteButton.Mute);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _sonyTv.SendIRCode(button);
    }

    [Fact]
    public void SetChannel_SendsTheCommand()
    {
        string expectedCommand = "*SCCHNN0000000000000002\n";
        
        _sonyTv.SetChannel(2);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
    
    [Fact]
    public void ChannelUp_SendsTheCommand()
    {
        string expectedCommand = "*SCIRCC0000000000000033\n";
        
        _sonyTv.ChannelUp();
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
    
    [Fact]
    public void ChannelDown_SendsTheCommand()
    {
        string expectedCommand = "*SCIRCC0000000000000034\n";
        
        _sonyTv.ChannelDown();
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}