using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Context;

namespace AVCoders.SignalR.Destination;

public class DestinationHub : Hub<IDestinationHub>
{
    private static readonly ConcurrentDictionary<string, DestinationManager> DestinationManagers = new();

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
                Log.Verbose("Routing source {SourceId} to destination {Group}", sourceId, groupName);
                destinationManager.RouteSource(sourceId);
            });
    }

    public void SetVideoMute(string groupName, bool muted)
    {
        if (DestinationManagers.TryGetValue(groupName, out var destinationManager))
            Dispatch(groupName, "SetVideoMute", () =>
            {
                Log.Verbose("Setting video mute on destination {Group} to {Muted}", groupName, muted);
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
