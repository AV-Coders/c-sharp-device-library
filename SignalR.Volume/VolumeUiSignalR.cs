using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AVCoders.SignalR.Volume;

public class VolumeUiSignalR : DeviceBase
{
    private readonly VolumeManager _volumeManager;
    private readonly IHubContext<VolumeHub, IVolumeHub> _hubContext;

    public VolumeUiSignalR(VolumeManager volumeManager, IHubContext<VolumeHub, IVolumeHub> hubContext)
        : base(volumeManager.Name, CommunicationClient.None)
    {
        _volumeManager = volumeManager;
        _hubContext = hubContext;
        VolumeHub.RegisterVolumeManager(Name, volumeManager);

        // Subscribe to volume change events
        _volumeManager.OnVolumeLevelChanged += OnVolumeLevelChanged;
        _volumeManager.OnVolumeMuteChanged += OnVolumeMuteChanged;
    }

    private async void OnVolumeLevelChanged(int index, VolumeControl control)
    {
        await _hubContext.Clients.Group(Name).OnVolumeLevelChanged(index, control);
    }

    private async void OnVolumeMuteChanged(int index, VolumeControl control)
    {
        await _hubContext.Clients.Group(Name).OnVolumeMuteChanged(index, control);
    }

    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            Log.Information("Turning on volume controls");
            _volumeManager.PowerOn();
        }
    }

    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            Log.Information("Turning off volume controls");
            _volumeManager.PowerOff();
        }
    }
}
