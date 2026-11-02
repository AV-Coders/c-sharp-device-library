using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.Display;

namespace AVCoders.SignalR.Display.Tests;

public class DisplayHubTest
{
    private const int StartVolume = 20;

    private readonly TestDisplay _display;
    private readonly DisplayManager _manager;
    private readonly string _groupName;
    private readonly DisplayHubTestHarness _harness;

    public DisplayHubTest()
    {
        _groupName = $"hub-display-{Guid.NewGuid()}";
        _display = new TestDisplay(_groupName);
        _manager = new DisplayManager(_display);
        DisplayHub.RegisterDisplayManager(_groupName, _manager);
        _harness = DisplayHubTestHarness.CreateHub();
    }

    public static TheoryData<string, Action<DisplayHub, string>, Func<TestDisplay, bool>> Commands => new()
    {
        { "PowerOn", (hub, group) => hub.PowerOn(group), display => display.DoPowerOnCallCount == 1 },
        { "PowerOff", (hub, group) => hub.PowerOff(group), display => display.DoPowerOffCallCount == 1 },
        { "TogglePower", (hub, group) => hub.TogglePower(group), display => display.DoPowerOnCallCount == 1 },
        { "SetInput", (hub, group) => hub.SetInput(group, Input.Hdmi2), display => display.LastDoSetInputArg == Input.Hdmi2 },
        { "SetVolume", (hub, group) => hub.SetVolume(group, 70), display => display.LastDoSetVolumeArg == 70 },
        { "LevelUp", (hub, group) => hub.LevelUp(group, 3), display => display.LastDoSetVolumeArg == StartVolume + 3 },
        { "LevelDown", (hub, group) => hub.LevelDown(group, 5), display => display.LastDoSetVolumeArg == StartVolume - 5 },
        { "SetAudioMute", (hub, group) => hub.SetAudioMute(group, MuteState.On), display => display.LastDoSetAudioMute == MuteState.On },
        { "ToggleAudioMute", (hub, group) => hub.ToggleAudioMute(group), display => display.LastDoSetAudioMute == MuteState.On },
    };

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsState()
    {
        _display.SetPowerStateForTest(PowerState.On);
        _display.SetInputForTest(Input.Hdmi2);
        _display.SetVolumeForTest(55);
        _display.SetAudioMuteForTest(MuteState.On);

        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
        _harness.CallerMock.Verify(c => c.OnSupportedInputsChanged(_display.SupportedInputs), Times.Once);
        _harness.CallerMock.Verify(c => c.OnInputChanged(Input.Hdmi2), Times.Once);
        _harness.CallerMock.Verify(c => c.OnVolumeChanged(55), Times.Once);
        _harness.CallerMock.Verify(c => c.OnAudioMuteChanged(MuteState.On), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_DoesNotSendDisplayState()
    {
        await _harness.Hub.JoinGroup($"missing-{Guid.NewGuid()}");

        _harness.CallerMock.Verify(c => c.OnSupportedInputsChanged(It.IsAny<IReadOnlyList<Input>>()), Times.Never);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(It.IsAny<PowerState>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_ReachesTheDisplay(string command, Action<DisplayHub, string> send, Func<TestDisplay, bool> received)
    {
        _display.SetPowerStateForTest(PowerState.Off);
        _display.SetVolumeForTest(StartVolume);
        _display.SetAudioMuteForTest(MuteState.Off);

        send(_harness.Hub, _groupName);

        await Wait.UntilAsync(() => received(_display), $"{command} did not reach the display");
    }

    [Fact]
    public void PowerOn_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.PowerOn($"missing-{Guid.NewGuid()}");

        Assert.Equal(0, _display.DoPowerOnCallCount);
    }
}
