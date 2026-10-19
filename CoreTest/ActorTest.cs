using System.Diagnostics;

namespace AVCoders.Core.Tests;

public class ActorTest : IDisposable
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public void InvokeAddEvent(string info) => AddEvent(EventType.Power, info);

        public void DoWork()
        {
            using (PushProperties())
            {
            }
        }
    }

    private static readonly Actor Browser = new(ActorKind.Browser, "10.1.2.3", "Firefox", "LAPTOP-12", "circuit-1");
    private readonly TestLogBase _logBase = new("ActorTest");
    private readonly List<Activity> _started = [];
    private readonly object _startedLock = new();
    private readonly ActivityListener _listener;

    public ActorTest()
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

    private List<Activity> Started()
    {
        lock (_startedLock)
            return _started.ToList();
    }

    [Fact]
    public void ToString_IncludesTheHostName()
    {
        Assert.Equal("Browser 10.1.2.3 (LAPTOP-12)", Browser.ToString());
    }

    [Fact]
    public void ToString_WithoutAHostName_ShowsKindAndIp()
    {
        var mcp = new Actor(ActorKind.Mcp, "10.1.2.4", "claude-code", null, "session-1");

        Assert.Equal("Mcp 10.1.2.4", mcp.ToString());
    }

    [Fact]
    public void AddEvent_UnderAnActor_RecordsTheActor()
    {
        Actor.Current = Browser;

        _logBase.InvokeAddEvent("Connect requested");

        Assert.Equal("Browser 10.1.2.3 (LAPTOP-12)", _logBase.Events[^1].Context[LogBase.ActorProperty]);
    }

    [Fact]
    public void AddEvent_WithoutAnActor_HasNoActor()
    {
        Actor.Current = null;

        _logBase.InvokeAddEvent("Connect requested");

        Assert.False(_logBase.Events[^1].Context.ContainsKey(LogBase.ActorProperty));
    }

    [Fact]
    public void PushProperties_UnderAnActor_TagsTheSpanWithTheActor()
    {
        Actor.Current = Browser;

        _logBase.DoWork();

        var span = Assert.Single(Started(), a => a.OperationName == "TestLogBase.DoWork" && a.GetTagItem("InstanceUid") as string == _logBase.InstanceUid);
        Assert.Equal("Browser 10.1.2.3 (LAPTOP-12)", span.GetTagItem(LogBase.ActorProperty));
    }
}
