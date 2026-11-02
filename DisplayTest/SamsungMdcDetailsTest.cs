using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public abstract class SamsungPollBench
{
    protected static readonly TimeSpan PollStep = TimeSpan.FromSeconds(23);
    protected const byte PowerCommand = 0x11;
    protected const string NotReadYet = "Not read yet: read while the display is off";
    protected static readonly byte[] CapturedPowerOffReply = [0xAA, 0xFF, 0x00, 0x03, 0x41, 0x11, 0x00, 0x54];
    protected static readonly byte[] PowerOnReply = [0xAA, 0xFF, 0x00, 0x03, 0x41, 0x11, 0x01, 0x55];
    protected static readonly byte[] PowerPoll = [0xAA, 0x11, 0x00, 0x00, 0x11];
    protected static readonly byte[] InputPoll = [0xAA, 0x14, 0x00, 0x00, 0x14];
    protected static readonly byte[] VolumePoll = [0xAA, 0x12, 0x00, 0x00, 0x12];
    protected static readonly byte[] MutePoll = [0xAA, 0x13, 0x00, 0x00, 0x13];
    protected static readonly string[] ReadLabels =
        ["Serial Number", "Software Version", "Model", "Lamp", "Temperature", "Brightness Sensor", "Signal", "Fan", "Panel On Time"];
    protected readonly FakeTimeProvider _time = new();
    protected readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly List<byte[]> _sent = [];
    protected readonly ConcurrentDictionary<byte, byte[]> _replies = new();
    protected byte[] _powerReply = CapturedPowerOffReply;
    protected readonly SamsungMdc _samsungMdc;

    protected SamsungPollBench()
    {
        _samsungMdc = new SamsungMdc(_mockClient.Object, 0x00, "Test display", Input.Hdmi2, _time);
        _mockClient.Setup(x => x.Send(It.IsAny<byte[]>())).Callback<byte[]>(frame =>
        {
            lock (_sent)
                _sent.Add(frame);
            if (frame[1] == PowerCommand)
                _samsungMdc.HandleResponse(_powerReply);
            else if (_replies.TryGetValue(frame[1], out var reply))
                _samsungMdc.HandleResponse(reply);
        });
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, ConnectionState.Connected);
    }

    protected static byte[] Reply(byte command, params byte[] data)
    {
        byte[] body = [0xFF, 0x00, (byte)(data.Length + 2), (byte)'A', command, ..data];
        return [0xAA, ..body, (byte)body.Sum(b => (int)b)];
    }

    protected static byte[] Nak(byte command)
    {
        byte[] body = [0xFF, 0x00, 0x03, (byte)'N', command, 0x01];
        return [0xAA, ..body, (byte)body.Sum(b => (int)b)];
    }

    protected static byte[] Query(byte command) => [0xAA, command, 0x00, 0x00, command];

    protected List<byte[]> Sent()
    {
        lock (_sent)
            return _sent.ToList();
    }

    protected int TimesSent(byte[] frame) => Sent().Count(sent => sent.SequenceEqual(frame));

    protected Task UntilPowerPolled(int times) =>
        Wait.UntilAsync(_time, PollStep, () => TimesSent(PowerPoll) >= times, $"The power poll was sent fewer than {times} times");

    protected void AnswerEveryRead()
    {
        _replies[0x0B] = Reply(0x0B, Encoding.ASCII.GetBytes("0B2YH4ZK100123"));
        _replies[0x0E] = Reply(0x0E, Encoding.ASCII.GetBytes("S-HKMLAKUC-1100.1"));
        _replies[0x8A] = Reply(0x8A, Encoding.ASCII.GetBytes("QM55B"));
        _replies[0x0D] = Reply(0x0D, 0x00, 0x00, 0x02, 0x00, 0x2A, 0x02);
        _replies[0x83] = Reply(0x83, 0x04, 0xB0);
    }

    protected async Task PollOnceAnswering(byte command, byte[] reply)
    {
        AnswerEveryRead();
        _replies[command] = reply;
        await UntilPowerPolled(2);
    }
}

public class SamsungMdcDetailsTest : SamsungPollBench
{
    [Fact]
    public async Task Poll_WhileOff_SendsTheReadsAfterThePowerPoll_AndNoInputVolumeOrMutePoll()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        Assert.Equal([PowerPoll, Query(0x0B), Query(0x0E), Query(0x8A), Query(0x0D), Query(0x83), PowerPoll], Sent().Take(7));
    }

    [Fact]
    public async Task Poll_WhileOn_SendsOnlyTheExistingPolls()
    {
        _powerReply = PowerOnReply;

        await UntilPowerPolled(3);

        Assert.Equal([PowerPoll, InputPoll, VolumePoll, MutePoll, PowerPoll, InputPoll, VolumePoll, MutePoll, PowerPoll], Sent().Take(9));
    }

    [Fact]
    public void Constructor_ShowsEveryReadAsNotReadYet()
    {
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _samsungMdc.Details);
    }

    [Fact]
    public async Task Poll_WhileOn_LeavesTheReadsShowingNotReadYet()
    {
        _powerReply = PowerOnReply;

        await UntilPowerPolled(2);

        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _samsungMdc.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_ReplacesNotReadYetWithTheValuesRead()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        var details = _samsungMdc.Details.ToDictionary(d => d.Label, d => d.Value);
        Assert.Equal("0B2YH4ZK100123", details["Serial Number"]);
        Assert.Equal("S-HKMLAKUC-1100.1", details["Software Version"]);
        Assert.Equal("QM55B", details["Model"]);
        Assert.Equal("42 °C", details["Temperature"]);
        Assert.Equal("200 h", details["Panel On Time"]);
        Assert.DoesNotContain(NotReadYet, details.Values);
    }

    [Fact]
    public async Task Poll_WhileOff_AfterAMissedReply_AsksAgainOnlyForTheUnansweredIdentityRead()
    {
        _replies[0x0B] = Reply(0x0B, Encoding.ASCII.GetBytes("0B2YH4ZK100123"));
        _replies[0x8A] = Reply(0x8A, Encoding.ASCII.GetBytes("QM55B"));

        await UntilPowerPolled(3);

        Assert.Equal([PowerPoll, Query(0x0B), Query(0x0E), Query(0x8A), Query(0x0D), Query(0x83), PowerPoll, Query(0x0E), PowerPoll],
            Sent().Take(9));
        Assert.Empty(_samsungMdc.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenNoReplyThreeTimesInARow_RaisesTheReadIssue_KeepsAsking_AndAReplyResolvesIt()
    {
        AnswerEveryRead();
        _replies.TryRemove(0x0E, out _);

        await UntilPowerPolled(3);
        Assert.Empty(_samsungMdc.GetOngoingIssues());

        await UntilPowerPolled(4);
        var issue = Assert.Single(_samsungMdc.GetOngoingIssues(), i => i.Key == "software-version-read");
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The display did not give a real reply to the software version query 0x0E 3 times in a row, the last was no reply", issue.Message);

        _replies[0x0E] = Reply(0x0E, Encoding.ASCII.GetBytes("S-HKMLAKUC-1100.1"));
        await Wait.UntilAsync(_time, PollStep, () => _samsungMdc.GetOngoingIssues().Count == 0, "The software version reply never resolved the read issue");

        Assert.True(TimesSent(Query(0x0E)) >= 4);
        Assert.Contains(new DeviceDetail("Software Version", "S-HKMLAKUC-1100.1"), _samsungMdc.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_AfterAnIdentityReadIsRefused_StopsAskingForIt()
    {
        AnswerEveryRead();
        _replies[0x8A] = Nak(0x8A);

        await UntilPowerPolled(3);

        Assert.Equal(1, TimesSent(Query(0x8A)));
        Assert.Contains(new DeviceDetail("Model", $"Not supported: {Convert.ToHexString(Nak(0x8A))}"), _samsungMdc.Details);
        Assert.Empty(_samsungMdc.GetIssues());
    }

    [Fact]
    public void HandleResponse_GivenANakForTheVolume_StillReportsACommunicationError()
    {
        _samsungMdc.HandleResponse(Nak(0x12));

        Assert.Equal(CommunicationState.Error, _samsungMdc.CommunicationState);
    }

    [Fact]
    public async Task Poll_GivenAnotherControllersReplies_KeepsTheirPowerState_ButCountsThemNeitherForNorAgainstTheReads()
    {
        _samsungMdc.HandleResponse(PowerOnReply);
        _samsungMdc.HandleResponse(Reply(0x0B, Encoding.ASCII.GetBytes("0B2YH4ZK100123")));
        _samsungMdc.HandleResponse(Nak(0x0D));

        Assert.Equal(PowerState.On, _samsungMdc.PowerState);
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _samsungMdc.Details);

        AnswerEveryRead();
        await UntilPowerPolled(3);

        Assert.Equal(1, TimesSent(Query(0x0B)));
        Assert.Equal(1, TimesSent(Query(0x0D)));
        Assert.Equal("42 °C", _samsungMdc.Details.Single(d => d.Label == "Temperature").Value);
    }
}

public class SamsungMdcReadRepliesTest : SamsungPollBench
{
    [Fact]
    public async Task Poll_WhileOff_ShowsTheIdentityText()
    {
        await PollOnceAnswering(0x0B, Reply(0x0B, [..Encoding.ASCII.GetBytes("QM55B-1.2"), 0x00, 0x00]));

        Assert.Contains(_samsungMdc.Details, d => d.Label == "Serial Number" && d.Value == "QM55B-1.2");
    }

    [Fact]
    public async Task Poll_WhileOff_GivenASerialNumberOfOnlyPadding_ShowsTheRawReplyWithNoIssue()
    {
        var reply = Reply(0x0B, 0x00);

        await PollOnceAnswering(0x0B, reply);

        Assert.Contains(new DeviceDetail("Serial Number", Convert.ToHexString(reply)), _samsungMdc.Details);
        Assert.Empty(_samsungMdc.GetIssues());
    }

    [Theory]
    [InlineData((byte)0x0B, new[] { "Serial Number" })]
    [InlineData((byte)0x0E, new[] { "Software Version" })]
    [InlineData((byte)0x8A, new[] { "Model" })]
    [InlineData((byte)0x0D, new[] { "Temperature", "Signal" })]
    [InlineData((byte)0x83, new[] { "Panel On Time" })]
    public async Task Poll_WhileOff_GivenANakForARead_ShowsItIsNotSupportedWithNoIssue(byte command, string[] labels)
    {
        await PollOnceAnswering(command, Nak(command));

        foreach (var label in labels)
            Assert.Contains(new DeviceDetail(label, $"Not supported: {Convert.ToHexString(Nak(command))}", DetailTone.Normal), _samsungMdc.Details);
        Assert.Empty(_samsungMdc.GetIssues());
        Assert.Equal(CommunicationState.Okay, _samsungMdc.CommunicationState);
    }

    [Fact]
    public async Task Poll_WhileOff_GivenEveryErrorStatusFault_RaisesEachFault()
    {
        await PollOnceAnswering(0x0D, Reply(0x0D, 0x01, 0x01, 0x01, 0x01, 0x50, 0x01));

        var issues = _samsungMdc.GetOngoingIssues().ToDictionary(i => i.Key, i => i.Severity);
        Assert.Equal(new Dictionary<string, IssueSeverity>
        {
            { "lamp-error", IssueSeverity.Major },
            { "temperature-error", IssueSeverity.Major },
            { "brightness-sensor-error", IssueSeverity.Minor },
            { "fan-error", IssueSeverity.Major },
        }, issues);
        Assert.Contains(_samsungMdc.Details, d => d.Label == "Temperature" && d.Value == "80 °C" && d.Tone == DetailTone.Error);
        Assert.Contains(_samsungMdc.Details, d => d.Label == "Signal" && d.Value == "No sync");
    }

    [Fact]
    public async Task Poll_WhileOff_GivenUnknownErrorStatusValues_ShowsThemRawWithNoIssue()
    {
        await PollOnceAnswering(0x0D, Reply(0x0D, 0x03, 0x04, 0x03, 0x05, 0x2A, 0x06));

        var details = _samsungMdc.Details.ToDictionary(d => d.Label, d => d.Value);
        Assert.Equal("0x03", details["Lamp"]);
        Assert.Equal("42 °C, status 0x04", details["Temperature"]);
        Assert.Equal("0x03", details["Brightness Sensor"]);
        Assert.Equal("0x05", details["Signal"]);
        Assert.Equal("0x06", details["Fan"]);
        Assert.Empty(_samsungMdc.GetOngoingIssues());
    }
}

public class SamsungMdcHealthReadTest : SamsungPollBench
{
    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x02, 0x00, 0x2A })]
    [InlineData(new byte[] { 0x00, 0x00, 0x02, 0x00, 0x2A, 0x00, 0x00 })]
    public async Task Poll_WhileOff_GivenThreeErrorStatusRepliesOfTheWrongLength_RaisesTheReadIssueWithTheRawReply_AndKeepsAsking(byte[] status)
    {
        var reply = Reply(0x0D, status);
        AnswerEveryRead();
        _replies[0x0D] = reply;

        await UntilPowerPolled(11);
        Assert.Empty(_samsungMdc.GetOngoingIssues());

        await UntilPowerPolled(12);
        var issue = Assert.Single(_samsungMdc.GetOngoingIssues(), i => i.Key == "error-status-read");
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.EndsWith(Convert.ToHexString(reply), issue.Message);
        Assert.Contains(new DeviceDetail("Temperature", NotReadYet), _samsungMdc.Details);
        Assert.Equal(3, TimesSent(Query(0x0D)));
    }

    [Fact]
    public async Task Poll_WhileOff_GivenANormalErrorStatusAfterFaults_ResolvesThem()
    {
        await PollOnceAnswering(0x0D, Reply(0x0D, 0x01, 0x01, 0x01, 0x01, 0x50, 0x01));

        _replies[0x0D] = Reply(0x0D, 0x00, 0x00, 0x00, 0x02, 0x2A, 0x02);
        await UntilPowerPolled(7);

        Assert.Empty(_samsungMdc.GetOngoingIssues());
        Assert.Contains(_samsungMdc.Details, d => d.Label == "Signal" && d.Value == "0x02");
    }

    [Fact]
    public async Task Poll_WhileOff_AfterTheErrorStatusIsRefused_StopsAskingUntilTheDisplayReconnects()
    {
        AnswerEveryRead();
        _replies[0x0D] = Nak(0x0D);

        await UntilPowerPolled(7);
        Assert.Equal(1, TimesSent(Query(0x0D)));
        Assert.Equal(2, TimesSent(Query(0x83)));

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        await Wait.UntilAsync(_time, PollStep, () => TimesSent(Query(0x0D)) > 1, "The error status was not asked for again after the display reconnected");
    }
}
