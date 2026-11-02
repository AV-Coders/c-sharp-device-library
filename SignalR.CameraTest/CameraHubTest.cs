using AVCoders.Camera;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.SignalR.Camera.Tests;

public class CameraHubTest
{
    private readonly TestCamera _camera;
    private readonly CameraManager _manager;
    private readonly string _groupName;
    private readonly CameraHubTestHarness _harness;

    public CameraHubTest()
    {
        _groupName = $"hub-cam-{Guid.NewGuid()}";
        _camera = new TestCamera(_groupName);
        _manager = new CameraManager(_camera);
        CameraHub.RegisterCameraManager(_groupName, _manager);
        _harness = CameraHubTestHarness.CreateHub();
    }

    public static TheoryData<string, Action<CameraHub, string>, Func<TrackingTestCamera, bool>> Commands => new()
    {
        { "RecallPreset", (hub, group) => hub.RecallPreset(group, 4), camera => camera.DoRecallPresetCallCount == 1 && camera.LastDoRecallPresetArg == 4 },
        { "SavePreset", (hub, group) => hub.SavePreset(group, 6), camera => camera.SavePresetCallCount == 1 && camera.LastSavePresetArg == 6 },
        { "ZoomStop", (hub, group) => hub.ZoomStop(group), camera => camera.DoZoomStopCallCount == 1 },
        { "ZoomIn", (hub, group) => hub.ZoomIn(group), camera => camera.ZoomInCallCount == 1 },
        { "ZoomOut", (hub, group) => hub.ZoomOut(group), camera => camera.ZoomOutCallCount == 1 },
        { "PanTiltStop", (hub, group) => hub.PanTiltStop(group), camera => camera.DoPanTiltStopCallCount == 1 },
        { "PanTiltUp", (hub, group) => hub.PanTiltUp(group), camera => camera.PanTiltUpCallCount == 1 },
        { "PanTiltDown", (hub, group) => hub.PanTiltDown(group), camera => camera.PanTiltDownCallCount == 1 },
        { "PanTiltLeft", (hub, group) => hub.PanTiltLeft(group), camera => camera.PanTiltLeftCallCount == 1 },
        { "PanTiltRight", (hub, group) => hub.PanTiltRight(group), camera => camera.PanTiltRightCallCount == 1 },
        { "SetAutoFocus On", (hub, group) => hub.SetAutoFocus(group, PowerState.On), camera => camera.LastSetAutoFocus == PowerState.On },
        { "SetAutoFocus Off", (hub, group) => hub.SetAutoFocus(group, PowerState.Off), camera => camera.LastSetAutoFocus == PowerState.Off },
        { "SetTracking", (hub, group) => hub.SetTracking(group, CameraTrackingMode.Auto), camera => camera.SetTrackingCalls is [CameraTrackingMode.Auto] },
    };

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsState()
    {
        _camera.SetPowerStateForTest(PowerState.On);

        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPresetDefinitionChanged(_manager.PresetDefinitions()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPresetCleared(), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_SendsActivePresetWhenSet()
    {
        _camera.SetLastRecalledPresetForTest(5);

        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.OnPresetRecalled(5), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPresetCleared(), Times.Never);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_DoesNotSendCameraState()
    {
        await _harness.Hub.JoinGroup($"missing-{Guid.NewGuid()}");

        _harness.CallerMock.Verify(c => c.OnPresetDefinitionChanged(It.IsAny<Dictionary<int, string>>()), Times.Never);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(It.IsAny<PowerState>()), Times.Never);
    }

    [Fact]
    public async Task JoinGroup_SendsTrackingCapabilityFalseForNonTrackingCamera()
    {
        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.OnTrackingCapabilityChanged(false), Times.Once);
        _harness.CallerMock.Verify(c => c.OnTrackingModeChanged(It.IsAny<CameraTrackingMode>()), Times.Never);
    }

    [Fact]
    public async Task JoinGroup_SendsTrackingCapabilityAndModeForTrackingCamera()
    {
        var groupName = $"hub-tcam-{Guid.NewGuid()}";
        var tracking = new TrackingTestCamera(groupName);
        var manager = new CameraManager(tracking);
        CameraHub.RegisterCameraManager(groupName, manager);

        await _harness.Hub.JoinGroup(groupName);

        _harness.CallerMock.Verify(c => c.OnTrackingCapabilityChanged(true), Times.Once);
        _harness.CallerMock.Verify(c => c.OnTrackingModeChanged(CameraTrackingMode.Unknown), Times.Once);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_ReachesTheCamera(string command, Action<CameraHub, string> send, Func<TrackingTestCamera, bool> received)
    {
        var groupName = $"hub-tcam-{Guid.NewGuid()}";
        var camera = new TrackingTestCamera(groupName);
        CameraHub.RegisterCameraManager(groupName, new CameraManager(camera));

        send(_harness.Hub, groupName);

        await Wait.UntilAsync(() => received(camera), $"{command} did not reach the camera");
    }

    [Fact]
    public void RecallPreset_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.RecallPreset($"missing-{Guid.NewGuid()}", 1);

        Assert.Equal(0, _camera.DoRecallPresetCallCount);
    }
}
