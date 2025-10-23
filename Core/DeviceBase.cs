using Serilog;
using Serilog.Context;
using Serilog.Core;

namespace AVCoders.Core;

public abstract class DeviceBase : LogBase, IDevice
{
    public CommunicationStateHandler CommunicationStateHandlers;
    public PowerStateHandler PowerStateHandlers;
    
    public readonly CommunicationClient CommunicationClient;
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
            CommunicationStateHandlers.Invoke(CommunicationState);
        }
    }
    
    protected DeviceBase(string name, CommunicationClient client) : base(name)
    {
        CommunicationClient = client;
        CommunicationStateHandlers = x => AddEvent(EventType.DriverState, x.ToString());
        PowerStateHandlers = x => AddEvent(EventType.Power, x.ToString());
    }

    protected void ProcessPowerState()
    {
        if (PowerState == DesiredPowerState)
            return;
        
        using (PushProperties("ProcessPowerState"))
        {
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