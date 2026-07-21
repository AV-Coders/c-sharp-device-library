namespace AVCoders.SignalR.Source.Tests;

public class SourceHubTest
{
    private static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    private static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    private static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");

    private readonly List<SourceDefinition> _sources = [None, Laptop, Wireless];
    private readonly SourceManager _manager;
    private readonly string _groupName;
    private readonly SourceHubTestHarness _harness;

    public SourceHubTest()
    {
        _groupName = $"hub-src-{Guid.NewGuid()}";
        _manager = new SourceManager(_groupName, _sources, defaultSource: "Laptop", offSource: "None");
        SourceHub.RegisterSourceManager(_groupName, _manager);
        _harness = SourceHubTestHarness.CreateHub();
    }

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsSourceList()
    {
        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSourceList(
            It.Is<List<SourceDefinition>>(l => l.SequenceEqual(_sources))), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_SendsCurrentIndexWhenSourceMatches()
    {
        _manager.SetCurrentSource("Laptop");

        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.UpdateSourceIndex(1), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_SendsNoIndexWhenCurrentSourceIsNone()
    {
        // Default CurrentSource is "None" which is at index 0; verify it sends 0.
        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.UpdateSourceIndex(0), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_NoIndexSentWhenCurrentSourceNotInList()
    {
        // Build a manager whose default current source ("None") does not match any sourceId.
        var groupName = $"hub-src-{Guid.NewGuid()}";
        var sources = new List<SourceDefinition>
        { 
            new("Laptop", "Lectern HDMI", "laptop", "laptop"),
            new("Wireless", "Wireless Presenter", "wireless", "wifi")
        };
        var manager = new SourceManager(groupName, sources);
        SourceHub.RegisterSourceManager(groupName, manager);

        await _harness.Hub.JoinGroup(groupName);

        _harness.CallerMock.Verify(c => c.UpdateSourceList(It.IsAny<List<SourceDefinition>>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSourceIndex(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_StillJoinsButSendsNoState()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.JoinGroup(unknown);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), unknown, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSourceList(It.IsAny<List<SourceDefinition>>()), Times.Never);
        _harness.CallerMock.Verify(c => c.UpdateSourceIndex(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void SelectSource_ForwardsToManager()
    {
        _harness.Hub.SelectSource(_groupName, 2);

        WaitFor(() => _manager.CurrentSource == "wireless");
        Assert.Equal("wireless", _manager.CurrentSource);
    }

    [Fact]
    public void SelectSource_UnknownGroup_DoesNotChangeOtherManagers()
    {
        _harness.Hub.SelectSource($"missing-{Guid.NewGuid()}", 1);

        Assert.Equal("None", _manager.CurrentSource);
    }

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

    [Fact]
    public void RegisterSourceManager_ReplacesExistingRegistration()
    {
        var replacement = new SourceManager(_groupName, _sources);
        SourceHub.RegisterSourceManager(_groupName, replacement);

        _harness.Hub.SelectSource(_groupName, 2);

        WaitFor(() => replacement.CurrentSource == "wireless");
        // Original manager unaffected because the registration was replaced.
        Assert.Equal("None", _manager.CurrentSource);
        Assert.Equal("wireless", replacement.CurrentSource);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }
}
