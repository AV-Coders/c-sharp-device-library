using System.Collections.Concurrent;
using AVCoders.Core;
using AVCoders.MediaPlayer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.SetTopBox;

// SignalR only dispatches public instance methods, so hub methods must stay instance
// methods even when they touch no instance state - making them static breaks client calls.
#pragma warning disable S2325 // Methods should be static
public class SetTopBoxHub : Hub<ISetTopBoxHub>
{
    private static readonly ConcurrentDictionary<string, SetTopBoxManager> SetTopBoxManagers = new();

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<SetTopBoxHub>();

    public static void RegisterSetTopBoxManager(string groupName, SetTopBoxManager setTopBoxManager)
    {
        SetTopBoxManagers[groupName] = setTopBoxManager;
    }

    public List<string> GetGroups() => SetTopBoxManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (SetTopBoxManagers.TryGetValue(groupName, out var setTopBoxManager))
        {
            await Clients.Caller.UpdateSetTopBox(setTopBoxManager.State);
        }
    }

    public Task<SetTopBoxState?> GetState(string groupName)
    {
        if (TryGetManager(groupName, "GetState", out var setTopBoxManager))
            return Task.FromResult<SetTopBoxState?>(setTopBoxManager.State);
        return Task.FromResult<SetTopBoxState?>(null);
    }

    /// <summary>
    /// Sends a <see cref="RemoteButton"/> by name (case-insensitive). Unknown or unsupported
    /// buttons are logged and dropped rather than thrown back to the client.
    /// </summary>
    public Task SendRemoteButton(string groupName, string button)
    {
        if (!TryGetManager(groupName, "SendRemoteButton", out var setTopBoxManager))
            return Task.CompletedTask;

        if (!Enum.TryParse<RemoteButton>(button, ignoreCase: true, out var remoteButton) || !Enum.IsDefined(remoteButton))
        {
            Logger.LogWarning("Unknown remote button {Button} for group {Group}", button, groupName);
            return Task.CompletedTask;
        }

        if (!setTopBoxManager.SupportedButtons.Contains(remoteButton))
        {
            Logger.LogWarning("Remote button {Button} is not supported by group {Group}", remoteButton, groupName);
            return Task.CompletedTask;
        }

        Dispatch(groupName, "SendRemoteButton", () =>
        {
            Logger.LogTrace("Sending {Button} to group {Group}", remoteButton, groupName);
            setTopBoxManager.SendRemoteButton(remoteButton);
        });
        return Task.CompletedTask;
    }

    public Task PowerOn(string groupName)
    {
        if (TryGetManager(groupName, "PowerOn", out var setTopBoxManager))
            Dispatch(groupName, "PowerOn", () =>
            {
                Logger.LogInformation("Powering on {Group}", groupName);
                setTopBoxManager.PowerOn();
            });
        return Task.CompletedTask;
    }

    public Task PowerOff(string groupName)
    {
        if (TryGetManager(groupName, "PowerOff", out var setTopBoxManager))
            Dispatch(groupName, "PowerOff", () =>
            {
                Logger.LogInformation("Powering off {Group}", groupName);
                setTopBoxManager.PowerOff();
            });
        return Task.CompletedTask;
    }

    private static bool TryGetManager(string groupName, string methodName, out SetTopBoxManager setTopBoxManager)
    {
        if (SetTopBoxManagers.TryGetValue(groupName, out setTopBoxManager!))
            return true;
        Logger.LogWarning("{Method} ignored: no set-top box registered for group {Group}", methodName, groupName);
        return false;
    }

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work (including the driver's key hold) runs off the
    // SignalR dispatch thread, and the resulting state is pushed back via the
    // ISetTopBoxHub callbacks. Any exception is logged here rather than lost on an
    // unobserved Task.
    private static void Dispatch(string groupName, string methodName, Action work)
    {
        _ = Task.Run(() =>
        {
            using (Logger.BeginScope(new Dictionary<string, object>
                   { ["Class"] = nameof(SetTopBoxHub), [LogBase.MethodProperty] = methodName }))
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
