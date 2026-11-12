using System.Collections.Concurrent;
using AVCoders.Core;
using AVCoders.SignalR.Connections;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Room;

// SignalR only dispatches public instance methods, so hub methods must stay instance
// methods even when they touch no instance state - making them static breaks client calls.
#pragma warning disable S2325 // Methods should be static
public class RoomHub() : GroupHub<IRoomHub>(Connections)
{
    private static readonly ConcurrentDictionary<string, RoomManager> RoomManagers = new();

    public static HubConnections Connections { get; } = new(nameof(RoomHub));

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<RoomHub>();

    public static void RegisterRoomManager(string groupName, RoomManager roomManager)
    {
        RoomManagers[groupName] = roomManager;
        Connections.Register(groupName);
    }

    public List<string> GetGroups() => RoomManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await AddToGroup(groupName);
        if (!RoomManagers.TryGetValue(groupName, out var roomManager))
        {
            ReportUnknownGroup(groupName);
            return;
        }
        await Clients.Caller.OnPowerStateChanged(roomManager.PowerState);
        await Clients.Caller.OnPropertiesSnapshot(roomManager.GetProperties());
    }

    public void PowerOn(string groupName)
    {
        if (!RoomManagers.TryGetValue(groupName, out var roomManager))
        {
            ReportUnknownGroup(groupName);
            return;
        }
        Dispatch(groupName, "PowerOn", () =>
        {
            Logger.LogInformation("Powering on {Group}", groupName);
            roomManager.PowerOn();
        });
    }

    public void PowerOnWithArgs(string groupName, Dictionary<string, string> args)
    {
        if (!RoomManagers.TryGetValue(groupName, out var roomManager))
        {
            ReportUnknownGroup(groupName);
            return;
        }
        Dispatch(groupName, "PowerOnWithArgs", () =>
        {
            Logger.LogInformation("Powering on {Group} with {@Args}", groupName, args);
            roomManager.PowerOnWithArgs(args ?? new Dictionary<string, string>());
        });
    }

    public void PowerOff(string groupName)
    {
        if (!RoomManagers.TryGetValue(groupName, out var roomManager))
        {
            ReportUnknownGroup(groupName);
            return;
        }
        Dispatch(groupName, "PowerOff", () =>
        {
            Logger.LogInformation("Powering off {Group}", groupName);
            roomManager.PowerOff();
        });
    }

    public void PowerOffWithArgs(string groupName, Dictionary<string, string> args)
    {
        if (!RoomManagers.TryGetValue(groupName, out var roomManager))
        {
            ReportUnknownGroup(groupName);
            return;
        }
        Dispatch(groupName, "PowerOffWithArgs", () =>
        {
            Logger.LogInformation("Powering off {Group} with {@Args}", groupName, args);
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
            using (Logger.BeginScope(new Dictionary<string, object?>
                   {
                       ["Class"] = nameof(RoomHub),
                       [LogBase.MethodProperty] = methodName,
                       [LogBase.ActorProperty] = Actor.Current?.ToString(),
                       [LogBase.TriggerIdProperty] = Trigger.Id
                   }))
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "{Method} failed for group {Group}", methodName, groupName);
                }
            }
        });
    }
}
