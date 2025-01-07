namespace AVCoders.Core;

public abstract class DeviceBase : IDevice
{
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    
    protected PowerState DesiredPowerState = PowerState.Unknown;
    
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;

    public PowerState PowerState
    {
        get => _powerState;
        protected set
        {
            if (value == _powerState)
                return;
            _powerState = value;
            PowerStateHandlers?.Invoke(PowerState);
        }
    }

    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if (value == _communicationState)
                return;
            
            _communicationState = value;
            CommunicationStateHandlers?.Invoke(CommunicationState);
        }
    }

    protected void ProcessPowerState()
    {
        if (PowerState == DesiredPowerState)
            return;
        switch (DesiredPowerState)
        {
            case PowerState.Off:
                Log("Forcing Power off");
                PowerOff();
                break;
            case PowerState.On:
                Log("Forcing Power on");
                PowerOn();
                break;
        }
    }

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    public abstract void PowerOn();

    public abstract void PowerOff();

    protected void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {message}");

    protected void Error(string message) => LogHandlers?.Invoke($"{GetType()} - {message}", EventLevel.Error);
}