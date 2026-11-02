using AVCoders.Core;

namespace AVCoders.SignalR.Room.Tests;

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

    public void SetDesiredPowerStateForTest(PowerState state) => DesiredPowerState = state;
}
