using AVCoders.Core;
using Moq;

namespace AVCoders.Motor.Tests;

public class MotoluxBlindTransmitterTest
{
    private readonly Mock<SerialClient> _mockClient = new ("foo", "bar", (ushort)1);
    private readonly MotoluxBlindTransmitter _device;

    public MotoluxBlindTransmitterTest()
    {
        _device = new MotoluxBlindTransmitter("Blind", '\u0002', '\u0010', RelayAction.Lower, 30, _mockClient.Object);
    }

    [Fact]
    public void Lower_SendsTheCommand()
    {
        _device.Lower();

        char[] expectedPayload = ['\u009a', '\u0002', '\u0000', '\u0002', '\u000a', '\u00EE', '\u00E4'];
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Fact]
    public void Raise_SendsTheCommand()
    {
        _device.Raise();

        char[] expectedPayload = ['\u009a', '\u0002', '\u0000', '\u0002', '\u000a', '\u00DD', '\u00d7'];
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Fact]
    public void Stop_SendsTheCommand()
    {
        _device.Stop();
        char[] expectedPayload = ['\u009a', '\u0002', '\u0000', '\u0002', '\u000a', '\u00CC', '\u00c6'];
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Theory]
    [InlineData('\u0000', 0x00, 0x00)]
    [InlineData('\u0001', 0x01, 0x00)]
    [InlineData('\u0002', 0x02, 0x00)]
    [InlineData('\u0008', 0x80, 0x00)]
    [InlineData('\u0009', 0x00, 0x01)]
    [InlineData('\u0010', 0x00, 0x02)]
    [InlineData('\u0012', 0x00, 0x08)]
    [InlineData('\u0016', 0x00, 0x80)]
    public void GetIdBytes_ReturnsTheCorrectValue(char input, char expectedLow, char expectedHigh)
    {
        (char actualLow, char actualHigh) = _device.GetIdBytes(input);
        
        Assert.Equal(expectedLow, actualLow);
        Assert.Equal(expectedHigh, actualHigh);
    }
}