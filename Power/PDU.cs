using AVCoders.Core;

namespace AVCoders.Power;

public delegate void OutletDefinitionHandler(List<Outlet> outlets);

public abstract class Pdu : DeviceBase
{
    protected string Name;
    protected List<Outlet> Outlets;
    public OutletDefinitionHandler? OutletDefinitionHandlers;

    protected Pdu(string name)
    {
        Name = name;
        Outlets = new List<Outlet>();
    }
}