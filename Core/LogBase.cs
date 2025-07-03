using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class LogBase(string name)
{
    public const string MethodProperty = "Method";
    public readonly string Name = name;
    public readonly string InstanceUid = Guid.NewGuid().ToString();
    private readonly Dictionary<string, string> _logProperties = new ();

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
        disposables.Add(LogContext.PushProperty("InstanceName", Name));
        if (methodName != null)
            disposables.Add(LogContext.PushProperty("Method", methodName));
        
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