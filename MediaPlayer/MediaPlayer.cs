using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public abstract class MediaPlayer : IDevice
{
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    protected int Volume = 0;
    protected MuteState AudioMute = MuteState.Unknown;
    protected MuteState VideoMute = MuteState.Unknown;
    
    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if(_communicationState == value)
                return;
            _communicationState = value;
            CommunicationStateHandlers?.Invoke(value);
        }
    }
    public PowerState PowerState
    {
        get => _powerState;
        protected set
        {
            if (_powerState == value)
                return;
            _powerState = value;
            PowerStateHandlers?.Invoke(value);
        }
    }
    
    public abstract void PowerOn();

    public abstract void PowerOff();
    
    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;
    
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"MediaPlayer - {message}");
    }
}