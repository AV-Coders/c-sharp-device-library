using AVCoders.Core;

namespace AVCoders.SignalR.Volume;

public class VolumeManager : DeviceBase
{
    private readonly List<VolumeControl> _volumeControls;

    public IReadOnlyList<VolumeControl> VolumeControls => _volumeControls;

    public event Action<int, VolumeControl>? OnVolumeLevelChanged;
    public event Action<int, VolumeControl>? OnVolumeMuteChanged;

    public VolumeManager(string name, List<VolumeControl> volumeControls)
        : base(name, CommunicationClient.None)
    {
        _volumeControls = volumeControls;

        // Subscribe to all volume control events
        for (int i = 0; i < _volumeControls.Count; i++)
        {
            var index = i; // Capture the index for the closure
            var control = _volumeControls[i];
            control.VolumeLevelHandlers += _ => OnVolumeLevelChanged?.Invoke(index, control);
            control.MuteStateHandlers += _ => OnVolumeMuteChanged?.Invoke(index, control);
        }
    }

    public void SetVolumeLevel(int index, ushort level)
    {
        if (index >= 0 && index < _volumeControls.Count)
        {
            _volumeControls[index].SetLevel(level);
        }
    }

    public void SetVolumeMute(int index, MuteState state)
    {
        if (index >= 0 && index < _volumeControls.Count)
        {
            _volumeControls[index].SetAudioMute(state);
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
