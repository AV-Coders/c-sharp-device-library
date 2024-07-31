using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class LgCommercialTest
{
    private readonly LGCommercial _display;
    private readonly Mock<TcpClient> _client;

    public LgCommercialTest()
    {
        _client = new Mock<TcpClient>("foo", (ushort)1);
        _display = new LGCommercial(_client.Object, "00-00-00-00-00-00",0);
    }
    
    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        String expectedPowerOnCommand = "ka 00 01\r";
        _display.PowerOn();

        _client.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        String expectedPowerOffCommand = "ka 00 00\r";
        _display.PowerOff();

        _client.Verify(x => x.Send(expectedPowerOffCommand), Times.Once);
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

        _client.Verify(x => x.Send(expectedInputCommand), Times.Once);
    }

    [Theory]
    [InlineData(0, "kf 00 00\r")]
    [InlineData(50, "kf 00 32\r")]
    [InlineData(100, "kf 00 64\r")]
    public void SetVolume_SetsTheVolume(int volume, string expectedVolumeCommand)
    {
        _display.SetVolume(volume);

        _client.Verify(x => x.Send(expectedVolumeCommand), Times.Once);
    }
    
    [Theory]
    [InlineData(MuteState.On, "ke 00 00\r")]
    [InlineData(MuteState.Off, "ke 00 01\r")]
    public void setAudioMute_SendsTheCommand(MuteState state, string expectedMuteCommand)
    {
        _display.SetAudioMute(state);

        _client.Verify(x => x.Send(expectedMuteCommand), Times.Once);
    }

    [Theory]
    [InlineData(1, "ma 00 00 01 10\r")]
    [InlineData(10, "ma 00 00 0A 10\r")]
    [InlineData(72, "ma 00 00 48 10\r")]
    [InlineData(99, "ma 00 00 63 10\r")]
    public void SetChannel_SendsTheCommand(int channel, string expectedCommand)
    {
        _display.SetChannel(channel);
        
        _client.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData("a 00 OK01x", PowerState.On)]
    [InlineData("a 00 OK00x", PowerState.Off)]
    public void HandleResponse_UpdatesThePowerState(string response, PowerState expectedState)
    {

        Mock<PowerStateHandler> handler = new Mock<PowerStateHandler>();
        _display.PowerStateHandlers += handler.Object;
        
        _client.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedState));
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
        
        _client.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedInput));
    }

    [Theory]
    [InlineData("e 00 OK01x", MuteState.Off)]
    [InlineData("e 00 OK00x", MuteState.On)]
    public void HandleResponse_UpdatesTheMuteState(string response, MuteState expectedState)
    {

        Mock<MuteStateHandler> handler = new Mock<MuteStateHandler>();
        _display.MuteStateHandlers += handler.Object;
        
        _client.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedState));
    }

    [Theory]
    [InlineData("f 00 OK00x", 0)]
    [InlineData("f 00 OK09x", 9)]
    [InlineData("f 00 OK64x", 100)]
    public void HandleResponse_UpdatesTheVolumeLevel(string response, int expectedVolume)
    {

        Mock<VolumeLevelHandler> handler = new Mock<VolumeLevelHandler>();
        _display.VolumeLevelHandlers += handler.Object;
        
        _client.Object.ResponseHandlers!.Invoke(response);
        
        handler.Verify(x => x.Invoke(expectedVolume));
    }
}