using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class LogBase
{
    public readonly string Name;

    protected LogBase(string name)
    {
        Name = name;
    }

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