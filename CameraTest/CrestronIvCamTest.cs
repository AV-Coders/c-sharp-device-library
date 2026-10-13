using AVCoders.Core;

namespace AVCoders.Camera.Tests;

public class CrestronIvCamTest
{
    private readonly ConnectedClient _client = new();
    private readonly CrestronIvCam _camera;

    public CrestronIvCamTest()
    {
        _camera = new CrestronIvCam(_client, "Test Cam", new Dictionary<int, string> { { 2, "Lectern" } });
    }

    [Fact]
    public void PowerOn_SendsTheCommandWithoutAnIpHeader()
    {
        _camera.PowerOn();

        Assert.Equal([[0x81, 0x01, 0x04, 0x00, 0x02, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(99)]
    public void RecallPreset_Reserved_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        var recalled = new List<int>();
        _camera.OnPresetRecalled += recalled.Add;

        _camera.RecallPreset(preset);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be recalled");
        Assert.Equal(CameraBase.NoActivePreset, _camera.LastRecalledPreset);
        Assert.Empty(recalled);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(99)]
    public void SavePreset_Reserved_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _camera.SavePreset(preset);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be saved");
        Assert.DoesNotContain(_camera.Events, e => e.Type == EventType.Preset);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(99)]
    public void RecallPreset_ReservedWhenTheDeviceDoesNotSendResponses_SendsNothingAndKeepsTheLastPreset(int preset)
    {
        var client = new ConnectedClient();
        var camera = new CrestronIvCam(client, "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };
        camera.RecallPreset(2);

        camera.RecallPreset(preset);

        Assert.Equal([[0x81, 0x01, 0x04, 0x3F, 0x02, 0x02, 0xFF]], client.Sent);
        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be recalled");
        Assert.Equal(2, camera.LastRecalledPreset);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(99)]
    public void SavePreset_ReservedWhenTheDeviceDoesNotSendResponses_SendsNothing(int preset)
    {
        var client = new ConnectedClient();
        var camera = new CrestronIvCam(client, "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };

        camera.SavePreset(preset);

        Assert.Empty(client.Sent);
        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be saved");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(80)]
    public void RecallPreset_NotReserved_SendsTheRecall(int preset)
    {
        _camera.RecallPreset(preset);

        Assert.Equal([[0x81, 0x01, 0x04, 0x3F, 0x02, (byte)preset, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(80)]
    public void SavePreset_NotReserved_SendsTheSave(int preset)
    {
        _camera.SavePreset(preset);

        Assert.Equal([[0x81, 0x01, 0x04, 0x3F, 0x01, (byte)preset, 0xFF]], _client.Sent);
    }

    [Fact]
    public void MainStreamUrl_ByDefault_UsesPort554()
    {
        Assert.Equal("rtsp://host:554/1.h264", _camera.MainStreamUrl());
    }

    [Fact]
    public void MainStreamUrl_WithAnRtspPort_UsesThatPort()
    {
        var camera = new CrestronIvCam(new ConnectedClient(), "Test Cam", new Dictionary<int, string>(), rtspPort: 5000);

        Assert.Equal("rtsp://host:5000/1.h264", camera.MainStreamUrl());
    }
}

public class CrestronIvCamI12Test
{
    private readonly ConnectedClient _client = new();
    private readonly CrestronIvCamI12 _camera;

    public CrestronIvCamI12Test()
    {
        _camera = new CrestronIvCamI12(_client, "Test Cam", new Dictionary<int, string> { { 2, "Lectern" } });
    }

    [Theory]
    [InlineData(80)]
    [InlineData(81)]
    [InlineData(84)]
    [InlineData(85)]
    [InlineData(86)]
    [InlineData(95)]
    [InlineData(99)]
    public void RecallPreset_Reserved_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _camera.RecallPreset(preset);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be recalled");
        Assert.Equal(CameraBase.NoActivePreset, _camera.LastRecalledPreset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(81)]
    [InlineData(84)]
    [InlineData(85)]
    [InlineData(86)]
    [InlineData(95)]
    [InlineData(99)]
    public void SavePreset_Reserved_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _camera.SavePreset(preset);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be saved");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RecallPreset_HomeAndTrackingShots_SendsTheRecall(int preset)
    {
        _camera.RecallPreset(preset);

        Assert.Equal([[0x81, 0x01, 0x04, 0x3F, 0x02, (byte)preset, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(CameraTrackingMode.Auto, (byte)0x50)]
    [InlineData(CameraTrackingMode.Disabled, (byte)0x51)]
    public void SetTracking_SendsTheTrackingPresetRecallOnly(CameraTrackingMode mode, byte preset)
    {
        _camera.SetTracking(mode);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal([[0x81, 0x01, 0x04, 0x3F, 0x02, preset, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetTracking_DoesNotReportAPresetRecall(bool deviceSendsResponses)
    {
        var client = new ConnectedClient();
        var camera = new CrestronIvCamI12(client, "Test Cam", new Dictionary<int, string>())
            { DeviceSendsResponses = deviceSendsResponses };
        var recalled = new List<int>();
        camera.OnPresetRecalled += recalled.Add;

        camera.SetTracking(CameraTrackingMode.Auto);
        client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);
        camera.SetTracking(CameraTrackingMode.Disabled);
        client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal(CameraBase.NoActivePreset, camera.LastRecalledPreset);
        Assert.Empty(recalled);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Preset);
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
        var camera = new CrestronIvCamI12(new ConnectedClient(), "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };

        camera.SetTracking(CameraTrackingMode.Disabled);

        Assert.Equal(CameraTrackingMode.Disabled, camera.TrackingMode);
    }

    [Fact]
    public void SetTracking_ErrorInPlaceOfTheAck_LeavesTheModeUnchanged()
    {
        _camera.SetTracking(CameraTrackingMode.Auto);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x60, 0x03, 0xFF]);

        Assert.Equal(CameraTrackingMode.Unknown, _camera.TrackingMode);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to start tracking: Command buffer full");
    }

    [Fact]
    public async Task SetTracking_ErrorAfterTheAck_KeepsTheModeUntilThePollReply()
    {
        _camera.SetTracking(CameraTrackingMode.Auto);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);
        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);

        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(CameraTrackingMode.Disabled, _camera.TrackingMode);
    }

    [Theory]
    [InlineData(CameraTrackingMode.Manual)]
    [InlineData(CameraTrackingMode.TriggerOnce)]
    [InlineData(CameraTrackingMode.Unknown)]
    public async Task SetTracking_UnsupportedMode_SendsNothingAndLeavesTheMode(CameraTrackingMode mode)
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x02, 0xFF]);
        _client.Sent.Clear();

        _camera.SetTracking(mode);

        Assert.Empty(_client.Sent);
        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }

    [Fact]
    public async Task Poll_SendsThePowerInquiryThenTheTrackingInquiry()
    {
        await SonyViscaPolling.InvokePoll(_camera);

        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x09, 0x08, 0x01, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x03, 0xFF }, PowerState.On, CameraTrackingMode.Disabled)]
    [InlineData(new byte[] { 0x90, 0x50, 0x03, 0xFF, 0x90, 0x50, 0x02, 0xFF }, PowerState.Off, CameraTrackingMode.Auto)]
    public async Task Poll_Replies_SetThePowerStateAndTheTrackingMode(byte[] replies, PowerState powerState, CameraTrackingMode trackingMode)
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke(replies);

        Assert.Equal(powerState, _camera.PowerState);
        Assert.Equal(trackingMode, _camera.TrackingMode);
    }

    [Fact]
    public async Task Poll_UnknownTrackingReply_LeavesTheMode()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x02, 0xFF]);

        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x04, 0xFF]);

        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }

    [Fact]
    public async Task PanTiltStopAndRecallPreset_LeaveTheTrackingMode()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x02, 0xFF]);

        _camera.PanTiltStop();
        _camera.RecallPreset(2);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal(2, _camera.LastRecalledPreset);
        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }

    [Fact]
    public void SupportedTrackingModes_AreAutoAndDisabled()
    {
        Assert.Equal([CameraTrackingMode.Auto, CameraTrackingMode.Disabled], _camera.SupportedTrackingModes);
    }
}
