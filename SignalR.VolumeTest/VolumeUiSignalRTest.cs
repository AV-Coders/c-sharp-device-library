using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Volume.Tests;

public class VolumeUiSignalRTest
{
    private readonly TestVolumeControl _speaker = new("Speaker", VolumeType.Speaker);
    private readonly TestVolumeControl _mic = new("Mic", VolumeType.Microphone);
    private readonly VolumeManager _manager;
    private readonly string _groupName;
    private readonly Mock<IVolumeHub> _groupClient = new();
    private readonly Mock<IHubClients<IVolumeHub>> _hubClients = new();
    private readonly Mock<IHubContext<VolumeHub, IVolumeHub>> _hubContext = new();
    private readonly VolumeUiSignalR _ui;

    public VolumeUiSignalRTest()
    {
        _groupName = $"ui-vol-{Guid.NewGuid()}";
        _manager = new VolumeManager(_groupName, [_speaker, _mic]);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new VolumeUiSignalR(_manager, _hubContext.Object);
    }

    [Fact]
    public async Task Constructor_RegistersManagerWithHub()
    {
        var harness = VolumeHubTestHarness.CreateHub();

        await harness.Hub.JoinGroup(_groupName);

        harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        harness.CallerMock.Verify(c => c.OnVolumeControlsChanged(
            It.Is<List<VolumeControl>>(l => l.Count == 2)), Times.Once);
    }

    [Fact]
    public void ManagerVolumeLevelChanged_NotifiesHubGroupWithIndexAndControl()
    {
        _manager.SetVolumeLevel(0, 80);

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnVolumeLevelChanged(0, _speaker), Times.Once);
    }

    [Fact]
    public void ManagerVolumeMuteChanged_NotifiesHubGroupWithIndexAndControl()
    {
        _manager.SetVolumeMute(1, MuteState.On);

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnVolumeMuteChanged(1, _mic), Times.Once);
    }

    [Fact]
    public void PowerOn_IsNoOp()
    {
        _ui.PowerOn();

        Assert.Null(_speaker.LastSetLevel);
        _groupClient.VerifyNoOtherCalls();
    }

    [Fact]
    public void PowerOff_IsNoOp()
    {
        _ui.PowerOff();

        Assert.Null(_speaker.LastSetLevel);
        _groupClient.VerifyNoOtherCalls();
    }

    [Fact]
    public void Name_MatchesManagerName()
    {
        Assert.Equal(_manager.Name, _ui.Name);
    }
}
