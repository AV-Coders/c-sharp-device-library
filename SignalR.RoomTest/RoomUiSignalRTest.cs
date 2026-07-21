using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Room.Tests;

public class RoomUiSignalRTest
{
    private readonly TestDevice _device;
    private readonly RoomManager _manager;
    private readonly string _groupName;
    private readonly Mock<IRoomHub> _groupClient = new();
    private readonly Mock<IHubClients<IRoomHub>> _hubClients = new();
    private readonly Mock<IHubContext<RoomHub, IRoomHub>> _hubContext = new();
    private readonly RoomUiSignalR _ui;

    public RoomUiSignalRTest()
    {
        _groupName = $"ui-room-{Guid.NewGuid()}";
        _device = new TestDevice(_groupName);
        _manager = new RoomManager(_device);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new RoomUiSignalR(_manager, _hubContext.Object);
    }

    [Fact]
    public async Task Constructor_RegistersManagerWithHub()
    {
        var harness = RoomHubTestHarness.CreateHub();
        _device.SetPowerStateForTest(PowerState.On);

        await harness.Hub.JoinGroup(_groupName);

        harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        harness.CallerMock.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
    }

    [Fact]
    public void DevicePowerStateChange_NotifiesHubGroup()
    {
        _device.SetPowerStateForTest(PowerState.On);

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
    }

    [Fact]
    public void PowerOn_ForwardsToDeviceAndPropagatesEvent()
    {
        _ui.PowerOn();

        Assert.Equal(1, _device.PowerOnCallCount);
        _groupClient.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
    }

    [Fact]
    public void PowerOff_ForwardsToDeviceAndPropagatesEvent()
    {
        _ui.PowerOn();
        _groupClient.Invocations.Clear();

        _ui.PowerOff();

        Assert.Equal(1, _device.PowerOffCallCount);
        _groupClient.Verify(c => c.OnPowerStateChanged(PowerState.Off), Times.Once);
    }

    [Fact]
    public void Name_MatchesManagerName()
    {
        Assert.Equal(_manager.Name, _ui.Name);
    }

    [Fact]
    public void ManagerSetProperty_NotifiesHubGroupWithDelta()
    {
        _manager.SetProperty("status", "in-meeting");

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnPropertyChanged("status", "in-meeting"), Times.Once);
    }

    [Fact]
    public void ManagerSetProperty_DoesNotBroadcastSnapshotOnEachUpdate()
    {
        _manager.SetProperty("status", "in-meeting");

        _groupClient.Verify(c => c.OnPropertiesSnapshot(It.IsAny<Dictionary<string, string>>()), Times.Never);
    }
}
