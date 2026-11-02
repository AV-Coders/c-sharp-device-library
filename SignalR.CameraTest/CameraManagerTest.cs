using AVCoders.Camera;
using AVCoders.Core;

namespace AVCoders.SignalR.Camera.Tests;

public class CameraManagerTest
{
    private readonly TestCamera _camera;
    private readonly CameraManager _manager;

    public CameraManagerTest()
    {
        _camera = new TestCamera();
        _manager = new CameraManager(_camera);
    }

    public static TheoryData<string, Action<CameraManager>, Func<TestCamera, bool>> Commands => new()
    {
        { "PowerOn", manager => manager.PowerOn(), camera => camera.PowerOnCallCount == 1 },
        { "PowerOff", manager => manager.PowerOff(), camera => camera.PowerOffCallCount == 1 },
        { "RecallPreset", manager => manager.RecallPreset(2), camera => camera.DoRecallPresetCallCount == 1 && camera.LastDoRecallPresetArg == 2 },
        { "SavePreset", manager => manager.SavePreset(3), camera => camera.SavePresetCallCount == 1 && camera.LastSavePresetArg == 3 },
        { "ZoomStop", manager => manager.ZoomStop(), camera => camera.DoZoomStopCallCount == 1 },
        { "ZoomIn", manager => manager.ZoomIn(), camera => camera.ZoomInCallCount == 1 },
        { "ZoomOut", manager => manager.ZoomOut(), camera => camera.ZoomOutCallCount == 1 },
        { "PanTiltStop", manager => manager.PanTiltStop(), camera => camera.DoPanTiltStopCallCount == 1 },
        { "PanTiltUp", manager => manager.PanTiltUp(), camera => camera.PanTiltUpCallCount == 1 },
        { "PanTiltDown", manager => manager.PanTiltDown(), camera => camera.PanTiltDownCallCount == 1 },
        { "PanTiltLeft", manager => manager.PanTiltLeft(), camera => camera.PanTiltLeftCallCount == 1 },
        { "PanTiltRight", manager => manager.PanTiltRight(), camera => camera.PanTiltRightCallCount == 1 },
        { "SetAutoFocus On", manager => manager.SetAutoFocus(PowerState.On), camera => camera.LastSetAutoFocus == PowerState.On },
        { "SetAutoFocus Off", manager => manager.SetAutoFocus(PowerState.Off), camera => camera.LastSetAutoFocus == PowerState.Off },
    };

    [Fact]
    public void PresetDefinitions_ReturnsCameraPresetNames()
    {
        Assert.Equal(_camera.PresetNames, _manager.PresetDefinitions());
    }

    [Fact]
    public void LastRecalledPreset_DefaultsToNoActivePreset()
    {
        Assert.Equal(CameraBase.NoActivePreset, _manager.LastRecalledPreset);
    }

    [Fact]
    public void LastRecalledPreset_ReflectsCameraValue()
    {
        _camera.SetLastRecalledPresetForTest(3);

        Assert.Equal(3, _manager.LastRecalledPreset);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_ReachesTheCamera(string command, Action<CameraManager> send, Func<TestCamera, bool> received)
    {
        send(_manager);

        Assert.True(received(_camera), $"{command} did not reach the camera");
    }

    [Fact]
    public void CameraPowerStateChange_UpdatesManagerPowerState()
    {
        _camera.SetPowerStateForTest(PowerState.On);

        Assert.Equal(PowerState.On, _manager.PowerState);
    }

    [Fact]
    public void CameraOnPresetRecalled_RaisesManagerOnPresetRecalled()
    {
        var handler = new Mock<Action<int>>();
        _manager.OnPresetRecalled += handler.Object;

        _camera.SetLastRecalledPresetForTest(4);

        handler.Verify(h => h.Invoke(4), Times.Once);
    }

    [Fact]
    public void CameraOnPresetCleared_RaisesManagerOnPresetCleared()
    {
        var handler = new Mock<Action>();
        _manager.OnPresetCleared += handler.Object;

        _camera.SetLastRecalledPresetForTest(1);
        _camera.PanTiltStop();

        handler.Verify(h => h.Invoke(), Times.Once);
    }

    [Fact]
    public void SupportsTracking_FalseForNonTrackingCamera()
    {
        Assert.False(_manager.SupportsTracking);
    }

    [Fact]
    public void SupportsTracking_TrueForTrackingCamera()
    {
        var manager = new CameraManager(new TrackingTestCamera());

        Assert.True(manager.SupportsTracking);
    }

    [Fact]
    public void SetTracking_NonTrackingCamera_NoOp()
    {
        var handler = new Mock<Action<CameraTrackingMode>>();
        _manager.OnTrackingModeChanged += handler.Object;

        _manager.SetTracking(CameraTrackingMode.Auto);

        Assert.Equal(CameraTrackingMode.Unknown, _manager.TrackingMode);
        handler.Verify(h => h.Invoke(It.IsAny<CameraTrackingMode>()), Times.Never);
    }

    [Fact]
    public void SetTracking_TrackingCamera_ForwardsAndRaisesEvent()
    {
        var tracking = new TrackingTestCamera();
        var manager = new CameraManager(tracking);
        var handler = new Mock<Action<CameraTrackingMode>>();
        manager.OnTrackingModeChanged += handler.Object;

        manager.SetTracking(CameraTrackingMode.Auto);

        Assert.Equal(new[] { CameraTrackingMode.Auto }, tracking.SetTrackingCalls);
        Assert.Equal(CameraTrackingMode.Auto, manager.TrackingMode);
        handler.Verify(h => h.Invoke(CameraTrackingMode.Auto), Times.Once);
    }

    [Fact]
    public void CameraOnTrackingModeChange_RaisesManagerOnTrackingModeChanged()
    {
        var tracking = new TrackingTestCamera();
        var manager = new CameraManager(tracking);
        var handler = new Mock<Action<CameraTrackingMode>>();
        manager.OnTrackingModeChanged += handler.Object;

        tracking.SetTrackingModeForTest(CameraTrackingMode.Manual);

        Assert.Equal(CameraTrackingMode.Manual, manager.TrackingMode);
        handler.Verify(h => h.Invoke(CameraTrackingMode.Manual), Times.Once);
    }

    [Fact]
    public void SetTracking_SameMode_DoesNotRaiseEventTwice()
    {
        var tracking = new TrackingTestCamera();
        var manager = new CameraManager(tracking);
        var handler = new Mock<Action<CameraTrackingMode>>();
        manager.OnTrackingModeChanged += handler.Object;

        manager.SetTracking(CameraTrackingMode.Auto);
        manager.SetTracking(CameraTrackingMode.Auto);

        Assert.Equal(2, tracking.SetTrackingCalls.Count);
        handler.Verify(h => h.Invoke(CameraTrackingMode.Auto), Times.Once);
    }
}
