using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Context;

namespace AVCoders.SignalR.Room;

public class RoomHub : Hub<IRoomHub>
{
    private static readonly ConcurrentDictionary<string, RoomManager> RoomManagers = new();

    public static void RegisterRoomManager(string groupName, RoomManager roomManager)
    {
        RoomManagers[groupName] = roomManager;
    }

    public List<string> GetGroups() => RoomManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (RoomManagers.TryGetValue(groupName, out var roomManager))
        {
            await Clients.Caller.OnPowerStateChanged(roomManager.PowerState);
            await Clients.Caller.OnPropertiesSnapshot(roomManager.Properties);
        }
    }

    public void PowerOn(string groupName)
    {
        if (RoomManagers.TryGetValue(groupName, out var roomManager))
            Dispatch(groupName, "PowerOn", () =>
            {
                Log.Information("Powering on {Group}", groupName);
                roomManager.PowerOn();
            });
    }

    public void PowerOnWithArgs(string groupName, Dictionary<string, string> args)
    {
        if (RoomManagers.TryGetValue(groupName, out var roomManager))
            Dispatch(groupName, "PowerOnWithArgs", () =>
            {
                Log.Information("Powering on {Group} with {@Args}", groupName, args);
                roomManager.PowerOnWithArgs(args ?? new Dictionary<string, string>());
            });
    }

    public void PowerOff(string groupName)
    {
        if (RoomManagers.TryGetValue(groupName, out var roomManager))
            Dispatch(groupName, "PowerOff", () =>
            {
                Log.Information("Powering off {Group}", groupName);
                roomManager.PowerOff();
            });
    }

    public void PowerOffWithArgs(string groupName, Dictionary<string, string> args)
    {
        if (RoomManagers.TryGetValue(groupName, out var roomManager))
            Dispatch(groupName, "PowerOffWithArgs", () =>
            {
                Log.Information("Powering off {Group} with {@Args}", groupName, args);
                roomManager.PowerOffWithArgs(args ?? new Dictionary<string, string>());
            });
    }

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work runs off the SignalR dispatch thread, and the
    // resulting state is pushed back via the IRoomHub callbacks. Any exception is
    // logged here rather than lost on an unobserved Task.
    private static void Dispatch(string groupName, string methodName, Action work)
    {
        _ = Task.Run(() =>
        {
            using (LogContext.PushProperty(LogBase.MethodProperty, methodName))
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    Log.Error(e, "{Method} failed for group {Group}", methodName, groupName);
                }
            }
        });
    }
}
