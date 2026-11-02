using AVCoders.Core;

namespace AVCoders.SignalR.Room.Tests;

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
}
