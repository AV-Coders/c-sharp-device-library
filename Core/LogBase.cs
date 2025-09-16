using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class LogBase
{
    private string _name;
    public const string MethodProperty = "Method";
    public readonly string InstanceUid = Guid.NewGuid().ToString();
    private readonly Dictionary<string, string> _logProperties = new ();
    public StringHandler? NameChangedHandlers;
    private List<Error> _errors = []; 
    public ActionHandler ErrorsChangedHandlers;

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
        ErrorsChangedHandlers += HandleErrorListChange;
    }

    private void HandleErrorListChange()
    {
        if(_errors.Count > 100)
            _errors.RemoveRange(0, 75);
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
            if(message != null)
                Log.Error(message);
            Log.Error("{ExceptionType} \r\n{ExceptionMessage}\r\n{StackTrace}", 
                e.GetType().Name, e.Message, e.StackTrace);
            _errors.Add(new Error(DateTime.Now, message ?? e.Message, e));
            if (e.InnerException == null)
                return;
            Log.Error("Caused by: {InnerExceptionType} \r\n{InnerExceptionMessage}\r\n{InnerStackTrace}", 
                e.InnerException.GetType().Name, e.InnerException.Message, e.InnerException.StackTrace);
            _errors.Add(new Error(DateTime.Now, e.InnerException.Message, e.InnerException));
        }
    }
}