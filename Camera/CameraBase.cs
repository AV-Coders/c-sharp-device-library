using AVCoders.Core;

namespace AVCoders.Camera;

public abstract class CameraBase(string name, CommunicationClient client, CommandStringFormat commandStringFormat)
    : DeviceBase(name, client, commandStringFormat)
{
    private int _lastRecalledPreset = 0;
    public IntHandler? LastRecalledPresetHandlers;

    public int LastRecalledPreset
    {
        get => _lastRecalledPreset;
        set
        {
            if (value == _lastRecalledPreset)
                return;
            _lastRecalledPreset = value;
            LastRecalledPresetHandlers?.Invoke(value);
        }
    }

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
        LastRecalledPreset = presetNumber;
    }

    public abstract void DoRecallPreset(int presetNumber);

    public abstract void SavePreset(int presetNumber);
}