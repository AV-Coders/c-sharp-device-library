using AVCoders.Camera;
using AVCoders.Core;

namespace AVCoders.SignalR.Camera;

public class CameraManager : DeviceBase
{
    private readonly CameraBase _camera;
    private readonly ITrackingCamera? _trackingCamera;
    public Dictionary<int, string> PresetDefinitions() => _camera.PresetNames;
    public int LastRecalledPreset => _camera.LastRecalledPreset;
    public bool SupportsTracking => _trackingCamera is not null;
    public CameraTrackingMode TrackingMode => _trackingCamera?.TrackingMode ?? CameraTrackingMode.Unknown;
    public event Action<int>? OnPresetRecalled;
    public event Action? OnPresetCleared;
    public event Action<CameraTrackingMode>? OnTrackingModeChanged;

    public CameraManager(CameraBase camera) : base(camera.Name, camera.CommunicationClient)
    {
        _camera = camera;
        _trackingCamera = camera as ITrackingCamera;
        camera.PowerStateHandlers += x => PowerState = x;
        camera.OnPresetRecalled += x => OnPresetRecalled?.Invoke(x);
        camera.OnPresetCleared += () => OnPresetCleared?.Invoke();
        if (_trackingCamera is not null)
            _trackingCamera.OnTrackingModeChange += mode => OnTrackingModeChanged?.Invoke(mode);
    }

    public override void PowerOn() => _camera.PowerOn();
    public override void PowerOff() => _camera.PowerOff();
    public void RecallPreset(int index) => _camera.RecallPreset(index);
    public void SavePreset(int index) => _camera.SavePreset(index);
    public void ZoomStop() => _camera.ZoomStop();
    public void ZoomIn() => _camera.ZoomIn();
    public void ZoomOut() => _camera.ZoomOut();
    public void PanTiltStop() => _camera.PanTiltStop();
    public void PanTiltUp() => _camera.PanTiltUp();
    public void PanTiltDown() => _camera.PanTiltDown();
    public void PanTiltLeft() => _camera.PanTiltLeft();
    public void PanTiltRight() => _camera.PanTiltRight();
    public void SetAutoFocus(PowerState state) => _camera.SetAutoFocus(state);

    public void SetTracking(CameraTrackingMode mode) => _trackingCamera?.SetTracking(mode);
}
