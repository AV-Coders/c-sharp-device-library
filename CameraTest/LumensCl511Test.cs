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
    public void DeviceSendsResponses_SetToTrue_StaysFalse()
    {
        _camera.DeviceSendsResponses = true;

        Assert.False(_camera.DeviceSendsResponses);
    }

    [Fact]
    public void RecallPreset_AfterAttemptingToEnableResponses_StillReportsTheRecall()
    {
        var recalled = new List<int>();
        _camera.OnPresetRecalled += p => recalled.Add(p);
        _camera.DeviceSendsResponses = true;

        _camera.RecallPreset(2);

        Assert.Equal(2, _camera.LastRecalledPreset);
        Assert.Equal([2], recalled);
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        byte[] expectedCommand = [0xA0, 0xB1, 0x01, 0x00, 0x00, 0xAF];
        _camera.PowerOn();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
        Assert.Equal(PowerState.On, _camera.DesiredPowerState);
        Assert.Equal(PowerState.On, _camera.PowerState);
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        byte[] expectedCommand = [0xA0, 0xB1, 0x00, 0x00, 0x00, 0xAF];
        _camera.PowerOff();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
        Assert.Equal(PowerState.Off, _camera.DesiredPowerState);
        Assert.Equal(PowerState.Off, _camera.PowerState);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(7, 0x08)]
    public void RecallPreset_SendsTheOneBasedPresetIndex(int preset, byte expectedIndex)
    {
        _camera.RecallPreset(preset);

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x03, 0x00, 0x00, expectedIndex, 0xAF }), Times.Once);
        Assert.Equal(preset, _camera.LastRecalledPreset);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(7, 0x08)]
    public void SavePreset_SendsTheOneBasedPresetIndex(int preset, byte expectedIndex)
    {
        _camera.SavePreset(preset);

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x03, 0x00, 0x01, expectedIndex, 0xAF }), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void RecallPreset_OutOfRange_SendsNothing(int preset)
    {
        _camera.RecallPreset(2);
        _mockClient.Invocations.Clear();

        _camera.RecallPreset(preset);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        Assert.Equal(2, _camera.LastRecalledPreset);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is out of range, this module supports presets 0-7");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void SavePreset_OutOfRange_SendsNothing(int preset)
    {
        _camera.SavePreset(preset);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }
}
