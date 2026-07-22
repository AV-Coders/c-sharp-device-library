using System.Collections.Concurrent;
using AVCoders.Camera;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Camera;

public class CameraHub : Hub<ICameraHub>
{
    private static readonly ConcurrentDictionary<string, CameraManager> CameraManagers = new();

    // Resolved per use so the hub honours whatever LogBase.LoggerFactory consumers set at
    // startup, regardless of when this type is first touched. CreateLogger caches per category.
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<CameraHub>();
    
    public static void RegisterCameraManager(string groupName, CameraManager cameraManager)
    {
        CameraManagers[groupName] = cameraManager;
    }

    public List<string> GetGroups() => CameraManagers.Keys.ToList();

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        if (CameraManagers.TryGetValue(groupName, out var cameraManager))
        {
            await Clients.Caller.OnPowerStateChanged(cameraManager.PowerState);
            await Clients.Caller.OnPresetDefinitionChanged(cameraManager.PresetDefinitions());
            if (cameraManager.LastRecalledPreset == -1)
                await Clients.Caller.OnPresetCleared();
            else
                await Clients.Caller.OnPresetRecalled(cameraManager.LastRecalledPreset);
            await Clients.Caller.OnTrackingCapabilityChanged(cameraManager.SupportsTracking);
            if (cameraManager.SupportsTracking)
                await Clients.Caller.OnTrackingModeChanged(cameraManager.TrackingMode);
        }
    }

    public void RecallPreset(string groupName, int index)
    {
        if (CameraManagers.TryGetValue(groupName, out var cameraManager))
            Dispatch(groupName, "RecallPreset", () =>
            {
                Logger.LogTrace("Recalling preset {Index} on camera {Group}", index, groupName);
                cameraManager.RecallPreset(index);
            });
    }

    public void SavePreset(string groupName, int index)
    {
        if (CameraManagers.TryGetValue(groupName, out var cameraManager))
            Dispatch(groupName, "SavePreset", () =>
            {
                Logger.LogTrace("Saving preset {Index} on camera {Group}", index, groupName);
                cameraManager.SavePreset(index);
            });
    }

    public void ZoomStop(string groupName) => InvokeCameraAction(groupName, "ZoomStop", c => c.ZoomStop());
    public void ZoomIn(string groupName) => InvokeCameraAction(groupName, "ZoomIn", c => c.ZoomIn());
    public void ZoomOut(string groupName) => InvokeCameraAction(groupName, "ZoomOut", c => c.ZoomOut());
    public void PanTiltStop(string groupName) => InvokeCameraAction(groupName, "PanTiltStop", c => c.PanTiltStop());
    public void PanTiltUp(string groupName) => InvokeCameraAction(groupName, "PanTiltUp", c => c.PanTiltUp());
    public void PanTiltDown(string groupName) => InvokeCameraAction(groupName, "PanTiltDown", c => c.PanTiltDown());
    public void PanTiltLeft(string groupName) => InvokeCameraAction(groupName, "PanTiltLeft", c => c.PanTiltLeft());
    public void PanTiltRight(string groupName) => InvokeCameraAction(groupName, "PanTiltRight", c => c.PanTiltRight());
    public void SetAutoFocus(string groupName, PowerState state) => InvokeCameraAction(groupName, "SetAutoFocus", c => c.SetAutoFocus(state));
    public void SetTracking(string groupName, CameraTrackingMode mode) => InvokeCameraAction(groupName, "SetTracking", c => c.SetTracking(mode));

    private static void InvokeCameraAction(string groupName, string methodName, Action<CameraManager> action)
    {
        if (CameraManagers.TryGetValue(groupName, out var cameraManager))
            Dispatch(groupName, methodName, () =>
            {
                Logger.LogTrace("{MethodName} on camera {Group}", methodName, groupName);
                action(cameraManager);
            });
    }

    // Commands are fire-and-forget: the hub method returns immediately so the client
    // gets a fast ack, the device work runs off the SignalR dispatch thread, and the
    // resulting state is pushed back via the ICameraHub callbacks. Any exception is
    // logged here rather than lost on an unobserved Task.
    private static void Dispatch(string groupName, string methodName, Action work)
    {
        _ = Task.Run(() =>
        {
            using (Logger.BeginScope(new Dictionary<string, object>
                   { ["Class"] = nameof(CameraHub), [LogBase.MethodProperty] = methodName }))
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