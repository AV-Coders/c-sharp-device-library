using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class DeviceBase(string name, CommunicationClient client) : LogBase(name), IDevice
{
    public string Name { get; protected set; } = name;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    
    public readonly CommunicationClient CommunicationClient = client;
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
                Log.Verbose("Forcing Power off");
                PowerOff();
                break;
            case PowerState.On:
                Log.Verbose("Forcing Power on");
                PowerOn();
                break;
        }
    }

    public abstract void PowerOn();

    public abstract void PowerOff();
}