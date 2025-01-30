using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public abstract class MediaPlayer : DeviceBase
{
    protected int Volume = 0;
    protected MuteState AudioMute = MuteState.Unknown;
    protected MuteState VideoMute = MuteState.Unknown;
}