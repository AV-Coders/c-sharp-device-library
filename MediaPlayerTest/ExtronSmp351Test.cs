using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.MediaPlayer.Tests;

public class ExtronSmp351Test
{
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronSmp351 _recorder;
    private readonly Mock<SshClient> _mockClient = TestFactory.CreateSshClient();
    private readonly Mock<TransportStateHandler> _recordStateHandler = new();
    private readonly Mock<StringHandler> _timestampHandler = new ();
    private readonly Mock<ConnectionStateHandler> _mockFrontUsbConnectionStateHandler = new();
    private readonly Mock<ConnectionStateHandler> _mockRearUsbConnectionStateHandler = new();
    public const string EscapeHeader = "\x1b";
    private const string VerboseMode = $"{EscapeHeader}3CV\r";
    private const int PollTime = 1000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(PollTime);
    private static readonly TimeSpan VerboseModeDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
    private const string TemperatureRead = $"{EscapeHeader}20STAT\r";
    private const string StoppedInformation = "Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>*<41:28:03*00:00:00>\r\n";
    private const int MaxPolls = 30;
    // No SMP has been on a bench: no fixture in this file is a bench capture. They are invented or from SMP 300 Series UG 68-2238-01 P.
    private static readonly Dictionary<string, string> GoodReplies = new()
    {
        { "I", StoppedInformation },
        { "*Q", "Bld3.15.0004\r\n" },
        { "N", "Pno60-1324-01\r\n" },
        { "1I", "Inf01*SMP 351\r\n" },
        { "99I", "Inf99*A13VE3R\r\n" },
        { "98I", "Inf98*00:05:A6:12:34:56\r\n" },
        { TemperatureRead, "20Stat 45.2C\r\n" },
        { "39I", "Inf39*None active\r\n" }
    };

    public ExtronSmp351Test()
    {
        _recorder = new ExtronSmp351(_mockClient.Object, 1024000, 512000, "test", PollTime, _time);
        _recorder.TransportStateHandlers += _recordStateHandler.Object;
        _recorder.TimestampHandlers += _timestampHandler.Object;
        _recorder.FrontUsbConnectionStateHandlers += _mockFrontUsbConnectionStateHandler.Object;
        _recorder.RearUsbConnectionStateHandlers += _mockRearUsbConnectionStateHandler.Object;
    }

    private static void SetConnection(Mock<SshClient> client, ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(client.Object, state);

    private void SetConnection(ConnectionState state) => SetConnection(_mockClient, state);

    private int VerboseModeSends() => _mockClient.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(CommunicationClient.Send) && VerboseMode.Equals(invocation.Arguments[0]));

    private string? Detail(string label) => Detail(_recorder, label);

    private static string? Detail(ExtronSmp351 recorder, string label) =>
        recorder.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? Ongoing(string key) => Ongoing(_recorder, key);

    private static Issue? Ongoing(ExtronSmp351 recorder, string key) =>
        recorder.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    private static int Sends(Mock<SshClient> client, string command) => client.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(CommunicationClient.Send) && command.Equals(invocation.Arguments[0]));

    private static List<string> Sent(Mock<SshClient> client) => client.Invocations
        .Where(invocation => invocation.Method.Name == nameof(CommunicationClient.Send))
        .Select(invocation => (string)invocation.Arguments[0])
        .ToList();

    private static List<string> Reads(Mock<SshClient> client) =>
        Sent(client).Where(command => command != "I" && command != VerboseMode).ToList();

    private static void Reply(Mock<SshClient> client, string response) => client.Object.ResponseHandlers!.Invoke(response);

    private static List<CapturedLog> Warnings(ScopeCapturingLoggerFactory logs) => logs.Logs
        .Where(log => log.Level == LogLevel.Warning && log.Scope.GetValueOrDefault("Class") is nameof(ExtronSmp351))
        .ToList();

    private static void Poll(ExtronSmp351 recorder) =>
        ((Task)typeof(ExtronSmp351).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(recorder, [CancellationToken.None])!).GetAwaiter().GetResult();

    private void PollAndReply(ExtronSmp351 recorder, Mock<SshClient> client, string read, string? reply)
    {
        var sentBefore = Sent(client).Count;
        Poll(recorder);
        foreach (var command in Sent(client).Skip(sentBefore))
        {
            var answer = command == read ? reply : GoodReplies[command];
            if (answer != null)
                Reply(client, answer);
        }
        _time.Advance(PollInterval);
    }

    private async Task<(ExtronSmp351 Recorder, Mock<SshClient> Client, ScopeCapturingLoggerFactory Logs)> ConnectedRecorderAsync()
    {
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "reads", PollTime, _time), out var recorder);
        await ((ThreadWorker)typeof(ExtronSmp351).GetField("_pollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(recorder)!).Stop();
        SetConnection(client, ConnectionState.Connected);
        _time.Advance(VerboseModeDelay);
        Reply(client, "Vrb3\r\n");
        return (recorder, client, logs);
    }

    [Fact]
    public void HandleConnectionState_SetsVerboseMode3OnceTheDelayHasPassed()
    {
        SetConnection(ConnectionState.Connected);

        Assert.Equal(0, VerboseModeSends());
        _time.Advance(VerboseModeDelay - OneTick);
        Assert.Equal(0, VerboseModeSends());

        _time.Advance(OneTick);

        Assert.Equal(1, VerboseModeSends());
    }

    [Fact]
    public void HandleConnectionState_DisconnectInsideTheDelay_SendsNothing()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);

        _time.Advance(VerboseModeDelay);

        Assert.Equal(0, VerboseModeSends());
    }

    [Fact]
    public void HandleConnectionState_ReconnectInsideTheDelay_SetsVerboseModeOnce()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);

        _time.Advance(VerboseModeDelay);

        Assert.Equal(1, VerboseModeSends());
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Error)]
    public void Module_DoesNotSetVerboseModeWhenNotConnected(ConnectionState state)
    {
        _mockClient.Object.ConnectionStateHandlers!.Invoke(state);
        _time.Advance(VerboseModeDelay);

        _mockClient.Verify(x => x.Send($"{EscapeHeader}3CV\r"), Times.Never);
    }

    [Fact]
    public void Record_SendsTheCommand()
    {
        _recorder.Record();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}Y1RCDR\r"));
    }

    [Fact]
    public void Pause_SendsTheCommand()
    {
        _recorder.Pause();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}Y2RCDR\r"));
    }

    [Fact]
    public void Stop_SendsTheCommand()
    {
        _recorder.Stop();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}Y0RCDR\r"));
    }

    [Fact]
    public void AddMarker_SendsTheCommand()
    {
        _recorder.AddMarker();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}BRCDR\r"));
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>*<41:28:03*00:00:00>\r\n", TransportState.Stopped)]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*N/A>*<116606580*N/A>*<00:00:06>*<824:27:00*00:00:00>\r\n", TransportState.Recording)]
    [InlineData("Inf*<ChA1*ChB3>*<paused>*<internal*N/A>*<116606376*N/A>*<00:00:13>*<824:26:40*00:00:00>\n\r\n", TransportState.RecordingPaused)]
    [InlineData("RcdrY1\r\n", TransportState.Recording)]
    [InlineData("RcdrY0\r\n", TransportState.Stopped)]
    [InlineData("RcdrY2\r\n", TransportState.RecordingPaused)]
    [InlineData("RecStart05\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart04\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart03\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart02\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart01\r\n", TransportState.PreparingToRecord)]
    public void HandleResponse_ReportsRecordingState(string response, TransportState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _recordStateHandler.Verify(x => x.Invoke(expectedState));
    }

    [Fact]
    public void HandleResponse_ClearsTimestampWhenStopped()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("RcdrY0\r\n");
        
        _timestampHandler.Verify(x => x.Invoke(string.Empty));
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*auto>*<116606760*N/A>*<00:00:06>*<41:28:03*00:00:00>\r\n", "00:00:06")]
    [InlineData("Inf*<ChA1*ChB3>*<paused>*<internal*N/A>*<116606580*N/A>*<09:10:06>*<824:27:00*00:00:00>\r\n", "09:10:06")]
    public void HandleResponse_ReportsRecordTime(string response, string expectedTime)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _timestampHandler.Verify(x => x.Invoke(expectedTime));
    }
    
    [Fact]
    public void HandleResponse_DoesNotReportRecordTimeWhenStopped()
    {
        var response = "Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:06>*<41:28:03*00:00:00>\r\n";
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _timestampHandler.Verify(x => x.Invoke(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("RcdrN0*usbrear\r\n", ConnectionState.Disconnected)]
    [InlineData("RcdrN1*usbrear\r\n", ConnectionState.Connected)]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:49:20*0\r\n", ConnectionState.Connected)]
    [InlineData("Inf57*N/A\r\n", ConnectionState.Disconnected)]
    public void HandleResponse_HandlesRearUsbStatus(string response, ConnectionState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _mockRearUsbConnectionStateHandler.Verify(x => x.Invoke(expectedState));
        Assert.Equal(expectedState, _recorder.RearUsbConnectionState);
    }

    [Theory]
    [InlineData("RcdrN0*usbfront\r\n", ConnectionState.Disconnected)]
    [InlineData("RcdrN1*usbfront\r\n", ConnectionState.Connected)]
    [InlineData("Inf56*usbfront/232*93MB*238557MB*238464MB*86:49:20*0\r\n", ConnectionState.Connected)]
    [InlineData("Inf56*N/A\r\n", ConnectionState.Disconnected)]
    public void HandleResponse_HandlesFrontUsbStatus(string response, ConnectionState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        _mockFrontUsbConnectionStateHandler.Verify(x => x.Invoke(expectedState));
        Assert.Equal(expectedState, _recorder.FrontUsbConnectionState);
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("RcdrY0\r\n");

        Assert.Equal(CommunicationState.Okay, _recorder.CommunicationState);
    }

    [Fact]
    public void HandleResponse_IgnoresUnknownResponses()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Vrb3\r\n");

        Assert.NotEqual(CommunicationState.Okay, _recorder.CommunicationState);
        Assert.Empty(_recorder.GetIssues());
    }

    [Theory]
    [InlineData("E22\r\n", "E22 (Busy)")]
    [InlineData("E99\r\n", "E99")]
    public async Task HandleResponse_ErrorWithNothingAwaitingAReply_LogsAWarningWithTheRawErrorAndRaisesNoIssue(string error, string logged)
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();

        Reply(client, error);

        var warning = Assert.Single(Warnings(logs));
        Assert.Equal($"The SMP returned {logged} with no command awaiting a reply", warning.Message);
        Assert.Empty(recorder.GetIssues());
    }

    [Fact]
    public async Task HandleResponse_RecordErrorWhileAReadIsInFlight_RaisesAMomentaryIssueForRecordAndLeavesTheRead()
    {
        const string message = "The SMP returned an error: E14 (Not valid for this configuration) to Esc Y1RCDR";
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        using (Trigger.Resume("5d30414a"))
            recorder.Record();
        Poll(recorder);

        Reply(client, "E14\r\n");
        Reply(client, StoppedInformation);
        Reply(client, "Bld3.15.0004\r\n");
        _time.Advance(PollInterval);
        Poll(recorder);

        var issue = Assert.Single(recorder.GetIssues());
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal("error-reply", issue.Key);
        Assert.Equal(message, issue.Message);
        var warning = Assert.Single(Warnings(logs));
        Assert.Equal(message, warning.Message);
        Assert.Equal("5d30414a", warning.TriggerId);
        Assert.Equal("3.15.0004", Detail(recorder, "Firmware"));
        Assert.DoesNotContain(logs.Logs, log => log.Message.Contains("*Q"));
    }

    [Fact]
    public async Task HandleResponse_ErrorAfterTheReadIsAnswered_GoesToTheCommandSentAfterIt()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        Poll(recorder);
        recorder.Stop();

        Reply(client, StoppedInformation);
        Reply(client, "Bld3.15.0004\r\n");
        Reply(client, "E22\r\n");

        Assert.Equal("The SMP returned an error: E22 (Busy) to Esc Y0RCDR", Assert.Single(Warnings(logs)).Message);
        Assert.Equal("3.15.0004", Detail(recorder, "Firmware"));
        Assert.Equal(IssueStatus.Momentary, Assert.Single(recorder.GetIssues()).Status);
    }

    [Fact]
    public async Task HandleResponse_MarkerRejected_RaisesAMomentaryIssue()
    {
        var (recorder, client, _) = await ConnectedRecorderAsync();
        recorder.AddMarker();

        Reply(client, "E14\r\n");

        var issue = Assert.Single(recorder.GetIssues());
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal("The SMP returned an error: E14 (Not valid for this configuration) to Esc BRCDR", issue.Message);
    }

    [Fact]
    public async Task HandleResponse_PollRejected_LogsAWarningNamingTheCommandAndRaisesNoIssue()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        Poll(recorder);

        Reply(client, "E22\r\n");

        Assert.Equal("The SMP returned E22 (Busy) to I", Assert.Single(Warnings(logs)).Message);
        Assert.Empty(recorder.GetIssues());
    }

    [Fact]
    public async Task HandleResponse_VerboseModeRejected_LogsAWarningNamingTheCommandAndRaisesNoIssue()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        SetConnection(client, ConnectionState.Disconnected);
        SetConnection(client, ConnectionState.Connected);
        _time.Advance(VerboseModeDelay);

        Reply(client, "E10\r\n");

        Assert.Equal("The SMP returned E10 (Unrecognized command) to Esc 3CV", Assert.Single(Warnings(logs)).Message);
        Assert.Empty(recorder.GetIssues());
    }

    [Fact]
    public async Task HandleResponse_UnsolicitedNoticesMidSequence_UpdateStateAndTakeNoCommandsReply()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        recorder.Stop();
        Poll(recorder);

        Reply(client, "RcdrN1*usbrear\r\n");
        Reply(client, "RcdrY0\r\n");
        Reply(client, StoppedInformation);
        Reply(client, "RecStart05\r\n");
        Reply(client, "E13\r\n");

        Assert.Equal(ConnectionState.Connected, recorder.RearUsbConnectionState);
        Assert.Equal(TransportState.PreparingToRecord, recorder.TransportState);
        Assert.Contains(new DeviceDetail("Firmware", "Not supported: E13", DetailTone.Normal), recorder.Details);
        Assert.Empty(Warnings(logs));
        Assert.Empty(recorder.GetIssues());
    }

    [Fact]
    public async Task HandleConnectionState_Disconnect_ForgetsTheCommandsAwaitingAReply()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        recorder.Record();

        SetConnection(client, ConnectionState.Disconnected);
        Reply(client, "E14\r\n");

        Assert.Equal("The SMP returned E14 (Not valid for this configuration) with no command awaiting a reply",
            Assert.Single(Warnings(logs)).Message);
    }

    [Fact]
    public async Task Send_MoreCommandsAwaitingAReplyThanTheTrackerHolds_LogsTheDroppedCommand()
    {
        var (recorder, _, logs) = await ConnectedRecorderAsync();

        for (var marker = 0; marker <= SentCommandTracker<string>.MaxCommands; marker++)
            recorder.AddMarker();

        Assert.Equal("Dropped Esc BRCDR: more than 256 commands awaiting a reply", Assert.Single(Warnings(logs)).Message);
    }

    [Fact]
    public async Task Poll_UnansweredCoreQuery_LogsItAtDebugAndRaisesNoIssue()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();
        Poll(recorder);
        Reply(client, "Bld3.15.0004\r\n");

        _time.Advance(PollInterval);
        Poll(recorder);

        Assert.Contains(logs.Logs, log => log.Level == LogLevel.Debug && log.Message == "The SMP gave no reply to I within 1 s");
        Assert.Empty(recorder.GetIssues());
    }

    [Theory]
    [InlineData("Bld3.15.0004\r\n", "Firmware", "3.15.0004")]
    [InlineData("Pno60-1324-01\r\n", "Part Number", "60-1324-01")]
    [InlineData("Inf01*SMP 351\r\n", "Model", "SMP 351")]
    [InlineData("Inf99*A13VE3R\r\n", "Serial", "A13VE3R")]
    [InlineData("Inf98*00:05:A6:12:34:56\r\n", "MAC", "00:05:A6:12:34:56")]
    [InlineData("20Stat 45.2C\r\n", "Temperature", "45.2C")]
    public void HandleResponse_ReadReply_SetsTheDetail(string response, string label, string value)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(value, Detail(label));
        Assert.Empty(_recorder.GetOngoingIssues());
        Assert.Equal(CommunicationState.Okay, _recorder.CommunicationState);
    }

    [Theory]
    [InlineData("Bld\r\n", "Firmware", "Bld")]
    [InlineData("Inf99*\r\n", "Serial", "Inf99*")]
    [InlineData("20Stat \r\n", "Temperature", "20Stat ")]
    [InlineData("Inf39*\r\n", "Alarms", "Inf39*")]
    public void HandleResponse_ReadReplyWithNoValue_ShowsTheRawReplyWithNoIssue(string response, string label, string raw)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(new DeviceDetail(label, raw), _recorder.Details.Single(detail => detail.Label == label));
        Assert.Empty(_recorder.GetIssues());
    }

    [Fact]
    public void HandleResponse_ActiveAlarms_RaiseAnIssueEachBySeverity()
    {
        // From the manual (SMP 300 Series UG 68-2238-01 P): 39I form p. 116, alarm names p. 50, levels p. 88. Not a capture.
        const string alarms = "[name:Disk Error],[level:Notify],[name:Audio Loss],[level:Display],[name:NTP Sync],[level:Log],[name:HDCP Video],[level:Disabled]";

        _mockClient.Object.ResponseHandlers!.Invoke($"Inf39*{alarms}\r\n");

        Assert.Equal(alarms, Detail("Alarms"));
        Assert.Equal(DetailTone.Warning, _recorder.Details.Single(detail => detail.Label == "Alarms").Tone);
        Assert.Equal(IssueSeverity.Major, Ongoing("alarm:Disk Error")!.Severity);
        Assert.Equal(IssueSeverity.Minor, Ongoing("alarm:Audio Loss")!.Severity);
        Assert.Equal(IssueSeverity.Minor, Ongoing("alarm:NTP Sync")!.Severity);
        Assert.Null(Ongoing("alarm:HDCP Video"));
        Assert.Equal("The SMP reports the Disk Error alarm at level Notify", Ongoing("alarm:Disk Error")!.Message);
    }

    [Fact]
    public void HandleResponse_DisabledAlarm_ShowsInTheDetailOnlyAndResolvesItsIssue()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Inf39*[name:Disk Error],[level:Notify]\r\n");
        _mockClient.Object.ResponseHandlers!.Invoke("Inf39*[name:Disk Error],[level:Disabled]\r\n");

        Assert.Equal(new DeviceDetail("Alarms", "[name:Disk Error],[level:Disabled]", DetailTone.Normal),
            _recorder.Details.Single(detail => detail.Label == "Alarms"));
        Assert.Empty(_recorder.GetOngoingIssues());
    }

    [Theory]
    [InlineData("notify", IssueSeverity.Major)]
    [InlineData("NOTIFY", IssueSeverity.Major)]
    [InlineData("display", IssueSeverity.Minor)]
    public void HandleResponse_AlarmLevelInAnotherCase_IsStillRecognised(string level, IssueSeverity severity)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"Inf39*[name:Disk Error],[level:{level}]\r\n");

        Assert.Equal(severity, Ongoing("alarm:Disk Error")!.Severity);
        Assert.Equal($"The SMP reports the Disk Error alarm at level {level}", Ongoing("alarm:Disk Error")!.Message);
    }

    [Fact]
    public void HandleResponse_AlarmAtALevelTheManualDoesNotList_ShowsTheRawLevelWithAMinorIssue()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Inf39*[name:Disk Error],[level:emergency]\r\n");

        Assert.Equal("[name:Disk Error],[level:emergency]", Detail("Alarms"));
        Assert.Equal(IssueSeverity.Minor, Ongoing("alarm:Disk Error")!.Severity);
        Assert.Equal("The SMP reports the Disk Error alarm at a level its manual doesn't list: emergency",
            Ongoing("alarm:Disk Error")!.Message);
    }

    [Fact]
    public void HandleResponse_AlarmsCleared_ResolvesOnlyTheClearedAlarms()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Inf39*[name:Video Loss],[level:Display],[name:Audio Loss],[level:Display]\r\n");
        _mockClient.Object.ResponseHandlers!.Invoke("Inf39*[name:Audio Loss],[level:Display]\r\n");

        Assert.Null(Ongoing("alarm:Video Loss"));
        Assert.NotNull(Ongoing("alarm:Audio Loss"));

        _mockClient.Object.ResponseHandlers!.Invoke("Inf39*None active\r\n");

        Assert.Empty(_recorder.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("Alarms", "None active", DetailTone.Normal), _recorder.Details.Single(detail => detail.Label == "Alarms"));
    }

    [Fact]
    public async Task HandleResponse_UnrecognisedAlarmList_IsAskedAgainAndRaisesTheReadIssueWithTheRawReplyOnTheThirdInARow()
    {
        const string garbled = "Inf39*<name:video_loss,level:warning>";
        var (recorder, client, _) = await ConnectedRecorderAsync();

        for (var poll = 0; poll < MaxPolls && Sends(client, "39I") < 2; poll++)
            PollAndReply(recorder, client, "39I", $"{garbled}\r\n");

        Assert.Null(Ongoing(recorder, "alarms-read"));

        for (var poll = 0; poll < MaxPolls && Sends(client, "39I") < 3; poll++)
            PollAndReply(recorder, client, "39I", $"{garbled}\r\n");

        Assert.Equal($"The SMP did not give a real reply to 39I 3 times in a row, the last was {garbled}",
            Ongoing(recorder, "alarms-read")!.Message);
        Assert.Equal(IssueSeverity.Minor, Ongoing(recorder, "alarms-read")!.Severity);

        for (var poll = 0; poll < MaxPolls && Sends(client, "39I") < 4; poll++)
            PollAndReply(recorder, client, "39I", "Inf39*None active\r\n");

        Assert.Null(Ongoing(recorder, "alarms-read"));
        Assert.Equal("None active", Detail(recorder, "Alarms"));
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>*<41:28:03*00:00:00>\r\n",
        "internal: 116606760, auto: N/A", "internal: 41:28:03, auto: 00:00:00")]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*N/A>*<116606580*N/A>*<00:00:06>*<824:27:00*00:00:00>\r\n",
        "internal: 116606580, N/A: N/A", "internal: 824:27:00, N/A: 00:00:00")]
    [InlineData("Inf*<ChA1*ChB3>*<stopped>*<usbfront>*<23855700>*<00:00:00>*<86:49:20>\r\n",
        "usbfront: 23855700", "usbfront: 86:49:20")]
    public void HandleResponse_Information_SetsTheStorageDetails(string response, string freeSpace, string timeRemaining)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(freeSpace, Detail("Free Space (KB)"));
        Assert.Equal(timeRemaining, Detail("Record Time Remaining"));
        Assert.Empty(_recorder.GetOngoingIssues());
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760>*<00:00:00>*<41:28:03*00:00:00>\r\n",
        "Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760>*<00:00:00>*<41:28:03*00:00:00>", "internal: 41:28:03, auto: 00:00:00")]
    [InlineData("Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>\r\n",
        "internal: 116606760, auto: N/A", "Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>")]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*N/A>\r\n",
        "Inf*<ChA1*ChB3>*<recording>*<internal*N/A>", "Inf*<ChA1*ChB3>*<recording>*<internal*N/A>")]
    public void HandleResponse_InformationWithFewerFields_ShowsWhatItHasAndTheRawReplyAtDebugWithNoIssue(string response,
        string freeSpace, string timeRemaining)
    {
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "short"), out var recorder);

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(freeSpace, Detail(recorder, "Free Space (KB)"));
        Assert.Equal(timeRemaining, Detail(recorder, "Record Time Remaining"));
        Assert.Empty(recorder.GetIssues());
        Assert.Contains(logs.Logs, log => log.Level == LogLevel.Debug && log.Message.Contains(response.Trim()));
    }

    [Fact]
    public void HandleResponse_RepeatedInformation_ShowsTheStorageOnceAndAChangedReplyAgain()
    {
        const string shortReply = "Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>\r\n";
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "repeated"), out var recorder);

        Reply(client, shortReply);
        Reply(client, shortReply);

        Assert.Single(logs.Logs, log => log.Level == LogLevel.Debug && log.Message.Contains(shortReply.Trim()));

        Reply(client, StoppedInformation);

        Assert.Equal("internal: 116606760, auto: N/A", Detail(recorder, "Free Space (KB)"));
        Assert.Equal("internal: 41:28:03, auto: 00:00:00", Detail(recorder, "Record Time Remaining"));
    }

    [Theory]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:49:20*0\r\n", "Rear USB", "usbrear/232: 93MB used of 238557MB, 238464MB free")]
    [InlineData("Inf56*N/A\r\n", "Front USB", "N/A")]
    [InlineData("Inf58*usbrcp/1*10MB*100MB*90MB*01:00:00*0,usbrcp/2*20MB*200MB*180MB*02:00:00*0\r\n", "RCP USB",
        "usbrcp/1: 10MB used of 100MB, 90MB free, usbrcp/2: 20MB used of 200MB, 180MB free")]
    public void HandleResponse_UsbStatus_SetsTheStorageDetail(string response, string label, string value)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(value, Detail(label));
    }

    [Fact]
    public void HandleResponse_MalformedUsbStatus_LogsAWarningAndShowsTheRawStatusWithNoIssue()
    {
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "usb"), out var recorder);

        Reply(client, "Inf57*usbrear/232*93MB*238557MB\r\n");

        Assert.Equal("Unexpected Rear USB status usbrear/232*93MB*238557MB", Assert.Single(Warnings(logs)).Message);
        Assert.Equal("usbrear/232*93MB*238557MB", Detail(recorder, "Rear USB"));
        Assert.Empty(recorder.GetIssues());
    }

    [Fact]
    public void HandleResponse_InformationWithTooFewFieldsForTheRecordingState_LogsAWarningWithTheRawReplyAndNoIssue()
    {
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "short"), out var recorder);

        Reply(client, "Inf*<ChA1*ChB3>*<recording>*<internal*N/A>\r\n");

        Assert.Equal("Unexpected information response Inf*<ChA1*ChB3>*<recording>*<internal*N/A>", Assert.Single(Warnings(logs)).Message);
        Assert.Equal(TransportState.Unknown, recorder.TransportState);
        Assert.Empty(recorder.GetIssues());
    }

    [Fact]
    public async Task Poll_AfterVerboseMode_SendsOneReadPerPollInOrder()
    {
        var (recorder, client, _) = await ConnectedRecorderAsync();

        for (var poll = 0; poll < 7; poll++)
            Poll(recorder);

        Assert.Equal(["*Q", "N", "1I", "99I", "98I", TemperatureRead, "39I"], Reads(client));
        Assert.Equal(7, Sends(client, "I"));
    }

    [Fact]
    public async Task Poll_UnansweredRead_IsAskedAgainAndRaisesItsIssueOnTheThirdMiss()
    {
        var (recorder, client, _) = await ConnectedRecorderAsync();

        for (var poll = 0; poll < MaxPolls && Sends(client, "*Q") < 3; poll++)
            PollAndReply(recorder, client, "*Q", null);

        Assert.Equal(3, Sends(client, "*Q"));
        Assert.Empty(recorder.GetOngoingIssues());

        Poll(recorder);

        var issue = Assert.Single(recorder.GetOngoingIssues());
        Assert.Equal("firmware-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The SMP did not give a real reply to *Q 3 times in a row, the last was no reply", issue.Message);

        client.Object.ResponseHandlers!.Invoke("Bld3.15.0004\r\n");

        Assert.Empty(recorder.GetOngoingIssues());
        Assert.Equal("3.15.0004", Detail(recorder, "Firmware"));
    }

    [Fact]
    public async Task Poll_ReadAnsweredBeforeTheNextPoll_RaisesNoIssue()
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();

        Poll(recorder);
        Reply(client, "Bld3.15.0004\r\n");
        _time.Advance(PollInterval);
        Poll(recorder);

        Assert.Empty(recorder.GetOngoingIssues());
        Assert.DoesNotContain(logs.Logs, log => log.Message.Contains("*Q"));
    }

    [Theory]
    [InlineData("E10", "E10 (Unrecognized command)")]
    [InlineData("E13", "E13 (Invalid parameter)")]
    [InlineData("E14", "E14 (Not valid for this configuration)")]
    public async Task HandleResponse_ReadRejected_ShowsItIsNotSupportedWithNoIssueAndIsNotAskedAgain(string error, string logged)
    {
        var (recorder, client, logs) = await ConnectedRecorderAsync();

        Poll(recorder);
        Reply(client, StoppedInformation);
        Reply(client, $"{error}\r\n");
        _time.Advance(PollInterval);
        for (var poll = 0; poll < MaxPolls; poll++)
            PollAndReply(recorder, client, "*Q", "Bld3.15.0004\r\n");

        Assert.Contains(new DeviceDetail("Firmware", $"Not supported: {error}", DetailTone.Normal), recorder.Details);
        Assert.Empty(recorder.GetIssues());
        Assert.Equal(CommunicationState.Okay, recorder.CommunicationState);
        Assert.Equal(1, Sends(client, "*Q"));
        Assert.Single(logs.Logs, log => log.Level == LogLevel.Information && log.Message == $"The SMP does not support *Q: {logged}");
        Assert.Empty(Warnings(logs));
    }

    [Fact]
    public async Task HandleResponse_BusyReplyToAHealthRead_CountsAsAMissAndIsAskedAgainAtTheNextHealthRead()
    {
        var (recorder, client, _) = await ConnectedRecorderAsync();

        for (var poll = 0; poll < MaxPolls && Sends(client, TemperatureRead) < 2; poll++)
            PollAndReply(recorder, client, TemperatureRead, "E22\r\n");

        Assert.Null(Ongoing(recorder, "temperature-read"));

        for (var poll = 0; poll < MaxPolls && Sends(client, TemperatureRead) < 3; poll++)
            PollAndReply(recorder, client, TemperatureRead, "E22\r\n");

        Assert.Equal("The SMP did not give a real reply to Esc 20STAT 3 times in a row, the last was E22 (Busy)",
            Ongoing(recorder, "temperature-read")!.Message);
        Assert.Equal(IssueSeverity.Minor, Ongoing(recorder, "temperature-read")!.Severity);

        for (var poll = 0; poll < MaxPolls && Sends(client, TemperatureRead) < 4; poll++)
            PollAndReply(recorder, client, TemperatureRead, "20Stat 45.2C\r\n");

        Assert.Null(Ongoing(recorder, "temperature-read"));
        Assert.Equal("45.2C", Detail(recorder, "Temperature"));
    }

    [Fact]
    public async Task Poll_HealthReads_AreRepeatedEveryTenPollsExceptARejectedOne()
    {
        var (recorder, client, _) = await ConnectedRecorderAsync();

        for (var poll = 0; poll < MaxPolls && Sends(client, "39I") < 2; poll++)
            PollAndReply(recorder, client, TemperatureRead, "E10\r\n");

        Assert.Equal(2, Sends(client, "39I"));
        Assert.Equal(1, Sends(client, TemperatureRead));
        Assert.Equal("Not supported: E10", Detail(recorder, "Temperature"));
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*N/A>\r\n", "Inf*<ChA1*ChB3>*<recording>*<internal*N/A>")]
    [InlineData("RcdrYx\r\n", "RcdrYx")]
    [InlineData("Inf57*usbrear/232*93MB*238557MB\r\n", "usbrear/232*93MB*238557MB")]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:49\r\n", "238464MB*86:49")]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:4x:20*0\r\n", "86:4x:20*0")]
    public void HandleResponse_LogsAMalformedResponseAsAWarningAndChangesNoState(string response, string logged)
    {
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "malformed"),
            out var recorder);
        var rearUsbHandler = new Mock<ConnectionStateHandler>();
        recorder.RearUsbConnectionStateHandlers += rearUsbHandler.Object;

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(TransportState.Unknown, recorder.TransportState);
        Assert.Equal(ConnectionState.Unknown, recorder.RearUsbConnectionState);
        rearUsbHandler.Verify(x => x.Invoke(It.IsAny<ConnectionState>()), Times.Never);
        Assert.Contains(Warnings(logs), log => log.Message.Contains(logged));
        Assert.Empty(recorder.GetIssues());
    }
}