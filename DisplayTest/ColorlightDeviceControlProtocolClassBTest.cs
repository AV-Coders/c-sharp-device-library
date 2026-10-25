using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Display.Tests;

public class ColorlightDeviceControlProtocolClassBTest
{

    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient(ColorlightDeviceControlProtocolClassB.TcpPort);
    private readonly ColorlightDeviceControlProtocolClassB _display;

    public ColorlightDeviceControlProtocolClassBTest()
    {
        _display = new ColorlightDeviceControlProtocolClassB(_mockClient.Object, "LED Wall");
    }

    [Fact]
    public void Module_RespondsToHeartbeat()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00]);

        _mockClient.Verify(x => x.Send(new byte[] { 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void Heartbeat_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00]);

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void Module_RespondsToHeartbeatsChainedTogether()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00]);

        _mockClient.Verify(x => x.Send(new byte[] { 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }), Times.Exactly(4));
    }

    [Fact]
    public void Module_RespondsToAHeartbeatSplitAcrossTwoReceives()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x99, 0x99, 0x00]);
        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x08, 0x00, 0x00, 0x00, 0x00]);

        _mockClient.Verify(x => x.Send(new byte[] { 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }), Times.Once);
    }

    [Fact]
    public void Module_RespondsToAHeartbeatAfterOtherBytes()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x99, 0x99]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x0A]);

        _mockClient.Verify(x => x.Send(new byte[] { 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }), Times.Once);
    }

    [Fact]
    public void PowerOn_DisablesBlackout()
    {
        _display.PowerOn();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void PowerOff_EnablesBlackout()
    {
        _display.PowerOff();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }));
    }

    [Theory]
    [InlineData(0, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(50, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x88, 0x13 })]
    [InlineData(100, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x27 })]
    public void SetBrightness_SendsTheCommand(uint brightness, byte[] expectedCommand)
    {
        _display.SetBrightness(brightness);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void SetBrightness_IgnoresValuesOver100()
    {
        
        _display.SetBrightness(101);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }

    [Theory]
    [InlineData(4u, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x58, 0x02 })]
    [InlineData(10u, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(11u, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(uint.MaxValue, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public void BrightnessDown_StopsAtZero(uint amount, byte[] expectedCommand)
    {
        _display.SetBrightness(10);
        _mockClient.Invocations.Clear();

        _display.BrightnessDown(amount);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(0, (byte)0x00)]
    [InlineData(15, (byte)0x0F)]
    public void RecallPreset_SendsThePreset(int preset, byte expectedPreset)
    {
        _display.RecallPreset(preset);

        _mockClient.Verify(x => x.Send(new byte[] { 0x07, 0x10, 0x03, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, expectedPreset, 0x00 }), Times.Once);
    }

    [Theory]
    [InlineData(0, (byte)0x00)]
    [InlineData(15, (byte)0x0F)]
    public void RecallPresetOnZ8_SendsThePreset(int preset, byte expectedPreset)
    {
        _display.RecallPresetOnZ8(preset);

        _mockClient.Verify(x => x.Send(new byte[] { 0x02, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, expectedPreset, 0x00 }), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(256)]
    public void RecallPreset_OutsideTheSixteenPresets_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _display.RecallPreset(preset);
        _display.RecallPresetOnZ8(preset);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        Assert.Equal(2, _display.Events.Count(e => e.Type == EventType.Error && e.Info == $"Preset {preset} is outside 0-15"));
    }

    [Fact]
    public void RestartPolling_LeavesThePollWorkerStopped()
    {
        typeof(Display).GetMethod("RestartPolling", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_display, null);

        var pollWorker = (ThreadWorker)typeof(Display).GetField("PollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_display)!;
        Assert.False(pollWorker.IsRunning);
    }
}