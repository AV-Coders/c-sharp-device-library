using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.SetTopBox.Tests;

public class SetTopBoxUiSignalRTest
{
    private readonly TestSetTopBox _device;
    private readonly SetTopBoxManager _manager;
    private readonly string _groupName;
    private readonly Mock<ISetTopBoxHub> _groupClient = new();
    private readonly Mock<IHubClients<ISetTopBoxHub>> _hubClients = new();
    private readonly Mock<IHubContext<SetTopBoxHub, ISetTopBoxHub>> _hubContext = new();
    private readonly SetTopBoxUiSignalR _ui;

    public SetTopBoxUiSignalRTest()
    {
        _groupName = $"ui-stb-{Guid.NewGuid()}";
        _device = new TestSetTopBox();
        _manager = new SetTopBoxManager(_groupName, _device, "AppleTv");
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new SetTopBoxUiSignalR(_manager, _hubContext.Object);
    }

    [Fact]
    public async Task Constructor_RegistersManagerWithHub()
    {
        var harness = SetTopBoxHubTestHarness.CreateHub();

        await harness.Hub.JoinGroup(_groupName);

        harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        harness.CallerMock.Verify(c => c.UpdateSetTopBox(
            It.Is<SetTopBoxState>(s => s.Name == _groupName && s.SourceId == "AppleTv")), Times.Once);
    }

    [Fact]
    public void DevicePowerStateChange_PushesStateToHubGroup()
    {
        _device.SetPowerStateForTest(PowerState.On);

        _hubClients.Verify(c => c.Group(_groupName), Times.Once);
        _groupClient.Verify(c => c.UpdateSetTopBox(
            It.Is<SetTopBoxState>(s => s.Name == _groupName && s.PowerState == PowerState.On)), Times.Once);
    }

    [Fact]
    public void DeviceCommunicationStateChange_PushesStateToHubGroup()
    {
        _device.SetCommunicationStateForTest(CommunicationState.Okay);

        _groupClient.Verify(c => c.UpdateSetTopBox(
            It.Is<SetTopBoxState>(s => s.CommunicationState == CommunicationState.Okay)), Times.Once);
    }

    [Fact]
    public void DeviceStateNoOp_PushesNothing()
    {
        _device.SetPowerStateForTest(PowerState.Unknown); // already Unknown

        _groupClient.Verify(c => c.UpdateSetTopBox(It.IsAny<SetTopBoxState>()), Times.Never);
    }

    [Fact]
    public void PowerOn_ForwardsToDevice()
    {
        _ui.PowerOn();

        Assert.Equal(1, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToDevice()
    {
        _ui.PowerOff();

        Assert.Equal(1, _device.PowerOffCallCount);
    }

    [Fact]
    public void Name_MatchesManagerName()
    {
        Assert.Equal(_manager.Name, _ui.Name);
    }
}
