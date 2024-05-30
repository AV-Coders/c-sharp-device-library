using AVCoders.Core;
using Moq;

namespace AVCoders.Motor.Tests;

public class MotoluxBlindTransmitterTest
{
    private Mock<SerialClient> _mockClient;
    private MotoluxBlindTransmitter _device;

    public MotoluxBlindTransmitterTest()
    {
        _mockClient = new Mock<SerialClient>();
        _device = new MotoluxBlindTransmitter("Blind", '\u0002', '\u0001', RelayAction.Lower, 30, _mockClient.Object);
    }

    [Fact]
    public void Lower_SendsTheCommand()
    {
        _device.Lower();

        char[] expectedPayload = { '\u009a', '\u0002', '\u0001', '\u0000', '\u000a', '\u00EE', '\u00E7' };
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Fact]
    public void Raise_SendsTheCommand()
    {
        _device.Raise();

        char[] expectedPayload = { '\u009a', '\u0002', '\u0001', '\u0000', '\u000a', '\u00DD', '\u00D4' };
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Fact]
    public void Stop_SendsTheCommand()
    {
        _device.Stop();
        char[] expectedPayload = { '\u009a', '\u0002', '\u0001', '\u0000', '\u000a', '\u00CC', '\u00C5' };
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Theory]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(1, 0x01, 0x00)]
    [InlineData(2, 0x02, 0x00)]
    [InlineData(10, 0x00, 0x02)]
    [InlineData(12, 0x00, 0x08)]
    [InlineData(16, 0x00, 0x80)]
    public void GetIdBytes_ReturnsTheCorrectValue(char input, char expectedLow, char expectedHigh)
    {
        (char actualLow, char actualHigh) = _device.GetIdBytes(input);
        
        Assert.Equal(expectedLow, actualLow);
        Assert.Equal(expectedHigh, actualHigh);
    }
}