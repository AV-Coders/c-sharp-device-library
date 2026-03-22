using Serilog;
using Serilog.Context;
using Serilog.Core;

namespace AVCoders.Core;

public record Event(
    DateTimeOffset Timestamp,
    EventType Type,
    string Info,
    ILogEventEnricher LogContext);

public abstract class LogBase
{
    public IReadOnlyList<Event> Events => _events;
    private string _name;
    public const string MethodProperty = "Method";
    public const string TriggerProperty = "Trigger";
    public readonly string InstanceUid = Guid.NewGuid().ToString();
    private readonly Dictionary<string, string> _logProperties = new ();
    public StringHandler? NameChangedHandlers;
    private readonly List<Error> _errors = [];
    private readonly List<Event> _events = [];
    private int _errorLimit = 10;
    private int _eventLimit = 100;
    public event ActionHandler? EventsUpdated;
    public event ActionHandler? ErrorsUpdated;

    public string Name
    {
        get => _name;
        protected set
        {
            if (value == _name)
                return;
            _name = value;
            NameChangedHandlers?.Invoke(value);
        }
    }
    
    public List<Error> Errors => _errors;

    protected LogBase(string name)
    {
        _name = name;
    }

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
        if (methodName != null)
            disposables.Add(LogContext.PushProperty("Method", methodName));
        
        return new DisposableItems(disposables);
    }

    protected void LogException(Exception e, string? message = null)
    {
        using (PushProperties())
        {
            Log.Error(e, message == null ? e.Message : $"{message} - {e.Message}");
            _errors.Add(new Error(DateTime.Now, message ?? e.Message, e));
            if (e.InnerException != null)
                Log.Error(e.InnerException, e.InnerException.Message);

            LimitErrors();
            ErrorsUpdated?.Invoke();
        }
    }

    public void ClearErrors()
    {
        _errors.Clear();
        ErrorsUpdated?.Invoke();
    }
    private void LimitErrors()
    {
        if (_errors.Count > _errorLimit)
            _errors.RemoveRange(0, _errors.Count - _errorLimit);
    }

    protected void AddEvent(EventType type, string info)
    {
        Log.Verbose(info);
        _events.Add(new Event(DateTimeOffset.Now, type, info, LogContext.Clone()));
        LimitEvents();
        EventsUpdated?.Invoke();
    }
    
    public void ClearEvents()
    {
        _events.Clear();
        EventsUpdated?.Invoke();
    }

    private void LimitEvents()
    {
        if (_events.Count > _eventLimit)
            _events.RemoveRange(0, _events.Count - _eventLimit);
    }
    }
}