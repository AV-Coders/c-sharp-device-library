using AVCoders.Core;

namespace AVCoders.Power;

public delegate void OutletDefinitionHandler(List<Outlet> outlets);

public abstract class Pdu : DeviceBase
{
    protected List<Outlet> Outlets;
    public OutletDefinitionHandler? OutletDefinitionHandlers;

    protected Pdu(string name, CommunicationClient comms, CommandStringFormat commandStringFormat) 
        : base(name, comms, commandStringFormat)
    {
        Outlets = [];
    }
}