using AVCoders.Core;
using AVCoders.Display;

namespace AVCoders.SignalR.Display.Tests;

public class DisplayManagerTest
{
    private const int StartVolume = 40;

    private readonly TestDisplay _display;
    private readonly DisplayManager _manager;

    public DisplayManagerTest()
    {
        _display = new TestDisplay();
        _manager = new DisplayManager(_display);
    }

    public static TheoryData<string, Action<DisplayManager>, Func<TestDisplay, bool>> Commands => new()
    {
        { "PowerOn", manager => manager.PowerOn(), display => display.DoPowerOnCallCount == 1 },
        { "PowerOff", manager => manager.PowerOff(), display => display.DoPowerOffCallCount == 1 },
        { "SetInput", manager => manager.SetInput(Input.Hdmi2), display => display.LastDoSetInputArg == Input.Hdmi2 },
        { "SetVolume", manager => manager.SetVolume(55), display => display.LastDoSetVolumeArg == 55 },
        { "LevelUp", manager => manager.LevelUp(5), display => display.LastDoSetVolumeArg == StartVolume + 5 },
        { "LevelDown", manager => manager.LevelDown(10), display => display.LastDoSetVolumeArg == StartVolume - 10 },
        { "SetAudioMute", manager => manager.SetAudioMute(MuteState.On), display => display.LastDoSetAudioMute == MuteState.On },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_ReachesTheDisplay(string command, Action<DisplayManager> send, Func<TestDisplay, bool> received)
    {
        _display.SetVolumeForTest(StartVolume);

        send(_manager);

        Assert.True(received(_display), $"{command} did not reach the display");
    }

    [Fact]
    public void TogglePower_OffWhenOn()
    {
        _display.SetPowerStateForTest(PowerState.On);

        _manager.TogglePower();

        Assert.Equal(1, _display.DoPowerOffCallCount);
    }

    [Fact]
    public void TogglePower_OnWhenOff()
    {
        _display.SetPowerStateForTest(PowerState.Off);

        _manager.TogglePower();

        Assert.Equal(1, _display.DoPowerOnCallCount);
    }

    [Fact]
    public void SetInput_UnsupportedInput_DoesNotForward()
    {
        _manager.SetInput(Input.DisplayPort);

        Assert.Null(_display.LastDoSetInputArg);
    }

    [Fact]
    public void ToggleAudioMute_OffWhenOn()
    {
        _display.SetAudioMuteForTest(MuteState.On);

        _manager.ToggleAudioMute();

        Assert.Equal(MuteState.Off, _display.LastDoSetAudioMute);
    }

    [Fact]
    public void ToggleAudioMute_OnWhenOff()
    {
        _display.SetAudioMuteForTest(MuteState.Off);

        _manager.ToggleAudioMute();

        Assert.Equal(MuteState.On, _display.LastDoSetAudioMute);
    }

    [Fact]
    public void DisplayPowerStateChange_UpdatesManagerPowerState()
    {
        _display.SetPowerStateForTest(PowerState.On);

        Assert.Equal(PowerState.On, _manager.PowerState);
    }

    [Fact]
    public void DisplayInputChange_RaisesManagerOnInputChanged()
    {
        var handler = new Mock<Action<Input>>();
        _manager.OnInputChanged += handler.Object;

        _display.SetInputForTest(Input.Hdmi2);

        handler.Verify(h => h.Invoke(Input.Hdmi2), Times.Once);
    }

    [Fact]
    public void DisplayVolumeChange_RaisesManagerOnVolumeChanged()
    {
        var handler = new Mock<Action<int>>();
        _manager.OnVolumeChanged += handler.Object;

        _display.SetVolumeForTest(75);

        handler.Verify(h => h.Invoke(75), Times.Once);
    }

    [Fact]
    public void DisplayAudioMuteChange_RaisesManagerOnAudioMuteChanged()
    {
        var handler = new Mock<Action<MuteState>>();
        _manager.OnAudioMuteChanged += handler.Object;

        _display.SetAudioMuteForTest(MuteState.On);

        handler.Verify(h => h.Invoke(MuteState.On), Times.Once);
    }
}
