namespace AVCoders.Core.Tests;

// The registry is shared static state and every LogBase in every concurrently-running test
// auto-registers, so these tests assert containment / filter by source — never global counts.
// Same collection as IssuesTest: SetIssueLimits mutates every registered instance's cap, which
// would race with that class's cap assertions if they ran in parallel.
[Collection("LogBaseIssues")]
public class LogBaseRegistryTest : IDisposable
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public void Momentary(string message, string? key = null) => RaiseMomentaryIssue(message, key);

        public void Ongoing(string key, string message) => RaiseOngoingIssue(key, message);

        public void Resolve(string key) => ResolveIssue(key);
    }

    private readonly TestLogBase _logBase = new("RegistryTest");

    public void Dispose() => LogBaseRegistry.Deregister(_logBase);

    [Fact]
    public void Constructor_AutoRegisters_InstanceAppearsInGetAll()
    {
        Assert.Contains(_logBase, LogBaseRegistry.GetAll());
    }

    [Fact]
    public void Deregister_RemovesInstance()
    {
        var instance = new TestLogBase("Transient");

        LogBaseRegistry.Deregister(instance);

        Assert.DoesNotContain(instance, LogBaseRegistry.GetAll());
    }

    [Fact]
    public void GetOngoingIssues_AggregatesOngoingIssues_ExcludingResolvedAndMomentary()
    {
        _logBase.Ongoing("communication", "Comms error");
        _logBase.Ongoing("input", "Wrong input");
        _logBase.Resolve("input");
        _logBase.Momentary("Missed a poll", key: "poll");

        var mine = LogBaseRegistry.GetOngoingIssues().Where(s => s.Source == _logBase).ToList();

        var sourced = Assert.Single(mine);
        Assert.Equal("communication", sourced.Issue.Key);
        Assert.Equal(IssueStatus.Ongoing, sourced.Issue.Status);
    }

    [Fact]
    public void OngoingIssuesChanged_FiresWhenAnInstanceRaisesOngoing_WithAggregateArgs()
    {
        var firings = new List<(object? Sender, OngoingIssuesChangedEventArgs Args)>();
        EventHandler<OngoingIssuesChangedEventArgs> handler = (sender, e) => firings.Add((sender, e));
        LogBaseRegistry.OngoingIssuesChanged += handler;
        try
        {
            _logBase.Ongoing("communication", "Comms error");
        }
        finally
        {
            LogBaseRegistry.OngoingIssuesChanged -= handler;
        }

        var firing = Assert.Single(firings, f => ReferenceEquals(f.Sender, _logBase));
        Assert.Contains(firing.Args.OngoingIssues,
            s => s.Source == _logBase && s.Issue.Key == "communication");
    }

    [Fact]
    public void OngoingIssuesChanged_DoesNotFireForMomentaryOnlyChanges()
    {
        var firings = new List<object?>();
        EventHandler<OngoingIssuesChangedEventArgs> handler = (sender, _) => firings.Add(sender);
        LogBaseRegistry.OngoingIssuesChanged += handler;
        try
        {
            _logBase.Momentary("Missed a poll", key: "poll");
            _logBase.Momentary("Missed a poll", key: "poll");
        }
        finally
        {
            LogBaseRegistry.OngoingIssuesChanged -= handler;
        }

        Assert.DoesNotContain(firings, sender => ReferenceEquals(sender, _logBase));
    }

    [Fact]
    public void OngoingIssuesChanged_FiresOnResolve()
    {
        _logBase.Ongoing("communication", "Comms error");

        var firings = new List<object?>();
        EventHandler<OngoingIssuesChangedEventArgs> handler = (sender, _) => firings.Add(sender);
        LogBaseRegistry.OngoingIssuesChanged += handler;
        try
        {
            _logBase.Resolve("communication");
        }
        finally
        {
            LogBaseRegistry.OngoingIssuesChanged -= handler;
        }

        Assert.Contains(firings, sender => ReferenceEquals(sender, _logBase));
        Assert.DoesNotContain(LogBaseRegistry.GetOngoingIssues(), s => s.Source == _logBase);
    }

    [Fact]
    public void Deregister_StopsEventForwarding()
    {
        var instance = new TestLogBase("Transient");
        LogBaseRegistry.Deregister(instance);

        var firings = new List<object?>();
        EventHandler<OngoingIssuesChangedEventArgs> handler = (sender, _) => firings.Add(sender);
        LogBaseRegistry.OngoingIssuesChanged += handler;
        try
        {
            instance.Ongoing("communication", "Comms error");
        }
        finally
        {
            LogBaseRegistry.OngoingIssuesChanged -= handler;
        }

        Assert.DoesNotContain(firings, sender => ReferenceEquals(sender, instance));
    }

    [Fact]
    public void SetIssueLimits_FansOutToInstances()
    {
        _logBase.Momentary("first", key: "first");
        _logBase.Momentary("second", key: "second");

        LogBaseRegistry.SetIssueLimits(1);
        try
        {
            Assert.Single(_logBase.Issues);
        }
        finally
        {
            LogBaseRegistry.SetIssueLimits(50);
        }
    }
}
