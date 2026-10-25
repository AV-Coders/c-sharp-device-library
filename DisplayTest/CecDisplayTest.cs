using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;
using Moq;

namespace AVCoders.Display.Tests;

public class CecDisplayTest
{
    private readonly CecDisplay _display;
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();
    private readonly List<char[]> _sentFrames = [];
    private readonly List<string?> _sentTriggerIds = [];
    private char[] _physicalAddress = ['\x30', '\x00'];
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
    public static IEnumerable<object[]> RemoteButtonValues()
    {
        return Enum.GetValues(typeof(RemoteButton))
            .Cast<RemoteButton>()
            .Where(rb => !_excludedButtons.Contains(rb))
            .Select(rb => new object[] { rb });
    }

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
        _display = new CecDisplay(_mockClient.Object, "Test display", () => _physicalAddress);
    }

    private List<char[]> SentFramesExceptPolls()
    {
        lock (_sentFrames)
            return _sentFrames.Where(frame => !frame.SequenceEqual(PowerStatusPoll)).ToList();
    }

    private List<string?> SentTriggerIdsExceptPolls()
    {
        lock (_sentFrames)
            return _sentFrames.Zip(_sentTriggerIds)
                .Where(sent => !sent.First.SequenceEqual(PowerStatusPoll))
                .Select(sent => sent.Second)
                .ToList();
    }

    private List<char[]> SentFrames()
    {
        lock (_sentFrames)
            return _sentFrames.ToList();
    }

    private Task WaitForFrames(int count) =>
        Wait.UntilAsync(() => SentFramesExceptPolls().Count >= count, $"Fewer than {count} frames were sent");

    private void SetConnectionState(ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, state);

    private Task InvokeDoPoll() =>
        (Task)typeof(CecDisplay).GetMethod("DoPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_display, [CancellationToken.None])!;

    [Fact]
    public async Task PowerOn_SendsTheUserControlPressedAndReleasedCommands()
    {
        _display.PowerOn();
        await WaitForFrames(3);

        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x44', '\x6D'}));
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x45'}));
    }

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
    public async Task PowerOff_ClaimsActiveSourceBeforeEveryStandby()
    {
        _display.PowerOff();
        _display.PowerOff();
        await WaitForFrames(4);

        Assert.Equal([ClaimActiveSourceAtHdmi3, StandbyToTv, ClaimActiveSourceAtHdmi3, StandbyToTv], SentFramesExceptPolls());
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
    public void PowerOff_SetsThePowerStateToOff()
    {
        _display.PowerOff();

        Assert.Equal(PowerState.Off, _display.PowerState);
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

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public async Task SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _mockClient.Invocations.Clear();

        _display.SendIRCode(button);
        await WaitForFrames(2);

        Assert.Contains(button, _display.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.AtLeastOnce);
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

    [Fact]
    public async Task DoPoll_DoesNothingWhileDisconnected()
    {
        SetConnectionState(ConnectionState.Disconnected);

        await InvokeDoPoll();
        _display.SetInput(Input.Hdmi1);
        await WaitForFrames(1);

        Assert.DoesNotContain(SentFrames(), frame => frame.SequenceEqual(PowerStatusPoll));
    }

    [Theory]
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

        await Wait.UntilAsync(() => SentFrames().Any(frame => frame.SequenceEqual(PowerStatusPoll)), "The power status was never requested");
    }

    [Fact]
    public async Task DoPoll_WhileTheStreamStateIsUnknown_Polls()
    {
        Assert.Equal(ConnectionState.Unknown, _mockClient.Object.ConnectionState);

        await InvokeDoPoll();

        await Wait.UntilAsync(() => SentFrames().Any(frame => frame.SequenceEqual(PowerStatusPoll)), "The power status was never requested");
    }

    [Fact]
    public async Task Send_GivenAFailure_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<char[]>())).Throws(new IOException("Oh No!"));

        _display.SetInput(Input.Hdmi1);

        await Wait.UntilAsync(() => _display.GetOngoingIssues().Any(i => i.Key == "communication" && i.Severity == IssueSeverity.Critical), "The failed send was never reported");
        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
    }

    [Theory]
    [InlineData('\x40', '\x00', '\x40', '\x4F')]
    [InlineData('\x80', '\x05', '\x85', '\x8F')]
    [InlineData('\x4F', '\x00', '\x4F', '\x4F')]
    public async Task Headers_CombineTheSourceAndDestinationBits(char sourceId, char destinationId, char commandHeader, char broadcastHeader)
    {
        var display = new CecDisplay(_mockClient.Object, "Header display", () => _physicalAddress, sourceId, destinationId);

        display.PowerOff();
        await WaitForFrames(2);

        Assert.Equal(broadcastHeader, SentFramesExceptPolls()[0][0]);
        Assert.Equal(commandHeader, SentFramesExceptPolls()[1][0]);
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x90\x00");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void SupportedButtons_MatchTheRemoteMap()
    {
        Assert.Contains(RemoteButton.Enter, _display.SupportedButtons);
        Assert.Contains(RemoteButton.Red, _display.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Guide, _display.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Home, _display.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Menu, _display.SupportedButtons);
    }

    [Fact]
    public void SendIRCode_UnsupportedButtonSendsNothing()
    {
        _display.SendIRCode(RemoteButton.Home);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
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
}
