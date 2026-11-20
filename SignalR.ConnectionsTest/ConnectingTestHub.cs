using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Connections.Tests;

public interface IConnectingTestHub;

public class ConnectingTestHub() : GroupHub<IConnectingTestHub>(Connections)
{
    public const string RoomQuery = "room";

    public static HubConnections Connections { get; } = new(nameof(ConnectingTestHub));

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        await AddToGroup(Context.GetHttpContext()!.Request.Query[RoomQuery].ToString());
    }
}
