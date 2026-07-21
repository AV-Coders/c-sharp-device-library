using AVCoders.Core;
using AVCoders.Display;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Display.Tests;

public class DisplayUiSignalRTest
{
    private readonly TestDisplay _display;
    private readonly DisplayManager _manager;
    private readonly Mock<IDisplayHub> _groupClient = new();
    private readonly Mock<IHubClients<IDisplayHub>> _hubClients = new();
    private readonly Mock<IHubContext<DisplayHub, IDisplayHub>> _hubContext = new();
    private readonly DisplayUiSignalR _ui;

    public DisplayUiSignalRTest()
    {
        _display = new TestDisplay($"UiDisplay-{Guid.NewGuid()}");
        _manager = new DisplayManager(_display);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new DisplayUiSignalR(_manager, _hubContext.Object);
    }

    [Fact]
    public void PowerOn_ForwardsToDisplay()
    {
        _ui.PowerOn();

        Assert.Equal(1, _display.DoPowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToDisplay()
    {
        _ui.PowerOff();

        Assert.Equal(1, _display.DoPowerOffCallCount);
    }

    [Fact]
    public void DisplayPowerStateChange_NotifiesHubGroup()
    {
        _display.SetPowerStateForTest(PowerState.On);

        _hubClients.Verify(c => c.Group(_manager.Name), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
    }

    [Fact]
    public void DisplayInputChange_NotifiesHubGroup()
    {
        _display.SetInputForTest(Input.Hdmi2);

        _groupClient.Verify(c => c.OnInputChanged(Input.Hdmi2), Times.Once);
    }

    [Fact]
    public void DisplayVolumeChange_NotifiesHubGroup()
    {
        _display.SetVolumeForTest(33);

        _groupClient.Verify(c => c.OnVolumeChanged(33), Times.Once);
    }

    [Fact]
    public void DisplayAudioMuteChange_NotifiesHubGroup()
    {
        _display.SetAudioMuteForTest(MuteState.On);

        _groupClient.Verify(c => c.OnAudioMuteChanged(MuteState.On), Times.Once);
    }

    [Fact]
    public void Constructor_RegistersManagerWithHub()
    {
        var hub = DisplayHubTestHarness.CreateHub();
        hub.JoinGroup(_manager.Name).GetAwaiter().GetResult();

        hub.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _manager.Name, It.IsAny<CancellationToken>()), Times.Once);
        hub.CallerMock.Verify(c => c.OnSupportedInputsChanged(_manager.SupportedInputs), Times.Once);
    }
}
