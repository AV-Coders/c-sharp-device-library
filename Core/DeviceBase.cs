using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class DeviceBase : IDevice
{
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public readonly string InstanceUid;
    private readonly Dictionary<string, string> _logProperties = new ();
    public string Name { get; protected set; }

    protected PowerState DesiredPowerState = PowerState.Unknown;
    
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;

    protected DeviceBase(string name)
    {
        Name = name;
        InstanceUid = Guid.NewGuid().ToString();
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

    public abstract void PowerOn();

    public abstract void PowerOff();

    public void AddLogProperty(string name, string value)
    {
        _logProperties[name] = value;
    }
    
    private IDisposable PushProperties()
    {
        var disposables = new List<IDisposable>();

        foreach (var property in _logProperties)
        {
            disposables.Add(LogContext.PushProperty(property.Key, property.Value));
        }
        
        disposables.Add(LogContext.PushProperty("InstanceUid", InstanceUid));
        disposables.Add(LogContext.PushProperty("Class", GetType().Name));
        disposables.Add(LogContext.PushProperty("InstanceName", Name));

        return new DisposableItems(disposables);
    }

    protected void Verbose(string message)
    {
        using (PushProperties())
            Log.Verbose(message);
    }
    
    protected void Debug(string message)
    {
        using (PushProperties())
            Log.Debug(message);
    }
    
    protected void Info(string message)
    {
        using (PushProperties())
            Log.Information(message);
    }
    
    protected void Warn(string message)
    {
        using (PushProperties())
            Log.Warning(message);
    }

    protected void Error(string message)
    {
        
        using (PushProperties())
            Log.Error(message);
    }

    protected void Fatal(string message)
    {
        
        using (PushProperties())
            Log.Fatal(message);
    }
    
    protected void LogException(Exception e)
    {
        Error(e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace);
        if (e.InnerException == null)
            return;
        Error("Caused by: " + e.InnerException.GetType().Name + Environment.NewLine + e.InnerException.Message + Environment.NewLine + e.InnerException.StackTrace);
    }
}