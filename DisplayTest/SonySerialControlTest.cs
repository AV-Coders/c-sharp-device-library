using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class SonySerialControlTest
{
    private readonly SonySerialControl _display;
    private readonly Mock<SerialClient> _mockClient;

    public SonySerialControlTest()
    {
        _mockClient = new Mock<SerialClient>("foo");
        _display = new SonySerialControl(_mockClient.Object, "Test display", Input.Hdmi1);
    }
    
    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        char[] expectedPowerCommand = ['\u008C', '\u0000', '\u0000', '\u0002', '\u0000', '\u008e'];
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }
    
    

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        char[] expectedPowerOnCommand = ['\u008C', '\u0000', '\u0000', '\u0002','\u0001', '\u008f'];
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }
    
    [Theory]
    [InlineData(Input.Hdmi1, new char[] { '\u008C','\u0000','\u0002','\u0003','\u0004','\u0001','\u0096' })]
    [InlineData(Input.Hdmi2, new char[] { '\u008C','\u0000','\u0002','\u0003','\u0004','\u0002','\u0097' })]
    [InlineData(Input.Hdmi3, new char[] { '\u008C','\u0000','\u0002','\u0003','\u0004','\u0003','\u0098' })]
    [InlineData(Input.Hdmi4, new char[] { '\u008C','\u0000','\u0002','\u0003','\u0004','\u0004','\u0099' })]
    public void SetInput_SendsTheExpectedCommand(Input source, char[] command)
    {
        _display.SetInput(source);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
    
    [Theory]
    [InlineData(0,   new char[] { '\u008C', '\u0000', '\u0005', '\u0003', '\u0001', '\u0000', '\u0095' })]
    [InlineData(1,   new char[] { '\u008C', '\u0000', '\u0005', '\u0003', '\u0001', '\u0001', '\u0096' })]
    [InlineData(100, new char[] { '\u008C', '\u0000', '\u0005', '\u0003', '\u0001', '\u0064', '\u00F9' })]
    public void SetVolume_SendsTheExpectedCommand(int volume, char[] command)
    {
        _display.SetVolume(volume);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
    
    [Theory]
    [InlineData(MuteState.On,   new char[] { '\u008C','\u0000','\u0006','\u0003','\u0001','\u0001','\u0097'})]
    [InlineData(MuteState.Off,   new char[] { '\u008C','\u0000','\u0006','\u0003','\u0001','\u0000','\u0096'})]
    public void SetAudioMute_SendsTheExpectedCommand(MuteState state, char[] command)
    {
        _display.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
}