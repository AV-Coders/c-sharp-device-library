using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class PhilipsSICPTest
{
    private readonly Mock<TcpClient> _mockClient;
    private readonly PhilipsSICP _display;
    private readonly byte _displayId = 0x00;
    private readonly byte _groupId = 0x00;

    public PhilipsSICPTest()
    {
        _mockClient = new("foo", PhilipsSICP.DefaultPort, "Test");
        _display = new PhilipsSICP(_mockClient.Object, _displayId, _groupId, "Test", Input.Hdmi1);
    }
    
    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = [0x06, 0x00, 0x00, 0x18, 0x02, 0x1C];
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_UsesTheCorrectDisplayId()
    {
        PhilipsSICP display2 = new PhilipsSICP(_mockClient.Object, 0x05, 0x01,"Test display", Input.Hdmi1);
        byte[] expectedPowerOnCommand = [0x06, 0x05, 0x01, 0x18, 0x02, 0x18];

        display2.PowerOn();
        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        byte[] expectedPowerOnCommand = [0x06, 0x00, 0x00, 0x18, 0x01, 0x1F];
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_UsesTheCorrectDisplayId()
    {
        PhilipsSICP display2 = new PhilipsSICP(_mockClient.Object, 0x04, 0x01, "Test display", Input.Hdmi1);
        byte[] expectedPowerOnCommand = [0x06, 0x04, 0x01, 0x18, 0x01, 0x1A];

        display2.PowerOff();
        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Theory]
    [InlineData(Input.Hdmi1, new byte[] { 0x09, 0x00, 0x00, 0xAC, 0x0D, 0x00, 0x01, 0x00, 0xA9 })]
    [InlineData(Input.Hdmi2, new byte[] { 0x09, 0x00, 0x00, 0xAC, 0x06, 0x00, 0x01, 0x00, 0xA2 })]
    [InlineData(Input.Hdmi3, new byte[] { 0x09, 0x00, 0x00, 0xAC, 0x0F, 0x00, 0x01, 0x00, 0xAB })]
    [InlineData(Input.Hdmi4, new byte[] { 0x09, 0x00, 0x00, 0xAC, 0x19, 0x00, 0x01, 0x00, 0xBD })]
    public void SetInput_SendsTheCommand(Input input, byte[] expectedCommand)
    {
        _display.SetInput(input);
        
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, new byte[] { 0x06, 0x00, 0x00, 0x47, 0x01, 0x40 })]
    [InlineData(MuteState.Off, new byte[] { 0x06, 0x00, 0x00, 0x47, 0x00, 0x41 })]
    public void SetAudioMute_SendsTheCommand(MuteState state, byte[] expectedState)
    {
        _display.SetAudioMute(state);
        _mockClient.Verify(x => x.Send(expectedState), Times.Once);
    }

    [Theory]
    [InlineData(0, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x00, 0x00, 0x43 })]
    [InlineData(10, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x0A, 0x0A, 0x43 })]
    [InlineData(20, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x14, 0x14, 0x43 })]
    [InlineData(30, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x1E, 0x1E, 0x43 })]
    [InlineData(40, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x28, 0x28, 0x43 })]
    [InlineData(50, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x32, 0x32, 0x43 })]
    [InlineData(60, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x3C, 0x3C, 0x43 })]
    [InlineData(70, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x46, 0x46, 0x43 })]
    [InlineData(80, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x50, 0x50, 0x43 })]
    [InlineData(90, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x5A, 0x5A, 0x43 })]
    [InlineData(100, new byte[] { 0x07, 0x00, 0x00, 0x44, 0x64, 0x64, 0x43 })]
    public void SetVolume_SendsTheCommand(int volume, byte[] expectedCommand)
    {
        _display.SetVolume(volume);
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x02, 0x1D }, PowerState.On)]
    [InlineData(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x01, 0x1E }, PowerState.Off)]
    public void HandleResponse_UpdatesThePowerState(byte[] response, PowerState expectedState)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        
        Assert.Equal(expectedState, _display.PowerState);
    }
}