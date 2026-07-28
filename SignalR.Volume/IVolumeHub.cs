using AVCoders.Core;

namespace AVCoders.SignalR.Volume;

public interface IVolumeHub
{
    Task OnVolumeControlsChanged(IReadOnlyList<VolumeControl> volumeControls);
    Task OnVolumeLevelChanged(int index, VolumeControl control);
    Task OnVolumeMuteChanged(int index, VolumeControl control);
}
