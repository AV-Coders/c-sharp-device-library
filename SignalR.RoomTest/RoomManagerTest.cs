using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Room.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class RoomManagerTest
{
    private readonly TestDevice _device;
    private readonly RoomManager _manager;

    public RoomManagerTest()
    {
        _device = new TestDevice("Boardroom");
        _manager = new RoomManager(_device);
    }

    public static TheoryData<string, Action<RoomManager>, Func<TestDevice, bool>> Commands => new()
    {
        { "PowerOn", manager => manager.PowerOn(), device => device.PowerOnCallCount == 1 },
        { "PowerOff", manager => manager.PowerOff(), device => device.PowerOffCallCount == 1 },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_ReachesTheDevice(string command, Action<RoomManager> send, Func<TestDevice, bool> received)
    {
        send(_manager);

        Assert.True(received(_device), $"{command} did not reach the device");
    }

    [Fact]
    public void DevicePowerStateChange_PropagatesToManagerPowerState()
    {
        _device.SetPowerStateForTest(PowerState.On);

        Assert.Equal(PowerState.On, _manager.PowerState);
    }

    [Fact]
    public void PowerState_IsSeededFromTheDevice()
    {
        var device = new TestDevice("Boardroom");
        device.SetPowerStateForTest(PowerState.On);

        var manager = new RoomManager(device);

        Assert.Equal(PowerState.On, manager.PowerState);
    }

    [Fact]
    public void DesiredPowerState_IsSeededFromTheDevice()
    {
        var device = new TestDevice("Boardroom");
        device.SetDesiredPowerStateForTest(PowerState.Off);

        var manager = new RoomManager(device);

        Assert.Equal(PowerState.Off, manager.DesiredPowerState);
    }

    [Fact]
    public void DeviceDesiredPowerStateChange_PropagatesToManagerDesiredPowerState()
    {
        var handler = new Mock<PowerStateHandler>();
        _manager.DesiredPowerStateHandlers += handler.Object;

        _device.SetDesiredPowerStateForTest(PowerState.On);

        Assert.Equal(PowerState.On, _manager.DesiredPowerState);
        handler.Verify(h => h.Invoke(PowerState.On), Times.Once);
    }

    [Fact]
    public void PowerOn_PropagatesPowerStateThroughHandler()
    {
        var handler = new Mock<PowerStateHandler>();
        _manager.PowerStateHandlers += handler.Object;

        _manager.PowerOn();

        Assert.Equal(PowerState.On, _manager.PowerState);
        handler.Verify(h => h.Invoke(PowerState.On), Times.Once);
    }

    [Fact]
    public void SetProperty_StoresValue()
    {
        _manager.SetProperty("status", "in-meeting");

        Assert.Equal("in-meeting", _manager.GetProperties()["status"]);
    }

    [Fact]
    public void SetProperty_OverwritesExistingKey()
    {
        _manager.SetProperty("status", "in-meeting");
        _manager.SetProperty("status", "available");

        Assert.Equal("available", _manager.GetProperties()["status"]);
        Assert.Single(_manager.GetProperties());
    }

    [Fact]
    public void SetProperty_RaisesPropertyChanged()
    {
        var handler = new Mock<Action<string, string>>();
        _manager.OnPropertyChanged += handler.Object;

        _manager.SetProperty("status", "in-meeting");

        handler.Verify(h => h.Invoke("status", "in-meeting"), Times.Once);
    }

    [Fact]
    public void Properties_ReturnsCopyNotReference()
    {
        _manager.SetProperty("status", "in-meeting");
        var snapshot = _manager.GetProperties();

        snapshot["status"] = "tampered";

        Assert.Equal("in-meeting", _manager.GetProperties()["status"]);
    }

    [Fact]
    public void ConcurrentSetPropertyAndSnapshot_DoesNotThrow()
    {
        var exception = Record.Exception(() => Parallel.For(0, 10_000, i =>
        {
            _manager.SetProperty($"key-{i % 20}", $"value-{i}");
            _ = _manager.GetProperties();
        }));

        Assert.Null(exception);
    }

    [Fact]
    public void PowerOnWithArgs_RaisesPowerOnRequestedWithArgs()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOnRequested += handler.Object;
        var args = new Dictionary<string, string> { ["reason"] = "scheduled" };

        _manager.PowerOnWithArgs(args);

        handler.Verify(h => h.Invoke(args), Times.Once);
    }

    [Fact]
    public void PowerOnWithArgs_DoesNotForwardToDevice()
    {
        _manager.PowerOnWithArgs(new Dictionary<string, string>());

        Assert.Equal(0, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOffWithArgs_RaisesPowerOffRequestedWithArgs()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOffRequested += handler.Object;
        var args = new Dictionary<string, string> { ["reason"] = "idle" };

        _manager.PowerOffWithArgs(args);

        handler.Verify(h => h.Invoke(args), Times.Once);
    }

    [Fact]
    public void PowerOffWithArgs_DoesNotForwardToDevice()
    {
        _manager.PowerOffWithArgs(new Dictionary<string, string>());

        Assert.Equal(0, _device.PowerOffCallCount);
    }

    [Fact]
    public void PowerOn_DoesNotRaisePowerOnRequested()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOnRequested += handler.Object;

        _manager.PowerOn();

        handler.Verify(h => h.Invoke(It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public void PowerOff_DoesNotRaisePowerOffRequested()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOffRequested += handler.Object;

        _manager.PowerOff();

        handler.Verify(h => h.Invoke(It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public void Link_StartsUnlinked_WithNoPartner_AndCantStartALink()
    {
        Assert.Equal(new RoomLinkStatus(false, RoomLinkState.Unlinked, string.Empty), _manager.Link);
    }

    [Fact]
    public void LinkSetters_RaiseOnLinkChangedWithTheWholeStatus()
    {
        var changes = new List<RoomLinkStatus>();
        _manager.OnLinkChanged += changes.Add;

        _manager.SetLinkSupported(true);
        _manager.SetLinkPartner("Room B");
        _manager.SetLinkState(RoomLinkState.Leading);

        Assert.Equal(
        [
            new RoomLinkStatus(true, RoomLinkState.Unlinked, string.Empty),
            new RoomLinkStatus(true, RoomLinkState.Unlinked, "Room B"),
            new RoomLinkStatus(true, RoomLinkState.Leading, "Room B")
        ], changes);
        Assert.Equal(changes[^1], _manager.Link);
    }

    [Fact]
    public void LinkSetter_WithNoChange_RaisesNothing()
    {
        var changes = new List<RoomLinkStatus>();
        _manager.OnLinkChanged += changes.Add;

        _manager.SetLinkState(RoomLinkState.Unlinked);
        _manager.SetLinkSupported(false);
        _manager.SetLinkPartner(string.Empty);

        Assert.Empty(changes);
    }

    [Fact]
    public void LinkRooms_WhenItCanStartALinkAndIsOn_RaisesLinkRoomsRequested()
    {
        var raised = 0;
        _manager.OnLinkRoomsRequested += () => raised++;
        _manager.SetLinkSupported(true);
        _device.SetPowerStateForTest(PowerState.On);

        _manager.LinkRooms();

        Assert.Equal(1, raised);
    }

    public static TheoryData<bool, RoomLinkState, PowerState, string> RefusedLinks => new()
    {
        { false, RoomLinkState.Unlinked, PowerState.On, "it can't start a link" },
        { true, RoomLinkState.Leading, PowerState.On, "it is already Leading" },
        { false, RoomLinkState.Following, PowerState.On, "it can't start a link" },
        { true, RoomLinkState.Unlinked, PowerState.Off, "it starts a link only while on" },
        { true, RoomLinkState.Unlinked, PowerState.Unknown, "it starts a link only while on" },
    };

    [Theory]
    [MemberData(nameof(RefusedLinks))]
    public void LinkRooms_ThatTheRoomCantActOn_IsLoggedWithItsValues_AndNotRaised(bool supported, RoomLinkState state, PowerState power, string reason)
    {
        var (manager, device, logs) = LoggedManager();
        var raised = 0;
        manager.OnLinkRoomsRequested += () => raised++;
        manager.SetLinkSupported(supported);
        manager.SetLinkPartner("Room B");
        manager.SetLinkState(state);
        device.SetPowerStateForTest(power);

        manager.LinkRooms();

        Assert.Equal(0, raised);
        var warning = Assert.Single(logs, log => log.Level == LogLevel.Warning);
        Assert.Equal(
            $"Ignoring a request to link Boardroom: {reason}. Supports linking {supported}, link {state}, partner \"Room B\", power {power}",
            warning.Message);
    }

    [Theory]
    [InlineData(RoomLinkState.Leading, true)]
    [InlineData(RoomLinkState.Following, false)]
    public void UnlinkRooms_WhileLinked_RaisesUnlinkRoomsRequested(RoomLinkState state, bool supported)
    {
        var raised = 0;
        _manager.OnUnlinkRoomsRequested += () => raised++;
        _manager.SetLinkSupported(supported);
        _manager.SetLinkState(state);

        _manager.UnlinkRooms();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void UnlinkRooms_WhileUnlinked_IsLoggedWithItsValues_AndNotRaised()
    {
        var (manager, device, logs) = LoggedManager();
        var raised = 0;
        manager.OnUnlinkRoomsRequested += () => raised++;
        manager.SetLinkSupported(true);
        manager.SetLinkPartner("Room B");
        device.SetPowerStateForTest(PowerState.On);

        manager.UnlinkRooms();

        Assert.Equal(0, raised);
        var warning = Assert.Single(logs, log => log.Level == LogLevel.Warning);
        Assert.Equal(
            "Ignoring a request to unlink Boardroom: it isn't linked. Supports linking True, link Unlinked, partner \"Room B\", power On",
            warning.Message);
    }

    private static (RoomManager Manager, TestDevice Device, ConcurrentQueue<CapturedLog> Logs) LoggedManager()
    {
        var logs = new ConcurrentQueue<CapturedLog>();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = new CapturingLoggerFactory(logs);
        try
        {
            var device = new TestDevice("Boardroom");
            return (new RoomManager(device), device, logs);
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }
}
