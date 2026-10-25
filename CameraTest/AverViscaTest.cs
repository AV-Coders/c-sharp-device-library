using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Camera.Tests;

public class AverViscaTest
{
    private readonly ConnectedClient _client = new();
    private readonly AverVisca _camera;

    public AverViscaTest()
    {
        _camera = new AverVisca(_client, "Test Cam", new Dictionary<int, string>());
    }

    [Theory]
    [InlineData(CameraTrackingMode.Auto, new byte[] { 0x81, 0x01, 0x04, 0x7D, 0x02, 0x00, 0xFF })]
    [InlineData(CameraTrackingMode.Disabled, new byte[] { 0x81, 0x01, 0x04, 0x7D, 0x01, 0x00, 0xFF })]
    [InlineData(CameraTrackingMode.TriggerOnce, new byte[] { 0x81, 0x01, 0x04, 0x7D, 0x00, 0x00, 0xFF })]
    [InlineData(CameraTrackingMode.Manual, new byte[] { 0x81, 0x01, 0x04, 0x7D, 0x03, 0x00, 0xFF })]
    public void SetTracking_SendsTheTrackingCommand(CameraTrackingMode mode, byte[] expectedCommand)
    {
        _camera.SetTracking(mode);

        Assert.Equal([expectedCommand], _client.Sent);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x41, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x51, 0xFF })]
    public void SetTracking_WhenTheDeviceSendsResponses_ChangesTheModeOnTheReply(byte[] reply)
    {
        _camera.SetTracking(CameraTrackingMode.Auto);
        Assert.Equal(CameraTrackingMode.Unknown, _camera.TrackingMode);

        _client.ResponseByteHandlers!.Invoke(reply);

        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }

    [Fact]
    public void SetTracking_WhenTheDeviceDoesNotSendResponses_ChangesTheModeOnTheSend()
    {
        _camera.DeviceSendsResponses = false;

        _camera.SetTracking(CameraTrackingMode.Manual);

        Assert.Equal(CameraTrackingMode.Manual, _camera.TrackingMode);
    }

    [Fact]
    public void SetTracking_ErrorInPlaceOfTheAck_LeavesTheModeUnchanged()
    {
        _camera.SetTracking(CameraTrackingMode.Auto);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x60, 0x03, 0xFF]);

        Assert.Equal(CameraTrackingMode.Unknown, _camera.TrackingMode);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to set tracking to Auto: Command buffer full");
    }

    [Fact]
    public void SetTracking_WhenTheSendFails_LeavesTheMode()
    {
        var client = TestFactory.CreateCommunicationClient();
        client.Setup(x => x.Send(It.IsAny<byte[]>())).Throws(new IOException("Port closed"));
        var camera = new AverVisca(client.Object, "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };

        camera.SetTracking(CameraTrackingMode.Auto);

        Assert.Equal(CameraTrackingMode.Unknown, camera.TrackingMode);
    }

    [Fact]
    public void SetTracking_Unknown_SendsNothingAndLeavesTheMode()
    {
        _camera.DeviceSendsResponses = false;
        _camera.SetTracking(CameraTrackingMode.Auto);
        _client.Sent.Clear();

        _camera.SetTracking(CameraTrackingMode.Unknown);

        Assert.Empty(_client.Sent);
        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }
}
