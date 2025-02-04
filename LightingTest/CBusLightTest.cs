using AVCoders.Core;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusLightTest
{
    private readonly CBusLight _light;
    private readonly Mock<CBusInterface> _mockInterface;
    private readonly Mock<CommunicationClient> _mockClient;

    public CBusLightTest()
    {
        _mockClient = new Mock<CommunicationClient>("foo");
        _mockInterface = new Mock<CBusInterface>(_mockClient.Object, true);
        _light = new CBusLight(_mockInterface.Object, 0x10, CBusRampTime.Instant);
    }

    [Fact]
    public void PowerOnSendsTheCommand()
    {
        _light.PowerOn();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x79, 0x10, 0x3a, 0x0d }));
    }

    [Fact]
    public void PowerOffSendsTheCommand()
    {
        _light.PowerOff();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x5c, 0x05, 0x38, 0x00, 0x01, 0x10, 0xb2, 0x0d }));
    }
}