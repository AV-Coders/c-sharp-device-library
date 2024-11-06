namespace AVCoders.Core;

public enum VolumeType : ushort
{
    Speaker = 0,
    Microphone = 1,
    LineIn = 2
}

public abstract class VolumeControl
{
    public readonly string Name;
    public readonly VolumeType Type;
    public VolumeLevelHandler? VolumeLevelHandlers;
    public MuteStateHandler? MuteStateHandlers;

    protected VolumeControl(string name, VolumeType type)
    {
        Name = name;
        Type = type;
    }

    public abstract void LevelUp(int amount);

    public abstract void LevelDown(int amount);

    public abstract void SetLevel(int percentage);

    public abstract void ToggleAudioMute();

    public abstract void SetAudioMute(MuteState state);
}