using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class LogBase(string name)
{
    private string _name = name;
    public const string MethodProperty = "Method";
    public readonly string InstanceUid = Guid.NewGuid().ToString();
    private readonly Dictionary<string, string> _logProperties = new ();
    public StringHandler? NameChangedHandlers;

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
        if(Name != String.Empty)
            disposables.Add(LogContext.PushProperty("InstanceName", Name));
        if (methodName != null)
            disposables.Add(LogContext.PushProperty("Method", methodName));
        
        return new DisposableItems(disposables);
    }

    protected void LogException(Exception e)
    {
        using (PushProperties())
        {
            Log.Error(e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace);
            if (e.InnerException == null)
                return;
            Log.Error("Caused by: " + e.InnerException.GetType().Name + Environment.NewLine + e.InnerException.Message +
                  Environment.NewLine + e.InnerException.StackTrace);
        }
    }
}