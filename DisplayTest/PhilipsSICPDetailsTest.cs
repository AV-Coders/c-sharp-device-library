using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public abstract class PhilipsPollBench
{
    protected static readonly TimeSpan PollStep = TimeSpan.FromSeconds(23);
    protected const string NotReadYet = "Not read yet: read while the display is off";
    protected const string PowerQuery = "19";
    protected const string InputQuery = "AD";
    protected const string VolumeQuery = "45";
    protected const string MuteQuery = "46";
    protected const string ModelQuery = "A100";
    protected const string FirmwareQuery = "A101";
    protected const string BuildDateQuery = "A102";
    protected const string SerialQuery = "15";
    protected const string TemperatureQuery = "2F";
    protected const string PowerOffCommand = "1801";
    protected static readonly string[] ReadLabels = ["Model", "Firmware", "Build Date", "Serial Number", "Temperature"];
    protected static readonly byte[] ManualPowerOnReport = [0x06, 0x01, 0x01, 0x19, 0x02, 0x1D];
    private static readonly byte[] PowerOffReport = [0x06, 0x01, 0x01, 0x19, 0x01, 0x1E];
    protected static readonly byte[] ManualAck = [0x06, 0x01, 0x01, 0x00, 0x06, 0x00];
    protected static readonly byte[] ManualNotAvailable = [0x06, 0x01, 0x01, 0x00, 0x18, 0x1E];
    protected static readonly byte[] ManualNack = [0x06, 0x01, 0x01, 0x00, 0x15, 0x13];
    private static readonly byte[] ManualTemperatureReport = [0x07, 0x01, 0x01, 0x2F, 0x1C, 0x1F, 0x2B];
    private static readonly byte[] ManualSerialReport =
        [0x13, 0x01, 0x01, 0x15, 0x48, 0x41, 0x31, 0x41, 0x30, 0x39, 0x31, 0x37, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x77];
    protected static readonly byte[] ModelReport = Text(0xA1, "10BDL3051T");
    protected readonly FakeTimeProvider _time = new();
    protected readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    protected readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly List<byte[]> _sent = [];
    protected readonly ConcurrentDictionary<string, byte[][]> _replies = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[][]>> _repliesInTurn = new();
    protected byte[]? _powerReply = PowerOffReport;
    private (string After, byte[] Reply)? _lateReply;
    protected readonly PhilipsSICP _display;

    protected PhilipsPollBench(bool captureLogs)
    {
        _mockClient.Setup(x => x.Send(It.IsAny<byte[]>())).Callback<byte[]>(frame =>
        {
            string? previous;
            lock (_sent)
            {
                previous = _sent.Count == 0 ? null : DataOf(_sent[^1]);
                _sent.Add(frame);
            }
            if (_lateReply is { } late && late.After == previous)
            {
                _lateReply = null;
                _mockClient.Object.ResponseByteHandlers!.Invoke(late.Reply);
            }
            foreach (var reply in RepliesTo(DataOf(frame)))
                _mockClient.Object.ResponseByteHandlers!.Invoke(reply);
        });
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, ConnectionState.Connected);
        _display = captureLogs ? CreateDisplayWithCapturedLogs() : CreateDisplay();
    }

    private PhilipsSICP CreateDisplay() =>
        new(_mockClient.Object, 0x01, 0x00, "Test display", Input.Hdmi1, 23, _time);

    private PhilipsSICP CreateDisplayWithCapturedLogs()
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

    private byte[][] RepliesTo(string data)
    {
        if (_repliesInTurn.TryGetValue(data, out var inTurn) && inTurn.TryDequeue(out var turn))
            return turn;
        if (data == PowerQuery)
            return _powerReply == null ? [] : [_powerReply];
        return _replies.GetValueOrDefault(data, []);
    }

    private static byte[] Report(params byte[] data)
    {
        byte[] frame = [(byte)(data.Length + 4), 0x01, 0x01, ..data, 0x00];
        frame[^1] = frame[..^1].Aggregate((byte)0, (check, next) => (byte)(check ^ next));
        return frame;
    }

    protected static byte[] Text(byte command, string text) => Report([command, ..Encoding.ASCII.GetBytes(text)]);

    private static string DataOf(byte[] frame) => Convert.ToHexString(frame[3..^1]);

    protected List<string> Sent()
    {
        lock (_sent)
            return _sent.Select(DataOf).ToList();
    }

    protected int TimesSent(string data) => Sent().Count(sent => sent == data);

    protected Task UntilPowerPolled(int times) =>
        Wait.UntilAsync(_time, PollStep, () => TimesSent(PowerQuery) >= times, $"The power poll was sent fewer than {times} times");

    protected Task UntilSent(string data) =>
        Wait.UntilAsync(_time, PollStep, () => TimesSent(data) >= 1, $"{data} was never sent");

    protected void Answer(string data, params byte[][] frames) => _replies[data] = frames;

    protected void AnswerInTurn(string data, params byte[][][] turns) => _repliesInTurn[data] = new ConcurrentQueue<byte[][]>(turns);

    protected void ReplyLate(string after, byte[] reply) => _lateReply = (after, reply);

    protected void AnswerEveryRead()
    {
        Answer(ModelQuery, ModelReport);
        Answer(FirmwareQuery, Text(0xA1, "FB01.02"));
        Answer(BuildDateQuery, Text(0xA1, "2024/05/01"));
        Answer(SerialQuery, ManualSerialReport);
        Answer(TemperatureQuery, ManualTemperatureReport);
    }

    protected string Detail(string label) => _display.Details.Single(d => d.Label == label).Value;
}

public class PhilipsSICPDetailsTest() : PhilipsPollBench(captureLogs: false)
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

        Assert.Equal([PowerQuery, ModelQuery, FirmwareQuery, BuildDateQuery, SerialQuery, TemperatureQuery, PowerQuery], Sent().Take(7));
    }

    [Fact]
    public async Task Poll_WhileOn_SendsOnlyTheExistingPolls()
    {
        _powerReply = ManualPowerOnReport;

        await UntilPowerPolled(3);

        Assert.Equal([PowerQuery, InputQuery, VolumeQuery, MuteQuery, PowerQuery, InputQuery, VolumeQuery, MuteQuery, PowerQuery], Sent().Take(9));
        Assert.Equal(ReadLabels.Select(label => new DeviceDetail(label, NotReadYet)), _display.Details);
    }

    [Fact]
    public async Task Poll_WhileThePowerStateIsUnknown_SendsOnlyThePowerPoll()
    {
        _powerReply = null;

        await UntilPowerPolled(2);

        Assert.Equal([PowerQuery, PowerQuery], Sent());
    }

    [Fact]
    public async Task Poll_WhileOff_ReplacesNotReadYetWithTheValuesRead()
    {
        AnswerEveryRead();

        await UntilPowerPolled(2);

        Assert.Equal(
        [
            new DeviceDetail("Model", "10BDL3051T"),
            new DeviceDetail("Firmware", "FB01.02"),
            new DeviceDetail("Build Date", "2024/05/01"),
            new DeviceDetail("Serial Number", "HA1A0917123456"),
            new DeviceDetail("Temperature", "28 °C, 31 °C"),
        ], _display.Details);
        Assert.Empty(_display.GetIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_AfterAMissedReply_AsksAgainOnlyForTheUnansweredIdentityRead()
    {
        AnswerEveryRead();
        _replies.TryRemove(BuildDateQuery, out _);

        await UntilPowerPolled(3);

        Assert.Equal(1, TimesSent(ModelQuery));
        Assert.Equal(2, TimesSent(BuildDateQuery));
        Assert.Equal(1, TimesSent(SerialQuery));
        Assert.Equal(1, TimesSent(TemperatureQuery));
        Assert.Equal(NotReadYet, Detail("Build Date"));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenNoReplyThreeTimesInARow_RaisesTheReadIssue()
    {
        AnswerEveryRead();
        _replies.TryRemove(BuildDateQuery, out _);

        await UntilPowerPolled(4);

        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("build-date-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The display did not answer the build date read A102 3 times in a row, the last was no reply within 3 s", issue.Message);
    }

    [Fact]
    public async Task Poll_WhileOff_GivenThreeNacksInARow_RaisesTheReadIssue_KeepsAsking_AndAReplyResolvesIt()
    {
        AnswerEveryRead();
        AnswerInTurn(ModelQuery, [ManualNack], [ManualNack], [ManualNack]);

        await UntilPowerPolled(3);
        Assert.Empty(_display.GetOngoingIssues());

        await UntilPowerPolled(4);
        var issue = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("model-read", issue.Key);
        Assert.EndsWith(Convert.ToHexString(ManualNack), issue.Message);
        Assert.Equal(NotReadYet, Detail("Model"));

        await UntilPowerPolled(5);
        Assert.Equal(4, TimesSent(ModelQuery));
        Assert.Equal("10BDL3051T", Detail("Model"));
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenNotAvailable_ShowsItIsNotSupportedWithNoIssue_AndStopsUntilTheDisplayReconnects()
    {
        AnswerEveryRead();
        AnswerInTurn(TemperatureQuery, [ManualNotAvailable]);

        await UntilPowerPolled(7);

        Assert.Equal(1, TimesSent(TemperatureQuery));
        Assert.Equal(new DeviceDetail("Temperature", $"Not supported: {Convert.ToHexString(ManualNotAvailable)}", DetailTone.Normal),
            _display.Details.Single(d => d.Label == "Temperature"));
        Assert.Empty(_display.GetIssues());
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await Wait.UntilAsync(_time, PollStep, () => TimesSent(TemperatureQuery) >= 2, "The temperature read was not asked again after reconnecting");
        await UntilPowerPolled(TimesSent(PowerQuery) + 1);
        Assert.Equal("28 °C, 31 °C", Detail("Temperature"));
    }

    [Theory]
    [InlineData("\0\0\0\0")]
    [InlineData("    ")]
    public async Task Poll_WhileOff_GivenABlankModelNumber_ShowsTheRawReplyWithNoIssue_AndStopsAsking(string blank)
    {
        AnswerEveryRead();
        var empty = Text(0xA1, blank);
        Answer(ModelQuery, empty);

        await UntilPowerPolled(3);

        Assert.Equal(Convert.ToHexString(empty), Detail("Model"));
        Assert.Equal(1, TimesSent(ModelQuery));
        Assert.Empty(_display.GetIssues());
    }

    [Fact]
    public async Task Poll_WhileOff_GivenAModelNumberReplyAfterTheReadTimedOut_SendsNoOtherModelInformationRead_AndShowsNoValueUnderTheWrongRead()
    {
        AnswerEveryRead();
        AnswerInTurn(ModelQuery, [], [ModelReport]);
        ReplyLate(ModelQuery, ModelReport);

        await UntilPowerPolled(2);
        Assert.Equal([PowerQuery, ModelQuery, SerialQuery, TemperatureQuery, PowerQuery], Sent().Take(5));
        Assert.Equal(NotReadYet, Detail("Firmware"));
        Assert.Equal(NotReadYet, Detail("Model"));

        await UntilPowerPolled(3);
        Assert.Equal("10BDL3051T", Detail("Model"));
        Assert.Equal("FB01.02", Detail("Firmware"));
        Assert.Equal("2024/05/01", Detail("Build Date"));
    }

    [Fact]
    public void HandleResponse_GivenModelInformationWithNoReadWaiting_ShowsNothing()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(ModelReport);

        Assert.Equal(NotReadYet, Detail("Model"));
    }

    [Fact]
    public async Task Poll_GivenAPowerCommandRejectedDuringARead_RaisesAMomentaryIssueNamingTheCommand_AndTheReadIsAnswered()
    {
        AnswerEveryRead();
        Answer(ModelQuery);
        Answer(PowerOffCommand, ModelReport, ManualNotAvailable);
        await UntilSent(ModelQuery);

        _display.PowerOff();
        await UntilSent(FirmwareQuery);

        Assert.Equal("10BDL3051T", Detail("Model"));
        var issue = Assert.Single(_display.GetIssues());
        Assert.Equal("rejected-command", issue.Key);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal($"The display rejected the command {PowerOffCommand} with {Convert.ToHexString(ManualNotAvailable)}", issue.Message);
    }

    [Fact]
    public async Task Poll_GivenACommandAcknowledgedDuringARead_GivesTheReadItsOwnRejection()
    {
        AnswerEveryRead();
        Answer(ModelQuery);
        Answer(PowerOffCommand, ManualNotAvailable, ManualAck);
        await UntilSent(ModelQuery);

        _display.PowerOff();
        await UntilSent(FirmwareQuery);

        Assert.Equal(new DeviceDetail("Model", $"Not supported: {Convert.ToHexString(ManualNotAvailable)}", DetailTone.Normal),
            _display.Details.Single(d => d.Label == "Model"));
        Assert.Empty(_display.GetIssues());
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class PhilipsSICPRejectionLogTest() : PhilipsPollBench(captureLogs: true)
{
    [Fact]
    public void HandleResponse_GivenANackWithNoCommandWaiting_LogsItAndRaisesNothing()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(ManualNack);

        Assert.Empty(_display.GetOngoingIssues());
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
        Assert.Contains(_logs.Logs, log => log.Message == $"The display did not accept a command: {Convert.ToHexString(ManualNack)}");
    }
}
