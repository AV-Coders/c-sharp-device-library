using AVCoders.Core;

namespace AVCoders.Power;

public abstract class Outlet : DeviceBase
{
    public string Name { protected set; get; }

    protected Outlet(string name)
    {
        Name = name;
    }

    public void OverridePowerState(PowerState state)
    {
        PowerState = state;
    }
    
    public abstract void Reboot();
}