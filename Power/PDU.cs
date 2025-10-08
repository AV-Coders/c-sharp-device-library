using AVCoders.Core;

namespace AVCoders.Power;

public delegate void OutletDefinitionHandler(List<Outlet> outlets);

public abstract class Pdu : DeviceBase
{
    public List<Outlet> Outlets => _outlets;
    public OutletDefinitionHandler? OutletDefinitionHandlers;
    private List<Outlet> _outlets;

    protected Pdu(string name, CommunicationClient comms) 
        : base(name, comms)
    {
        _outlets = [];
    }

    protected void ClearOutlets() => _outlets.Clear();
    
    protected void AddOutlet(Outlet outlet) => _outlets.Add(outlet);
    
    
}