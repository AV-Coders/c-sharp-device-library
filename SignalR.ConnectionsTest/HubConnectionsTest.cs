using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Connections.Tests;

public class HubConnectionsTest
{
    private const string Room = "L1-01";
    private const string OtherRoom = "L1-02";
    private const string FirstConnection = "conn-a";
    private const string SecondConnection = "conn-b";
    private static readonly TimeSpan CrossThreadReadTimeout = TimeSpan.FromSeconds(2);
    private readonly HubConnections _connections = new("TestHub");
    private readonly List<HubConnectionsSnapshot> _changes = [];

    public HubConnectionsTest()
    {
        _connections.Changed += (_, _) => _changes.Add(_connections.GetSnapshot());
    }

    private Issue? UnknownGroupIssue(string group) =>
        _connections.GetIssues().LastOrDefault(issue => issue.Key == HubConnections.UnknownGroupIssuePrefix + group);

    [Fact]
    public void Open_CountsAnOpenConnectionInNoGroup()
    {
        _connections.Open(FirstConnection);

        var snapshot = _connections.GetSnapshot();
        Assert.Equal(1, snapshot.OpenConnections);
        Assert.Empty(snapshot.Groups);
        Assert.Equal(0, _connections.Count(Room));
    }

    [Fact]
    public void Join_CountsTheConnectionInItsGroup()
    {
        _connections.Open(FirstConnection);
        _connections.Open(SecondConnection);

        _connections.Join(FirstConnection, Room);
        _connections.Join(SecondConnection, Room);

        Assert.Equal(2, _connections.Count(Room));
        Assert.Equal(0, _connections.Count(OtherRoom));
    }

    [Fact]
    public void Join_TheSameGroupTwice_CountsTheConnectionOnce()
    {
        _connections.Open(FirstConnection);

        _connections.Join(FirstConnection, Room);
        _connections.Join(FirstConnection, Room);

        Assert.Equal(1, _connections.Count(Room));
    }

    [Fact]
    public void Join_TwoGroups_CountsTheConnectionInBoth()
    {
        _connections.Open(FirstConnection);

        _connections.Join(FirstConnection, Room);
        _connections.Join(FirstConnection, OtherRoom);

        Assert.Equal(1, _connections.Count(Room));
        Assert.Equal(1, _connections.Count(OtherRoom));
    }

    [Fact]
    public void Join_AConnectionThatWasNeverOpened_IsNotCounted()
    {
        _connections.Join(FirstConnection, Room);

        Assert.Equal(0, _connections.Count(Room));
        Assert.Equal(0, _connections.GetSnapshot().OpenConnections);
        Assert.Empty(_changes);
    }

    [Fact]
    public void Join_AfterTheConnectionClosed_DoesNotBringItBack()
    {
        _connections.Open(FirstConnection);
        _connections.Close(FirstConnection);

        _connections.Join(FirstConnection, Room);

        Assert.Equal(0, _connections.Count(Room));
        Assert.Equal(0, _connections.GetSnapshot().OpenConnections);
        Assert.Empty(_connections.GetOngoingIssues());
    }

    [Fact]
    public void Close_RemovesTheConnectionFromEveryGroup()
    {
        _connections.Open(FirstConnection);
        _connections.Join(FirstConnection, Room);
        _connections.Join(FirstConnection, OtherRoom);

        _connections.Close(FirstConnection);

        Assert.Equal(0, _connections.Count(Room));
        Assert.Equal(0, _connections.Count(OtherRoom));
        Assert.Equal(0, _connections.GetSnapshot().OpenConnections);
    }

    [Fact]
    public void Close_AConnectionThatIsNotOpen_ChangesNothing()
    {
        _connections.Close(FirstConnection, new IOException("Connection reset"));

        Assert.Empty(_changes);
    }

    [Fact]
    public void Changed_FiresAfterOpenJoinAndClose()
    {
        _connections.Register(Room);
        _changes.Clear();

        _connections.Open(FirstConnection);
        _connections.Join(FirstConnection, Room);
        _connections.Close(FirstConnection);

        Assert.Equal([1, 1, 0], _changes.Select(change => change.OpenConnections));
        Assert.Equal([0, 1, 0], _changes.Select(change => change.Groups.Single(group => group.Group == Room).Connections));
        Assert.All(_changes, change => Assert.Equal("TestHub", change.Hub));
    }

    [Fact]
    public void Changed_AHandlerThatThrows_StillRunsTheOtherHandlers()
    {
        var connections = new HubConnections("TestHub");
        var secondRan = false;
        connections.Changed += (_, _) => throw new InvalidOperationException("Health page failed");
        connections.Changed += (_, _) => secondRan = true;

        connections.Open(FirstConnection);

        Assert.True(secondRan);
        Assert.Single(connections.Errors, error => error.Exception is InvalidOperationException);
    }

    [Fact]
    public void Register_ListsTheGroupWithNoConnections()
    {
        _connections.Register(Room);

        Assert.Equal([new GroupConnections(Room, true, 0)], _connections.GetSnapshot().Groups);
    }

    [Fact]
    public void Register_TheSameGroupTwice_ChangesOnce()
    {
        _connections.Register(Room);
        _connections.Register(Room);

        Assert.Single(_changes);
    }

    [Fact]
    public void GetSnapshot_ListsAJoinedGroupThatIsNotRegisteredAsUnregistered()
    {
        _connections.Register(Room);
        _connections.Open(FirstConnection);
        _connections.Join(FirstConnection, OtherRoom);

        Assert.Equal([new GroupConnections(Room, true, 0), new GroupConnections(OtherRoom, false, 1)],
            _connections.GetSnapshot().Groups);
    }

    [Fact]
    public void GetSnapshot_ListsGroupsInOrdinalOrder()
    {
        _connections.Register("b");
        _connections.Register("B");
        _connections.Register("a");

        Assert.Equal(["B", "a", "b"], _connections.GetSnapshot().Groups.Select(group => group.Group));
    }

    [Fact]
    public void ReportUnknownGroup_RaisesAnOngoingIssueWithTheRawValues()
    {
        _connections.Register(Room);
        _connections.Open(FirstConnection);

        _connections.ReportUnknownGroup(FirstConnection, "PowerOn", "L1-1");

        var issue = Assert.Single(_connections.GetOngoingIssues());
        Assert.Equal("unknown-group:L1-1", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Equal("PowerOn from connection conn-a asked for group 'L1-1', which has nothing registered. Registered groups: L1-01",
            issue.Message);
    }

    [Fact]
    public void ReportUnknownGroup_ListsTheRegisteredGroupsInOrder()
    {
        _connections.Register(OtherRoom);
        _connections.Register(Room);
        _connections.Open(FirstConnection);

        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", "L1-1");

        Assert.EndsWith("Registered groups: L1-01, L1-02", UnknownGroupIssue("L1-1")!.Message);
    }

    [Fact]
    public void ReportUnknownGroup_WithNothingRegistered_SaysNone()
    {
        _connections.Open(FirstConnection);

        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", Room);

        Assert.EndsWith("Registered groups: none", UnknownGroupIssue(Room)!.Message);
    }

    [Fact]
    public void ReportUnknownGroup_KeepsTheGroupExactlyAsReceived()
    {
        const string garbage = " l1-01\t\u0002";
        _connections.Open(FirstConnection);

        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", garbage);

        Assert.Equal(garbage, Assert.Single(_connections.GetSnapshot().UnknownGroupRequests).Group);
        Assert.Contains($"'{garbage}'", UnknownGroupIssue(garbage)!.Message);
    }

    [Fact]
    public void ReportUnknownGroup_RecordsTheRequestInTheSnapshot()
    {
        var before = DateTimeOffset.UtcNow;
        _connections.Open(FirstConnection);
        _changes.Clear();

        _connections.ReportUnknownGroup(FirstConnection, "SelectSource", "L1-1");

        var request = Assert.Single(_connections.GetSnapshot().UnknownGroupRequests);
        Assert.Equal(("TestHub", "SelectSource", FirstConnection, "L1-1"), (request.Hub, request.Method, request.ConnectionId, request.Group));
        Assert.InRange(request.At, before, DateTimeOffset.UtcNow);
        Assert.Single(_changes);
    }

    [Fact]
    public void ReportUnknownGroup_KeepsOnlyTheLatestRequests()
    {
        _connections.Open(FirstConnection);

        for (var request = 0; request < HubConnections.UnknownGroupRequestLimit + 5; request++)
            _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", $"room-{request}");

        var requests = _connections.GetSnapshot().UnknownGroupRequests;
        Assert.Equal(HubConnections.UnknownGroupRequestLimit, requests.Count);
        Assert.Equal("room-5", requests[0].Group);
        Assert.Equal($"room-{HubConnections.UnknownGroupRequestLimit + 4}", requests[^1].Group);
    }

    [Fact]
    public void ReportUnknownGroup_AfterTheConnectionClosed_RecordsTheRequestButRaisesNoIssue()
    {
        _connections.Open(FirstConnection);
        _connections.Close(FirstConnection);

        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", "L1-1");

        Assert.Single(_connections.GetSnapshot().UnknownGroupRequests, request => request.Group == "L1-1");
        Assert.Equal(0, _connections.GetSnapshot().OpenConnections);
        Assert.Null(UnknownGroupIssue("L1-1"));
    }

    [Fact]
    public void ReportUnknownGroup_ForAGroupRegisteredMeanwhile_RaisesNoIssue()
    {
        _connections.Register(Room);
        _connections.Open(FirstConnection);

        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", Room);

        Assert.Null(UnknownGroupIssue(Room));
    }

    [Fact]
    public void ReportUnknownGroup_LogsThroughTheLoggerFactorySetAfterTheTrackerWasCreated()
    {
        var connections = new HubConnections("SourceHub");
        var loggerFactory = new ScopeCapturingLoggerFactory();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = loggerFactory;
        try
        {
            connections.ReportUnknownGroup(FirstConnection, "JoinGroup", "L1-1");

            var log = Assert.Single(loggerFactory.Logs, l => l.Message.Contains("asked SourceHub for group"));
            Assert.Equal(LogLevel.Warning, log.Level);
            Assert.Equal("JoinGroup from connection conn-a asked SourceHub for group L1-1, which has nothing registered. Registered groups: none",
                log.Message);
            Assert.Equal("SourceHub", log.Scope["InstanceName"]);
            Assert.Equal(nameof(HubConnections.ReportUnknownGroup), log.Scope[LogBase.MethodProperty]);
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }

    [Fact]
    public void Close_TheLastConnectionAskingForAnUnknownGroup_ResolvesTheIssue()
    {
        _connections.Open(FirstConnection);
        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", "L1-1");

        _connections.Close(FirstConnection);

        Assert.Equal(IssueStatus.Resolved, UnknownGroupIssue("L1-1")!.Status);
        Assert.Empty(_connections.GetOngoingIssues());
    }

    [Fact]
    public void Close_WhileAnotherConnectionStillAsks_KeepsTheIssue()
    {
        _connections.Open(FirstConnection);
        _connections.Open(SecondConnection);
        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", "L1-1");
        _connections.ReportUnknownGroup(SecondConnection, "JoinGroup", "L1-1");

        _connections.Close(FirstConnection);

        Assert.Equal(IssueStatus.Ongoing, UnknownGroupIssue("L1-1")!.Status);
    }

    [Fact]
    public void Register_AGroupThatWasAskedFor_ResolvesTheIssue()
    {
        _connections.Open(FirstConnection);
        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", Room);

        _connections.Register(Room);

        Assert.Equal(IssueStatus.Resolved, UnknownGroupIssue(Room)!.Status);
    }

    [Fact]
    public void Register_ThenClose_DoesNotRaiseTheIssueAgain()
    {
        _connections.Open(FirstConnection);
        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", Room);
        _connections.Register(Room);

        _connections.Close(FirstConnection);

        Assert.Single(_connections.GetIssues());
        Assert.Empty(_connections.GetOngoingIssues());
    }

    [Fact]
    public void IssuesChanged_HandlersCanReadTheTrackerFromAnotherThread()
    {
        var reads = new List<bool>();
        _connections.IssuesChanged += (_, _) =>
            reads.Add(Task.Run(() => _connections.GetSnapshot()).Wait(CrossThreadReadTimeout));
        _connections.Open(FirstConnection);
        _connections.Open(SecondConnection);

        _connections.ReportUnknownGroup(FirstConnection, "JoinGroup", Room);
        _connections.Close(FirstConnection);
        _connections.ReportUnknownGroup(SecondConnection, "JoinGroup", OtherRoom);
        _connections.Register(OtherRoom);

        Assert.Equal([true, true, true, true], reads);
    }

    [Fact]
    public void HubConnections_IsInTheLogBaseRegistry()
    {
        Assert.Contains(_connections, LogBaseRegistry.GetAll());
    }
}
