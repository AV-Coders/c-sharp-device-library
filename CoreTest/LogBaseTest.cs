using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVCoders.Core.Tests;

public class LogBaseTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public IDisposable InvokePushProperties(string? methodName = null) => PushProperties(methodName);
        public void InvokeLogException(Exception e, string? message = null) => LogException(e, message);
        public void InvokeLogException(Exception e, LogLevel level, string? message = null) => LogException(e, level, message);
    }

    private sealed record WrittenLine(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _lock = new();
        private readonly List<object> _activeScopes = new();
        private readonly List<Dictionary<string, object>> _events = new();
        private readonly List<WrittenLine> _lines = new();

        public int ActiveScopeCount { get { lock (_lock) return _activeScopes.Count; } }

        public Dictionary<string, object> LastEvent { get { lock (_lock) return _events[^1]; } }

        public List<WrittenLine> Lines { get { lock (_lock) return [.._lines]; } }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            lock (_lock)
                _activeScopes.Add(state);
            return new Scope(this, state);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                var properties = new Dictionary<string, object>();
                foreach (var scope in _activeScopes)
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                        foreach (var pair in pairs)
                            properties[pair.Key] = pair.Value;
                }
                _events.Add(properties);
                _lines.Add(new WrittenLine(logLevel, exception, formatter(state, exception)));
            }
        }

        private sealed class Scope(CapturingLogger logger, object state) : IDisposable
        {
            public void Dispose()
            {
                lock (logger._lock)
                    logger._activeScopes.Remove(state);
            }
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains(nameof(LogBaseTest)) ? logger : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private readonly CapturingLogger _logger = new();
    private readonly TestLogBase _logBase;

    public LogBaseTest()
    {
        LogBase.LoggerFactory = new CapturingLoggerFactory(_logger);
        _logBase = new TestLogBase("Test Instance");
    }

    private Dictionary<string, object> LogAndCapture()
    {
        _logger.Log(LogLevel.Information, default, "Canary", null, (state, _) => state);
        return _logger.LastEvent;
    }

    [Fact]
    public void PushProperties_AddsPropertiesToTheScope()
    {
        using (_logBase.InvokePushProperties("TestMethod"))
        {
            var properties = LogAndCapture();

            Assert.True(properties.ContainsKey("InstanceUid"));
            Assert.True(properties.ContainsKey("Class"));
            Assert.True(properties.ContainsKey("InstanceName"));
            Assert.True(properties.ContainsKey("Method"));
        }
    }

    [Fact]
    public void PushProperties_UnderAnActorAndATrigger_AddsBothToTheScope()
    {
        Actor.Current = new Actor(ActorKind.Browser, "10.1.2.3", "Firefox", "LAPTOP-12", "circuit-1");
        var triggerId = Trigger.Start();

        using (_logBase.InvokePushProperties("TestMethod"))
        {
            var properties = LogAndCapture();

            Assert.Equal("Browser 10.1.2.3 (LAPTOP-12)", properties[LogBase.ActorProperty]);
            Assert.Equal(triggerId, properties[LogBase.TriggerIdProperty]);
        }
    }

    [Fact]
    public void PushProperties_WithoutAnActorOrATrigger_AddsNeither()
    {
        Actor.Current = null;
        using (_logBase.InvokePushProperties("TestMethod"))
        {
            var properties = LogAndCapture();

            Assert.False(properties.ContainsKey(LogBase.ActorProperty));
            Assert.False(properties.ContainsKey(LogBase.TriggerIdProperty));
        }
    }

    [Fact]
    public void PushProperties_Dispose_RemovesAllPropertiesFromTheScope()
    {
        using (_logBase.InvokePushProperties("TestMethod"))
        {
        }

        var properties = LogAndCapture();

        Assert.False(properties.ContainsKey("InstanceUid"), "InstanceUid was left in scope after disposal");
        Assert.False(properties.ContainsKey("Class"), "Class was left in scope after disposal");
        Assert.False(properties.ContainsKey("InstanceName"), "InstanceName was left in scope after disposal");
        Assert.False(properties.ContainsKey("Method"), "Method was left in scope after disposal");
    }

    [Fact]
    public void PushProperties_Dispose_DoesNotAccumulateScopesAcrossRepeatedCalls()
    {
        int baseline = _logger.ActiveScopeCount;

        for (int i = 0; i < 500; i++)
        {
            using (_logBase.InvokePushProperties("TestMethod"))
            {
            }
        }

        Assert.Equal(baseline, _logger.ActiveScopeCount);
        Assert.Empty(LogAndCapture());
    }

    [Fact]
    public void LogException_AtDebug_WritesOneDebugLineAndKeepsTheError()
    {
        var timeout = new OperationCanceledException("The operation was canceled.");

        _logBase.InvokeLogException(timeout, LogLevel.Debug, "Connecting to 10.1.2.3 timed out");

        var line = Assert.Single(_logger.Lines);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Same(timeout, line.Exception);
        Assert.Equal("Connecting to 10.1.2.3 timed out", line.Message);
        var error = Assert.Single(_logBase.Errors);
        Assert.Same(timeout, error.Exception);
        Assert.Equal("Connecting to 10.1.2.3 timed out", error.Message);
    }

    [Fact]
    public void LogException_WithAnInnerException_WritesOneErrorLineCarryingTheInnerTypeAndMessage()
    {
        var inner = new System.Net.Sockets.SocketException(10060);
        var outer = new InvalidOperationException("Connect failed", inner);

        _logBase.InvokeLogException(outer);

        var line = Assert.Single(_logger.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Same(outer, line.Exception);
        Assert.Equal($"Connect failed (System.Net.Sockets.SocketException: {inner.Message})", line.Message);
    }

    [Fact]
    public void LogException_WithoutAnInnerException_WritesOneErrorLineWithTheMessage()
    {
        var failure = new InvalidOperationException("Connect failed");

        _logBase.InvokeLogException(failure, "Sending failed");

        var line = Assert.Single(_logger.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Same(failure, line.Exception);
        Assert.Equal("Sending failed", line.Message);
    }
}
