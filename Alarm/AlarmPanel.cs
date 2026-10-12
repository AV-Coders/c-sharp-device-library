using AVCoders.Core;

namespace AVCoders.Alarm;

public abstract class AlarmPanel : DeviceBase
{
    public readonly IReadOnlyList<Area> Areas;
    public readonly IReadOnlyList<Zone> Zones;
    public readonly IReadOnlyList<Relay> Relays;

    protected AlarmPanel(string name, CommunicationClient client, IReadOnlyList<Area> areas,
        IReadOnlyList<Zone> zones, IReadOnlyList<Relay> relays) : base(name, client)
    {
        Areas = areas;
        Zones = zones;
        Relays = relays;
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
}
