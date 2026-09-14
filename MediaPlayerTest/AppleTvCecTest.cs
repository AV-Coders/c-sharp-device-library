using System.Diagnostics;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class AppleTvCecTest
{
    private readonly AppleTvCec _appleTv;
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();

    // Display, TopMenu and PopupMenu: the real Apple TV answered with Feature Abort "invalid operand".
    // VolumeUp, VolumeDown and Mute: acknowledged, but only relayed back to the TV, never acted on.
    // Guide, ChannelUp/Down, Eject and the colour keys: acknowledged, confirmed no effect on screen.
    private static readonly RemoteButton[] _excludedButtons =
    [
        RemoteButton.Display, RemoteButton.TopMenu, RemoteButton.PopupMenu,
        RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Mute,
        RemoteButton.Guide, RemoteButton.ChannelUp, RemoteButton.ChannelDown, RemoteButton.Eject,
        RemoteButton.Blue, RemoteButton.Red, RemoteButton.Green, RemoteButton.Yellow
    ];

    public static IEnumerable<object[]> RemoteButtonValues()
    {
        return Enum.GetValues(typeof(RemoteButton))
            .Cast<RemoteButton>()
            .Where(rb => !_excludedButtons.Contains(rb))
            .Select(rb => new object[] { rb });
    }

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    public AppleTvCecTest()
    {
        // pollTime 0: no poll worker, so nothing sends unless a test asks for it.
        _appleTv = new AppleTvCec(_mockClient.Object, "Test Apple TV", pollTime: 0);
    }

    private void Receive(string frame) => _mockClient.Object.ResponseHandlers!.Invoke(frame);

    [Fact]
    public void PowerOn_SendsThePowerOnFunctionKey()
    {
        _appleTv.PowerOn();

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x45' }));
        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
    }

    [Fact]
    public void PowerOff_SendsStandby()
    {
        _appleTv.PowerOff();

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }));
        Assert.Equal(PowerState.Off, _appleTv.DesiredPowerState);
    }

    [Fact]
    public void LogicalAddress_ChangesTheHeaders()
    {
        var mock = TestFactory.CreateSerialClient();
        var appleTv = new AppleTvCec(mock.Object, "Playback 2", AppleTvCec.LogicalAddressPlayback2, pollTime: 0);

        appleTv.PowerOn();
        mock.Verify(x => x.Send(new[] { '\x08', '\x44', '\x6D' }));

        mock.Object.ResponseHandlers!.Invoke("\x80\x90\x01");
        Assert.Equal(PowerState.Off, appleTv.PowerState);
    }

    [Fact]
    public void Constructor_RejectsAnInvalidLogicalAddress()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppleTvCec(_mockClient.Object, "Bad", 0x0F, pollTime: 0));
    }

    [Fact]
    public void MakeActiveSource_BroadcastsSetStreamPathToTheKnownPhysicalAddress()
    {
        _appleTv.MakeActiveSource();
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x86', '\x10', '\x00' }));

        Receive("\x4F\x84\x20\x00\x04");
        _appleTv.MakeActiveSource();
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x86', '\x20', '\x00' }));
        Assert.Equal("2.0.0.0", _appleTv.PhysicalAddress);
    }

    [Fact]
    public void Discover_SendsTheDiscoveryRequests()
    {
        _appleTv.Discover();

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x83' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x46' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x8C' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x9F' }));
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x85' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x1A', '\x03' })); // Deck status, once
    }

    [Fact]
    public void PollPowerStatus_DiscoversFirstThenAsksForPowerStatus()
    {
        _appleTv.PollPowerStatus();

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x83' }), Times.Once);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x8F' }), Times.Once);

        Receive("\x4F\x84\x10\x00\x04"); // Report Physical Address completes discovery
        _appleTv.PollPowerStatus();

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x83' }), Times.Once);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x8F' }), Times.Exactly(2));
    }

    [Fact]
    public void PollPowerStatus_UnansweredPollsBecomeACommunicationError()
    {
        Receive("\x4F\x84\x10\x00\x04"); // discovered, so polls are just Give Power Status
        Receive("\x40\x90\x00");
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);

        for (var i = 0; i < AppleTvCec.MaxUnansweredPolls; i++)
            _appleTv.PollPowerStatus();
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
        Assert.Equal(PowerState.On, _appleTv.PowerState);

        _appleTv.PollPowerStatus();
        Assert.Equal(CommunicationState.Error, _appleTv.CommunicationState);
        Assert.Equal(PowerState.Unknown, _appleTv.PowerState);

        Receive("\x40\x90\x00");
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
        Assert.Equal(PowerState.On, _appleTv.PowerState);
    }

    [Fact]
    public void SendFrame_SpacesConsecutiveFramesApart()
    {
        var stamps = new List<long>();
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Callback(() => stamps.Add(Stopwatch.GetTimestamp()));
        var tolerance = TimeSpan.FromMilliseconds(20);

        _appleTv.PowerOff();                       // one frame
        _appleTv.SendIRCode(RemoteButton.Play);    // pressed + released, straight after
        Receive("\x40\x8F");                       // a reply sent from inside the receive callback

        Assert.Equal(4, stamps.Count);
        TimeSpan Gap(int i) => Stopwatch.GetElapsedTime(stamps[i - 1], stamps[i]);

        Assert.True(Gap(1) >= AppleTvCec.MinimumFrameSpacing - tolerance,
            $"The key press went out only {Gap(1).TotalMilliseconds:F0} ms after Standby");
        // The hold between Pressed and Released is the shorter KeyHold, not the generic spacing:
        // a ~300 ms hold makes tvOS repeat the key.
        Assert.True(Gap(2) >= AppleTvCec.KeyHold - tolerance,
            $"The key was held for only {Gap(2).TotalMilliseconds:F0} ms");
        Assert.True(Gap(2) < AppleTvCec.ReplySpacing,
            $"The key was held for {Gap(2).TotalMilliseconds:F0} ms, long enough to trigger key repeat");
        Assert.True(Gap(3) >= AppleTvCec.MinimumFrameSpacing - tolerance,
            $"The reply went out only {Gap(3).TotalMilliseconds:F0} ms after the key release");
    }

    [Fact]
    public void SendFrame_WaitsLongerAfterAQuery()
    {
        var stamps = new List<long>();
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Callback(() => stamps.Add(Stopwatch.GetTimestamp()));
        Receive("\x4F\x84\x10\x00\x04"); // discovered, so the poll is a single Give Power Status

        _appleTv.PollPowerStatus();                // query: its answer must clear the link first
        _appleTv.SendIRCode(RemoteButton.Play);

        Assert.Equal(3, stamps.Count);
        var gap = Stopwatch.GetElapsedTime(stamps[0], stamps[1]);
        Assert.True(gap >= AppleTvCec.ReplySpacing - TimeSpan.FromMilliseconds(20),
            $"The key went out only {gap.TotalMilliseconds:F0} ms after the poll");
    }

    [Theory]
    [InlineData("\x40\x90\x00", PowerState.On)]
    [InlineData("\x40\x90\x01", PowerState.Off)]
    [InlineData("\x40\x90\x02", PowerState.Warming)]
    [InlineData("\x40\x90\x03", PowerState.Cooling)]
    [InlineData("\x4F\x90\x01", PowerState.Off)]
    [InlineData("\x4F\x90\x00", PowerState.On)]
    public void HandleResponse_ParsesDirectedAndBroadcastPowerStatus(string frame, PowerState expected)
    {
        Receive(frame);

        Assert.Equal(expected, _appleTv.PowerState);
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ToleratesTheDoubledHeaderQuirk()
    {
        Receive("\x4F\x4F\x90\x01");

        Assert.Equal(PowerState.Off, _appleTv.PowerState);
    }

    [Fact]
    public void HandleResponse_IgnoresFramesFromOtherDevices()
    {
        Receive("\x80\x90\x01");
        Receive("\x0F\x90\x01");

        Assert.Equal(PowerState.Unknown, _appleTv.PowerState);
        Assert.Equal(CommunicationState.NotAttempted, _appleTv.CommunicationState);
    }

    [Fact]
    public void HandleResponse_IgnoresPollingMessagesAndEmptyFrames()
    {
        Receive("\x40");
        Receive("");

        Assert.Equal(CommunicationState.NotAttempted, _appleTv.CommunicationState);
    }

    [Fact]
    public void HandleResponse_RepeatedFramesAreIdempotent()
    {
        Receive("\x40\x90\x00");
        Receive("\x40\x90\x00");
        Receive("\x40\x90\x00");

        Assert.Equal(PowerState.On, _appleTv.PowerState);
    }

    [Fact]
    public void HandleResponse_StandbyFromTheAppleTvIsPowerOff()
    {
        Receive("\x4F\x82\x10\x00");
        Receive("\x4F\x36");

        Assert.Equal(PowerState.Off, _appleTv.PowerState);
        Assert.False(_appleTv.IsActiveSource);
    }

    [Fact]
    public void PhysicalRemote_SleepIsNotFoughtAfterPowerOn()
    {
        _appleTv.PowerOn();
        _mockClient.Invocations.Clear();

        // Captured from the Siri remote's power button
        Receive("\x4F\x36");
        Receive("\x40\x36");
        Receive("\x40\x9D\x10\x00");
        Receive("\x4F\x90\x01");

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }), Times.Never);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Equal(PowerState.Off, _appleTv.DesiredPowerState);
        Assert.Equal(PowerState.Off, _appleTv.PowerState);
        Assert.Empty(_appleTv.GetOngoingIssues());
    }

    [Fact]
    public void PhysicalRemote_WakeIsNotFoughtAfterPowerOff()
    {
        _appleTv.PowerOff();
        _mockClient.Invocations.Clear();

        Receive("\x4F\x90\x00");     // the broadcast report is the wake intent
        Receive("\x40\x04");         // as is Image View On
        Receive("\x4F\x82\x10\x00"); // Active Source is not (it also answers our own requests)
        Receive("\x40\x8E\x00");

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }), Times.Never);
        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
        Assert.Equal(PowerState.On, _appleTv.PowerState);
        Assert.True(_appleTv.IsActiveSource);
        Assert.Empty(_appleTv.GetOngoingIssues());
    }

    [Fact]
    public void PhysicalRemote_WakeSeenAsImageViewOnThenPowerStatusIsNotFought()
    {
        _appleTv.PowerOff();
        _mockClient.Invocations.Clear();

        Receive("\x40\x04");
        Receive("\x4F\x90\x00");

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }), Times.Never);
        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
    }

    [Fact]
    public void ActiveSourceAloneIsNotWakeIntent()
    {
        _appleTv.PowerOff();
        _mockClient.Invocations.Clear();

        Receive("\x4F\x82\x10\x00"); // e.g. the reply to the driver's own Request Active Source

        Assert.True(_appleTv.IsActiveSource);
        Assert.Equal(PowerState.Off, _appleTv.DesiredPowerState);
    }

    [Fact]
    public void ReplayedSleepFrameAfterTheWindowDoesNotUndoPowerOn()
    {
        var mock = TestFactory.CreateSerialClient();
        var appleTv = new AppleTvCec(mock.Object, "Replay", pollTime: 0,
            duplicateResponseWindow: TimeSpan.FromMilliseconds(20));
        void Rx(string f) => mock.Object.ResponseHandlers!.Invoke(f);

        // Physical remote sleeps the box
        Rx("\x4F\x36");
        Rx("\x40\x36");
        Rx("\x40\x9D\x10\x00");
        Rx("\x4F\x90\x01");
        Assert.Equal(PowerState.Off, appleTv.DesiredPowerState);

        // The panel turns it on; the release NACKs while the box wakes and CrestronCecStream
        // replays the stale last-received value, well after the dedupe window.
        appleTv.PowerOn();
        mock.Invocations.Clear();
        Thread.Sleep(60);
        Rx("\x4F\x90\x01");

        Assert.Equal(PowerState.On, appleTv.DesiredPowerState);
        mock.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Equal(CommunicationState.Okay, appleTv.CommunicationState);
    }

    [Fact]
    public void KeyPressAndReleaseCannotBeInterleavedByAnotherSender()
    {
        var sent = new List<(char[] frame, long at)>();
        var pressedSeen = new ManualResetEventSlim();
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Callback<char[]>(f =>
        {
            lock (sent) sent.Add((f, Stopwatch.GetTimestamp()));
            if (f.Length == 3 && f[1] == '\x44')
                pressedSeen.Set();
        });
        // A second sender that tries to get onto the bus the moment the Pressed frame has gone out.
        var intruder = new Thread(() =>
        {
            pressedSeen.Wait();
            _appleTv.PowerOff();
        });
        intruder.Start();

        _appleTv.SendIRCode(RemoteButton.Play);
        intruder.Join();

        Assert.Equal(3, sent.Count);
        Assert.Equal(new[] { '\x04', '\x44', '\x44' }, sent[0].frame);
        Assert.Equal(new[] { '\x04', '\x45' }, sent[1].frame); // released straight after pressed
        Assert.Equal(new[] { '\x04', '\x36' }, sent[2].frame); // the intruder only got in afterwards
        var hold = Stopwatch.GetElapsedTime(sent[0].at, sent[1].at);
        Assert.True(hold < AppleTvCec.ReplySpacing, $"The key was held for {hold.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void PollFindingTheDeviceOffWithoutAStandbyIsStillEnforced()
    {
        _appleTv.PowerOn();
        _mockClient.Invocations.Clear();

        Receive("\x40\x90\x01"); // poll reply only, nothing from the device first

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }), Times.Once);
        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
        Assert.Equal(PowerState.Off, _appleTv.PowerState);
        Assert.Contains(_appleTv.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Fact]
    public void HandleResponse_TracksActiveSource()
    {
        var changes = new List<bool>();
        _appleTv.ActiveSourceHandlers += changes.Add;

        Receive("\x4F\x82\x10\x00");
        Assert.True(_appleTv.IsActiveSource);

        Receive("\x40\x9D\x10\x00");
        Assert.False(_appleTv.IsActiveSource);

        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void HandleResponse_RecordsDiscoveryDetails()
    {
        Receive("\x4F\x84\x10\x00\x04");
        Receive("\x40\x47" + "Apple TV"); // Kept apart: C# would read "\x47A" as one escape
        Receive("\x4F\x87\x00\x10\xFA");
        Receive("\x40\x9E\x06");
        Receive("\x40\x8E\x00");

        Assert.Contains(_appleTv.Details, d => d is { Label: AppleTvCec.PhysicalAddressDetailLabel, Value: "1.0.0.0" });
        Assert.Contains(_appleTv.Details, d => d is { Label: AppleTvCec.OsdNameDetailLabel, Value: "Apple TV" });
        Assert.Contains(_appleTv.Details, d => d is { Label: AppleTvCec.VendorDetailLabel, Value: "Apple (00:10:FA)" });
        Assert.Contains(_appleTv.Details, d => d is { Label: AppleTvCec.CecVersionDetailLabel, Value: "2.0" });
        Assert.Contains(_appleTv.Details, d => d is { Label: AppleTvCec.MenuDetailLabel, Value: "Activated" });
    }

    [Theory]
    [InlineData("\x40\x1B\x11", TransportState.Playing)]
    [InlineData("\x40\x1B\x14", TransportState.Paused)]
    [InlineData("\x40\x1B\x1A", TransportState.Stopped)]
    [InlineData("\x40\x1B\x12", TransportState.Recording)]
    public void HandleResponse_ParsesDeckStatus(string frame, TransportState expected)
    {
        Receive(frame);

        Assert.Equal(expected, _appleTv.TransportState);
    }

    [Fact]
    public void HandleResponse_FeatureAbortOnDeckStatusLeavesTransportStateAlone()
    {
        Receive("@");
        Receive("@ ");

        Assert.Equal(TransportState.Playing, _appleTv.TransportState);

        var fresh = new AppleTvCec(TestFactory.CreateSerialClient().Object, "Fresh", pollTime: 0);
        Assert.Equal(TransportState.Unknown, fresh.TransportState);
    }

    [Fact]
    public void HandleResponse_AnswersTheAppleTvsQueriesAsAnOnTv()
    {
        Receive("\x40\x8F");
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x90', '\x00' }), Times.Once);

        Receive("\x40\x83");
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x84', '\x00', '\x00', '\x00' }), Times.Once);

        Receive("\x40\x9F");
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x9E', '\x05' }), Times.Once);
    }

    [Fact]
    public void HandleResponse_AnswersARepeatedQueryOnlyOnce()
    {
        Receive("\x40\x8F");
        Receive("\x40\x8F"); // CrestronCecStream re-emitting the stale value on a NACK event

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x90', '\x00' }), Times.Once);
    }

    [Fact]
    public void HandleResponse_DoesNotAnswerQueriesWhenDisabled()
    {
        var mock = TestFactory.CreateSerialClient();
        _ = new AppleTvCec(mock.Object, "Quiet", answerTvQueries: false, pollTime: 0);

        mock.Object.ResponseHandlers!.Invoke("\x40\x8F");

        mock.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
    }

    [Fact]
    public void HandleResponse_FeatureAbortOnAKeyDoesNotThrow()
    {
        Receive("\x40\x00\x44\x03");

        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _mockClient.Invocations.Clear();

        _appleTv.SendIRCode(button);

        Assert.Contains(button, _appleTv.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.AtLeastOnce);
    }

    [Theory]
    [MemberData(nameof(ExcludedButtonValues))]
    public void SendIRCode_ExcludedButtonsAreNotSupportedAndSendNothing(RemoteButton button)
    {
        _mockClient.Invocations.Clear();

        _appleTv.SendIRCode(button);

        Assert.DoesNotContain(button, _appleTv.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
    }

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b),
            _appleTv.SupportedButtons.OrderBy(b => b));
    }

    [Theory]
    [InlineData(RemoteButton.Enter, '\x00')]
    [InlineData(RemoteButton.Home, '\x09')]
    [InlineData(RemoteButton.Back, '\x0D')]
    [InlineData(RemoteButton.Menu, '\x0D')] // tvOS has no separate menu function; Menu is Back
    [InlineData(RemoteButton.Up, '\x01')]
    [InlineData(RemoteButton.Right, '\x04')]
    [InlineData(RemoteButton.Play, '\x44')]
    [InlineData(RemoteButton.Pause, '\x46')]
    [InlineData(RemoteButton.Next, '\x4B')]
    [InlineData(RemoteButton.Previous, '\x4C')]
    [InlineData(RemoteButton.Subtitle, '\x51')]
    public void SendIRCode_SendsPressedThenReleased(RemoteButton button, char code)
    {
        _appleTv.SendIRCode(button);

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', code }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x45' }));
    }

    [Fact]
    public void SendIRCode_PowerOnAndOffUseTheDriverPowerMethods()
    {
        _appleTv.SendIRCode(RemoteButton.PowerOn);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }));

        _appleTv.SendIRCode(RemoteButton.PowerOff);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }));
    }

    [Fact]
    public void SendIRCode_PowerTogglesFromTheKnownState()
    {
        Receive("\x40\x90\x00");
        _appleTv.SendIRCode(RemoteButton.Power);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }), Times.Once);

        Receive("\x40\x90\x01");
        _appleTv.SendIRCode(RemoteButton.Power);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }), Times.Once);
    }

    [Fact]
    public void SetChannel_SendsTheDigits()
    {
        _appleTv.SetChannel(12);

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x21' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x22' }));
    }

    [Fact]
    public void ChannelUpAndDown_AreNotSupportedAndSendNothing()
    {
        _appleTv.ChannelUp();
        _appleTv.ChannelDown();

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Contains(_appleTv.Events, e => e.Type == EventType.Error && e.Info.Contains("ChannelUp"));
        Assert.Contains(_appleTv.Events, e => e.Type == EventType.Error && e.Info.Contains("ChannelDown"));
    }

    [Fact]
    public void ToggleSubtitles_SendsSubPicture()
    {
        _appleTv.ToggleSubtitles();

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x51' }));
    }
}
