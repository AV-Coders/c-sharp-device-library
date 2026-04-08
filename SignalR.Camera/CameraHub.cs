using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Context;

namespace AVCoders.SignalR.Camera;

public class CameraHub : Hub<ICameraHub>
{
    private static readonly Dictionary<string, CameraManager> CameraManagers = [];
    
    public static void RegisterCameraManager(string groupName, CameraManager cameraManager)
    {
        CameraManagers[groupName] = cameraManager;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.OnCameraManagersChanged(CameraManagers.Keys.ToList());
        await base.OnConnectedAsync();
    }

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
             
        }
    }

    public void RecallPreset(string groupName, int index)
    {
        using (LogContext.PushProperty(LogBase.MethodProperty, "RecallPreset"))
        {
            if (CameraManagers.TryGetValue(groupName, out var cameraManager))
            {
                Log.Verbose("Recalling preset {Index} on camera {Group}", index, groupName);
                cameraManager.RecallPreset(index);
            }
        }
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

    private static void InvokeCameraAction(string groupName, string methodName, Action<CameraManager> action)
    {
        using (LogContext.PushProperty(LogBase.MethodProperty, methodName))
        {
            if (CameraManagers.TryGetValue(groupName, out var cameraManager))
            {
                Log.Verbose("{MethodName} on camera {Group}", methodName, groupName);
                action(cameraManager);
            }
        }
    }
}