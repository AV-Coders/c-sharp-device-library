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

    public void AddLogProperty(string name, string value)
    {
        _logProperties[name] = value;
    }
    
    protected IDisposable PushProperties(string? methodName = null)
    {
        var disposables = new List<IDisposable>();

        foreach (var property in _logProperties)
        {
            disposables.Add(LogContext.PushProperty(property.Key, property.Value));
        }
        
        disposables.Add(LogContext.PushProperty("InstanceUid", InstanceUid));
        disposables.Add(LogContext.PushProperty("Class", GetType().Name));
        if(Name != string.Empty)
            disposables.Add(LogContext.PushProperty("InstanceName", Name));
        if(methodName != null)
            disposables.Add(LogContext.PushProperty(LogBase.MethodProperty, methodName));

        return new DisposableItems(disposables);
    }
    
    protected void LogException(Exception e)
    {
        using (PushProperties())
        {
            Log.Error("{ExceptionType} \r\n{ExceptionMessage}\r\n{StackTrace}",
                e.GetType().Name, e.Message, e.StackTrace);
            if (e.InnerException == null)
                return;
            Log.Error("Caused by: {InnerExceptionType} \r\n{InnerExceptionMessage}\r\n{InnerStackTrace}",
                e.InnerException.GetType().Name, e.InnerException.Message, e.InnerException.StackTrace);
        }
    }
}