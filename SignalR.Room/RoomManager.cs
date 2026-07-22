using System.Collections.Concurrent;
using AVCoders.Core;

namespace AVCoders.SignalR.Room;

public class RoomManager : DeviceBase
{
    private readonly DeviceBase _device;
    private readonly ConcurrentDictionary<string, string> _properties = new();

    public event Action<string, string>? OnPropertyChanged;
    public event Action<Dictionary<string, string>>? OnPowerOnRequested;
    public event Action<Dictionary<string, string>>? OnPowerOffRequested;

    public Dictionary<string, string> Properties => new(_properties);

    public RoomManager(DeviceBase device)
        : base(device.Name, CommunicationClient.None)
    {
        _device = device;
        _device.PowerStateHandlers += state => PowerState = state;
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
}
