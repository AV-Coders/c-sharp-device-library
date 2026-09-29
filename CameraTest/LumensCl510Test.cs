using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Camera.Tests;

public class LumensCl510Test
{
    private readonly LumensCL510 _camera;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public LumensCl510Test()
    {
        _camera = new LumensCL510("Test Cam", _mockClient.Object, false, new Dictionary<int, string>());
    }

    [Fact]
    public void DeviceSendsResponses_IsFalse()
    {
        Assert.False(_camera.DeviceSendsResponses);
    }

    [Fact]
    public void DeviceSendsResponses_SetToTrue_StaysFalse()
    {
        _camera.DeviceSendsResponses = true;

        Assert.False(_camera.DeviceSendsResponses);
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        _camera.PowerOn();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0xB1, 0x01, 0x00, 0x00, 0xAF }), Times.Once);
        Assert.Equal(PowerState.On, _camera.DesiredPowerState);
        Assert.Equal(PowerState.On, _camera.PowerState);
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        _camera.PowerOff();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0xB1, 0x00, 0x00, 0x00, 0xAF }), Times.Once);
        Assert.Equal(PowerState.Off, _camera.DesiredPowerState);
        Assert.Equal(PowerState.Off, _camera.PowerState);
    }

    [Fact]
    public void ZoomIn_SendsZoomStartTele()
    {
        _camera.ZoomIn();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x11, 0x00, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public void ZoomOut_SendsZoomStartWide()
    {
        _camera.ZoomOut();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x11, 0x01, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public void ZoomStop_SendsZoomStop()
    {
        _camera.ZoomStop();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x10, 0x00, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public async Task ZoomStop_WithAutoFocusAfterZoom_TriggersAutoFocus()
    {
        var camera = new LumensCL510("Test Cam", _mockClient.Object, true, new Dictionary<int, string>());

        camera.ZoomStop();
        await Task.Delay(1500);

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0xA3, 0x01, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public async Task ZoomIn_AfterZoomStop_CancelsThePendingAutoFocus()
    {
        var camera = new LumensCL510("Test Cam", _mockClient.Object, true, new Dictionary<int, string>());

        camera.ZoomStop();
        camera.ZoomIn();
        await Task.Delay(1500);

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0xA3, 0x01, 0x00, 0x00, 0xAF }), Times.Never);
    }

    [Fact]
    public void OneTimeAutoFocus_SendsTheCommand()
    {
        _camera.OneTimeAutoFocus();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0xA3, 0x01, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public void SetAutoFocusOn_TriggersAOneTimeFocus()
    {
        _camera.SetAutoFocus(PowerState.On);

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0xA3, 0x01, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public void AutoTune_SendsTheCommand()
    {
        _camera.AutoTune();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x22, 0x00, 0x00, 0x00, 0xAF }), Times.Once);
    }

    [Fact]
    public void OneTimeAutoWhiteBalance_SendsTheCommand()
    {
        _camera.OneTimeAutoWhiteBalance();

        _mockClient.Verify(x => x.Send(new byte[] { 0xA0, 0x22, 0x01, 0x00, 0x00, 0xAF }), Times.Once);
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
