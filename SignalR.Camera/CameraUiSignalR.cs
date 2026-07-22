using AVCoders.Camera;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

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
        _cameraManager.OnTrackingModeChanged += OnTrackingModeChanged;
    }

    private async void OnPowerStateChanged(PowerState state) =>
        await _hubContext.Clients.Group(Name).OnPowerStateChanged(state);

    private async void OnPresetRecalled(int index) =>
        await _hubContext.Clients.Group(Name).OnPresetRecalled(index);

    private async void OnPresetCleared() =>
        await _hubContext.Clients.Group(Name).OnPresetCleared();

    private async void OnTrackingModeChanged(CameraTrackingMode mode) =>
        await _hubContext.Clients.Group(Name).OnTrackingModeChanged(mode);

    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            LogInformation("Turning on camera");
            _cameraManager.PowerOn();
        }
    }

    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            LogInformation("Turning off camera");
            _cameraManager.PowerOff();
        }
    }
}
