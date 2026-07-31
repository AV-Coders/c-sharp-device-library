using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Camera.Tests;

public class LumensCl511Test
{
    private readonly LumensCL511 _camera;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public LumensCl511Test()
    {
        _camera = new LumensCL511("Test Cam", _mockClient.Object, false, new Dictionary<int, string>());
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        byte[] expectedCommand = [0xA0, 0xB1, 0x01, 0x00, 0x00, 0xAF];
        _camera.PowerOn();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_SetsTheDesiredAndActualPowerState()
    {
        _camera.PowerOn();

        Assert.Equal(PowerState.On, _camera.DesiredPowerState);
        Assert.Equal(PowerState.On, _camera.PowerState);
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        byte[] expectedCommand = [0xA0, 0xB1, 0x00, 0x00, 0x00, 0xAF];
        _camera.PowerOff();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SetsTheDesiredAndActualPowerState()
    {
        _camera.PowerOff();

        Assert.Equal(PowerState.Off, _camera.DesiredPowerState);
        Assert.Equal(PowerState.Off, _camera.PowerState);
    }
}
