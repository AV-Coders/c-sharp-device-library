using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public class CecDisplayTest
{
    private static readonly TimeSpan FrameSpacing = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SmallStep = TimeSpan.FromMilliseconds(100);
    private const char SourcePlayBack1 = '\x40';
    private const char DestinationTv = '\x00';
    private const int PollSeconds = 23;
    private readonly FakeTimeProvider _time = new();
    private readonly CecDisplay _display;
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();
    private readonly List<char[]> _sentFrames = [];
    private readonly List<string?> _sentTriggerIds = [];
    private char[] _physicalAddress = ['\x30', '\x00'];
    private const char UnregisteredSource = '\xF0';
    private static readonly char[] PowerStatusPoll = ['\xF0', '\x8F'];
    private static readonly char[] ClaimActiveSourceAtHdmi3 = ['\x4F', '\x82', '\x30', '\x00'];
    private static readonly char[] ClaimActiveSourceAtHdmi1 = ['\x4F', '\x82', '\x10', '\x00'];
    private static readonly char[] StandbyToTv = ['\x40', '\x36'];
    private static readonly RemoteButton[] _excludedButtons =
    [
        RemoteButton.Display, RemoteButton.Eject,
        RemoteButton.PopupMenu, RemoteButton.TopMenu,
        RemoteButton.PowerOn, RemoteButton.PowerOff,
        RemoteButton.Guide, RemoteButton.Home, RemoteButton.Menu
    ];

    public CecDisplayTest()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Callback<char[]>(frame =>
        {
            lock (_sentFrames)
            {
                _sentFrames.Add(frame);
                _sentTriggerIds.Add(Trigger.Id);
            }
        });
        _display = new CecDisplay(_mockClient.Object, "Test display", () => _physicalAddress, SourcePlayBack1, DestinationTv, PollSeconds, _time);
    }

    private Task Until(Func<bool> condition, string failureMessage) =>
        Wait.UntilAsync(_time, FrameSpacing, condition, failureMessage);

    private List<char[]> SentFramesExceptPolls()
    {
        lock (_sentFrames)
            return _sentFrames.Where(frame => frame[0] != UnregisteredSource).ToList();
    }

    private List<string?> SentTriggerIdsExceptPolls()
    {
        lock (_sentFrames)
            return _sentFrames.Zip(_sentTriggerIds)
                .Where(sent => sent.First[0] != UnregisteredSource)
                .Select(sent => sent.Second)
                .ToList();
    }

    private List<char[]> SentFrames()
    {
        lock (_sentFrames)
            return _sentFrames.ToList();
    }

    private Task WaitForFrames(int count) =>
        Until(() => SentFramesExceptPolls().Count >= count, $"Fewer than {count} frames were sent");

    private void SetConnectionState(ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, state);

    private Task InvokeDoPoll() =>
        (Task)typeof(CecDisplay).GetMethod("DoPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_display, [CancellationToken.None])!;

    [Fact]
    public async Task PowerOn_SendsImageViewOnAfterTheKeyRelease()
    {
        _display.PowerOn();
        await WaitForFrames(3);

        Assert.Equal([['\x40', '\x44', '\x6D'], ['\x40', '\x45'], ['\x40', '\x04']], SentFramesExceptPolls());
    }

    [Fact]
    public async Task PowerOff_ClaimsActiveSourceThenSendsStandby()
    {
        _display.PowerOff();
        await WaitForFrames(2);

        Assert.Equal([ClaimActiveSourceAtHdmi3, StandbyToTv], SentFramesExceptPolls());
    }

    [Fact]
    public async Task PowerOff_ReturnsBeforeTheActiveSourceSettles()
    {
        _display.PowerOff();

        Assert.DoesNotContain(StandbyToTv, SentFramesExceptPolls());
        await WaitForFrames(2);
        Assert.Equal([ClaimActiveSourceAtHdmi3, StandbyToTv], SentFramesExceptPolls());
    }

    [Fact]
    public async Task PowerOff_SendsTheFramesUnderTheCallersTriggerId()
    {
        var triggerId = Trigger.Start();

        _display.PowerOff();
        await WaitForFrames(2);

        Assert.Equal([triggerId, triggerId], SentTriggerIdsExceptPolls());
    }

    [Fact]
    public async Task PowerOff_ClaimsActiveSourceAtThePhysicalAddressTheStreamReportsNow()
    {
        _display.PowerOff();
        _physicalAddress = ['\x10', '\x00'];
        _display.PowerOff();
        await WaitForFrames(4);

        Assert.Equal([ClaimActiveSourceAtHdmi3, StandbyToTv, ClaimActiveSourceAtHdmi1, StandbyToTv], SentFramesExceptPolls());
    }

    [Fact]
    public async Task PowerOff_WithAnUnknownPhysicalAddress_SendsStandbyOnlyAndAddsAnErrorEvent()
    {
        _physicalAddress = [];

        _display.PowerOff();
        await WaitForFrames(1);

        Assert.Equal([StandbyToTv], SentFramesExceptPolls());
        Assert.Contains(_display.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task Commands_AreSentInTheOrderTheyWereAsked()
    {
        _display.SetChannel(1);
        _display.PowerOff();
        _display.SetInput(Input.Hdmi2);
        await WaitForFrames(5);

        Assert.Equal(
            [['\x40', '\x44', '\x21'], ['\x40', '\x45'], ClaimActiveSourceAtHdmi3, StandbyToTv, ['\x40', '\x82', '\x20', '\x00']],
            SentFramesExceptPolls());
    }

    [Theory]
    [InlineData('\x00', PowerState.On)]
    [InlineData('\x01', PowerState.Off)]
    [InlineData('\x02', PowerState.Off)]
    public void HandleResponse_PowerStatus_SetsThePowerState(char status, PowerState expected)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"\x0F\x90{status}");

        Assert.Equal(expected, _display.PowerState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\x0F")]
    [InlineData("\x0F\x90")]
    public void HandleResponse_ShortReply_IsIgnored(string reply)
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseHandlers!.Invoke(reply));

        Assert.Null(exception);
        Assert.Equal(PowerState.Unknown, _display.PowerState);
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
    }

    [Theory]
    [InlineData(0, '\x00')]
    [InlineData(50, '\x3F')]
    [InlineData(100, '\x7F')]
    public async Task SetVolume_SendsTheCommand(int percentage, char expectedVolume)
    {
        _display.SetVolume(percentage);
        await WaitForFrames(1);

        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x7a', expectedVolume}));
    }

    [Theory]
    [InlineData(MuteState.On, '\x65')]
    [InlineData(MuteState.Off, '\x66')]
    public async Task SetAudioMute_SendsTheCommand(MuteState input, char expected)
    {
        _display.SetAudioMute(input);
        await WaitForFrames(2);

        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x44', expected}));
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x45'}));
    }

    [Fact]
    public void SetVolume_Unmutes()
    {
        _display.SetAudioMute(MuteState.On);
        _display.SetVolume(30);

        Assert.Equal(MuteState.Off, _display.AudioMute);
    }

    [Fact]
    public async Task SetChannel_SendsTheCommand()
    {
        _display.SetChannel(12);
        await WaitForFrames(4);

        _mockClient.Verify(x => x.Send(new []{'\x40', '\x44', '\x21'}));
        _mockClient.Verify(x => x.Send(new []{'\x40', '\x44', '\x22'}));
    }

    [Fact]
    public void SetChannel_Negative_SendsNothingAndAddsAnErrorEvent()
    {
        _display.SetChannel(-12);

        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
        Assert.Contains(_display.Events, e => e.Type == EventType.Error && e.Info == "Channel -12 can't be sent: CEC has no key for '-'");
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Error)]
    public async Task DoPoll_DoesNothingWhileTheStreamIsDown(ConnectionState state)
    {
        SetConnectionState(state);

        await InvokeDoPoll();
        _display.SetInput(Input.Hdmi1);
        await WaitForFrames(1);

        Assert.DoesNotContain(SentFrames(), frame => frame.SequenceEqual(PowerStatusPoll));
    }

    [Fact]
    public async Task DoPoll_RequestsThePowerStatusWhileConnected()
    {
        SetConnectionState(ConnectionState.Connected);

        await InvokeDoPoll();

        await Until(() => SentFrames().Any(frame => frame.SequenceEqual(PowerStatusPoll)), "The power status was never requested");
    }

    [Fact]
    public async Task DoPoll_WhileTheStreamStateIsUnknown_Polls()
    {
        Assert.Equal(ConnectionState.Unknown, _mockClient.Object.ConnectionState);

        await InvokeDoPoll();

        await Until(() => SentFrames().Any(frame => frame.SequenceEqual(PowerStatusPoll)), "The power status was never requested");
    }

    [Fact]
    public async Task Send_GivenAFailure_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Throws(new IOException("Oh No!"));

        _display.SetInput(Input.Hdmi1);

        await Until(() => _display.GetOngoingIssues().Any(i => i.Key == "communication" && i.Severity == IssueSeverity.Critical), "The failed send was never reported");
        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
    }

    [Theory]
    [InlineData('\x40', '\x00', '\x40', '\x4F')]
    [InlineData('\x80', '\x05', '\x85', '\x8F')]
    [InlineData('\x4F', '\x00', '\x4F', '\x4F')]
    public async Task Headers_CombineTheSourceAndDestinationBits(char sourceId, char destinationId, char commandHeader, char broadcastHeader)
    {
        var display = new CecDisplay(_mockClient.Object, "Header display", () => _physicalAddress, sourceId, destinationId, PollSeconds, _time);

        display.PowerOff();
        await WaitForFrames(2);

        Assert.Equal(broadcastHeader, SentFramesExceptPolls()[0][0]);
        Assert.Equal(commandHeader, SentFramesExceptPolls()[1][0]);
    }

    [Theory]
    [InlineData("\x0F\x82\x00\x00")]
    [InlineData("\x0F\x87\x00\x00")]
    [InlineData("\x0F\x00\x36\x00")]
    public void HandleResponse_AFrameThatIsNotAReplyWeAskedFor_LeavesTheCommunicationState(string frame)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(frame);

        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
    }

    [Fact]
    public async Task HandleResponse_AfterASendFailure_ATvBroadcastLeavesTheError_AndAPowerReplyClearsIt()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Throws(new IOException("Oh No!"));
        _display.SetInput(Input.Hdmi1);
        await Wait.UntilAsync(() => _display.CommunicationState == CommunicationState.Error, "The failed send was never reported");

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x82\x00\x00");

        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x90\x00");
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x90\x00");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public async Task SendIRCode_SendsTheButtonAsAKeyPress()
    {
        _display.SendIRCode(RemoteButton.Enter);
        await WaitForFrames(2);

        Assert.Equal([['\x40', '\x44', '\x2B'], ['\x40', '\x45']], SentFramesExceptPolls());
    }

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b), _display.SupportedButtons.OrderBy(b => b));
    }

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    [Theory]
    [MemberData(nameof(ExcludedButtonValues))]
    public void SendIRCode_ExcludedButtonsAreNotSupportedAndSendNothing(RemoteButton button)
    {
        _mockClient.Invocations.Clear();
        _display.SendIRCode(button);

        Assert.DoesNotContain(button, _display.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
    }

    private static readonly char[][] DiscoveryRequests =
        [['\xF0', '\x83'], ['\xF0', '\x46'], ['\xF0', '\x8C'], ['\xF0', '\x9F']];

    private bool WasSent(char[] frame) => SentFrames().Any(sent => sent.SequenceEqual(frame));

    private int TimesSent(char[] frame) => SentFrames().Count(sent => sent.SequenceEqual(frame));

    [Fact]
    public async Task DoPoll_AsksForEveryIdentityValue()
    {
        await InvokeDoPoll();

        await Until(() => WasSent(DiscoveryRequests[^1]), "Get CEC Version was never sent");
        Assert.All(DiscoveryRequests, request => Assert.True(WasSent(request)));
    }

    [Fact]
    public async Task DoPoll_AsksOnlyForTheIdentityValuesNotYetAnswered()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x47TV");

        await InvokeDoPoll();

        await Until(() => WasSent(DiscoveryRequests[^1]), "Get CEC Version was never sent");
        Assert.False(WasSent(DiscoveryRequests[0]));
        Assert.False(WasSent(DiscoveryRequests[1]));
        Assert.True(WasSent(DiscoveryRequests[2]));
    }

    [Fact]
    public async Task DoPoll_OnceEveryIdentityValueIsAnswered_OnlyPollsThePower()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x47TV");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x87\x00\x00\xF0");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x9E\x05");

        await InvokeDoPoll();

        await Until(() => WasSent(PowerStatusPoll), "The power status was never requested");
        Assert.DoesNotContain(SentFrames(), frame => DiscoveryRequests.Any(request => frame.SequenceEqual(request)));
    }

    [Fact]
    public async Task DoPoll_GivenNoAnswer_RaisesAnIssueAfterThreeMisses_AsksAgainEveryTenthPoll_AndAReplyResolvesIt()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x47TV");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x9E\x05");

        for (var poll = 0; poll < 3; poll++)
            await InvokeDoPoll();
        Assert.Empty(_display.GetOngoingIssues());

        await InvokeDoPoll();
        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("vendor-read", issue.Key);
        Assert.Equal("The TV did not give a real reply to Give Device Vendor ID 3 times in a row, the last was no reply", issue.Message);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);

        for (var poll = 4; poll < 11; poll++)
            await InvokeDoPoll();
        await Until(() => TimesSent(DiscoveryRequests[2]) == 4, "Give Device Vendor ID was not asked again on the eleventh poll");
        Assert.Equal(0, TimesSent(DiscoveryRequests[0]));

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x87\x00\x00\xF0");
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task DoPoll_GivenNoAnswerToTheRequestsWithADirectedReply_ShowsNoAnswerWithTheRawFrame_NeverRaisesAnIssue_AndAReplyReplacesIt()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x87\x00\x00\xF0");

        for (var poll = 0; poll < 3; poll++)
        {
            await InvokeDoPoll();
            _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x9E\x05\x00");
        }
        Assert.DoesNotContain(_display.Details, d => d.Label is "OSD name" or "CEC version");

        await InvokeDoPoll();

        Assert.Empty(_display.GetIssues());
        Assert.Contains(new DeviceDetail("OSD name", "No answer (the TV may ignore requests from address 15): no reply"), _display.Details);
        Assert.Contains(new DeviceDetail("CEC version", "No answer (the TV may ignore requests from address 15): 0F:9E:05:00"), _display.Details);
        await Until(() => TimesSent(DiscoveryRequests[1]) == 3 && TimesSent(DiscoveryRequests[3]) == 3, "Give OSD Name and Get CEC Version were not asked on each of the first three polls");

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x47TV");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x9E\x05");
        Assert.Contains(new DeviceDetail("OSD name", "TV"), _display.Details);
        Assert.Contains(new DeviceDetail("CEC version", "1.4"), _display.Details);
        Assert.Empty(_display.GetIssues());
    }

    [Fact]
    public async Task Polling_WhileNoCommandIsWaiting_SendsEveryIdentityRequestInTurnAfterThePowerPoll()
    {
        await Until(() => WasSent(DiscoveryRequests[^1]), "Get CEC Version was never sent");

        Assert.Equal([PowerStatusPoll, ..DiscoveryRequests], SentFrames());
    }

    private Task UntilTheFirstIdentityRequest() =>
        Wait.UntilAsync(_time, SmallStep, () => WasSent(DiscoveryRequests[0]), "Give Physical Address was never sent");

    [Fact]
    public async Task Polling_WaitsTheReplySpacingAfterThePowerPollBeforeTheFirstIdentityRequest()
    {
        await Wait.UntilAsync(_time, SmallStep, () => WasSent(PowerStatusPoll), "The power status was never requested");
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.Equal([PowerStatusPoll], SentFrames());

        await UntilTheFirstIdentityRequest();

        Assert.Equal([PowerStatusPoll, DiscoveryRequests[0]], SentFrames());
    }

    [Fact]
    public async Task Reconnecting_DropsTheIdentityRequestsStillWaitingAndAsksAgainFromTheStart()
    {
        SetConnectionState(ConnectionState.Connected);
        await UntilTheFirstIdentityRequest();
        var sentBeforeReconnecting = SentFrames().Count;

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        await Until(() => SentFrames().Skip(sentBeforeReconnecting).Any(frame => frame.SequenceEqual(DiscoveryRequests[^1])),
            "Get CEC Version was never sent after reconnecting");
        Assert.Equal([PowerStatusPoll, ..DiscoveryRequests], SentFrames().Skip(sentBeforeReconnecting));
    }

    private async Task AssertTheCommandGoesOutBeforeTheDueIdentityRequests(Action command, char[][] commandFrames)
    {
        await UntilTheFirstIdentityRequest();

        command();

        await Wait.UntilAsync(() => WasSent(commandFrames[0]), "The command waited behind the identity requests");
        Assert.Equal([PowerStatusPoll, DiscoveryRequests[0], commandFrames[0]], SentFrames());
        await Until(() => WasSent(DiscoveryRequests[^1]), "Get CEC Version was never sent");
        var sent = SentFrames();
        Assert.Equal(commandFrames, SentFramesExceptPolls());
        Assert.True(sent.FindIndex(frame => frame.SequenceEqual(commandFrames[^1])) < sent.FindIndex(frame => frame.SequenceEqual(DiscoveryRequests[1])),
            "An identity request went out before the command finished");
    }

    [Fact]
    public Task PowerOn_AskedWhileIdentityRequestsAreDue_GoesOutBeforeThem() =>
        AssertTheCommandGoesOutBeforeTheDueIdentityRequests(_display.PowerOn, [['\x40', '\x44', '\x6D'], ['\x40', '\x45'], ['\x40', '\x04']]);

    [Fact]
    public Task PowerOff_AskedWhileIdentityRequestsAreDue_GoesOutBeforeThem() =>
        AssertTheCommandGoesOutBeforeTheDueIdentityRequests(_display.PowerOff, [ClaimActiveSourceAtHdmi3, StandbyToTv]);

    [Fact]
    public Task SetInput_AskedWhileIdentityRequestsAreDue_GoesOutBeforeThem() =>
        AssertTheCommandGoesOutBeforeTheDueIdentityRequests(() => _display.SetInput(Input.Hdmi2), [['\x40', '\x82', '\x20', '\x00']]);

    [Fact]
    public Task SendIRCode_AskedWhileIdentityRequestsAreDue_GoesOutBeforeThem() =>
        AssertTheCommandGoesOutBeforeTheDueIdentityRequests(() => _display.SendIRCode(RemoteButton.Enter), [['\x40', '\x44', '\x2B'], ['\x40', '\x45']]);

    [Fact]
    public async Task Connecting_AsksForTheIdentityAgain()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        SetConnectionState(ConnectionState.Connected);

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        await Until(() => WasSent(DiscoveryRequests[0]), "Give Physical Address was never sent");
    }

    [Theory]
    [InlineData("\x0F\x47TV", "OSD name", "TV")]
    [InlineData("\x0F\x84\x00\x00\x00", "Physical address", "0.0.0.0")]
    [InlineData("\x0F\x87\x00\x00\xF0", "Vendor", "Samsung (00:00:F0)")]
    [InlineData("\x0F\x87\x12\x34\x56", "Vendor", "12:34:56")]
    [InlineData("\x0F\x9E\x05", "CEC version", "1.4")]
    [InlineData("\x0F\x9E\x09", "CEC version", "0x09")]
    public void HandleResponse_ShowsTheTvsIdentity(string reply, string label, string expected)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(reply);

        Assert.Contains(_display.Details, d => d.Label == label && d.Value == expected);
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Theory]
    [InlineData("\x0F\x87\x00\x00", '\x8C', "vendor-read", "0F:87:00:00")]
    [InlineData("\x0F\x84\x10\x00", '\x83', "physical-address-read", "0F:84:10:00")]
    public async Task HandleResponse_GivenAMalformedIdentityFrameOnEveryPoll_AsksAgain_AndRaisesTheReadIssueWithTheRawFrameOnTheThirdMiss(
        string reply, char request, string issueKey, string raw)
    {
        for (var poll = 0; poll < 3; poll++)
        {
            await InvokeDoPoll();
            _mockClient.Object.ResponseHandlers!.Invoke(reply);
        }
        Assert.Empty(_display.GetOngoingIssues());

        await InvokeDoPoll();

        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == issueKey);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.EndsWith(raw, issue.Message);
        await Until(() => TimesSent([UnregisteredSource, request]) == 3, "The request was not asked on each of the first three polls");
    }

    [Theory]
    [InlineData('\x46', "OSD name")]
    [InlineData('\x83', "Physical address")]
    [InlineData('\x8C', "Vendor")]
    [InlineData('\x9F', "CEC version")]
    public void HandleResponse_GivenAFeatureAbortForAnIdentityRequest_ShowsItIsNotSupportedWithNoIssue(char opcode, string label)
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x90\x01");

        _mockClient.Object.ResponseHandlers!.Invoke($"\x0F\x00{opcode}\x00");

        Assert.Contains(new DeviceDetail(label, $"Not supported: 0F:00:{(int)opcode:X2}:00", DetailTone.Normal), _display.Details);
        Assert.Empty(_display.GetIssues());
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Theory]
    [InlineData('\x00')]
    [InlineData('\x02')]
    [InlineData('\x03')]
    public async Task DoPoll_AfterAFeatureAbortThatMeansNotSupported_ShowsItAndStopsAskingWithNoIssue(char reason)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"\x0F\x00\x83{reason}");

        await InvokeDoPoll();

        await Until(() => WasSent(DiscoveryRequests[^1]), "Get CEC Version was never sent");
        Assert.False(WasSent(DiscoveryRequests[0]));
        Assert.Contains(new DeviceDetail("Physical address", $"Not supported: 0F:00:83:{(int)reason:X2}", DetailTone.Normal), _display.Details);
        Assert.Empty(_display.GetIssues());
    }

    [Theory]
    [InlineData('\x01', "not in the correct mode to respond")]
    [InlineData('\x04', "refused")]
    [InlineData('\x05', "unable to determine")]
    [InlineData('\x06', "reason 0x06")]
    public async Task HandleResponse_GivenAFeatureAbortForAPassingReasonOnEveryPoll_AsksAgain_AndRaisesTheReadIssueOnTheThirdMiss(char reason, string reasonText)
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x84\x00\x00\x00");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x47TV");
        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x9E\x05");
        for (var poll = 0; poll < 3; poll++)
        {
            await InvokeDoPoll();
            _mockClient.Object.ResponseHandlers!.Invoke($"\x0F\x00\x8C{reason}");
        }
        Assert.Empty(_display.GetOngoingIssues());
        Assert.DoesNotContain(_display.Details, d => d.Label == "Vendor");

        await InvokeDoPoll();

        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("vendor-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal($"The TV did not give a real reply to Give Device Vendor ID 3 times in a row, the last was 0F:00:8C:{(int)reason:X2} ({reasonText})", issue.Message);
        await Until(() => TimesSent(DiscoveryRequests[2]) == 3, "Give Device Vendor ID was not asked on each of the first three polls");

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x87\x00\x00\xF0");
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public void HandleResponse_IgnoresAnotherDevicesReport()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("\x4F\x84\x10\x00\x04");

        Assert.Empty(_display.Details);
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
    }
}
