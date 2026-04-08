using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AVCoders.SignalR.Camera;

public class CameraUiSignalR : DeviceBase
{
    private readonly CameraManager _cameraManager;
    private readonly IHubContext<CameraHub, ICameraHub> _hubContext;

    public CameraUiSignalR(CameraManager cameraManager, IHubContext<CameraHub, ICameraHub> hubContext)
        : base(cameraManager.Name, CommunicationClient.None)
    {
        _cameraManager = cameraManager;
        _hubContext = hubContext;

        CameraHub.RegisterCameraManager(Name, cameraManager);
        _cameraManager.PowerStateHandlers += OnPowerStateChanged;
        _cameraManager.OnPresetRecalled += OnPresetRecalled;
        _cameraManager.OnPresetCleared += OnPresetCleared;
    }

    private async void OnPowerStateChanged(PowerState state) =>
        await _hubContext.Clients.Group(Name).OnPowerStateChanged(state);

    private async void OnPresetRecalled(int index) =>
        await _hubContext.Clients.Group(Name).OnPresetRecalled(index);

    private async void OnPresetCleared() =>
        await _hubContext.Clients.Group(Name).OnPresetCleared();

    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            Log.Information("Turning on camera");
            _cameraManager.PowerOn();
        }
    }

    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            Log.Information("Turning off camera");
            _cameraManager.PowerOff();
        }
    }
}
