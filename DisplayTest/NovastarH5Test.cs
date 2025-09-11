using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Display.Tests;

public class NovastarH5Test
{
    private readonly NovaStarH5 _display;
    private readonly Mock<UdpClient> _mockClient = TestFactory.CreateUdpClient();
    
    public NovastarH5Test()
    {
        _display = new NovaStarH5(_mockClient.Object, 0, [0], "Test display", 0, 1);
    }
    
    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        string expectedPowerOnCommand = "[{\"cmd\":\"W0605\",\"deviceId\":0,\"screenId\":0,\"presetId\":0}]";
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        string expectedPowerOnCommand = "[{\"cmd\":\"W0605\",\"deviceId\":0,\"screenId\":0,\"presetId\":1}]";
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }
}