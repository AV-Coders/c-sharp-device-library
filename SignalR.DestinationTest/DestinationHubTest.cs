using AVCoders.SignalR.Source;

namespace AVCoders.SignalR.Destination.Tests;

public class DestinationHubTest
{
    private static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    private static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    private static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");

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

    [Fact]
    public void RouteSource_ForwardsToManager()
    {
        _harness.Hub.RouteSource(_groupName, "wireless");

        WaitFor(() => _destination.CurrentSource == Wireless);
        Assert.Equal(Wireless, _destination.CurrentSource);
    }

    [Fact]
    public void RouteSource_UnknownGroup_DoesNotChangeOtherDestinations()
    {
        _harness.Hub.RouteSource($"missing-{Guid.NewGuid()}", "wireless");

        Assert.Equal(None, _destination.CurrentSource);
    }

    [Fact]
    public void SetVideoMute_ForwardsToManager()
    {
        _harness.Hub.SetVideoMute(_groupName, true);

        WaitFor(() => _destination.VideoMute);
        Assert.True(_destination.VideoMute);
    }

    [Fact]
    public void SetVideoMute_UnknownGroup_DoesNotChangeOtherDestinations()
    {
        _harness.Hub.SetVideoMute($"missing-{Guid.NewGuid()}", true);

        Assert.False(_destination.VideoMute);
    }

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

    [Fact]
    public void RegisterDestinationManager_ReplacesExistingRegistration()
    {
        var replacement = new DestinationManager(_groupName, "display-1", "tv", _sourceManager);
        DestinationHub.RegisterDestinationManager(_groupName, replacement);

        _harness.Hub.RouteSource(_groupName, "wireless");

        WaitFor(() => replacement.CurrentSource == Wireless);
        // Original manager unaffected because the registration was replaced.
        Assert.Equal(None, _destination.CurrentSource);
        Assert.Equal(Wireless, replacement.CurrentSource);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }
}
