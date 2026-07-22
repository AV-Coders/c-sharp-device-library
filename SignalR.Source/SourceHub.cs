using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Source;

public class SourceHub : Hub<ISourceHub>
{
    private static readonly ConcurrentDictionary<string, SourceManager> SourceManagers = new();

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<SourceHub>();

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
                Logger.LogTrace("Selecting source {Index} for group {Group}", sourceIndex, groupName);
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
            using (Logger.BeginScope(new Dictionary<string, object>
                   { ["Class"] = nameof(SourceHub), [LogBase.MethodProperty] = methodName }))
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
