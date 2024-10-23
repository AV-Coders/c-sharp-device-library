using AVCoders.Core;

namespace AVCoders.Camera;

public interface ICamera : IDevice
{
    public void ZoomStop();

    public void ZoomIn();

    public void ZoomOut();

    public void PanTiltStop();

    public void PanTiltUp();

    public void PanTiltDown();

    public void PanTiltLeft();

    public void PanTiltRight();

    public void RecallPreset(int presetNumber);

    public void SavePreset(int presetNumber);
}