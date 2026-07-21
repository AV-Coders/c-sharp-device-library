using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Context;

namespace AVCoders.SignalR.Volume;

public class VolumeHub : Hub<IVolumeHub>
{
    private static readonly ConcurrentDictionary<string, VolumeManager> VolumeManagers = new();

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
                Log.Information("Setting volume level for {Group}[{Index}] to {Level}", groupName, index, level);
                volumeManager.SetVolumeLevel(index, level);
            });
    }

    public void SetVolumeMute(string groupName, int index, MuteState mute)
    {
        if (VolumeManagers.TryGetValue(groupName, out var volumeManager))
            Dispatch(groupName, "SetVolumeMute", () =>
            {
                Log.Information("Setting volume mute for {Group}[{Index}] to {Mute}", groupName, index, mute);
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
