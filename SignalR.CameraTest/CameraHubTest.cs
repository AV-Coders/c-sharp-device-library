using AVCoders.Camera;
using AVCoders.Core;

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

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

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
    public void RecallPreset_ForwardsToCamera()
    {
        _harness.Hub.RecallPreset(_groupName, 4);

        WaitFor(() => _camera.DoRecallPresetCallCount == 1);
        Assert.Equal(1, _camera.DoRecallPresetCallCount);
        Assert.Equal(4, _camera.LastDoRecallPresetArg);
    }

    [Fact]
    public void RecallPreset_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.RecallPreset($"missing-{Guid.NewGuid()}", 1);

        Assert.Equal(0, _camera.DoRecallPresetCallCount);
    }

    [Fact]
    public void SavePreset_ForwardsToCamera()
    {
        _harness.Hub.SavePreset(_groupName, 6);

        WaitFor(() => _camera.SavePresetCallCount == 1);
        Assert.Equal(1, _camera.SavePresetCallCount);
        Assert.Equal(6, _camera.LastSavePresetArg);
    }

    [Fact]
    public void SavePreset_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.SavePreset($"missing-{Guid.NewGuid()}", 1);

        Assert.Equal(0, _camera.SavePresetCallCount);
    }

    [Fact]
    public void ZoomStop_ForwardsToCamera()
    {
        _harness.Hub.ZoomStop(_groupName);

        WaitFor(() => _camera.DoZoomStopCallCount == 1);
        Assert.Equal(1, _camera.DoZoomStopCallCount);
    }

    [Fact]
    public void ZoomIn_ForwardsToCamera()
    {
        _harness.Hub.ZoomIn(_groupName);

        WaitFor(() => _camera.ZoomInCallCount == 1);
        Assert.Equal(1, _camera.ZoomInCallCount);
    }

    [Fact]
    public void ZoomOut_ForwardsToCamera()
    {
        _harness.Hub.ZoomOut(_groupName);

        WaitFor(() => _camera.ZoomOutCallCount == 1);
        Assert.Equal(1, _camera.ZoomOutCallCount);
    }

    [Fact]
    public void PanTiltStop_ForwardsToCamera()
    {
        _harness.Hub.PanTiltStop(_groupName);

        WaitFor(() => _camera.DoPanTiltStopCallCount == 1);
        Assert.Equal(1, _camera.DoPanTiltStopCallCount);
    }

    [Fact]
    public void PanTiltUp_ForwardsToCamera()
    {
        _harness.Hub.PanTiltUp(_groupName);

        WaitFor(() => _camera.PanTiltUpCallCount == 1);
        Assert.Equal(1, _camera.PanTiltUpCallCount);
    }

    [Fact]
    public void PanTiltDown_ForwardsToCamera()
    {
        _harness.Hub.PanTiltDown(_groupName);

        WaitFor(() => _camera.PanTiltDownCallCount == 1);
        Assert.Equal(1, _camera.PanTiltDownCallCount);
    }

    [Fact]
    public void PanTiltLeft_ForwardsToCamera()
    {
        _harness.Hub.PanTiltLeft(_groupName);

        WaitFor(() => _camera.PanTiltLeftCallCount == 1);
        Assert.Equal(1, _camera.PanTiltLeftCallCount);
    }

    [Fact]
    public void PanTiltRight_ForwardsToCamera()
    {
        _harness.Hub.PanTiltRight(_groupName);

        WaitFor(() => _camera.PanTiltRightCallCount == 1);
        Assert.Equal(1, _camera.PanTiltRightCallCount);
    }

    [Theory]
    [InlineData(PowerState.On)]
    [InlineData(PowerState.Off)]
    public void SetAutoFocus_ForwardsToCamera(PowerState state)
    {
        _harness.Hub.SetAutoFocus(_groupName, state);

        WaitFor(() => _camera.LastSetAutoFocus == state);
        Assert.Equal(state, _camera.LastSetAutoFocus);
    }

    [Fact]
    public void SetAutoFocus_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.SetAutoFocus($"missing-{Guid.NewGuid()}", PowerState.On);

        Assert.Null(_camera.LastSetAutoFocus);
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

    [Fact]
    public void SetTracking_ForwardsToTrackingCamera()
    {
        var groupName = $"hub-tcam-{Guid.NewGuid()}";
        var tracking = new TrackingTestCamera(groupName);
        var manager = new CameraManager(tracking);
        CameraHub.RegisterCameraManager(groupName, manager);

        _harness.Hub.SetTracking(groupName, CameraTrackingMode.Auto);

        WaitFor(() => tracking.SetTrackingCalls.Count >= 1);
        Assert.Equal(new[] { CameraTrackingMode.Auto }, tracking.SetTrackingCalls);
    }

    [Fact]
    public void SetTracking_NonTrackingCamera_NoOp()
    {
        _harness.Hub.SetTracking(_groupName, CameraTrackingMode.Auto);

        Assert.Equal(CameraTrackingMode.Unknown, _manager.TrackingMode);
    }

    [Fact]
    public void SetTracking_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.SetTracking($"missing-{Guid.NewGuid()}", CameraTrackingMode.Auto);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }
}
