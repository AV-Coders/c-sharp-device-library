
using AVCoders.Core;

namespace AVCoders.Camera;

public abstract class Camera : ICamera
{
    protected PowerState PowerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;

    protected Camera()
    {
    }

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;
    
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"Camera - {message}");
    }

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    public abstract void PowerOff();

    public abstract void PowerOn();

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