using AVCoders.Core;

namespace AVCoders.SignalR.Volume.Tests;

/// <summary>
/// Concrete <see cref="VolumeControl"/> used by the SignalR.Volume tests so we can drive
/// level/mute changes from outside the class. <see cref="SetLevel"/> and
/// <see cref="SetAudioMute"/> intentionally write to the protected base setters so the
/// VolumeLevelHandlers / MuteStateHandlers chain fires (which is how
/// <see cref="VolumeManager"/> observes changes).
/// </summary>
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
