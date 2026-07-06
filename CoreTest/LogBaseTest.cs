using Microsoft.Extensions.Logging;

namespace AVCoders.Core.Tests;

public class LogBaseTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public IDisposable InvokePushProperties(string? methodName = null) => PushProperties(methodName);
    }

    // Captures BeginScope state so tests can assert what an emitted log event would carry
    // and that scopes are fully unwound after disposal.
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<object> ActiveScopes = new();
        public readonly List<Dictionary<string, object>> Events = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            ActiveScopes.Add(state);
            return new Scope(this, state);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object>();
            foreach (var scope in ActiveScopes)
            {
                if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                    foreach (var pair in pairs)
                        properties[pair.Key] = pair.Value;
            }
            Events.Add(properties);
        }

        private sealed class Scope(CapturingLogger logger, object state) : IDisposable
        {
            public void Dispose() => logger.ActiveScopes.Remove(state);
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
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
        return _logger.Events[^1];
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
        int baseline = _logger.ActiveScopes.Count;

        for (int i = 0; i < 500; i++)
        {
            using (_logBase.InvokePushProperties("TestMethod"))
            {
            }
        }

        Assert.Equal(baseline, _logger.ActiveScopes.Count);
        Assert.Empty(LogAndCapture());
    }
}
