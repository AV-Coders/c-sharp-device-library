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

        public void ReportVolume(int volume) => Volume = volume;

        public void ReportThroughTheHandlers(int volume, MuteState muteState)
        {
            VolumeLevelHandlers?.Invoke(volume);
            MuteStateHandlers?.Invoke(muteState);
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

    [Fact]
    public void RestoreLevel_SavedBeforeAnyReport_RestoresNothing()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: true);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Empty(control.LevelsSet);
        Assert.Empty(control.MuteStatesSet);
    }

    [Fact]
    public void RestoreLevel_SavedBeforeAMuteReport_DoesNotSetTheMute()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: true);
        control.ReportVolume(40);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Equal([40], control.LevelsSet);
        Assert.Empty(control.MuteStatesSet);
    }

    [Fact]
    public void RestoreLevel_AfterAReportOfZero_RestoresZero()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: true);
        control.Report(0, MuteState.Off);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Equal([0], control.LevelsSet);
        Assert.Equal([MuteState.Off], control.MuteStatesSet);
    }

    [Fact]
    public void RestoreLevel_AfterAReportThroughTheHandlers_RestoresIt()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: true);
        control.ReportThroughTheHandlers(35, MuteState.On);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Equal([35], control.LevelsSet);
        Assert.Equal([MuteState.On], control.MuteStatesSet);
    }

    [Fact]
    public void RestoreLevel_SavedAfterTheMuteWentBackToUnknown_DoesNotSetTheMute()
    {
        var control = new TestVolumeControl(supportsLevel: true, supportsMute: true);
        control.Report(40, MuteState.On);
        control.Report(40, MuteState.Unknown);
        control.SaveLevel();

        control.RestoreLevel();

        Assert.Equal([40], control.LevelsSet);
        Assert.Empty(control.MuteStatesSet);
    }
}
