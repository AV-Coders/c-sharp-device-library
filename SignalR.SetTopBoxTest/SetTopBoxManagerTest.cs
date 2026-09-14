using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;

namespace AVCoders.SignalR.SetTopBox.Tests;

public class SetTopBoxManagerTest
{
    private readonly TestSetTopBox _device;
    private readonly SetTopBoxManager _manager;

    public SetTopBoxManagerTest()
    {
        _device = new TestSetTopBox("Apple TV");
        _manager = new SetTopBoxManager("Boardroom", _device, "AppleTv");
    }

    [Fact]
    public void Constructor_RejectsMediaPlayerThatIsNotASetTopBox()
    {
        var plain = new TestPlainMediaPlayer();

        var exception = Assert.Throws<ArgumentException>(() => new SetTopBoxManager("Boardroom", plain, "Player"));
        Assert.Equal("device", exception.ParamName);
    }

    [Fact]
    public void Name_IsTheGroupNameNotTheDeviceName()
    {
        Assert.Equal("Boardroom", _manager.Name);
        Assert.Equal("Boardroom", _manager.State.Name);
    }

    [Fact]
    public void State_CarriesSourceId()
    {
        Assert.Equal("AppleTv", _manager.SourceId);
        Assert.Equal("AppleTv", _manager.State.SourceId);
    }

    [Fact]
    public void State_ListsSupportedButtonsByName()
    {
        var device = new TestSetTopBox(supportedButtons: [RemoteButton.Up, RemoteButton.Down, RemoteButton.Enter]);
        var manager = new SetTopBoxManager("Boardroom", device, "AppleTv");

        Assert.Equal(["Up", "Down", "Enter"], manager.State.SupportedButtons);
        Assert.Equal(device.SupportedButtons, manager.SupportedButtons);
    }

    [Fact]
    public void State_DefaultsToUnknown()
    {
        var state = _manager.State;

        Assert.Equal(PowerState.Unknown, state.PowerState);
        Assert.Equal(PowerState.Unknown, state.DesiredPowerState);
        Assert.Equal(CommunicationState.Unknown, state.CommunicationState);
        Assert.False(state.IsActiveSource);
    }

    [Fact]
    public void State_ReflectsDevicePowerDesiredPowerAndComms()
    {
        _device.SetPowerStateForTest(PowerState.On);
        _device.SetDesiredPowerStateForTest(PowerState.Off);
        _device.SetCommunicationStateForTest(CommunicationState.Okay);

        var state = _manager.State;

        Assert.Equal(PowerState.On, state.PowerState);
        Assert.Equal(PowerState.Off, state.DesiredPowerState);
        Assert.Equal(CommunicationState.Okay, state.CommunicationState);
    }

    [Fact]
    public void DevicePowerStateChange_MirrorsOntoManagerAndRaisesStateChanged()
    {
        var handler = new Mock<Action<SetTopBoxState>>();
        _manager.StateChanged += handler.Object;

        _device.SetPowerStateForTest(PowerState.On);

        Assert.Equal(PowerState.On, _manager.PowerState);
        handler.Verify(h => h.Invoke(It.Is<SetTopBoxState>(s => s.PowerState == PowerState.On)), Times.Once);
    }

    [Fact]
    public void DeviceDesiredPowerStateChange_RaisesStateChanged()
    {
        var handler = new Mock<Action<SetTopBoxState>>();
        _manager.StateChanged += handler.Object;

        _device.SetDesiredPowerStateForTest(PowerState.On);

        handler.Verify(h => h.Invoke(It.Is<SetTopBoxState>(s => s.DesiredPowerState == PowerState.On)), Times.Once);
    }

    [Fact]
    public void DeviceCommunicationStateChange_RaisesStateChanged()
    {
        var handler = new Mock<Action<SetTopBoxState>>();
        _manager.StateChanged += handler.Object;

        _device.SetCommunicationStateForTest(CommunicationState.Error);

        handler.Verify(h => h.Invoke(It.Is<SetTopBoxState>(s => s.CommunicationState == CommunicationState.Error)), Times.Once);
    }

    [Fact]
    public void DeviceStateNoOp_DoesNotRaiseStateChanged()
    {
        var handler = new Mock<Action<SetTopBoxState>>();
        _manager.StateChanged += handler.Object;

        _device.SetPowerStateForTest(PowerState.Unknown); // already Unknown

        handler.Verify(h => h.Invoke(It.IsAny<SetTopBoxState>()), Times.Never);
    }

    [Fact]
    public void SendRemoteButton_ForwardsToDevice()
    {
        _manager.SendRemoteButton(RemoteButton.Play);

        Assert.Equal([RemoteButton.Play], _device.SentButtons);
    }

    [Fact]
    public void PowerOn_ForwardsToDevice()
    {
        _manager.PowerOn();

        Assert.Equal(1, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToDevice()
    {
        _manager.PowerOff();

        Assert.Equal(1, _device.PowerOffCallCount);
    }

    [Fact]
    public void AppleTvCec_ActiveSourceChange_ReflectsInStateAndRaisesStateChanged()
    {
        // pollTime 0: no poll worker, so nothing sends unless a test asks for it.
        var cecStream = TestFactory.CreateSerialClient();
        var appleTv = new AppleTvCec(cecStream.Object, "Apple TV", pollTime: 0);
        var manager = new SetTopBoxManager("Boardroom", appleTv, "AppleTv");
        var handler = new Mock<Action<SetTopBoxState>>();
        manager.StateChanged += handler.Object;
        Assert.False(manager.State.IsActiveSource);

        cecStream.Object.ResponseHandlers!.Invoke("\x4F\x82\x10\x00"); // Active Source broadcast

        Assert.True(appleTv.IsActiveSource);
        Assert.True(manager.State.IsActiveSource);
        handler.Verify(h => h.Invoke(It.Is<SetTopBoxState>(s => s.IsActiveSource)), Times.Once);
    }

    [Fact]
    public void AppleTvCec_SupportedButtonsAreReportedByName()
    {
        var appleTv = new AppleTvCec(TestFactory.CreateSerialClient().Object, "Apple TV", pollTime: 0);
        var manager = new SetTopBoxManager("Boardroom", appleTv, "AppleTv");

        var names = manager.State.SupportedButtons;

        Assert.Equal(appleTv.SupportedButtons.Count, names.Length);
        Assert.Contains("PowerOn", names);
        Assert.Contains("Enter", names);
        Assert.DoesNotContain("Guide", names);
    }
}
