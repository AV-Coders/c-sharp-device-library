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
    private string _name;
    public const string MethodProperty = "Method";
    public const string TriggerProperty = "Trigger";
    public readonly string InstanceUid = Guid.NewGuid().ToString();
    private readonly Dictionary<string, string> _logProperties = new ();
    public StringHandler? NameChangedHandlers;
    private readonly List<Error> _errors = [];
    private readonly List<Event> _events = [];
    private readonly object _eventsLock = new();
    private readonly object _errorsLock = new();
    private int _errorLimit = 10;
    private int _eventLimit = 100;
    public event ActionHandler? EventsUpdated;
    public event ActionHandler? ErrorsUpdated;

    public IReadOnlyList<Event> Events
    {
        get { lock (_eventsLock) return _events.ToList(); }
    }

    public IReadOnlyList<Error> Errors
    {
        get { lock (_errorsLock) return _errors.ToList(); }
    }

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
            if (e.InnerException != null)
                Log.Error(e.InnerException, e.InnerException.Message);

            lock (_errorsLock)
            {
                _errors.Add(new Error(DateTimeOffset.UtcNow, message ?? e.Message, e));
                LimitErrors();
            }
            ErrorsUpdated?.Invoke();
        }
    }

    public void ClearErrors()
    {
        lock (_errorsLock)
        {
            _errors.Clear();
        }
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
        lock (_eventsLock)
        {
            _events.Add(new Event(DateTimeOffset.UtcNow, type, info, LogContext.Clone()));
            LimitEvents();
        }
        EventsUpdated?.Invoke();
    }

    public void ClearEvents()
    {
        lock (_eventsLock)
        {
            _events.Clear();
        }
        EventsUpdated?.Invoke();
    }

    private void LimitEvents()
    {
        if (_events.Count > _eventLimit)
            _events.RemoveRange(0, _events.Count - _eventLimit);
    }

    public void SetEventLimit(int limit)
    {
        lock (_eventsLock)
        {
            _eventLimit = limit;
            LimitEvents();
        }
        EventsUpdated?.Invoke();
    }

    public void SetErrorLimit(int limit)
    {
        lock (_errorsLock)
        {
            _errorLimit = limit;
            LimitErrors();
        }
        ErrorsUpdated?.Invoke();
    }
}