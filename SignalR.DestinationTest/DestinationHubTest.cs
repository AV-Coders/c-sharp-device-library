using AVCoders.SignalR.Source;
using static AVCoders.SignalR.Destination.Tests.TestSources;

namespace AVCoders.SignalR.Destination.Tests;

public class DestinationHubTest
{
    private const int WaitTimeoutMs = 2000;

    private readonly List<SourceDefinition> _sources = [None, Laptop, Wireless];
    private readonly SourceManager _sourceManager;
    private readonly DestinationManager _destination;
    private readonly string _groupName;
    private readonly DestinationHubTestHarness _harness;

    public DestinationHubTest()
    {
        _groupName = $"hub-dst-{Guid.NewGuid()}";
        _sourceManager = new SourceManager($"src-{_groupName}", _sources);
        _destination = new DestinationManager(_groupName, "display-1", "tv", _sourceManager);
        DestinationHub.RegisterDestinationManager(_groupName, _destination);
        _harness = DestinationHubTestHarness.CreateHub();
    }

    public static TheoryData<string, Action<DestinationHub, string>, Func<DestinationManager, bool>> Commands => new()
    {
        { "RouteSource", (hub, group) => hub.RouteSource(group, "wireless"), destination => destination.CurrentSource == Wireless },
        { "SetVideoMute", (hub, group) => hub.SetVideoMute(group, true), destination => destination.VideoMute },
    };

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsSnapshot()
    {
        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnDestinationChanged(
            It.Is<DestinationDefinition>(d =>
                d.Name == _groupName &&
                d.DestinationId == "display-1" &&
                d.CurrentSource == None &&
                !d.VideoMute)), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_StillJoinsButSendsNoSnapshot()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.JoinGroup(unknown);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), unknown, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnDestinationChanged(It.IsAny<DestinationDefinition>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_ReachesTheDestination(string command, Action<DestinationHub, string> send, Func<DestinationManager, bool> received)
    {
        send(_harness.Hub, _groupName);

        WaitFor(() => received(_destination), $"{command} did not reach the destination");
    }

    [Fact]
    public void RouteSource_UnknownGroup_DoesNotChangeOtherDestinations()
    {
        _harness.Hub.RouteSource($"missing-{Guid.NewGuid()}", "wireless");

        Assert.Equal(None, _destination.CurrentSource);
    }

    [Fact]
    public void RegisterDestinationManager_ReplacesExistingRegistration()
    {
        var replacement = new DestinationManager(_groupName, "display-1", "tv", _sourceManager);
        DestinationHub.RegisterDestinationManager(_groupName, replacement);

        _harness.Hub.RouteSource(_groupName, "wireless");

        WaitFor(() => replacement.CurrentSource == Wireless, "The route did not reach the replacement destination");
        Assert.Equal(None, _destination.CurrentSource);
        Assert.Equal(Wireless, replacement.CurrentSource);
    }

    private static void WaitFor(Func<bool> condition, string failureMessage)
    {
        Assert.True(SpinWait.SpinUntil(condition, WaitTimeoutMs), failureMessage);
    }
}
