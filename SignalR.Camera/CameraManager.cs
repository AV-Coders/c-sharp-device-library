using AVCoders.Camera;
using AVCoders.Core;

namespace AVCoders.SignalR.Camera;

public class CameraManager : DeviceBase
{
    private readonly CameraBase _camera;
    public Dictionary<int, string> PresetDefinitions() => _camera.PresetNames;
    public int LastRecalledPreset => _camera.LastRecalledPreset;
    public event Action<int>? OnPresetRecalled;
    public event Action? OnPresetCleared;

    public CameraManager(CameraBase camera) : base(camera.Name, camera.CommunicationClient)
    {
        _camera = camera;
        camera.PowerStateHandlers += x => PowerState = x;
        camera.OnPresetRecalled += x => OnPresetRecalled?.Invoke(x);
        camera.OnPresetCleared += () => OnPresetCleared?.Invoke();
    }

    public override void PowerOn() => _camera.PowerOn();
    public override void PowerOff() => _camera.PowerOff();
    public void RecallPreset(int index) => _camera.RecallPreset(index);
    public void ZoomStop() => _camera.ZoomStop();
    public void ZoomIn() => _camera.ZoomIn();
    public void ZoomOut() => _camera.ZoomOut();
    public void PanTiltStop() => _camera.PanTiltStop();
    public void PanTiltUp() => _camera.PanTiltUp();
    public void PanTiltDown() => _camera.PanTiltDown();
    public void PanTiltLeft() => _camera.PanTiltLeft();
    public void PanTiltRight() => _camera.PanTiltRight();
    public void SetAutoFocus(PowerState state) => _camera.SetAutoFocus(state);
}
