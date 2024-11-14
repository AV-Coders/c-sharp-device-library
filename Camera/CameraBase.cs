using AVCoders.Core;

namespace AVCoders.Camera;

public abstract class CameraBase : DeviceBase
{
    public abstract void ZoomStop();

    public abstract void ZoomIn();

    public abstract void ZoomOut();

    public abstract void PanTiltStop();

    public abstract void PanTiltUp();

    public abstract void PanTiltDown();

    public abstract void PanTiltLeft();

    public abstract void PanTiltRight();

    public abstract void RecallPreset(int presetNumber);

    public abstract void SavePreset(int presetNumber);
}