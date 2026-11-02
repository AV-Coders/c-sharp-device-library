using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public abstract class NecPollBench
{
    protected static readonly TimeSpan PollStep = TimeSpan.FromSeconds(23);
    protected const string NotReadYet = "Not read yet: read while the display is off";
    protected const string PowerQuery = "01D6";
    protected const string InputQuery = "0060";
    protected const string VolumeQuery = "0062";
    protected const string ModelQuery = "C217";
    protected const string SerialQuery = "C216";
    protected const string FirmwareQuery = "CA0200";
    protected const string MacAddressQuery = "C22000";
    protected const string SelfDiagnosisQuery = "B1";
    protected const string PowerOffCommand = "C203D60004";
    protected const string SelectHdmi2Command = "00600012";
    protected static readonly string[] ReadLabels = ["Model", "Serial Number", "Firmware", "MAC Address", "Self-Diagnosis", "Signal"];
    protected static readonly byte[] CapturedPowerQuery = Convert.FromHexString("01302A304130360230314436031F0D");
    protected static readonly byte[] CapturedPowerOffReply =
        [0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x34, 0x03, 0x71, 0x0D];
    protected static readonly byte[] ManualNullMessage = [0x01, 0x30, 0x30, 0x41, 0x42, 0x30, 0x34, 0x02, 0x42, 0x45, 0x03, 0x01, 0x0D];
    protected static readonly byte[] ModelReply = Reply("C317" + "50343033");
    protected readonly FakeTimeProvider _time = new();
    protected readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    protected readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly List<byte[]> _sent = [];
    protected readonly ConcurrentDictionary<string, byte[][]> _replies = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[][]>> _repliesInTurn = new();
    protected byte[] _powerReply = CapturedPowerOffReply;
    protected readonly NecUhdExternalControl _display;

    protected NecPollBench(bool captureLogs)
    {
        _mockClient.Setup(x => x.Send(It.IsAny<byte[]>())).Callback<byte[]>(frame =>
        {
            lock (_sent)
                _sent.Add(frame);
            foreach (var reply in RepliesTo(MessageOf(frame)))
                _mockClient.Object.ResponseByteHandlers!.Invoke(reply);
        });
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, ConnectionState.Connected);
        _display = captureLogs ? CreateDisplayWithCapturedLogs() : CreateDisplay();
    }

    private NecUhdExternalControl CreateDisplay() =>
        new(_mockClient.Object, "Test display", Input.Hdmi1, 0x2A, _time);

    private NecUhdExternalControl CreateDisplayWithCapturedLogs()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            return CreateDisplay();
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private byte[][] RepliesTo(string message)
    {
        if (_repliesInTurn.TryGetValue(message, out var inTurn) && inTurn.TryDequeue(out var turn))
            return turn;
        if (message == PowerQuery)
            return [_powerReply];
        return _replies.GetValueOrDefault(message, []);
    }

    protected static byte[] Reply(string message, byte messageType = 0x42)
    {
        byte[] body = [0x30, 0x30, (byte)'A', messageType, ..Encoding.ASCII.GetBytes($"{message.Length + 2:X2}"), 0x02, ..Encoding.ASCII.GetBytes(message), 0x03];
        return [0x01, ..body, body.Aggregate((byte)0, (check, next) => (byte)(check ^ next)), 0x0D];
    }

    private static string MessageOf(byte[] frame) => Encoding.ASCII.GetString(frame[8..^3]);

    protected List<string> Sent()
    {
        lock (_sent)
            return _sent.Select(MessageOf).ToList();
    }

    protected byte[] FirstSent()
    {
        lock (_sent)
            return _sent[0];
    }

    protected int TimesSent(string message) => Sent().Count(sent => sent == message);

    protected Task UntilPowerPolled(int times) =>
        Wait.UntilAsync(_time, PollStep, () => TimesSent(PowerQuery) >= times, $"The power poll was sent fewer than {times} times");

    protected Task UntilSent(string message) =>
        Wait.UntilAsync(_time, PollStep, () => TimesSent(message) >= 1, $"{message} was never sent");

    protected void Answer(string message, params byte[][] frames) => _replies[message] = frames;

    protected void AnswerInTurn(string message, params byte[][][] turns) => _repliesInTurn[message] = new ConcurrentQueue<byte[][]>(turns);

    protected void AnswerEveryRead()
    {
        Answer(ModelQuery, ModelReply);
        Answer(SerialQuery, Reply("C316" + "31323334"));
        Answer(FirmwareQuery, Reply("CB02" + "00" + "00" + "R1.234AB"));
        Answer(MacAddressQuery, Reply("C320" + "00" + "0011AABBCCDD"));
        Answer(SelfDiagnosisQuery, Reply("A1" + "00"));
    }

    protected string Detail(string label) => _display.Details.Single(d => d.Label == label).Value;
}

public class NecUhdExternalControlDetailsTest() : NecPollBench(captureLogs: false)
{
    [Fact]
    public void Constructor_ShowsEveryReadAsNotReadYet()
    {
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _display.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_SendsTheReadsAfterThePowerPoll()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        Assert.Equal([PowerQuery, ModelQuery, SerialQuery, FirmwareQuery, MacAddressQuery, SelfDiagnosisQuery, PowerQuery], Sent().Take(7));
        Assert.Equal(CapturedPowerQuery, FirstSent());
    }

    [Fact]
    public async Task Poll_WhileOn_SendsOnlyTheExistingPolls()
    {
        _powerReply = NecUhdExternalControlTest.PowerOnReply;

        await UntilPowerPolled(3);

        Assert.Equal([PowerQuery, InputQuery, VolumeQuery, PowerQuery, InputQuery, VolumeQuery, PowerQuery], Sent().Take(7));
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _display.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_ReplacesNotReadYetWithTheValuesRead()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        Assert.Equal(
        [
            new DeviceDetail("Model", "P403"),
            new DeviceDetail("Serial Number", "1234"),
            new DeviceDetail("Firmware", "R1.234AB"),
            new DeviceDetail("MAC Address", "0011AABBCCDD"),
            new DeviceDetail("Self-Diagnosis", "Normal"),
            new DeviceDetail("Signal", "Present"),
        ], _display.Details);
        Assert.Empty(_display.GetIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_AfterAMissedReply_AsksAgainOnlyForTheUnansweredIdentityRead()
    {
        AnswerEveryRead();
        _replies.TryRemove(SerialQuery, out _);

        await UntilPowerPolled(3);

        Assert.Equal(1, TimesSent(ModelQuery));
        Assert.Equal(2, TimesSent(SerialQuery));
        Assert.Equal(1, TimesSent(FirmwareQuery));
        Assert.Equal(1, TimesSent(SelfDiagnosisQuery));
        Assert.Equal(NotReadYet, Detail("Serial Number"));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenNoReplyThreeTimesInARow_RaisesTheReadIssue_KeepsAsking_AndAReplyResolvesIt()
    {
        AnswerEveryRead();
        AnswerInTurn(ModelQuery, [], [], [], [ModelReply]);

        await UntilPowerPolled(3);
        Assert.Empty(_display.GetOngoingIssues());

        await UntilPowerPolled(4);
        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("model-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The display did not give a real reply to the model name read C217 3 times in a row, the last was no reply within 1500 ms", issue.Message);

        await UntilPowerPolled(5);
        Assert.Equal(4, TimesSent(ModelQuery));
        Assert.Equal("P403", Detail("Model"));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenThreeUnreadableRepliesInARow_RaisesTheReadIssueWithTheRawReply_AndAReplyResolvesIt()
    {
        AnswerEveryRead();
        var unreadable = Reply("C317" + "5G");
        AnswerInTurn(ModelQuery, [unreadable], [unreadable], [unreadable], [ModelReply]);

        await UntilPowerPolled(4);
        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("model-read", issue.Key);
        Assert.EndsWith(Convert.ToHexString(unreadable), issue.Message);

        await UntilPowerPolled(5);
        Assert.Equal("P403", Detail("Model"));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenANullMessageThreeTimesInARow_ShowsItIsNotSupportedWithNoIssue_AndStopsUntilTheDisplayReconnects()
    {
        AnswerEveryRead();
        AnswerInTurn(ModelQuery, [ManualNullMessage], [ManualNullMessage], [ManualNullMessage]);

        await UntilPowerPolled(5);

        Assert.Equal(3, TimesSent(ModelQuery));
        Assert.Equal(new DeviceDetail("Model", $"Not supported: {Convert.ToHexString(ManualNullMessage)}", DetailTone.Normal),
            _display.Details.Single(d => d.Label == "Model"));
        Assert.Empty(_display.GetIssues());
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await Wait.UntilAsync(_time, PollStep, () => TimesSent(ModelQuery) >= 4, "The model name read was not asked again after reconnecting");
        await UntilPowerPolled(TimesSent(PowerQuery) + 1);
        Assert.Equal("P403", Detail("Model"));
    }

    [Fact]
    public async Task Poll_WhileOff_GivenAFirmwareErrorResult_ShowsItIsNotSupportedWithNoIssue_AndStopsAsking()
    {
        AnswerEveryRead();
        var rejected = Reply("CB02" + "01" + "00");
        Answer(FirmwareQuery, rejected);

        await UntilPowerPolled(3);

        Assert.Equal(1, TimesSent(FirmwareQuery));
        Assert.Equal(new DeviceDetail("Firmware", $"Not supported: {Convert.ToHexString(rejected)}", DetailTone.Normal),
            _display.Details.Single(d => d.Label == "Firmware"));
        Assert.Empty(_display.GetIssues());
    }

    [Theory]
    [InlineData(SerialQuery, "C316", "Serial Number")]
    [InlineData(FirmwareQuery, "CB02" + "00" + "00", "Firmware")]
    [InlineData(MacAddressQuery, "C320" + "00", "MAC Address")]
    public async Task Poll_WhileOff_GivenAnEmptyIdentityValue_ShowsTheRawReplyWithNoIssue(string query, string message, string label)
    {
        AnswerEveryRead();
        var reply = Reply(message);
        Answer(query, reply);

        await UntilPowerPolled(2);

        Assert.Equal(Convert.ToHexString(reply), Detail(label));
        Assert.Empty(_display.GetIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenAFanFaultAndNoSignal_RaisesTheFaultAndShowsNoSignalAsADetail()
    {
        AnswerEveryRead();
        Answer(SelfDiagnosisQuery, Reply("A1" + "80" + "B0"));

        await UntilPowerPolled(2);

        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("self-diagnosis:80", issue.Key);
        Assert.Equal("Self-diagnosis reports cooling fan 1 abnormality (80)", issue.Message);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Equal("No signal", Detail("Signal"));
        Assert.Contains(new DeviceDetail("Self-Diagnosis", "cooling fan 1 abnormality (80)", DetailTone.Error), _display.Details);
    }

    [Fact]
    public async Task Poll_WhileOff_GivenAUserTemperatureWarning_RaisesAMinorIssue()
    {
        AnswerEveryRead();
        Answer(SelfDiagnosisQuery, Reply("A1" + "A2"));

        await UntilPowerPolled(2);

        Assert.Equal(IssueSeverity.Minor, Assert.Single(_display.GetOngoingIssues()).Severity);
        Assert.Contains(_display.Details, d => d.Label == "Self-Diagnosis" && d.Tone == DetailTone.Warning);
    }

    [Fact]
    public async Task Poll_WhileOff_GivenAnUnrecognisedDiagnosisCode_RaisesItWithTheRawCode()
    {
        AnswerEveryRead();
        Answer(SelfDiagnosisQuery, Reply("A1" + "Z9"));

        await UntilPowerPolled(2);

        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == "self-diagnosis:Z9");
        Assert.Equal("Self-diagnosis reports an unrecognised code (Z9)", issue.Message);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
    }

    [Fact]
    public async Task Poll_WhileOff_ReadsTheSelfDiagnosisEveryFourthPoll_AndANormalDiagnosisResolvesAnEarlierFault()
    {
        AnswerEveryRead();
        AnswerInTurn(SelfDiagnosisQuery, [Reply("A1" + "80" + "E0")], [Reply("A1" + "E0")]);

        await UntilPowerPolled(6);

        Assert.Equal(2, TimesSent(SelfDiagnosisQuery));
        Assert.Equal(["self-diagnosis:E0"], _display.GetOngoingIssues().Select(i => i.Key));
        Assert.Contains(_display.GetIssues(), i => i.Key == "self-diagnosis:80" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public async Task Poll_GivenAnInputCommandRejectedDuringARead_ReportsTheErrorResult_AndTheReadIsAnswered()
    {
        AnswerEveryRead();
        Answer(ModelQuery);
        Answer(SelectHdmi2Command, ModelReply, Reply("01" + "0060" + "00" + "0088" + "0012", 0x46));
        await UntilSent(ModelQuery);

        _display.SetInput(Input.Hdmi2);
        await UntilPowerPolled(2);

        Assert.Equal("P403", Detail("Model"));
        Assert.Contains(_display.Events, e => e.Type == EventType.Error && e.Info == "The display returned an error result");
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_GivenAnotherControllersReplies_KeepsTheirPowerState_ButCountsThemNeitherForNorAgainstTheReads()
    {
        AnswerEveryRead();
        _replies.TryRemove(SerialQuery, out _);
        Answer(ModelQuery, CapturedPowerOffReply, Reply("C316" + "31323334"), ModelReply);
        _mockClient.Object.ResponseByteHandlers!.Invoke(CapturedPowerOffReply);
        Assert.Equal(PowerState.Off, _display.PowerState);

        await UntilPowerPolled(3);

        Assert.Equal("P403", Detail("Model"));
        Assert.Equal(NotReadYet, Detail("Serial Number"));
        Assert.Equal(2, TimesSent(SerialQuery));
        Assert.Equal(1, TimesSent(ModelQuery));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public void ResponseHandler_GivenAnIdentityReplyWithNoReadWaiting_ShowsNothing()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(ModelReply);

        Assert.Equal(NotReadYet, Detail("Model"));
    }

    [Fact]
    public void ResponseHandler_GivenANullMessageWithNoCommandWaiting_RaisesNothing()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(ManualNullMessage);

        Assert.Empty(_display.GetOngoingIssues());
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class NecUhdExternalControlRejectionLogTest() : NecPollBench(captureLogs: true)
{
    [Fact]
    public async Task Poll_GivenAPowerCommandRejectedDuringARead_ReportsTheNullAgainstThePowerCommand_AndTheReadIsAnswered()
    {
        AnswerEveryRead();
        Answer(ModelQuery);
        Answer(PowerOffCommand, ModelReply, ManualNullMessage);
        await UntilSent(ModelQuery);

        _display.PowerOff();
        await UntilPowerPolled(2);

        Assert.Equal("P403", Detail("Model"));
        Assert.Empty(_display.GetIssues());
        Assert.Contains(_logs.Logs, log => log.Message == $"The display gave a NULL message {Convert.ToHexString(ManualNullMessage)} for {PowerOffCommand}");
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("got a NULL message"));
    }
}
