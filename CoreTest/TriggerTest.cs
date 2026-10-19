namespace AVCoders.Core.Tests;

public class TriggerTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public void InvokeAddEvent(string info) => AddEvent(EventType.Power, info);
    }

    private const string IdPattern = "^[0-9a-f]{8}$";
    private const int IdCount = 100;
    private readonly TestLogBase _logBase = new("TriggerTest");

    private static void WithoutATrigger(Action action)
    {
        using (ExecutionContext.SuppressFlow())
            Task.Run(action).GetAwaiter().GetResult();
    }

    [Fact]
    public void Start_GivesEightLowercaseHexCharactersAndMakesThemCurrent()
    {
        var id = Trigger.Start();

        Assert.Matches(IdPattern, id);
        Assert.Equal(id, Trigger.Id);
    }

    [Fact]
    public void Start_GivesADifferentIdEachTime()
    {
        var ids = Enumerable.Range(0, IdCount).Select(_ => Trigger.Start()).ToList();

        Assert.Equal(IdCount, ids.Distinct().Count());
    }

    [Fact]
    public void Resume_RestoresThePreviousIdWhenDisposed()
    {
        var started = Trigger.Start();

        using (Trigger.Resume("0000000a"))
            Assert.Equal("0000000a", Trigger.Id);

        Assert.Equal(started, Trigger.Id);
    }

    [Fact]
    public void Resume_Nested_RestoresEachLevel()
    {
        var started = Trigger.Start();

        using (Trigger.Resume("0000000a"))
        {
            using (Trigger.Resume("0000000b"))
                Assert.Equal("0000000b", Trigger.Id);
            Assert.Equal("0000000a", Trigger.Id);
        }

        Assert.Equal(started, Trigger.Id);
    }

    [Fact]
    public void Resume_Null_KeepsTheCurrentIdAndRestoresItWhenDisposed()
    {
        var started = Trigger.Start();

        using (Trigger.Resume(null))
            Assert.Equal(started, Trigger.Id);

        Assert.Equal(started, Trigger.Id);
    }

    [Fact]
    public void Resume_Null_WithoutATrigger_LeavesNoId()
    {
        WithoutATrigger(() =>
        {
            using (Trigger.Resume(null))
                Assert.Null(Trigger.Id);
            Assert.Null(Trigger.Id);
        });
    }

    [Fact]
    public void Begin_SetsAnEightHexId()
    {
        using (Trigger.Begin())
            Assert.Matches(IdPattern, Trigger.Id!);
    }

    [Fact]
    public void Begin_RestoresThePreviousIdWhenDisposed()
    {
        var started = Trigger.Start();

        using (Trigger.Begin())
            Assert.NotEqual(started, Trigger.Id);

        Assert.Equal(started, Trigger.Id);
    }

    [Fact]
    public void Begin_WithNoPreviousId_RestoresNull()
    {
        WithoutATrigger(() =>
        {
            using (Trigger.Begin())
                Assert.NotNull(Trigger.Id);
            Assert.Null(Trigger.Id);
        });
    }

    [Fact]
    public void Begin_Nested_RestoresEachLevel()
    {
        var started = Trigger.Start();

        using (Trigger.Begin())
        {
            var outer = Trigger.Id;
            using (Trigger.Begin())
                Assert.NotEqual(outer, Trigger.Id);
            Assert.Equal(outer, Trigger.Id);
        }

        Assert.Equal(started, Trigger.Id);
    }

    [Fact]
    public async Task Start_TheIdFlowsIntoTaskRun()
    {
        var id = Trigger.Start();

        var seen = await Task.Run(() => Trigger.Id);

        Assert.Equal(id, seen);
    }

    [Fact]
    public void AddEvent_UnderATrigger_RecordsTheTriggerId()
    {
        var id = Trigger.Start();

        _logBase.InvokeAddEvent("Power on requested");

        Assert.Equal(id, _logBase.Events[^1].Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void AddEvent_WithoutATrigger_HasNoTriggerId()
    {
        WithoutATrigger(() => _logBase.InvokeAddEvent("Power on requested"));

        Assert.False(_logBase.Events[^1].Context.ContainsKey(LogBase.TriggerIdProperty));
    }
}
