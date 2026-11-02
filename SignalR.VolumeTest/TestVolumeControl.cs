using AVCoders.Core;

namespace AVCoders.SignalR.Volume.Tests;

public class TestVolumeControl : VolumeControl
{
    public int LevelUpCallCount;
    public int LevelDownCallCount;
    public int? LastSetLevel;
    public MuteState? LastSetMute;
    public int ToggleAudioMuteCallCount;

    public TestVolumeControl(string name = "TestVolume", VolumeType type = VolumeType.Speaker)
        : base(name, type)
    {
    }

    public override void LevelUp(int amount) => LevelUpCallCount++;
    public override void LevelDown(int amount) => LevelDownCallCount++;

    public override void SetLevel(int percentage)
    {
        LastSetLevel = percentage;
        Volume = percentage;
    }

    public override void ToggleAudioMute() => ToggleAudioMuteCallCount++;

    public override void SetAudioMute(MuteState state)
    {
        LastSetMute = state;
        MuteState = state;
    }
}
