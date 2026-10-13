namespace AVCoders.Core.Tests;

public class VolumeControlTest
{
    private class TestVolumeControl(bool supportsLevel, bool supportsMute) : VolumeControl("Test", VolumeType.Speaker)
    {
        public readonly List<int> LevelsSet = [];
        public readonly List<MuteState> MuteStatesSet = [];

        public override bool SupportsLevel => supportsLevel;

        public override bool SupportsMute => supportsMute;

        public void Report(int volume, MuteState muteState)
        {
            Volume = volume;
            MuteState = muteState;
        }

        public override void LevelUp(int amount) { }

        public override void LevelDown(int amount) { }

        public override void SetLevel(int percentage) => LevelsSet.Add(percentage);

        public override void ToggleAudioMute() { }

        public override void SetAudioMute(MuteState state) => MuteStatesSet.Add(state);
    }

    [Fact]
    public void RestoreLevel_WithLevelAndMute_RestoresBoth()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: true);
        control.Report(40, MuteState.On);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Equal([40], control.LevelsSet);
        Assert.Equal([MuteState.On], control.MuteStatesSet);
    }

    [Fact]
    public void RestoreLevel_WithoutMute_DoesNotSetTheMute()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: false);
        control.Report(40, MuteState.On);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Equal([40], control.LevelsSet);
        Assert.Empty(control.MuteStatesSet);
    }

    [Fact]
    public void RestoreLevel_WithoutLevel_DoesNotSetTheLevel()
    {
        var control = new TestVolumeControl(supportsLevel: false, supportsMute: true);
        control.Report(40, MuteState.On);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Empty(control.LevelsSet);
        Assert.Equal([MuteState.On], control.MuteStatesSet);
    }
}
