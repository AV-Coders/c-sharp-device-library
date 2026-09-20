using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Dsp.Tests;

public class CecAudioSystemTest
{
    private readonly CecAudioSystem _audioSystem;
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();

    private static readonly char[] GiveAudioStatus = ['\x45', '\x71'];
    private static readonly char[] GivePhysicalAddress = ['\x45', '\x83'];
    private static readonly char[] VolumeUpPressed = ['\x45', '\x44', '\x41'];
    private static readonly char[] VolumeDownPressed = ['\x45', '\x44', '\x42'];
    private static readonly char[] MutePressed = ['\x45', '\x44', '\x43'];
    private static readonly char[] KeyReleased = ['\x45', '\x45'];
    private const string ReportPhysicalAddress = "\x5F\x84\x20\x00\x05";

    public CecAudioSystemTest()
    {
        _audioSystem = CreateUndiscovered(_mockClient);
        Receive(ReportPhysicalAddress);
    }

    private static CecAudioSystem CreateUndiscovered(Mock<SerialClient> client) =>
        new(client.Object, "Test Sonos", pollTime: 0);

    private void Receive(string frame) => _mockClient.Object.ResponseHandlers!.Invoke(frame);

    private sealed class SimulatedBeam
    {
        public int Level;
        public bool Muted;
        public bool ReportUnknownOnce;
        public bool IgnoreKeys;
        public int? AnswerLimit;
        private int _answered;

        public SimulatedBeam(Mock<SerialClient> client, int level, bool muted = false)
        {
            Level = level;
            Muted = muted;
            client.Setup(x => x.Send(It.IsAny<char[]>())).Callback<char[]>(frame =>
            {
                if (frame.SequenceEqual(GiveAudioStatus))
                {
                    if (AnswerLimit is { } limit && _answered >= limit)
                        return;
                    _answered++;
                    var status = ReportUnknownOnce ? 0x7F : Level | (Muted ? 0x80 : 0);
                    ReportUnknownOnce = false;
                    client.Object.ResponseHandlers!.Invoke("\x54\x7A" + (char)status);
                }
                else if (IgnoreKeys)
                    return;
                else if (frame.SequenceEqual(VolumeUpPressed))
                    Level = Math.Min(100, Level + 2);
                else if (frame.SequenceEqual(VolumeDownPressed))
                    Level = Math.Max(0, Level - 2);
                else if (frame.SequenceEqual(MutePressed))
                    Muted = !Muted;
            });
        }
    }

    [Theory]
    [InlineData("\x54\x7A\x1A", 26, MuteState.Off)]
    [InlineData("\x54\x7A\x1C", 28, MuteState.Off)]
    [InlineData("\x54\x7A\x1E", 30, MuteState.Off)]
    [InlineData("\x54\x7A\x9C", 28, MuteState.On)]
    [InlineData("\x54\x7A\x00", 0, MuteState.Off)]
    [InlineData("\x54\x7A\x64", 100, MuteState.Off)]
    public void HandleResponse_ParsesReportAudioStatus(string frame, int expectedVolume, MuteState expectedMute)
    {
        Receive(frame);

        Assert.Equal(expectedVolume, _audioSystem.Volume);
        Assert.Equal(expectedMute, _audioSystem.MuteState);
        Assert.True(_audioSystem.IsLevelKnown);
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);
    }

    [Fact]
    public void HandleResponse_UnknownVolumeKeepsTheLastLevelButFlagsIt()
    {
        Receive("\x54\x7A\x1C");
        Receive("\x54\x7A\x7F");

        Assert.Equal(28, _audioSystem.Volume);
        Assert.False(_audioSystem.IsLevelKnown);
        Assert.Equal(MuteState.Off, _audioSystem.MuteState);
    }

    [Fact]
    public void HandleResponse_ToleratesTheDoubledHeaderQuirk()
    {
        Receive("\x54\x54\x7A\x1C");

        Assert.Equal(28, _audioSystem.Volume);
    }

    [Fact]
    public void HandleResponse_IgnoresFramesFromOtherDevices()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = CreateUndiscovered(mock);

        mock.Object.ResponseHandlers!.Invoke("\x40\x7A\x1C");
        mock.Object.ResponseHandlers!.Invoke("\x50\x7A\x1C");

        Assert.False(audioSystem.IsLevelKnown);
        Assert.Equal(CommunicationState.NotAttempted, audioSystem.CommunicationState);
    }

    [Fact]
    public void HandleResponse_IgnoresAMergedPhysicalAddressFrame()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = CreateUndiscovered(mock);

        mock.Object.ResponseHandlers!.Invoke("\x5F\x84\x20\x54\x7E\x01");
        audioSystem.PollAudioStatus();

        Assert.DoesNotContain(audioSystem.Details, d => d.Label == CecAudioSystem.PhysicalAddressDetailLabel);
        mock.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
    }

    [Fact]
    public void HandleResponse_ReportsPowerOn()
    {
        Receive("\x54\x90\x00");

        Assert.Equal(PowerState.On, _audioSystem.PowerState);
    }

    [Fact]
    public void HandleResponse_RecordsDiscoveryDetails()
    {
        Receive("\x54\x47" + "Sonos Beam");
        Receive("\x5F\x87\xEA\xBE\xA7");
        Receive("\x54\x9E\x05");
        Receive("\x54\x7E\x01");

        Assert.Contains(_audioSystem.Details, d => d is { Label: CecAudioSystem.OsdNameDetailLabel, Value: "Sonos Beam" });
        Assert.Contains(_audioSystem.Details, d => d is { Label: CecAudioSystem.VendorDetailLabel, Value: "Sonos (EA:BE:A7)" });
        Assert.Contains(_audioSystem.Details, d => d is { Label: CecAudioSystem.CecVersionDetailLabel, Value: "1.4" });
        Assert.Contains(_audioSystem.Details, d => d is { Label: CecAudioSystem.PhysicalAddressDetailLabel, Value: "2.0.0.0" });
        Assert.Contains(_audioSystem.Details, d => d is { Label: CecAudioSystem.SystemAudioModeDetailLabel, Value: "On" });
    }

    [Fact]
    public void Discover_SendsTheDiscoveryRequests()
    {
        _audioSystem.Discover();

        _mockClient.Verify(x => x.Send(GivePhysicalAddress));
        _mockClient.Verify(x => x.Send(new[] { '\x45', '\x46' }));
        _mockClient.Verify(x => x.Send(new[] { '\x45', '\x8C' }));
        _mockClient.Verify(x => x.Send(new[] { '\x45', '\x9F' }));
        _mockClient.Verify(x => x.Send(new[] { '\x45', '\x7D' }));
        _mockClient.Verify(x => x.Send(new[] { '\x45', '\x8F' }));
    }

    [Fact]
    public void PollAudioStatus_DiscoversFirstThenAsksForAudioStatus()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = CreateUndiscovered(mock);

        audioSystem.PollAudioStatus();

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
        mock.Verify(x => x.Send(GiveAudioStatus), Times.Once);

        mock.Object.ResponseHandlers!.Invoke(ReportPhysicalAddress);
        audioSystem.PollAudioStatus();

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
        mock.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
    }

    [Fact]
    public void PollAudioStatus_UnansweredPollsBecomeACommunicationError()
    {
        Receive("\x54\x7A\x1C");
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);

        for (var i = 0; i < CecAudioSystem.MaxUnansweredPolls; i++)
            _audioSystem.PollAudioStatus();
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);

        _audioSystem.PollAudioStatus();
        Assert.Equal(CommunicationState.Error, _audioSystem.CommunicationState);
        Assert.Equal(PowerState.Unknown, _audioSystem.PowerState);
        Assert.Equal(MuteState.Unknown, _audioSystem.MuteState);
        Assert.False(_audioSystem.IsLevelKnown);
        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Never);

        Receive("\x54\x7A\x1C");
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);
        Assert.Equal(MuteState.Off, _audioSystem.MuteState);

        _audioSystem.PollAudioStatus();
        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
    }

    [Fact]
    public void ConnectionState_ConnectedRunsDiscoveryAgain()
    {
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        _audioSystem.PollAudioStatus();

        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
    }

    [Fact]
    public async Task PollWorker_PollsAfterStartup()
    {
        var mock = TestFactory.CreateSerialClient();
        _ = new CecAudioSystem(mock.Object, "Polling", pollTime: 1);

        await Task.Delay(2500);

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    public void LevelUp_SendsOnePressPerVolumeStepOfTheAmount(int amount, int expectedPresses)
    {
        _audioSystem.LevelUp(amount);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(expectedPresses));
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Exactly(expectedPresses));
    }

    [Fact]
    public void LevelDown_SendsOnePressPerVolumeStepOfTheAmount()
    {
        _audioSystem.LevelDown(4);

        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
    }

    [Fact]
    public void LevelUp_RejectsAnAmountBelowOne()
    {
        _audioSystem.LevelUp(0);

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
    }

    [Fact]
    public async Task SetLevel_From26To30_SendsTwoPressesUp()
    {
        var beam = new SimulatedBeam(_mockClient, 26);

        await _audioSystem.SetLevelAsync(30);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Never);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
        Assert.Equal(30, beam.Level);
        Assert.Equal(30, _audioSystem.Volume);
    }

    [Fact]
    public async Task SetLevel_From30To25_SendsThreePressesDownThenReadsAgain()
    {
        var beam = new SimulatedBeam(_mockClient, 30);

        await _audioSystem.SetLevelAsync(25);

        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Exactly(3));
        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
        Assert.Equal(24, beam.Level);
        Assert.Equal(24, _audioSystem.Volume);
    }

    [Fact]
    public async Task SetLevel_CorrectsWithOneMorePassWhenTheFirstFallsShort()
    {
        var beam = new SimulatedBeam(_mockClient, 20);
        var presses = 0;
        _mockClient.Setup(x => x.Send(VolumeUpPressed)).Callback(() =>
        {
            if (++presses != 3)
                beam.Level += 2;
        });

        await _audioSystem.SetLevelAsync(30);

        Assert.Equal(30, beam.Level);
        Assert.Equal(30, _audioSystem.Volume);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(3));
    }

    [Fact]
    public async Task SetLevel_StopsAfterTwoPassesWhenTheLevelNeverMoves()
    {
        _ = new SimulatedBeam(_mockClient, 28) { IgnoreKeys = true };

        await _audioSystem.SetLevelAsync(36);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(8));
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(3));
        Assert.Equal(28, _audioSystem.Volume);
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task SetLevel_GivesUpWhenTheReadAfterPressesGetsNoReply()
    {
        var beam = new SimulatedBeam(_mockClient, 26) { AnswerLimit = 1 };

        await _audioSystem.SetLevelAsync(30);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
        Assert.Equal(30, beam.Level);
        Assert.Equal(26, _audioSystem.Volume);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task SetLevel_ClampsTheTarget()
    {
        var beam = new SimulatedBeam(_mockClient, 98);

        await _audioSystem.SetLevelAsync(150);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Once);
        Assert.Equal(100, beam.Level);
        Assert.Equal(100, _audioSystem.Volume);
    }

    [Fact]
    public async Task SetLevel_DoesNothingWhenAlreadyThere()
    {
        _ = new SimulatedBeam(_mockClient, 28);

        await _audioSystem.SetLevelAsync(28);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Never);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
    }

    [Fact]
    public async Task SetLevel_ReadsAgainWhenTheLevelIsReportedUnknown()
    {
        var beam = new SimulatedBeam(_mockClient, 28) { ReportUnknownOnce = true };

        await _audioSystem.SetLevelAsync(30);

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Once);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(3));
        Assert.Equal(30, beam.Level);
    }

    [Fact]
    public async Task SetLevel_GivesUpWithoutPressingWhenNothingAnswers()
    {
        await _audioSystem.SetLevelAsync(30);

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Never);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task SetLevel_ANewTargetWhileWalkingWins()
    {
        var beam = new SimulatedBeam(_mockClient, 20);

        var first = _audioSystem.SetLevelAsync(40);
        var second = _audioSystem.SetLevelAsync(24);
        await first;
        await second;

        Assert.Same(first, second);
        Assert.Equal(24, beam.Level);
        Assert.Equal(24, _audioSystem.Volume);
    }

    [Fact]
    public async Task RestoreLevel_PutsBackTheSavedLevelAndMute()
    {
        Receive("\x54\x7A\x9C");
        _audioSystem.SaveLevel();
        var beam = new SimulatedBeam(_mockClient, 30);
        Receive("\x54\x7A\x1E");

        _audioSystem.RestoreLevel();
        await _audioSystem.SetLevelAsync(28);

        Assert.Equal(28, beam.Level);
        Assert.True(beam.Muted);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
    }

    [Fact]
    public void SetAudioMute_SendsTheMuteKeyOnlyWhenTheStateDiffers()
    {
        Receive("\x54\x7A\x1C");

        _audioSystem.SetAudioMute(MuteState.Off);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Never);

        _audioSystem.SetAudioMute(MuteState.On);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Once);

        Receive("\x54\x7A\x9C");
        _audioSystem.SetAudioMute(MuteState.On);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);

        _audioSystem.SetAudioMute(MuteState.Off);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Exactly(2));
    }

    [Fact]
    public void SetAudioMute_TwiceInARowSendsOneKey()
    {
        Receive("\x54\x7A\x1C");

        _audioSystem.SetAudioMute(MuteState.On);
        _audioSystem.SetAudioMute(MuteState.On);

        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);
    }

    [Fact]
    public async Task SetAudioMute_ReadsTheStatusFirstWhenItHasNeverBeenReported()
    {
        _ = new SimulatedBeam(_mockClient, 28, muted: true);

        await _audioSystem.SetAudioMuteAsync(MuteState.On);

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Never);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);

        await _audioSystem.SetAudioMuteAsync(MuteState.Off);

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        Assert.Equal(MuteState.Off, _audioSystem.MuteState);
    }

    [Fact]
    public async Task SetAudioMute_DoesNotSendBlindWhenNothingAnswers()
    {
        await _audioSystem.SetAudioMuteAsync(MuteState.On);

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Never);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public void ToggleAudioMute_SendsTheMuteKeyOnce()
    {
        Receive("\x54\x7A\x1C");

        _audioSystem.ToggleAudioMute();

        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Once);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);
    }

    [Fact]
    public async Task Keys_ReadTheStatusBackAfterADelay()
    {
        _audioSystem.LevelUp(1);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Never);

        await Task.Delay(CecAudioSystem.StatusReadDelayAfterKey + TimeSpan.FromMilliseconds(400));

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Never);
    }

    [Fact]
    public void Power_IsANoOpThatSendsNothing()
    {
        _audioSystem.PowerOn();
        _audioSystem.PowerOff();

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Equal(PowerState.Unknown, _audioSystem.DesiredPowerState);
    }

    [Fact]
    public void LogicalAddresses_ChangeTheHeaders()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = new CecAudioSystem(mock.Object, "From the TV", 5, 0, pollTime: 0);

        audioSystem.ToggleAudioMute();
        mock.Verify(x => x.Send(new[] { '\x05', '\x44', '\x43' }));

        mock.Object.ResponseHandlers!.Invoke("\x50\x7A\x1C");
        Assert.Equal(28, audioSystem.Volume);
    }

    [Fact]
    public void Constructor_RejectsAnInvalidLogicalAddress()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CecAudioSystem(_mockClient.Object, "Bad", 0x0F, pollTime: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CecAudioSystem(_mockClient.Object, "Bad", 5, 0x0F, pollTime: 0));
    }

    [Fact]
    public void VolumeStep_RejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _audioSystem.VolumeStep = 0);
    }

    [Fact]
    public void ItIsAVolumeControlAndADevice()
    {
        Assert.IsAssignableFrom<VolumeControl>(_audioSystem);
        Assert.IsAssignableFrom<IDevice>(_audioSystem);
        Assert.Equal(VolumeType.Speaker, _audioSystem.Type);
    }
}
