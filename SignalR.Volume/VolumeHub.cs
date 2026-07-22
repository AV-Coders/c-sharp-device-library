using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Volume;

public class VolumeHub : Hub<IVolumeHub>
{
    private static readonly ConcurrentDictionary<string, VolumeManager> VolumeManagers = new();

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<VolumeHub>();

    public static void RegisterVolumeManager(string groupName, VolumeManager volumeManager)
    {
        VolumeManagers[groupName] = volumeManager;
    }

    public List<string> GetGroups() => VolumeManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (VolumeManagers.TryGetValue(groupName, out var volumeManager))
        {
            await Clients.Caller.OnVolumeControlsChanged(volumeManager.VolumeControls);
        }
    }

    public void SetVolumeLevel(string groupName, int index, ushort level)
    {
        if (VolumeManagers.TryGetValue(groupName, out var volumeManager))
            Dispatch(groupName, "SetVolumeLevel", () =>
            {
                Logger.LogInformation("Setting volume level for {Group}[{Index}] to {Level}", groupName, index, level);
                volumeManager.SetVolumeLevel(index, level);
            });
    }

    public void SetVolumeMute(string groupName, int index, MuteState mute)
    {
        if (VolumeManagers.TryGetValue(groupName, out var volumeManager))
            Dispatch(groupName, "SetVolumeMute", () =>
            {
                Logger.LogInformation("Setting volume mute for {Group}[{Index}] to {Mute}", groupName, index, mute);
                volumeManager.SetVolumeMute(index, mute);
            });
    }

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work runs off the SignalR dispatch thread, and the
    // resulting state is pushed back via the IVolumeHub callbacks. Any exception is
    // logged here rather than lost on an unobserved Task.
    private static void Dispatch(string groupName, string methodName, Action work)
    {
        _ = Task.Run(() =>
        {
            using (Logger.BeginScope(new Dictionary<string, object>
                   { ["Class"] = nameof(VolumeHub), [LogBase.MethodProperty] = methodName }))
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
