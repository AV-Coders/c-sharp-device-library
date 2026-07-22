using System.Collections.Concurrent;
using AVCoders.Core;
using AVCoders.Display;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Display;

public class DisplayHub : Hub<IDisplayHub>
{
    private static readonly ConcurrentDictionary<string, DisplayManager> DisplayManagers = new();

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<DisplayHub>();

    public static void RegisterDisplayManager(string groupName, DisplayManager displayManager)
    {
        DisplayManagers[groupName] = displayManager;
    }

    public List<string> GetGroups() => DisplayManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (DisplayManagers.TryGetValue(groupName, out var displayManager))
        {
            await Clients.Caller.OnPowerStateChanged(displayManager.PowerState);
            await Clients.Caller.OnSupportedInputsChanged(displayManager.SupportedInputs);
            await Clients.Caller.OnInputChanged(displayManager.Input);
            await Clients.Caller.OnVolumeChanged(displayManager.Volume);
            await Clients.Caller.OnAudioMuteChanged(displayManager.AudioMute);
        }
    }

    public void PowerOn(string groupName) => InvokeDisplayAction(groupName, "PowerOn", d => d.PowerOn());
    public void PowerOff(string groupName) => InvokeDisplayAction(groupName, "PowerOff", d => d.PowerOff());
    public void TogglePower(string groupName) => InvokeDisplayAction(groupName, "TogglePower", d => d.TogglePower());
    public void SetInput(string groupName, Input input) => InvokeDisplayAction(groupName, "SetInput", d => d.SetInput(input));
    public void SetVolume(string groupName, int volume) => InvokeDisplayAction(groupName, "SetVolume", d => d.SetVolume(volume));
    public void LevelUp(string groupName, int amount) => InvokeDisplayAction(groupName, "LevelUp", d => d.LevelUp(amount));
    public void LevelDown(string groupName, int amount) => InvokeDisplayAction(groupName, "LevelDown", d => d.LevelDown(amount));
    public void SetAudioMute(string groupName, MuteState state) => InvokeDisplayAction(groupName, "SetAudioMute", d => d.SetAudioMute(state));
    public void ToggleAudioMute(string groupName) => InvokeDisplayAction(groupName, "ToggleAudioMute", d => d.ToggleAudioMute());

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work runs off the SignalR dispatch thread, and the
    // resulting state is pushed back via the IDisplayHub callbacks. Any exception is
    // logged here rather than lost on an unobserved Task.
    private static void InvokeDisplayAction(string groupName, string methodName, Action<DisplayManager> action)
    {
        if (!DisplayManagers.TryGetValue(groupName, out var displayManager))
            return;

        _ = Task.Run(() =>
        {
            using (Logger.BeginScope(new Dictionary<string, object>
                   { ["Class"] = nameof(DisplayHub), [LogBase.MethodProperty] = methodName }))
            {
                try
                {
                    Logger.LogTrace("{MethodName} on display {Group}", methodName, groupName);
                    action(displayManager);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "{MethodName} failed on display {Group}", methodName, groupName);
                }
            }
        });
    }
}
