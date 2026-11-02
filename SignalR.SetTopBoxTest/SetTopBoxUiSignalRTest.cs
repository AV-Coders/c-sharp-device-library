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

    public static TheoryData<string, Action<SetTopBoxUiSignalR>, Func<TestSetTopBox, bool>> PowerCommands => new()
    {
        { "PowerOn", ui => ui.PowerOn(), device => device.PowerOnCallCount == 1 },
        { "PowerOff", ui => ui.PowerOff(), device => device.PowerOffCallCount == 1 },
    };

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
        _device.SetPowerStateForTest(PowerState.Unknown);

        _groupClient.Verify(c => c.UpdateSetTopBox(It.IsAny<SetTopBoxState>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(PowerCommands))]
    public void PowerCommand_ReachesTheDevice(string command, Action<SetTopBoxUiSignalR> send, Func<TestSetTopBox, bool> received)
    {
        send(_ui);

        Assert.True(received(_device), $"{command} did not reach the set-top box");
    }
}
