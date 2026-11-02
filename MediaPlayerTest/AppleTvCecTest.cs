using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.MediaPlayer.Tests;

public class AppleTvCecTest
{
    private static readonly TimeSpan AnySpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();
    private readonly AppleTvCec _appleTv;

    private static readonly RemoteButton[] _excludedButtons =
    [
        RemoteButton.Display, RemoteButton.TopMenu, RemoteButton.PopupMenu,
        RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Mute,
        RemoteButton.Guide, RemoteButton.ChannelUp, RemoteButton.ChannelDown, RemoteButton.Eject,
        RemoteButton.Blue, RemoteButton.Red, RemoteButton.Green, RemoteButton.Yellow
    ];

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    public AppleTvCecTest()
    {
        _appleTv = Create(_mockClient, "Test Apple TV");
    }

    private AppleTvCec Create(Mock<SerialClient> client, string name, byte logicalAddress = AppleTvCec.LogicalAddressPlayback1,
        bool answerTvQueries = true) =>
        new(client.Object, name, logicalAddress, answerTvQueries, 0, null, _time);

    private void Receive(string frame) => _mockClient.Object.ResponseHandlers!.Invoke(frame);

    private Task RunWithTimePassing(Action action) => RunWithTimePassing(_appleTv, action);

    private async Task RunWithTimePassing(AppleTvCec appleTv, Action action)
    {
        await Task.Run(action);
        await Wait.UntilAsync(_time, AnySpacing, () => SendsAreIdle(appleTv), "The driver never finished sending");
        _time.Advance(AnySpacing);
    }

    private static bool SendsAreIdle(AppleTvCec appleTv) => ((SemaphoreSlim)typeof(AppleTvCec)
        .GetField("_spacingLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(appleTv)!).CurrentCount == 1;

    private ConcurrentQueue<char[]> RecordFrames()
    {
        var sent = new ConcurrentQueue<char[]>();
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Callback<char[]>(sent.Enqueue);
        return sent;
    }

    private long LastSendTimestamp() => (long)typeof(AppleTvCec)
        .GetField("_lastSendTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_appleTv)!;

    private Task FrameSent(ConcurrentQueue<char[]> sent, int frames) =>
        Wait.UntilAsync(() => sent.Count == frames && LastSendTimestamp() == _time.GetTimestamp(),
            $"Frame {frames} was never sent");

    private async Task AdvanceThenExpect(ConcurrentQueue<char[]> sent, TimeSpan spacing, int frames)
    {
        _time.Advance(spacing - OneTick);
        Assert.Equal(frames - 1, sent.Count);
        _time.Advance(OneTick);
        await FrameSent(sent, frames);
    }

    [Fact]
    public async Task PowerOn_SendsThePowerOnFunctionKey()
    {
        await RunWithTimePassing(_appleTv.PowerOn);

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
    public async Task LogicalAddress_ChangesTheHeaders()
    {
        var mock = TestFactory.CreateSerialClient();
        var appleTv = Create(mock, "Playback 2", AppleTvCec.LogicalAddressPlayback2);

        await RunWithTimePassing(appleTv, appleTv.PowerOn);
        mock.Verify(x => x.Send(new[] { '\x08', '\x44', '\x6D' }));

        await RunWithTimePassing(appleTv, () => mock.Object.ResponseHandlers!.Invoke("\x80\x90\x01"));
        Assert.Equal(PowerState.Off, appleTv.PowerState);
    }

    [Fact]
    public void Constructor_WithAnInvalidLogicalAddress_RaisesAnIssueAndSendsNothing()
    {
        var mock = TestFactory.CreateSerialClient();
        var appleTv = Create(mock, "Bad", 0x0F);

        appleTv.PowerOn();
        appleTv.PollPowerStatus();

        mock.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Contains(appleTv.GetOngoingIssues(),
            issue => issue.Key == "logical-address" && issue.Message.Contains("15") && issue.Severity == IssueSeverity.Critical);
    }

    [Fact]
    public async Task MakeActiveSource_BroadcastsSetStreamPathToTheKnownPhysicalAddress()
    {
        _appleTv.MakeActiveSource();
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x86', '\x10', '\x00' }));

        Receive("\x4F\x84\x20\x00\x04");
        await RunWithTimePassing(_appleTv.MakeActiveSource);
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x86', '\x20', '\x00' }));
        Assert.Equal("2.0.0.0", _appleTv.PhysicalAddress);
    }

    [Fact]
    public async Task Discover_SendsTheDiscoveryRequests()
    {
        await RunWithTimePassing(_appleTv.Discover);

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x83' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x46' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x8C' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x9F' }));
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x85' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x1A', '\x03' }));
    }

    [Fact]
    public async Task PollPowerStatus_DiscoversFirstThenAsksForPowerStatus()
    {
        await RunWithTimePassing(_appleTv.PollPowerStatus);

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x83' }), Times.Once);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x8F' }), Times.Once);

        Receive("\x4F\x84\x10\x00\x04");
        await RunWithTimePassing(_appleTv.PollPowerStatus);

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x83' }), Times.Once);
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x8F' }), Times.Exactly(2));
    }

    [Fact]
    public async Task PollPowerStatus_UnansweredPollsBecomeACommunicationError()
    {
        Receive("\x4F\x84\x10\x00\x04");
        Receive("\x40\x90\x00");
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);

        await RunWithTimePassing(() =>
        {
            for (var i = 0; i < AppleTvCec.MaxUnansweredPolls; i++)
                _appleTv.PollPowerStatus();
        });
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
        Assert.Equal(PowerState.On, _appleTv.PowerState);

        await RunWithTimePassing(_appleTv.PollPowerStatus);
        Assert.Equal(CommunicationState.Error, _appleTv.CommunicationState);
        Assert.Equal(PowerState.Unknown, _appleTv.PowerState);

        _time.Advance(AppleTvCec.DefaultDuplicateResponseWindow);
        Receive("\x40\x90\x00");
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
        Assert.Equal(PowerState.On, _appleTv.PowerState);
    }

    [Fact]
    public async Task SendFrame_SpacesConsecutiveFramesApart()
    {
        var sent = RecordFrames();

        _appleTv.PowerOff();
        _appleTv.SendIRCode(RemoteButton.Play);

        await FrameSent(sent, 1);
        await AdvanceThenExpect(sent, AppleTvCec.MinimumFrameSpacing, 2);
        await AdvanceThenExpect(sent, AppleTvCec.KeyHold, 3);
        await Wait.UntilAsync(() => SendsAreIdle(_appleTv), "The key was never finished");
        Receive("\x40\x8F");
        await AdvanceThenExpect(sent, AppleTvCec.MinimumFrameSpacing, 4);
    }

    [Fact]
    public async Task SendIRCode_DuringTheSpacing_ReturnsAtOnceAndSendsOnceTheSpacingHasPassed()
    {
        var sent = RecordFrames();
        _appleTv.PowerOff();

        _appleTv.SendIRCode(RemoteButton.Play);

        Assert.Single(sent);
        await AdvanceThenExpect(sent, AppleTvCec.MinimumFrameSpacing, 2);
    }

    [Fact]
    public async Task SendFrame_WaitsLongerAfterAQuery()
    {
        var sent = RecordFrames();
        Receive("\x4F\x84\x10\x00\x04");

        _appleTv.PollPowerStatus();
        _appleTv.SendIRCode(RemoteButton.Play);

        await FrameSent(sent, 1);
        await AdvanceThenExpect(sent, AppleTvCec.QueryReplySpacing, 2);
        await AdvanceThenExpect(sent, AppleTvCec.KeyHold, 3);
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
    public async Task PhysicalRemote_SleepIsNotFoughtAfterPowerOn()
    {
        await RunWithTimePassing(_appleTv.PowerOn);
        _mockClient.Invocations.Clear();

        await RunWithTimePassing(() =>
        {
            Receive("\x4F\x36");
            Receive("\x40\x36");
            Receive("\x40\x9D\x10\x00");
            Receive("\x4F\x90\x01");
        });

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Equal(PowerState.Off, _appleTv.DesiredPowerState);
        Assert.Equal(PowerState.Off, _appleTv.PowerState);
        Assert.Empty(_appleTv.GetOngoingIssues());
    }

    [Fact]
    public async Task PhysicalRemote_WakeIsNotFoughtAfterPowerOff()
    {
        _appleTv.PowerOff();
        _mockClient.Invocations.Clear();

        await RunWithTimePassing(() =>
        {
            Receive("\x4F\x90\x00");
            Receive("\x40\x04");
            Receive("\x4F\x82\x10\x00");
            Receive("\x40\x8E\x00");
        });

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }), Times.Never);
        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
        Assert.Equal(PowerState.On, _appleTv.PowerState);
        Assert.True(_appleTv.IsActiveSource);
        Assert.Empty(_appleTv.GetOngoingIssues());
    }

    [Fact]
    public async Task PhysicalRemote_WakeSeenAsImageViewOnThenPowerStatusIsNotFought()
    {
        _appleTv.PowerOff();
        _mockClient.Invocations.Clear();

        await RunWithTimePassing(() =>
        {
            Receive("\x40\x04");
            Receive("\x4F\x90\x00");
        });

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }), Times.Never);
        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
    }

    [Fact]
    public async Task ActiveSourceAloneIsNotWakeIntent()
    {
        _appleTv.PowerOff();
        _mockClient.Invocations.Clear();

        await RunWithTimePassing(() => Receive("\x4F\x82\x10\x00"));

        Assert.True(_appleTv.IsActiveSource);
        Assert.Equal(PowerState.Off, _appleTv.DesiredPowerState);
    }

    [Fact]
    public async Task ReplayedSleepFrameAfterTheWindowDoesNotUndoPowerOn()
    {
        Receive("\x4F\x36");
        Receive("\x40\x36");
        Receive("\x40\x9D\x10\x00");
        Receive("\x4F\x90\x01");
        Assert.Equal(PowerState.Off, _appleTv.DesiredPowerState);

        await RunWithTimePassing(_appleTv.PowerOn);
        _mockClient.Invocations.Clear();
        _time.Advance(AppleTvCec.DefaultDuplicateResponseWindow);
        await RunWithTimePassing(() => Receive("\x4F\x90\x01"));

        Assert.Equal(PowerState.On, _appleTv.DesiredPowerState);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Equal(CommunicationState.Okay, _appleTv.CommunicationState);
    }

    [Fact]
    public async Task KeyPressAndReleaseCannotBeInterleavedByAnotherSender()
    {
        var sent = RecordFrames();
        var intruder = new Thread(() =>
        {
            SpinWait.SpinUntil(() => !sent.IsEmpty);
            _appleTv.PowerOff();
        });

        var sending = Task.Run(() =>
        {
            intruder.Start();
            _appleTv.SendIRCode(RemoteButton.Play);
            intruder.Join();
        });

        await FrameSent(sent, 1);
        await AdvanceThenExpect(sent, AppleTvCec.KeyHold, 2);
        await sending;
        await Wait.UntilAsync(_time, AppleTvCec.MinimumFrameSpacing, () => sent.Count == 3, "The other sender's frame was never sent");

        Assert.Equal(new[] { new[] { '\x04', '\x44', '\x44' }, new[] { '\x04', '\x45' }, new[] { '\x04', '\x36' } }, sent.ToArray());
    }

    [Fact]
    public async Task PollFindingTheDeviceOffWithoutAStandbyIsStillEnforced()
    {
        await RunWithTimePassing(_appleTv.PowerOn);
        _mockClient.Invocations.Clear();

        await RunWithTimePassing(() => Receive("\x40\x90\x01"));

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
        Receive("\x40\x47" + "Apple TV");
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
        Receive("\x40\x1B\x11");
        Receive("\x40\x00\x1A\x01");

        Assert.Equal(TransportState.Playing, _appleTv.TransportState);

        var fresh = Create(TestFactory.CreateSerialClient(), "Fresh");
        Assert.Equal(TransportState.Unknown, fresh.TransportState);
    }

    [Fact]
    public async Task HandleResponse_AnswersTheAppleTvsQueriesAsAnOnTv()
    {
        Receive("\x40\x8F");
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x90', '\x00' }), Times.Once);

        await RunWithTimePassing(() => Receive("\x40\x83"));
        _mockClient.Verify(x => x.Send(new[] { '\x0F', '\x84', '\x00', '\x00', '\x00' }), Times.Once);

        await RunWithTimePassing(() => Receive("\x40\x9F"));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x9E', '\x05' }), Times.Once);
    }

    [Fact]
    public void HandleResponse_AnswersARepeatedQueryOnlyOnce()
    {
        Receive("\x40\x8F");
        Receive("\x40\x8F");

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x90', '\x00' }), Times.Once);
    }

    [Fact]
    public void HandleResponse_DoesNotAnswerQueriesWhenDisabled()
    {
        var mock = TestFactory.CreateSerialClient();
        _ = Create(mock, "Quiet", answerTvQueries: false);

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
    [InlineData(RemoteButton.Menu, '\x0D')]
    [InlineData(RemoteButton.Up, '\x01')]
    [InlineData(RemoteButton.Right, '\x04')]
    [InlineData(RemoteButton.Play, '\x44')]
    [InlineData(RemoteButton.Pause, '\x46')]
    [InlineData(RemoteButton.Next, '\x4B')]
    [InlineData(RemoteButton.Previous, '\x4C')]
    [InlineData(RemoteButton.Subtitle, '\x51')]
    public async Task SendIRCode_SendsPressedThenReleased(RemoteButton button, char code)
    {
        await RunWithTimePassing(() => _appleTv.SendIRCode(button));

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', code }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x45' }));
    }

    [Fact]
    public async Task SendIRCode_PowerOnAndOffUseTheDriverPowerMethods()
    {
        await RunWithTimePassing(() => _appleTv.SendIRCode(RemoteButton.PowerOn));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }));

        await RunWithTimePassing(() => _appleTv.SendIRCode(RemoteButton.PowerOff));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }));
    }

    [Fact]
    public async Task SendIRCode_PowerTogglesFromTheKnownState()
    {
        Receive("\x40\x90\x00");
        await RunWithTimePassing(() => _appleTv.SendIRCode(RemoteButton.Power));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x36' }), Times.Once);

        Receive("\x40\x90\x01");
        await RunWithTimePassing(() => _appleTv.SendIRCode(RemoteButton.Power));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x6D' }), Times.Once);
    }

    [Fact]
    public async Task SetChannel_SendsTheDigits()
    {
        await RunWithTimePassing(() => _appleTv.SetChannel(12));

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x21' }));
        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x22' }));
    }

    [Fact]
    public void SetChannel_WithANegativeChannel_SendsNothing()
    {
        _appleTv.SetChannel(-12);

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Contains(_appleTv.Events, e => e.Type == EventType.Error && e.Info.Contains("-12"));
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
    public async Task ToggleSubtitles_SendsSubPicture()
    {
        await RunWithTimePassing(_appleTv.ToggleSubtitles);

        _mockClient.Verify(x => x.Send(new[] { '\x04', '\x44', '\x51' }));
    }
}
