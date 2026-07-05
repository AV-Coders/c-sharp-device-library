using System.Collections;
using System.Reflection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace AVCoders.Core.Tests;

public class LogBaseTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public IDisposable InvokePushProperties(string? methodName = null) => PushProperties(methodName);
    }

    private class CapturingSink : ILogEventSink
    {
        public readonly List<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private readonly TestLogBase _logBase = new("Test Instance");
    private readonly CapturingSink _sink = new();
    private readonly Logger _logger;

    public LogBaseTest()
    {
        _logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    private LogEvent LogAndCapture()
    {
        _logger.Information("Canary");
        return _sink.Events[^1];
    }

    [Fact]
    public void PushProperties_AddsPropertiesToTheLogContext()
    {
        using (_logBase.InvokePushProperties("TestMethod"))
        {
            var logEvent = LogAndCapture();

            Assert.True(logEvent.Properties.ContainsKey("InstanceUid"));
            Assert.True(logEvent.Properties.ContainsKey("Class"));
            Assert.True(logEvent.Properties.ContainsKey("InstanceName"));
            Assert.True(logEvent.Properties.ContainsKey("Method"));
        }
    }

    [Fact]
    public void PushProperties_Dispose_RemovesAllPropertiesFromTheLogContext()
    {
        using (_logBase.InvokePushProperties("TestMethod"))
        {
        }

        var logEvent = LogAndCapture();

        Assert.False(logEvent.Properties.ContainsKey("InstanceUid"), "InstanceUid was left on the LogContext after disposal");
        Assert.False(logEvent.Properties.ContainsKey("Class"), "Class was left on the LogContext after disposal");
        Assert.False(logEvent.Properties.ContainsKey("InstanceName"), "InstanceName was left on the LogContext after disposal");
        Assert.False(logEvent.Properties.ContainsKey("Method"), "Method was left on the LogContext after disposal");
    }

    [Fact]
    public void PushProperties_Dispose_DoesNotAccumulateEnrichersAcrossRepeatedCalls()
    {
        for (int i = 0; i < 500; i++)
        {
            using (_logBase.InvokePushProperties("TestMethod"))
            {
            }
        }

        var logEvent = LogAndCapture();

        Assert.Empty(logEvent.Properties);
    }

    [Fact]
    public void PushProperties_Dispose_LeavesNoEnrichersOnTheLogContextStack()
    {
        int baseline = CurrentEnricherStackDepth();

        for (int i = 0; i < 100; i++)
        {
            using (_logBase.InvokePushProperties("TestMethod"))
            {
            }
        }

        int depth = CurrentEnricherStackDepth();

        Assert.Equal(baseline, depth);
    }

    // The event Properties dictionary is keyed by name, so leaked enrichers collide onto the same keys
    // and their true count is invisible to the tests above. This counts the live enrichers on the
    // ambient LogContext stack itself, so the test failure shows the scale of the accumulation.
    private static int CurrentEnricherStackDepth()
    {
        var clone = LogContext.Clone();
        var stackField = clone.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType.Name.Contains("EnricherStack"));
        if (stackField != null)
            return CountItems(stackField.GetValue(clone));

        var enrichersProperty = typeof(LogContext)
            .GetProperty("Enrichers", BindingFlags.Static | BindingFlags.NonPublic);
        if (enrichersProperty != null)
            return CountItems(enrichersProperty.GetValue(null));

        throw new InvalidOperationException(
            "Could not locate the LogContext enricher stack via reflection - Serilog internals may have changed.");
    }

    private static int CountItems(object? stack)
    {
        if (stack == null)
            return 0;
        int count = 0;
        foreach (var _ in (IEnumerable)stack)
            count++;
        return count;
    }
}
