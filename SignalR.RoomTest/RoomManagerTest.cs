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

    [Fact]
    public void Name_MatchesUnderlyingDeviceName()
    {
        Assert.Equal("Boardroom", _manager.Name);
    }

    [Fact]
    public void PowerState_DefaultsToUnknown()
    {
        Assert.Equal(PowerState.Unknown, _manager.PowerState);
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
    public void DevicePowerStateChange_PropagatesToManagerPowerState()
    {
        _device.SetPowerStateForTest(PowerState.On);

        Assert.Equal(PowerState.On, _manager.PowerState);
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
    public void Properties_DefaultsToEmpty()
    {
        Assert.Empty(_manager.Properties);
    }

    [Fact]
    public void SetProperty_StoresValue()
    {
        _manager.SetProperty("status", "in-meeting");

        Assert.Equal("in-meeting", _manager.Properties["status"]);
    }

    [Fact]
    public void SetProperty_OverwritesExistingKey()
    {
        _manager.SetProperty("status", "in-meeting");
        _manager.SetProperty("status", "available");

        Assert.Equal("available", _manager.Properties["status"]);
        Assert.Single(_manager.Properties);
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
        var snapshot = _manager.Properties;

        snapshot["status"] = "tampered";

        Assert.Equal("in-meeting", _manager.Properties["status"]);
    }

    [Fact]
    public void ConcurrentSetPropertyAndSnapshot_DoesNotThrow()
    {
        // SetProperty writes from device-callback threads while JoinGroup
        // copy-enumerates via the Properties getter on a dispatch thread:
        // against a plain Dictionary this throws/corrupts intermittently,
        // against the ConcurrentDictionary it is safe. Guards against
        // regressing M2.
        var exception = Record.Exception(() => Parallel.For(0, 10_000, i =>
        {
            _manager.SetProperty($"key-{i % 20}", $"value-{i}");
            _ = _manager.Properties;
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
