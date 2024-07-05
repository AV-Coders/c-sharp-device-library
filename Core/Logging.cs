namespace AVCoders.Core;

public delegate void LogEvent(Type owner, string message, EventLevel level);

public class Logging
{
    public event LogEvent? LogEvents;
    
    protected void Log(string message, EventLevel level = EventLevel.Verbose) => LogEvents?.Invoke(GetType(), $"{message}", level);

    protected void Warn(string message) => LogEvents?.Invoke(GetType(), $"{message}", EventLevel.Warning);

    protected void Error(string message) => LogEvents?.Invoke(GetType(), message, EventLevel.Error);

    protected void Critical(string message) => LogEvents?.Invoke(GetType(), message, EventLevel.Critical);
}