using System.Collections.Concurrent;
using AVCoders.Core;

namespace AVCoders.SignalR.Room;

public class RoomManager : DeviceBase
{
    private readonly DeviceBase _device;
    private readonly ConcurrentDictionary<string, string> _properties = new();
    private readonly object _linkLock = new();

    public event Action<string, string>? OnPropertyChanged;
    public event Action<Dictionary<string, string>>? OnPowerOnRequested;
    public event Action<Dictionary<string, string>>? OnPowerOffRequested;
    public event Action<RoomLinkStatus>? OnLinkChanged;
    public event Action? OnLinkRoomsRequested;
    public event Action? OnUnlinkRoomsRequested;

    /// <summary>Snapshot of the room's properties. A method so the copy cost is visible at the call site (S2365).</summary>
    public Dictionary<string, string> GetProperties() => new(_properties);

    public RoomLinkStatus Link { get; private set; } = new(false, RoomLinkState.Unlinked, string.Empty);

    public RoomManager(DeviceBase device)
        : base(device.Name, CommunicationClient.None)
    {
        _device = device;
        _device.PowerStateHandlers += state => PowerState = state;
        _device.DesiredPowerStateHandlers += state => DesiredPowerState = state;
        PowerState = _device.PowerState;
        DesiredPowerState = _device.DesiredPowerState;
    }

    public void SetProperty(string key, string value)
    {
        _properties[key] = value;
        OnPropertyChanged?.Invoke(key, value);
    }

    public override void PowerOn() => _device.PowerOn();
    public override void PowerOff() => _device.PowerOff();

    public void PowerOnWithArgs(Dictionary<string, string> args)
    {
        OnPowerOnRequested?.Invoke(args);
    }

    public void PowerOffWithArgs(Dictionary<string, string> args)
    {
        OnPowerOffRequested?.Invoke(args);
    }

    public void SetLinkSupported(bool supported) => UpdateLink(link => link with { SupportsLinking = supported });

    public void SetLinkPartner(string partner) => UpdateLink(link => link with { Partner = partner });

    public void SetLinkState(RoomLinkState state) => UpdateLink(link => link with { State = state });

    public void LinkRooms()
    {
        using var scope = PushProperties();
        var link = Link;
        var refusal = LinkRoomsRefusal(link);
        if (refusal != null)
        {
            LogWarning("Ignoring a request to link {Room}: {Reason}. Supports linking {SupportsLinking}, link {State}, partner \"{Partner}\", power {PowerState}",
                Name, refusal, link.SupportsLinking, link.State, link.Partner, PowerState);
            return;
        }
        OnLinkRoomsRequested?.Invoke();
    }

    public void UnlinkRooms()
    {
        using var scope = PushProperties();
        var link = Link;
        if (link.State == RoomLinkState.Unlinked)
        {
            LogWarning("Ignoring a request to unlink {Room}: it isn't linked. Supports linking {SupportsLinking}, link {State}, partner \"{Partner}\", power {PowerState}",
                Name, link.SupportsLinking, link.State, link.Partner, PowerState);
            return;
        }
        OnUnlinkRoomsRequested?.Invoke();
    }

    private string? LinkRoomsRefusal(RoomLinkStatus link) => link switch
    {
        { SupportsLinking: false } => "it can't start a link",
        { State: not RoomLinkState.Unlinked } => $"it is already {link.State}",
        _ when PowerState != PowerState.On => "it starts a link only while on",
        _ => null
    };

    private void UpdateLink(Func<RoomLinkStatus, RoomLinkStatus> change)
    {
        lock (_linkLock)
        {
            var link = change(Link);
            if (link == Link)
                return;
            Link = link;
            OnLinkChanged?.Invoke(link);
        }
    }
}
