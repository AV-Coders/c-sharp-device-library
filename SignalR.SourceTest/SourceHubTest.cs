using static AVCoders.SignalR.Source.Tests.TestSources;

namespace AVCoders.SignalR.Source.Tests;

public class SourceHubTest
{
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
    public async Task JoinGroup_SendsTheIndexOfNoneWhenCurrentSourceIsNone()
    {
        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.UpdateSourceIndex(0), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_NoIndexSentWhenCurrentSourceNotInList()
    {
        var groupName = $"hub-src-{Guid.NewGuid()}";
        var manager = new SourceManager(groupName, [Laptop, Wireless]);
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
    public void RegisterSourceManager_ReplacesExistingRegistration()
    {
        var replacement = new SourceManager(_groupName, _sources);
        SourceHub.RegisterSourceManager(_groupName, replacement);

        _harness.Hub.SelectSource(_groupName, 2);

        WaitFor(() => replacement.CurrentSource == "wireless");
        Assert.Equal("None", _manager.CurrentSource);
        Assert.Equal("wireless", replacement.CurrentSource);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }
}
