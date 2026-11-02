using System.Diagnostics;

namespace AVCoders.Core.Tests;

public class LogBaseSpanTest : IDisposable
{
    private sealed class Device(string name) : LogBase(name)
    {
        public void DoWork()
        {
            using (PushProperties())
            {
            }
        }
    }

    private readonly List<Activity> _started = [];
    private readonly object _startedLock = new();
    private readonly ActivityListener _listener;

    private List<Activity> Started()
    {
        lock (_startedLock)
            return _started.ToList();
    }

    public LogBaseSpanTest()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LogBase.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (_startedLock)
                    _started.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void PushProperties_CreatesMethodSpan_NestedUnderExternalParent()
    {
        using var parent = LogBase.ActivitySource.StartActivity("ExternalSpan");

        new Device("Projector").DoWork();

        var methodSpan = Assert.Single(Started(), a => a.OperationName == "Device.DoWork");
        Assert.Equal(parent!.SpanId, methodSpan.ParentSpanId);
        Assert.Equal(parent.TraceId, methodSpan.TraceId);
        Assert.Equal("Projector", methodSpan.GetTagItem("InstanceName"));
        Assert.Equal("Device", methodSpan.GetTagItem("Class"));
        Assert.Equal("DoWork", methodSpan.GetTagItem("Method"));
    }

    [Fact]
    public void PushProperties_StartsARootSpan_WhenNoExternalParent()
    {
        new Device("Projector").DoWork();

        Assert.Contains(Started(), a => a.OperationName == "Device.DoWork" && a.Parent == null);
    }
}
