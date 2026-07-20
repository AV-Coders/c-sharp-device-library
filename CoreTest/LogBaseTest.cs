using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVCoders.Core.Tests;

public class LogBaseTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public IDisposable InvokePushProperties(string? methodName = null) => PushProperties(methodName);
    }

    // Captures BeginScope state so tests can assert what an emitted log event would carry
    // and that scopes are fully unwound after disposal. LogBase.LoggerFactory is static,
    // so background threads from parallel test classes can log concurrently - all list
    // access is locked.
    private sealed class CapturingLogger : ILogger
    {
        private readonly object _lock = new();
        private readonly List<object> _activeScopes = new();
        private readonly List<Dictionary<string, object>> _events = new();

        public int ActiveScopeCount { get { lock (_lock) return _activeScopes.Count; } }

        public Dictionary<string, object> LastEvent { get { lock (_lock) return _events[^1]; } }

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

    // Only this test class's own TestLogBase captures - every other LogBase constructed
    // while this factory is installed (workers and drivers from parallel test classes)
    // gets a NullLogger, so their scopes cannot pollute or race the assertions.
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
}
