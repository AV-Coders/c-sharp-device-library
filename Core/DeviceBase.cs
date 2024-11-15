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
        set
        {
            if (value == _powerState)
                return;
            _powerState = value;
            ReportPowerState();
        }
    }

    protected CommunicationState CommunicationState
    {
        get => _communicationState;
        set
        {
            if (value == _communicationState)
                return;
            
            _communicationState = value;
            ReportCommunicationState();
        }
    }

    protected void ProcessPowerState()
    {
        PowerStateHandlers?.Invoke(PowerState);
        if (PowerState == DesiredPowerState)
            return;
        if (DesiredPowerState == PowerState.Unknown)
            return;
        Log("Forcing Power");
        if (DesiredPowerState == PowerState.Off)
            PowerOff();
        else if (DesiredPowerState == PowerState.On) 
            PowerOn();
    }

    protected void ReportPowerState() => PowerStateHandlers?.Invoke(PowerState);
    protected void ReportCommunicationState() => CommunicationStateHandlers?.Invoke(CommunicationState);

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    public abstract void PowerOn();

    public abstract void PowerOff();

    protected void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {message}");

    protected void Error(string message) => LogHandlers?.Invoke($"{GetType()} - {message}", EventLevel.Error);
}