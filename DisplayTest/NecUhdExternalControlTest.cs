using System.Text;
using AVCoders.Core;
using Moq;
using Xunit.Abstractions;

namespace AVCoders.Display.Tests;

public class NecUhdExternalControlTest
{
    private readonly ITestOutputHelper _testOutputHelper;
    private NecUhdExternalControl _display;
    private readonly Mock<TcpClient> _mockClient;

    public NecUhdExternalControlTest(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _mockClient = new Mock<TcpClient>("foo", (ushort)1);
        _display = new NecUhdExternalControl(_mockClient.Object, (byte)'A');
    }

    [Fact]
    public void Constructor_SetsPortTo7142()
    {
        _mockClient.Verify(x => x.SetPort(7142), Times.Once);
    }
    
    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        byte[] expectedPowerCommand = { 0x01, 0x30, 0x41, 0x30, 0x41, 0x30, 0x43, 0x02, 
            (byte)'C', (byte)'2', (byte)'0', (byte)'3', (byte)'D', (byte)'6', (byte)'0', (byte)'0', (byte)'0', (byte)'4', 0x03, 0x76, 0x0d };
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }

    // [Fact]
    // public void PowerOff_UsesTheCorrectDisplayId()
    // {
    //     NecUhdExternalControl samsungMdcForDisplay2 = new NecUhdExternalControl(0x03, _mockClient.Object);
    //     byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x03, 0x01, 0x00, 0x15 };
    //
    //     samsungMdcForDisplay2.PowerOff();
    //     _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    // }

    [Fact]
    public void PowerOff_UpdatesInternalPowerState()
    {
        _display.PowerOff();

        Assert.Equal(PowerState.Off, _display.GetCurrentPowerState());
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = { 0x01, 0x30, 0x41, 0x30, 0x41, 0x30, 0x43, 0x02, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, 0x03, 0x73, 0x0d };
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    // [Fact]
    // public void PowerOn_UsesTheCorrectDisplayId()
    // {
    //     SamsungMdc samsungMdcForDisplay2 = new SamsungMdc(0x02, _mockClient.Object);
    //     byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x02, 0x01, 0x01, 0x15 };
    //
    //     samsungMdcForDisplay2.PowerOn();
    //     _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    // }

    [Fact]
    public void PowerOn_UpdatesInternalPowerState()
    {
        _display.PowerOn();

        Assert.Equal(PowerState.On, _display.GetCurrentPowerState());
    }
    
    [Theory]
    [InlineData(Input.Hdmi1, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x31, 0x31, 0x03, 0x72, 0x0d })]
    [InlineData(Input.Hdmi2, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x31, 0x32, 0x03, 0x71, 0x0d })]
    [InlineData(Input.Hdmi3, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x38, 0x32, 0x03, 0x78, 0x0d })]
    [InlineData(Input.Hdmi4, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x38, 0x33, 0x03, 0x79, 0x0d })]
    public void SetInput_SendsTheExpectedCommand(Input source, byte[] command)
    {
        _display.SetInput(source);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
    
    [Theory]
    [InlineData(0,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x30, 0x03, 0x70, 0x0d })]
    [InlineData(1,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x31, 0x03, 0x71, 0x0d })]
    [InlineData(100, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x36, 0x34, 0x03, 0x72, 0x0d })]
    public void SetVolume_SendsTheExpectedCommand(int volume, byte[] command)
    {
        _display.SetVolume(volume);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
    
    [Theory]
    [InlineData(MuteState.On,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, 0x31, 0x03, 0x09, 0x0d })]
    [InlineData(MuteState.Off,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, 0x32, 0x03, 0x0A, 0x0d })]
    public void SetAudioMute_SendsTheExpectedCommand(MuteState state, byte[] command)
    {
        _display.SetAudioMute(state);

        foreach (IInvocation x in _mockClient.Invocations)
        {
            foreach (Object xArgument in x.Arguments)
            {
                if (xArgument.GetType() == typeof(Byte[]))
                {
                    byte[] bytes = (byte[])xArgument;
                    _testOutputHelper.WriteLine(Encoding.ASCII.GetString(bytes));
                }
            }
        }

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
}