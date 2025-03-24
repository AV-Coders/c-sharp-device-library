using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class DeviceBase : IDevice
{
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected string Name;
    
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;

    protected DeviceBase(string name)
    {
        Name = name;
    }

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
                Verbose("Forcing Power off");
                PowerOff();
                break;
            case PowerState.On:
                Verbose("Forcing Power on");
                PowerOn();
                break;
        }
    }

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    public abstract void PowerOn();

    public abstract void PowerOff();

    protected void Verbose(string message)
    {
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Verbose(message);
        }
    }
    
    protected void Debug(string message)
    {
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Debug(message);
        }
    }
    
    protected void Info(string message)
    {
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Information(message);
        }
    }
    
    protected void Warn(string message)
    {
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Warning(message);
        }
    }

    protected void Error(string message)
    {
        
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Error(message);
        }
    }

    protected void Fatal(string message)
    {
        
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Fatal(message);
        }
    }
}