namespace AVCoders.Core;

public abstract class DeviceBase : LogBase, IDevice
{
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public event Action<PowerState>? OnPowerStateChanged;
    public event Action<CommunicationState>? OnCommunicationStateChanged;
    
    public readonly CommunicationClient CommunicationClient;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected const string PowerStateErrorKey = "power-state";
    protected const string CommunicationErrorKey = "communication";
    
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
            AddEvent(EventType.Power, value.ToString());
            PowerStateHandlers?.Invoke(PowerState);
            OnPowerStateChanged?.Invoke(PowerState);
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
            AddEvent(EventType.DriverState, value.ToString());
            if (value == CommunicationState.Error)
                RaisePersistentError(CommunicationErrorKey, "Device communication error");
            else if (value == CommunicationState.Okay)
                ClearPersistentError(CommunicationErrorKey);
            CommunicationStateHandlers?.Invoke(CommunicationState);
            OnCommunicationStateChanged?.Invoke(CommunicationState);
        }
    }

    protected DeviceBase(string name, CommunicationClient client) : base(name)
    {
        CommunicationClient = client;
    }

    protected void ProcessPowerState()
    {
        if (PowerState == DesiredPowerState || DesiredPowerState == PowerState.Unknown)
        {
            ClearPersistentError(PowerStateErrorKey);
            return;
        }

        using (PushProperties("ProcessPowerState"))
        {
            RaisePersistentError(PowerStateErrorKey, $"Power is {PowerState}, should be {DesiredPowerState}");
            switch (DesiredPowerState)
            {
                case PowerState.Off:
                    AddEvent(EventType.Power, "Power state not desired, forcing power Off");
                    PowerOff();
                    break;
                case PowerState.On:
                    AddEvent(EventType.Power, "Power state not desired, forcing power On");
                    PowerOn();
                    break;
            }
        }
    }

    public abstract void PowerOn();

    public abstract void PowerOff();
}