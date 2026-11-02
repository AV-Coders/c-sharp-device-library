using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public abstract class LgPollBench
{
    protected static readonly TimeSpan PollStep = TimeSpan.FromSeconds(12);
    protected const string NotReadYet = "Not read yet: read while the display is off";
    protected const string PowerPoll = "ka 00 FF\r";
    protected const string InputPoll = "xb 00 FF\r";
    protected const string VolumePoll = "kf 00 FF\r";
    protected const string MutePoll = "ke 00 FF\r";
    protected const string SerialQuery = "fy 00 FF\r";
    protected const string SoftwareVersionQuery = "fz 00 FF\r";
    protected const string TemperatureQuery = "dn 00 FF\r";
    protected const string SignalQuery = "sv 00 02 FF\r";
    protected static readonly string[] ReadLabels = ["Serial Number", "Software Version", "Temperature", "Signal"];
    protected readonly FakeTimeProvider _time = new();
    protected readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly List<string> _sent = [];
    protected readonly ConcurrentDictionary<string, string> _replies = new();
    protected string _powerReply = "a 00 OK00x";
    protected readonly LGCommercial _display;

    protected LgPollBench()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(command =>
        {
            lock (_sent)
                _sent.Add(command);
            if (command == PowerPoll)
                _mockClient.Object.ResponseHandlers!.Invoke(_powerReply);
            else if (_replies.TryGetValue(command, out var reply))
                _mockClient.Object.ResponseHandlers!.Invoke(reply);
        });
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, ConnectionState.Connected);
        _display = new LGCommercial(_mockClient.Object, "Test display", null, null, 0, _time);
    }

    protected List<string> Sent()
    {
        lock (_sent)
            return _sent.ToList();
    }

    protected int TimesSent(string command) => Sent().Count(sent => sent == command);

    protected Task UntilPowerPolled(int times) =>
        Wait.UntilAsync(_time, PollStep, () => TimesSent(PowerPoll) >= times, $"The power poll was sent fewer than {times} times");

    protected void AnswerEveryRead()
    {
        _replies[SerialQuery] = "y 00 OK503KCPV12345x";
        _replies[SoftwareVersionQuery] = "z 00 OK03.21.10x";
        _replies[TemperatureQuery] = "n 00 OK2Ax";
        _replies[SignalQuery] = "v 00 OK0201x";
    }

    protected async Task PollOnceAnswering(string query, string reply)
    {
        AnswerEveryRead();
        _replies[query] = reply;
        await UntilPowerPolled(2);
    }
}

public class LgCommercialDetailsTest : LgPollBench
{
    [Fact]
    public async Task Poll_WhileOff_SendsTheReadsAfterTheExistingPolls()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        Assert.Equal([PowerPoll, InputPoll, VolumePoll, MutePoll, SerialQuery, SoftwareVersionQuery, TemperatureQuery, SignalQuery, PowerPoll],
            Sent().Take(9));
    }

    [Fact]
    public async Task Poll_WhileOn_SendsOnlyTheExistingPolls()
    {
        _powerReply = "a 00 OK01x";

        await UntilPowerPolled(3);

        Assert.Equal([PowerPoll, InputPoll, VolumePoll, MutePoll, PowerPoll, InputPoll, VolumePoll, MutePoll, PowerPoll], Sent().Take(9));
    }

    [Fact]
    public void Constructor_ShowsEveryReadAsNotReadYet()
    {
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _display.Details);
    }

    [Fact]
    public async Task Poll_WhileOn_LeavesTheReadsShowingNotReadYet()
    {
        _powerReply = "a 00 OK01x";

        await UntilPowerPolled(2);

        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _display.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_ReplacesNotReadYetWithTheValuesRead()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        Assert.Equal(
        [
            new DeviceDetail("Serial Number", "503KCPV12345"),
            new DeviceDetail("Software Version", "03.21.10"),
            new DeviceDetail("Temperature", "42 °C"),
            new DeviceDetail("Signal", "Present"),
        ], _display.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_AfterAMissedReply_AsksAgainOnlyForTheUnansweredIdentityRead()
    {
        _replies[SerialQuery] = "y 00 OK503KCPV12345x";

        await UntilPowerPolled(3);

        Assert.Equal(1, TimesSent(SerialQuery));
        Assert.Equal(2, TimesSent(SoftwareVersionQuery));
        Assert.Equal(1, TimesSent(TemperatureQuery));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenNoReplyThreeTimesInARow_RaisesTheReadIssue_KeepsAsking_AndAReplyResolvesIt()
    {
        AnswerEveryRead();
        _replies.TryRemove(SoftwareVersionQuery, out _);

        await UntilPowerPolled(3);
        Assert.Empty(_display.GetOngoingIssues());

        await UntilPowerPolled(4);
        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == "software-version-read");
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The display did not give a real reply to the software version query fz 00 FF 3 times in a row, the last was no reply", issue.Message);

        _replies[SoftwareVersionQuery] = "z 00 OK03.21.10x";
        await Wait.UntilAsync(_time, PollStep, () => _display.GetOngoingIssues().Count == 0, "The software version reply never resolved the read issue");

        Assert.True(TimesSent(SoftwareVersionQuery) >= 4);
        Assert.Contains(new DeviceDetail("Software Version", "03.21.10"), _display.Details);
    }

    [Fact]
    public async Task Poll_GivenAnotherControllersReplies_KeepsTheirPowerState_ButCountsThemNeitherForNorAgainstTheReads()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("a 00 OK01x");
        _mockClient.Object.ResponseHandlers!.Invoke("y 00 OK503KCPV12345x");
        _mockClient.Object.ResponseHandlers!.Invoke("n 00 NG01x");

        Assert.Equal(PowerState.On, _display.PowerState);
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _display.Details);

        AnswerEveryRead();
        await UntilPowerPolled(2);

        Assert.Equal(1, TimesSent(SerialQuery));
        Assert.Equal(1, TimesSent(TemperatureQuery));
        Assert.Contains(new DeviceDetail("Temperature", "42 °C"), _display.Details);
    }

    [Fact]
    public async Task Reconnect_RestartsThePollWorker_SoTheNextPollGoesOutAtOnceAndNeverAlongsideAnother()
    {
        await Wait.UntilAsync(_time, PollStep, () => TimesSent(PowerPoll) == 1, "The first power poll was never sent");

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await Wait.UntilAsync(() => TimesSent(PowerPoll) == 2, "The power poll did not go out straight after the reconnect");
        await Wait.UntilAsync(_time, PollStep, () => TimesSent(MutePoll) >= 1, "The mute poll was never sent");

        Assert.Equal([PowerPoll, PowerPoll, InputPoll, VolumePoll, MutePoll], Sent().Take(5));
    }
}

public class LgCommercialReadRepliesTest : LgPollBench
{
    [Fact]
    public async Task Poll_WhileOff_ShowsTheSerialNumber()
    {
        await PollOnceAnswering(SerialQuery, "y 00 OK503KCPV12345x");

        Assert.Contains(_display.Details, d => d.Label == "Serial Number" && d.Value == "503KCPV12345");
    }

    [Fact]
    public async Task Poll_WhileOff_ShowsTheSoftwareVersion()
    {
        await PollOnceAnswering(SoftwareVersionQuery, "z 00 OK03.21.10x");

        Assert.Contains(_display.Details, d => d.Label == "Software Version" && d.Value == "03.21.10");
    }

    [Fact]
    public async Task Poll_WhileOff_ShowsTheTemperatureFromHex()
    {
        await PollOnceAnswering(TemperatureQuery, "n 00 OK2Ax");

        Assert.Contains(_display.Details, d => d.Label == "Temperature" && d.Value == "42 °C");
    }

    [Theory]
    [InlineData("v 00 OK0201x", "Present")]
    [InlineData("v 00 OK02 01x", "Present")]
    [InlineData("v 00 OK0200x", "No signal")]
    [InlineData("v 00 OK0207x", "0207")]
    [InlineData("v 00 OK02 07x", "02 07")]
    public async Task Poll_WhileOff_ShowsTheSignalStatus(string reply, string expected)
    {
        await PollOnceAnswering(SignalQuery, reply);

        Assert.Contains(_display.Details, d => d.Label == "Signal" && d.Value == expected);
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Theory]
    [InlineData(SerialQuery, "y 00 OKx", "Serial Number")]
    [InlineData(SoftwareVersionQuery, "z 00 OKx", "Software Version")]
    public async Task Poll_WhileOff_GivenAnEmptyIdentityValue_ShowsTheRawReplyWithNoIssue(string query, string reply, string label)
    {
        await PollOnceAnswering(query, reply);

        Assert.Contains(new DeviceDetail(label, reply), _display.Details);
        Assert.Empty(_display.GetIssues());
    }

    [Theory]
    [InlineData(SerialQuery, "y 00 NG01x", "Serial Number")]
    [InlineData(SoftwareVersionQuery, "z 00 NG01x", "Software Version")]
    [InlineData(TemperatureQuery, "n 00 NG01x", "Temperature")]
    [InlineData(SignalQuery, "v 00 NG01x", "Signal")]
    public async Task Poll_WhileOff_GivenARejectedRead_ShowsItIsNotSupportedWithNoIssue(string query, string reply, string label)
    {
        await PollOnceAnswering(query, reply);

        Assert.Contains(new DeviceDetail(label, $"Not supported: {reply}", DetailTone.Normal), _display.Details);
        Assert.Empty(_display.GetIssues());
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }
}

public class LgCommercialHealthReadTest : LgPollBench
{
    [Theory]
    [InlineData("n 00 OKzzx", "temperature-read", TemperatureQuery, "Temperature")]
    [InlineData("v 00 OKzzx", "signal-read", SignalQuery, "Signal")]
    public async Task Poll_WhileOff_GivenThreeUnreadableRepliesInARow_RaisesTheReadIssueWithTheRawReply_AndKeepsAsking(string reply, string issueKey, string query, string label)
    {
        AnswerEveryRead();
        _replies[query] = reply;

        await UntilPowerPolled(11);
        Assert.Empty(_display.GetOngoingIssues());

        await UntilPowerPolled(12);
        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == issueKey);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.EndsWith(reply, issue.Message);
        Assert.Contains(new DeviceDetail(label, NotReadYet), _display.Details);
        Assert.Equal(3, TimesSent(query));
    }

    [Fact]
    public async Task Poll_WhileOff_GivenAReadableReplyAfterAnUnreadableOne_ResolvesTheMiss()
    {
        await PollOnceAnswering(TemperatureQuery, "n 00 OKzzx");

        _replies[TemperatureQuery] = "n 00 OK28x";
        await UntilPowerPolled(7);

        Assert.Contains(new DeviceDetail("Temperature", "40 °C"), _display.Details);
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "temperature-read");
    }

    [Fact]
    public async Task Poll_WhileOff_AfterTheTemperatureQueryIsRejected_StopsAskingUntilTheDisplayReconnects()
    {
        AnswerEveryRead();
        _replies[TemperatureQuery] = "n 00 NG01x";

        await UntilPowerPolled(7);
        Assert.Equal(1, TimesSent(TemperatureQuery));
        Assert.Equal(2, TimesSent(SignalQuery));

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        await Wait.UntilAsync(_time, PollStep, () => TimesSent(TemperatureQuery) > 1, "The temperature was not asked for again after the display reconnected");
    }
}
