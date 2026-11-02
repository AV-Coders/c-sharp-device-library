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

    public static TheoryData<string, Action<SetTopBoxManager>, Func<TestSetTopBox, bool>> Commands => new()
    {
        { "SendRemoteButton", manager => manager.SendRemoteButton(RemoteButton.Play), device => device.SentButtons is [RemoteButton.Play] },
        { "PowerOn", manager => manager.PowerOn(), device => device.PowerOnCallCount == 1 },
        { "PowerOff", manager => manager.PowerOff(), device => device.PowerOffCallCount == 1 },
    };

    [Fact]
    public void Constructor_RejectsMediaPlayerThatIsNotASetTopBox()
    {
        var plain = new TestPlainMediaPlayer();

        var exception = Assert.Throws<ArgumentException>(() => new SetTopBoxManager("Boardroom", plain, "Player"));
        Assert.Equal("device", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectingADevice_LeavesNothingRegistered()
    {
        var plain = new TestPlainMediaPlayer();
        var noneHandlers = CommunicationClient.None.ConnectionStateHandlers?.GetInvocationList().Length ?? 0;

        Assert.Throws<ArgumentException>(() => new SetTopBoxManager("Rejected manager", plain, "Player"));

        Assert.DoesNotContain(LogBaseRegistry.GetAll(), i => i is SetTopBoxManager { Name: "Rejected manager" });
        Assert.Equal(noneHandlers, CommunicationClient.None.ConnectionStateHandlers?.GetInvocationList().Length ?? 0);
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
    public void State_ReflectsDevicePowerDesiredPowerAndComms()
    {
        _device.SetPowerStateForTest(PowerState.On);
        _device.SetDesiredPowerStateForTest(PowerState.Off);
        _device.SetCommunicationStateForTest(CommunicationState.Okay);

        var state = _manager.State;

        Assert.Equal(PowerState.On, state.PowerState);
        Assert.Equal(PowerState.Off, state.DesiredPowerState);
        Assert.Equal(CommunicationState.Okay, state.CommunicationState);
        Assert.False(state.IsActiveSource);
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

        _device.SetPowerStateForTest(PowerState.Unknown);

        handler.Verify(h => h.Invoke(It.IsAny<SetTopBoxState>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_ReachesTheDevice(string command, Action<SetTopBoxManager> send, Func<TestSetTopBox, bool> received)
    {
        send(_manager);

        Assert.True(received(_device), $"{command} did not reach the set-top box");
    }

    [Fact]
    public void AppleTvCec_ActiveSourceChange_ReflectsInStateAndRaisesStateChanged()
    {
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
