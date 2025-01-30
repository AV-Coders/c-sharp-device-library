using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public abstract class MediaPlayer : DeviceBase
{
    public MediaStateHandler? MediaStateHandlers;
    public TransportStateHandler? TransportStateHandlers;
    protected int Volume = 0;
    protected MuteState AudioMute = MuteState.Unknown;
    protected MuteState VideoMute = MuteState.Unknown;
    protected TransportState DesiredTransportState;
    private MediaState _mediaState = MediaState.Unknown;
    private TransportState _transportState = TransportState.Unknown;

    public MediaState MediaState
    {
        get => _mediaState;
        protected set
        {
            if (value == _mediaState)
                return;
            _mediaState = value;
            MediaStateHandlers?.Invoke(value);
        }
    }

    public TransportState TransportState
    {
        get => _transportState;
        protected set
        {
            if (value == _transportState)
                return;
            _transportState = value;
            TransportStateHandlers?.Invoke(value);
        }
    }
}