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
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x00, 0x00, 0x00, 0xFF }, 0x60)]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x01, 0x00, 0x00, 0xFF }, 0x5F)]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x02, 0x00, 0x00, 0xFF }, 0x5E)]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x03, 0x00, 0x00, 0xFF }, 0x5D)]
    public void CalcualteChecksum_ReturnsTheChecksum(byte[] input, byte expected)
    {
        byte actual = DyNet.CalculateChecksum(input);
        
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(33, 1, 100, new byte[] { 0x1C, 0x21, 0x64, 0x00, 0x00, 0x00, 0xFF, 0x60 })]
    [InlineData(33, 2, 100, new byte[] { 0x1C, 0x21, 0x64, 0x01, 0x00, 0x00, 0xFF, 0x5F })]
    [InlineData(33, 3, 100, new byte[] { 0x1C, 0x21, 0x64, 0x02, 0x00, 0x00, 0xFF, 0x5E })]
    [InlineData(33, 4, 100, new byte[] { 0x1C, 0x21, 0x64, 0x03, 0x00, 0x00, 0xFF, 0x5D })]
    public void SelectCurrentPreset_SendsTheExpectedCommand(byte area, byte preset, byte rampTime, byte[] expectedCommand)
    {
        _dyNet.SelectCurrentPreset(area, preset, rampTime);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}