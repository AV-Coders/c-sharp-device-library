using AVCoders.Core;

namespace AVCoders.SignalR.Volume.Tests;

public class VolumeHubTest
{
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

    [Fact]
    public void SetVolumeLevel_ForwardsToManager()
    {
        _harness.Hub.SetVolumeLevel(_groupName, 0, 75);

        WaitFor(() => _speaker.LastSetLevel == 75);
        Assert.Equal(75, _speaker.LastSetLevel);
    }

    [Fact]
    public void SetVolumeLevel_UnknownGroup_DoesNotChangeKnownManager()
    {
        _harness.Hub.SetVolumeLevel($"missing-{Guid.NewGuid()}", 0, 75);

        Assert.Null(_speaker.LastSetLevel);
    }

    [Fact]
    public void SetVolumeMute_ForwardsToManager()
    {
        _harness.Hub.SetVolumeMute(_groupName, 1, MuteState.On);

        WaitFor(() => _mic.LastSetMute == MuteState.On);
        Assert.Equal(MuteState.On, _mic.LastSetMute);
    }

    [Fact]
    public void SetVolumeMute_UnknownGroup_DoesNotChangeKnownManager()
    {
        _harness.Hub.SetVolumeMute($"missing-{Guid.NewGuid()}", 0, MuteState.On);

        Assert.Null(_speaker.LastSetMute);
    }

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

    [Fact]
    public void RegisterVolumeManager_ReplacesExistingRegistration()
    {
        var replacementSpeaker = new TestVolumeControl("ReplacementSpeaker");
        var replacement = new VolumeManager(_groupName, [replacementSpeaker]);
        VolumeHub.RegisterVolumeManager(_groupName, replacement);

        _harness.Hub.SetVolumeLevel(_groupName, 0, 33);

        WaitFor(() => replacementSpeaker.LastSetLevel == 33);
        Assert.Equal(33, replacementSpeaker.LastSetLevel);
        Assert.Null(_speaker.LastSetLevel);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }

    [Fact]
    public void ConcurrentRegisterAndRead_DoesNotThrow()
    {
        // Registration races reads on SignalR dispatch threads: against a plain
        // Dictionary this throws/corrupts intermittently, against the
        // ConcurrentDictionary registry it is safe. Guards against regressing H1.
        var exception = Record.Exception(() => Parallel.For(0, 10_000, i =>
        {
            var name = $"race-{Guid.NewGuid()}";
            VolumeHub.RegisterVolumeManager(name, _manager);
            _ = _harness.Hub.GetGroups();
            _harness.Hub.SetVolumeLevel(name, 0, 50);
        }));

        Assert.Null(exception);
    }
}
