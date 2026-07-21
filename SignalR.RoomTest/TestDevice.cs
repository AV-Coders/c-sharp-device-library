using AVCoders.Core;

namespace AVCoders.SignalR.Room.Tests;

/// <summary>
/// Concrete <see cref="DeviceBase"/> used by the SignalR.Room tests. PowerOn and
/// PowerOff write to the protected base setter so the PowerStateHandlers chain
/// fires (which is how <see cref="RoomManager"/> observes per-device changes).
/// </summary>
public class TestDevice : DeviceBase
{
    public int PowerOnCallCount;
    public int PowerOffCallCount;

    public TestDevice(string name = "TestDevice")
        : base(name, CommunicationClient.None)
    {
    }

    public override void PowerOn()
    {
        PowerOnCallCount++;
        PowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        PowerOffCallCount++;
        PowerState = PowerState.Off;
    }

    public void SetPowerStateForTest(PowerState state) => PowerState = state;
}
