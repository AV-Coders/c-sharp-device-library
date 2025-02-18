using AVCoders.Core;

namespace AVCoders.Camera;

public abstract class CameraBase : DeviceBase
{
    public IntHandler? LastRecalledPresetHandlers;
    public abstract void ZoomStop();

    public abstract void ZoomIn();

    public abstract void ZoomOut();

    public abstract void PanTiltStop();

    public abstract void PanTiltUp();

    public abstract void PanTiltDown();

    public abstract void PanTiltLeft();

    public abstract void PanTiltRight();

    public void RecallPreset(int presetNumber)
    {
        DoRecallPreset(presetNumber);
        LastRecalledPresetHandlers?.Invoke(presetNumber);
    }

    public abstract void DoRecallPreset(int presetNumber);

    public abstract void SavePreset(int presetNumber);
}