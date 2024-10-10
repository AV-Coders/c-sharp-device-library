using AVCoders.Core;
using Moq;

namespace AVCoders.Lighting.Tests;

public class DyNetTest
{
    private readonly DyNet _dyNet;
    private readonly Mock<TcpClient> _mockClient;

    public DyNetTest()
    {
        _mockClient = new("foo", (ushort)1);
        _dyNet = new DyNet(_mockClient.Object);
    }
    
    [Theory]
    [InlineData(new byte[] { 0x1c, 0x01, 0x20, 0x00, 0x00, 0x00, 0xFF }, 0xc4)]
    public void CalcualteChecksum_ReturnsTheChecksum(byte[] input, byte expected)
    {
        byte actual = DyNet.CalculateChecksum(input);
        
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(33, 103, 100, new byte[] { 0x1c, 0x21, 0x00, 0x67, 0x00, 0x64, 0xff, 0xf9 })]
    public void RecallPreset_SendsTheExpectedCommand(byte area, byte preset, byte rampTime, byte[] expectedCommand)
    {
        _dyNet.RecallPreset(area, preset, rampTime);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}