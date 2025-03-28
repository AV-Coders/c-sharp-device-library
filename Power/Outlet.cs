using AVCoders.Core;

namespace AVCoders.Power;

public abstract class Outlet : DeviceBase
{
    protected Outlet(string name) : base(name) { }

    public void OverridePowerState(PowerState state)
    {
        PowerState = state;
    }
    
    public abstract void Reboot();
}