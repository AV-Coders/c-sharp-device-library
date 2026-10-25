using AVCoders.Core;

namespace AVCoders.Power;

public delegate void OutletDefinitionHandler(List<Outlet> outlets);

public abstract class Pdu : DeviceBase
{
    public List<Outlet> Outlets => _outlets;
    public OutletDefinitionHandler? OutletDefinitionHandlers;
    private volatile List<Outlet> _outlets;

    protected Pdu(string name, CommunicationClient comms) 
        : base(name, comms)
    {
        _outlets = [];
    }

    protected void SetOutlets(List<Outlet> outlets) => _outlets = outlets;

    [Obsolete("This method is deprecated in favor of SetOutlets")]
    protected void ClearOutlets() => SetOutlets([]);

    protected void AddOutlet(Outlet outlet) => _outlets.Add(outlet);
    
    
}