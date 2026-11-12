using AVCoders.SignalR.Connections;
using AVCoders.Core;

namespace AVCoders.SignalR.Volume.Tests;

public class VolumeHubTest
{
    private const int WaitTimeoutMs = 2000;

    private readonly TestVolumeControl _speaker = new("Speaker", VolumeType.Speaker);
    private readonly TestVolumeControl _mic = new("Mic", VolumeType.Microphone);
    private readonly VolumeManager _manager;
    private readonly string _groupName;
    private readonly VolumeHubTestHarness _harness;

    public VolumeHubTest()
    {
        _groupName = $"hub-vol-{Guid.NewGuid()}";
        _manager = new VolumeManager(_groupName, [_speaker, _mic]);
        VolumeHub.RegisterVolumeManager(_groupName, _manager);
        _harness = VolumeHubTestHarness.CreateHub();
    }

    public static TheoryData<string, Action<VolumeHub, string>, Func<TestVolumeControl, TestVolumeControl, bool>> Commands => new()
    {
        { "SetVolumeLevel", (hub, group) => hub.SetVolumeLevel(group, 0, 75), (speaker, _) => speaker.LastSetLevel == 75 },
        { "SetVolumeMute", (hub, group) => hub.SetVolumeMute(group, 1, MuteState.On), (_, mic) => mic.LastSetMute == MuteState.On },
    };

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsControls()
    {
        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnVolumeControlsChanged(
            It.Is<List<VolumeControl>>(l => l.Count == 2 && l[0] == _speaker && l[1] == _mic)),
            Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_StillJoinsButSendsNoControls()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.JoinGroup(unknown);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), unknown, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnVolumeControlsChanged(It.IsAny<List<VolumeControl>>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_ReachesTheControl(string command, Action<VolumeHub, string> send, Func<TestVolumeControl, TestVolumeControl, bool> received)
    {
        send(_harness.Hub, _groupName);

        WaitFor(() => received(_speaker, _mic), $"{command} did not reach the volume control");
    }

    [Fact]
    public void SetVolumeLevel_UnknownGroup_DoesNotChangeKnownManager()
    {
        _harness.Hub.SetVolumeLevel($"missing-{Guid.NewGuid()}", 0, 75);

        Assert.Null(_speaker.LastSetLevel);
    }

    [Fact]
    public void RegisterVolumeManager_ReplacesExistingRegistration()
    {
        var replacementSpeaker = new TestVolumeControl("ReplacementSpeaker");
        var replacement = new VolumeManager(_groupName, [replacementSpeaker]);
        VolumeHub.RegisterVolumeManager(_groupName, replacement);

        _harness.Hub.SetVolumeLevel(_groupName, 0, 33);

        WaitFor(() => replacementSpeaker.LastSetLevel == 33, "SetVolumeLevel did not reach the replacement volume control");
        Assert.Equal(33, replacementSpeaker.LastSetLevel);
        Assert.Null(_speaker.LastSetLevel);
    }

    [Fact]
    public void ConcurrentRegisterAndRead_DoesNotThrow()
    {
        var exception = Record.Exception(() => Parallel.For(0, 10_000, i =>
        {
            var name = $"race-{Guid.NewGuid()}";
            VolumeHub.RegisterVolumeManager(name, _manager);
            _ = _harness.Hub.GetGroups();
            _harness.Hub.SetVolumeLevel(name, 0, 50);
        }));

        Assert.Null(exception);
    }

    private static void WaitFor(Func<bool> condition, string failureMessage)
    {
        Assert.True(SpinWait.SpinUntil(condition, WaitTimeoutMs), failureMessage);
    }

    [Fact]
    public async Task JoinGroup_CountsTheConnectionInTheGroup()
    {
        await _harness.Hub.OnConnectedAsync();
        await _harness.Hub.JoinGroup(_groupName);

        Assert.Equal(1, VolumeHub.Connections.Count(_groupName));
    }

    [Fact]
    public async Task OnDisconnectedAsync_RemovesTheConnectionFromTheGroup()
    {
        await _harness.Hub.OnConnectedAsync();
        await _harness.Hub.JoinGroup(_groupName);

        await _harness.Hub.OnDisconnectedAsync(null);

        Assert.Equal(0, VolumeHub.Connections.Count(_groupName));
    }

    [Fact]
    public void RegisterVolumeManager_ListsTheGroupInConnections()
    {
        Assert.Contains(new GroupConnections(_groupName, true, 0), VolumeHub.Connections.GetSnapshot().Groups);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_IsReported()
    {
        var unknown = $"missing-{Guid.NewGuid()}";
        await _harness.Hub.OnConnectedAsync();

        await _harness.Hub.JoinGroup(unknown);

        var request = Assert.Single(VolumeHub.Connections.GetSnapshot().UnknownGroupRequests, r => r.Group == unknown);
        Assert.Equal(("VolumeHub", "JoinGroup", _harness.ContextMock.Object.ConnectionId), (request.Hub, request.Method, request.ConnectionId));
        var issue = Assert.Single(VolumeHub.Connections.GetOngoingIssues(), i => i.Key == HubConnections.UnknownGroupIssuePrefix + unknown);
        Assert.StartsWith($"JoinGroup from connection {request.ConnectionId} asked for group '{unknown}', which has nothing registered.", issue.Message);
        Assert.Equal(1, VolumeHub.Connections.Count(unknown));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_UnknownGroup_IsReportedUnderItsName(string command, Action<VolumeHub, string> send, Func<TestVolumeControl, TestVolumeControl, bool> _)
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        send(_harness.Hub, unknown);

        Assert.Equal(command.Split(' ')[0], Assert.Single(VolumeHub.Connections.GetSnapshot().UnknownGroupRequests, r => r.Group == unknown).Method);
    }
}
