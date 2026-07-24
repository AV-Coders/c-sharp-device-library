namespace AVCoders.Core;

public abstract class DeviceBase : LogBase, IDevice
{
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public event Action<PowerState>? OnPowerStateChanged;
    public event Action<CommunicationState>? OnCommunicationStateChanged;
    
    public readonly CommunicationClient CommunicationClient;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected const string PowerStateIssueKey = "power-state";
    protected const string CommunicationIssueKey = "communication";
    
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
                RaiseOngoingIssue(CommunicationIssueKey, "Device communication error", IssueSeverity.Critical);
            else if (value == CommunicationState.Okay)
                ResolveIssue(CommunicationIssueKey);
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
            ResolveIssue(PowerStateIssueKey);
            return;
        }

        using (PushProperties("ProcessPowerState"))
        {
            RaiseOngoingIssue(PowerStateIssueKey, $"Power is {PowerState}, should be {DesiredPowerState}");
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