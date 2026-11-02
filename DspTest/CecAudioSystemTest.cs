using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Dsp.Tests;

public class CecAudioSystemTest
{
    private readonly FakeTimeProvider _time = new();
    private readonly CecAudioSystem _audioSystem;
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();

    private static readonly char[] GiveAudioStatus = ['\x45', '\x71'];
    private static readonly char[] GivePhysicalAddress = ['\x45', '\x83'];
    private static readonly char[] VolumeUpPressed = ['\x45', '\x44', '\x41'];
    private static readonly char[] VolumeDownPressed = ['\x45', '\x44', '\x42'];
    private static readonly char[] MutePressed = ['\x45', '\x44', '\x43'];
    private static readonly char[] KeyReleased = ['\x45', '\x45'];
    private const string ReportPhysicalAddress = "\x5F\x84\x20\x00\x05";
    private const int NoPolling = 0;
    private const int PollSeconds = 1;
    private static readonly TimeSpan ToggleJoinTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan KeyHold = TimeSpan.FromMilliseconds(100);
    private static readonly char[][] DiscoveryRequests =
    [
        GivePhysicalAddress, ['\x45', '\x46'], ['\x45', '\x8C'], ['\x45', '\x9F'], ['\x45', '\x7D'], ['\x45', '\x8F']
    ];

    public CecAudioSystemTest()
    {
        _audioSystem = Create(_mockClient, "Test Sonos", NoPolling);
        Receive(ReportPhysicalAddress);
    }

    private CecAudioSystem Create(Mock<SerialClient> client, string name, int pollTime) =>
        new(client.Object, name, CecAudioSystem.LogicalAddressAudioSystem, CecAudioSystem.LogicalAddressPlayback1, pollTime, _time);

    private void Receive(string frame) => _mockClient.Object.ResponseHandlers!.Invoke(frame);

    private async Task Finish(Task task)
    {
        await Wait.UntilAsync(_time, Step, () => task.IsCompleted, "the sends never finished");
        await task;
    }

    private Task FinishTheQueuedSends(CecAudioSystem audioSystem) => Finish(QueuedSends(audioSystem));

    private static int Sent(Mock<SerialClient> client, char[] frame) =>
        client.Invocations.Count(i => i.Arguments.Count == 1 && i.Arguments[0] is char[] sent && sent.SequenceEqual(frame));

    private static List<string> SentFrames(Mock<SerialClient> client) =>
        client.Invocations
            .Where(i => i.Arguments.Count == 1 && i.Arguments[0] is char[])
            .Select(i => new string((char[])i.Arguments[0]))
            .ToList();

    private static Task QueuedSends(CecAudioSystem audioSystem) =>
        (Task)typeof(CecAudioSystem).GetField("_queuedSends", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(audioSystem)!;

    private static ThreadWorker PollWorker(CecAudioSystem audioSystem) =>
        (ThreadWorker)typeof(CecAudioSystem).GetField("_pollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(audioSystem)!;

    private static ITimer? StatusReadTimer(CecAudioSystem audioSystem) =>
        (ITimer?)typeof(CecAudioSystem).GetField("_statusReadTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(audioSystem);

    private static void SetConnectionState(Mock<SerialClient> client, ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client.Object, state);

    private static void PollNow(CecAudioSystem audioSystem) =>
        ((Task)typeof(CecAudioSystem).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(audioSystem, [CancellationToken.None])!).GetAwaiter().GetResult();

    private sealed class FakeBeam
    {
        public int Level;
        public bool Muted;
        public bool ReportUnknownOnce;
        public bool IgnoreKeys;
        public int? AnswerLimit;
        private int _answered;

        public FakeBeam(Mock<SerialClient> client, int level, bool muted = false)
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
        var audioSystem = Create(mock, "Test Sonos", NoPolling);

        mock.Object.ResponseHandlers!.Invoke("\x40\x7A\x1C");
        mock.Object.ResponseHandlers!.Invoke("\x50\x7A\x1C");

        Assert.False(audioSystem.IsLevelKnown);
        Assert.Equal(CommunicationState.NotAttempted, audioSystem.CommunicationState);
    }

    [Fact]
    public async Task HandleResponse_IgnoresAMergedPhysicalAddressFrame()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = Create(mock, "Test Sonos", NoPolling);

        mock.Object.ResponseHandlers!.Invoke("\x5F\x84\x20\x54\x7E\x01");
        audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(audioSystem);

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
    public async Task Discover_ReturnsBeforeTheRequestsAreSentThenSendsThemInOrder()
    {
        _audioSystem.Discover();

        Assert.True(SentFrames(_mockClient).Count < DiscoveryRequests.Length);
        await FinishTheQueuedSends(_audioSystem);
        Assert.Equal(DiscoveryRequests.Select(frame => new string(frame)), SentFrames(_mockClient));
    }

    [Fact]
    public async Task PollAudioStatus_ReturnsAtOnce()
    {
        _audioSystem.PollAudioStatus();
        _audioSystem.PollAudioStatus();

        Assert.True(Sent(_mockClient, GiveAudioStatus) < 2);
        await FinishTheQueuedSends(_audioSystem);
        Assert.Equal(2, Sent(_mockClient, GiveAudioStatus));
    }

    [Fact]
    public async Task PollAudioStatus_DiscoversFirstThenAsksForAudioStatus()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = Create(mock, "Test Sonos", NoPolling);

        audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(audioSystem);

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
        mock.Verify(x => x.Send(GiveAudioStatus), Times.Once);

        mock.Object.ResponseHandlers!.Invoke(ReportPhysicalAddress);
        audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(audioSystem);

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
        mock.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
    }

    [Fact]
    public async Task PollAudioStatus_UnansweredPollsBecomeACommunicationError()
    {
        Receive("\x54\x7A\x1C");
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);

        for (var i = 0; i < CecAudioSystem.MaxUnansweredPolls; i++)
            _audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(_audioSystem);
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);

        _audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(_audioSystem);
        Assert.Equal(CommunicationState.Error, _audioSystem.CommunicationState);
        Assert.Equal(PowerState.Unknown, _audioSystem.PowerState);
        Assert.Equal(MuteState.Unknown, _audioSystem.MuteState);
        Assert.False(_audioSystem.IsLevelKnown);
        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Never);

        Receive("\x54\x7A\x1C");
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);
        Assert.Equal(MuteState.Off, _audioSystem.MuteState);

        _audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(_audioSystem);
        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
    }

    [Fact]
    public async Task ConnectionState_ConnectedRunsDiscoveryAgain()
    {
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        _audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(_audioSystem);

        _mockClient.Verify(x => x.Send(GivePhysicalAddress), Times.Once);
    }

    [Fact]
    public async Task PollWorker_PollsAfterStartup()
    {
        var mock = TestFactory.CreateSerialClient();
        SetConnectionState(mock, ConnectionState.Connected);
        _ = Create(mock, "Polling", PollSeconds);

        await Wait.UntilAsync(_time, Step, () => Sent(mock, GivePhysicalAddress) >= 1, "the poll worker never polled");
        await Wait.UntilAsync(_time, Step, () => Sent(mock, GiveAudioStatus) >= 1, "the poll never finished");

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollWorker_WhileTheStreamStateIsUnknown_Polls()
    {
        var mock = TestFactory.CreateSerialClient();
        _ = Create(mock, "Unknown stream", PollSeconds);

        await Wait.UntilAsync(_time, Step, () => Sent(mock, GivePhysicalAddress) >= 1, "the poll worker never polled");
        await Wait.UntilAsync(_time, Step, () => Sent(mock, GiveAudioStatus) >= 1, "the poll never finished");

        mock.Verify(x => x.Send(GivePhysicalAddress), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Error)]
    public void Poll_GivenADownStream_SendsNothing(ConnectionState state)
    {
        SetConnectionState(_mockClient, state);

        PollNow(_audioSystem);

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
    }

    [Fact]
    public async Task ClientDisconnectedOnRequest_ReportsNotAttemptedAndResolvesTheCommunicationIssue()
    {
        Receive("\x54\x7A\x1C");
        for (var i = 0; i <= CecAudioSystem.MaxUnansweredPolls; i++)
            _audioSystem.PollAudioStatus();
        await FinishTheQueuedSends(_audioSystem);
        Assert.Equal(CommunicationState.Error, _audioSystem.CommunicationState);

        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.DesiredConnectionState))!
            .SetValue(_mockClient.Object, ConnectionState.Disconnected);
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.NotAttempted, _audioSystem.CommunicationState);
        Assert.DoesNotContain(_audioSystem.GetOngoingIssues(), i => i.Key == "communication");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    public async Task LevelUp_SendsOnePressPerVolumeStepOfTheAmount(int amount, int expectedPresses)
    {
        _audioSystem.LevelUp(amount);
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, KeyReleased) == expectedPresses, "the key presses");

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(expectedPresses));
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Exactly(expectedPresses));
    }

    [Fact]
    public async Task LevelDown_SendsOnePressPerVolumeStepOfTheAmount()
    {
        _audioSystem.LevelDown(4);
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, KeyReleased) == 2, "the key presses");

        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
    }

    [Fact]
    public async Task LevelUp_ReturnsBeforeThePressesAreSent()
    {
        _audioSystem.LevelUp(10);

        Assert.True(Sent(_mockClient, KeyReleased) < 5);
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, KeyReleased) == 5, "the key presses");
    }

    [Fact]
    public async Task Keys_AreSentInTheOrderTheyWereAskedFor()
    {
        Receive("\x54\x7A\x1C");

        _audioSystem.LevelUp(2);
        _audioSystem.LevelDown(2);
        _audioSystem.ToggleAudioMute();
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, KeyReleased) == 3, "the key presses");

        var pressedKeys = _mockClient.Invocations
            .Select(i => i.Arguments[0])
            .OfType<char[]>()
            .Where(frame => frame.Length == VolumeUpPressed.Length && frame[1] == VolumeUpPressed[1])
            .Select(frame => new string(frame));
        Assert.Equal([new string(VolumeUpPressed), new string(VolumeDownPressed), new string(MutePressed)], pressedKeys);
    }

    [Fact]
    public async Task Keys_AreSentAndReadBackUnderTheKeyPressTriggerId()
    {
        var sentUnder = new ConcurrentQueue<(char[] Frame, string? TriggerId)>();
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Callback<char[]>(frame => sentUnder.Enqueue((frame, Trigger.Id)));
        Trigger.Start();
        var keyPressTriggerId = Trigger.Id;

        _audioSystem.LevelUp(1);
        await Wait.UntilAsync(_time, Step, () => sentUnder.Any(s => s.Frame.SequenceEqual(GiveAudioStatus)), "the status read after the key");

        Assert.All(sentUnder, s => Assert.Equal(keyPressTriggerId, s.TriggerId));
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
        var beam = new FakeBeam(_mockClient, 26);

        await Finish(_audioSystem.SetLevelAsync(30));

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Never);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
        Assert.Equal(30, beam.Level);
        Assert.Equal(30, _audioSystem.Volume);
    }

    [Fact]
    public async Task SetLevel_From30To25_SendsThreePressesDownThenReadsAgain()
    {
        var beam = new FakeBeam(_mockClient, 30);

        await Finish(_audioSystem.SetLevelAsync(25));

        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Exactly(3));
        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
        Assert.Equal(24, beam.Level);
        Assert.Equal(24, _audioSystem.Volume);
    }

    [Fact]
    public async Task SetLevel_CorrectsWithOneMorePassWhenTheFirstFallsShort()
    {
        var beam = new FakeBeam(_mockClient, 20);
        var presses = 0;
        _mockClient.Setup(x => x.Send(VolumeUpPressed)).Callback(() =>
        {
            if (++presses != 3)
                beam.Level += 2;
        });

        await Finish(_audioSystem.SetLevelAsync(30));

        Assert.Equal(30, beam.Level);
        Assert.Equal(30, _audioSystem.Volume);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(3));
    }

    [Fact]
    public async Task SetLevel_StopsAfterTwoPassesWhenTheLevelNeverMoves()
    {
        _ = new FakeBeam(_mockClient, 28) { IgnoreKeys = true };

        await Finish(_audioSystem.SetLevelAsync(36));

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(8));
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(3));
        Assert.Equal(28, _audioSystem.Volume);
        Assert.Equal(CommunicationState.Okay, _audioSystem.CommunicationState);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task SetLevel_GivesUpWhenTheReadAfterPressesGetsNoReply()
    {
        var beam = new FakeBeam(_mockClient, 26) { AnswerLimit = 1 };

        await Finish(_audioSystem.SetLevelAsync(30));

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Exactly(2));
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(2));
        Assert.Equal(30, beam.Level);
        Assert.Equal(26, _audioSystem.Volume);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task SetLevel_ClampsTheTarget()
    {
        var beam = new FakeBeam(_mockClient, 98);

        await Finish(_audioSystem.SetLevelAsync(150));

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Once);
        Assert.Equal(100, beam.Level);
        Assert.Equal(100, _audioSystem.Volume);
    }

    [Fact]
    public async Task SetLevel_DoesNothingWhenAlreadyThere()
    {
        _ = new FakeBeam(_mockClient, 28);

        await Finish(_audioSystem.SetLevelAsync(28));

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Never);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
    }

    [Fact]
    public async Task SetLevel_ReadsAgainWhenTheLevelIsReportedUnknown()
    {
        var beam = new FakeBeam(_mockClient, 28) { ReportUnknownOnce = true };

        await Finish(_audioSystem.SetLevelAsync(30));

        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Once);
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Exactly(3));
        Assert.Equal(30, beam.Level);
    }

    [Fact]
    public async Task SetLevel_GivesUpWithoutPressingWhenNothingAnswers()
    {
        await Finish(_audioSystem.SetLevelAsync(30));

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(VolumeUpPressed), Times.Never);
        _mockClient.Verify(x => x.Send(VolumeDownPressed), Times.Never);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task SetLevel_ANewTargetWhileWalkingWins()
    {
        var beam = new FakeBeam(_mockClient, 20);

        var first = _audioSystem.SetLevelAsync(40);
        var second = _audioSystem.SetLevelAsync(24);
        await Finish(first);
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
        var beam = new FakeBeam(_mockClient, 30);
        Receive("\x54\x7A\x1E");

        _audioSystem.RestoreLevel();
        await Finish(_audioSystem.SetLevelAsync(28));
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, MutePressed) == 1, "the mute key");

        Assert.Equal(28, beam.Level);
        Assert.True(beam.Muted);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
    }

    [Fact]
    public async Task SetAudioMute_SendsTheMuteKeyOnlyWhenTheStateDiffers()
    {
        Receive("\x54\x7A\x1C");

        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.Off));
        _mockClient.Verify(x => x.Send(MutePressed), Times.Never);

        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.On));
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Once);

        Receive("\x54\x7A\x9C");
        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.On));
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);

        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.Off));
        _mockClient.Verify(x => x.Send(MutePressed), Times.Exactly(2));
    }

    [Fact]
    public async Task SetAudioMute_TwiceInARowSendsOneKey()
    {
        Receive("\x54\x7A\x1C");

        var first = _audioSystem.SetAudioMuteAsync(MuteState.On);
        var second = _audioSystem.SetAudioMuteAsync(MuteState.On);
        await Finish(Task.WhenAll(first, second));

        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);
    }

    [Fact]
    public async Task SetAudioMute_ReadsTheStatusFirstWhenItHasNeverBeenReported()
    {
        _ = new FakeBeam(_mockClient, 28, muted: true);

        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.On));

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Never);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);

        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.Off));

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        Assert.Equal(MuteState.Off, _audioSystem.MuteState);
    }

    [Fact]
    public async Task SetAudioMuteAsync_AfterReadingTheStatus_CompletesOnceTheKeyIsSent()
    {
        _ = new FakeBeam(_mockClient, 28);

        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.On));

        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Once);
    }

    [Fact]
    public async Task SetAudioMute_DoesNotSendBlindWhenNothingAnswers()
    {
        await Finish(_audioSystem.SetAudioMuteAsync(MuteState.On));

        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Once);
        _mockClient.Verify(x => x.Send(MutePressed), Times.Never);
        Assert.Contains(_audioSystem.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task ToggleAudioMute_SendsTheMuteKeyOnce()
    {
        Receive("\x54\x7A\x1C");

        _audioSystem.ToggleAudioMute();
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, KeyReleased) == 1, "the mute key");

        _mockClient.Verify(x => x.Send(MutePressed), Times.Once);
        _mockClient.Verify(x => x.Send(KeyReleased), Times.Once);
        Assert.Equal(MuteState.On, _audioSystem.MuteState);
    }

    [Fact]
    public async Task ToggleAudioMute_FromTwoThreadsAtOnce_EndsWhereItStarted()
    {
        Receive("\x54\x7A\x1C");

        await Task.WhenAll(Task.Run(_audioSystem.ToggleAudioMute), Task.Run(_audioSystem.ToggleAudioMute));
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, MutePressed) == 2, "two mute keys");

        Assert.Equal(MuteState.Off, _audioSystem.MuteState);
    }

    [Fact]
    public async Task ToggleAudioMute_FromAMuteSubscriberOnAnotherThread_DoesNotDeadlock()
    {
        Receive("\x54\x7A\x1C");
        Task? toggle = null;
        var joined = false;
        _audioSystem.MuteStateHandlers += _ =>
        {
            if (toggle != null)
                return;
            toggle = Task.Run(_audioSystem.ToggleAudioMute);
            joined = toggle.Wait(ToggleJoinTimeout);
        };

        _audioSystem.ToggleAudioMute();

        Assert.True(joined);
        await Wait.UntilAsync(_time, Step, () => Sent(_mockClient, MutePressed) == 2, "two mute keys");
        Assert.Equal(MuteState.Off, _audioSystem.MuteState);
    }

    [Fact]
    public async Task Keys_ReadTheStatusBackAfterADelay()
    {
        _audioSystem.LevelUp(1);
        await Wait.UntilAsync(_time, KeyHold, () => QueuedSends(_audioSystem).IsCompleted, "the key was never sent");
        _mockClient.Verify(x => x.Send(GiveAudioStatus), Times.Never);

        _time.Advance(CecAudioSystem.StatusReadDelayAfterKey);
        await Wait.UntilAsync(() => Sent(_mockClient, GiveAudioStatus) == 1, "the status was never read back");

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
    public async Task LogicalAddresses_ChangeTheHeaders()
    {
        var mock = TestFactory.CreateSerialClient();
        var audioSystem = new CecAudioSystem(mock.Object, "From the TV", 5, 0, NoPolling, _time);

        audioSystem.ToggleAudioMute();
        await Wait.UntilAsync(_time, Step, () => Sent(mock, ['\x05', '\x45']) == 1, "the mute key");
        mock.Verify(x => x.Send(new[] { '\x05', '\x44', '\x43' }));

        mock.Object.ResponseHandlers!.Invoke("\x50\x7A\x1C");
        Assert.Equal(28, audioSystem.Volume);
    }

    [Fact]
    public async Task Dispose_StopsRespondingToTheClientAndStopsThePollAndTheStatusRead()
    {
        var mock = TestFactory.CreateSerialClient();
        SetConnectionState(mock, ConnectionState.Disconnected);
        var audioSystem = Create(mock, "Disposed", PollSeconds);
        var pollWorker = PollWorker(audioSystem);
        await Wait.UntilAsync(_time, Step, () => pollWorker.IsRunning, "the poll never started");
        audioSystem.LevelUp(1);
        await FinishTheQueuedSends(audioSystem);
        Assert.NotNull(StatusReadTimer(audioSystem));

        audioSystem.Dispose();
        audioSystem.Dispose();

        Assert.Null(mock.Object.ResponseHandlers);
        Assert.Null(mock.Object.ConnectionStateHandlers);
        Assert.False(pollWorker.IsRunning);
        Assert.Null(StatusReadTimer(audioSystem));
        Assert.DoesNotContain<LogBase>(audioSystem, LogBaseRegistry.GetAll());
        Assert.DoesNotContain<LogBase>(pollWorker, LogBaseRegistry.GetAll());
    }

    [Collection(LoggerFactoryCollection.Name)]
    public class LogTest
    {
        private readonly FakeTimeProvider _time = new();

        [Theory]
        [InlineData(0x0F, 0x04, "CEC logical addresses are 0x00 to 0x0E, the audio system is 0x0F and this device is 0x04")]
        [InlineData(0x05, 0x0F, "CEC logical addresses are 0x00 to 0x0E, the audio system is 0x05 and this device is 0x0F")]
        public async Task Constructor_GivenAnInvalidLogicalAddress_RaisesAnIssueAndSendsNothing(int audioSystemAddress, int ownAddress, string expectedMessage)
        {
            var logs = new ScopeCapturingLoggerFactory();
            var mock = TestFactory.CreateSerialClient();
            var audioSystem = Logged.Create(logs, () => new CecAudioSystem(mock.Object, "Bad", (byte)audioSystemAddress, (byte)ownAddress, NoPolling, _time));

            audioSystem.PollAudioStatus();
            await QueuedSends(audioSystem);

            var issue = Assert.Single(audioSystem.GetOngoingIssues(), i => i.Key == CecAudioSystem.LogicalAddressIssueKey);
            Assert.Equal(expectedMessage, issue.Message);
            Assert.Equal(IssueSeverity.Critical, issue.Severity);
            mock.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
            Assert.Contains(logs.Logs, x => x.Message.StartsWith("Not sending ") && x.Message.EndsWith($": {expectedMessage}"));
        }

        [Fact]
        public void VolumeStep_GivenZero_KeepsTheStepAndLogs()
        {
            var logs = new ScopeCapturingLoggerFactory();
            var audioSystem = Logged.Create(logs, () => new CecAudioSystem(TestFactory.CreateSerialClient().Object, "Test Sonos",
                CecAudioSystem.LogicalAddressAudioSystem, CecAudioSystem.LogicalAddressPlayback1, NoPolling, _time));

            audioSystem.VolumeStep = 0;

            Assert.Equal(CecAudioSystem.DefaultVolumeStep, audioSystem.VolumeStep);
            Assert.Contains(logs.Logs, x => x.Message == "The volume step is at least one level per press, not 0, so it stays 2");
            Assert.Contains(audioSystem.Events, e => e.Type == EventType.Error);
        }
    }
}
