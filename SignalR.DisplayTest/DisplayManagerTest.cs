using AVCoders.Core;
using AVCoders.Display;

namespace AVCoders.SignalR.Display.Tests;

public class DisplayManagerTest
{
    private readonly TestDisplay _display;
    private readonly DisplayManager _manager;

    public DisplayManagerTest()
    {
        _display = new TestDisplay();
        _manager = new DisplayManager(_display);
    }

    [Fact]
    public void SupportedInputs_ReflectsDisplay()
    {
        Assert.Equal(_display.SupportedInputs, _manager.SupportedInputs);
    }

    [Fact]
    public void Input_DefaultsToUnknown()
    {
        Assert.Equal(Input.Unknown, _manager.Input);
    }

    [Fact]
    public void Input_ReflectsDisplay()
    {
        _display.SetInputForTest(Input.Hdmi2);

        Assert.Equal(Input.Hdmi2, _manager.Input);
    }

    [Fact]
    public void Volume_ReflectsDisplay()
    {
        _display.SetVolumeForTest(42);

        Assert.Equal(42, _manager.Volume);
    }

    [Fact]
    public void AudioMute_ReflectsDisplay()
    {
        _display.SetAudioMuteForTest(MuteState.On);

        Assert.Equal(MuteState.On, _manager.AudioMute);
    }

    [Fact]
    public void PowerOn_ForwardsToDisplay()
    {
        _manager.PowerOn();

        Assert.Equal(1, _display.DoPowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToDisplay()
    {
        _manager.PowerOff();

        Assert.Equal(1, _display.DoPowerOffCallCount);
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
    public void SetInput_ForwardsToDisplay()
    {
        _manager.SetInput(Input.Hdmi2);

        Assert.Equal(Input.Hdmi2, _display.LastDoSetInputArg);
    }

    [Fact]
    public void SetInput_UnsupportedInput_DoesNotForward()
    {
        _manager.SetInput(Input.DisplayPort);

        Assert.Null(_display.LastDoSetInputArg);
    }

    [Fact]
    public void SetVolume_ForwardsToDisplay()
    {
        _manager.SetVolume(55);

        Assert.Equal(55, _display.LastDoSetVolumeArg);
    }

    [Fact]
    public void LevelUp_ForwardsToDisplay()
    {
        _display.SetVolumeForTest(40);

        _manager.LevelUp(5);

        Assert.Equal(45, _display.LastDoSetVolumeArg);
    }

    [Fact]
    public void LevelDown_ForwardsToDisplay()
    {
        _display.SetVolumeForTest(40);

        _manager.LevelDown(10);

        Assert.Equal(30, _display.LastDoSetVolumeArg);
    }

    [Fact]
    public void SetAudioMute_ForwardsToDisplay()
    {
        _manager.SetAudioMute(MuteState.On);

        Assert.Equal(MuteState.On, _display.LastDoSetAudioMute);
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
