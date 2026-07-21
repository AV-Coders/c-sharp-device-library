using AVCoders.Core;

namespace AVCoders.SignalR.Volume;

public class VolumeManager : DeviceBase
{
    public readonly List<VolumeControl> VolumeControls;

    public Action<int, VolumeControl>? OnVolumeLevelChanged;
    public Action<int, VolumeControl>? OnVolumeMuteChanged;

    public VolumeManager(string name, List<VolumeControl> volumeControls)
        : base(name, CommunicationClient.None)
    {
        VolumeControls = volumeControls;

        // Subscribe to all volume control events
        for (int i = 0; i < VolumeControls.Count; i++)
        {
            var index = i; // Capture the index for the closure
            var control = VolumeControls[i];
            control.VolumeLevelHandlers += _ => OnVolumeLevelChanged?.Invoke(index, control);
            control.MuteStateHandlers += _ => OnVolumeMuteChanged?.Invoke(index, control);
        }
    }

    public void SetVolumeLevel(int index, ushort level)
    {
        if (index >= 0 && index < VolumeControls.Count)
        {
            VolumeControls[index].SetLevel(level);
        }
    }

    public void SetVolumeMute(int index, MuteState state)
    {
        if (index >= 0 && index < VolumeControls.Count)
        {
            VolumeControls[index].SetAudioMute(state);
        }
    }

    public override void PowerOn()
    {
        // Volume controls don't have power state
    }

    public override void PowerOff()
    {
        // Volume controls don't have power state
    }
}
