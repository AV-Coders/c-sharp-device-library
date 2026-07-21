using AVCoders.Core;
using AVCoders.Display;

namespace AVCoders.SignalR.Display.Tests;

public class DisplayHubTest
{
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

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

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
        _harness.CallerMock.Verify(c => c.OnSupportedInputsChanged(_manager.SupportedInputs), Times.Once);
        _harness.CallerMock.Verify(c => c.OnInputChanged(Input.Hdmi2), Times.Once);
        _harness.CallerMock.Verify(c => c.OnVolumeChanged(55), Times.Once);
        _harness.CallerMock.Verify(c => c.OnAudioMuteChanged(MuteState.On), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_DoesNotSendDisplayState()
    {
        await _harness.Hub.JoinGroup($"missing-{Guid.NewGuid()}");

        _harness.CallerMock.Verify(c => c.OnSupportedInputsChanged(It.IsAny<List<Input>>()), Times.Never);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(It.IsAny<PowerState>()), Times.Never);
    }

    [Fact]
    public void PowerOn_ForwardsToDisplay()
    {
        _harness.Hub.PowerOn(_groupName);

        WaitFor(() => _display.DoPowerOnCallCount == 1);
        Assert.Equal(1, _display.DoPowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToDisplay()
    {
        _harness.Hub.PowerOff(_groupName);

        WaitFor(() => _display.DoPowerOffCallCount == 1);
        Assert.Equal(1, _display.DoPowerOffCallCount);
    }

    [Fact]
    public void TogglePower_ForwardsToDisplay()
    {
        _display.SetPowerStateForTest(PowerState.Off);

        _harness.Hub.TogglePower(_groupName);

        WaitFor(() => _display.DoPowerOnCallCount == 1);
        Assert.Equal(1, _display.DoPowerOnCallCount);
    }

    [Fact]
    public void SetInput_ForwardsToDisplay()
    {
        _harness.Hub.SetInput(_groupName, Input.Hdmi2);

        WaitFor(() => _display.LastDoSetInputArg == Input.Hdmi2);
        Assert.Equal(Input.Hdmi2, _display.LastDoSetInputArg);
    }

    [Fact]
    public void SetVolume_ForwardsToDisplay()
    {
        _harness.Hub.SetVolume(_groupName, 70);

        WaitFor(() => _display.LastDoSetVolumeArg == 70);
        Assert.Equal(70, _display.LastDoSetVolumeArg);
    }

    [Fact]
    public void LevelUp_ForwardsToDisplay()
    {
        _display.SetVolumeForTest(10);

        _harness.Hub.LevelUp(_groupName, 3);

        WaitFor(() => _display.LastDoSetVolumeArg == 13);
        Assert.Equal(13, _display.LastDoSetVolumeArg);
    }

    [Fact]
    public void LevelDown_ForwardsToDisplay()
    {
        _display.SetVolumeForTest(20);

        _harness.Hub.LevelDown(_groupName, 5);

        WaitFor(() => _display.LastDoSetVolumeArg == 15);
        Assert.Equal(15, _display.LastDoSetVolumeArg);
    }

    [Fact]
    public void SetAudioMute_ForwardsToDisplay()
    {
        _harness.Hub.SetAudioMute(_groupName, MuteState.On);

        WaitFor(() => _display.LastDoSetAudioMute == MuteState.On);
        Assert.Equal(MuteState.On, _display.LastDoSetAudioMute);
    }

    [Fact]
    public void ToggleAudioMute_ForwardsToDisplay()
    {
        _display.SetAudioMuteForTest(MuteState.Off);

        _harness.Hub.ToggleAudioMute(_groupName);

        WaitFor(() => _display.LastDoSetAudioMute == MuteState.On);
        Assert.Equal(MuteState.On, _display.LastDoSetAudioMute);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }

    [Fact]
    public void PowerOn_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.PowerOn($"missing-{Guid.NewGuid()}");

        Assert.Equal(0, _display.DoPowerOnCallCount);
    }

    [Fact]
    public void SetInput_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.SetInput($"missing-{Guid.NewGuid()}", Input.Hdmi2);

        Assert.Null(_display.LastDoSetInputArg);
    }

    [Fact]
    public void SetVolume_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.SetVolume($"missing-{Guid.NewGuid()}", 50);

        Assert.Null(_display.LastDoSetVolumeArg);
    }

    [Fact]
    public void SetAudioMute_UnknownGroup_DoesNotThrow()
    {
        _harness.Hub.SetAudioMute($"missing-{Guid.NewGuid()}", MuteState.On);

        Assert.Null(_display.LastDoSetAudioMute);
    }
}
