using System.Reflection;
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
    public void DoRecallPreset_Reserved_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _camera.DoRecallPreset(preset);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be recalled");
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
    public void DoRecallPreset_Reserved_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _camera.DoRecallPreset(preset);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is reserved and can't be recalled");
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
    public async Task Poll_SendsEachInquiryOnlyAfterThePreviousOneIsAnswered()
    {
        byte[][] inquiries =
        [
            [0x81, 0x09, 0x04, 0x00, 0xFF],
            [0x81, 0x09, 0x08, 0x01, 0xFF],
            [0x81, 0xC2, 0x09, 0x08, 0xFF],
            [0x81, 0xC2, 0x09, 0x09, 0x02, 0xFF],
            [0x81, 0xC2, 0x09, 0x09, 0x03, 0xFF],
            [0x81, 0xC2, 0x09, 0x09, 0x04, 0xFF],
            [0x81, 0xC2, 0x09, 0x09, 0x05, 0xFF],
            SonyViscaPolling.VersionInquiry
        ];
        byte[][] replies =
        [
            [0x90, 0x50, 0x02, 0xFF],
            [0x90, 0x50, 0x02, 0xFF],
            [0x90, 0x50, 0x01, 0x01, 0xFF],
            [0x90, 0x50, 0x02, 0x00, 0x0A, 0x0F, 0x05, 0x03, 0x01, 0x05, 0x0C, 0xFF],
            [0x90, 0x50, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF],
            [0x90, 0x50, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF],
            [0x90, 0x50, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF]
        ];

        await SonyViscaPolling.InvokePoll(_camera);

        for (var answered = 0; answered < replies.Length; answered++)
        {
            Assert.Equal(inquiries[..(answered + 1)], _client.Sent);
            _client.ResponseByteHandlers!.Invoke(replies[answered]);
        }
        Assert.Equal(inquiries, _client.Sent);
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
        _camera.SetTracking(CameraTrackingMode.Auto);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);

        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x04, 0xFF]);

        Assert.Equal([0x81, 0x09, 0x08, 0x01, 0xFF], _client.Sent[2]);
        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }

    [Fact]
    public async Task PanTiltStopAndRecallPreset_LeaveTheTrackingMode()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x02, 0xFF]);

        _camera.PanTiltStop();
        _camera.RecallPreset(2);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF, 0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal(2, _camera.LastRecalledPreset);
        Assert.Equal(CameraTrackingMode.Auto, _camera.TrackingMode);
    }
}

public class CrestronIvCamI12MultiCamTest
{
    private static readonly byte[] Ack = [0x90, 0x41, 0xFF];
    private static readonly byte[] PowerAndTrackingReplies = [0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x02, 0xFF];
    private static readonly byte[] Companion2Reply = [0x90, 0x50, 0x02, 0x0C, 0x00, 0x0A, 0x08, 0x00, 0x01, 0x01, 0x04, 0xFF];
    private static readonly byte[] EmptyCompanion3Reply = [0x90, 0x50, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF];
    private static readonly byte[] ActiveCameraInquiry = [0x81, 0xC2, 0x09, 0x08, 0xFF];
    private static readonly byte[] Companion5Inquiry = [0x81, 0xC2, 0x09, 0x09, 0x05, 0xFF];
    private static readonly Dictionary<int, string?> UnreadCompanions = new() { [2] = null, [3] = null, [4] = null, [5] = null };
    private readonly ConnectedClient _client = new();
    private readonly CrestronIvCamI12 _camera;

    public CrestronIvCamI12MultiCamTest()
    {
        _camera = new CrestronIvCamI12(_client, "Test Cam", new Dictionary<int, string>());
    }

    private static Task InvokeLiveCameraCheck(CrestronIvCamI12 camera) =>
        (Task)typeof(CrestronIvCamI12).GetMethod("CheckLiveCamera", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(camera, [CancellationToken.None])!;

    private void TurnIntelligentSwitchingOn()
    {
        _camera.SetIntelligentSwitching(PowerState.On);
        _client.ResponseByteHandlers!.Invoke(Ack);
        lock (_client.Sent)
            _client.Sent.Clear();
    }

    [Fact]
    public void InitialState_IsUnknownWithUnreadCompanions()
    {
        Assert.Equal(CrestronIvCamI12.UnknownCamera, _camera.ActiveCamera);
        Assert.Equal(PowerState.Unknown, _camera.IntelligentSwitching);
        Assert.Equal(UnreadCompanions, _camera.CompanionIps);
    }

    [Fact]
    public void QueryStatus_SendsEveryPollInquiryAsEachIsAnswered()
    {
        _camera.QueryStatus();
        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF]], _client.Sent);

        _client.ResponseByteHandlers!.Invoke([
            ..PowerAndTrackingReplies,
            0x90, 0x50, 0x01, 0x01, 0xFF,
            ..Companion2Reply,
            ..EmptyCompanion3Reply,
            0x90, 0x50, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF
        ]);

        Assert.Equal(
        [
            [0x81, 0x09, 0x04, 0x00, 0xFF],
            [0x81, 0x09, 0x08, 0x01, 0xFF],
            ActiveCameraInquiry,
            [0x81, 0xC2, 0x09, 0x09, 0x02, 0xFF],
            [0x81, 0xC2, 0x09, 0x09, 0x03, 0xFF],
            [0x81, 0xC2, 0x09, 0x09, 0x04, 0xFF],
            Companion5Inquiry
        ], _client.Sent);
    }

    [Fact]
    public async Task QueryStatus_WhileTheLiveCheckAwaitsItsReply_PassesThatReplyToTheLiveCheck()
    {
        TurnIntelligentSwitchingOn();
        await InvokeLiveCameraCheck(_camera);

        _camera.QueryStatus();
        Assert.Equal([ActiveCameraInquiry], _client.Sent);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x01, 0x03, 0xFF, 0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(3, _camera.ActiveCamera);
        Assert.Equal(PowerState.Off, _camera.PowerState);
        Assert.Equal([ActiveCameraInquiry, [0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x09, 0x08, 0x01, 0xFF]], _client.Sent);
    }

    [Fact]
    public void QueryStatus_Replies_UpdateTheState()
    {
        _camera.QueryStatus();

        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, 0x90, 0x50, 0x01, 0x02, 0xFF, ..Companion2Reply]);

        Assert.Equal(PowerState.On, _camera.PowerState);
        Assert.Equal(PowerState.On, _camera.IntelligentSwitching);
        Assert.Equal(2, _camera.ActiveCamera);
        Assert.Equal("192.168.1.20", _camera.CompanionIps[2]);
    }

    [Fact]
    public void QueryStatus_WhenDisconnected_SendsNothing()
    {
        _client.SetConnectionState(ConnectionState.Disconnected);

        _camera.QueryStatus();

        Assert.Empty(_client.Sent);
    }

    [Fact]
    public void QueryStatus_WhenTheDeviceDoesNotSendResponses_SendsNothing()
    {
        var client = new ConnectedClient();
        var camera = new CrestronIvCamI12(client, "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };

        camera.QueryStatus();

        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_WhileIntelligentSwitchingIsOn_QueriesTheActiveCamera()
    {
        TurnIntelligentSwitchingOn();

        await InvokeLiveCameraCheck(_camera);

        Assert.Equal([ActiveCameraInquiry], _client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_WhileIntelligentSwitchingIsUnknown_SendsNothing()
    {
        await InvokeLiveCameraCheck(_camera);

        Assert.Empty(_client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_AfterIntelligentSwitchingTurnsOff_SendsNothing()
    {
        TurnIntelligentSwitchingOn();
        _camera.SetIntelligentSwitching(PowerState.Off);
        _client.ResponseByteHandlers!.Invoke(Ack);
        _client.Sent.Clear();

        await InvokeLiveCameraCheck(_camera);

        Assert.Empty(_client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_WhenDisconnected_SendsNothing()
    {
        TurnIntelligentSwitchingOn();
        _client.SetConnectionState(ConnectionState.Disconnected);

        await InvokeLiveCameraCheck(_camera);

        Assert.Empty(_client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_WhenTheDeviceDoesNotSendResponses_SendsNothing()
    {
        var client = new ConnectedClient();
        var camera = new CrestronIvCamI12(client, "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };
        camera.SetIntelligentSwitching(PowerState.On);
        client.Sent.Clear();

        await InvokeLiveCameraCheck(camera);

        Assert.Equal(PowerState.On, camera.IntelligentSwitching);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_Reply_SetsTheActiveCamera()
    {
        TurnIntelligentSwitchingOn();
        await InvokeLiveCameraCheck(_camera);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x01, 0x03, 0xFF]);

        Assert.Equal(3, _camera.ActiveCamera);
        Assert.Equal(PowerState.On, _camera.IntelligentSwitching);
    }

    [Fact]
    public async Task LiveCameraCheck_ReplyWithIntelligentSwitchingOff_StopsTheChecks()
    {
        TurnIntelligentSwitchingOn();
        await InvokeLiveCameraCheck(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x00, 0x02, 0xFF]);
        _client.Sent.Clear();

        await InvokeLiveCameraCheck(_camera);

        Assert.Equal(PowerState.Off, _camera.IntelligentSwitching);
        Assert.Empty(_client.Sent);
    }

    [Fact]
    public async Task LiveCameraCheck_WhileIntelligentSwitchingIsOn_RunsEverySecond()
    {
        TurnIntelligentSwitchingOn();

        await Task.Delay(2500);

        byte[][] sent;
        lock (_client.Sent)
            sent = _client.Sent.ToArray();
        Assert.Contains(ActiveCameraInquiry, sent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void SelectCamera_SendsTheOutputSelect(int camera)
    {
        _camera.SelectCamera(camera);

        Assert.Equal([[0x81, 0xC2, 0x01, 0x08, (byte)camera, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void SelectCamera_OutOfRange_SendsNothingAndAddsAnErrorEvent(int camera)
    {
        _camera.SelectCamera(camera);

        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events, e => e.Type == EventType.Error && e.Info == $"Camera {camera} can't be selected");
    }

    [Fact]
    public async Task SelectCamera_OnTheAck_SetsTheActiveCameraAndTurnsIntelligentSwitchingOff()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, 0x90, 0x50, 0x01, 0x01, 0xFF]);
        var activeCameras = new List<int>();
        var switchingStates = new List<PowerState>();
        _camera.OnActiveCameraChange += activeCameras.Add;
        _camera.OnIntelligentSwitchingChange += switchingStates.Add;

        _camera.SelectCamera(2);
        Assert.Equal(1, _camera.ActiveCamera);
        _client.ResponseByteHandlers!.Invoke(Ack);

        Assert.Equal(2, _camera.ActiveCamera);
        Assert.Equal(PowerState.Off, _camera.IntelligentSwitching);
        Assert.Equal([2], activeCameras);
        Assert.Equal([PowerState.Off], switchingStates);
    }

    [Fact]
    public void SelectCamera_WhenTheDeviceDoesNotSendResponses_SetsTheActiveCameraOnTheSend()
    {
        var camera = new CrestronIvCamI12(new ConnectedClient(), "Test Cam", new Dictionary<int, string>()) { DeviceSendsResponses = false };

        camera.SelectCamera(3);

        Assert.Equal(3, camera.ActiveCamera);
        Assert.Equal(PowerState.Off, camera.IntelligentSwitching);
    }

    [Theory]
    [InlineData(PowerState.On, (byte)0x08)]
    [InlineData(PowerState.Off, (byte)0x0B)]
    public void SetIntelligentSwitching_SendsTheCommand(PowerState state, byte function)
    {
        _camera.SetIntelligentSwitching(state);

        Assert.Equal([[0x81, 0xC2, 0x01, function, 0x00, 0xFF]], _client.Sent);
    }

    [Theory]
    [InlineData(PowerState.On)]
    [InlineData(PowerState.Off)]
    public void SetIntelligentSwitching_OnTheAck_SetsTheState(PowerState state)
    {
        var states = new List<PowerState>();
        _camera.OnIntelligentSwitchingChange += states.Add;

        _camera.SetIntelligentSwitching(state);
        Assert.Equal(PowerState.Unknown, _camera.IntelligentSwitching);
        _client.ResponseByteHandlers!.Invoke(Ack);

        Assert.Equal(state, _camera.IntelligentSwitching);
        Assert.Equal([state], states);
    }

    [Theory]
    [InlineData(PowerState.Unknown)]
    [InlineData(PowerState.Warming)]
    public void SetIntelligentSwitching_NeitherOnNorOff_SendsNothing(PowerState state)
    {
        _camera.SetIntelligentSwitching(state);

        Assert.Empty(_client.Sent);
        Assert.Equal(PowerState.Unknown, _camera.IntelligentSwitching);
    }

    [Fact]
    public void SetCompanionIp_SendsOneByteForEachHexDigitOfTheAddressAndReturnsTrue()
    {
        var accepted = _camera.SetCompanionIp(2, "192.168.1.20");

        Assert.True(accepted);
        Assert.Equal([[0x81, 0xC2, 0x01, 0x09, 0x02, 0x0C, 0x00, 0x0A, 0x08, 0x00, 0x01, 0x01, 0x04, 0xFF]], _client.Sent);
    }

    [Fact]
    public void SetCompanionIp_WhenTheSendFails_StillReturnsTrue()
    {
        var camera = new CrestronIvCamI12(new FailingClient(), "Test Cam", new Dictionary<int, string>());

        var accepted = camera.SetCompanionIp(2, "192.168.1.20");

        Assert.True(accepted);
        Assert.Equal(CommunicationState.Error, camera.CommunicationState);
        Assert.Null(camera.CompanionIps[2]);
    }

    [Theory]
    [InlineData(5, "100.64.255.1", new byte[] { 0x81, 0xC2, 0x01, 0x09, 0x05, 0x06, 0x04, 0x04, 0x00, 0x0F, 0x0F, 0x00, 0x01, 0xFF })]
    [InlineData(3, "8.8.8.8", new byte[] { 0x81, 0xC2, 0x01, 0x09, 0x03, 0x00, 0x08, 0x00, 0x08, 0x00, 0x08, 0x00, 0x08, 0xFF })]
    public void SetCompanionIp_AddressOutsideThePrivateRanges_IsSent(int camera, string ipAddress, byte[] expected)
    {
        var accepted = _camera.SetCompanionIp(camera, ipAddress);

        Assert.True(accepted);
        Assert.Equal([expected], _client.Sent);
    }

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("256.1.1.1")]
    [InlineData("192.168.001.020")]
    [InlineData(" 192.168.1.20")]
    [InlineData("fe80::1")]
    [InlineData("camera")]
    [InlineData("")]
    public void SetCompanionIp_NotAnIpv4Address_ReturnsFalseSendsNothingAndAddsAnErrorEvent(string ipAddress)
    {
        var accepted = _camera.SetCompanionIp(2, ipAddress);

        Assert.False(accepted);
        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events,
            e => e.Type == EventType.Error && e.Info == $"Companion camera 2 wasn't set, {ipAddress} isn't an IPv4 address");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void SetCompanionIp_NotACompanionCamera_ReturnsFalseSendsNothingAndAddsAnErrorEvent(int camera)
    {
        var accepted = _camera.SetCompanionIp(camera, "192.168.1.20");

        Assert.False(accepted);
        Assert.Empty(_client.Sent);
        Assert.Contains(_camera.Events, e => e.Type == EventType.Error && e.Info == $"Companion camera {camera} doesn't exist");
    }

    [Fact]
    public void SetCompanionIp_OnTheAck_SetsTheCompanionIp()
    {
        var changes = new List<(int, string?)>();
        _camera.OnCompanionIpChange += (camera, ip) => changes.Add((camera, ip));

        _camera.SetCompanionIp(4, "192.168.1.20");
        Assert.Null(_camera.CompanionIps[4]);
        _client.ResponseByteHandlers!.Invoke(Ack);

        Assert.Equal("192.168.1.20", _camera.CompanionIps[4]);
        Assert.Equal([(4, "192.168.1.20")], changes);
    }

    [Fact]
    public void ClearCompanionIps_SendsTheClear()
    {
        _camera.ClearCompanionIps();

        Assert.Equal([[0x81, 0xC2, 0x01, 0x0A, 0x00, 0xFF]], _client.Sent);
    }

    [Fact]
    public async Task ClearCompanionIps_OnTheAck_EmptiesEveryCompanionIncludingUnreadOnes()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, 0x90, 0x50, 0x00, 0x01, 0xFF, ..Companion2Reply]);
        var changes = new List<(int, string?)>();
        _camera.OnCompanionIpChange += (camera, ip) => changes.Add((camera, ip));

        _camera.ClearCompanionIps();
        _client.ResponseByteHandlers!.Invoke(Ack);

        Assert.Equal(new Dictionary<int, string?> { [2] = "", [3] = "", [4] = "", [5] = "" }, _camera.CompanionIps);
        Assert.Equal([(2, ""), (3, ""), (4, ""), (5, "")], changes);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x50, 0x01, 0x02, 0xFF }, PowerState.On, 2)]
    [InlineData(new byte[] { 0x90, 0x50, 0x00, 0x01, 0xFF }, PowerState.Off, 1)]
    [InlineData(new byte[] { 0x90, 0x50, 0x02, 0x05, 0xFF }, PowerState.Off, 5)]
    public async Task Poll_ActiveCameraReply_SetsIntelligentSwitchingAndTheActiveCamera(byte[] reply, PowerState switching, int activeCamera)
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, ..reply]);

        Assert.Equal(switching, _camera.IntelligentSwitching);
        Assert.Equal(activeCamera, _camera.ActiveCamera);
    }

    [Fact]
    public async Task Poll_CompanionReplies_SetTheCompanionIps()
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([
            ..PowerAndTrackingReplies,
            0x90, 0x50, 0x00, 0x01, 0xFF,
            ..Companion2Reply,
            0x90, 0x50, 0x03, 0x0A, 0x0C, 0x01, 0x00, 0x0F, 0x0F, 0x00, 0x00, 0xFF,
            0x90, 0x50, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF,
            0x90, 0x50, 0x05, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0xFF
        ]);

        Assert.Equal(new Dictionary<int, string?> { [2] = "192.168.1.20", [3] = "172.16.255.0", [4] = "", [5] = "10.0.0.9" },
            _camera.CompanionIps);
    }

    [Fact]
    public async Task Poll_CompanionsWithoutAReply_StayUnread()
    {
        var changes = new List<(int, string?)>();
        _camera.OnCompanionIpChange += (camera, ip) => changes.Add((camera, ip));
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, 0x90, 0x50, 0x00, 0x01, 0xFF, ..Companion2Reply]);

        Assert.Equal(new Dictionary<int, string?> { [2] = "192.168.1.20", [3] = null, [4] = null, [5] = null }, _camera.CompanionIps);
        Assert.Equal([(2, "192.168.1.20")], changes);
    }

    [Fact]
    public async Task Poll_EmptyCompanionReply_ChangesUnreadToEmpty()
    {
        var changes = new List<(int, string?)>();
        _camera.OnCompanionIpChange += (camera, ip) => changes.Add((camera, ip));
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([
            ..PowerAndTrackingReplies,
            0x90, 0x50, 0x00, 0x01, 0xFF,
            0x90, 0x50, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF
        ]);

        Assert.Equal("", _camera.CompanionIps[2]);
        Assert.Equal([(2, "")], changes);
    }

    [Fact]
    public async Task Poll_CompanionReplySplitAcrossTwoReads_IsReassembled()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, 0x90, 0x50, 0x00, 0x01, 0xFF]);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0x0C, 0x00]);
        _client.ResponseByteHandlers!.Invoke([0x0A, 0x08, 0x00, 0x01, 0x01, 0x04, 0xFF]);

        Assert.Equal("192.168.1.20", _camera.CompanionIps[2]);
        Assert.DoesNotContain(_camera.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task Poll_ActiveCameraAndCompanionRepliesInOneRead_AreBothHandled()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke(PowerAndTrackingReplies);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x01, 0x03, 0xFF, ..Companion2Reply]);

        Assert.Equal(PowerState.On, _camera.IntelligentSwitching);
        Assert.Equal(3, _camera.ActiveCamera);
        Assert.Equal("192.168.1.20", _camera.CompanionIps[2]);
    }

    [Fact]
    public async Task Poll_CompanionReplyForAnotherCamera_IsIgnored()
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([
            ..PowerAndTrackingReplies,
            0x90, 0x50, 0x00, 0x01, 0xFF,
            0x90, 0x50, 0x03, 0x0C, 0x00, 0x0A, 0x08, 0x00, 0x01, 0x01, 0x04, 0xFF
        ]);

        Assert.Equal(UnreadCompanions, _camera.CompanionIps);
    }

    [Fact]
    public async Task Poll_ShortReplyInPlaceOfTheActiveCameraReply_IsIgnored()
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, 0x90, 0x50, 0x01, 0xFF]);

        Assert.Equal(PowerState.Unknown, _camera.IntelligentSwitching);
        Assert.Equal(CrestronIvCamI12.UnknownCamera, _camera.ActiveCamera);
    }

    [Fact]
    public async Task Poll_CompanionReplyInPlaceOfTheActiveCameraReply_IsIgnored()
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([..PowerAndTrackingReplies, ..Companion2Reply]);

        Assert.Equal(PowerState.Unknown, _camera.IntelligentSwitching);
        Assert.Equal(CrestronIvCamI12.UnknownCamera, _camera.ActiveCamera);
    }

    [Fact]
    public async Task CameraAddressTwo_SendsHeader82AndReadsRepliesFromAddressA0()
    {
        var client = new ConnectedClient();
        var camera = new CrestronIvCamI12(client, "Test Cam", new Dictionary<int, string>(), cameraId: 2);

        camera.SelectCamera(2);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([
            0xA0, 0x41, 0xFF,
            0xA0, 0x50, 0x02, 0xFF, 0xA0, 0x50, 0x02, 0xFF,
            0xA0, 0x50, 0x01, 0x04, 0xFF,
            0xA0, 0x50, 0x02, 0x0C, 0x00, 0x0A, 0x08, 0x00, 0x01, 0x01, 0x04, 0xFF
        ]);

        Assert.Equal([0x82, 0xC2, 0x01, 0x08, 0x02, 0xFF], client.Sent[0]);
        Assert.Equal([0x82, 0xC2, 0x09, 0x08, 0xFF], client.Sent[3]);
        Assert.Equal(4, camera.ActiveCamera);
        Assert.Equal(PowerState.On, camera.IntelligentSwitching);
        Assert.Equal("192.168.1.20", camera.CompanionIps[2]);
    }

    private class FailingClient : ConnectedClient
    {
        public override void Send(byte[] bytes) => throw new IOException("Socket closed");
    }
}
