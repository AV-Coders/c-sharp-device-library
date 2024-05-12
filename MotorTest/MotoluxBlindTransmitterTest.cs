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
        _device = new MotoluxBlindTransmitter("Blind", 0x00, 0x01, RelayAction.Lower, 30, _mockClient.Object);
    }

    [Fact]
    public void Lower_SendsTheCommand()
    {
        _device.Lower();

        byte[] expectedPayload = { 0x9a, 0x00, 0x01, 0x00, 0x0a, 0xEE, 0xE5 };
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Fact]
    public void Raise_SendsTheCommand()
    {
        _device.Raise();

        byte[] expectedPayload = { 0x9a, 0x00, 0x01, 0x00, 0x0a, 0xDD, 0xD6 };
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Fact]
    public void Stop_SendsTheCommand()
    {
        _device.Stop();

        byte[] expectedPayload = { 0x9a, 0x00, 0x01, 0x00, 0x0a, 0xCC, 0xC7 };
        
        _mockClient.Verify(x => x.Send(expectedPayload));
    }

    [Theory]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(1, 0x01, 0x00)]
    [InlineData(2, 0x02, 0x00)]
    [InlineData(10, 0x00, 0x02)]
    [InlineData(12, 0x00, 0x08)]
    [InlineData(16, 0x00, 0x80)]
    public void GetIdBytes_ReturnsTheCorrectValue(byte input, byte expectedLow, byte expectedHigh)
    {
        (byte actualLow, byte actualHigh) = _device.GetIdBytes(input);
        
        Assert.Equal(expectedLow, actualLow);
        Assert.Equal(expectedHigh, actualHigh);
    }
}