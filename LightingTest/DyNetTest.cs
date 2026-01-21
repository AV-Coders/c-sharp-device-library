using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Lighting.Tests;

public class DyNetTest
{
    private readonly DyNet _dyNet;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public DyNetTest()
    {
        _dyNet = new DyNet(_mockClient.Object, "name");
    }
    
    [Theory]
    [InlineData(new byte[] { 0x1c, 0x01, 0x20, 0x00, 0x00, 0x00, 0xFF }, 0xc4)]
    public void CalcualteChecksum_ReturnsTheChecksum(byte[] input, byte expected)
    {
        byte actual = DyNet.CalculateChecksum(input);
        
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(27, 1, 100, new byte[] { 0x1C ,0x1B ,0x64 ,0x00 ,0x00 ,0x00 ,0xFF ,0x66 })]
    [InlineData(27, 2, 100, new byte [] {0x1C ,0x1B ,0x64 ,0x01 ,0x00 ,0x00 ,0xFF ,0x65 })]
    [InlineData(27, 3, 100, new byte [] {0x1C ,0x1B ,0x64 ,0x02 ,0x00 ,0x00 ,0xFF ,0x64 })]
    [InlineData(27, 5, 100, new byte [] {0x1C ,0x1B ,0x64 ,0x0A ,0x00 ,0x00 ,0xFF ,0x5C })]
    [InlineData(27, 9, 100, new byte [] {0x1C ,0x1B ,0x64 ,0x00 ,0x00 ,0x01 ,0xFF ,0x65 })]
    [InlineData(1, 4, 2, new byte [] {0x1C ,0x01 ,0x20 ,0x03 ,0x00 ,0x00 ,0xFF ,0xC1 })]
    public void RecallPreset_SendsTheExpectedCommand(byte area, byte preset, byte rampTime, byte[] expectedCommand)
    {
        _dyNet.RecallPresetInBank(area, preset, rampTime);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}