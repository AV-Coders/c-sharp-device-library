using AVCoders.Core;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusLightTest
{
    private readonly CBusLight _light;
    private readonly Mock<CBusInterface> _mockInterface;
    private readonly Mock<CommunicationClient> _mockClient = new("foo", "bar", (ushort)1);

    public CBusLightTest()
    {
        _mockInterface = new Mock<CBusInterface>(_mockClient.Object, true);
        _light = new CBusLight("Test Light", _mockInterface.Object, 3, CBusRampTime.Instant);
    }

    [Fact]
    public void PowerOnSendsTheCommand()
    {
        _light.PowerOn();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x79, 0x03, 0x47, 0x0d }));
    }

    [Fact]
    public void PowerOffSendsTheCommand()
    {
        _light.PowerOff();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x01, 0x03, 0xbf, 0x0d }));
    }

    [Theory]
    [InlineData(0, new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x02, 0x03, 0x00, 0xBE, 0x0d })]
    [InlineData(25, new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x02, 0x03, 0x3f, 0x7F, 0x0d })]
    [InlineData(50, new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x02, 0x03, 0x7F, 0x3F, 0x0d })]
    [InlineData(100, new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x02, 0x03, 0xFE, 0xC0, 0x0d })]
    public void SetLevel_SendsTheCommand(int level, byte[] expected)
    {
        _light.SetLevel(level);
        _mockClient.Verify(x => x.Send(expected));
    }
}