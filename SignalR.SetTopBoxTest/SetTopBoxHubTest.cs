using AVCoders.SignalR.Connections;
using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;

namespace AVCoders.SignalR.SetTopBox.Tests;

public class SetTopBoxHubTest
{
    private readonly TestSetTopBox _device;
    private readonly SetTopBoxManager _manager;
    private readonly string _groupName;
    private readonly SetTopBoxHubTestHarness _harness;

    public SetTopBoxHubTest()
    {
        _groupName = $"hub-stb-{Guid.NewGuid()}";
        _device = new TestSetTopBox();
        _manager = new SetTopBoxManager(_groupName, _device, "AppleTv");
        SetTopBoxHub.RegisterSetTopBoxManager(_groupName, _manager);
        _harness = SetTopBoxHubTestHarness.CreateHub();
    }

    public static TheoryData<string, Func<SetTopBoxHub, string, Task>, Func<TestSetTopBox, bool>> Commands => new()
    {
        { "PowerOn", (hub, group) => hub.PowerOn(group), device => device.PowerOnCallCount == 1 && device.PowerOffCallCount == 0 },
        { "PowerOff", (hub, group) => hub.PowerOff(group), device => device.PowerOffCallCount == 1 && device.PowerOnCallCount == 0 },
    };

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsState()
    {
        _device.SetPowerStateForTest(PowerState.On);
        _device.SetCommunicationStateForTest(CommunicationState.Okay);

        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSetTopBox(It.Is<SetTopBoxState>(s =>
            s.Name == _groupName
            && s.SourceId == "AppleTv"
            && s.PowerState == PowerState.On
            && s.CommunicationState == CommunicationState.Okay)), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_StillJoinsButSendsNoState()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.JoinGroup(unknown);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), unknown, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSetTopBox(It.IsAny<SetTopBoxState>()), Times.Never);
    }

    [Fact]
    public async Task GetState_ReturnsManagerState()
    {
        _device.SetPowerStateForTest(PowerState.Off);

        var state = await _harness.Hub.GetState(_groupName);

        Assert.NotNull(state);
        Assert.Equal(_groupName, state.Name);
        Assert.Equal("AppleTv", state.SourceId);
        Assert.Equal(PowerState.Off, state.PowerState);
        Assert.Equal(TestSetTopBox.DefaultSupportedButtons.Select(b => b.ToString()), state.SupportedButtons);
    }

    [Fact]
    public async Task GetState_UnknownGroup_ReturnsNull()
    {
        var state = await _harness.Hub.GetState($"missing-{Guid.NewGuid()}");

        Assert.Null(state);
    }

    [Theory]
    [InlineData("Up", RemoteButton.Up)]
    [InlineData("ENTER", RemoteButton.Enter)]
    [InlineData("powerOn", RemoteButton.PowerOn)]
    public async Task SendRemoteButton_ParsesNameCaseInsensitivelyAndForwardsToDevice(string name, RemoteButton expected)
    {
        await _harness.Hub.SendRemoteButton(_groupName, name);

        await Wait.UntilAsync(() => _device.SentButtons.Count == 1, $"{name} did not reach the set-top box");
        Assert.Equal([expected], _device.SentButtons);
    }

    [Fact]
    public async Task SendRemoteButton_UnknownButton_DoesNotThrowAndDoesNotSend()
    {
        await _harness.Hub.SendRemoteButton(_groupName, "NotAButton");

        AssertNothingSent();
    }

    [Fact]
    public async Task SendRemoteButton_UnknownButton_WarnsInsideTheHubScope()
    {
        var loggerFactory = new ScopeCapturingLoggerFactory();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = loggerFactory;
        try
        {
            var actor = new Actor(ActorKind.Panel, "10.0.0.21", string.Empty, null, $"conn-{Guid.NewGuid()}");
            Actor.Current = actor;
            var triggerId = Trigger.Start();

            await _harness.Hub.SendRemoteButton(_groupName, "NotAButton");

            var log = Assert.Single(loggerFactory.Logs, l => l.Message == $"Unknown remote button NotAButton for group {_groupName}");
            Assert.Equal(nameof(SetTopBoxHub.SendRemoteButton), log.Scope[LogBase.MethodProperty]);
            Assert.Equal(actor.ToString(), log.Scope[LogBase.ActorProperty]);
            Assert.Equal(triggerId, log.Scope[LogBase.TriggerIdProperty]);
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }

    [Fact]
    public async Task SendRemoteButton_EmptyButton_DoesNotThrowAndDoesNotSend()
    {
        await _harness.Hub.SendRemoteButton(_groupName, string.Empty);

        AssertNothingSent();
    }

    [Fact]
    public async Task SendRemoteButton_UnsupportedButton_DoesNotSend()
    {
        Assert.DoesNotContain(RemoteButton.Guide, _device.SupportedButtons);

        await _harness.Hub.SendRemoteButton(_groupName, "Guide");

        AssertNothingSent();
    }

    [Fact]
    public async Task SendRemoteButton_UnknownGroup_DoesNotSendToOtherManagers()
    {
        await _harness.Hub.SendRemoteButton($"missing-{Guid.NewGuid()}", "Up");

        AssertNothingSent();
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_ReachesTheDevice(string command, Func<SetTopBoxHub, string, Task> send, Func<TestSetTopBox, bool> received)
    {
        await send(_harness.Hub, _groupName);

        await Wait.UntilAsync(() => received(_device), $"{command} did not reach the set-top box");
    }

    [Fact]
    public async Task RegisterSetTopBoxManager_ReplacesExistingRegistration()
    {
        var replacementDevice = new TestSetTopBox();
        var replacement = new SetTopBoxManager(_groupName, replacementDevice, "AppleTv");
        SetTopBoxHub.RegisterSetTopBoxManager(_groupName, replacement);

        await _harness.Hub.SendRemoteButton(_groupName, "Down");

        await Wait.UntilAsync(() => replacementDevice.SentButtons.Count == 1, "The button did not reach the replacement set-top box");
        Assert.Empty(_device.SentButtons);
        Assert.Equal([RemoteButton.Down], replacementDevice.SentButtons);
    }

    private void AssertNothingSent()
    {
        Assert.False(SpinWait.SpinUntil(() => _device.SentButtons.Count > 0, 200));
        Assert.Empty(_device.SentButtons);
    }

    [Fact]
    public async Task JoinGroup_CountsTheConnectionInTheGroup()
    {
        await _harness.Hub.OnConnectedAsync();
        await _harness.Hub.JoinGroup(_groupName);

        Assert.Equal(1, SetTopBoxHub.Connections.Count(_groupName));
    }

    [Fact]
    public async Task OnDisconnectedAsync_RemovesTheConnectionFromTheGroup()
    {
        await _harness.Hub.OnConnectedAsync();
        await _harness.Hub.JoinGroup(_groupName);

        await _harness.Hub.OnDisconnectedAsync(null);

        Assert.Equal(0, SetTopBoxHub.Connections.Count(_groupName));
    }

    [Fact]
    public void RegisterSetTopBoxManager_ListsTheGroupInConnections()
    {
        Assert.Contains(new GroupConnections(_groupName, true, 0), SetTopBoxHub.Connections.GetSnapshot().Groups);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_IsReported()
    {
        var unknown = $"missing-{Guid.NewGuid()}";
        await _harness.Hub.OnConnectedAsync();

        await _harness.Hub.JoinGroup(unknown);

        var request = Assert.Single(SetTopBoxHub.Connections.GetSnapshot().UnknownGroupRequests, r => r.Group == unknown);
        Assert.Equal(("SetTopBoxHub", "JoinGroup", _harness.ContextMock.Object.ConnectionId), (request.Hub, request.Method, request.ConnectionId));
        var issue = Assert.Single(SetTopBoxHub.Connections.GetOngoingIssues(), i => i.Key == HubConnections.UnknownGroupIssuePrefix + unknown);
        Assert.StartsWith($"JoinGroup from connection {request.ConnectionId} asked for group '{unknown}', which has nothing registered.", issue.Message);
        Assert.Equal(1, SetTopBoxHub.Connections.Count(unknown));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_UnknownGroup_IsReportedUnderItsName(string command, Func<SetTopBoxHub, string, Task> send, Func<TestSetTopBox, bool> _)
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await send(_harness.Hub, unknown);

        Assert.Equal(0, _device.PowerOnCallCount + _device.PowerOffCallCount);
        Assert.Equal(command, Assert.Single(SetTopBoxHub.Connections.GetSnapshot().UnknownGroupRequests, r => r.Group == unknown).Method);
    }

    [Fact]
    public async Task SendRemoteButton_UnknownGroup_IsReported()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.SendRemoteButton(unknown, "Up");

        Assert.Equal(nameof(SetTopBoxHub.SendRemoteButton), Assert.Single(SetTopBoxHub.Connections.GetSnapshot().UnknownGroupRequests, r => r.Group == unknown).Method);
    }

    [Fact]
    public async Task GetState_UnknownGroup_IsReported()
    {
        var unknown = $"missing-{Guid.NewGuid()}";
        await _harness.Hub.OnConnectedAsync();

        await _harness.Hub.GetState(unknown);

        Assert.Equal(nameof(SetTopBoxHub.GetState), Assert.Single(SetTopBoxHub.Connections.GetSnapshot().UnknownGroupRequests, r => r.Group == unknown).Method);
        Assert.Contains(SetTopBoxHub.Connections.GetOngoingIssues(), i => i.Key == HubConnections.UnknownGroupIssuePrefix + unknown);
    }

    [Fact]
    public async Task PowerOn_UnknownGroup_StillLogsTheIgnoredWarning()
    {
        var loggerFactory = new ScopeCapturingLoggerFactory();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = loggerFactory;
        try
        {
            var unknown = $"missing-{Guid.NewGuid()}";

            await _harness.Hub.PowerOn(unknown);

            var log = Assert.Single(loggerFactory.Logs, l => l.Message == $"PowerOn ignored: no set-top box registered for group {unknown}");
            Assert.Equal(nameof(SetTopBoxHub.PowerOn), log.Scope[LogBase.MethodProperty]);
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }
}
