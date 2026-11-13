using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Room.Tests;

[CollectionDefinition(Name)]
public sealed class LoggerFactoryCollection
{
    public const string Name = "LogBase.LoggerFactory";
}

public sealed record CapturedLog(LogLevel Level, Exception? Exception, string Message);

public sealed class CapturingLoggerFactory(ConcurrentQueue<CapturedLog> events) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(events);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLog> events) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            events.Enqueue(new CapturedLog(logLevel, exception, formatter(state, exception)));
    }
}
