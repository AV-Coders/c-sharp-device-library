using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Connections.Tests;

[Collection(nameof(TestHub))]
public class GroupHubTest
{
    private readonly string _connectionId = $"conn-{Guid.NewGuid()}";
    private readonly string _groupName = $"group-{Guid.NewGuid()}";
    private readonly Mock<ITestHub> _caller = new();
    private readonly Mock<IHubCallerClients<ITestHub>> _clients = new();
    private readonly Mock<IGroupManager> _groups = new();
    private readonly Mock<HubCallerContext> _context = new();
    private readonly TestHub _hub;

    public GroupHubTest()
    {
        _clients.Setup(c => c.Caller).Returns(_caller.Object);
        _context.Setup(c => c.ConnectionId).Returns(_connectionId);
        _hub = new TestHub { Clients = _clients.Object, Groups = _groups.Object, Context = _context.Object };
    }

    [Fact]
    public async Task OnConnectedAsync_OpensTheConnection()
    {
        var before = TestHub.Connections.GetSnapshot().OpenConnections;

        await _hub.OnConnectedAsync();

        Assert.Equal(before + 1, TestHub.Connections.GetSnapshot().OpenConnections);
    }

    [Fact]
    public async Task OnDisconnectedAsync_ClosesTheConnection()
    {
        await _hub.OnConnectedAsync();
        var before = TestHub.Connections.GetSnapshot().OpenConnections;

        await _hub.OnDisconnectedAsync(null);

        Assert.Equal(before - 1, TestHub.Connections.GetSnapshot().OpenConnections);
    }

    [Fact]
    public async Task AddToGroup_JoinsTheSignalRGroupAndCountsTheConnection()
    {
        TestHub.RegisterGroup(_groupName);
        await _hub.OnConnectedAsync();

        await _hub.JoinGroup(_groupName);

        _groups.Verify(g => g.AddToGroupAsync(_connectionId, _groupName, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, TestHub.Connections.Count(_groupName));
        _caller.Verify(c => c.Joined(_groupName), Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_RemovesTheConnectionFromItsGroup()
    {
        TestHub.RegisterGroup(_groupName);
        await _hub.OnConnectedAsync();
        await _hub.JoinGroup(_groupName);

        await _hub.OnDisconnectedAsync(new IOException("Connection reset"));

        Assert.Equal(0, TestHub.Connections.Count(_groupName));
    }

    [Fact]
    public async Task ReportUnknownGroup_AfterJoining_ReportsTheCallingMethodAndReturnsNormally()
    {
        await _hub.OnConnectedAsync();

        await _hub.JoinGroup(_groupName);

        _groups.Verify(g => g.AddToGroupAsync(_connectionId, _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _caller.Verify(c => c.Joined(It.IsAny<string>()), Times.Never);
        Assert.Equal(1, TestHub.Connections.Count(_groupName));
        var request = TestHub.Connections.GetSnapshot().UnknownGroupRequests.Last(r => r.Group == _groupName);
        Assert.Equal(("JoinGroup", _connectionId), (request.Method, request.ConnectionId));
    }

    [Fact]
    public void ReportUnknownGroup_FromACommand_NamesTheCommand()
    {
        _hub.PowerOn(_groupName);

        var request = TestHub.Connections.GetSnapshot().UnknownGroupRequests.Last(r => r.Group == _groupName);
        Assert.Equal("PowerOn", request.Method);
    }

    [Fact]
    public async Task JoinGroup_AfterTheConnectionClosed_IsNotCountedAndRaisesNoIssue()
    {
        TestHub.RegisterGroup(_groupName);
        var before = TestHub.Connections.GetSnapshot().OpenConnections;
        await _hub.OnConnectedAsync();
        await _hub.OnDisconnectedAsync(null);

        await _hub.JoinGroup(_groupName);

        Assert.Equal(0, TestHub.Connections.Count(_groupName));
        Assert.Equal(before, TestHub.Connections.GetSnapshot().OpenConnections);
        Assert.DoesNotContain(TestHub.Connections.GetOngoingIssues(), issue => issue.Key == HubConnections.UnknownGroupIssuePrefix + _groupName);
    }

    [Fact]
    public async Task ReportUnknownGroup_AfterTheConnectionClosed_IsRecordedButRaisesNoIssue()
    {
        var before = TestHub.Connections.GetSnapshot().OpenConnections;
        await _hub.OnConnectedAsync();
        await _hub.OnDisconnectedAsync(null);

        await _hub.JoinGroup(_groupName);

        Assert.Equal(0, TestHub.Connections.Count(_groupName));
        Assert.Equal(before, TestHub.Connections.GetSnapshot().OpenConnections);
        Assert.DoesNotContain(TestHub.Connections.GetOngoingIssues(), issue => issue.Key == HubConnections.UnknownGroupIssuePrefix + _groupName);
        Assert.Contains(TestHub.Connections.GetSnapshot().UnknownGroupRequests, request => request.Group == _groupName);
    }
}
