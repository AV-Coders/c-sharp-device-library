using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class PjLinkTest
{
    private readonly PjLink _display;
    private readonly Mock<TcpClient> _mockClient;

    public PjLinkTest()
    {
        _mockClient = new Mock<TcpClient>("foo", PjLink.DefaultPort, "bar");
        _display = new PjLink(_mockClient.Object, "Test display", null);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        string expectedPowerCommand = "%1POWR 0\r";
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        string expectedPowerCommand = "%1POWR 1\r";
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }

    [Theory]
    [InlineData(Input.Hdmi1, "%1INPT 31\r")]
    [InlineData(Input.Hdmi2, "%1INPT 32\r")]
    public void SetInput_SendsTheExpectedCommand(Input source, string command)
    {
        _display.SetInput(source);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Fact]
    public void SetVolume_SendsTheExpectedCommand()
    {
        _display.SetVolume(1);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(MuteState.On, "%1AVMT 21\r")]
    [InlineData(MuteState.Off, "%1AVMT 30\r")]
    public void SetAudioMute_SendsTheExpectedCommand(MuteState state, string command)
    {
        _display.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, "%1AVMT 11\r")]
    [InlineData(MuteState.Off, "%1AVMT 30\r")]
    public void SetPictureMute_SendsTheExpectedCommand(MuteState state, string command)
    {
        _display.SetPictureMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, MuteState.On, "%1AVMT 31\r")]
    [InlineData(MuteState.Off, MuteState.Off, "%1AVMT 30\r")]
    [InlineData(MuteState.On, MuteState.Off, "%1AVMT 21\r")]
    [InlineData(MuteState.Off, MuteState.On, "%1AVMT 11\r")]
    public void SetPictureAndAudioMutes_SendTheExpectedCommand(MuteState audioState, MuteState videoState,
        string command)
    {
        _display.SetPictureMute(videoState);
        _display.SetAudioMute(audioState);

        Assert.Equal(command, _mockClient.Invocations.Last().Arguments[0]);
    }

    [Theory]
    [InlineData("%1POWR=1", PowerState.On)]
    [InlineData("%1POWR=0", PowerState.Off)]
    public void HandleResponse_UpdatesPowerState(string input, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(input);

        Assert.Equal(_display.PowerState, expectedPowerState);
    }

    [Theory]
    [InlineData("%1INPT=31", Input.Hdmi1)]
    [InlineData("%1INPT=32", Input.Hdmi2)]
    public void HandleResponse_UpdatesInput(string input, Input expectedInput)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(input);

        Assert.Equal(_display.Input, expectedInput);
    }

    [Fact]
    public void HandleResponse_ForcesPowerState()
    {
        _display.PowerOff();
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1");

        _mockClient.Verify(x => x.Send("%1POWR 0\r"), Times.Exactly(2));
    }

    [Fact]
    public void HandleResponse_DoesntForcePowerStateWhenCorrect()
    {
        _display.PowerOn();
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1");

        _mockClient.Verify(x => x.Send("%1POWR 1\r"), Times.Exactly(1));
    }

    [Fact]
    public void HandleResponse_ForcesInput()
    {
        _display.SetInput(Input.Hdmi1);
        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=32");

        _mockClient.Verify(x => x.Send("%1INPT 31\r"), Times.Exactly(2));
    }

    [Fact]
    public void HandleResponse_DoesntForceInputWhenCorrect()
    {
        _display.SetInput(Input.Hdmi1);
        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=31");

        _mockClient.Verify(x => x.Send("%1INPT 31\r"), Times.Exactly(1));
    }

    [Fact]
    public void HandleResponse_ForcesPictureMute()
    {
        _display.SetPictureMute(MuteState.On);
        _mockClient.Object.ResponseHandlers?.Invoke("%1AVMT=30");

        _mockClient.Verify(x => x.Send("%1AVMT 11\r"), Times.Exactly(2));
    }

    [Fact]
    public void HandleResponse_LogsInAndPollsPower()
    {
        // AV Coders
        _mockClient.Object.ResponseHandlers!.Invoke("PJLINK 1 3bcc52b3");
        byte[] expected = { 0x36, 0x35, 0x36, 0x30, 0x34, 0x65, 0x38, 0x63, 0x61, 0x34, 0x32, 0x65, 0x36, 0x34, 0x65, 0x36, 0x64, 0x31, 0x63, 0x39, 0x39, 0x38, 0x66, 0x39, 0x65, 0x39, 0x35, 0x33, 0x35, 0x64, 0x38, 0x38, 0x25, 0x31, 0x50, 0x4f, 0x57, 0x52, 0x20, 0x3f, 0x0d };

        _mockClient.Verify(x => x.Send(expected), Times.Once);
    }

    [Fact]
    public void GetMd5Hash_ReturnsTheHash()
    {
        var actual = _display.GetMd5Hash("3bcc52b3JBMIAProjectorLink");
        var expected = new byte[] { 0x36, 0x35, 0x36, 0x30, 0x34, 0x65, 0x38, 0x63, 0x61, 0x34, 0x32, 0x65, 0x36, 0x34, 0x65, 0x36, 0x64, 0x31, 0x63, 0x39, 0x39, 0x38, 0x66, 0x39, 0x65, 0x39, 0x35, 0x33, 0x35, 0x64, 0x38, 0x38 };
        Assert.Equal(expected, actual);

    }
}