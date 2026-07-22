using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Room;

public class RoomUiSignalR : DeviceBase
{
    private readonly RoomManager _roomManager;
    private readonly IHubContext<RoomHub, IRoomHub> _hubContext;

    public RoomUiSignalR(RoomManager roomManager, IHubContext<RoomHub, IRoomHub> hubContext)
        : base(roomManager.Name, CommunicationClient.None)
    {
        _roomManager = roomManager;
        _hubContext = hubContext;
        RoomHub.RegisterRoomManager(Name, roomManager);

        _roomManager.PowerStateHandlers += OnRoomPowerStateChanged;
        _roomManager.OnPropertyChanged += OnRoomPropertyChanged;
    }

    private async void OnRoomPowerStateChanged(PowerState state)
    {
        await _hubContext.Clients.Group(Name).OnPowerStateChanged(state);
    }

    private async void OnRoomPropertyChanged(string key, string value)
    {
        await _hubContext.Clients.Group(Name).OnPropertyChanged(key, value);
    }

    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            LogInformation("Powering on room");
            _roomManager.PowerOn();
        }
    }

    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            LogInformation("Powering off room");
            _roomManager.PowerOff();
        }
    }
}
