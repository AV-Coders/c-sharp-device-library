using AVCoders.Core;

namespace AVCoders.SignalR.Room;

public interface IRoomHub
{
    Task OnPowerStateChanged(PowerState state);
    Task OnPropertiesSnapshot(Dictionary<string, string> properties);
    Task OnPropertyChanged(string key, string value);
}
