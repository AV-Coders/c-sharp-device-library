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
    public void ToString_ForASchedule_ShowsKindAndName()
    {
        Assert.Equal("Schedule DailyShutdown", Actor.Schedule("DailyShutdown").ToString());
    }

    [Fact]
    public void ToString_ForTheSystem_ShowsKindAndName()
    {
        Assert.Equal("System AvCodersTcpClient.Receive (Projector)", Actor.System("AvCodersTcpClient.Receive (Projector)").ToString());
    }

    [Fact]
    public void ToString_ForAConsoleCommand_ShowsKindAndCommand()
    {
        Assert.Equal("Console dumpheap", Actor.Console("dumpheap").ToString());
    }

    [Fact]
    public void ToString_ForAPeer_ShowsKindIpAndHostName()
    {
        Assert.Equal("Peer 10.1.2.6 (RWR-CP4)", Actor.Peer("10.1.2.6", "RWR-CP4", "client-1").ToString());
    }

    [Theory]
    [InlineData(ActorKind.Browser, true)]
    [InlineData(ActorKind.Mcp, true)]
    [InlineData(ActorKind.Panel, true)]
    [InlineData(ActorKind.Console, true)]
    [InlineData(ActorKind.Peer, true)]
    [InlineData(ActorKind.Schedule, false)]
    [InlineData(ActorKind.System, false)]
    public void IsPerson_IsTrueForPeopleAndWhatTheyRelay(ActorKind kind, bool isPerson)
    {
        Assert.Equal(isPerson, new Actor(kind, string.Empty, string.Empty, null, string.Empty).IsPerson);
    }

    [Fact]
    public void Begin_SetsTheActorUntilDisposed()
    {
        Actor.Current = Browser;
        var schedule = Actor.Schedule("DailyShutdown");

        using (Actor.Begin(schedule))
            Assert.Equal(schedule, Actor.Current);

        Assert.Equal(Browser, Actor.Current);
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
