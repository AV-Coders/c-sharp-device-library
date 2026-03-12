using AVCoders.Core;

namespace AVCoders.Camera;

public abstract class CameraBase(string name, CommunicationClient client, Dictionary<int, string> presetNames)
    : DeviceBase(name, client)
{
    public const int NoActivePreset = -1;
    private int _lastRecalledPreset = -1;
    /// <summary>
    /// Invoked when the last recalled preset changes.
    /// A value of -1 indicates that the driver received a move command.
    /// </summary>
    public event Action? OnPresetCleared;
    public event Action<int>? OnPresetRecalled;
    public Dictionary<int, string> PresetNames { get; } = presetNames;

    /// <summary>
    /// The last recalled preset number. Returns <see cref="NoActivePreset"/> when no preset is active.
    /// </summary>
    public int LastRecalledPreset
    {
        get => _lastRecalledPreset;
        protected set
        {
            if (value == _lastRecalledPreset)
                return;
            _lastRecalledPreset = value;
            OnPresetRecalled?.Invoke(value);
        }
    }

    public void ZoomStop() { DoZoomStop(); ClearPreset(); }

    protected abstract void DoZoomStop();

    public abstract void ZoomIn();

    public abstract void ZoomOut();

    public void PanTiltStop() { DoPanTiltStop(); ClearPreset(); }

    private void ClearPreset()
    {
        if(LastRecalledPreset == NoActivePreset)
            return;
        _lastRecalledPreset = NoActivePreset; // Don't invoke OnPresetRecalled
        OnPresetCleared?.Invoke();
    }

    protected abstract void DoPanTiltStop();

    public abstract void PanTiltUp();

    public abstract void PanTiltDown();

    public abstract void PanTiltLeft();

    public abstract void PanTiltRight();

    public abstract void SetAutoFocus(PowerState state);

    public void RecallPreset(int presetNumber)
    {
        DoRecallPreset(presetNumber);
        LastRecalledPreset = presetNumber;
    }

    public abstract void DoRecallPreset(int presetNumber);

    public abstract void SavePreset(int presetNumber);
}