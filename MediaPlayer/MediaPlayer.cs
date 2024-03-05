using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public abstract class MediaPlayer : IDevice
{
    protected PowerState PowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    protected int Volume = 0;
    protected MuteState AudioMute = MuteState.Unknown;
    protected MuteState VideoMute = MuteState.Unknown;
    
    public abstract void PowerOn();

    public abstract void PowerOff();
    
    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;
    
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"MediaPlayer - {message}");
    }

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }
}