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

    [Fact]
    public void PowerOn_ForwardsToCamera()
    {
        _manager.PowerOn();

        Assert.Equal(1, _camera.PowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToCamera()
    {
        _manager.PowerOff();

        Assert.Equal(1, _camera.PowerOffCallCount);
    }

    [Fact]
    public void RecallPreset_ForwardsToCamera()
    {
        _manager.RecallPreset(2);

        Assert.Equal(1, _camera.DoRecallPresetCallCount);
        Assert.Equal(2, _camera.LastDoRecallPresetArg);
    }

    [Fact]
    public void SavePreset_ForwardsToCamera()
    {
        _manager.SavePreset(3);

        Assert.Equal(1, _camera.SavePresetCallCount);
        Assert.Equal(3, _camera.LastSavePresetArg);
    }

    [Fact]
    public void ZoomStop_ForwardsToCamera()
    {
        _manager.ZoomStop();

        Assert.Equal(1, _camera.DoZoomStopCallCount);
    }

    [Fact]
    public void ZoomIn_ForwardsToCamera()
    {
        _manager.ZoomIn();

        Assert.Equal(1, _camera.ZoomInCallCount);
    }

    [Fact]
    public void ZoomOut_ForwardsToCamera()
    {
        _manager.ZoomOut();

        Assert.Equal(1, _camera.ZoomOutCallCount);
    }

    [Fact]
    public void PanTiltStop_ForwardsToCamera()
    {
        _manager.PanTiltStop();

        Assert.Equal(1, _camera.DoPanTiltStopCallCount);
    }

    [Fact]
    public void PanTiltUp_ForwardsToCamera()
    {
        _manager.PanTiltUp();

        Assert.Equal(1, _camera.PanTiltUpCallCount);
    }

    [Fact]
    public void PanTiltDown_ForwardsToCamera()
    {
        _manager.PanTiltDown();

        Assert.Equal(1, _camera.PanTiltDownCallCount);
    }

    [Fact]
    public void PanTiltLeft_ForwardsToCamera()
    {
        _manager.PanTiltLeft();

        Assert.Equal(1, _camera.PanTiltLeftCallCount);
    }

    [Fact]
    public void PanTiltRight_ForwardsToCamera()
    {
        _manager.PanTiltRight();

        Assert.Equal(1, _camera.PanTiltRightCallCount);
    }

    [Theory]
    [InlineData(PowerState.On)]
    [InlineData(PowerState.Off)]
    public void SetAutoFocus_ForwardsToCamera(PowerState state)
    {
        _manager.SetAutoFocus(state);

        Assert.Equal(state, _camera.LastSetAutoFocus);
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
    public void TrackingMode_DefaultsToUnknown()
    {
        Assert.Equal(CameraTrackingMode.Unknown, _manager.TrackingMode);
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
