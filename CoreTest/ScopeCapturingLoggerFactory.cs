using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AVCoders.Core.Tests;

public sealed record CapturedLog(string Message, Dictionary<string, object?> Scope)
{
    public string? TriggerId => Scope.GetValueOrDefault(LogBase.TriggerIdProperty) as string;
}

public sealed class ScopeCapturingLoggerFactory : ILoggerFactory, ILogger
{
    private readonly LoggerExternalScopeProvider _scopes = new();

    public ConcurrentQueue<CapturedLog> Logs { get; } = new();

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _scopes.Push(state);
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var scope = new Dictionary<string, object?>();
        _scopes.ForEachScope((scopeState, properties) =>
        {
            if (scopeState is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var pair in pairs)
                    properties[pair.Key] = pair.Value;
        }, scope);
        Logs.Enqueue(new CapturedLog(formatter(state, exception), scope));
    }
}
