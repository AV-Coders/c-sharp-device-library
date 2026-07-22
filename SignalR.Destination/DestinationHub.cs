using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Destination;

public class DestinationHub : Hub<IDestinationHub>
{
    private static readonly ConcurrentDictionary<string, DestinationManager> DestinationManagers = new();

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<DestinationHub>();

    public static void RegisterDestinationManager(string groupName, DestinationManager destinationManager)
    {
        DestinationManagers[groupName] = destinationManager;
    }

    public List<string> GetGroups() => DestinationManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (DestinationManagers.TryGetValue(groupName, out var destinationManager))
        {
            await Clients.Caller.OnDestinationChanged(destinationManager.Snapshot);
        }
    }

    public void RouteSource(string groupName, string sourceId)
    {
        if (DestinationManagers.TryGetValue(groupName, out var destinationManager))
            Dispatch(groupName, "RouteSource", () =>
            {
                Logger.LogTrace("Routing source {SourceId} to destination {Group}", sourceId, groupName);
                destinationManager.RouteSource(sourceId);
            });
    }

    public void SetVideoMute(string groupName, bool muted)
    {
        if (DestinationManagers.TryGetValue(groupName, out var destinationManager))
            Dispatch(groupName, "SetVideoMute", () =>
            {
                Logger.LogTrace("Setting video mute on destination {Group} to {Muted}", groupName, muted);
                destinationManager.SetVideoMute(muted);
            });
    }

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work runs off the SignalR dispatch thread, and the
    // resulting state is pushed back via the IDestinationHub callbacks. Any exception
    // is logged here rather than lost on an unobserved Task.
    private static void Dispatch(string groupName, string methodName, Action work)
    {
        _ = Task.Run(() =>
        {
            using (Logger.BeginScope(new Dictionary<string, object>
                   { ["Class"] = nameof(DestinationHub), [LogBase.MethodProperty] = methodName }))
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
