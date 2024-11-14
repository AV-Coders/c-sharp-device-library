namespace AVCoders.Core;

public abstract class DeviceBase : IDevice
{
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    
    private PowerState _powerState;
    private CommunicationState _communicationState;

    public PowerState CurrentPowerState
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

    protected DeviceBase()
    {
        _powerState = PowerState.Unknown;
        _communicationState = CommunicationState.Unknown;
    }

    protected void ReportPowerState() => PowerStateHandlers?.Invoke(CurrentPowerState);
    private void ReportCommunicationState() => CommunicationStateHandlers?.Invoke(CommunicationState);

    public PowerState GetCurrentPowerState() => CurrentPowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    public abstract void PowerOn();

    public abstract void PowerOff();

    protected void Log(string message)
    {
        LogHandlers?.Invoke($"{GetType()} - {message}");
    }

    protected void Error(string message)
    {
        LogHandlers?.Invoke($"{GetType()} - {message}", EventLevel.Error);
    }
}