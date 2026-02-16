namespace AVCoders.Core;

public enum VolumeType : ushort
{
    Speaker = 0,
    Microphone = 1,
    LineIn = 2
}

public abstract class VolumeControl : LogBase
{
    public readonly VolumeType Type;
    public VolumeLevelHandler? VolumeLevelHandlers;
    public MuteStateHandler? MuteStateHandlers;
    private int _volume;
    private int? _storedLevel = null;
    private MuteState _muteState;
    private MuteState? _storedMuteState = null;

    public int Volume
    {
        get => _volume;
        protected set
        {
            if( _volume == value)
                return;
            _volume = value;
            AddEvent(EventType.Volume,  value.ToString());
            VolumeLevelHandlers?.Invoke(_volume);
        }
    }

    public MuteState MuteState
    {
        get => _muteState;
        protected set
        {
            if( _muteState == value)
                return;
            _muteState = value;
            AddEvent(EventType.Volume, $"Mute: {value.ToString()}");
            MuteStateHandlers?.Invoke(_muteState);
        }
    }

    protected VolumeControl(string name, VolumeType type) : base(name)
    {
        Type = type;
        
        VolumeLevelHandlers += x => _volume = x;
        MuteStateHandlers += x => _muteState = x;
    }

    public abstract void LevelUp(int amount);

    public abstract void LevelDown(int amount);

    public abstract void SetLevel(int percentage);

    public abstract void ToggleAudioMute();

    public abstract void SetAudioMute(MuteState state);
    
    public void SaveLevel()
    {
        _storedLevel = _volume;
        _storedMuteState = _muteState;
    }

    public void RestoreLevel()
    {
        if(_storedLevel.HasValue)
            SetLevel(_storedLevel.Value);
        if(_storedMuteState.HasValue)
            SetAudioMute(_storedMuteState.Value);
    }
}