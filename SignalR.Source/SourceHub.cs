using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Context;

namespace AVCoders.SignalR.Source;

public class SourceHub : Hub<ISourceHub>
{
    private static readonly ConcurrentDictionary<string, SourceManager> SourceManagers = new();

    public static void RegisterSourceManager(string groupName, SourceManager sourceManager)
    {
        SourceManagers[groupName] = sourceManager;
    }

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (SourceManagers.TryGetValue(groupName, out var sourceManager))
        {
            await Clients.Caller.UpdateSourceList(sourceManager.Sources);
            var index = sourceManager.Sources.FindIndex(s => s.SourceId == sourceManager.CurrentSource);
            if (index != -1)
            {
                await Clients.Caller.UpdateSourceIndex(index);
            }
        }
    }

    public void SelectSource(string groupName, int sourceIndex)
    {
        if (SourceManagers.TryGetValue(groupName, out var sourceManager))
            Dispatch(groupName, "SelectSource", () =>
            {
                Log.Verbose("Selecting source {Index} for group {Group}", sourceIndex, groupName);
                sourceManager.SetCurrentSource(sourceIndex);
            });
    }

    public List<string> GetGroups() => SourceManagers.Keys.ToList();

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work runs off the SignalR dispatch thread, and the
    // resulting state is pushed back via the ISourceHub callbacks. Any exception is
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
