using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Dsp.Tests;

public class YamahaXmvTest
{
    // Invented replies, built from the formats and examples in Yamaha's XMV protocol spec: no XMV has been captured. Dsp/README.md
    // ("YamahaXmv test fixtures") says which formats the spec gives no example of.
    private const string RunModeNormal = "OK devstatus runmode \"normal\"";
    private const string RunModeUpdate = "OK devstatus runmode \"update\"";
    private const string RunModeNormalNotification = "NOTIFY devstatus runmode \"normal\"";
    private const string NoAlerts = "OK devstatus error \"none\"";
    private const string DcpErrorAlert = "err/DCP[0] communication error// x53 on (1) ID-001 2013/1/22 11:38:23";
    private const string PowerSupplyFault = "flt/PS temperature// x101 on (1) ID-001 2013/1/22 11:38:23";
    private const string PowerSupplyFaultOff = "flt/PS temperature// x101 off (1) ID-001 2013/1/22 11:52:40";
    private const string PowerSupplyProtection = "err/PS protection// x103 on (1) ID-001 2013/1/22 11:38:23";
    private const string ProtectionMuteA = "err/Protection[chA]// x104 on (1) ID-001 2013/1/22 11:38:23";
    private const string ProtectionMuteB = "err/Protection[chB]// x104 on (1) ID-001 2013/1/22 11:39:10";
    private const string ProtectionMuteAOff = "err/Protection[chA]// x104 off (1) ID-001 2013/1/22 11:45:00";
    private const string WordClockError = "err/Word clock unlock// x21 on (1) ID-001 2013/1/22 11:40:00";
    private const string LaterPowerSupplyFault = "flt/PS temperature// x101 on (1) ID-001 2013/1/22 11:50:00";
    private const string LaterPowerSupplyFaultOff = "flt/PS temperature// x101 off (1) ID-001 2013/1/22 11:50:00";
    private const string LaterHeatSinkWarning = "wrn/Heat sink temperature// x106 on (1) ID-001 2013/1/22 11:45:00";
    private const string LaterWordClockError = "err/Word clock unlock// x21 on (1) ID-001 2013/1/22 11:51:00";
    private const string LatestDcpErrorAlert = "err/DCP[0] communication error// x53 on (1) ID-001 2013/1/22 11:52:00";
    private const string HeatSinkWarning = "wrn/Heat sink temperature// x106 on (1) ID-001 2013/1/22 11:38:23";
    private const string PowerOnInformation = "inf/Power on// x200 on (1) ID-001 2013/1/22 11:38:23";
    private const string GarbledAlert = "flt/PS temperature x101 on";
    private const string Model = "OK devinfo productname \"XMV4140\"";
    private const string Firmware = "OK devinfo version \"1.0.0\"";
    private const string Serial = "OK devinfo serialno \"ZA37640CHNET101001\"";
    private const string DeviceName = "OK devinfo devicename \"Amp Rack 1\"";
    private const string UnitId = "OK devinfo deviceid \"001\"";
    private const string ProtocolVersion = "OK devinfo protocolver \"1.0.0\"";
    private const string ParameterSetVersion = "OK devinfo paramsetver \"MTX:1.2.0\"";
    private const string SamplingRate = "OK devstatus fs \"44.1kHz\"";
    private const string WordClockLocked = "OK devstatus lockstatus \"lock\"";
    private const string LimiterStarted = "OK mtrstart MTX:mtr_512/80001/meter";
    private const string LimiterAllOff = "NOTIFY mtr MTX:mtr_512/80001/meter raw 00 00 00 00";
    private const string LimiterOnB = "NOTIFY mtr MTX:mtr_512/80001/meter raw 00 01 00 00";
    private const string LimiterOnBAndD = "NOTIFY mtr MTX:mtr_512/80001/meter raw 00 01 00 01";
    private const string PowerSupplyFaultKey = "alert:101:PS temperature";
    private const string WordClockErrorKey = "alert:21:Word clock unlock";

    private const string RunModeQuery = "devstatus runmode\n";
    private const string AlertQuery = "devstatus error\n";
    private const string LimiterRequest = "mtrstart MTX:mtr_512/80001/meter 1000\n";
    private const string AlertLogCountQuery = "event MTX:EventLogGetLogNumber \"\"\n";
    private const string LevelA = "MTX:mem_512/1/4/0/0/0/0";
    private const string MuteB = "MTX:mem_512/1/4/1/0/2/0";
    private const string PolarityC = "MTX:mem_512/1/4/2/0/3/0";
    private const string InputSelect = "MTX:mem_512/1/7/0/0/0/0";
    private const string Power = "MTX:mem_512/1/6/0/0/0/0";
    private const string LevelE = "MTX:mem_512/1/4/4/0/0/0";
    private const string MissingControlsLabel = "Missing Controls";
    private const int NeverPollMs = 3_600_000;

    private readonly FakeTimeProvider _time = new();
    private readonly TestTcpClient _client = new();
    private readonly Mock<VolumeLevelHandler> _levelHandler = new();
    private readonly Mock<MuteStateHandler> _muteHandler = new();
    private readonly Mock<StringValueHandler> _stringHandler = new();
    private readonly TestableYamahaXmv _amp;

    public YamahaXmvTest()
    {
        _client.SetConnectionState(ConnectionState.Connected);
        _amp = new TestableYamahaXmv(_client, _time);
    }

    private void Receive(string line) => _client.Reply(line + "\n");

    private void Run()
    {
        Receive(RunModeNormal);
        Receive(NoAlerts);
    }

    private List<string> SentAfter(int count) => _client.Sent.Skip(count).ToList();

    private DeviceDetail? Detail(string label) => _amp.Details.SingleOrDefault(detail => detail.Label == label);

    private Issue? Ongoing(string key) => _amp.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    private void Reconnect()
    {
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        Receive(RunModeNormal);
    }

    private void ReadTheAlertLog(params string[] entries)
    {
        Receive($"OK event MTX:EventLogGetLogNumber \"lognum={entries.Length}\"");
        Receive(entries.Length == 1
            ? $"OK event MTX:EventLogGetLog \"log0={entries[0]}\""
            : $"OK event MTX:EventLogGetLogList \"logindex=0-{entries.Length - 1}{string.Concat(entries.Select((entry, index) => $"|log{index}={entry}"))}\"");
    }

    private async Task PollAnsweringTheRunMode()
    {
        await _amp.PollNow();
        Receive(RunModeNormal);
    }

    [Fact]
    public void Connecting_SendsOnlyTheRunModeQuery()
    {
        Assert.Equal([RunModeQuery], _client.Sent);
    }

    [Fact]
    public void RunModeNormal_ReadsTheAlertStatusIdentityClockLimiterPowerAndEachRegisteredControl()
    {
        _amp.AddControl(_levelHandler.Object, "A");
        _amp.AddControl(_muteHandler.Object, "B");
        _amp.AddControl(_stringHandler.Object, "C Polarity");

        Receive(RunModeNormal);

        Assert.Equal(
        [
            RunModeQuery, AlertQuery, "devinfo productname\n", "devinfo version\n", "devinfo serialno\n", "devinfo devicename\n",
            "devinfo deviceid\n", "devinfo protocolver\n", "devinfo paramsetver\n", "devstatus fs\n", "devstatus lockstatus\n",
            AlertLogCountQuery, LimiterRequest, $"get {Power} 0 0\n", $"get {LevelA} 0 0\n", $"get {MuteB} 0 0\n", $"get {PolarityC} 0 0\n"
        ], _client.Sent);
    }

    [Fact]
    public async Task RunModeUpdate_RaisesTheCriticalRunModeIssueWithTheRawReply_AndPollsOnlyTheRunMode()
    {
        Receive(RunModeUpdate);
        await _amp.PollNow();

        var issue = Ongoing(YamahaXmv.RunModeIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue?.Severity);
        Assert.EndsWith(RunModeUpdate, issue?.Message);
        Assert.Equal(new DeviceDetail("Run Mode", "update", DetailTone.Error), Detail("Run Mode"));
        Assert.Equal([RunModeQuery, RunModeQuery], _client.Sent);
    }

    [Fact]
    public void RunModeNormalAfterUpdate_ResolvesTheRunModeIssue_AndReadsTheAmp()
    {
        Receive(RunModeUpdate);
        var sentBefore = _client.Sent.Count;

        Receive(RunModeNormalNotification);

        Assert.Null(Ongoing(YamahaXmv.RunModeIssueKey));
        Assert.Equal(AlertQuery, SentAfter(sentBefore).First());
    }

    [Fact]
    public async Task ThreeUnansweredRunModeQueries_SetCommunicationErrorAndReconnect()
    {
        await _amp.PollNow();
        await _amp.PollNow();
        Assert.Equal(0, _client.Reconnects);

        await _amp.PollNow();

        Assert.Equal(1, _client.Reconnects);
        Assert.Equal(CommunicationState.Error, _amp.CommunicationState);
        Assert.Equal(IssueSeverity.Major, Ongoing(YamahaXmv.UnansweredRunModeIssueKey)?.Severity);
    }

    [Fact]
    public async Task AnsweredRunModeQueries_NeverReconnect()
    {
        for (var poll = 0; poll < YamahaXmv.MaxUnansweredRunModeQueries + 1; poll++)
        {
            Receive(RunModeNormal);
            await _amp.PollNow();
        }

        Assert.Equal(0, _client.Reconnects);
        Assert.DoesNotContain(_amp.GetIssues(), issue => issue.Key == YamahaXmv.UnansweredRunModeIssueKey);
        Assert.Equal(CommunicationState.Okay, _amp.CommunicationState);
    }

    [Fact]
    public async Task Poll_WhileDisconnected_SendsNothing()
    {
        _client.SetConnectionState(ConnectionState.Disconnected);
        var sentBefore = _client.Sent.Count;

        await _amp.PollNow();

        Assert.Empty(SentAfter(sentBefore));
    }

    [Fact]
    public async Task Poll_OnceRunning_SendsTheRunModeAlertStatusAndLimiterQueries()
    {
        Run();
        Receive(Model);
        Receive(Firmware);
        Receive(Serial);
        Receive(DeviceName);
        Receive(UnitId);
        Receive(ProtocolVersion);
        Receive(ParameterSetVersion);
        Receive(SamplingRate);
        Receive(WordClockLocked);
        var sentBefore = _client.Sent.Count;

        await _amp.PollNow();

        Assert.Equal([RunModeQuery, AlertQuery, LimiterRequest], SentAfter(sentBefore));
    }

    [Theory]
    [InlineData(PowerSupplyFault, PowerSupplyFaultKey, IssueSeverity.Critical)]
    [InlineData(DcpErrorAlert, "alert:53:DCP[0] communication error", IssueSeverity.Major)]
    [InlineData(HeatSinkWarning, "alert:106:Heat sink temperature", IssueSeverity.Minor)]
    public void AnAlertTurningOn_RaisesAnOngoingIssueKeyedByItsNumberAndMessage_AtTheSeverityOfItsType_ShowingTheRawAlert(
        string alert, string key, IssueSeverity severity)
    {
        Run();

        Receive($"NOTIFY devstatus error \"{alert}\"");

        var issue = Ongoing(key);
        Assert.Equal(severity, issue?.Severity);
        Assert.EndsWith(alert, issue?.Message);
    }

    [Fact]
    public void AnAlertStatusReply_RaisesTheAlertItReports_AndShowsItInTheAlertDetail()
    {
        Receive(RunModeNormal);

        Receive($"OK devstatus error \"{DcpErrorAlert}\"");

        Assert.Equal(IssueSeverity.Major, Ongoing("alert:53:DCP[0] communication error")?.Severity);
        Assert.Equal(new DeviceDetail("Alert", DcpErrorAlert, DetailTone.Error), Detail("Alert"));
    }

    [Fact]
    public void AnAlertTurningOff_ResolvesThatAlertsIssueOnly()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{PowerSupplyFault}\"");
        Receive($"NOTIFY devstatus error \"{DcpErrorAlert}\"");

        Receive($"NOTIFY devstatus error \"{PowerSupplyFaultOff}\"");

        Assert.Null(Ongoing(PowerSupplyFaultKey));
        Assert.NotNull(Ongoing("alert:53:DCP[0] communication error"));
    }

    [Fact]
    public void AnAlertStatusOfNone_ResolvesEveryAlertIssue()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{PowerSupplyFault}\"");
        Receive($"NOTIFY devstatus error \"{HeatSinkWarning}\"");
        Receive($"NOTIFY devstatus error \"{GarbledAlert}\"");

        Receive(NoAlerts);

        Assert.Empty(_amp.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("Alert", "none"), Detail("Alert"));
    }

    [Fact]
    public void AFaultThatClearsWhileDisconnected_IsResolvedWhenTheReconnectReadsNoAlerts()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{PowerSupplyFault}\"");
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        Assert.NotNull(Ongoing(PowerSupplyFaultKey));

        var sentBefore = _client.Sent.Count;
        Receive(RunModeNormal);
        Assert.Contains(AlertQuery, SentAfter(sentBefore));
        Receive(NoAlerts);

        Assert.Null(Ongoing(PowerSupplyFaultKey));
    }

    [Fact]
    public void AFaultRaisedWhileDisconnected_IsRaisedWhenTheReconnectReadsIt()
    {
        Run();
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);

        Receive(RunModeNormal);
        Receive($"OK devstatus error \"{PowerSupplyFault}\"");

        Assert.Equal(IssueSeverity.Critical, Ongoing(PowerSupplyFaultKey)?.Severity);
    }

    [Fact]
    public void AnAlertOfATypeTheSpecDoesNotList_RaisesAMomentaryMinorIssueWithTheRawText()
    {
        Run();

        Receive($"NOTIFY devstatus error \"{PowerOnInformation}\"");

        var issue = Assert.Single(_amp.GetIssues(), issue => issue.Key == "alert:200:Power on");
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.EndsWith(PowerOnInformation, issue.Message);
    }

    [Fact]
    public void AnUnreadableAlert_RaisesTheUnreadableAlertIssueWithTheRawText_UntilTheAmpReportsNone()
    {
        Run();

        Receive($"NOTIFY devstatus error \"{GarbledAlert}\"");
        var issue = Ongoing(YamahaXmv.UnreadableAlertIssueKey);
        Receive(NoAlerts);

        Assert.Equal(IssueSeverity.Major, issue?.Severity);
        Assert.EndsWith(GarbledAlert, issue?.Message);
        Assert.Null(Ongoing(YamahaXmv.UnreadableAlertIssueKey));
    }

    [Theory]
    [InlineData(PowerSupplyProtection, "alert:103:PS protection")]
    [InlineData(ProtectionMuteA, "alert:104:Protection[chA]")]
    public void TheProtectionAlerts103And104_AreRaisedAsCritical_AboveTheirErrorType(string alert, string key)
    {
        Run();

        Receive($"NOTIFY devstatus error \"{alert}\"");

        Assert.Equal(IssueSeverity.Critical, Ongoing(key)?.Severity);
    }

    [Fact]
    public void TwoChannelsWithTheSameAlertNumber_AreTwoIssues_AndOneTurningOffLeavesTheOther()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{ProtectionMuteA}\"");
        Receive($"NOTIFY devstatus error \"{ProtectionMuteB}\"");

        Receive($"NOTIFY devstatus error \"{ProtectionMuteAOff}\"");

        Assert.Null(Ongoing("alert:104:Protection[chA]"));
        Assert.EndsWith(ProtectionMuteB, Ongoing("alert:104:Protection[chB]")?.Message);
    }

    [Fact]
    public void AnUnknownTypeAlertTheAmpKeepsReporting_IsRaisedOncePerOccurrence()
    {
        Run();
        const string laterOccurrence = "inf/Power on// x200 on (1) ID-001 2013/1/22 12:00:00";

        Receive($"OK devstatus error \"{PowerOnInformation}\"");
        Receive($"OK devstatus error \"{PowerOnInformation}\"");
        Receive($"OK devstatus error \"{laterOccurrence}\"");

        Assert.Equal(2, Assert.Single(_amp.GetIssues(), issue => issue.Key == "alert:200:Power on").OccurrenceCount);
    }

    [Fact]
    public void AnAlertThatClearedWhileDisconnected_IsResolvedFromTheAlertLog_WhileAnotherAlertIsStillOn()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{PowerSupplyFault}\"");
        Receive($"NOTIFY devstatus error \"{WordClockError}\"");
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        Receive(RunModeNormal);
        Receive($"OK devstatus error \"{WordClockError}\"");

        Receive("OK event MTX:EventLogGetLogNumber \"lognum=3\"");
        Assert.Equal("event MTX:EventLogGetLogList \"logindex=0-2\"\n", _client.Sent[^1]);
        Receive($"OK event MTX:EventLogGetLogList \"logindex=0-2|log0={PowerSupplyFault}|log1={WordClockError}|log2={PowerSupplyFaultOff}\"");

        Assert.Null(Ongoing(PowerSupplyFaultKey));
        Assert.NotNull(Ongoing(WordClockErrorKey));
        Assert.Equal(new DeviceDetail("Alert Log", "3 entries, oldest first"), Detail("Alert Log"));
    }

    [Fact]
    public void AnAlertThatCameOnWhileDisconnected_IsRaisedFromTheAlertLog()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{WordClockError}\"");
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        Receive(RunModeNormal);
        Receive($"OK devstatus error \"{WordClockError}\"");

        Receive("OK event MTX:EventLogGetLogNumber \"lognum=2\"");
        Receive($"OK event MTX:EventLogGetLogList \"logindex=0-1|log0={WordClockError}|log1={LaterPowerSupplyFault}\"");

        Assert.Equal(IssueSeverity.Critical, Ongoing(PowerSupplyFaultKey)?.Severity);
    }

    [Fact]
    public void OnTheFirstConnection_AnAlertLogEntryThatIsStillOn_IsNotRaised()
    {
        Receive(RunModeNormal);
        Receive($"OK devstatus error \"{WordClockError}\"");

        Receive("OK event MTX:EventLogGetLogNumber \"lognum=1\"");
        Assert.Equal("event MTX:EventLogGetLog \"logindex=0\"\n", _client.Sent[^1]);
        Receive($"OK event MTX:EventLogGetLog \"log0={LaterPowerSupplyFault}\"");

        Assert.Null(Ongoing(PowerSupplyFaultKey));
        Assert.Equal(new DeviceDetail("Alert Log", "1 entries, order unknown"), Detail("Alert Log"));
    }

    [Fact]
    public void AnAlertStatusOfNone_KeepsTheAlertLogFromRaisingAnything()
    {
        Run();
        Receive($"NOTIFY devstatus error \"{WordClockError}\"");
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        Receive(RunModeNormal);
        Receive(NoAlerts);

        Receive("OK event MTX:EventLogGetLogNumber \"lognum=1\"");
        Receive($"OK event MTX:EventLogGetLog \"log0={LaterPowerSupplyFault}\"");

        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public void TheAlertLog_IsReadTenEntriesAtATime_WithTheLastRangeOverlappingSoItStaysAscending()
    {
        Run();
        var entries = string.Concat(Enumerable.Range(0, 10).Select(index => $"|log{index}={PowerSupplyFaultOff}"));

        Receive("OK event MTX:EventLogGetLogNumber \"lognum=11\"");
        var first = _client.Sent[^1];
        Receive($"OK event MTX:EventLogGetLogList \"logindex=0-9{entries}\"");

        Assert.Equal("event MTX:EventLogGetLogList \"logindex=0-9\"\n", first);
        Assert.Equal("event MTX:EventLogGetLogList \"logindex=9-10\"\n", _client.Sent[^1]);
    }

    [Fact]
    public void ACleanAmpThatComesBackWithTwoNewFaults_RaisesTheOneTheStatusReports_AndTheOtherFromTheAlertLog()
    {
        Run();
        ReadTheAlertLog(HeatSinkWarning);
        Reconnect();

        Receive($"OK devstatus error \"{LaterWordClockError}\"");
        ReadTheAlertLog(HeatSinkWarning, LaterPowerSupplyFault, LaterWordClockError);

        Assert.Equal(IssueSeverity.Critical, Ongoing(PowerSupplyFaultKey)?.Severity);
        Assert.NotNull(Ongoing(WordClockErrorKey));
        Assert.Null(Ongoing("alert:106:Heat sink temperature"));
    }

    [Fact]
    public async Task AReinitialiseOnTheSameConnection_DoesNotRaiseAgainAnAlertThatWasRaisedAndResolvedOnIt()
    {
        Run();
        ReadTheAlertLog(DcpErrorAlert);
        Reconnect();
        Receive(NoAlerts);
        ReadTheAlertLog(DcpErrorAlert);
        Receive($"NOTIFY devstatus error \"{LaterHeatSinkWarning}\"");
        Receive(NoAlerts);
        Receive($"NOTIFY devstatus error \"{LaterWordClockError}\"");

        await _amp.Reinitialise();
        Receive($"OK devstatus error \"{LaterWordClockError}\"");
        ReadTheAlertLog(DcpErrorAlert, LaterHeatSinkWarning, LaterWordClockError);

        Assert.Null(Ongoing("alert:106:Heat sink temperature"));
        Assert.NotNull(Ongoing(WordClockErrorKey));
    }

    [Fact]
    public void ASameSecondOnAndOff_IsResolved_WhenTheLogOrderShowsTheOffIsNewer()
    {
        Run();
        ReadTheAlertLog(HeatSinkWarning);
        Receive($"NOTIFY devstatus error \"{PowerSupplyFault}\"");
        Reconnect();

        Receive($"OK devstatus error \"{LatestDcpErrorAlert}\"");
        ReadTheAlertLog(HeatSinkWarning, LaterPowerSupplyFault, LaterPowerSupplyFaultOff, LatestDcpErrorAlert);

        Assert.Null(Ongoing(PowerSupplyFaultKey));
        Assert.Equal(new DeviceDetail("Alert Log", "4 entries, oldest first"), Detail("Alert Log"));
    }

    [Fact]
    public void ASameSecondOnAndOff_LeavesTheAlertRaised_WhenTheLogOrderIsUnknown()
    {
        Run();
        ReadTheAlertLog(HeatSinkWarning);
        Reconnect();

        Receive($"OK devstatus error \"{LatestDcpErrorAlert}\"");
        ReadTheAlertLog(LaterPowerSupplyFaultOff, LaterPowerSupplyFault);

        Assert.NotNull(Ongoing(PowerSupplyFaultKey));
        Assert.Equal(new DeviceDetail("Alert Log", "2 entries, order unknown"), Detail("Alert Log"));
    }

    [Fact]
    public void AnAlertStatusOfNoneFromTheLastConnection_DoesNotStopTheAlertLogRaisingAfterTheReconnect()
    {
        Run();
        ReadTheAlertLog(HeatSinkWarning);
        Reconnect();

        ReadTheAlertLog(HeatSinkWarning, LaterPowerSupplyFault);

        Assert.Equal(IssueSeverity.Critical, Ongoing(PowerSupplyFaultKey)?.Severity);
    }

    [Fact]
    public void ARejectedAlertLog_ShowsNotSupportedWithTheRawReply_AndRaisesNothing()
    {
        Run();

        Receive("ERROR event UnknownEventID");

        Assert.Equal(new DeviceDetail("Alert Log", "Not supported: ERROR event UnknownEventID"), Detail("Alert Log"));
        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public async Task AnUnansweredAlertStatusPoll_RaisesAnIssue_ThatTheThirdInARowMakesOngoing_AndAReplyResolves()
    {
        Run();
        for (var poll = 0; poll < 3; poll++)
        {
            await PollAnsweringTheRunMode();
            _time.Advance(YamahaXmv.ReplyTimeout);
        }
        await PollAnsweringTheRunMode();
        var issue = Ongoing(YamahaXmv.UnansweredAlertStatusIssueKey);

        Receive(NoAlerts);

        Assert.Equal(IssueSeverity.Major, issue?.Severity);
        Assert.Null(Ongoing(YamahaXmv.UnansweredAlertStatusIssueKey));
    }

    [Fact]
    public void ALimiterEngaging_RaisesAMomentaryMinorIssueForThatOutput_AndShowsItEngaged()
    {
        Run();
        Receive(LimiterAllOff);

        Receive(LimiterOnB);

        var issue = Assert.Single(_amp.GetIssues(), issue => issue.Key == "limiter:B");
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("Output B's limiter engaged (MTX:mtr_512/80001/meter reads 01)", issue.Message);
        Assert.Equal(new DeviceDetail("Limiter", "Engaged: B", DetailTone.Warning), Detail("Limiter"));
    }

    [Fact]
    public void ALimiterStayingEngaged_IsCountedOnce_AndEachNewEngagementCountsAgain()
    {
        Run();
        Receive(LimiterOnB);
        Receive(LimiterOnBAndD);
        Receive(LimiterAllOff);

        Receive(LimiterOnB);

        Assert.Equal(2, Assert.Single(_amp.GetIssues(), issue => issue.Key == "limiter:B").OccurrenceCount);
        Assert.Equal(1, Assert.Single(_amp.GetIssues(), issue => issue.Key == "limiter:D").OccurrenceCount);
        Assert.Equal(new DeviceDetail("Limiter", "Engaged: B", DetailTone.Warning), Detail("Limiter"));
    }

    [Fact]
    public async Task ARejectedLimiterMeter_ShowsNotSupportedWithTheRawReply_RaisesNothing_AndIsNotRequestedAgain()
    {
        Run();

        Receive("ERROR mtrstart UnknownAddress");
        var sentBefore = _client.Sent.Count;
        await _amp.PollNow();

        Assert.Equal(new DeviceDetail("Limiter", "Not supported: ERROR mtrstart UnknownAddress"), Detail("Limiter"));
        Assert.DoesNotContain(LimiterRequest, SentAfter(sentBefore));
        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public void AnUnreadableLimiterMeter_RaisesTheUnreadableReplyIssueWithTheRawLine()
    {
        Run();
        const string garbled = "NOTIFY mtr MTX:mtr_512/80001/meter raw 00 zz";

        Receive(garbled);

        Assert.EndsWith(garbled, Assert.Single(_amp.GetIssues(), issue => issue.Key == YamahaXmv.UnreadableReplyIssueKey).Message);
    }

    [Fact]
    public async Task ALimiterMeterTheAmpNeverAnswers_RaisesTheMinorLimiterReadIssueOnTheThirdMiss_AndAReplyResolvesIt()
    {
        Run();
        Receive(LimiterStarted);
        await PollAnsweringTheRunMode();
        _time.Advance(YamahaXmv.ReplyTimeout);
        await PollAnsweringTheRunMode();
        _time.Advance(YamahaXmv.ReplyTimeout);
        await PollAnsweringTheRunMode();
        Assert.Null(Ongoing("limiter-read"));

        _time.Advance(YamahaXmv.ReplyTimeout);
        await PollAnsweringTheRunMode();
        var issue = Ongoing("limiter-read");
        Receive(LimiterAllOff);

        Assert.Equal(IssueSeverity.Minor, issue?.Severity);
        Assert.Null(Ongoing("limiter-read"));
    }

    [Fact]
    public void IdentityAndClockReplies_ShowAsDetails()
    {
        Run();

        Receive(Model);
        Receive(Firmware);
        Receive(Serial);
        Receive(DeviceName);
        Receive(UnitId);
        Receive(ProtocolVersion);
        Receive(ParameterSetVersion);
        Receive(SamplingRate);
        Receive(WordClockLocked);

        Assert.Equal(
        [
            new("Run Mode", "normal"), new("Alert", "none"), new("Model", "XMV4140"), new("Firmware", "1.0.0"),
            new("Serial", "ZA37640CHNET101001"), new("Device Name", "Amp Rack 1"), new("Unit ID", "001"), new("Protocol Version", "1.0.0"),
            new("Parameter Set Version", "MTX:1.2.0"), new("Sampling Rate", "44.1kHz"), new DeviceDetail("Word Clock", "lock")
        ], _amp.Details);
    }

    [Theory]
    [InlineData("NOTIFY devstatus lockstatus \"unlock\"", "Word Clock", "unlock")]
    [InlineData("NOTIFY devstatus fs \"unknown\"", "Sampling Rate", "unknown")]
    public void AnUnlockedWordClockOrUnknownSamplingRate_ShowsInWarningTone_WithNoIssue(string notification, string label, string value)
    {
        Run();

        Receive(notification);

        Assert.Equal(new DeviceDetail(label, value, DetailTone.Warning), Detail(label));
        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public async Task ARejectedIdentityRead_ShowsNotSupportedWithTheRawReply_RaisesNothing_AndIsNotAskedAgain()
    {
        Run();

        Receive("ERROR devinfo UnknownCommand");
        _time.Advance(YamahaXmv.ReplyTimeout);
        var sentBefore = _client.Sent.Count;
        await _amp.PollNow();

        Assert.Equal(new DeviceDetail("Model", "Not supported: ERROR devinfo UnknownCommand"), Detail("Model"));
        Assert.DoesNotContain("devinfo productname\n", SentAfter(sentBefore));
        Assert.DoesNotContain(_amp.GetIssues(), issue => issue.Key == "model-read");
    }

    [Fact]
    public async Task AnUnansweredIdentityRead_IsAskedAgainEachPoll_AndTheThirdMissRaisesAMinorIssue_ThatAReplyResolves()
    {
        Run();
        for (var miss = 1; miss < YamahaXmv.MaxMissedReads; miss++)
        {
            _time.Advance(YamahaXmv.ReplyTimeout);
            var sentBefore = _client.Sent.Count;
            await PollAnsweringTheRunMode();
            Assert.Contains("devinfo productname\n", SentAfter(sentBefore));
        }
        Assert.Null(Ongoing("model-read"));

        _time.Advance(YamahaXmv.ReplyTimeout);
        await PollAnsweringTheRunMode();
        var issue = Ongoing("model-read");
        Receive(Model);

        Assert.Equal(IssueSeverity.Minor, issue?.Severity);
        Assert.Null(Ongoing("model-read"));
    }

    [Fact]
    public async Task ABusyReplyToAnIdentityRead_CountsAsAMiss_AndItIsAskedAgain()
    {
        Run();

        Receive("ERROR devinfo Busy");
        var sentBefore = _client.Sent.Count;
        await _amp.PollNow();

        Assert.Contains("devinfo productname\n", SentAfter(sentBefore));
        Assert.Null(Detail("Model"));
    }

    [Fact]
    public void AnEmptyIdentityValue_ShowsTheRawReply_AndCountsAsAnswered()
    {
        Run();
        const string empty = "OK devinfo devicename \"\"";

        Receive(empty);

        Assert.Equal(new DeviceDetail("Device Name", empty), Detail("Device Name"));
    }

    [Fact]
    public void AnIdentityReplyWithNoValue_ShowsTheRawReplyInWarningTone()
    {
        Run();
        const string noValue = "OK devinfo version";

        Receive(noValue);

        Assert.Equal(new DeviceDetail("Firmware", noValue, DetailTone.Warning), Detail("Firmware"));
    }

    [Fact]
    public void SetLevel_SendsTheAttenuationInHundredthsOfADb()
    {
        _amp.AddControl(_levelHandler.Object, "A");
        var sentBefore = _client.Sent.Count;

        _amp.SetLevel("A", 50);

        Assert.Equal([$"set {LevelA} 0 0 -4950\n"], SentAfter(sentBefore));
    }

    [Theory]
    [InlineData($"NOTIFY set {LevelA} 0 0 -4950 \"-49.50\"")]
    [InlineData($"OK get {LevelA} 0 0 -4950")]
    [InlineData($"OK set {LevelA} 0 0 -4950 \"-49.50\"")]
    [InlineData("NOTIFY set MTX:mem_512/1/4/0/0/1/0 0 0 -4950 \"-49.50\"")]
    public void ALevelFromTheAmp_SetsTheLevelPercentage(string line)
    {
        _amp.AddControl(_levelHandler.Object, "A");

        Receive(line);

        Assert.Equal(50, _amp.GetLevel("A"));
        _levelHandler.Verify(handler => handler(50));
    }

    [Fact]
    public void SetLevel_OnAChannelWithNoLevelControl_SendsNothing()
    {
        var sentBefore = _client.Sent.Count;

        _amp.SetLevel("A", 50);

        Assert.Empty(SentAfter(sentBefore));
    }

    [Fact]
    public void SetAudioMute_SendsTheMuteParameter()
    {
        var sentBefore = _client.Sent.Count;

        _amp.SetAudioMute("B", MuteState.On);
        _amp.SetAudioMute("B", MuteState.Off);

        Assert.Equal([$"set {MuteB} 0 0 1\n", $"set {MuteB} 0 0 0\n"], SentAfter(sentBefore));
    }

    [Theory]
    [InlineData(1, MuteState.On)]
    [InlineData(0, MuteState.Off)]
    [InlineData(7, MuteState.Unknown)]
    public void AMuteFromTheAmp_SetsTheMuteState(int value, MuteState expected)
    {
        _amp.AddControl(_muteHandler.Object, "B");

        Receive($"NOTIFY set {MuteB} 0 0 {value} \"x\"");

        Assert.Equal(expected, _amp.GetAudioMute("B"));
    }

    [Fact]
    public void ToggleAudioMute_UnmutesAMutedChannel()
    {
        _amp.AddControl(_muteHandler.Object, "B");
        Receive($"OK get {MuteB} 0 0 1");
        var sentBefore = _client.Sent.Count;

        _amp.ToggleAudioMute("B");

        Assert.Equal([$"set {MuteB} 0 0 0\n"], SentAfter(sentBefore));
    }

    [Fact]
    public void PowerOnAndOff_SendTheStandbyParameter_AndSetTheDesiredPowerState()
    {
        var sentBefore = _client.Sent.Count;

        _amp.PowerOff();
        Assert.Equal(PowerState.Off, _amp.DesiredPowerState);
        _amp.PowerOn();

        Assert.Equal(PowerState.On, _amp.DesiredPowerState);
        Assert.Equal([$"set {Power} 0 0 1\n", $"set {Power} 0 0 0\n"], SentAfter(sentBefore));
    }

    [Theory]
    [InlineData(0, PowerState.On)]
    [InlineData(1, PowerState.Off)]
    [InlineData(2, PowerState.Unknown)]
    public void APowerValueFromTheAmp_SetsThePowerState(int value, PowerState expected)
    {
        _amp.PowerOn();

        Receive($"NOTIFY set {Power} 0 0 {value} \"x\"");

        Assert.Equal(expected, _amp.PowerState);
    }

    [Fact]
    public void SetValue_SendsTheValueOfTheLabel()
    {
        var sentBefore = _client.Sent.Count;

        _amp.SetValue("Input Select", "digital");
        _amp.SetValue("C Polarity", "Inverse");

        Assert.Equal([$"set {InputSelect} 0 0 1\n", $"set {PolarityC} 0 0 1\n"], SentAfter(sentBefore));
    }

    [Fact]
    public void SetValue_WithALabelTheParameterDoesNotTake_SendsNothing()
    {
        var sentBefore = _client.Sent.Count;

        _amp.SetValue("C Polarity", "Digital");

        Assert.Empty(SentAfter(sentBefore));
    }

    [Theory]
    [InlineData(2, "Multiple")]
    [InlineData(9, "9")]
    public void AStringValueFromTheAmp_ShowsItsLabel_OrTheRawValueWithoutOne(int value, string expected)
    {
        _amp.AddControl(_stringHandler.Object, "Input Select");

        Receive($"NOTIFY set {InputSelect} 0 0 {value} \"x\"");

        Assert.Equal(expected, _amp.GetValue("Input Select"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Ch 1")]
    [InlineData("")]
    public void AddControl_WithANameThatIsNotAChannel_RaisesABadControlIssue_ShowsItMissing_AndSendsNothing(string name)
    {
        Receive(RunModeNormal);
        var sentBefore = _client.Sent.Count;

        _amp.AddControl(_levelHandler.Object, name);

        Assert.NotNull(Ongoing($"bad-control:{name}"));
        Assert.Equal(DetailTone.Warning, Detail(MissingControlsLabel)?.Tone);
        Assert.Empty(SentAfter(sentBefore));
    }

    [Fact]
    public void ARejectedRead_ShowsTheChannelAsABadControl_UntilItReportsAValue()
    {
        _amp.AddControl(_levelHandler.Object, "E");
        Receive(RunModeNormal);
        Receive($"OK get {Power} 0 0 0");

        Receive("ERROR get UnknownAddress");
        Assert.Contains($"get {LevelE} 0 0", Ongoing("bad-control:E")?.Message);
        Assert.Equal(new DeviceDetail(MissingControlsLabel, "E", DetailTone.Warning), Detail(MissingControlsLabel));
        Receive($"NOTIFY set {LevelE} 0 0 0 \"0.00\"");

        Assert.Null(Ongoing("bad-control:E"));
        Assert.Null(Detail(MissingControlsLabel));
    }

    [Fact]
    public void AnInternalErrorToAChannelRead_ShowsTheChannelAsABadControl()
    {
        _amp.AddControl(_levelHandler.Object, "E");
        Receive(RunModeNormal);
        Receive($"OK get {Power} 0 0 0");

        Receive("ERROR get InternalError");

        Assert.NotNull(Ongoing("bad-control:E"));
        Assert.DoesNotContain(_amp.GetIssues(), issue => issue.Key == YamahaXmv.ErrorReplyIssueKey);
    }

    [Fact]
    public async Task AnInternalErrorToTheLimiterMeter_ShowsNotSupported_RaisesNothing_AndIsNotRequestedAgain()
    {
        Run();

        Receive("ERROR mtrstart InternalError");
        var sentBefore = _client.Sent.Count;
        await _amp.PollNow();

        Assert.Equal(new DeviceDetail("Limiter", "Not supported: ERROR mtrstart InternalError"), Detail("Limiter"));
        Assert.DoesNotContain(LimiterRequest, SentAfter(sentBefore));
        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public async Task ABusyReplyToAChannelRead_IsAskedAgainAtTheNextPoll()
    {
        _amp.AddControl(_levelHandler.Object, "A");
        Run();
        Receive($"OK get {Power} 0 0 0");

        Receive("ERROR get Busy");
        var sentBefore = _client.Sent.Count;
        await _amp.PollNow();

        Assert.Contains($"get {LevelA} 0 0\n", SentAfter(sentBefore));
    }

    [Fact]
    public async Task AnUnansweredChannelRead_IsAskedAgainAtTheNextPoll()
    {
        _amp.AddControl(_levelHandler.Object, "A");
        Run();

        _time.Advance(YamahaXmv.ReplyTimeout);
        var sentBefore = _client.Sent.Count;
        await _amp.PollNow();

        Assert.Contains($"get {LevelA} 0 0\n", SentAfter(sentBefore));
    }

    [Fact]
    public void ALevelTheAmpShowsDifferentlyFromHundredthsOfADb_RaisesTheLevelScaleIssue_UntilOneAgrees()
    {
        _amp.AddControl(_levelHandler.Object, "A");

        Receive($"NOTIFY set {LevelA} 0 0 -495 \"-49.50\"");
        var issue = Ongoing($"{YamahaXmv.LevelScaleIssuePrefix}A");
        Receive($"NOTIFY set {LevelA} 0 0 -4950 \"-49.50\"");

        Assert.Equal($"Channel A's ATT value -495 reads as -4.95 dB, but the amp shows \"-49.50\": NOTIFY set {LevelA} 0 0 -495 \"-49.50\"",
            issue?.Message);
        Assert.Null(Ongoing($"{YamahaXmv.LevelScaleIssuePrefix}A"));
    }

    [Fact]
    public void TwoChannelsShowingLevelsDifferently_AreTwoIssues_AndOneAgreeingResolvesOnlyItsOwn()
    {
        _amp.AddControl(_levelHandler.Object, "A");
        _amp.AddControl(_levelHandler.Object, "B");
        Receive($"NOTIFY set {LevelA} 0 0 -495 \"-49.50\"");
        Receive("NOTIFY set MTX:mem_512/1/4/1/0/0/0 0 0 -300 \"-30.00\"");

        Receive($"NOTIFY set {LevelA} 0 0 -4950 \"-49.50\"");

        Assert.Null(Ongoing($"{YamahaXmv.LevelScaleIssuePrefix}A"));
        Assert.NotNull(Ongoing($"{YamahaXmv.LevelScaleIssuePrefix}B"));
        Assert.Equal(new DeviceDetail("Level Display", "A: \"-49.50\" for value -4950"), Detail("Level Display"));
    }

    [Fact]
    public void ALevelDisplayThatIsNotANumber_ShowsInTheLevelDisplayDetail_InWarningTone()
    {
        _amp.AddControl(_levelHandler.Object, "A");

        Receive($"NOTIFY set {LevelA} 0 0 -2000 \"OFF\"");

        Assert.Equal(new DeviceDetail("Level Display", "A: \"OFF\" for value -2000 isn't a number, so it isn't checked", DetailTone.Warning),
            Detail("Level Display"));
        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public void TheSpecsMinusInfinityDisplay_AgreesWithItsRawValue()
    {
        _amp.AddControl(_levelHandler.Object, "A");

        Receive($"NOTIFY set {LevelA} 0 0 -13801 \"-INFINITY\"");

        Assert.Equal(new DeviceDetail("Level Display", "A: \"-INFINITY\" for value -13801"), Detail("Level Display"));
        Assert.Empty(_amp.GetOngoingIssues());
    }

    [Fact]
    public void APowerValueTheAmpShowsAsTheOtherState_RaisesThePowerValueIssue_AndShowsTheDisplay()
    {
        Receive($"NOTIFY set {Power} 0 0 0 \"Standby\"");

        Assert.Equal(IssueSeverity.Major, Ongoing(YamahaXmv.PowerValueIssueKey)?.Severity);
        Assert.Equal(new DeviceDetail("Power", "Standby (value 0)", DetailTone.Warning), Detail("Power"));
    }

    [Fact]
    public void APowerValueTheAmpShowsTheSameWay_RaisesNothing_AndShowsTheDisplay()
    {
        Receive($"NOTIFY set {Power} 0 0 1 \"Standby\"");

        Assert.Empty(_amp.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("Power", "Standby (value 1)"), Detail("Power"));
        Assert.Equal(PowerState.Off, _amp.PowerState);
    }

    [Fact]
    public void RepliesEndingInCrLf_AreHandledLikeLf()
    {
        _amp.AddControl(_muteHandler.Object, "B");

        _client.Reply($"OK devstatus runmode \"normal\"\r\nNOTIFY set {MuteB} 0 0 1 \"ON\"\r\n");

        Assert.Equal(new DeviceDetail("Run Mode", "normal"), Detail("Run Mode"));
        Assert.Equal(MuteState.On, _amp.GetAudioMute("B"));
    }

    [Fact]
    public void AnEightChannelLimiterLine_ShowsEachEngagedOutput()
    {
        Run();

        Receive("NOTIFY mtr MTX:mtr_512/80001/meter raw 01 00 00 00 00 00 00 01");

        Assert.Equal(new DeviceDetail("Limiter", "Engaged: A, H", DetailTone.Warning), Detail("Limiter"));
        Assert.Single(_amp.GetIssues(), issue => issue.Key == "limiter:H");
    }

    [Fact]
    public void ARejectedSet_RaisesTheMomentaryRejectedCommandIssue_NamingTheCommandAndTheReply()
    {
        _amp.SetAudioMute("B", MuteState.On);

        Receive("ERROR set InvalidArgument");

        Assert.Equal($"The amp rejected set {MuteB} 0 0 1 with ERROR set InvalidArgument",
            Assert.Single(_amp.GetIssues(), issue => issue.Key == YamahaXmv.RejectedCommandIssueKey).Message);
    }

    [Fact]
    public void AnErrorForNoCommandAwaitingAReply_RaisesTheMomentaryErrorReplyIssue()
    {
        Receive("ERROR set InvalidArgument");

        Assert.Equal("The amp sent ERROR set InvalidArgument when no set command was awaiting a reply",
            Assert.Single(_amp.GetIssues(), issue => issue.Key == YamahaXmv.ErrorReplyIssueKey).Message);
    }

    [Fact]
    public void AnUnreadableLine_RaisesTheUnreadableReplyIssueWithTheRawLine_AndTheNextLineIsStillHandled()
    {
        _amp.AddControl(_muteHandler.Object, "B");

        _client.Reply($"\u0002garbage\u007F\nNOTIFY set {MuteB} 0 0 1 \"ON\"\n");

        Assert.Equal("Unreadable line from the amp: \u0002garbage\u007F",
            Assert.Single(_amp.GetIssues(), issue => issue.Key == YamahaXmv.UnreadableReplyIssueKey).Message);
        Assert.Equal(MuteState.On, _amp.GetAudioMute("B"));
    }

    [Fact]
    public void ALineSplitAcrossTwoReads_IsHandledOnceWhole()
    {
        _amp.AddControl(_muteHandler.Object, "B");

        _client.Reply($"NOTIFY set {MuteB} 0");
        _client.Reply(" 0 1 \"ON\"\n");

        Assert.Equal(MuteState.On, _amp.GetAudioMute("B"));
        _muteHandler.Verify(handler => handler(MuteState.On), Times.Once);
        Assert.DoesNotContain(_amp.GetIssues(), issue => issue.Key == YamahaXmv.UnreadableReplyIssueKey);
    }

    [Fact]
    public void Disconnecting_ShowsCommunicationUnknown_AndOnRequestNotAttempted()
    {
        Receive(RunModeNormal);

        _client.SetConnectionState(ConnectionState.Disconnected);
        var dropped = _amp.CommunicationState;
        _client.SetConnectionState(ConnectionState.Connected);
        _client.Disconnect();

        Assert.Equal(CommunicationState.Unknown, dropped);
        Assert.Equal(CommunicationState.NotAttempted, _amp.CommunicationState);
    }

    [Fact]
    public void APersonsSet_WhileDisconnectedOnRequest_ReachesTheClientsSend_AndReconnectsIt()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var amp = new TestableYamahaXmv(client, _time);
        AsAPerson.Run(client.Disconnect);

        AsAPerson.Run(() => amp.SetAudioMute("B", MuteState.On));

        Assert.Equal(1, client.Connects);
        Assert.Equal($"set {MuteB} 0 0 1\n", client.Sent[^1]);
    }

    [Fact]
    public void ASetNoPersonMade_WhileDisconnectedOnRequest_DoesNotReconnectTheClient()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var amp = new TestableYamahaXmv(client, _time);
        AsAPerson.Run(client.Disconnect);

        amp.SetAudioMute("B", MuteState.On);

        Assert.Equal(0, client.Connects);
        Assert.Equal([RunModeQuery], client.Sent);
    }

    [Fact]
    public async Task APoll_WhileDisconnectedOnRequest_SendsNothing_SoItCannotReconnectTheClient()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var amp = new TestableYamahaXmv(client, _time);
        client.Reply(RunModeNormal + "\n");
        AsAPerson.Run(client.Disconnect);
        var sentBefore = client.Sent.Count;

        await amp.PollNow();

        Assert.Equal(0, client.Connects);
        Assert.Equal(sentBefore, client.Sent.Count);
    }

    [Fact]
    public void TheQsysVolumeControl_DrivesAnXmvChannel()
    {
        var channel = new QsysVolumeControl(new QscAudioBlockInfo("Zone 1", "A", "A"), VolumeType.Speaker, _amp);
        var sentBefore = _client.Sent.Count;

        Receive($"NOTIFY set {LevelA} 0 0 -4950 \"-49.50\"");
        channel.SetAudioMute(MuteState.On);

        Assert.Equal(50, channel.Volume);
        Assert.Equal(["set MTX:mem_512/1/4/0/0/2/0 0 0 1\n"], SentAfter(sentBefore));
    }

    [Collection(LoggerFactoryCollection.Name)]
    public class LogTest
    {
        private readonly FakeTimeProvider _time = new();

        private (TestTcpClient Client, TestableYamahaXmv Amp) Create(ScopeCapturingLoggerFactory logs) =>
            Logged.Create(logs, () =>
            {
                var client = new TestTcpClient();
                client.SetConnectionState(ConnectionState.Connected);
                return (client, new TestableYamahaXmv(client, _time));
            });

        [Fact]
        public void ARejectedSet_IsReportedUnderTheTriggerIdOfTheCallThatSentIt()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, amp) = Create(logs);
            var press = Trigger.Start();
            amp.SetAudioMute("B", MuteState.On);

            Trigger.Start();
            client.Reply("ERROR set InvalidArgument\n");

            var rejection = Assert.Single(logs.Logs, log => log.Message.StartsWith("The amp rejected set"));
            Assert.Equal(press, rejection.TriggerId);
        }

        [Fact]
        public void AHandlerThatThrows_IsLoggedAsAnErrorWithTheRawLine()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, amp) = Create(logs);
            amp.AddControl((MuteStateHandler)(_ => throw new InvalidOperationException("subscriber failed")), "B");
            const string notification = $"NOTIFY set {MuteB} 0 0 1 \"ON\"";

            client.Reply(notification + "\n");

            Assert.Contains(logs.Logs, log => log.Level == LogLevel.Error && log.Message.Contains(notification));
        }
    }

    internal class TestableYamahaXmv(TcpClient client, TimeProvider time) : YamahaXmv(client, "Test", FaderCurve.Linear, NeverPollMs, time)
    {
        public Task PollNow() => Poll(CancellationToken.None);
    }
}
