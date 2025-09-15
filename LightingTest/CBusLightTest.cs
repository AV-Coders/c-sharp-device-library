using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusLightTest
{
    private readonly CBusLight _light;
    private readonly Mock<CBusSerialInterface> _mockInterface;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public CBusLightTest()
    {
        _mockInterface = new Mock<CBusSerialInterface>(_mockClient.Object, true);
        _light = new CBusLight("Test Light", _mockInterface.Object, 3, CBusRampTime.Instant);
    }

    [Fact]
    public void PowerOnSendsTheCommand()
    {
        _light.PowerOn();
        
        _mockClient.Verify(x => x.Send("\\053800790347\r"));
    }

    [Fact]
    public void PowerOffSendsTheCommand()
    {
        _light.PowerOff();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x01, 0x03, 0xbf, 0x0d }));
    }

    [Theory]
    [InlineData(0, "\\053800020300BE\r")]
    [InlineData(25, "\\05380002033F7F\r")]
    [InlineData(50, "\\05380002037F3F\r")]
    [InlineData(100, "\\0538000203FEC0\r")]
    public void SetLevel_SendsTheCommand(int level, string expected)
    {
        _light.SetLevel(level);
        _mockClient.Verify(x => x.Send(expected));
    }
}