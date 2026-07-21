using AVCoders.Core;

namespace AVCoders.SignalR.Volume.Tests;

public class VolumeManagerTest
{
    private readonly TestVolumeControl _speaker = new("Speaker", VolumeType.Speaker);
    private readonly TestVolumeControl _mic = new("Mic", VolumeType.Microphone);
    private readonly VolumeManager _manager;

    public VolumeManagerTest()
    {
        _manager = new VolumeManager("test-room", [_speaker, _mic]);
    }

    [Fact]
    public void VolumeControls_ExposesUnderlyingList()
    {
        Assert.Equal(2, _manager.VolumeControls.Count);
        Assert.Same(_speaker, _manager.VolumeControls[0]);
        Assert.Same(_mic, _manager.VolumeControls[1]);
    }

    [Fact]
    public void SetVolumeLevel_ForwardsToCorrectControl()
    {
        _manager.SetVolumeLevel(0, 60);

        Assert.Equal(60, _speaker.LastSetLevel);
        Assert.Null(_mic.LastSetLevel);
    }

    [Fact]
    public void SetVolumeLevel_NegativeIndex_DoesNothing()
    {
        _manager.SetVolumeLevel(-1, 60);

        Assert.Null(_speaker.LastSetLevel);
        Assert.Null(_mic.LastSetLevel);
    }

    [Fact]
    public void SetVolumeLevel_OutOfRangeIndex_DoesNothing()
    {
        _manager.SetVolumeLevel(99, 60);

        Assert.Null(_speaker.LastSetLevel);
        Assert.Null(_mic.LastSetLevel);
    }

    [Fact]
    public void SetVolumeMute_ForwardsToCorrectControl()
    {
        _manager.SetVolumeMute(1, MuteState.On);

        Assert.Equal(MuteState.On, _mic.LastSetMute);
        Assert.Null(_speaker.LastSetMute);
    }

    [Fact]
    public void SetVolumeMute_OutOfRangeIndex_DoesNothing()
    {
        _manager.SetVolumeMute(5, MuteState.On);

        Assert.Null(_speaker.LastSetMute);
        Assert.Null(_mic.LastSetMute);
    }

    [Fact]
    public void ControlVolumeLevelChange_FiresOnVolumeLevelChangedWithIndex()
    {
        var handler = new Mock<Action<int, VolumeControl>>();
        _manager.OnVolumeLevelChanged += handler.Object;

        _manager.SetVolumeLevel(1, 42);

        handler.Verify(h => h.Invoke(1, _mic), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public void ControlMuteStateChange_FiresOnVolumeMuteChangedWithIndex()
    {
        var handler = new Mock<Action<int, VolumeControl>>();
        _manager.OnVolumeMuteChanged += handler.Object;

        _manager.SetVolumeMute(0, MuteState.On);

        handler.Verify(h => h.Invoke(0, _speaker), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public void PowerOn_IsNoOp()
    {
        // Volume controls don't have power state — must not throw and must not poke controls.
        _manager.PowerOn();

        Assert.Null(_speaker.LastSetLevel);
        Assert.Null(_speaker.LastSetMute);
    }

    [Fact]
    public void PowerOff_IsNoOp()
    {
        _manager.PowerOff();

        Assert.Null(_speaker.LastSetLevel);
        Assert.Null(_speaker.LastSetMute);
    }
}
